using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using STool.Models;

namespace STool.Modules.Translation;

public partial class TranslationPanel : Window
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly TranslationManager _translationManager;
    private TranslationProvider _provider = TranslationProvider.Google;
    private bool _busy;
    private bool _initializingLanguages = true;
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
        {
            _ = TranslateAsync();
        }
    }

    private void UpdateProviderButtons()
    {
        btnProviderGoogle.Tag = _provider == TranslationProvider.Google ? "on" : null;
        btnProviderTencent.Tag = _provider == TranslationProvider.Tencent ? "on" : null;
        btnProviderAi.Tag = _provider == TranslationProvider.OpenAI ? "on" : null;
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
        {
            _ = TranslateAsync();
        }
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtSource.Text);
        srcWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        sourceActions.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtTarget_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtTarget.Text);
        tgtWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task TranslateAsync()
    {
        var sourceText = txtSource.Text.Trim();
        if (string.IsNullOrEmpty(sourceText) || _busy)
            return;

        try
        {
            _busy = true;
            txtTarget.Text = "翻译中…";

            var sourceLang = "auto";
            var targetLang = TranslationManager.ResolveTargetLanguage(sourceText, GetTranslationMode());

            var result = await _translationManager.TranslateAsync(sourceText, sourceLang, targetLang, _provider);

            // 结果与错误都直接显示在结果区,翻译完成不再弹出右下角提示
            txtTarget.Text = result.Success
                ? result.TranslatedText
                : $"翻译失败：{result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            txtTarget.Text = $"翻译失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            tgtWatermark.Visibility = string.IsNullOrEmpty(txtTarget.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string GetTranslationMode()
    {
        return (cmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-en";
    }

    /// <summary>复制译文到剪贴板;无结果或翻译中返回 false。</summary>
    private bool CopyText()
    {
        if (_busy || string.IsNullOrEmpty(txtTarget.Text))
            return false;
        try
        {
            System.Windows.Clipboard.SetText(txtTarget.Text);
            return true;
        }
        catch
        {
            return false;   // 忽略偶发的剪贴板占用异常
        }
    }

    private void BtnCopyOnly_Click(object sender, RoutedEventArgs e) => CopyText();

    private void BtnTranslateSource_Click(object sender, RoutedEventArgs e) => _ = TranslateAsync();

    private void BtnClearSource_Click(object sender, RoutedEventArgs e)
    {
        txtSource.Clear();
        txtTarget.Clear();
        txtSource.Focus();
    }

    private void BtnCopyHide_Click(object sender, RoutedEventArgs e)
    {
        CopyText();
        Close();
    }

    private void BtnCopyInput_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyText())
            return;

        var hwnd = _targetHwnd;
        Hide();
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);

        // 延迟少许确保焦点已切回原应用,再发送粘贴
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { System.Windows.Forms.SendKeys.SendWait("^v"); } catch { }
            Close();
        };
        timer.Start();
    }
}
