using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace STool.Modules.LanTransfer;

internal sealed class TransferSessionManager : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDisconnectedDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultStalledDelay = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, TransferSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _disconnectedDelay;
    private readonly TimeSpan _stalledDelay;
    private readonly Task _watchdogTask;

    internal TransferSessionManager(
        TimeSpan? disconnectedDelay = null,
        TimeSpan? stalledDelay = null)
    {
        _disconnectedDelay = disconnectedDelay ?? DefaultDisconnectedDelay;
        _stalledDelay = stalledDelay ?? DefaultStalledDelay;
        _watchdogTask = WatchForStalledTransfersAsync(_lifetime.Token);
    }

    public event Action<TransferSessionSnapshot>? Changed;

    public IReadOnlyList<TransferSessionSnapshot> Snapshot(bool includeTerminal = false) =>
        _sessions.Values
            .Select(session => session.Snapshot())
            .Where(snapshot => includeTerminal || snapshot.State is not
                (TransferState.Completed or TransferState.Canceled or TransferState.Rejected))
            .OrderBy(snapshot => snapshot.StartedUtc)
            .ToArray();

    public TransferSessionSnapshot CreateOutgoing(
        string name,
        long totalBytes,
        int fileCount,
        IReadOnlyList<string> sourcePaths,
        string? destinationPath,
        bool preparing)
    {
        var session = new TransferSession(
            Guid.NewGuid().ToString("N"),
            name,
            TransferDirection.ToPhone,
            preparing ? TransferState.Preparing : TransferState.AwaitingConfirmation,
            totalBytes,
            fileCount,
            sourcePaths,
            destinationPath,
            null);
        _sessions[session.Id] = session;
        return Publish(session);
    }

    public TransferSessionSnapshot CreateIncoming(
        string id,
        string name,
        long totalBytes,
        int fileCount,
        string ownerToken)
    {
        var session = new TransferSession(
            id,
            name,
            TransferDirection.ToComputer,
            TransferState.Transferring,
            totalBytes,
            fileCount,
            [],
            null,
            ownerToken);
        if (!_sessions.TryAdd(id, session))
            throw new InvalidDataException("上传批次已经存在。 ");
        return Publish(session);
    }

    public bool TryGet(string id, out TransferSessionSnapshot? snapshot)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            snapshot = session.Snapshot();
            return true;
        }
        snapshot = null;
        return false;
    }

    public bool CanControl(string id, string ownerToken)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;
        lock (session.Gate)
            return session.OwnerToken == null || string.Equals(session.OwnerToken, ownerToken, StringComparison.Ordinal);
    }

    public bool TryClaim(string id, string ownerToken)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;
        lock (session.Gate)
        {
            if (session.OwnerToken != null && !string.Equals(session.OwnerToken, ownerToken, StringComparison.Ordinal))
                return false;
            session.OwnerToken ??= ownerToken;
            return session.State is TransferState.AwaitingConfirmation or TransferState.WaitingForResume or
                TransferState.Transferring or TransferState.Paused;
        }
    }

    public void AssignOwner(string id, string ownerToken)
    {
        var session = GetSession(id);
        lock (session.Gate)
            session.OwnerToken ??= ownerToken;
    }

    public CancellationToken GetCancellationToken(string id) =>
        GetSession(id).Cancellation.Token;

    public AsyncPauseGate GetPauseGate(string id) => GetSession(id).PauseGate;

    public int ActiveCount => Snapshot().Count;

    public Task WaitIfPausedAsync(string id, CancellationToken cancellationToken) =>
        GetSession(id).PauseGate.WaitAsync(cancellationToken);

    public void SetPreparingProgress(string id, long transferred, long total)
    {
        var session = GetSession(id);
        lock (session.Gate)
        {
            if (session.State is TransferState.Canceled or TransferState.Failed)
                return;
            session.TransferredBytes = Math.Clamp(transferred, 0, Math.Max(0, total));
            session.TotalBytes = Math.Max(0, total);
            if (session.State != TransferState.Paused)
                session.State = TransferState.Preparing;
            session.LastProgressTimestamp = Stopwatch.GetTimestamp();
        }
        Publish(session);
    }

    public void SetAwaitingConfirmation(string id, long preparedSize)
    {
        var session = GetSession(id);
        lock (session.Gate)
        {
            if (IsTerminal(session.State))
                return;
            session.TotalBytes = Math.Max(0, preparedSize);
            session.TransferredBytes = 0;
            session.State = TransferState.AwaitingConfirmation;
            session.Error = null;
            ResetSpeedLocked(session);
        }
        Publish(session);
    }

    public void BeginRequest(string id)
    {
        var session = GetSession(id);
        lock (session.Gate)
        {
            if (IsTerminal(session.State))
                throw new OperationCanceledException("传输任务已结束。 ");
            if (session.ActiveRequests == 0 && session.State is
                (TransferState.AwaitingConfirmation or TransferState.WaitingForResume))
            {
                ResetSpeedLocked(session);
            }
            session.ActiveRequests++;
            session.DisconnectGeneration++;
            if (session.State != TransferState.Paused)
                session.State = TransferState.Transferring;
            session.LastProgressTimestamp = Stopwatch.GetTimestamp();
        }
        Publish(session);
    }

    public void ReportProgress(string id, long transferred, long total, bool completeWhenReached = true)
    {
        var session = GetSession(id);
        var completed = false;
        lock (session.Gate)
        {
            if (IsTerminal(session.State))
                return;
            session.TotalBytes = Math.Max(0, total);
            var nextTransferred = Math.Clamp(transferred, 0, session.TotalBytes);
            UpdateSpeedLocked(session, nextTransferred);
            session.TransferredBytes = nextTransferred;
            session.LastProgressTimestamp = Stopwatch.GetTimestamp();
            if (session.State != TransferState.Paused)
                session.State = TransferState.Transferring;
            completed = completeWhenReached &&
                (session.TotalBytes <= 0 || session.TransferredBytes >= session.TotalBytes);
            if (completed)
                CompleteLocked(session, session.DestinationPath);
        }
        Publish(session);
    }

    public void EndRequest(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return;
        int generation;
        lock (session.Gate)
        {
            if (session.ActiveRequests > 0)
                session.ActiveRequests--;
            generation = ++session.DisconnectGeneration;
            if (session.ActiveRequests > 0 || IsTerminal(session.State) || session.State == TransferState.Paused)
                return;
        }
        _ = MarkDisconnectedAfterDelayAsync(session, generation, _lifetime.Token);
    }

    public bool Pause(string id) => ChangePauseState(id, true);

    public bool Resume(string id) => ChangePauseState(id, false);

    public bool Cancel(string id, bool rejected = false)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;
        lock (session.Gate)
        {
            if (IsTerminal(session.State))
                return false;
            session.State = rejected ? TransferState.Rejected : TransferState.Canceled;
            session.CompletedUtc = DateTimeOffset.UtcNow;
            session.BytesPerSecond = 0;
            session.Cancellation.Cancel();
            session.PauseGate.Resume();
        }
        Publish(session);
        return true;
    }

    public void Fail(string id, string message)
    {
        var session = GetSession(id);
        lock (session.Gate)
        {
            if (IsTerminal(session.State))
                return;
            session.State = TransferState.Failed;
            session.Error = message;
            session.CompletedUtc = DateTimeOffset.UtcNow;
            session.BytesPerSecond = 0;
            session.PauseGate.Resume();
        }
        Publish(session);
    }

    public void Complete(string id, string? destinationPath = null)
    {
        var session = GetSession(id);
        lock (session.Gate)
            CompleteLocked(session, destinationPath);
        Publish(session);
    }

    public bool ResetForRetry(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;
        lock (session.Gate)
        {
            if (session.State != TransferState.Failed)
                return false;
            session.Cancellation.Dispose();
            session.Cancellation = new CancellationTokenSource();
            session.State = TransferState.Preparing;
            session.TransferredBytes = 0;
            session.CompletedUtc = null;
            session.Error = null;
            session.PauseGate.Resume();
            session.LastProgressTimestamp = Stopwatch.GetTimestamp();
            ResetSpeedLocked(session);
        }
        Publish(session);
        return true;
    }

    public void Remove(string id)
    {
        if (_sessions.TryRemove(id, out var session))
            session.Dispose();
    }

    private bool ChangePauseState(string id, bool pause)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;
        lock (session.Gate)
        {
            if (IsTerminal(session.State) || session.State == TransferState.AwaitingConfirmation)
                return false;
            if (pause)
            {
                if (session.State == TransferState.Paused)
                    return true;
                session.StateBeforePause = session.State;
                session.PauseGate.Pause();
                session.State = TransferState.Paused;
                session.BytesPerSecond = 0;
            }
            else
            {
                if (session.State != TransferState.Paused)
                    return session.State == TransferState.WaitingForResume;
                session.PauseGate.Resume();
                session.State = session.StateBeforePause == TransferState.Preparing
                    ? TransferState.Preparing
                    : session.ActiveRequests > 0
                        ? TransferState.Transferring
                        : TransferState.WaitingForResume;
                session.LastProgressTimestamp = Stopwatch.GetTimestamp();
                ResetSpeedLocked(session);
            }
        }
        Publish(session);
        return true;
    }

    private async Task MarkDisconnectedAfterDelayAsync(
        TransferSession session,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_disconnectedDelay, cancellationToken);
            lock (session.Gate)
            {
                if (session.DisconnectGeneration != generation || session.ActiveRequests > 0 ||
                    IsTerminal(session.State) || session.State == TransferState.Paused)
                    return;
                session.State = TransferState.WaitingForResume;
                session.BytesPerSecond = 0;
            }
            Publish(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WatchForStalledTransfersAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var session in _sessions.Values)
                {
                    var changed = false;
                    lock (session.Gate)
                    {
                        if (session.State == TransferState.Transferring && session.ActiveRequests > 0 &&
                            Stopwatch.GetElapsedTime(session.LastProgressTimestamp) >= _stalledDelay)
                        {
                            session.State = TransferState.WaitingForResume;
                            session.BytesPerSecond = 0;
                            changed = true;
                        }
                    }
                    if (changed)
                        Publish(session);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TransferSession GetSession(string id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : throw new FileNotFoundException("传输任务不存在。 ");

    private TransferSessionSnapshot Publish(TransferSession session)
    {
        var snapshot = session.Snapshot();
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    private static void CompleteLocked(TransferSession session, string? destinationPath)
    {
        if (IsTerminal(session.State))
            return;
        session.State = TransferState.Completed;
        session.TransferredBytes = session.TotalBytes;
        session.DestinationPath = destinationPath ?? session.DestinationPath;
        session.CompletedUtc = DateTimeOffset.UtcNow;
        session.Error = null;
        session.BytesPerSecond = 0;
        session.PauseGate.Resume();
    }

    private static void UpdateSpeedLocked(TransferSession session, long transferred)
    {
        var now = Stopwatch.GetTimestamp();
        if (transferred < session.SpeedSampleBytes)
        {
            session.SpeedSampleBytes = transferred;
            session.SpeedSampleTimestamp = now;
            session.BytesPerSecond = 0;
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(session.SpeedSampleTimestamp, now).TotalSeconds;
        var delta = transferred - session.SpeedSampleBytes;
        if (delta <= 0 || elapsed < 0.02)
            return;

        var current = delta / elapsed;
        session.BytesPerSecond = session.BytesPerSecond <= 0
            ? current
            : session.BytesPerSecond * 0.65 + current * 0.35;
        session.SpeedSampleBytes = transferred;
        session.SpeedSampleTimestamp = now;
    }

    private static void ResetSpeedLocked(TransferSession session)
    {
        session.BytesPerSecond = 0;
        session.SpeedSampleBytes = session.TransferredBytes;
        session.SpeedSampleTimestamp = Stopwatch.GetTimestamp();
    }

    private static bool IsTerminal(TransferState state) =>
        state is TransferState.Completed or TransferState.Canceled or TransferState.Rejected or TransferState.Failed;

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _watchdogTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Transfer watchdog ended with an error during disposal"); }
        foreach (var session in _sessions.Values)
        {
            session.Cancellation.Cancel();
            session.PauseGate.Resume();
            session.Dispose();
        }
        _sessions.Clear();
        _lifetime.Dispose();
    }

    private sealed class TransferSession(
        string id,
        string name,
        TransferDirection direction,
        TransferState state,
        long totalBytes,
        int fileCount,
        IReadOnlyList<string> sourcePaths,
        string? destinationPath,
        string? ownerToken) : IDisposable
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string Name { get; } = name;
        public TransferDirection Direction { get; } = direction;
        public TransferState State { get; set; } = state;
        public TransferState StateBeforePause { get; set; } = state;
        public long TransferredBytes { get; set; }
        public long TotalBytes { get; set; } = Math.Max(0, totalBytes);
        public double BytesPerSecond { get; set; }
        public long SpeedSampleBytes { get; set; }
        public long SpeedSampleTimestamp { get; set; } = Stopwatch.GetTimestamp();
        public int FileCount { get; } = Math.Max(1, fileCount);
        public IReadOnlyList<string> SourcePaths { get; } = sourcePaths;
        public string? DestinationPath { get; set; } = destinationPath;
        public string? OwnerToken { get; set; } = ownerToken;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public string? Error { get; set; }
        public CancellationTokenSource Cancellation { get; set; } = new();
        public AsyncPauseGate PauseGate { get; } = new();
        public int ActiveRequests { get; set; }
        public int DisconnectGeneration { get; set; }
        public long LastProgressTimestamp { get; set; } = Stopwatch.GetTimestamp();

        public TransferSessionSnapshot Snapshot()
        {
            lock (Gate)
            {
                return new TransferSessionSnapshot(
                    Id,
                    Name,
                    Direction,
                    State,
                    TransferredBytes,
                    TotalBytes,
                    State == TransferState.Transferring &&
                    Stopwatch.GetElapsedTime(LastProgressTimestamp) < TimeSpan.FromSeconds(2)
                        ? BytesPerSecond
                        : 0,
                    FileCount,
                    SourcePaths,
                    DestinationPath,
                    StartedUtc,
                    CompletedUtc,
                    Error,
                    Direction == TransferDirection.ToPhone && State is
                        (TransferState.AwaitingConfirmation or TransferState.Transferring or
                         TransferState.Paused or TransferState.WaitingForResume)
                        ? $"/api/transfers/{Id}/download"
                        : null);
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }
}
