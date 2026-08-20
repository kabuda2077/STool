using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;
using Serilog;
using STool.Core;
using Forms = System.Windows.Forms;

namespace STool.Modules.LanTransfer;

public partial class LanTransferWindow : Window
{
    internal enum ServerLifecycleState
    {
        Stopped,
        Starting,
        PermissionRequired,
        Running,
        Stopping,
        Failed
    }

    private static readonly TimeSpan ClientPresenceTimeout = TimeSpan.FromSeconds(12);
    private readonly ConfigManager _configManager;
    private readonly SharedFileCatalog _sharedFiles = new();
    private readonly DeviceTokenStore _deviceTokens = new();
    private readonly TransferHistoryStore _historyStore = new();
    private readonly ObservableCollection<TransferRow> _activeRows = [];
    private readonly ObservableCollection<HistoryRow> _historyRows = [];
    private readonly HashSet<string> _separateOutgoingIds = new(StringComparer.Ordinal);
    private LanTransferServer? _server;
    private NetworkEndpoint? _endpoint;
    private readonly LatestOperationCoordinator _serverLifecycle = new();
    private ServerLifecycleState _serverState = ServerLifecycleState.Stopped;
    private bool _closing;
    private bool _cleanupCompleted;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _connectionTimer;
    private readonly object _progressGate = new();
    private readonly Dictionary<string, TransferSessionSnapshot> _pendingProgress = new(StringComparer.Ordinal);
    private long _lastClientActivityTimestamp;
    private bool _phoneConnected;
    private bool _showingHistory;

    public LanTransferWindow(ConfigManager configManager)
    {
        InitializeComponent();
        _configManager = configManager;
        sharedList.ItemsSource = _activeRows;
        transferList.ItemsSource = _historyRows;
        UpdateActiveTransferVisibility();
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();
        _connectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _connectionTimer.Tick += ConnectionTimer_Tick;
        _connectionTimer.Start();
        Loaded += LanTransferWindow_Loaded;
    }

    private async void LanTransferWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var config = _configManager.Get();
        if (string.IsNullOrWhiteSpace(config.LanTransfer.ReceiveDirectory))
        {
            var defaultDirectory = GetDefaultReceiveDirectory();
            config = _configManager.Update(current => current.LanTransfer.ReceiveDirectory = defaultDirectory);
        }

        receiveDirectoryText.Text = config.LanTransfer.ReceiveDirectory;
        LoadTransferHistory();
        UpdateTransferTabUi(false);
        await RestartServerAsync();
    }

    private Task RestartServerAsync()
    {
        if (_closing)
            return Task.CompletedTask;

        return _serverLifecycle.RunLatestAsync(RestartServerCoreAsync);
    }

    private async Task RestartServerCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopServerCoreAsync(updateState: false);
            cancellationToken.ThrowIfCancellationRequested();
            SetServerState(ServerLifecycleState.Starting);

            _endpoint = NetworkEndpointSelector.GetPreferred();
            if (_endpoint == null)
            {
                ShowConnectionDetails();
                addressText.Text = "请先连接 Wi-Fi 或有线局域网";
                repairButton.Visibility = Visibility.Collapsed;
                SetServerState(ServerLifecycleState.Failed, "未找到可用的局域网");
                return;
            }

            var config = _configManager.Get();
            if (string.IsNullOrWhiteSpace(config.LanTransfer.ConfiguredExecutablePath))
            {
                ShowPermissionSetup();
                SetServerState(ServerLifecycleState.PermissionRequired);
                return;
            }

            ShowConnectionDetails();
            Directory.CreateDirectory(config.LanTransfer.ReceiveDirectory);
            var server = new LanTransferServer(
                _endpoint,
                config.LanTransfer.Port,
                config.LanTransfer.ReceiveDirectory,
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

                UpdateConnectionDetails();
                var needsRepair = !string.Equals(
                    config.LanTransfer.ConfiguredExecutablePath,
                    Environment.ProcessPath,
                    StringComparison.OrdinalIgnoreCase);
                repairButton.Visibility = needsRepair ? Visibility.Visible : Visibility.Collapsed;
                ResetClientPresence();
                SetServerState(
                    ServerLifecycleState.Running,
                    needsRepair ? "服务已启动，建议修复防火墙" : "等待手机连接");
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
            ShowPermissionSetup();
            SetServerState(ServerLifecycleState.PermissionRequired);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start LAN transfer");
            ShowConnectionDetails();
            addressText.Text = ex.Message;
            repairButton.Visibility = Visibility.Visible;
            SetServerState(ServerLifecycleState.Failed);
        }
    }

    private void AttachServer(LanTransferServer server)
    {
        server.FileReceived += Server_FileReceived;
        server.BatchReceived += Server_BatchReceived;
        server.TransferChanged += Server_TransferChanged;
        server.OutgoingItemCompleted += Server_OutgoingItemCompleted;
        server.ClientActivity += Server_ClientActivity;
    }

    private void DetachServer(LanTransferServer server)
    {
        server.FileReceived -= Server_FileReceived;
        server.BatchReceived -= Server_BatchReceived;
        server.TransferChanged -= Server_TransferChanged;
        server.OutgoingItemCompleted -= Server_OutgoingItemCompleted;
        server.ClientActivity -= Server_ClientActivity;
    }

    private void SetServerState(ServerLifecycleState state, string? status = null)
    {
        _serverState = state;
        var text = status ?? state switch
        {
            ServerLifecycleState.Stopped => "服务已停止",
            ServerLifecycleState.Starting => "正在启动服务",
            ServerLifecycleState.PermissionRequired => "等待配置局域网权限",
            ServerLifecycleState.Running => "等待手机连接",
            ServerLifecycleState.Stopping => "正在停止服务",
            ServerLifecycleState.Failed => "服务启动失败",
            _ => "等待手机连接"
        };
        SetStatus(text, state == ServerLifecycleState.Running && _phoneConnected);

        var transitioning = state is ServerLifecycleState.Starting or ServerLifecycleState.Stopping;
        var running = state == ServerLifecycleState.Running;
        browseReceiveDirectoryButton.IsEnabled = !transitioning;
        repairButton.IsEnabled = !transitioning;
        copyAddressButton.IsEnabled = running;
        refreshCodeButton.IsEnabled = running;
        revokeDevicesButton.IsEnabled = running;
        addFilesButton.IsEnabled = running;
        addFolderButton.IsEnabled = running;
        allowLanAccessButton.IsEnabled = state == ServerLifecycleState.PermissionRequired;
        permissionLaterButton.IsEnabled = state == ServerLifecycleState.PermissionRequired;
        AllowDrop = running;
    }

    private void ShowPermissionSetup()
    {
        ResetClientPresence();
        connectionDetailsPanel.Visibility = Visibility.Collapsed;
        permissionSetupPanel.Visibility = Visibility.Visible;
        repairButton.Visibility = Visibility.Collapsed;
        qrImage.Source = null;
        SetStatus("等待手机连接", false);
    }

    private void ShowConnectionDetails()
    {
        permissionSetupPanel.Visibility = Visibility.Collapsed;
        connectionDetailsPanel.Visibility = Visibility.Visible;
    }

    private void UpdateConnectionDetails()
    {
        if (_server == null)
            return;
        addressText.Text = _server.Address;
        codeText.Text = _server.ManualCode;
        qrImage.Source = CreateQrImage(_server.QrAddress);
    }

    private void SetStatus(string text, bool online)
    {
        statusText.Text = text;
        statusDot.Fill = (System.Windows.Media.Brush)FindResource(online ? "SuccessBrush" : "TextSecondaryBrush");
    }

    private void TransferTab_Click(object sender, RoutedEventArgs e)
    {
        var showHistory = ReferenceEquals(sender, historyTabButton);
        if (showHistory == _showingHistory)
            return;

        _showingHistory = showHistory;
        UpdateTransferTabUi(true);
    }

    private void UpdateTransferTabUi(bool animate)
    {
        shareTabButton.Tag = _showingHistory ? null : "on";
        historyTabButton.Tag = _showingHistory ? "on" : null;
        shareView.Visibility = _showingHistory ? Visibility.Collapsed : Visibility.Visible;
        historyView.Visibility = _showingHistory ? Visibility.Visible : Visibility.Collapsed;
        shareActions.Visibility = _showingHistory ? Visibility.Collapsed : Visibility.Visible;
        historyActions.Visibility = _showingHistory ? Visibility.Visible : Visibility.Collapsed;

        var selectedButton = _showingHistory ? historyTabButton : shareTabButton;
        var target = selectedButton.TranslatePoint(new System.Windows.Point(0, 0), transferTabGrid).X;
        SegmentedSliderMotion.MoveTo(
            transferTabSlider,
            transferTabSliderTransform,
            transferTabSliderScale,
            target,
            selectedButton.ActualWidth,
            animate && IsLoaded);
    }

    private void Server_ClientActivity(LanTransferServer server)
    {
        if (_closing || !ReferenceEquals(_server, server))
            return;

        Interlocked.Exchange(ref _lastClientActivityTimestamp, Stopwatch.GetTimestamp());
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !ReferenceEquals(_server, server) || _phoneConnected)
                return;
            _phoneConnected = true;
            SetStatus("手机已连接", true);
        });
    }

    private void ConnectionTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _server?.IsRunning != true)
            return;

        var lastActivity = Interlocked.Read(ref _lastClientActivityTimestamp);
        var connected = _server.ActiveTransferCount > 0 ||
            lastActivity != 0 && Stopwatch.GetElapsedTime(lastActivity) <= ClientPresenceTimeout;
        if (connected == _phoneConnected)
            return;

        _phoneConnected = connected;
        SetStatus(connected ? "手机已连接" : "等待手机连接", connected);
    }

    private void ResetClientPresence()
    {
        Interlocked.Exchange(ref _lastClientActivityTimestamp, 0);
        _phoneConnected = false;
    }

    private async void RepairConnection_Click(object sender, RoutedEventArgs e)
    {
        repairButton.IsEnabled = false;
        repairButton.Content = "配置中...";
        var success = await LanTransferSetup.RunElevatedAsync(_configManager.Get().LanTransfer.Port);
        repairButton.IsEnabled = true;
        repairButton.Content = "修复连接";
        if (!success)
        {
            ToastNotification.Show("连接配置未完成", "请允许管理员权限后重试。", ToastNotification.ToastType.Warning);
            return;
        }

        _configManager.Update(config => config.LanTransfer.ConfiguredExecutablePath = Environment.ProcessPath);
        ToastNotification.Show("连接已修复", type: ToastNotification.ToastType.Success);
        await RestartServerAsync();
    }

    private void PermissionLater_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void AllowLanAccess_Click(object sender, RoutedEventArgs e)
    {
        permissionLaterButton.IsEnabled = false;
        try
        {
            await UiBusyState.RunWithBusyStateAsync(allowLanAccessButton, "配置中…", async () =>
            {
                var success = await LanTransferSetup.RunElevatedAsync(_configManager.Get().LanTransfer.Port);
                if (!success)
                {
                    ToastNotification.Show(
                        "局域网访问未启用",
                        "请允许 Windows 管理员授权后重试。",
                        ToastNotification.ToastType.Warning);
                    return;
                }

                _configManager.Update(config => config.LanTransfer.ConfiguredExecutablePath = Environment.ProcessPath);
                ToastNotification.Show("局域网访问已允许", type: ToastNotification.ToastType.Success);
                await RestartServerAsync();
            });
        }
        finally
        {
            permissionLaterButton.IsEnabled = true;
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null)
            return;
        System.Windows.Clipboard.SetText(_server.Address);
        ToastNotification.Show("地址已复制");
    }

    private void RefreshCode_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null)
            return;
        _server.RotateAccessCodes();
        UpdateConnectionDetails();
        ToastNotification.Show("连接码已刷新");
    }

    private void RevokeDevices_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = ConfirmDialog.Show(
            this,
            "移除已记住设备？",
            "之前选择“记住此设备”的手机将需要重新扫码或输入验证码。",
            "移除",
            "取消");
        if (!confirmed)
            return;

        _deviceTokens.RevokeAll();
        ToastNotification.Show("已移除记住的设备");
    }

    private async void BrowseReceiveDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_server?.ActiveTransferCount > 0)
        {
            ToastNotification.Show("正在传输文件", "传输完成后再更改接收目录。", ToastNotification.ToastType.Info);
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择手机文件的接收目录",
            SelectedPath = receiveDirectoryText.Text,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        _configManager.Update(config => config.LanTransfer.ReceiveDirectory = dialog.SelectedPath);
        receiveDirectoryText.Text = dialog.SelectedPath;
        await RestartServerAsync();
        ToastNotification.Show("接收目录已更新");
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要发送到手机的文件" };
        if (dialog.ShowDialog(this) == true)
            await AddSharedPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择要发送到手机的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            await AddSharedPathsAsync([dialog.SelectedPath]);
    }

    private async Task AddSharedPathsAsync(IEnumerable<string> paths)
    {
        if (_closing)
            return;

        var added = _sharedFiles.AddPaths(paths, allowDuplicatePaths: true).ToArray();
        if (added.Length == 0)
        {
            ToastNotification.Show("没有找到可发送的文件或文件夹");
            return;
        }

        if (_server?.IsRunning != true)
            await RestartServerAsync();

        var server = _server;
        if (server?.IsRunning != true)
        {
            foreach (var item in added)
                _sharedFiles.Remove(item.Id);
            ToastNotification.Show("传输服务尚未启动", type: ToastNotification.ToastType.Error);
            return;
        }

        var available = added.Where(item => item.IsAvailable).ToArray();
        if (available.Length != added.Length)
        {
            foreach (var item in added)
                _sharedFiles.Remove(item.Id);
            ToastNotification.Show("部分发送项目已被移动或删除", type: ToastNotification.ToastType.Error);
            return;
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
            foreach (var item in added)
                _sharedFiles.Remove(item.Id);
            ToastNotification.Show("无法创建发送任务", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    internal static OutgoingTransferMode ResolveOutgoingMode(IReadOnlyList<SharedFileEntry> entries) =>
        entries.Count >= 6 || entries.Count == 1 && entries[0].IsFolder
            ? OutgoingTransferMode.CombinedArchive
            : entries.Count == 1
                ? OutgoingTransferMode.Automatic
                : OutgoingTransferMode.Separate;

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _serverState == ServerLifecycleState.Running &&
                    e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_serverState == ServerLifecycleState.Running &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            await AddSharedPathsAsync(paths);
    }

    private void Server_FileReceived(ReceivedFileInfo info)
    {
        // Compatibility path for clients that do not create upload batches.
        var entry = new TransferHistoryEntry(
            info.Id,
            info.Name,
            TransferDirection.ToComputer,
            info.Size,
            1,
            info.FullPath,
            false,
            DateTimeOffset.UtcNow);
        Dispatcher.BeginInvoke(() => AddHistoryEntry(entry));
    }

    private void Server_BatchReceived(ReceivedBatchInfo info)
    {
        if (_closing)
            return;
        Dispatcher.BeginInvoke(() => ToastNotification.ShowWithAction(
            "接收完成",
            $"{info.Name} · {FormatBytes(info.Size)}",
            "打开位置",
            () => OpenHistoryLocation(info.FullPath, info.IsDirectory)));
    }

    private void Server_OutgoingItemCompleted(TransferHistoryEntry entry)
    {
        if (_closing)
            return;
        Dispatcher.BeginInvoke(() => AddHistoryEntry(entry));
    }

    private void Server_TransferChanged(TransferSessionSnapshot snapshot)
    {
        if (_closing)
            return;
        lock (_progressGate)
            _pendingProgress[snapshot.Id] = snapshot;
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        TransferSessionSnapshot[] updates;
        lock (_progressGate)
        {
            if (_pendingProgress.Count == 0)
                return;
            updates = _pendingProgress.Values.ToArray();
            _pendingProgress.Clear();
        }

        foreach (var snapshot in updates.OrderBy(item => item.StartedUtc))
        {
            if (_closing)
                return;
            if (snapshot.State == TransferState.Completed)
            {
                RemoveActiveRow(snapshot.Id);
                if (!_separateOutgoingIds.Remove(snapshot.Id))
                    AddCompletedHistory(snapshot);
                continue;
            }
            if (snapshot.State is TransferState.Canceled or TransferState.Rejected)
            {
                _separateOutgoingIds.Remove(snapshot.Id);
                RemoveActiveRow(snapshot.Id);
                continue;
            }
            var row = FindActiveRow(snapshot.Id);
            if (row == null)
            {
                row = new TransferRow(snapshot.Id, snapshot.Name);
                _activeRows.Insert(0, row);
                UpdateActiveTransferVisibility();
            }
            row.Apply(snapshot);
        }
    }

    private TransferRow? FindActiveRow(string id) =>
        _activeRows.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    private void RemoveActiveRow(string id)
    {
        var row = FindActiveRow(id);
        if (row != null)
            _activeRows.Remove(row);
        UpdateActiveTransferVisibility();
    }

    private void UpdateActiveTransferVisibility()
    {
        var hasTransfers = _activeRows.Count > 0;
        activeTransferSection.Visibility = hasTransfers ? Visibility.Visible : Visibility.Collapsed;
        shareEmptyText.Visibility = hasTransfers ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddCompletedHistory(TransferSessionSnapshot snapshot)
    {
        var path = snapshot.DestinationPath;
        if (string.IsNullOrWhiteSpace(path))
            path = snapshot.Direction == TransferDirection.ToComputer
                ? receiveDirectoryText.Text
                : snapshot.SourcePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            return;

        AddHistoryEntry(new TransferHistoryEntry(
            snapshot.Id,
            snapshot.Name,
            snapshot.Direction,
            snapshot.TotalBytes,
            snapshot.FileCount,
            path,
            Directory.Exists(path),
            snapshot.CompletedUtc ?? DateTimeOffset.UtcNow));
    }

    private void RemoveTransferRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: HistoryRow row })
            return;
        _historyStore.Remove(row.Id);
        _historyRows.Remove(row);
        UpdateTransferHistoryVisibility();
    }

    private void OpenTransferRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: HistoryRow row })
            OpenHistoryLocation(row.LocationPath, row.LocationIsDirectory);
    }

    private void ClearTransferHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRows.Count == 0)
        {
            ToastNotification.Show("暂无可清除的已完成记录");
            return;
        }
        _historyStore.Clear();
        _historyRows.Clear();
        UpdateTransferHistoryVisibility();
    }

    private void UpdateTransferHistoryVisibility()
    {
        clearHistoryButton.Visibility = _historyRows.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void LoadTransferHistory()
    {
        _historyRows.Clear();
        foreach (var entry in _historyStore.Snapshot())
            _historyRows.Add(new HistoryRow(entry));
        UpdateTransferHistoryVisibility();
    }

    private void AddHistoryEntry(TransferHistoryEntry entry)
    {
        _historyStore.Add(entry);
        var existing = _historyRows.FirstOrDefault(item => item.Id == entry.Id);
        if (existing != null)
            _historyRows.Remove(existing);
        _historyRows.Insert(0, new HistoryRow(entry));
        while (_historyRows.Count > 100)
            _historyRows.RemoveAt(_historyRows.Count - 1);
        UpdateTransferHistoryVisibility();
    }

    private void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: TransferRow row })
            _server?.PauseTransfer(row.Id);
    }

    private void ResumeTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: TransferRow row })
            _server?.ResumeTransfer(row.Id);
    }

    private void RetryTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: TransferRow row } &&
            _server?.RetryTransfer(row.Id) != true)
            ToastNotification.Show("无法重试该任务");
    }

    private async void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: TransferRow row })
            return;
        if (row.State == TransferState.Failed)
        {
            if (_server != null)
                await _server.RemoveTransferAsync(row.Id);
            _separateOutgoingIds.Remove(row.Id);
            RemoveActiveRow(row.Id);
            return;
        }
        await (_server?.CancelTransferAsync(row.Id) ?? Task.FromResult(false));
    }

    private static void OpenHistoryLocation(string path, bool isDirectory)
    {
        if (isDirectory && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        if (!isDirectory && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }
        ToastNotification.Show("文件已被移动或删除");
    }

    private static BitmapSource CreateQrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        // The card provides the QR quiet zone so the visible code can align with the field grid.
        var bytes = qrCode.GetGraphic(7, drawQuietZones: false);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string GetDefaultReceiveDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "STool");
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, value);
        var index = 0;
        var display = (double)size;
        while (display >= 1024 && index < units.Length - 1)
        {
            display /= 1024;
            index++;
        }
        return $"{display:0.#} {units[index]}";
    }

    private static string FormatSpeed(double value) =>
        $"{FormatBytes((long)Math.Max(0, value))}/s";

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing && _server?.ActiveTransferCount > 0)
        {
            var close = ConfirmDialog.Show(
                this,
                "停止正在进行的传输？",
                "关闭面板会断开手机连接，并删除尚未完成的临时文件。",
                "停止并关闭",
                "继续传输");
            if (!close)
            {
                e.Cancel = true;
                return;
            }
        }

        if (!_cleanupCompleted)
        {
            e.Cancel = true;
            _closing = true;
            _ = CompleteCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await _serverLifecycle.RunFinalAsync(async () =>
            {
                SetServerState(ServerLifecycleState.Stopping);
                await StopServerCoreAsync(updateState: false);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LAN transfer cleanup failed during close");
        }
        finally
        {
            _cleanupCompleted = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimer_Tick;
        _connectionTimer.Stop();
        _connectionTimer.Tick -= ConnectionTimer_Tick;
        lock (_progressGate)
            _pendingProgress.Clear();
        Loaded -= LanTransferWindow_Loaded;
        _serverLifecycle.Dispose();
        qrImage.Source = null;
        _sharedFiles.Clear();
        _activeRows.Clear();
        _historyRows.Clear();
        _separateOutgoingIds.Clear();
        MemoryDiagnostics.LogCheckpoint("LanTransferClosed");
        base.OnClosed(e);
    }

    private async Task StopServerCoreAsync(bool updateState)
    {
        var server = _server;
        _server = null;
        if (server == null)
        {
            if (updateState)
                SetServerState(ServerLifecycleState.Stopped);
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
                SetServerState(ServerLifecycleState.Stopped);
        }
    }

    private sealed class TransferRow : INotifyPropertyChanged
    {
        private double _progress;
        private string _status = string.Empty;
        private string _details = string.Empty;
        private bool _canPause;
        private bool _canResume;
        private bool _canRetry;
        private bool _canCancel = true;
        private TransferState _state;

        public TransferRow(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string Details { get => _details; set { _details = value; OnPropertyChanged(); } }
        public bool CanPause { get => _canPause; set { _canPause = value; OnPropertyChanged(); } }
        public bool CanResume { get => _canResume; set { _canResume = value; OnPropertyChanged(); } }
        public bool CanRetry { get => _canRetry; set { _canRetry = value; OnPropertyChanged(); } }
        public bool CanCancel { get => _canCancel; set { _canCancel = value; OnPropertyChanged(); } }
        public TransferState State { get => _state; set { _state = value; OnPropertyChanged(); } }

        public void Apply(TransferSessionSnapshot snapshot)
        {
            State = snapshot.State;
            Progress = snapshot.TotalBytes <= 0
                ? 0
                : Math.Clamp(snapshot.TransferredBytes * 100d / snapshot.TotalBytes, 0, 100);
            Status = snapshot.State switch
            {
                TransferState.Preparing => "准备中",
                TransferState.AwaitingConfirmation => "等待手机确认",
                TransferState.Transferring => snapshot.Direction == TransferDirection.ToPhone ? "发送中" : "接收中",
                TransferState.Paused => "已暂停",
                TransferState.WaitingForResume => "等待继续",
                TransferState.Failed => "失败",
                _ => string.Empty
            };
            Details = snapshot.State == TransferState.Failed
                ? snapshot.Error ?? "传输失败"
                : $"{FormatBytes(snapshot.TransferredBytes)} / {FormatBytes(snapshot.TotalBytes)}" +
                  (snapshot.State == TransferState.Transferring && snapshot.BytesPerSecond > 0
                      ? $" · {FormatSpeed(snapshot.BytesPerSecond)}"
                      : string.Empty);
            CanPause = snapshot.State is TransferState.Preparing or TransferState.Transferring;
            CanResume = snapshot.State == TransferState.Paused;
            CanRetry = snapshot.State == TransferState.Failed;
            CanCancel = snapshot.State is not TransferState.Completed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class HistoryRow
    {
        public HistoryRow(TransferHistoryEntry entry)
        {
            Id = entry.Id;
            Name = entry.Name;
            LocationPath = entry.LocationPath;
            LocationIsDirectory = entry.LocationIsDirectory;
            var status = entry.Direction == TransferDirection.ToPhone ? "已发送" : "已接收";
            Details = $"{status} · {FormatBytes(entry.TotalBytes)} · {entry.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }

        public string Id { get; }
        public string Name { get; }
        public string Details { get; }
        public string LocationPath { get; }
        public bool LocationIsDirectory { get; }
    }
}
