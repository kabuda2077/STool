using System;
using System.Windows;
using STool.Core;

namespace STool.Modules.Translation;

public partial class InPlaceTranslationWindow : Window
{
    public InPlaceTranslationWindow(string originalText, string translatedText, string provider)
    {
        InitializeComponent();
        txtOriginal.Text = originalText;
        txtTranslated.Text = translatedText;
        txtProvider.Text = $"提供商: {provider}";
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(txtTranslated.Text);
            ToastNotification.Show("已复制到剪贴板", type: ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
