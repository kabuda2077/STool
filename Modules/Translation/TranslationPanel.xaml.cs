using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using STool.Models;

namespace STool.Modules.Translation;

public partial class TranslationPanel : Window
{
    private readonly TranslationManager _translationManager;
    private TranslationProvider _provider = TranslationProvider.Google;

    public TranslationPanel(TranslationManager translationManager)
    {
        InitializeComponent();
        _translationManager = translationManager;
        UpdateProviderButtons();

        // Ctrl+Enter 触发翻译
        txtSource.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (btnTranslate.IsEnabled)
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
        var hasText = !string.IsNullOrWhiteSpace(txtSource.Text);
        btnTranslate.IsEnabled = hasText;
        srcWatermark.Visibility = string.IsNullOrEmpty(txtSource.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtTarget_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(txtTarget.Text);
        tgtWatermark.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        btnCopy.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnTranslate_Click(object sender, RoutedEventArgs e) => _ = TranslateAsync();

    private async System.Threading.Tasks.Task TranslateAsync()
    {
        var sourceText = txtSource.Text.Trim();
        if (string.IsNullOrEmpty(sourceText))
            return;

        try
        {
            btnTranslate.IsEnabled = false;
            loadingSpinner.Visibility = Visibility.Visible;
            translateIcon.Visibility = Visibility.Collapsed;

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
            btnTranslate.IsEnabled = !string.IsNullOrWhiteSpace(txtSource.Text);
            loadingSpinner.Visibility = Visibility.Collapsed;
            translateIcon.Visibility = Visibility.Visible;
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
