using System;
using System.Threading.Tasks;
using WpfButton = System.Windows.Controls.Button;

namespace STool.Core;

internal static class UiBusyState
{
    public static async Task RunWithBusyStateAsync(WpfButton button, string busyText, Func<Task> action)
    {
        var originalContent = button.Content;
        var originalEnabled = button.IsEnabled;

        try
        {
            button.IsEnabled = false;
            button.Content = busyText;
            await action();
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = originalEnabled;
        }
    }
}
