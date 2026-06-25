using System;
using System.Windows;
using STool.Core;

namespace STool.Modules.Ocr;

public partial class OcrResultWindow : Window
{
    public OcrResultWindow(string text, string provider)
    {
        InitializeComponent();
        txtResult.Text = text;
        txtProvider.Text = $"提供商: {provider}";
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(txtResult.Text);
            ToastNotification.Show("已复制到剪贴板", type: ToastNotification.ToastType.Success);
            Close();
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
