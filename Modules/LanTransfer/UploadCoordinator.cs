using System.Buffers;
using System.Collections.Concurrent;
using System.IO;

using Serilog;

namespace STool.Modules.LanTransfer;

/// <summary>
/// 手机上传：每个上传属于一个批次，文件先分块写入隐藏的临时目录，整批完成后再移入接收目录。
/// </summary>
internal sealed class UploadCoordinator : IAsyncDisposable
{
    private const int FileTransferBufferSize = 512 * 1024;
    public const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>接收前预留的磁盘余量，避免把系统盘写满。</summary>
    private const long MinimumFreeSpaceReserve = 64L * 1024 * 1024;

    private readonly string _receiveRoot;
    private readonly string _sessionTempRoot;
    private readonly TransferSessionManager? _sessions;
    private readonly Func<string, long?> _getAvailableFreeSpace;
    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UploadBatch> _batches = new(StringComparer.OrdinalIgnoreCase);

    public UploadCoordinator(
        string receiveRoot,
        TransferSessionManager? sessions = null,
        Func<string, long?>? getAvailableFreeSpace = null)
    {
        _receiveRoot = Path.GetFullPath(receiveRoot);
        _sessions = sessions;
        _getAvailableFreeSpace = getAvailableFreeSpace ?? GetAvailableFreeSpace;
        CleanupAbandonedSessions(_receiveRoot);
        _sessionTempRoot = Path.Combine(_receiveRoot, ".stool-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionTempRoot);
        TryHideDirectory(Path.GetDirectoryName(_sessionTempRoot)!);
    }

    public event Action<ReceivedBatchInfo>? BatchReceived;

    public int ActiveCount => _uploads.Values.Count(upload => !upload.IsComplete);

    public void BeginBatch(string id, UploadBatchCreateRequest request)
    {
        if (request.FileCount <= 0 || request.TotalBytes < 0)
            throw new InvalidDataException("上传批次信息无效。 ");
        EnsureDiskSpace(request.TotalBytes);

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
        if (string.IsNullOrWhiteSpace(request.BatchId) || !_batches.TryGetValue(request.BatchId, out var batch))
            throw new InvalidDataException("请先创建上传批次。 ");

        var relativePath = TransferPathGuard.SanitizeRelativePath(request.Name, request.RelativePath);
        var tempPath = TransferPathGuard.ResolveUnderRoot(batch.TempRoot, relativePath);
        await batch.CommitGate.WaitAsync(cancellationToken);
        PendingUpload upload;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (batch.Gate)
            {
                if (batch.Canceled || batch.Committed)
                    throw new OperationCanceledException("上传批次已结束。 ");
                if (batch.RelativePaths.Contains(relativePath))
                    throw new InvalidDataException("上传批次包含重复文件。 ");
                if (batch.RelativePaths.Count >= batch.FileCount)
                    throw new InvalidDataException("上传文件数量超出批次声明。 ");
                if (request.Size > batch.TotalBytes - batch.DeclaredBytes ||
                    batch.RelativePaths.Count + 1 == batch.FileCount && request.Size != batch.TotalBytes - batch.DeclaredBytes)
                    throw new InvalidDataException("文件大小与上传批次声明不一致。 ");
            }

            // 只有实际创建成功后才占用名额，取消或创建失败不会留下无法重试的路径。
            await CreateEmptyFileAsync(tempPath, cancellationToken);
            var id = Guid.NewGuid().ToString("N");
            upload = new PendingUpload(id, request.Name, request.Size, tempPath, relativePath, batch.Id);
            _uploads[id] = upload;
            lock (batch.Gate)
            {
                batch.RelativePaths.Add(relativePath);
                batch.DeclaredBytes += request.Size;
                batch.UploadIds.Add(id);
            }
        }
        finally
        {
            batch.CommitGate.Release();
        }

        if (request.Size == 0)
            await CompleteBatchFileAsync(upload, batch, cancellationToken);
        return new UploadStatusResponse(upload.Id, 0, request.Size, request.Size == 0, request.Name);
    }

    public bool TryGetStatus(string id, out UploadStatusResponse? status)
    {
        if (_uploads.TryGetValue(id, out var upload))
        {
            status = new UploadStatusResponse(id, upload.Offset, upload.Size, upload.IsComplete, upload.Name);
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

        var batch = GetBatch(upload.BatchId);
        using var sessionCancellation = CreateLinkedCancellation(upload.BatchId, cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellation?.Token ?? cancellationToken, batch.Cancellation.Token);
        var effectiveToken = linked.Token;
        if (_sessions != null)
            await _sessions.WaitIfPausedAsync(upload.BatchId, effectiveToken);

        await upload.Gate.WaitAsync(effectiveToken);
        try
        {
            if (upload.IsComplete)
                return new UploadStatusResponse(id, upload.Offset, upload.Size, true, upload.Name);
            if (requestedOffset != upload.Offset)
                throw new UploadOffsetMismatchException(upload.Offset);
            if (contentLength > upload.Size - requestedOffset)
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
            long transferred;
            lock (batch.Gate)
            {
                batch.TransferredBytes += contentLength;
                transferred = batch.TransferredBytes;
            }
            _sessions?.ReportProgress(
                batch.Id,
                transferred,
                batch.TotalBytes,
                completeWhenReached: false);
            if (complete)
                await CompleteBatchFileAsync(upload, batch, effectiveToken);

            return new UploadStatusResponse(id, upload.Offset, upload.Size, complete, upload.Name);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public async Task CancelAsync(string id)
    {
        if (_uploads.TryGetValue(id, out var upload))
            await CancelBatchAsync(upload.BatchId);
    }

    public async Task CancelBatchAsync(string id)
    {
        if (!_batches.TryRemove(id, out var batch))
            return;
        lock (batch.Gate)
            batch.Canceled = true;
        batch.Cancellation.Cancel();

        // 等文件创建和提交退出，再取得完整的上传列表。不持有提交锁等待单文件锁。
        await batch.CommitGate.WaitAsync();
        string[] uploadIds;
        try
        {
            lock (batch.Gate)
                uploadIds = batch.UploadIds.ToArray();
        }
        finally
        {
            batch.CommitGate.Release();
        }
        foreach (var uploadId in uploadIds)
        {
            if (!_uploads.TryRemove(uploadId, out var upload))
                continue;
            await upload.Gate.WaitAsync();
            upload.Gate.Release();
        }
        // SemaphoreSlim 不使用 WaitHandle；不要在仍可能有等待者时 Dispose。
        TryDeleteDirectory(batch.TempRoot);
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
            batch.Cancellation.Token.ThrowIfCancellationRequested();
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

    private void EnsureDiskSpace(long incomingBytes)
    {
        var available = _getAvailableFreeSpace(_receiveRoot);
        if (available == null)
            return;

        var reserve = Math.Max(MinimumFreeSpaceReserve, incomingBytes / 100);
        var required = incomingBytes > long.MaxValue - reserve ? long.MaxValue : incomingBytes + reserve;
        if (available.Value < reserve || incomingBytes > available.Value - reserve)
        {
            throw new InvalidDataException(
                $"电脑磁盘空间不足：需要 {FormatBytes(required)}，接收目录所在磁盘剩余 {FormatBytes(available.Value)}。 ");
        }
    }

    private static long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 网络路径等无法取得剩余空间时不阻止接收，由写入失败时再报错。
            return null;
        }
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, value);
        var index = 0;
        while (display >= 1024 && index < units.Length - 1)
        {
            display /= 1024;
            index++;
        }
        return $"{display:0.#} {units[index]}";
    }

    private CancellationTokenSource? CreateLinkedCancellation(string batchId, CancellationToken requestToken)
    {
        if (_sessions == null)
            return null;
        return CancellationTokenSource.CreateLinkedTokenSource(
            requestToken,
            _sessions.GetCancellationToken(batchId));
    }

    private UploadBatch GetBatch(string id) =>
        _batches.TryGetValue(id, out var batch)
            ? batch
            : throw new FileNotFoundException("上传批次不存在。 ");

    private static Task CreateEmptyFileAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        cancellationToken.ThrowIfCancellationRequested();
        // 空文件不需要异步 Flush；避免创建后因取消而失败，留下阻止重试的空文件。
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return Task.CompletedTask;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Failed to clean abandoned upload sessions under {Path}", tempRoot);
        }
    }

    private static void TryHideDirectory(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) { Log.Debug(ex, "Failed to hide upload directory {Path}", path); }
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
        TryDeleteDirectory(_sessionTempRoot);
    }

    private sealed class PendingUpload(
        string id,
        string name,
        long size,
        string tempPath,
        string relativePath,
        string batchId)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string TempPath { get; } = tempPath;
        public string RelativePath { get; } = relativePath;
        public string BatchId { get; } = batchId;
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
        public long DeclaredBytes { get; set; }
        public int CompletedFiles { get; set; }
        public bool Committed { get; set; }
        public bool Canceled { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim CommitGate { get; } = new(1, 1);
    }
}

internal sealed class UploadOffsetMismatchException(long expectedOffset) : Exception
{
    public long ExpectedOffset { get; } = expectedOffset;
}
