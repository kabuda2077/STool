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
        await SettingsPagesPreserveChoicesAndRecoverFromSaveFailureAsync();
    }

    private static async Task ModelResponsesStayWithTheCurrentAccountAsync()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var sectionOwner = new StackPanel();
        var section = new AiServiceSettingsSection(sectionOwner,
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

        pending = new TaskCompletionSource<IReadOnlyList<string>>();
        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        sectionOwner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        pending.SetResult(new[] { "response-for-retired-page" });
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

    private static async Task SettingsPagesPreserveChoicesAndRecoverFromSaveFailureAsync()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STool.SettingsTests", Guid.NewGuid().ToString("N"));
        var configPath = System.IO.Path.Combine(root, "config.json");
        var config = new STool.Core.ConfigManager(configPath);
        config.Update(c =>
        {
            c.Ocr.LocalLanguage = "missing-test-language";
            c.Clipboard.ExcludedApps.Clear();
        });
        var shell = new SettingsShell();
        var window = new STool.Views.SettingsWindow(config, shell,
            () => new GeneralSettingsPanel(config, shell, () => false, _ => true));
        var content = (Grid)window.FindName("contentPanel");
        void Navigate(string name) => ((System.Windows.Controls.Button)window.FindName(name))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        T Field<T>(object owner, string name) => (T)owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CaptureSettings(window, "general");
            var general = Assert.IsType<GeneralSettingsPanel>(content.Children[0]);
            var screenshotHotkey = Field<TextBox>(general, "_txtScreenshotHotkey");
            screenshotHotkey.Text = config.Get().Hotkeys.Translation;
            typeof(GeneralSettingsPanel).GetMethod("CommitHotkeysIfChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(general, null);
            Assert.Equal(config.Get().Hotkeys.Screenshot, screenshotHotkey.Text);
            var errors = Field<Dictionary<TextBox, TextBlock>>(general, "_hotkeyErrors");
            Assert.Equal(Visibility.Visible, errors[screenshotHotkey].Visibility);
            CaptureSettings(window, "hotkey-conflict");
            screenshotHotkey.Text = "Ctrl+Alt+F9";
            Assert.Equal(Visibility.Collapsed, errors[screenshotHotkey].Visibility);
            screenshotHotkey.Text = config.Get().Hotkeys.Screenshot;
            Navigate("btnClipboardSettings");
            var clipboard = Assert.IsType<ClipboardSettingsPanel>(content.Children[0]);
            var draft = Field<TextBox>(clipboard, "_txtExcludedApps");
            draft.Text = "Private";
            await Task.Delay(650);
            Assert.Equal("Private", draft.Text);
            Assert.Empty(config.Get().Clipboard.ExcludedApps);
            draft.Text = "KeePass.exe, keepass.EXE; PrivateApp.exe";
            // Navigation must include the unfinished input in the flush.
            Navigate("btnOcrSettings");
            Assert.Equal(new[] { "KeePass.exe", "PrivateApp.exe" }, config.Get().Clipboard.ExcludedApps);
            var ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            var languages = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbLocalLanguage");
            Assert.Equal("missing-test-language", ((ComboBoxItem)languages.SelectedItem).Tag);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(ocr, "_fallbackRow").Visibility);
            var provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            provider.SelectedIndex = 1;
            Assert.Equal("missing-test-language", config.Get().Ocr.LocalLanguage);
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(ocr, "_fallbackRow").Visibility);
            CaptureSettings(window, "ocr-missing-language");
            // Follow the actual missing-account link, then return to the originating page.
            var note = Field<TextBlock>(ocr, "_accountNote");
            note.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single()
                .RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.IsType<AccountsSettingsPanel>(content.Children[0]);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("returnToSettings")).Visibility);
            ((System.Windows.Controls.Button)window.FindName("returnToSettings"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.IsType<OcrSettingsPanel>(content.Children[0]);

            foreach (var (button, name) in new[]
            {
                ("btnGeneralSettings", "general"), ("btnClipboardSettings", "clipboard"),
                ("btnAccountsSettings", "accounts"), ("btnOcrSettings", "ocr"), ("btnTranslationSettings", "translation")
            })
            {
                Navigate(button);
                await Task.Delay(250); // Let the real expander transition finish before rendering.
                Assert.False(SettingsAutoSaveController.GetController(content.Children[0])?.HasPendingChanges ?? false);
                CaptureSettings(window, name);
                window.Width = 640; window.Height = 480;
                CaptureSettings(window, name + "-minimum");
                window.Width = 710; window.Height = 560;
            }

            foreach (var target in new[] { ServiceAccountTarget.AiText, ServiceAccountTarget.AiVision })
            {
                Navigate("btnTranslationSettings");
                typeof(STool.Views.SettingsWindow).GetMethod("OpenAccount", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object[] { target });
                await Task.Delay(250);
                var accounts = Assert.IsType<AccountsSettingsPanel>(content.Children[0]);
                var expander = Field<Expander>(accounts, target == ServiceAccountTarget.AiText ? "_aiTextExpander" : "_aiVisionExpander");
                Assert.True(expander.IsExpanded);
                CaptureSettings(window, "accounts-" + target);
                window.Width = 640; window.Height = 480;
                CaptureSettings(window, "accounts-" + target + "-minimum");
                window.Width = 710; window.Height = 560;
            }

            // Make the next atomic save fail without altering the saved configuration.
            Navigate("btnOcrSettings");
            ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            System.IO.Directory.CreateDirectory(configPath + ".tmp");
            provider.SelectedIndex = 2;
            Navigate("btnTranslationSettings");
            Assert.Same(ocr, content.Children[0]);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("saveFailure")).Visibility);
            CaptureSettings(window, "save-failure");
            typeof(STool.Views.SettingsWindow).GetMethod("DiscardSave_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.IsType<TranslationSettingsPanel>(content.Children[0]);
            Assert.Equal(STool.Models.OcrProvider.Tencent, config.Get().Ocr.Provider);
            System.IO.Directory.Delete(configPath + ".tmp");

            Navigate("btnOcrSettings");
            ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            System.IO.Directory.CreateDirectory(configPath + ".tmp");
            provider.SelectedIndex = 2;
            window.Close();
            Assert.True(window.IsVisible);
            // Resuming editing cancels the previous close request, including after recovery.
            System.IO.Directory.Delete(configPath + ".tmp");
            provider.SelectedIndex = 0;
            Assert.True(window.IsVisible);
            System.IO.Directory.CreateDirectory(configPath + ".tmp");
            provider.SelectedIndex = 2;
            typeof(STool.Views.SettingsWindow).GetMethod("DiscardSave_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.True(window.IsVisible);
            ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            Assert.Equal(0, provider.SelectedIndex);
            provider.SelectedIndex = 2;
            window.Close();
            Assert.True(window.IsVisible);
            typeof(STool.Views.SettingsWindow).GetMethod("DiscardSave_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.False(window.IsVisible);
            Assert.Equal(STool.Models.OcrProvider.WindowsLocal, config.Get().Ocr.Provider);
        }
        finally
        {
            if (content.Children.Count > 0)
                SettingsAutoSaveController.GetController(content.Children[0])?.DiscardChanges();
            window.Close();
            System.IO.Directory.Delete(root, true);
        }
    }

    private static void CaptureSettings(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("STOOL_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.UpdateLayout();
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(directory, "settings-" + name + ".png"));
        encoder.Save(file);
    }

    private sealed class SettingsShell : STool.Core.IAppShell
    {
        public IReadOnlyList<STool.Core.HotkeyRegistrationResult> ReloadHotkeys(bool notifyFailures = false) => [];
        public void SuspendHotkeys() { }
        public void ReloadTrayIconVisibility() { }
        public void ApplyClipboardSettings() { }
        public void ApplyDiagnosticsSettings() { }
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
