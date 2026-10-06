using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

using Serilog;

namespace STool.Modules.LanTransfer;

internal sealed class PreparedArchiveManager : IAsyncDisposable
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int ProgressReportBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CompletedArchiveRetention = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly string _sessionRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private readonly Dictionary<string, ArchiveJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceJobs = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PreparedArchiveManager(string receiveRoot)
    {
        var tempRoot = Path.Combine(Path.GetFullPath(receiveRoot), ".stool-transfer");
        _sessionRoot = Path.Combine(tempRoot, "archives-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionRoot);
        try { File.SetAttributes(tempRoot, File.GetAttributes(tempRoot) | FileAttributes.Hidden); }
        catch (Exception ex) { Log.Debug(ex, "Failed to hide archive temporary directory {Path}", tempRoot); }
    }

    public event Action<ArchiveProgressInfo>? ProgressChanged;

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _jobs.Values.Count(job => job.Snapshot().Status == ArchiveStatusNames.Preparing);
        }
    }

    public PreparedArchiveStatus Begin(
        string sourceKey,
        string archiveName,
        IReadOnlyList<PreparedArchiveItem> items,
        string? jobId = null,
        AsyncPauseGate? pauseGate = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sourceJobs.TryGetValue(sourceKey, out var existingId) &&
                _jobs.TryGetValue(existingId, out var existing))
            {
                var existingStatus = existing.Snapshot();
                if (existingStatus.Status == ArchiveStatusNames.Preparing ||
                    (existingStatus.Status == ArchiveStatusNames.Ready && File.Exists(existing.FullPath)))
                {
                    return existingStatus;
                }

                _jobs.Remove(existingId);
                _sourceJobs.Remove(sourceKey);
                TryDeleteFile(existing.FullPath);
                existing.Dispose();
            }

            var id = jobId ?? Guid.NewGuid().ToString("N");
            if (_jobs.Remove(id, out var replaced))
            {
                _sourceJobs.Remove(replaced.SourceKey);
                replaced.Cancel();
                TryDeleteFile(replaced.FullPath);
                replaced.Dispose();
            }
            var fullPath = Path.Combine(_sessionRoot, id + ".zip");
            var totalBytes = items.Aggregate(0L, (total, item) => AddSaturating(total, item.Size));
            var job = new ArchiveJob(
                id,
                sourceKey,
                EnsureZipExtension(archiveName),
                fullPath,
                totalBytes,
                items.Count(item => item.FullPath != null),
                pauseGate ?? new AsyncPauseGate(),
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken));
            _jobs[id] = job;
            _sourceJobs[sourceKey] = id;
            job.PreparationTask = PrepareAsync(job, items.ToArray(), job.Cancellation.Token);
            return job.Snapshot();
        }
    }

    public async Task CancelAsync(string id)
    {
        ArchiveJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job))
                return;
            _jobs.Remove(id);
            if (_sourceJobs.TryGetValue(job.SourceKey, out var currentId) && currentId == id)
                _sourceJobs.Remove(job.SourceKey);
            job.Cancel();
        }

        if (job.PreparationTask != null)
        {
            try { await job.PreparationTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug(ex, "Archive preparation ended with an error during cancellation"); }
        }
        TryDeleteFile(job.FullPath);
        job.Dispose();
    }

    public bool TryGetStatus(string id, out PreparedArchiveStatus? status)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var job))
            {
                status = job.Snapshot();
                return true;
            }
        }

        status = null;
        return false;
    }

    public bool TryGetDownload(string id, out PreparedArchiveDownload? download)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var job))
            {
                var status = job.Snapshot();
                if (status.Status == ArchiveStatusNames.Ready && File.Exists(job.FullPath))
                {
                    download = new PreparedArchiveDownload(job.Id, job.Name, job.FullPath);
                    return true;
                }
            }
        }

        download = null;
        return false;
    }

    public void MarkDownloaded(string id)
    {
        ArchiveJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job) || !job.TryScheduleCleanup())
                return;
        }

        _ = CleanupAfterDelayAsync(job, _lifetime.Token);
    }

    private async Task PrepareAsync(
        ArchiveJob job,
        IReadOnlyList<PreparedArchiveItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            await _preparationGate.WaitAsync(cancellationToken);
            try
            {
                EnsureDiskSpace(job.TotalBytes);
                await using var output = new FileStream(
                    job.FullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = item.EntryName.Replace('\\', '/');
                    if (item.FullPath == null)
                    {
                        archive.CreateEntry(entryName.TrimEnd('/') + "/");
                        continue;
                    }
                    if (!File.Exists(item.FullPath))
                        throw new FileNotFoundException($"文件已被移动或删除：{Path.GetFileName(item.FullPath)}");

                    var zipEntry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var input = new FileStream(
                        item.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var entryStream = zipEntry.Open();
                    await CopyFileAsync(input, entryStream, job, cancellationToken);
                }
            }
            finally
            {
                _preparationGate.Release();
            }

            var archiveSize = new FileInfo(job.FullPath).Length;
            job.SetReady(archiveSize);
            ProgressChanged?.Invoke(new ArchiveProgressInfo(
                job.Id,
                job.Name,
                job.TotalBytes,
                job.TotalBytes,
                ArchiveProgressStage.Ready));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(job.FullPath);
            job.SetFailed("准备已取消");
            ProgressChanged?.Invoke(new ArchiveProgressInfo(
                job.Id, job.Name, job.ProcessedBytes, job.TotalBytes, ArchiveProgressStage.Canceled));
        }
        catch (Exception ex)
        {
            TryDeleteFile(job.FullPath);
            job.SetFailed(ex is IOException
                ? "临时 ZIP 创建失败，请检查接收目录的可用空间。"
                : ex.Message);
            ProgressChanged?.Invoke(new ArchiveProgressInfo(
                job.Id, job.Name, job.ProcessedBytes, job.TotalBytes, ArchiveProgressStage.Failed));
        }
    }

    private async Task CopyFileAsync(
        Stream input,
        Stream output,
        ArchiveJob job,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var pendingBytes = 0L;
        var lastReport = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                await job.PauseGate.WaitAsync(cancellationToken);
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                job.AddProcessed(read);
                pendingBytes += read;
                if (pendingBytes >= ProgressReportBytes ||
                    Stopwatch.GetElapsedTime(lastReport) >= ProgressReportInterval)
                {
                    ReportPreparationProgress(job);
                    pendingBytes = 0;
                    lastReport = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            if (pendingBytes > 0)
                ReportPreparationProgress(job);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReportPreparationProgress(ArchiveJob job)
    {
        var status = job.Snapshot();
        ProgressChanged?.Invoke(new ArchiveProgressInfo(
            job.Id,
            job.Name,
            status.ProcessedBytes,
            status.TotalBytes,
            ArchiveProgressStage.Preparing));
    }

    private void EnsureDiskSpace(long sourceBytes)
    {
        var root = Path.GetPathRoot(_sessionRoot);
        if (string.IsNullOrWhiteSpace(root))
            return;

        var overhead = Math.Max(64L * 1024 * 1024, sourceBytes / 100);
        var required = AddSaturating(sourceBytes, overhead);
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < required)
            throw new IOException("临时 ZIP 所在磁盘空间不足。");
    }

    private async Task CleanupAfterDelayAsync(ArchiveJob job, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CompletedArchiveRetention, cancellationToken);
            RemoveAndDelete(job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RemoveAndDelete(ArchiveJob job)
    {
        lock (_gate)
        {
            _jobs.Remove(job.Id);
            if (_sourceJobs.TryGetValue(job.SourceKey, out var currentId) && currentId == job.Id)
                _sourceJobs.Remove(job.SourceKey);
        }
        TryDeleteFile(job.FullPath);
        job.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        ArchiveJob[] jobs;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            jobs = _jobs.Values.ToArray();
            _jobs.Clear();
            _sourceJobs.Clear();
        }

        _lifetime.Cancel();
        var preparationTasks = jobs.Select(job => job.PreparationTask).Where(task => task != null).Cast<Task>().ToArray();
        if (preparationTasks.Length > 0)
        {
            try { await Task.WhenAll(preparationTasks); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug(ex, "Archive preparation tasks ended with an error during disposal"); }
        }
        foreach (var job in jobs)
        {
            job.Cancel();
            TryDeleteFile(job.FullPath);
            job.Dispose();
        }
        TryDeleteDirectory(_sessionRoot);
        _preparationGate.Dispose();
        _lifetime.Dispose();
    }

    private static string EnsureZipExtension(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Debug(ex, "Failed to delete prepared archive file {Path}", path); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) { Log.Debug(ex, "Failed to delete prepared archive directory {Path}", path); }
    }

    private sealed class ArchiveJob(
        string id,
        string sourceKey,
        string name,
        string fullPath,
        long totalBytes,
        int fileCount,
        AsyncPauseGate pauseGate,
        CancellationTokenSource cancellation) : IDisposable
    {
        private readonly object _gate = new();
        private ArchiveProgressStage _stage = ArchiveProgressStage.Preparing;
        private long _processedBytes;
        private long _size;
        private string? _error;
        private bool _cleanupScheduled;

        public string Id { get; } = id;
        public string SourceKey { get; } = sourceKey;
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public long TotalBytes { get; } = totalBytes;
        public int FileCount { get; } = fileCount;
        public AsyncPauseGate PauseGate { get; } = pauseGate;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? PreparationTask { get; set; }

        public long ProcessedBytes
        {
            get { lock (_gate) return _processedBytes; }
        }

        public void AddProcessed(int bytes)
        {
            lock (_gate)
                _processedBytes = AddSaturating(_processedBytes, bytes);
        }

        public void SetReady(long size)
        {
            lock (_gate)
            {
                _processedBytes = TotalBytes;
                _size = size;
                _stage = ArchiveProgressStage.Ready;
            }
        }

        public void SetFailed(string error)
        {
            lock (_gate)
            {
                _stage = ArchiveProgressStage.Failed;
                _error = error;
            }
        }

        public bool TryScheduleCleanup()
        {
            lock (_gate)
            {
                if (_cleanupScheduled)
                    return false;
                _cleanupScheduled = true;
                return true;
            }
        }

        public void Cancel()
        {
            Cancellation.Cancel();
            PauseGate.Resume();
        }

        public void Dispose() => Cancellation.Dispose();

        public PreparedArchiveStatus Snapshot()
        {
            lock (_gate)
            {
                var status = _stage switch
                {
                    ArchiveProgressStage.Ready => ArchiveStatusNames.Ready,
                    ArchiveProgressStage.Preparing => ArchiveStatusNames.Preparing,
                    _ => ArchiveStatusNames.Failed
                };
                return new PreparedArchiveStatus(
                    Id,
                    Name,
                    status,
                    _processedBytes,
                    TotalBytes,
                    _size,
                    FileCount,
                    _error,
                    _stage == ArchiveProgressStage.Ready ? $"/api/archives/{Id}/download" : null);
            }
        }
    }
}
