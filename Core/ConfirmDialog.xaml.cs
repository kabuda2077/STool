using System.Windows;

namespace STool.Core;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        titleText.Text = title;
        messageText.Text = message;
        confirmButton.Content = confirmText;
        cancelButton.Content = cancelText;
    }

    public static bool Show(Window owner, string title, string message, string confirmText = "确认", string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
