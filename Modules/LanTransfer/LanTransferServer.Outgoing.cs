using System.IO;
using Serilog;

namespace STool.Modules.LanTransfer;

/// <summary>LanTransferServer 发往手机的任务：排队、打包、进度与清理。</summary>
internal sealed partial class LanTransferServer
{
    public TransferSessionSnapshot QueueOutgoing(
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode = OutgoingTransferMode.Automatic,
        bool offerRequired = true,
        string? ownerToken = null)
    {
        if (entries.Count == 0)
            throw new InvalidDataException("没有可发送的项目。 ");

        var available = entries.Where(entry => entry.IsAvailable).ToArray();
        if (available.Length == 0)
            throw new FileNotFoundException("发送项目已被移动或删除。 ");

        var resolvedMode = mode == OutgoingTransferMode.Automatic
            ? available.Length == 1 && !available[0].IsFolder
                ? OutgoingTransferMode.Automatic
                : OutgoingTransferMode.CombinedArchive
            : mode;
        var separate = resolvedMode == OutgoingTransferMode.Separate;
        var needsArchive = resolvedMode == OutgoingTransferMode.CombinedArchive ||
            separate && available.Any(entry => entry.IsFolder);
        var name = resolvedMode switch
        {
            OutgoingTransferMode.Separate => $"{available.Length} 个项目",
            OutgoingTransferMode.CombinedArchive when available.Length == 1 => available[0].Name + ".zip",
            OutgoingTransferMode.CombinedArchive => $"STool发送-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            _ => available[0].Name
        };
        var sourcePaths = available.Select(entry => entry.FullPath).ToArray();
        var total = available.Aggregate(0L, (sum, entry) => AddSaturating(sum, entry.Size));
        var fileCount = available.Sum(entry => entry.FileCount);
        var location = GetCommonLocation(sourcePaths);
        var session = _sessions.CreateOutgoing(
            name,
            total,
            fileCount,
            sourcePaths,
            location,
            needsArchive);
        var payloads = CreateOutgoingPayloads(session.Id, available, resolvedMode, name);
        var outgoing = new OutgoingTransfer(
            session.Id,
            available.Select(entry => entry.Id).ToArray(),
            available,
            resolvedMode,
            payloads,
            offerRequired);
        _outgoing[session.Id] = outgoing;
        if (!string.IsNullOrWhiteSpace(ownerToken))
            _sessions.AssignOwner(session.Id, ownerToken);

        if (needsArchive)
        {
            ReportPreparationProgress(outgoing);
            foreach (var payload in payloads.Where(payload => payload.NeedsArchive))
                StartArchivePreparation(outgoing, payload);
        }
        return session;
    }

    public IReadOnlyList<TransferSessionSnapshot> GetTransfers() => _sessions.Snapshot();

    public bool PauseTransfer(string id) => _sessions.Pause(id);

    public bool ResumeTransfer(string id) => _sessions.Resume(id);

    public async Task<bool> CancelTransferAsync(
        string id,
        bool rejected = false,
        bool removeSources = true)
    {
        if (!_sessions.Cancel(id, rejected))
            return false;
        await CleanupCanceledTransferAsync(id, removeSources);
        return true;
    }

    public bool RetryTransfer(string id)
    {
        if (!_outgoing.TryGetValue(id, out var outgoing) || !_sessions.ResetForRetry(id))
            return false;
        outgoing.ResetForRetry();
        RemoveDownloadProgress(outgoing);
        if (outgoing.NeedsArchive)
        {
            ReportPreparationProgress(outgoing);
            foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
                StartArchivePreparation(outgoing, payload);
        }
        else
            _sessions.SetAwaitingConfirmation(id, outgoing.DownloadSize);
        return true;
    }

    public async Task RemoveTransferAsync(string id)
    {
        await CleanupCanceledTransferAsync(id);
        _sessions.Remove(id);
    }

    private static IReadOnlyList<OutgoingPayload> CreateOutgoingPayloads(
        string transferId,
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode,
        string transferName)
    {
        if (mode == OutgoingTransferMode.Separate)
        {
            return entries.Select(entry => new OutgoingPayload(
                Guid.NewGuid().ToString("N"),
                transferId,
                entry.IsFolder ? entry.Name + ".zip" : entry.Name,
                [entry],
                entry.IsFolder,
                entry.IsFolder ? null : entry.FullPath,
                entry.IsFolder ? 0 : entry.Size)).ToArray();
        }

        var archive = mode == OutgoingTransferMode.CombinedArchive;
        return
        [
            new OutgoingPayload(
                Guid.NewGuid().ToString("N"),
                transferId,
                transferName,
                entries,
                archive,
                archive ? null : entries[0].FullPath,
                archive ? 0 : entries[0].Size)
        ];
    }

    private TransferSessionSnapshot EnrichOutgoingSnapshot(TransferSessionSnapshot snapshot)
    {
        if (!_outgoing.TryGetValue(snapshot.Id, out var outgoing))
            return snapshot;
        return snapshot with
        {
            DownloadUrl = outgoing.Payloads.Count == 1
                ? $"/api/transfers/{snapshot.Id}/download"
                : null,
            Downloads = outgoing.CreateDownloadDtos()
        };
    }

    private void StartArchivePreparation(OutgoingTransfer outgoing, OutgoingPayload payload)
    {
        var sourceKey = "transfer:" + outgoing.Id + ":" + payload.Id + ":" +
            string.Join('|', payload.Entries.Select(ArchiveEntryVersion));
        var items = CreateArchiveItems(payload.Entries).ToArray();
        _archivePayloads[payload.ArchiveJobId] = new ArchivePayloadReference(outgoing.Id, payload.Id);
        _archives.Begin(
            sourceKey,
            payload.Name,
            items,
            payload.ArchiveJobId,
            _sessions.GetPauseGate(outgoing.Id),
            _sessions.GetCancellationToken(outgoing.Id));
    }

    private void Archives_ProgressChanged(ArchiveProgressInfo info)
    {
        try
        {
            if (!_archivePayloads.TryGetValue(info.Id, out var reference) ||
                !_outgoing.TryGetValue(reference.TransferId, out var outgoing) ||
                !outgoing.TryGetPayload(reference.PayloadId, out var payload) || payload == null)
            {
                return;
            }

            switch (info.Stage)
            {
                case ArchiveProgressStage.Preparing:
                    payload.SetPreparationProgress(info.Processed, info.Total);
                    ReportPreparationProgress(outgoing);
                    break;

                case ArchiveProgressStage.Ready:
                    if (_archives.TryGetDownload(info.Id, out var archive) && archive != null)
                    {
                        payload.SetPrepared(archive.FullPath, new FileInfo(archive.FullPath).Length);
                        if (outgoing.IsReady)
                            _sessions.SetAwaitingConfirmation(outgoing.Id, outgoing.DownloadSize);
                        else
                            ReportPreparationProgress(outgoing);
                    }
                    break;

                case ArchiveProgressStage.Failed:
                    var message = _archives.TryGetStatus(info.Id, out var status) && status != null
                        ? status.Error ?? "下载内容准备失败。"
                        : "下载内容准备失败。";
                    _sessions.Fail(outgoing.Id, message);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply archive transfer state id={TransferId}", info.Id);
        }
    }

    private void ReportPreparationProgress(OutgoingTransfer outgoing)
    {
        var (processed, total) = outgoing.GetPreparationProgress();
        _sessions.SetPreparingProgress(outgoing.Id, processed, total);
    }

    private void Sessions_Changed(TransferSessionSnapshot snapshot)
    {
        TransferChanged?.Invoke(snapshot);
        if (snapshot.State == TransferState.Completed)
        {
            _downloadProgress.Remove(snapshot.Id);
            // 先只把源项从待发送列表移除;下载载荷保留到会话清理为止,
            // 以便客户端下载管理器在最后一个字节之后发起的并行/重试请求仍能读到同一内容。
            if (_outgoing.TryGetValue(snapshot.Id, out var outgoing))
            {
                foreach (var sourceId in outgoing.SourceIds)
                    _sharedFiles.Remove(sourceId);
            }
            ScheduleSessionRemoval(snapshot.Id, CompletedDownloadGrace);
        }
        else if (snapshot.State is TransferState.Canceled or TransferState.Rejected)
        {
            _downloadProgress.Remove(snapshot.Id);
            ScheduleSessionRemoval(snapshot.Id, TerminalSessionGrace);
        }
    }

    private async Task CleanupCanceledTransferAsync(string id, bool removeSources = true)
    {
        await _uploads.CancelBatchAsync(id);
        if (_outgoing.TryRemove(id, out var outgoing))
        {
            RemoveDownloadProgress(outgoing);
            foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
            {
                _archivePayloads.TryRemove(payload.ArchiveJobId, out _);
                await _archives.CancelAsync(payload.ArchiveJobId);
            }
            if (removeSources)
            {
                foreach (var sourceId in outgoing.SourceIds)
                    _sharedFiles.Remove(sourceId);
            }
        }
    }

    /// <summary>宽限期结束后释放已完成任务的下载载荷和临时归档。</summary>
    private void ReleaseCompletedOutgoing(string id)
    {
        if (!_outgoing.TryRemove(id, out var outgoing))
            return;

        RemoveDownloadProgress(outgoing);
        foreach (var sourceId in outgoing.SourceIds)
            _sharedFiles.Remove(sourceId);
        foreach (var payload in outgoing.Payloads.Where(payload => payload.NeedsArchive))
        {
            _archivePayloads.TryRemove(payload.ArchiveJobId, out _);
            _archives.MarkDownloaded(payload.ArchiveJobId);
        }
    }

    private void ScheduleSessionRemoval(string id, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _lifetime?.Token ?? CancellationToken.None);
                ReleaseCompletedOutgoing(id);
                _sessions.Remove(id);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static PreparedArchiveStatus ToArchiveStatus(TransferSessionSnapshot snapshot)
    {
        var status = snapshot.State switch
        {
            TransferState.Preparing or TransferState.Paused when snapshot.TransferredBytes < snapshot.TotalBytes => ArchiveStatusNames.Preparing,
            TransferState.Failed or TransferState.Canceled or TransferState.Rejected => ArchiveStatusNames.Failed,
            _ => ArchiveStatusNames.Ready
        };
        var ready = status == ArchiveStatusNames.Ready;
        return new PreparedArchiveStatus(
            snapshot.Id,
            snapshot.Name,
            status,
            snapshot.TransferredBytes,
            snapshot.TotalBytes,
            ready ? snapshot.TotalBytes : 0,
            snapshot.FileCount,
            snapshot.Error,
            ready ? $"/api/transfers/{snapshot.Id}/download" : null);
    }

    private static string GetPayloadTrackerId(string transferId, string payloadId) =>
        transferId + ":" + payloadId;

    private void RemoveDownloadProgress(OutgoingTransfer outgoing)
    {
        foreach (var payload in outgoing.Payloads)
            _downloadProgress.Remove(GetPayloadTrackerId(outgoing.Id, payload.Id));
    }

    private static string ArchiveEntryVersion(SharedFileEntry entry) =>
        $"{entry.Id}:{entry.ModifiedUtc.UtcTicks}:{entry.Size}:{entry.FileCount}";

    private static IEnumerable<PreparedArchiveItem> CreateArchiveItems(IReadOnlyList<SharedFileEntry> entries)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var rootName = CreateUniqueArchiveRoot(entry.Name, usedNames);
            if (!entry.IsFolder)
            {
                yield return new PreparedArchiveItem(entry.FullPath, rootName, entry.Size);
                continue;
            }

            if (entry.FolderFiles.Count == 0)
            {
                yield return new PreparedArchiveItem(null, rootName + "/", 0);
                continue;
            }

            foreach (var file in entry.FolderFiles)
            {
                yield return new PreparedArchiveItem(
                    file.FullPath,
                    Path.Combine(rootName, file.RelativePath),
                    file.Size);
            }
        }
    }

    private static string CreateUniqueArchiveRoot(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private sealed class OutgoingTransfer(
        string id,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<SharedFileEntry> entries,
        OutgoingTransferMode mode,
        IReadOnlyList<OutgoingPayload> payloads,
        bool offerRequired)
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> SourceIds { get; } = sourceIds;
        public IReadOnlyList<SharedFileEntry> Entries { get; } = entries;
        public OutgoingTransferMode Mode { get; } = mode;
        public IReadOnlyList<OutgoingPayload> Payloads { get; } = payloads;
        public bool NeedsArchive => Payloads.Any(payload => payload.NeedsArchive);
        public bool OfferRequired { get; } = offerRequired;

        public bool IsReady => Payloads.All(payload => payload.IsReady);
        public bool IsDownloadComplete => Payloads.All(payload => payload.Completed);
        public long DownloadSize => Payloads.Aggregate(0L, (sum, payload) => AddSaturating(sum, payload.DownloadSize));
        public long TransferredBytes => Payloads.Aggregate(0L, (sum, payload) => AddSaturating(sum, payload.CoveredBytes));

        public bool TryGetPayload(string payloadId, out OutgoingPayload? payload)
        {
            payload = Payloads.FirstOrDefault(item =>
                string.Equals(item.Id, payloadId, StringComparison.OrdinalIgnoreCase));
            return payload != null;
        }

        public (long Processed, long Total) GetPreparationProgress() =>
            Payloads.Aggregate(
                (Processed: 0L, Total: 0L),
                (progress, payload) => (
                    AddSaturating(progress.Processed, payload.PreparationProcessed),
                    AddSaturating(progress.Total, payload.PreparationTotal)));

        public IReadOnlyList<TransferDownloadDto> CreateDownloadDtos() =>
            Payloads.Select(payload => payload.CreateDownloadDto(Id, Payloads.Count == 1)).ToArray();

        public void ResetForRetry()
        {
            foreach (var payload in Payloads)
                payload.ResetForRetry();
        }
    }

    private sealed class OutgoingPayload
    {
        private readonly object _gate = new();
        private string? _downloadPath;
        private long _downloadSize;
        private long _coveredBytes;
        private long _preparationProcessed;
        private long _preparationTotal;
        private bool _ready;
        private bool _started;
        private bool _completed;

        public OutgoingPayload(
            string id,
            string transferId,
            string name,
            IReadOnlyList<SharedFileEntry> entries,
            bool needsArchive,
            string? downloadPath,
            long downloadSize)
        {
            Id = id;
            ArchiveJobId = transferId + "-" + id;
            Name = name;
            Entries = entries;
            NeedsArchive = needsArchive;
            _downloadPath = downloadPath;
            _downloadSize = Math.Max(0, downloadSize);
            _preparationTotal = entries.Aggregate(0L, (sum, entry) => AddSaturating(sum, entry.Size));
            _preparationProcessed = needsArchive ? 0 : _preparationTotal;
            _ready = !needsArchive;
        }

        public string Id { get; }
        public string ArchiveJobId { get; }
        public string Name { get; }
        public IReadOnlyList<SharedFileEntry> Entries { get; }
        public bool NeedsArchive { get; }

        public long DownloadSize { get { lock (_gate) return _downloadSize; } }
        public long CoveredBytes { get { lock (_gate) return _coveredBytes; } }
        public long PreparationProcessed { get { lock (_gate) return _preparationProcessed; } }
        public long PreparationTotal { get { lock (_gate) return _preparationTotal; } }
        public bool IsReady { get { lock (_gate) return _ready; } }
        public bool Completed { get { lock (_gate) return _completed; } }

        public void SetPreparationProgress(long processed, long total)
        {
            lock (_gate)
            {
                _preparationTotal = Math.Max(0, total);
                _preparationProcessed = Math.Clamp(processed, 0, _preparationTotal);
            }
        }

        public void SetPrepared(string fullPath, long size)
        {
            lock (_gate)
            {
                _downloadPath = fullPath;
                _downloadSize = Math.Max(0, size);
                _preparationProcessed = _preparationTotal;
                _ready = true;
            }
        }

        public bool TryGetDownload(out string fullPath)
        {
            lock (_gate)
            {
                fullPath = _downloadPath ?? string.Empty;
                return _ready && !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);
            }
        }

        public bool SetDownloadProgress(long coveredBytes, long totalLength)
        {
            lock (_gate)
            {
                _started = true;
                _downloadSize = Math.Max(0, totalLength);
                _coveredBytes = Math.Clamp(coveredBytes, 0, _downloadSize);
                var complete = _downloadSize == 0 || _coveredBytes >= _downloadSize;
                if (!complete || _completed)
                    return false;
                _completed = true;
                return true;
            }
        }

        public TransferDownloadDto CreateDownloadDto(string transferId, bool single)
        {
            lock (_gate)
            {
                return new TransferDownloadDto(
                    Id,
                    Name,
                    _downloadSize,
                    single
                        ? $"/api/transfers/{transferId}/download"
                        : $"/api/transfers/{transferId}/files/{Id}",
                    _started,
                    _completed);
            }
        }

        public TransferHistoryEntry CreateHistoryEntry(string transferId)
        {
            var entry = Entries[0];
            return new TransferHistoryEntry(
                transferId + ":" + Id,
                entry.Name,
                TransferDirection.ToPhone,
                entry.Size,
                entry.FileCount,
                entry.FullPath,
                entry.IsFolder,
                DateTimeOffset.UtcNow);
        }

        public void ResetForRetry()
        {
            lock (_gate)
            {
                _coveredBytes = 0;
                _started = false;
                _completed = false;
                if (NeedsArchive)
                {
                    _downloadPath = null;
                    _downloadSize = 0;
                    _preparationProcessed = 0;
                    _ready = false;
                }
            }
        }
    }

    private readonly record struct ArchivePayloadReference(string TransferId, string PayloadId);
}
