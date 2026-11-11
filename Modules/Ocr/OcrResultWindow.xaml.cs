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
        txtProvider.Text = provider switch
        {
            "Windows Local" => "Windows 本地识别",
            "Tencent Cloud" => "腾讯云识别",
            "AI Vision" => "AI 图片识别",
            _ => provider
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                BtnCopy_Click(btnCopy, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardWriter.SetTextAsync(txtResult.Text);
            ToastNotification.Show("已复制");
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

    protected override void OnClosed(EventArgs e)
    {
        txtResult.Clear();
        txtProvider.Text = string.Empty;
        MemoryDiagnostics.LogCheckpoint("OcrResultClosed");
        base.OnClosed(e);
    }
}
