using System.Buffers;
using System.Collections.Concurrent;
using System.IO;

using Serilog;

namespace STool.Modules.LanTransfer;

internal sealed class UploadCoordinator : IAsyncDisposable
{
    private const int FileTransferBufferSize = 512 * 1024;
    public const int ChunkSize = 4 * 1024 * 1024;

    private readonly string _receiveRoot;
    private readonly string _sessionTempRoot;
    private readonly TransferSessionManager? _sessions;
    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UploadBatch> _batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedFinalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pathGate = new();

    public UploadCoordinator(string receiveRoot, TransferSessionManager? sessions = null)
    {
        _receiveRoot = Path.GetFullPath(receiveRoot);
        _sessions = sessions;
        CleanupAbandonedSessions(_receiveRoot);
        _sessionTempRoot = Path.Combine(_receiveRoot, ".stool-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionTempRoot);
        TryHideDirectory(Path.GetDirectoryName(_sessionTempRoot)!);
    }

    public event Action<ReceivedFileInfo>? FileReceived;
    public event Action<ReceivedBatchInfo>? BatchReceived;
    public event Action<TransferProgressInfo>? ProgressChanged;

    public int ActiveCount => _uploads.Values.Count(upload => !upload.IsComplete);

    public void BeginBatch(string id, UploadBatchCreateRequest request)
    {
        if (request.FileCount <= 0 || request.TotalBytes < 0)
            throw new InvalidDataException("上传批次信息无效。 ");
        var name = SanitizeDisplayName(request.Name, request.IsFolder ? "文件夹" : $"{request.FileCount} 个文件");
        var tempRoot = Path.Combine(_sessionTempRoot, "batch-" + id);
        Directory.CreateDirectory(tempRoot);
        var batch = new UploadBatch(id, name, request.FileCount, request.TotalBytes, request.IsFolder, tempRoot);
        if (!_batches.TryAdd(id, batch))
            throw new InvalidDataException("上传批次已经存在。 ");
    }

    public async Task<UploadStatusResponse> BeginAsync(UploadInitRequest request, CancellationToken cancellationToken)
    {
        if (request.Size < 0)
            throw new InvalidDataException("文件大小无效。 ");

        if (!string.IsNullOrWhiteSpace(request.BatchId))
            return await BeginBatchFileAsync(request, cancellationToken);

        var relativePath = TransferPathGuard.SanitizeRelativePath(request.Name, request.RelativePath);
        var requestedFinalPath = TransferPathGuard.ResolveUnderRoot(_receiveRoot, relativePath);
        string finalPath;
        lock (_pathGate)
        {
            finalPath = TransferPathGuard.CreateUniquePath(requestedFinalPath, _reservedFinalPaths.Contains);
            _reservedFinalPaths.Add(finalPath);
        }

        var id = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_sessionTempRoot, id + ".stool-uploading");
        await CreateEmptyFileAsync(tempPath, cancellationToken);
        var upload = new PendingUpload(id, request.Name, request.Size, tempPath, finalPath, relativePath, null);
        _uploads[id] = upload;
        if (request.Size == 0)
            await CompleteLegacyAsync(upload, cancellationToken);

        return new UploadStatusResponse(id, upload.Offset, request.Size, request.Size == 0, Path.GetFileName(finalPath));
    }

    private async Task<UploadStatusResponse> BeginBatchFileAsync(
        UploadInitRequest request,
        CancellationToken cancellationToken)
    {
        if (!_batches.TryGetValue(request.BatchId!, out var batch))
            throw new FileNotFoundException("上传批次不存在。 ");

        var relativePath = TransferPathGuard.SanitizeRelativePath(request.Name, request.RelativePath);
        var tempPath = TransferPathGuard.ResolveUnderRoot(batch.TempRoot, relativePath);
        lock (batch.Gate)
        {
            if (batch.RelativePaths.Contains(relativePath))
                throw new InvalidDataException("上传批次包含重复文件。 ");
            if (batch.RelativePaths.Count >= batch.FileCount)
                throw new InvalidDataException("上传文件数量超出批次声明。 ");
            batch.RelativePaths.Add(relativePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await CreateEmptyFileAsync(tempPath, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var upload = new PendingUpload(id, request.Name, request.Size, tempPath, null, relativePath, batch.Id);
        _uploads[id] = upload;
        lock (batch.Gate)
            batch.UploadIds.Add(id);

        if (request.Size == 0)
            await CompleteBatchFileAsync(upload, batch, cancellationToken);
        return new UploadStatusResponse(id, 0, request.Size, request.Size == 0, request.Name);
    }

    public bool TryGetStatus(string id, out UploadStatusResponse? status)
    {
        if (_uploads.TryGetValue(id, out var upload))
        {
            status = new UploadStatusResponse(
                id,
                upload.Offset,
                upload.Size,
                upload.IsComplete,
                upload.FinalPath == null ? upload.Name : Path.GetFileName(upload.FinalPath));
            return true;
        }
        status = null;
        return false;
    }

    public bool TryGetBatchId(string id, out string? batchId)
    {
        if (_uploads.TryGetValue(id, out var upload))
        {
            batchId = upload.BatchId;
            return true;
        }
        batchId = null;
        return false;
    }

    public async Task<UploadStatusResponse> AppendAsync(
        string id,
        long requestedOffset,
        Stream input,
        long contentLength,
        CancellationToken cancellationToken)
    {
        if (!_uploads.TryGetValue(id, out var upload))
            throw new FileNotFoundException("上传任务不存在。 ");
        if (contentLength < 0 || contentLength > ChunkSize)
            throw new InvalidDataException("上传分块大小无效。 ");

        using var linked = CreateLinkedCancellation(upload.BatchId, cancellationToken);
        var effectiveToken = linked?.Token ?? cancellationToken;
        if (upload.BatchId != null && _sessions != null)
            await _sessions.WaitIfPausedAsync(upload.BatchId, effectiveToken);

        await upload.Gate.WaitAsync(effectiveToken);
        try
        {
            if (upload.IsComplete)
                return new UploadStatusResponse(id, upload.Offset, upload.Size, true, upload.Name);
            if (requestedOffset != upload.Offset)
                throw new UploadOffsetMismatchException(upload.Offset);
            if (requestedOffset + contentLength > upload.Size)
                throw new InvalidDataException("上传内容超出声明的文件大小。 ");

            await using (var output = new FileStream(
                upload.TempPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                FileTransferBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                output.Seek(upload.Offset, SeekOrigin.Begin);
                await CopyExactlyAsync(input, output, contentLength, effectiveToken);
                await output.FlushAsync(effectiveToken);
            }

            upload.Offset += contentLength;
            var complete = upload.Offset == upload.Size;
            if (upload.BatchId != null)
            {
                var batch = GetBatch(upload.BatchId);
                lock (batch.Gate)
                    batch.TransferredBytes += contentLength;
                _sessions?.ReportProgress(
                    batch.Id,
                    batch.TransferredBytes,
                    batch.TotalBytes,
                    completeWhenReached: false);
                if (complete)
                    await CompleteBatchFileAsync(upload, batch, effectiveToken);
            }
            else
            {
                ProgressChanged?.Invoke(new TransferProgressInfo(id, upload.Name, upload.Offset, upload.Size, true));
                if (complete)
                    await CompleteLegacyAsync(upload, effectiveToken);
            }

            return new UploadStatusResponse(id, upload.Offset, upload.Size, complete, upload.Name);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public async Task CancelAsync(string id)
    {
        if (!_uploads.TryGetValue(id, out var upload))
            return;
        if (upload.BatchId != null)
        {
            await CancelBatchAsync(upload.BatchId);
            return;
        }
        if (!_uploads.TryRemove(id, out upload))
            return;
        await upload.Gate.WaitAsync();
        try
        {
            TryDeleteFile(upload.TempPath);
            if (upload.FinalPath != null)
            {
                lock (_pathGate)
                    _reservedFinalPaths.Remove(upload.FinalPath);
            }
        }
        finally
        {
            upload.Gate.Release();
            upload.Gate.Dispose();
        }
    }

    public async Task CancelBatchAsync(string id)
    {
        if (!_batches.TryRemove(id, out var batch))
            return;
        string[] uploadIds;
        lock (batch.Gate)
            uploadIds = batch.UploadIds.ToArray();
        foreach (var uploadId in uploadIds)
        {
            if (!_uploads.TryRemove(uploadId, out var upload))
                continue;
            await upload.Gate.WaitAsync();
            upload.Gate.Release();
            upload.Gate.Dispose();
        }
        TryDeleteDirectory(batch.TempRoot);
        batch.CommitGate.Dispose();
    }

    private async Task CompleteBatchFileAsync(
        PendingUpload upload,
        UploadBatch batch,
        CancellationToken cancellationToken)
    {
        upload.IsComplete = true;
        var shouldCommit = false;
        lock (batch.Gate)
        {
            batch.CompletedFiles++;
            shouldCommit = batch.CompletedFiles == batch.FileCount &&
                batch.TransferredBytes == batch.TotalBytes;
        }
        if (shouldCommit)
            await CommitBatchAsync(batch, cancellationToken);
    }

    private async Task CommitBatchAsync(UploadBatch batch, CancellationToken cancellationToken)
    {
        await batch.CommitGate.WaitAsync(cancellationToken);
        try
        {
            if (batch.Committed)
                return;
            cancellationToken.ThrowIfCancellationRequested();

            string destinationPath;
            if (batch.IsFolder)
            {
                var safeName = Path.GetFileName(TransferPathGuard.SanitizeRelativePath(batch.Name, batch.Name));
                var stagedRoot = Path.Combine(batch.TempRoot, safeName);
                if (!Directory.Exists(stagedRoot))
                    stagedRoot = batch.TempRoot;
                var target = TransferPathGuard.CreateUniquePath(Path.Combine(_receiveRoot, safeName));
                Directory.Move(stagedRoot, target);
                destinationPath = target;
            }
            else
            {
                var uploads = GetBatchUploads(batch);
                string? singlePath = null;
                foreach (var upload in uploads)
                {
                    var requested = TransferPathGuard.ResolveUnderRoot(_receiveRoot, upload.RelativePath);
                    var target = TransferPathGuard.CreateUniquePath(requested);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(upload.TempPath, target);
                    singlePath ??= target;
                }
                destinationPath = batch.FileCount == 1 && singlePath != null ? singlePath : _receiveRoot;
            }

            batch.Committed = true;
            RemoveBatchUploads(batch);
            _batches.TryRemove(batch.Id, out _);
            if (Directory.Exists(batch.TempRoot))
                TryDeleteDirectory(batch.TempRoot);
            _sessions?.Complete(batch.Id, destinationPath);
            BatchReceived?.Invoke(new ReceivedBatchInfo(
                batch.Id,
                batch.Name,
                destinationPath,
                batch.TotalBytes,
                batch.FileCount,
                batch.IsFolder));
        }
        finally
        {
            batch.CommitGate.Release();
        }
    }

    private IReadOnlyList<PendingUpload> GetBatchUploads(UploadBatch batch)
    {
        lock (batch.Gate)
            return batch.UploadIds.Select(id => _uploads[id]).ToArray();
    }

    private void RemoveBatchUploads(UploadBatch batch)
    {
        foreach (var upload in GetBatchUploads(batch))
            _uploads.TryRemove(upload.Id, out _);
    }

    private Task<ReceivedFileInfo> CompleteLegacyAsync(
        PendingUpload upload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var finalPath = upload.FinalPath ?? throw new InvalidOperationException("缺少目标文件路径。 ");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        lock (_pathGate)
        {
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                _reservedFinalPaths.Remove(finalPath);
                finalPath = TransferPathGuard.CreateUniquePath(finalPath, _reservedFinalPaths.Contains);
                _reservedFinalPaths.Add(finalPath);
            }
        }
        File.Move(upload.TempPath, finalPath);
        _uploads.TryRemove(upload.Id, out _);
        lock (_pathGate)
            _reservedFinalPaths.Remove(finalPath);
        var result = new ReceivedFileInfo(upload.Id, Path.GetFileName(finalPath), finalPath, upload.Size);
        FileReceived?.Invoke(result);
        return Task.FromResult(result);
    }

    private CancellationTokenSource? CreateLinkedCancellation(string? batchId, CancellationToken requestToken)
    {
        if (batchId == null || _sessions == null)
            return null;
        return CancellationTokenSource.CreateLinkedTokenSource(
            requestToken,
            _sessions.GetCancellationToken(batchId));
    }

    private UploadBatch GetBatch(string id) =>
        _batches.TryGetValue(id, out var batch)
            ? batch
            : throw new FileNotFoundException("上传批次不存在。 ");

    private static async Task CreateEmptyFileAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
        await stream.FlushAsync(cancellationToken);
    }

    private static string SanitizeDisplayName(string value, string fallback)
    {
        var name = Path.GetFileName(value?.Trim().TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(FileTransferBufferSize);
        var remaining = length;
        try
        {
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                var read = await input.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("上传分块提前结束。 ");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CleanupAbandonedSessions(string receiveRoot)
    {
        var tempRoot = Path.Combine(receiveRoot, ".stool-transfer");
        if (!Directory.Exists(tempRoot))
            return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(tempRoot))
                TryDeleteDirectory(directory);
        }
        catch
        {
        }
    }

    private static void TryHideDirectory(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) { Log.Debug(ex, "Failed to hide upload directory {Path}", path); }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Debug(ex, "Failed to delete upload temporary file {Path}", path); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) { Log.Debug(ex, "Failed to delete upload temporary directory {Path}", path); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _batches.Keys.ToArray())
            await CancelBatchAsync(id);
        foreach (var id in _uploads.Keys.ToArray())
            await CancelAsync(id);
        TryDeleteDirectory(_sessionTempRoot);
    }

    private sealed class PendingUpload(
        string id,
        string name,
        long size,
        string tempPath,
        string? finalPath,
        string relativePath,
        string? batchId)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string TempPath { get; } = tempPath;
        public string? FinalPath { get; } = finalPath;
        public string RelativePath { get; } = relativePath;
        public string? BatchId { get; } = batchId;
        public long Offset { get; set; }
        public bool IsComplete { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class UploadBatch(
        string id,
        string name,
        int fileCount,
        long totalBytes,
        bool isFolder,
        string tempRoot)
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string Name { get; } = name;
        public int FileCount { get; } = fileCount;
        public long TotalBytes { get; } = totalBytes;
        public bool IsFolder { get; } = isFolder;
        public string TempRoot { get; } = tempRoot;
        public HashSet<string> RelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadIds { get; } = [];
        public long TransferredBytes { get; set; }
        public int CompletedFiles { get; set; }
        public bool Committed { get; set; }
        public SemaphoreSlim CommitGate { get; } = new(1, 1);
    }
}

internal sealed class UploadOffsetMismatchException(long expectedOffset) : Exception
{
    public long ExpectedOffset { get; } = expectedOffset;
}
