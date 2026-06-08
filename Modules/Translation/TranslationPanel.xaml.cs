using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;

namespace STool.Modules.Translation;

public partial class TranslationPanel : Window
{
    private readonly TranslationManager _translationManager;

    public TranslationPanel(TranslationManager translationManager)
    {
        InitializeComponent();
        _translationManager = translationManager;
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        btnTranslate.IsEnabled = !string.IsNullOrWhiteSpace(txtSource.Text);
    }

    private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
        var sourceText = txtSource.Text.Trim();
        if (string.IsNullOrEmpty(sourceText))
            return;

        try
        {
            // 显示加载状态
            btnTranslate.IsEnabled = false;
            loadingSpinner.Visibility = Visibility.Visible;
            translateIcon.Visibility = Visibility.Collapsed;

            // 获取选中的语言
            var sourceLang = (cmbSourceLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            var targetLang = (cmbTargetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh";

            // 执行翻译
            var result = await _translationManager.TranslateAsync(sourceText, sourceLang, targetLang);

            if (result.Success)
            {
                txtTarget.Text = result.TranslatedText;
                txtProvider.Text = $"提供商: {result.Provider}";
                btnCopy.IsEnabled = true;

                // 显示成功提示
                ToastNotification.Show("翻译完成", type: ToastNotification.ToastType.Success);
            }
            else
            {
                txtTarget.Text = $"翻译失败: {result.ErrorMessage}";
                txtProvider.Text = "";
                btnCopy.IsEnabled = false;

                // 显示错误提示
                ToastNotification.Show("翻译失败", result.ErrorMessage ?? "未知错误", ToastNotification.ToastType.Error);
            }
        }
        catch (Exception ex)
        {
            ToastNotification.Show("翻译失败", ex.Message, ToastNotification.ToastType.Error);
        }
        finally
        {
            // 恢复按钮状态
            btnTranslate.IsEnabled = true;
            loadingSpinner.Visibility = Visibility.Collapsed;
            translateIcon.Visibility = Visibility.Visible;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(txtTarget.Text);
            ToastNotification.Show("已复制到剪贴板", type: ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        txtSource.Clear();
        txtTarget.Clear();
        txtProvider.Text = "";
        btnCopy.IsEnabled = false;
    }

    private void BtnSwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        // 不能交换"自动检测"
        if (cmbSourceLanguage.SelectedIndex == 0) // auto
        {
            ToastNotification.Show("无法交换", "源语言为自动检测时无法交换", ToastNotification.ToastType.Warning);
            return;
        }

        // 交换语言选择
        var sourceIndex = cmbSourceLanguage.SelectedIndex;
        var targetIndex = cmbTargetLanguage.SelectedIndex;

        // 源语言ComboBox包含"自动检测"，所以索引需要调整
        // 目标语言从索引0开始（中文=0, 英文=1, 日文=2, 韩文=3）
        // 源语言从索引1开始对应实际语言（中文=1, 英文=2, 日文=3, 韩文=4）

        cmbTargetLanguage.SelectedIndex = sourceIndex - 1; // 源语言索引-1对应目标语言
        cmbSourceLanguage.SelectedIndex = targetIndex + 1; // 目标语言索引+1对应源语言

        // 交换文本内容
        var temp = txtSource.Text;
        txtSource.Text = txtTarget.Text;
        txtTarget.Text = temp;

        ToastNotification.Show("已交换语言", type: ToastNotification.ToastType.Info);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
