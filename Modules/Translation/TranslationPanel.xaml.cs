using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using STool.Models;

namespace STool.Modules.Translation;

public partial class TranslationPanel : Window
{
    private readonly TranslationManager _translationManager;
    private TranslationProvider _provider = TranslationProvider.Google;
    private bool _busy;

    public TranslationPanel(TranslationManager translationManager)
    {
        InitializeComponent();
        _translationManager = translationManager;
        UpdateProviderButtons();

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

    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        _provider = ReferenceEquals(sender, btnProviderTencent)
            ? TranslationProvider.Tencent
            : TranslationProvider.Google;
        UpdateProviderButtons();
    }

    private void UpdateProviderButtons()
    {
        btnProviderGoogle.Tag = _provider == TranslationProvider.Google ? "on" : null;
        btnProviderTencent.Tag = _provider == TranslationProvider.Tencent ? "on" : null;
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        srcWatermark.Visibility = string.IsNullOrEmpty(txtSource.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtTarget_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtTarget.Text);
        tgtWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        btnCopy.Visibility = (hasText && !_busy) ? Visibility.Visible : Visibility.Collapsed;
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

            var sourceLang = (cmbSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            var targetLang = (cmbTarget.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";

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
            var hasText = !string.IsNullOrEmpty(txtTarget.Text);
            btnCopy.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            tgtWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(txtTarget.Text))
            return;
        try
        {
            System.Windows.Clipboard.SetText(txtTarget.Text);
        }
        catch
        {
            // 忽略偶发的剪贴板占用异常
        }
    }
}
