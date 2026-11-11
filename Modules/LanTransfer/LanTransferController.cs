using System.Diagnostics;
using System.IO;
using System.Net;
using Serilog;
using STool.Core;

namespace STool.Modules.LanTransfer;

internal enum ServerLifecycleState
{
    Stopped,
    Starting,
    PermissionRequired,
    Running,
    Stopping,
    Failed
}

/// <summary>状态栏要表达的含义；具体文案由窗口决定，不要拿文案当状态值。</summary>
internal enum LanStatusKind
{
    Stopped,
    Starting,
    WaitingForPermission,
    WaitingForPhone,
    PhoneConnected,
    Stopping,
    StartFailed,
    NoNetwork,
    FirewallRepairSuggested
}

/// <summary>地址栏是否需要用提示替代真实地址。</summary>
internal enum LanAddressNotice
{
    None,
    NoNetwork,
    Error
}

internal enum AddSharedResult
{
    Queued,
    /// <summary>面板正在关闭，忽略。</summary>
    Ignored,
    ScanFailed,
    NothingFound,
    ServerNotRunning,
    ItemsUnavailable,
    QueueFailed
}

internal sealed record AddSharedOutcome(AddSharedResult Result, string? Error = null);

internal enum TransferUpdateKind
{
    /// <summary>进行中，需要新增或刷新进度行。</summary>
    Active,
    /// <summary>已结束（完成、取消、拒绝），需要移除进度行。</summary>
    Finished
}

internal sealed record TransferUpdate(TransferSessionSnapshot Snapshot, TransferUpdateKind Kind);

/// <summary>
/// 局域网传输面板背后的与界面无关的逻辑：服务的创建/启动/停止/重启、地址选择与网络变化、
/// 防火墙修复判断、共享文件扫描、传输进度汇总与历史记录。窗口只负责绑定和渲染。
/// 所有公开方法都在 UI 线程（创建控制器时的 SynchronizationContext）上调用；
/// 服务线程触发的事件会先切回该上下文再通知窗口。
/// </summary>
internal sealed class LanTransferController : IDisposable
{
    private static readonly TimeSpan ClientPresenceTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultNetworkChangeDebounce = TimeSpan.FromSeconds(1);

    private readonly ConfigManager _configManager;
    private readonly ILanTransferEnvironment _environment;
    private readonly SharedFileCatalog _sharedFiles = new();
    private readonly DeviceTokenStore _deviceTokens;
    private readonly TransferHistoryStore _historyStore;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly TimeSpan _networkChangeDebounce;
    private readonly HashSet<string> _separateOutgoingIds = new(StringComparer.Ordinal);
    private readonly object _progressGate = new();
    private readonly Dictionary<string, TransferSessionSnapshot> _pendingProgress = new(StringComparer.Ordinal);
    private readonly IDisposable _networkSubscription;
    private LanTransferServer? _server;
    // 服务重启的"最新者胜出"串行化。
    private Task _lastRestart = Task.CompletedTask;
    private CancellationTokenSource _restartCts = new();
    private CancellationTokenSource? _networkCts;
    private long _lastClientActivityTimestamp;
    private bool _phoneConnected;
    private volatile bool _closing;
    private bool _disposed;

    public LanTransferController(
        ConfigManager configManager,
        ILanTransferEnvironment? environment = null,
        DeviceTokenStore? deviceTokens = null,
        TransferHistoryStore? historyStore = null,
        TimeSpan? networkChangeDebounce = null)
    {
        _configManager = configManager;
        _environment = environment ?? new SystemLanTransferEnvironment();
        _deviceTokens = deviceTokens ?? new DeviceTokenStore();
        _historyStore = historyStore ?? new TransferHistoryStore();
        _networkChangeDebounce = networkChangeDebounce ?? DefaultNetworkChangeDebounce;
        _networkSubscription = _environment.SubscribeNetworkChanged(OnNetworkAddressChanged);
    }

    /// <summary>状态、地址或二维码变化，窗口据此重新渲染。在 UI 上下文触发。</summary>
    public event Action? Changed;

    public event Action<ReceivedBatchInfo>? BatchReceived;

    /// <summary>有新的历史记录（接收完成或发送完成），窗口据此插入一行。</summary>
    public event Action<TransferHistoryEntry>? HistoryAdded;

    public ServerLifecycleState State { get; private set; } = ServerLifecycleState.Stopped;
    public LanStatusKind Status { get; private set; } = LanStatusKind.Stopped;
    public LanAddressNotice AddressNotice { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool RepairVisible { get; private set; }
    public bool IsClosing => _closing;
    public bool IsRunning => _server?.IsRunning == true;
    public int ActiveTransferCount => _server?.ActiveTransferCount ?? 0;
    public string? Address => _server?.Address;
    public string? ManualCode => _server?.ManualCode;
    public string? QrAddress => _server?.QrAddress;

    // ---- 配置 ----

    public static string GetDefaultReceiveDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "STool");
    }

    /// <summary>返回接收目录；尚未设置时写入默认目录。</summary>
    public string EnsureReceiveDirectory()
    {
        var config = _configManager.Get();
        if (string.IsNullOrWhiteSpace(config.LanTransfer.ReceiveDirectory))
        {
            var defaultDirectory = GetDefaultReceiveDirectory();
            config = _configManager.Update(current => current.LanTransfer.ReceiveDirectory = defaultDirectory);
        }

        return config.LanTransfer.ReceiveDirectory;
    }

    public void SetReceiveDirectory(string path) =>
        _configManager.Update(config => config.LanTransfer.ReceiveDirectory = path);

    /// <summary>
    /// 是否需要修复：只看当前端口，没有覆盖该端口的防火墙放行规则才需要；
    /// 无法判断（null）时不打扰用户。与程序所在路径无关。
    /// </summary>
    internal static bool NeedsRepair(bool? firewallOpenForPort) => firewallOpenForPort == false;

    /// <summary>以管理员权限为当前端口配置监听权限和防火墙，成功后调用 <see cref="RestartAsync"/> 生效。</summary>
    public Task<bool> ConfigureAccessAsync() =>
        _environment.RunElevatedSetupAsync(_configManager.Get().LanTransfer.Port);

    // ---- 服务生命周期 ----

    public Task RestartAsync()
    {
        if (_closing)
            return Task.CompletedTask;

        // 旧的重启尚未完成时取消它,排队这次重启;依次 await 保证同一时刻只有一个重启在跑。
        _restartCts.Cancel();
        _restartCts.Dispose();
        _restartCts = new CancellationTokenSource();
        var token = _restartCts.Token;
        var previous = _lastRestart;
        _lastRestart = RunAfterAsync(previous, () => RestartCoreAsync(token));
        return _lastRestart;
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try { await previous; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug(ex, "Previous LAN server restart ended with an error"); }

        await operation();
    }

    private async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopServerCoreAsync(updateState: false);
            cancellationToken.ThrowIfCancellationRequested();
            SetState(ServerLifecycleState.Starting);

            var endpoint = await Task.Run(_environment.GetPreferredEndpoint);
            cancellationToken.ThrowIfCancellationRequested();
            if (endpoint == null)
            {
                AddressNotice = LanAddressNotice.NoNetwork;
                RepairVisible = false;
                SetState(ServerLifecycleState.Failed, LanStatusKind.NoNetwork);
                return;
            }

            var config = _configManager.Get();
            var port = config.LanTransfer.Port;
            var receiveDirectory = config.LanTransfer.ReceiveDirectory;
            // 防火墙读取与目录创建都是 IO，放到后台。
            var firewallOpen = await Task.Run(() => _environment.IsFirewallOpenForPort(port));
            await Task.Run(() => Directory.CreateDirectory(receiveDirectory));
            cancellationToken.ThrowIfCancellationRequested();

            var server = _environment.CreateServer(
                endpoint,
                port,
                receiveDirectory,
                config.LanTransfer.MaxConcurrentTransfers,
                _sharedFiles,
                _deviceTokens);
            AttachServer(server);
            _server = server;

            try
            {
                await server.StartAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_server, server))
                    return;

                var needsRepair = NeedsRepair(firewallOpen);
                AddressNotice = LanAddressNotice.None;
                RepairVisible = needsRepair;
                ResetClientPresence();
                SetState(
                    ServerLifecycleState.Running,
                    needsRepair ? LanStatusKind.FirewallRepairSuggested : LanStatusKind.WaitingForPhone);
                MemoryDiagnostics.LogCheckpoint("LanTransferOpened");
            }
            catch
            {
                await StopServerCoreAsync(updateState: false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("LAN transfer server restart superseded or canceled");
        }
        catch (Exception ex) when (ex is HttpListenerException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "LAN transfer listener requires setup");
            EnterPermissionRequired();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start LAN transfer");
            AddressNotice = LanAddressNotice.Error;
            ErrorMessage = ex.Message;
            RepairVisible = true;
            SetState(ServerLifecycleState.Failed);
        }
    }

    private void EnterPermissionRequired()
    {
        ResetClientPresence();
        RepairVisible = false;
        SetState(ServerLifecycleState.PermissionRequired);
    }

    /// <summary>关闭面板时调用：取消排队的重启并停止服务。之后控制器不再响应重启请求。</summary>
    public async Task ShutdownAsync()
    {
        _closing = true;
        try
        {
            // 取消仍在排队的重启并等它退出,再执行最终的停止。
            _restartCts.Cancel();
            try { await _lastRestart; }
            catch (OperationCanceledException) { }

            SetState(ServerLifecycleState.Stopping);
            await StopServerCoreAsync(updateState: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LAN transfer cleanup failed during close");
        }
    }

    private async Task StopServerCoreAsync(bool updateState)
    {
        var server = _server;
        _server = null;
        if (server == null)
        {
            if (updateState)
                SetState(ServerLifecycleState.Stopped);
            return;
        }

        DetachServer(server);
        ResetClientPresence();
        try
        {
            await server.DisposeAsync();
        }
        finally
        {
            if (updateState)
                SetState(ServerLifecycleState.Stopped);
        }
    }

    private void SetState(ServerLifecycleState state, LanStatusKind? status = null)
    {
        State = state;
        Status = status ?? state switch
        {
            ServerLifecycleState.Stopped => LanStatusKind.Stopped,
            ServerLifecycleState.Starting => LanStatusKind.Starting,
            ServerLifecycleState.PermissionRequired => LanStatusKind.WaitingForPermission,
            ServerLifecycleState.Running => LanStatusKind.WaitingForPhone,
            ServerLifecycleState.Stopping => LanStatusKind.Stopping,
            ServerLifecycleState.Failed => LanStatusKind.StartFailed,
            _ => LanStatusKind.WaitingForPhone
        };
        Changed?.Invoke();
    }

    private void SetStatus(LanStatusKind status)
    {
        Status = status;
        Changed?.Invoke();
    }

    // ---- 服务事件 ----

    private void AttachServer(LanTransferServer server)
    {
        server.BatchReceived += Server_BatchReceived;
        server.TransferChanged += Server_TransferChanged;
        server.OutgoingItemCompleted += Server_OutgoingItemCompleted;
        server.ClientActivity += Server_ClientActivity;
        server.AccessCodesRotated += Server_AccessCodesRotated;
    }

    private void DetachServer(LanTransferServer server)
    {
        server.BatchReceived -= Server_BatchReceived;
        server.TransferChanged -= Server_TransferChanged;
        server.OutgoingItemCompleted -= Server_OutgoingItemCompleted;
        server.ClientActivity -= Server_ClientActivity;
        server.AccessCodesRotated -= Server_AccessCodesRotated;
    }

    private void Post(Action action)
    {
        if (_context == null)
            action();
        else
            _context.Post(_ => action(), null);
    }

    /// <summary>配对成功或触发限流后服务端会自动换码，二维码与验证码随之刷新。</summary>
    private void Server_AccessCodesRotated()
    {
        if (_closing)
            return;

        Post(() =>
        {
            if (!_closing && _server?.IsRunning == true)
                Changed?.Invoke();
        });
    }

    private void Server_BatchReceived(ReceivedBatchInfo info)
    {
        if (_closing)
            return;
        Post(() => BatchReceived?.Invoke(info));
    }

    private void Server_OutgoingItemCompleted(TransferHistoryEntry entry)
    {
        if (_closing)
            return;
        // 历史文件写入留在服务线程，只把界面通知切回 UI。
        _historyStore.Add(entry);
        Post(() => HistoryAdded?.Invoke(entry));
    }

    private void Server_TransferChanged(TransferSessionSnapshot snapshot)
    {
        if (_closing)
            return;
        lock (_progressGate)
            _pendingProgress[snapshot.Id] = snapshot;
    }

    private void Server_ClientActivity(LanTransferServer server)
    {
        if (_closing || !ReferenceEquals(_server, server))
            return;

        Interlocked.Exchange(ref _lastClientActivityTimestamp, Stopwatch.GetTimestamp());
        Post(() =>
        {
            if (_closing || !ReferenceEquals(_server, server) || _phoneConnected)
                return;
            _phoneConnected = true;
            SetStatus(LanStatusKind.PhoneConnected);
        });
    }

    // ---- 手机在线状态 ----

    internal static bool IsClientPresent(long lastActivityTimestamp, long currentTimestamp) =>
        lastActivityTimestamp != 0 &&
        Stopwatch.GetElapsedTime(lastActivityTimestamp, currentTimestamp) <= ClientPresenceTimeout;

    /// <summary>由窗口的 1 秒定时器调用，刷新“手机已连接/等待手机连接”。</summary>
    public void CheckClientPresence()
    {
        if (_closing || _server?.IsRunning != true)
            return;

        var lastActivity = Interlocked.Read(ref _lastClientActivityTimestamp);
        var connected = IsClientPresent(lastActivity, Stopwatch.GetTimestamp());
        if (connected == _phoneConnected)
            return;

        _phoneConnected = connected;
        SetStatus(connected ? LanStatusKind.PhoneConnected : LanStatusKind.WaitingForPhone);
    }

    private void ResetClientPresence()
    {
        Interlocked.Exchange(ref _lastClientActivityTimestamp, 0);
        _phoneConnected = false;
    }

    // ---- 网络变化 ----

    private void OnNetworkAddressChanged()
    {
        if (_closing || _disposed)
            return;

        // 网络切换常连续触发多次，合并后再处理。
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _networkCts, cts)?.Cancel();
        _ = RefreshAfterDelayAsync(cts.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_networkChangeDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Post(() => _ = RefreshNetworkAsync());
    }

    /// <summary>
    /// 重新选择局域网地址。服务监听的是通配地址，运行中只需更新显示的地址、二维码和来源网段校验，
    /// 不会打断进行中的传输；之前因没有局域网而失败时，网络恢复后自动重启服务。
    /// </summary>
    internal async Task RefreshNetworkAsync()
    {
        if (_closing || _disposed)
            return;

        try
        {
            var endpoint = await Task.Run(_environment.GetPreferredEndpoint);
            if (_closing || _disposed)
                return;

            var server = _server;
            if (server != null)
            {
                // 暂时没有可用网络时保留原地址，等网络恢复再更新。
                if (endpoint == null || endpoint == server.Endpoint)
                    return;

                server.UpdateEndpoint(endpoint);
                Changed?.Invoke();
                return;
            }

            if (endpoint != null && State == ServerLifecycleState.Failed && AddressNotice == LanAddressNotice.NoNetwork)
                await RestartAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh LAN transfer address after network change");
        }
    }

    // ---- 访问控制 ----

    public bool RotateAccessCodes()
    {
        if (_server == null)
            return false;
        // 换码会触发 AccessCodesRotated，由它通知窗口刷新二维码与验证码。
        _server.RotateAccessCodes();
        return true;
    }

    /// <summary>移除记住的设备；服务在运行时同时让当前在线会话失效。</summary>
    public void RevokeRememberedDevices()
    {
        if (_server != null)
            _server.RevokeRememberedDevices();
        else
            _deviceTokens.RevokeAll();
    }

    // ---- 共享文件 ----

    internal static OutgoingTransferMode ResolveOutgoingMode(IReadOnlyList<SharedFileEntry> entries) =>
        entries.Count >= 6 || entries.Count == 1 && entries[0].IsFolder
            ? OutgoingTransferMode.CombinedArchive
            : entries.Count == 1
                ? OutgoingTransferMode.Automatic
                : OutgoingTransferMode.Separate;

    public async Task<AddSharedOutcome> AddSharedPathsAsync(IEnumerable<string> paths)
    {
        if (_closing)
            return new AddSharedOutcome(AddSharedResult.Ignored);

        // 文件夹会递归遍历目录，放到后台线程，结束后回到 UI 线程继续。
        var pathList = paths.ToArray();
        SharedFileEntry[] added;
        try
        {
            added = await Task.Run(() => _sharedFiles.AddPaths(pathList, allowDuplicatePaths: true).ToArray());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to scan shared paths");
            return new AddSharedOutcome(AddSharedResult.ScanFailed, ex.Message);
        }

        if (_closing)
        {
            RemoveShared(added);
            return new AddSharedOutcome(AddSharedResult.Ignored);
        }

        if (added.Length == 0)
            return new AddSharedOutcome(AddSharedResult.NothingFound);

        if (_server?.IsRunning != true)
            await RestartAsync();

        var server = _server;
        if (server?.IsRunning != true)
        {
            RemoveShared(added);
            return new AddSharedOutcome(AddSharedResult.ServerNotRunning);
        }

        var available = added.Where(item => item.IsAvailable).ToArray();
        if (available.Length != added.Length)
        {
            RemoveShared(added);
            return new AddSharedOutcome(AddSharedResult.ItemsUnavailable);
        }

        var mode = ResolveOutgoingMode(available);
        try
        {
            var transfer = server.QueueOutgoing(available, mode);
            if (mode == OutgoingTransferMode.Separate)
                _separateOutgoingIds.Add(transfer.Id);
        }
        catch (Exception ex)
        {
            RemoveShared(added);
            return new AddSharedOutcome(AddSharedResult.QueueFailed, ex.Message);
        }

        return new AddSharedOutcome(AddSharedResult.Queued);
    }

    private void RemoveShared(IEnumerable<SharedFileEntry> items)
    {
        foreach (var item in items)
            _sharedFiles.Remove(item.Id);
    }

    // ---- 传输任务 ----

    public void PauseTransfer(string id) => _server?.PauseTransfer(id);

    public void ResumeTransfer(string id) => _server?.ResumeTransfer(id);

    public bool RetryTransfer(string id) => _server?.RetryTransfer(id) == true;

    public Task<bool> CancelTransferAsync(string id) =>
        _server?.CancelTransferAsync(id) ?? Task.FromResult(false);

    /// <summary>移除失败的任务。</summary>
    public async Task RemoveFailedTransferAsync(string id)
    {
        if (_server != null)
            await _server.RemoveTransferAsync(id);
        _separateOutgoingIds.Remove(id);
    }

    /// <summary>取出自上次以来的进度变化（按开始时间排序），并处理完成/取消后的历史与标记。由窗口定时器调用。</summary>
    public IReadOnlyList<TransferUpdate> DrainTransferUpdates()
    {
        TransferSessionSnapshot[] snapshots;
        lock (_progressGate)
        {
            if (_pendingProgress.Count == 0)
                return [];
            snapshots = _pendingProgress.Values.ToArray();
            _pendingProgress.Clear();
        }

        var updates = new List<TransferUpdate>(snapshots.Length);
        foreach (var snapshot in snapshots.OrderBy(item => item.StartedUtc))
        {
            if (_closing)
                break;

            if (snapshot.State == TransferState.Completed)
            {
                updates.Add(new TransferUpdate(snapshot, TransferUpdateKind.Finished));
                if (!_separateOutgoingIds.Remove(snapshot.Id))
                    AddCompletedHistory(snapshot);
            }
            else if (snapshot.State is TransferState.Canceled or TransferState.Rejected)
            {
                _separateOutgoingIds.Remove(snapshot.Id);
                updates.Add(new TransferUpdate(snapshot, TransferUpdateKind.Finished));
            }
            else
            {
                updates.Add(new TransferUpdate(snapshot, TransferUpdateKind.Active));
            }
        }

        return updates;
    }

    // ---- 历史记录 ----

    public IReadOnlyList<TransferHistoryEntry> LoadHistory() => _historyStore.Snapshot();

    public void RemoveHistory(string id) => _historyStore.Remove(id);

    public void ClearHistory() => _historyStore.Clear();

    private void AddCompletedHistory(TransferSessionSnapshot snapshot)
    {
        var entry = BuildHistoryEntry(snapshot, _configManager.Get().LanTransfer.ReceiveDirectory);
        if (entry == null)
            return;

        _historyStore.Add(entry);
        HistoryAdded?.Invoke(entry);
    }

    /// <summary>由完成的传输快照生成历史记录；没有可定位的路径时返回 null。</summary>
    internal static TransferHistoryEntry? BuildHistoryEntry(TransferSessionSnapshot snapshot, string receiveDirectory)
    {
        var path = snapshot.DestinationPath;
        if (string.IsNullOrWhiteSpace(path))
            path = snapshot.Direction == TransferDirection.ToComputer
                ? receiveDirectory
                : snapshot.SourcePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return new TransferHistoryEntry(
            snapshot.Id,
            snapshot.Name,
            snapshot.Direction,
            snapshot.TotalBytes,
            snapshot.FileCount,
            path,
            Directory.Exists(path),
            snapshot.CompletedUtc ?? DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _closing = true;

        _networkSubscription.Dispose();
        Interlocked.Exchange(ref _networkCts, null)?.Cancel();
        _restartCts.Cancel();
        _restartCts.Dispose();
        lock (_progressGate)
            _pendingProgress.Clear();
        _sharedFiles.Clear();
        _separateOutgoingIds.Clear();

        // 正常关闭流程已经停掉了服务；这里兜底释放没走关闭流程的情况。
        var server = _server;
        _server = null;
        if (server != null)
        {
            DetachServer(server);
            _ = server.DisposeAsync().AsTask().ContinueWith(
                task => Log.Debug(task.Exception, "LAN server disposal failed"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
