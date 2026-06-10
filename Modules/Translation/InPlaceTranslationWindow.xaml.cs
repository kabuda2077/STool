using System;
using System.Windows;
using System.Windows.Input;
using STool.Core;

namespace STool.Modules.Translation;

public partial class InPlaceTranslationWindow : Window
{
    public InPlaceTranslationWindow(string originalText, string translatedText, string provider)
        : this(originalText, translatedText, provider, null)
    {
    }

    public InPlaceTranslationWindow(string originalText, string translatedText, string provider, Rect? targetDip)
    {
        InitializeComponent();
        txtTranslated.Text = translatedText;
        txtProvider.Text = provider;

        if (targetDip is Rect target && target.Width >= 1 && target.Height >= 1)
        {
            PositionAtTarget(target);
        }
        else
        {
            Width = 420;
            Height = 220;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }
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

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void PositionAtTarget(Rect target)
    {
        Width = Clamp(target.Width, 280, 560);
        Height = Clamp(target.Height, 120, 380);

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        Left = Clamp(target.Left, virtualLeft + 8, virtualRight - Width - 8);
        Top = Clamp(target.Top, virtualTop + 8, virtualBottom - Height - 8);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
            return min;
        return value < min ? min : (value > max ? max : value);
    }
}
