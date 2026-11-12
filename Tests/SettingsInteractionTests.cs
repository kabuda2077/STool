using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using STool.Views.Settings;
using Xunit;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace STool.Tests;

// Run on the Application/Dispatcher owned by DesignInteractionTests.
internal static class SettingsInteractionChecks
{
    public static async Task VerifyAllAsync()
    {
        FailedFlushRetainsDraftAndAllowsRetryIncludingUnchangedResult();
        AutomationToggleSavesOnceAndRestoringARejectedValueDoesNotRecurse();
        PasswordLabelNamesBothEditorsAcrossRevealChanges();
        await ModelResponsesStayWithTheCurrentAccountAsync();
    }

    private static async Task ModelResponsesStayWithTheCurrentAccountAsync()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var section = new AiServiceSettingsSection(new StackPanel(),
            new[] { new AiPlatformOption("自定义", "custom", null) },
            (_, _, _) => Task.FromResult((true, "")), (_, _) => pending.Task);
        section.Platform.SelectedIndex = 0;
        section.ApiUrl.Text = "https://example.test/v1";
        section.ApiKey.Password = "first-key";
        var fetch = typeof(AiServiceSettingsSection).GetMethod("FetchModels_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var button = new System.Windows.Controls.Button();
        var status = (TextBlock)typeof(AiServiceSettingsSection)
            .GetField("_testStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(section)!;

        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        section.ApiKey.Password = "second-key";
        pending.SetResult(new[] { "old-account-model" });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(section.Model.Items);
        Assert.Equal("未测试", status.Text);

        pending = new TaskCompletionSource<IReadOnlyList<string>>();
        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        section.UseSharedCredentials(() => ("https://shared.test/v1", "shared-key"));
        pending.SetException(new InvalidOperationException("old account failure"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(section.Model.Items);
        Assert.Equal("未测试", status.Text);

        pending = new TaskCompletionSource<IReadOnlyList<string>>();
        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        pending.SetResult(new[] { "shared-account-model" });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("shared-account-model", section.Model.Items.Cast<string>().Single());
        Assert.True(button.IsEnabled);
    }

    private static void FailedFlushRetainsDraftAndAllowsRetryIncludingUnchangedResult()
    {
        var input = new TextBox();
        var owner = new StackPanel();
        owner.Children.Add(input);
        var fail = true;
        var attempts = 0;
        string? saved = null;
        var controller = new SettingsAutoSaveController(owner, () =>
        {
            attempts++;
            if (fail) throw new InvalidOperationException("disk unavailable");
            saved = input.Text;
            return false; // Existing save delegates also return false when unchanged.
        });
        controller.TrackDebounced(input);
        input.Text = "unsaved draft";

        Assert.Same(controller, SettingsAutoSaveController.GetController(owner));
        Assert.False(controller.SaveNow());
        Assert.True(controller.HasPendingChanges);
        Assert.True(controller.HasSaveError);
        Assert.Equal("disk unavailable", controller.SaveError);
        Assert.Equal("unsaved draft", input.Text);
        Assert.Null(saved);
        Assert.False(controller.SaveNow());
        Assert.Equal(2, attempts);

        fail = false;
        Assert.True(controller.SaveNow());
        Assert.Equal("unsaved draft", saved);
        Assert.False(controller.HasPendingChanges);
        Assert.False(controller.HasSaveError);
        Assert.Null(controller.SaveError);
        Assert.True(controller.SaveNow());
        Assert.Equal(3, attempts);
    }

    private static void AutomationToggleSavesOnceAndRestoringARejectedValueDoesNotRecurse()
    {
        // Exercise the same event wiring as both row factories without creating a second
        // Application (the existing design interaction test owns the process Application).
        var switchBox = new CheckBox { Focusable = true };
        var host = new Border { Child = switchBox, Focusable = false };
        host.Resources["PrimaryBrush"] = Brushes.Blue;
        var saves = 0;
        var reject = false;
        Action<bool> save = enabled =>
        {
            saves++;
            if (reject) switchBox.IsChecked = !enabled;
        };
        typeof(SettingsLayout).GetMethod("WireSwitchHost", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { host, host, switchBox, "记录剪贴板历史", "", save, Brushes.Transparent, Brushes.LightBlue });

        switchBox.IsChecked = true;
        switchBox.IsChecked = false;
        Assert.Equal(0, saves);

        var window = new Window
        {
            Content = host, Width = 180, Height = 100,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0
        };
        try
        {
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(switchBox.IsLoaded);
            var peer = new CheckBoxAutomationPeer(switchBox);
            var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
            Assert.Equal("记录剪贴板历史", peer.GetName());
            Assert.True(peer.IsKeyboardFocusable());
            Assert.False(host.Focusable);

            toggle.Toggle();
            Assert.Equal(1, saves);
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            reject = true;
            toggle.Toggle();
            Assert.Equal(2, saves);
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            reject = false;
            toggle.Toggle();
            Assert.Equal(3, saves);
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
        }
        finally { window.Close(); }
    }

    private static void PasswordLabelNamesBothEditorsAcrossRevealChanges()
    {
        var resources = new Dictionary<string, object>
        {
            ["FontSizeBody"] = 12d,
            ["SunkenPasswordBox"] = new Style(typeof(PasswordBox)),
            ["SunkenTextBox"] = new Style(typeof(TextBox)),
            ["IconButton"] = new Style(typeof(System.Windows.Controls.Button)),
            ["IconEye"] = Geometry.Empty,
            ["IconEyeOff"] = Geometry.Empty,
            ["TextSecondaryBrush"] = Brushes.Gray,
            ["TransparentBrush"] = Brushes.Transparent
        };
        var field = new SecurePasswordField(key => resources[key]) { Password = "saved secret" };
        var label = new TextBlock { Text = "API Key" };
        field.SetAccessibleLabel(label);
        var masked = field.Children.OfType<PasswordBox>().Single();
        var revealed = field.Children.OfType<TextBox>().Single();
        var button = field.Children.OfType<System.Windows.Controls.Button>().Single();
        var maskedPeer = new PasswordBoxAutomationPeer(masked);
        var revealedPeer = new TextBoxAutomationPeer(revealed);

        Assert.Same(label, AutomationProperties.GetLabeledBy(masked));
        Assert.Same(label, AutomationProperties.GetLabeledBy(revealed));
        Assert.Equal("API Key", maskedPeer.GetName());
        Assert.Equal("API Key", revealedPeer.GetName());
        Assert.Equal("显示 API Key", AutomationProperties.GetName(button));

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(Visibility.Visible, revealed.Visibility);
        Assert.Equal(Visibility.Collapsed, masked.Visibility);
        Assert.Equal("saved secret", revealed.Text);
        Assert.Equal("API Key", revealedPeer.GetName());
        Assert.Equal("隐藏 API Key", AutomationProperties.GetName(button));

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(Visibility.Visible, masked.Visibility);
        Assert.Equal(Visibility.Collapsed, revealed.Visibility);
        Assert.Equal("API Key", maskedPeer.GetName());
        Assert.Equal("saved secret", field.Password);
    }
}
