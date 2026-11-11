using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using STool.Core;
using Forms = System.Windows.Forms;

namespace STool.Modules.LanTransfer;

/// <summary>局域网传输面板：只负责绑定与渲染，服务与业务逻辑见 <see cref="LanTransferController"/>。</summary>
public partial class LanTransferWindow : Window
{
    private readonly LanTransferController _controller;
    private readonly ObservableCollection<TransferRow> _activeRows = [];
    private readonly ObservableCollection<HistoryRow> _historyRows = [];
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _connectionTimer;
    private string? _renderedQrAddress;
    private bool _closeStarted;
    private bool _cleanupCompleted;
    private bool _showingHistory;

    internal LanTransferWindow(LanTransferController controller)
    {
        InitializeComponent();
        _controller = controller;
        _controller.Changed += Render;
        _controller.BatchReceived += Controller_BatchReceived;
        _controller.HistoryAdded += AddHistoryRow;
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
        receiveDirectoryText.Text = _controller.EnsureReceiveDirectory();
        LoadTransferHistory();
        UpdateTransferTabUi(false);
        await _controller.RestartAsync();
    }

    /// <summary>按控制器的当前状态刷新状态栏、按钮可用性、地址与二维码。</summary>
    private void Render()
    {
        var state = _controller.State;
        var permissionRequired = state == ServerLifecycleState.PermissionRequired;
        permissionSetupPanel.Visibility = permissionRequired ? Visibility.Visible : Visibility.Collapsed;
        connectionDetailsPanel.Visibility = permissionRequired ? Visibility.Collapsed : Visibility.Visible;
        repairButton.Visibility = _controller.RepairVisible ? Visibility.Visible : Visibility.Collapsed;
        if (permissionRequired)
        {
            qrImage.Source = null;
            _renderedQrAddress = null;
        }
        else
        {
            RenderConnectionDetails();
        }

        var status = _controller.Status;
        SetStatus(StatusText(status), status == LanStatusKind.PhoneConnected);

        var transitioning = state is ServerLifecycleState.Starting or ServerLifecycleState.Stopping;
        var running = state == ServerLifecycleState.Running;
        browseReceiveDirectoryButton.IsEnabled = !transitioning;
        repairButton.IsEnabled = !transitioning;
        copyAddressButton.IsEnabled = running;
        refreshCodeButton.IsEnabled = running;
        revokeDevicesButton.IsEnabled = running;
        addFilesButton.IsEnabled = running;
        addFolderButton.IsEnabled = running;
        allowLanAccessButton.IsEnabled = permissionRequired;
        permissionLaterButton.IsEnabled = permissionRequired;
        AllowDrop = running;
    }

    private void RenderConnectionDetails()
    {
        switch (_controller.AddressNotice)
        {
            case LanAddressNotice.NoNetwork:
                addressText.Text = "请先连接 Wi-Fi 或有线局域网";
                return;
            case LanAddressNotice.Error:
                addressText.Text = _controller.ErrorMessage ?? string.Empty;
                return;
        }

        var address = _controller.Address;
        var qrAddress = _controller.QrAddress;
        if (address == null || qrAddress == null)
            return;

        addressText.Text = address;
        codeText.Text = _controller.ManualCode;
        if (qrAddress != _renderedQrAddress)
        {
            _renderedQrAddress = qrAddress;
            qrImage.Source = CreateQrImage(qrAddress);
        }
    }

    private static string StatusText(LanStatusKind status) => status switch
    {
        LanStatusKind.Stopped => "服务已停止",
        LanStatusKind.Starting => "正在启动服务",
        LanStatusKind.WaitingForPermission => "等待配置局域网权限",
        LanStatusKind.WaitingForPhone => "等待手机连接",
        LanStatusKind.PhoneConnected => "手机已连接",
        LanStatusKind.Stopping => "正在停止服务",
        LanStatusKind.StartFailed => "服务启动失败",
        LanStatusKind.NoNetwork => "未找到可用的局域网",
        LanStatusKind.FirewallRepairSuggested => "服务已启动，建议修复防火墙",
        _ => "等待手机连接"
    };

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

    private void ConnectionTimer_Tick(object? sender, EventArgs e) => _controller.CheckClientPresence();

    private async void RepairConnection_Click(object sender, RoutedEventArgs e)
    {
        repairButton.IsEnabled = false;
        repairButton.Content = "配置中...";
        var success = await _controller.ConfigureAccessAsync();
        repairButton.IsEnabled = true;
        repairButton.Content = "修复连接";
        if (!success)
        {
            ToastNotification.Show("连接配置未完成", "请允许管理员权限后重试。", ToastNotification.ToastType.Warning);
            return;
        }

        ToastNotification.Show("连接已修复", type: ToastNotification.ToastType.Success);
        await _controller.RestartAsync();
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
                var success = await _controller.ConfigureAccessAsync();
                if (!success)
                {
                    ToastNotification.Show(
                        "局域网访问未启用",
                        "请允许 Windows 管理员授权后重试。",
                        ToastNotification.ToastType.Warning);
                    return;
                }

                ToastNotification.Show("局域网访问已允许", type: ToastNotification.ToastType.Success);
                await _controller.RestartAsync();
            });
        }
        finally
        {
            permissionLaterButton.IsEnabled = true;
        }
    }

    private async void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = _controller.Address;
        if (address == null)
            return;

        try
        {
            await ClipboardWriter.SetTextAsync(address);
            ToastNotification.Show("地址已复制");
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void RefreshCode_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.RotateAccessCodes())
            return;
        // 换码后控制器会通知刷新二维码与验证码。
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

        // 同时让当前在线会话失效；服务未运行时只清理记住的设备。
        _controller.RevokeRememberedDevices();
        ToastNotification.Show("已移除记住的设备");
    }

    private async void BrowseReceiveDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.ActiveTransferCount > 0)
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

        _controller.SetReceiveDirectory(dialog.SelectedPath);
        receiveDirectoryText.Text = dialog.SelectedPath;
        await _controller.RestartAsync();
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
        var outcome = await _controller.AddSharedPathsAsync(paths);
        switch (outcome.Result)
        {
            case AddSharedResult.ScanFailed:
                ToastNotification.Show("无法读取所选文件或文件夹", outcome.Error ?? string.Empty, ToastNotification.ToastType.Error);
                break;
            case AddSharedResult.NothingFound:
                ToastNotification.Show("没有找到可发送的文件或文件夹");
                break;
            case AddSharedResult.ServerNotRunning:
                ToastNotification.Show("传输服务尚未启动", type: ToastNotification.ToastType.Error);
                break;
            case AddSharedResult.ItemsUnavailable:
                ToastNotification.Show("部分发送项目已被移动或删除", type: ToastNotification.ToastType.Error);
                break;
            case AddSharedResult.QueueFailed:
                ToastNotification.Show("无法创建发送任务", outcome.Error ?? string.Empty, ToastNotification.ToastType.Error);
                break;
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _controller.State == ServerLifecycleState.Running &&
                    e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_controller.State == ServerLifecycleState.Running &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            await AddSharedPathsAsync(paths);
    }

    private void Controller_BatchReceived(ReceivedBatchInfo info) =>
        ToastNotification.ShowWithAction(
            "接收完成",
            $"{info.Name} · {FormatBytes(info.Size)}",
            "打开位置",
            () => OpenHistoryLocation(info.FullPath, info.IsDirectory),
            placement: ToastNotification.ToastPlacement.BottomRight);

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var update in _controller.DrainTransferUpdates())
        {
            if (update.Kind == TransferUpdateKind.Finished)
            {
                RemoveActiveRow(update.Snapshot.Id);
                continue;
            }

            var row = FindActiveRow(update.Snapshot.Id);
            if (row == null)
            {
                row = new TransferRow(update.Snapshot.Id, update.Snapshot.Name);
                _activeRows.Insert(0, row);
                UpdateActiveTransferVisibility();
            }
            row.Apply(update.Snapshot);
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

    private void RemoveTransferRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: HistoryRow row })
            return;
        _controller.RemoveHistory(row.Id);
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
        _controller.ClearHistory();
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
        foreach (var entry in _controller.LoadHistory())
            _historyRows.Add(new HistoryRow(entry));
        UpdateTransferHistoryVisibility();
    }

    /// <summary>历史记录已由控制器写入存储，这里只更新界面列表。</summary>
    private void AddHistoryRow(TransferHistoryEntry entry)
    {
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
            _controller.PauseTransfer(row.Id);
    }

    private void ResumeTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: TransferRow row })
            _controller.ResumeTransfer(row.Id);
    }

    private void RetryTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: TransferRow row } &&
            !_controller.RetryTransfer(row.Id))
            ToastNotification.Show("无法重试该任务");
    }

    private async void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { CommandParameter: TransferRow row })
            return;
        if (row.State == TransferState.Failed)
        {
            await _controller.RemoveFailedTransferAsync(row.Id);
            RemoveActiveRow(row.Id);
            return;
        }
        await _controller.CancelTransferAsync(row.Id);
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
        if (!_closeStarted && _controller.ActiveTransferCount > 0)
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
            if (!_closeStarted)
            {
                _closeStarted = true;
                _ = CompleteCloseAsync();
            }
            return;
        }

        base.OnClosing(e);
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await _controller.ShutdownAsync();
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
        Loaded -= LanTransferWindow_Loaded;
        _controller.Changed -= Render;
        _controller.BatchReceived -= Controller_BatchReceived;
        _controller.HistoryAdded -= AddHistoryRow;
        _controller.Dispose();
        qrImage.Source = null;
        _activeRows.Clear();
        _historyRows.Clear();
        MemoryDiagnostics.LogCheckpoint("LanTransferClosed");
        base.OnClosed(e);
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
