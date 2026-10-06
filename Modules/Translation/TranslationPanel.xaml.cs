using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using STool.Core;
using STool.Models;

namespace STool.Modules.Translation;

public partial class TranslationPanel : Window
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly TranslationManager _translationManager;
    private TranslationProvider _provider = TranslationProvider.Google;
    private bool _busy;
    private bool _closing;
    private bool _initializingLanguages = true;
    private CancellationTokenSource? _translationCts;
    private long _translationVersion;
    private readonly IntPtr _targetHwnd;   // 打开面板前的前台窗口("复制并输入"时切回它粘贴)

    public TranslationPanel(TranslationManager translationManager)
    {
        _targetHwnd = GetForegroundWindow();   // 在 Show() 之前抓取 = 用户原来的应用
        _translationManager = translationManager;
        InitializeComponent();
        _provider = _translationManager.GetConfiguredProvider();
        LoadTranslationMode();
        UpdateProviderButtons();
        Loaded += TranslationPanel_Loaded;
        providerSegmentGrid.SizeChanged += (_, _) => UpdateProviderSlider();

        // 回车翻译;Shift+Enter 换行
        txtSource.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = TranslateAsync();
            }
        };
    }

    private void TranslationPanel_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateProviderSlider();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            txtSource.Focus();
            Keyboard.Focus(txtSource);
        }), DispatcherPriority.ApplicationIdle);
    }

    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        _provider = sender switch
        {
            var button when ReferenceEquals(button, btnProviderTencent) => TranslationProvider.Tencent,
            var button when ReferenceEquals(button, btnProviderAi) => TranslationProvider.OpenAI,
            _ => TranslationProvider.Google
        };
        _translationManager.SaveConfiguredProvider(_provider);
        UpdateProviderButtons();

        if (!string.IsNullOrWhiteSpace(txtSource.Text))
            _ = TranslateAsync();
    }

    private void UpdateProviderButtons()
    {
        btnProviderGoogle.Tag = _provider == TranslationProvider.Google ? "on" : null;
        btnProviderTencent.Tag = _provider == TranslationProvider.Tencent ? "on" : null;
        btnProviderAi.Tag = _provider == TranslationProvider.OpenAI ? "on" : null;
        UpdateProviderSlider();
    }

    private void UpdateProviderSlider()
    {
        var selectedButton = _provider switch
        {
            TranslationProvider.Tencent => btnProviderTencent,
            TranslationProvider.OpenAI => btnProviderAi,
            _ => btnProviderGoogle
        };
        var target = selectedButton.TranslatePoint(new System.Windows.Point(0, 0), providerSegmentGrid).X;
        SegmentedSliderMotion.MoveTo(
            providerSlider,
            providerSliderTransform,
            providerSliderScale,
            target,
            selectedButton.ActualWidth,
            IsLoaded);
    }

    private void LoadTranslationMode()
    {
        _initializingLanguages = true;
        SelectTranslationMode(_translationManager.GetConfiguredTranslationMode());
        _initializingLanguages = false;
    }

    private void SelectTranslationMode(string mode)
    {
        foreach (ComboBoxItem item in cmbMode.Items)
        {
            if ((item.Tag?.ToString() ?? string.Empty).Equals(mode, StringComparison.OrdinalIgnoreCase))
            {
                cmbMode.SelectedItem = item;
                return;
            }
        }

        cmbMode.SelectedIndex = 0;
    }

    private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingLanguages)
            return;

        _translationManager.SaveConfiguredTranslationMode(GetTranslationMode());

        if (!string.IsNullOrWhiteSpace(txtSource.Text))
            _ = TranslateAsync();
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtSource.Text);
        srcWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        sourceActions.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (_busy)
            CancelCurrentTranslation();
    }

    private void TxtTarget_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtTarget.Text);
        tgtWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task TranslateAsync()
    {
        var sourceText = txtSource.Text.Trim();
        if (string.IsNullOrEmpty(sourceText) || _closing)
            return;

        _translationCts?.Cancel();
        var translationCts = new CancellationTokenSource();
        _translationCts = translationCts;
        var requestVersion = Interlocked.Increment(ref _translationVersion);
        var sourceSnapshot = sourceText;

        try
        {
            _busy = true;
            btnTranslateSource.ToolTip = "取消翻译";
            txtTarget.Text = string.Empty;
            loadingIndicator.Visibility = Visibility.Visible;

            var result = await _translationManager.TranslateAsync(
                sourceText,
                "auto",
                TranslationManager.ResolveTargetLanguage(sourceText, GetTranslationMode()),
                _provider,
                translationCts.Token);

            if (IsCurrentRequest(translationCts, requestVersion, sourceSnapshot))
            {
                txtTarget.Text = result.Success
                    ? result.TranslatedText
                    : $"翻译失败：{result.ErrorMessage}";
            }
        }
        catch (OperationCanceledException) when (translationCts.IsCancellationRequested || _closing)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRequest(translationCts, requestVersion, sourceSnapshot))
                txtTarget.Text = $"翻译失败：{NetworkErrorMessages.FromException(ex, translationCts.Token)}";
        }
        finally
        {
            var isCurrent = ReferenceEquals(_translationCts, translationCts);
            if (isCurrent)
                _translationCts = null;

            translationCts.Dispose();
            if (isCurrent && !_closing)
                ResetTranslationUi();
        }
    }

    private bool IsCurrentRequest(CancellationTokenSource request, long version, string sourceText) =>
        !_closing &&
        !request.IsCancellationRequested &&
        ReferenceEquals(_translationCts, request) &&
        version == Interlocked.Read(ref _translationVersion) &&
        string.Equals(txtSource.Text.Trim(), sourceText, StringComparison.Ordinal);

    private void CancelCurrentTranslation()
    {
        Interlocked.Increment(ref _translationVersion);
        var request = _translationCts;
        _translationCts = null;
        request?.Cancel();
        ResetTranslationUi();
    }

    private void ResetTranslationUi()
    {
        _busy = false;
        btnTranslateSource.ToolTip = "翻译";
        loadingIndicator.Visibility = Visibility.Collapsed;
        tgtWatermark.Visibility = string.IsNullOrEmpty(txtTarget.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private string GetTranslationMode()
    {
        return (cmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-en";
    }

    /// <summary>复制译文到剪贴板;无结果或翻译中返回 false。</summary>
    private async Task<bool> CopyTextAsync()
    {
        if (_busy || string.IsNullOrEmpty(txtTarget.Text))
            return false;
        try
        {
            await ClipboardWriter.SetTextAsync(txtTarget.Text);
            return true;
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
            return false;
        }
    }

    private async void BtnCopyOnly_Click(object sender, RoutedEventArgs e) => await CopyTextAsync();

    private void BtnTranslateSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CancelCurrentTranslation();
            return;
        }

        _ = TranslateAsync();
    }

    private void BtnClearSource_Click(object sender, RoutedEventArgs e)
    {
        txtSource.Clear();
        txtTarget.Clear();
        txtSource.Focus();
    }

    private async void BtnCopyHide_Click(object sender, RoutedEventArgs e)
    {
        if (await CopyTextAsync())
            Close();
    }

    private async void BtnCopyInput_Click(object sender, RoutedEventArgs e)
    {
        if (!await CopyTextAsync())
            return;

        var hwnd = _targetHwnd;
        Hide();
        try
        {
            if (!await ForegroundPaste.PasteToAsync(hwnd, focusDelayMs: 120))
                ToastNotification.Show("内容已复制", "无法切回原窗口，请手动粘贴。", ToastNotification.ToastType.Info);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("内容已复制", $"自动输入失败：{ex.Message}", ToastNotification.ToastType.Info);
        }
        finally
        {
            Close();
        }
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Enter: 翻译
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = TranslateAsync();
        }
        // Ctrl+Shift+C: 复制结果并隐藏
        else if (e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            if (await CopyTextAsync())
                Close();
        }
        // Ctrl+L: 循环切换语言/翻译模式
        else if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            CycleLanguageMode();
        }
        // Escape: 关闭
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CycleLanguageMode()
    {
        if (cmbMode.Items.Count > 0)
        {
            int nextIndex = (cmbMode.SelectedIndex + 1) % cmbMode.Items.Count;
            cmbMode.SelectedIndex = nextIndex;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        Interlocked.Increment(ref _translationVersion);
        _translationCts?.Cancel();
        _translationCts = null;
        loadingIndicator.Visibility = Visibility.Collapsed;
        txtSource.Clear();
        txtTarget.Clear();
        MemoryDiagnostics.LogCheckpoint("TranslationPanelClosed");
        base.OnClosed(e);
    }
}
