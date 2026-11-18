using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using STool.Core;
using STool.Views.Settings;
using Xunit;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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
        await ConditionalTooltipsFollowVisibleTextAndEndpointChangesAsync();
        await ModelResponsesStayWithTheCurrentAccountAsync();
        await PickerKeyboardCommitsOnlyConfirmedChoicesAsync();
        await CollapsedAccountCancelsAndPreservesNewerRequestsAsync();
        await CloudTestRequestsCancelAndKeepCompletedResultsAsync();
        AccountReusePreservesDraftsAndIndependentModels();
        await SettingsPagesPreserveChoicesAndRecoverFromSaveFailureAsync();
        await RetrySaveContinuesPendingNavigationAndCloseAsync();
    }

    private static async Task ConditionalTooltipsFollowVisibleTextAndEndpointChangesAsync()
    {
        var label = new TextBlock
        {
            Text = "a-long-application-process-name.exe", Width = 320,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SettingsLayout.SetTextHelp(label);
        var modelButton = SettingsLayout.CreateFieldButton("model-id", () => { });
        var modelLabel = (TextBlock)modelButton.Tag;
        SettingsLayout.SetTextHelp(modelLabel, "model-id", modelButton);
        var sectionOwner = new StackPanel();
        var section = new AiServiceSettingsSection(sectionOwner, [], (_, _, _, _) => Task.FromResult((true, "")));
        var content = new StackPanel();
        content.Children.Add(label);
        content.Children.Add(modelButton);
        content.Children.Add(sectionOwner);
        var window = new Window { Content = content, Width = 500, Height = 400, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(label.ToolTip);
            Assert.Null(modelButton.ToolTip);
            label.Width = 45;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(label.Text, Assert.IsType<ToolTip>(label.ToolTip).Content);
            label.Width = 320;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(label.ToolTip);

            SettingsLayout.SetFieldButtonLabel(modelButton, "Friendly model");
            SettingsLayout.SetTextHelp(modelLabel, "model-id", modelButton);
            Assert.Equal("model-id", Assert.IsType<ToolTip>(modelButton.ToolTip).Content);
            Assert.Null(((FrameworkElement)modelButton.Parent).ToolTip);
            SettingsLayout.SetFieldButtonLabel(modelButton, "选择模型");
            SettingsLayout.SetTextHelp(modelLabel, string.Empty, modelButton);
            Assert.Null(modelButton.ToolTip);

            section.ApiUrl.Text = "https://example.test/v1";
            var tip = Assert.IsType<ToolTip>(section.ApiUrl.ToolTip);
            Assert.Same(section.ApiUrl, tip.PlacementTarget);
            Assert.Contains("/v1/chat/completions", Assert.IsType<string>(tip.Content));
            Assert.Null(((FrameworkElement)section.ApiUrl.Parent).ToolTip);
            section.ApiUrl.Text = "https://example.test/v1/chat/completions";
            Assert.Null(section.ApiUrl.ToolTip);
            section.ApiUrl.Text = "invalid";
            Assert.Null(section.ApiUrl.ToolTip);
            var hint = (TextBlock)typeof(AiServiceSettingsSection).GetField("_apiUrlHint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(section)!;
            Assert.Equal(Visibility.Visible, hint.Visibility);
            Assert.NotEmpty(hint.Text);
        }
        finally { window.Close(); }
    }

    private static async Task ModelResponsesStayWithTheCurrentAccountAsync()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var sectionOwner = new StackPanel();
        CancellationToken lastToken = default;
        var section = new AiServiceSettingsSection(sectionOwner,
            new[] { new AiPlatformOption("自定义", "custom", null) },
            (_, _, _, _) => Task.FromResult((true, "")), (_, _, token) => { lastToken = token; return pending.Task; });
        section.Platform.SelectedIndex = 0;
        section.ApiUrl.Text = "https://example.test/v1";
        section.ApiKey.Password = "first-key";
        var fetch = typeof(AiServiceSettingsSection).GetMethod("FetchModels_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var button = new System.Windows.Controls.Button();
        var status = (TextBlock)typeof(AiServiceSettingsSection)
            .GetField("_testStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(section)!;

        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        section.ApiKey.Password = "second-key";
        Assert.True(lastToken.IsCancellationRequested);
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
        pending.SetResult(new[] { "gpt-6-luna", "Vendor-model-v2" });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var items = section.Model.Items.Cast<ComboBoxItem>().ToArray();
        Assert.Equal("GPT-6 Luna", items[0].Content);
        Assert.Equal("gpt-6-luna", items[0].Tag);
        Assert.Equal("Vendor-model-v2", items[1].Content);
        section.Model.SelectedItem = items[0];
        Assert.Equal("gpt-6-luna", section.ModelName);
        Assert.True(button.IsEnabled);

        pending = new TaskCompletionSource<IReadOnlyList<string>>();
        fetch.Invoke(section, new object[] { button, new RoutedEventArgs() });
        sectionOwner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        pending.SetResult(new[] { "response-for-retired-page" });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(new[] { "gpt-6-luna", "Vendor-model-v2" }, section.Model.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag));
        Assert.True(lastToken.IsCancellationRequested);
        Assert.True(button.IsEnabled);

        var pendingTest = new TaskCompletionSource<(bool Success, string Message)>();
        var tested = new AiServiceSettingsSection(new StackPanel(),
            new[] { new AiPlatformOption("自定义", "custom", null) },
            (_, _, _, token) => { lastToken = token; return pendingTest.Task; });
        tested.Platform.SelectedIndex = 0;
        tested.ApiUrl.Text = "https://example.test/v1/chat/completions";
        tested.ApiKey.Password = "key";
        tested.Model.Text = "first-model";
        typeof(AiServiceSettingsSection).GetMethod("Test_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tested, new object[] { button, new RoutedEventArgs() });
        tested.Model.Text = "second-model";
        // An editable combo without its template does not raise TextBoxBase.TextChanged yet.
        tested.InvalidateTest();
        Assert.True(lastToken.IsCancellationRequested);
        pendingTest.SetResult((true, "old model success"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(tested.TestSucceeded);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name)!.GetValue(owner)!;

    private static async Task PickerKeyboardCommitsOnlyConfirmedChoicesAsync()
    {
        var combo = SettingsLayout.CreateEditableComboBox();
        var first = new ComboBoxItem { Content = "GPT-6 Luna", Tag = "gpt-6-luna" };
        var second = new ComboBoxItem { Content = "Vendor-model-v2", Tag = "Vendor-model-v2" };
        TextSearch.SetText(first, "gpt-6-luna");
        TextSearch.SetText(second, "Vendor-model-v2");
        combo.Items.Add(first);
        combo.Items.Add(second);
        combo.SelectedItem = first;
        combo.Text = "gpt-6-luna";
        var anchor = SettingsLayout.CreateSecondaryActionButton("选择模型");
        SettingsPickerPlacement.SetMenuAnchor(combo, anchor);
        var content = new StackPanel();
        content.Children.Add(combo);
        content.Children.Add(anchor);
        var window = new Window { Content = content, Width = 500, Height = 180, ShowInTaskbar = false };
        var commits = 0;
        combo.SelectionChanged += (_, _) => commits++;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            void Press(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            combo.Focus();
            Press(combo, Key.Down);
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal("gpt-6-luna", combo.Text);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(first.Focus());
            Press(first, Key.Down);
            Assert.True(second.IsKeyboardFocused);
            Assert.Same(first, combo.SelectedItem);
            Assert.Equal("gpt-6-luna", combo.Text);
            Assert.True(combo.IsDropDownOpen);
            Assert.Equal(0, commits);
            Press(second, Key.Escape);
            Assert.False(combo.IsDropDownOpen);
            Assert.True(anchor.IsKeyboardFocused);
            Assert.Same(first, combo.SelectedItem);
            Assert.Equal(0, commits);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(first.Focus());
            Press(first, Key.Down);
            Press(second, Key.Enter);
            Assert.False(combo.IsDropDownOpen);
            Assert.True(anchor.IsKeyboardFocused);
            Assert.Same(second, combo.SelectedItem);
            Assert.Equal("Vendor-model-v2", combo.Text);
            Assert.Equal(1, commits);
        }
        finally { window.Close(); }
    }

    private static async Task CollapsedAccountCancelsAndPreservesNewerRequestsAsync()
    {
        var requests = new Queue<TaskCompletionSource<IReadOnlyList<string>>>();
        var firstRequest = new TaskCompletionSource<IReadOnlyList<string>>();
        var secondRequest = new TaskCompletionSource<IReadOnlyList<string>>();
        requests.Enqueue(firstRequest);
        requests.Enqueue(secondRequest);
        CancellationToken lastToken = default;
        var content = new StackPanel();
        var section = new AiServiceSettingsSection(content,
            [new("自定义", "custom", null)], (_, _, _, _) => Task.FromResult((true, "")),
            (_, _, token) => { lastToken = token; return requests.Dequeue().Task; });
        section.Platform.SelectedIndex = 0;
        section.ApiUrl.Text = "https://example.test/v1/chat/completions";
        section.ApiKey.Password = "fixture-key";
        section.Model.Text = "fixture-model";
        var expander = new Expander
        {
            Header = "AI 文字识别", Content = content, IsExpanded = true,
            Style = (Style)Application.Current.FindResource("SettingsExpander")
        };
        var window = new Window { Content = expander, Width = 600, Height = 450, ShowInTaskbar = false };
        var button = SettingsLayout.CreateSecondaryActionButton("选择模型");
        var fetch = typeof(AiServiceSettingsSection).GetMethod("FetchModels_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(content.IsVisible);
            fetch.Invoke(section, [button, new RoutedEventArgs()]);
            Assert.False(button.IsEnabled);
            expander.IsExpanded = false;
            window.UpdateLayout();
            Assert.True(lastToken.IsCancellationRequested);
            Assert.True(button.IsEnabled);
            Assert.Equal("选择模型", button.Content);
            expander.IsExpanded = true;
            window.UpdateLayout();
            fetch.Invoke(section, [button, new RoutedEventArgs()]);
            Assert.False(lastToken.IsCancellationRequested);
            Assert.False(button.IsEnabled);
            firstRequest.SetResult(["stale-model"]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(button.IsEnabled);
            Assert.Equal("获取中…", button.Content);
            Assert.Empty(section.Model.Items);
            Assert.False(section.Model.IsDropDownOpen);
            expander.IsExpanded = false;
            window.UpdateLayout();
            Assert.True(lastToken.IsCancellationRequested);
            Assert.True(button.IsEnabled);
            secondRequest.SetResult(["hidden-model"]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Empty(section.Model.Items);
            Assert.False(section.Model.IsDropDownOpen);
            Assert.Equal("选择模型", button.Content);
        }
        finally { window.Close(); }
    }

    private static async Task CloudTestRequestsCancelAndKeepCompletedResultsAsync()
    {
        var firstRequest = new TaskCompletionSource<(bool Success, string Message)>();
        var secondRequest = new TaskCompletionSource<(bool Success, string Message)>();
        var requests = new Queue<TaskCompletionSource<(bool Success, string Message)>>();
        requests.Enqueue(firstRequest);
        requests.Enqueue(secondRequest);
        var tokens = new List<CancellationToken>();
        Func<string, string, CancellationToken, Task<(bool Success, string Message)>> test = (id, key, token) =>
        {
            Assert.Equal("fixture-id", id);
            Assert.Equal("fixture-key", key);
            tokens.Add(token);
            return requests.Dequeue().Task; // Deliberately ignore cancellation to exercise late responses.
        };
        static bool Decrypt(string? value, out string plain)
        {
            plain = value ?? "";
            return true;
        }
        Func<string?, EncryptedSetting> secretSetting = value => new EncryptedSetting(value, plain => plain, Decrypt);
        var editorType = typeof(AccountsSettingsPanel).GetNestedType("CloudAccountEditor", BindingFlags.NonPublic)!;
        var editor = Activator.CreateInstance(editorType,
            ["腾讯云 OCR", "复用翻译的腾讯云账号", "翻译", "测试识别", test, secretSetting])!;
        Property<TextBox>(editor, "SecretId").Text = "fixture-id";
        Property<SecurePasswordField>(editor, "SecretKey").Password = "fixture-key";
        var expander = Property<Expander>(editor, "Expander");
        expander.IsExpanded = true;
        var content = (StackPanel)expander.Content;
        var actions = (Grid)content.Children.OfType<Border>().Single().Child;
        var button = actions.Children.OfType<System.Windows.Controls.Button>().Single();
        var status = Field<TextBlock>(editor, "_testStatus");
        var window = new Window { Content = expander, Width = 600, Height = 450, ShowInTaskbar = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(content.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Single(tokens);
            Assert.False(button.IsEnabled);
            expander.IsExpanded = false;
            window.UpdateLayout();
            Assert.True(tokens[0].IsCancellationRequested);
            Assert.True(button.IsEnabled);
            Assert.Equal("测试识别", button.Content);
            Assert.Equal("未测试", status.Text);
            Assert.False(Property<bool>(editor, "TestSucceeded"));

            expander.IsExpanded = true;
            window.UpdateLayout();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(2, tokens.Count);
            Assert.False(tokens[1].IsCancellationRequested);
            firstRequest.SetResult((true, "stale success"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(button.IsEnabled);
            Assert.Equal("测试中…", button.Content);
            Assert.Equal("正在测试…", status.Text);
            Assert.False(Property<bool>(editor, "TestSucceeded"));

            var busyRestored = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnBusyRestored(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (e.NewValue is true) busyRestored.TrySetResult(true);
            }
            button.IsEnabledChanged += OnBusyRestored;
            try
            {
                secondRequest.SetResult((true, "current success"));
                await busyRestored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { button.IsEnabledChanged -= OnBusyRestored; }
            Assert.True(button.IsEnabled);
            Assert.Equal("测试识别", button.Content);
            Assert.Equal("测试通过", status.Text);
            Assert.True(Property<bool>(editor, "TestSucceeded"));
            expander.IsExpanded = false;
            window.UpdateLayout();
            Assert.True(Property<bool>(editor, "TestSucceeded"));
            Assert.Equal("测试通过", status.Text);
            expander.IsExpanded = true;
            window.UpdateLayout();
            Assert.True(Property<bool>(editor, "TestSucceeded"));
            Assert.Equal("测试通过", status.Text);
            Assert.Equal(2, tokens.Count);
        }
        finally { window.Close(); }
    }

    private static void AccountReusePreservesDraftsAndIndependentModels()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STool.AccountSettingsTests", Guid.NewGuid().ToString("N"));
        var config = new STool.Core.ConfigManager(System.IO.Path.Combine(root, "config.json"),
            value => value[3..], value => "v2:" + value, value => value.StartsWith("v2:"));
        static bool Decrypt(string? value, out string plain)
        {
            plain = value == null ? "" : value[3..];
            return true;
        }
        var panel = new AccountsSettingsPanel(config, null,
            value => new EncryptedSetting(value, plain => "v2:" + plain, Decrypt));
        var controller = SettingsAutoSaveController.GetController(panel)!;
        try
        {
            Assert.Equal(2, panel.Children.Count);
            var vision = Field<AiServiceSettingsSection>(panel, "_aiVision");
            var text = Field<AiServiceSettingsSection>(panel, "_aiText");
            var visionOffer = Field<object>(panel, "_visionReuse");
            var textOffer = Field<object>(panel, "_textReuse");
            var visionReuse = Property<CheckBox>(visionOffer, "CheckBox");
            var textReuse = Property<CheckBox>(textOffer, "CheckBox");
            var visionUrl = vision.ApiUrl.Text;
            vision.ApiKey.Password = "vision-independent-key";
            vision.Model.Text = "Vision-Custom-ID";
            text.ApiKey.Password = "text-source-key";
            text.Model.Text = ""; // A connection can be reused before choosing its own model.
            Assert.Equal(Visibility.Visible, Property<StackPanel>(visionOffer, "Panel").Visibility);
            visionReuse.IsChecked = true;
            Assert.Equal(STool.Models.AccountReuseSource.Translation, config.Get().Accounts.AiVision.ReuseFrom);
            Assert.Equal("Vision-Custom-ID", config.Get().Accounts.AiVision.Model);
            Assert.True(vision.IsConfigured);
            Assert.Equal(Visibility.Collapsed, Property<StackPanel>(textOffer, "Panel").Visibility);
            textReuse.IsChecked = true; // Programmatic/automation attempts cannot create a reverse cycle.
            Assert.False(textReuse.IsChecked);
            Assert.Equal(STool.Models.AccountReuseSource.None, config.Get().Accounts.AiText.ReuseFrom);
            text.ApiKey.Password = "";
            Assert.True(visionReuse.IsChecked);
            Assert.False(vision.IsConfigured);
            Assert.Equal(Visibility.Visible, Field<TextBlock>(visionOffer, "_warning").Visibility);
            text.ApiKey.Password = "replacement-source-key";
            text.Platform.SelectedIndex = 2;
            Assert.True(vision.IsConfigured);
            Assert.Equal("Vision-Custom-ID", vision.ModelName);
            visionReuse.IsChecked = false;
            Assert.Equal(visionUrl, vision.ApiUrl.Text);
            Assert.Equal("vision-independent-key", vision.ApiKey.Password);
            Assert.Equal(STool.Models.AiPlatform.OpenAI, vision.SelectedPlatformTag);
            Assert.Equal(STool.Models.AccountReuseSource.None, config.Get().Accounts.AiVision.ReuseFrom);

            var ocr = Field<object>(panel, "_tencentOcr");
            var translation = Field<object>(panel, "_tencentTranslation");
            Property<TextBox>(ocr, "SecretId").Text = "ocr-own-id";
            Property<SecurePasswordField>(ocr, "SecretKey").Password = "ocr-own-key";
            Property<TextBox>(translation, "SecretId").Text = "translation-source-id";
            Property<SecurePasswordField>(translation, "SecretKey").Password = "translation-source-key";
            var reuse = Property<CheckBox>(Property<object>(ocr, "Reuse"), "CheckBox");
            reuse.IsChecked = true;
            Assert.Equal(STool.Models.AccountReuseSource.Translation, config.Get().Accounts.TencentOcr.ReuseFrom);
            Assert.Equal("v2:ocr-own-id", config.Get().Accounts.TencentOcr.SecretIdEncrypted);
            reuse.IsChecked = false;
            Assert.Equal("ocr-own-id", Property<TextBox>(ocr, "SecretId").Text);
            Assert.Equal("ocr-own-key", Property<SecurePasswordField>(ocr, "SecretKey").Password);
            var ocrExpander = Property<Expander>(ocr, "Expander");
            var visionExpander = Field<Expander>(panel, "_aiVisionExpander");
            ocrExpander.IsExpanded = true;
            visionExpander.IsExpanded = true;
            Assert.False(ocrExpander.IsExpanded);
        }
        finally
        {
            controller.DiscardChanges();
            System.IO.Directory.Delete(root, true);
        }
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
            c.Clipboard.MaxEntries = 1234;
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
            var originalScreenshotHotkey = config.Get().Hotkeys.Screenshot;
            var originalTranslationHotkey = config.Get().Hotkeys.Translation;
            var displayedScreenshotHotkey = STool.Core.HotkeyManager.NormalizeHotkey(originalScreenshotHotkey)!.Replace("+", " + ");
            Assert.Equal(displayedScreenshotHotkey, screenshotHotkey.Text);
            screenshotHotkey.Text = originalTranslationHotkey;
            var commitHotkeys = typeof(GeneralSettingsPanel)
                .GetMethod("CommitHotkeysIfChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            commitHotkeys.Invoke(general, null);
            Assert.Equal(originalScreenshotHotkey, config.Get().Hotkeys.Screenshot);
            Assert.Equal(originalTranslationHotkey, config.Get().Hotkeys.Translation);
            Assert.Equal(displayedScreenshotHotkey, screenshotHotkey.Text);
            var errors = Field<Dictionary<TextBox, TextBlock>>(general, "_hotkeyErrors");
            Assert.Equal(Visibility.Visible, errors[screenshotHotkey].Visibility);
            CaptureSettings(window, "hotkey-conflict");
            screenshotHotkey.Text = "Ctrl + Alt + F9";
            Assert.Equal(Visibility.Collapsed, errors[screenshotHotkey].Visibility);
            commitHotkeys.Invoke(general, null);
            Assert.Equal("Ctrl+Alt+F9", config.Get().Hotkeys.Screenshot);
            Assert.Equal("Ctrl + Alt + F9", screenshotHotkey.Text);
            screenshotHotkey.Text = originalScreenshotHotkey;
            commitHotkeys.Invoke(general, null);
            Assert.Equal(originalScreenshotHotkey, config.Get().Hotkeys.Screenshot);
            Assert.Equal(displayedScreenshotHotkey, screenshotHotkey.Text);
            HotkeyRecordingUsesNativeEventsAndSavesImmediately(general, window, config, shell);
            Navigate("btnClipboardSettings");
            var clipboard = Assert.IsType<ClipboardSettingsPanel>(content.Children[0]);
            var draft = Field<TextBox>(clipboard, "_txtExcludedApps");
            Assert.Equal(1234, ((ComboBoxItem)Field<System.Windows.Controls.ComboBox>(clipboard, "_cmbMaxEntries").SelectedItem).Tag);
            draft.Text = "Private";
            await Task.Delay(650);
            Assert.Equal("Private", draft.Text);
            Assert.Empty(config.Get().Clipboard.ExcludedApps);
            draft.Text = "KeePass.exe, keepass.EXE; PrivateApp.exe";
            // Navigation must include the unfinished input in the flush.
            Navigate("btnOcrSettings");
            var clipboardSave = SettingsAutoSaveController.GetController(clipboard)!;
            Assert.False(clipboardSave.HasSaveError, clipboardSave.SaveError);
            Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            Assert.Equal(new[] { "KeePass.exe", "PrivateApp.exe" }, config.Get().Clipboard.ExcludedApps);
            Assert.Equal(1234, config.Get().Clipboard.MaxEntries);
            var ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            var languages = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbLocalLanguage");
            Assert.Equal("missing-test-language", ((ComboBoxItem)languages.SelectedItem).Tag);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(ocr, "_fallbackRow").Visibility);
            var provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            provider.SelectedIndex = 1;
            Assert.Equal("missing-test-language", config.Get().Ocr.LocalLanguage);
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(ocr, "_fallbackRow").Visibility);
            var fallback = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbFallbackPolicy");
            fallback.SelectedIndex = 1;
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(ocr, "_localSection").Visibility);
            CaptureSettings(window, "ocr-missing-language");
            // Follow the actual missing-account link, then return to the originating page.
            var note = Field<WrapPanel>(ocr, "_accountNote");
            note.Children.OfType<System.Windows.Controls.Button>().Single()
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.IsType<AccountsSettingsPanel>(content.Children[0]);
            Navigate("btnOcrSettings");
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
                window.Width = 600; window.Height = 480;
                CaptureSettings(window, name + "-minimum");
                window.Width = 660; window.Height = 560;
            }

            foreach (var target in new[] { ServiceAccountTarget.TencentOcr, ServiceAccountTarget.AiVision, ServiceAccountTarget.TencentTranslation, ServiceAccountTarget.AiText })
            {
                Navigate("btnTranslationSettings");
                typeof(STool.Views.SettingsWindow).GetMethod("OpenAccount", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object[] { target });
                await Task.Delay(250);
                var accounts = Assert.IsType<AccountsSettingsPanel>(content.Children[0]);
                var expander = (Expander)typeof(AccountsSettingsPanel).GetMethod("GetExpander", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(accounts, new object[] { target })!;
                Assert.True(expander.IsExpanded);
                CaptureSettings(window, "accounts-" + target);
                window.Width = 600; window.Height = 480;
                CaptureSettings(window, "accounts-" + target + "-minimum");
                window.Width = 660; window.Height = 560;
            }

            config.Update(c => c.Translation.ScreenshotOcrMigrationPending = true);
            Navigate("btnTranslationSettings");
            var translationPage = Assert.IsType<TranslationSettingsPanel>(content.Children[0]);
            var migration = Field<WrapPanel>(translationPage, "_migrationNote");
            Assert.Equal(Visibility.Visible, migration.Visibility);
            migration.Children.OfType<System.Windows.Controls.Button>().Single()
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(config.Get().Translation.ScreenshotOcrMigrationPending);
            Assert.Equal(Visibility.Collapsed, migration.Visibility);
            Assert.Equal(Visibility.Visible, Field<WrapPanel>(translationPage, "_screenshotOcrNote").Visibility);
            Field<System.Windows.Controls.ComboBox>(translationPage, "_cmbScreenshotMode").SelectedIndex = 1;
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(translationPage, "_smartModelRow").Visibility);
            Assert.Equal(config.Get().Translation.TranslationMode,
                ((ComboBoxItem)Field<System.Windows.Controls.ComboBox>(translationPage, "_cmbTranslationMode").SelectedItem).Tag);

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

    private static async Task RetrySaveContinuesPendingNavigationAndCloseAsync()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STool.SettingsRetryTests", Guid.NewGuid().ToString("N"));
        var configPath = System.IO.Path.Combine(root, "config.json");
        var config = new ConfigManager(configPath, value => value, value => value, _ => true);
        var shell = new SettingsShell();
        var window = new STool.Views.SettingsWindow(config, shell,
            () => new GeneralSettingsPanel(config, shell, () => false, _ => true));
        var content = (Grid)window.FindName("contentPanel");
        var retry = (System.Windows.Controls.Button)window.FindName("retrySave");
        void Navigate(string name) => ((System.Windows.Controls.Button)window.FindName(name))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Navigate("btnOcrSettings");
            var ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            var provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            var controller = SettingsAutoSaveController.GetController(ocr)!;
            System.IO.Directory.CreateDirectory(configPath + ".tmp");
            provider.SelectedIndex = 1;
            Navigate("btnTranslationSettings");
            Assert.Same(ocr, content.Children[0]);
            Assert.True(controller.HasPendingChanges);
            Assert.True(controller.HasSaveError);
            Assert.True(retry.IsVisible);
            Assert.Equal(STool.Models.OcrProvider.WindowsLocal, config.Get().Ocr.Provider);

            // Restore the atomic-save target and use the actual window button to resume navigation.
            System.IO.Directory.Delete(configPath + ".tmp");
            retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.IsType<TranslationSettingsPanel>(content.Children[0]);
            Assert.False(controller.HasPendingChanges);
            Assert.False(controller.HasSaveError);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("saveFailure")).Visibility);
            Assert.Equal(STool.Models.OcrProvider.Tencent,
                new ConfigManager(configPath, value => value, value => value, _ => true).Get().Ocr.Provider);

            Navigate("btnOcrSettings");
            ocr = Assert.IsType<OcrSettingsPanel>(content.Children[0]);
            provider = Field<System.Windows.Controls.ComboBox>(ocr, "_cmbProvider");
            controller = SettingsAutoSaveController.GetController(ocr)!;
            System.IO.Directory.CreateDirectory(configPath + ".tmp");
            provider.SelectedIndex = 2;
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Same(ocr, content.Children[0]);
            Assert.True(controller.HasPendingChanges);
            Assert.True(controller.HasSaveError);
            Assert.True(retry.IsVisible);
            Assert.Equal(STool.Models.OcrProvider.Tencent, config.Get().Ocr.Provider);

            System.IO.Directory.Delete(configPath + ".tmp");
            retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(window.IsVisible);
            Assert.False(controller.HasPendingChanges);
            Assert.False(controller.HasSaveError);
            Assert.Equal(STool.Models.OcrProvider.AI,
                new ConfigManager(configPath, value => value, value => value, _ => true).Get().Ocr.Provider);
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

    private static void HotkeyRecordingUsesNativeEventsAndSavesImmediately(
        GeneralSettingsPanel panel, Window window, ConfigManager config, SettingsShell shell)
    {
        var screenshot = Field<HotkeyBox>(panel, "_txtScreenshotHotkey");
        var translation = Field<HotkeyBox>(panel, "_txtTranslationHotkey");
        var errors = Field<Dictionary<TextBox, TextBlock>>(panel, "_hotkeyErrors");
        var originalScreenshot = config.Get().Hotkeys.Screenshot;
        var originalText = screenshot.Text;
        var originalTranslation = config.Get().Hotkeys.Translation;
        var outside = (System.Windows.Controls.Button)window.FindName("btnClipboardSettings");
        var source = PresentationSource.FromVisual(window)!;
        KeyEventArgs PressKey(HotkeyBox box, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            box.RaiseEvent(args);
            return args;
        }
        void Click(HotkeyBox box) => box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });

        Assert.True(screenshot.Focus());
        Assert.False(screenshot.IsRecording);
        PressKey(screenshot, Key.Enter);
        Assert.True(screenshot.IsRecording);
        Assert.True(shell.HotkeysSuspended);
        Assert.Equal("按下快捷键…", screenshot.RecordingText);
        Assert.Equal(originalText, screenshot.Text);
        Assert.Equal(Visibility.Collapsed, errors[screenshot].Visibility);
        CaptureSettings(window, "hotkey-recording");
        PressKey(screenshot, Key.LeftCtrl);
        Assert.True(screenshot.IsRecording);
        Assert.Equal(Visibility.Collapsed, errors[screenshot].Visibility);
        PressKey(screenshot, Key.A);
        Assert.True(screenshot.IsRecording);
        Assert.Equal(originalText, screenshot.Text);
        Assert.Equal("请使用修饰键组合或功能键。", errors[screenshot].Text);
        Assert.Equal(Visibility.Visible, errors[screenshot].Visibility);
        CaptureSettings(window, "hotkey-recording-error");
        PressKey(screenshot, Key.Escape);
        Assert.False(screenshot.IsRecording);
        Assert.Equal(originalScreenshot, config.Get().Hotkeys.Screenshot);
        Assert.Equal(originalText, screenshot.Text);

        // A click on the already-focused box restarts capture. F8 saves before blur.
        Click(screenshot);
        Assert.True(screenshot.IsRecording);
        var reloadsBeforeSave = shell.HotkeyReloads;
        PressKey(screenshot, Key.F8);
        Assert.False(screenshot.IsRecording);
        Assert.False(shell.HotkeysSuspended);
        Assert.Equal("F8", config.Get().Hotkeys.Screenshot);
        Assert.Equal("F8", screenshot.Text);
        Assert.Equal(reloadsBeforeSave + 1, shell.HotkeyReloads);
        var reloadsAfterSave = shell.HotkeyReloads;
        Assert.True(outside.Focus());
        Assert.Equal(reloadsAfterSave, shell.HotkeyReloads);

        Assert.True(screenshot.Focus());
        Click(screenshot);
        var tab = PressKey(screenshot, Key.Tab);
        Assert.False(tab.Handled);
        Assert.False(screenshot.IsRecording);
        Assert.Equal("F8", config.Get().Hotkeys.Screenshot);
        Assert.True(translation.Focus());
        Assert.False(translation.IsRecording);
        Assert.True(screenshot.Focus());
        Click(screenshot);
        Assert.True(screenshot.IsRecording);
        Assert.True(outside.Focus());
        Assert.False(screenshot.IsRecording);
        Assert.Equal("F8", screenshot.Text);

        Assert.True(screenshot.Focus());
        Click(screenshot);
        var blankArea = (Grid)window.FindName("contentPanel");
        blankArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.False(screenshot.IsRecording);
        Assert.False(shell.HotkeysSuspended);
        Assert.Equal("F8", screenshot.Text);

        Assert.True(translation.Focus());
        Click(translation);
        PressKey(translation, Key.F8);
        Assert.True(translation.IsRecording);
        Assert.Equal(originalTranslation, config.Get().Hotkeys.Translation);
        Assert.Equal(Visibility.Visible, errors[translation].Visibility);
        Assert.Equal(Visibility.Collapsed, errors[screenshot].Visibility);
        Assert.Equal("此快捷键已分配给截图。", errors[translation].Text);
        CaptureSettings(window, "hotkey-recording-conflict");
        PressKey(translation, Key.Escape);
        Assert.False(translation.IsRecording);
        Assert.Equal(Visibility.Collapsed, errors[translation].Visibility);

        Assert.True(screenshot.Focus());
        Click(screenshot);
        shell.NextHotkeyFailure = new HotkeyRegistrationResult("截图", "F9", false, HotkeyRegistrationFailure.SystemRejected);
        PressKey(screenshot, Key.F9);
        Assert.True(screenshot.IsRecording);
        Assert.True(shell.HotkeysSuspended);
        Assert.Equal("F8", config.Get().Hotkeys.Screenshot);
        Assert.Equal("F8", screenshot.Text);
        Assert.Equal(Visibility.Visible, errors[screenshot].Visibility);
        PressKey(screenshot, Key.Escape);
        Assert.False(screenshot.IsRecording);
        Assert.False(shell.HotkeysSuspended);

        screenshot.Text = originalText;
        typeof(GeneralSettingsPanel).GetMethod("CommitHotkeysIfChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        Assert.Equal(originalScreenshot, config.Get().Hotkeys.Screenshot);
        Assert.Equal(originalText, screenshot.Text);
    }

    private sealed class SettingsShell : STool.Core.IAppShell
    {
        public int HotkeyReloads { get; private set; }
        public bool HotkeysSuspended { get; private set; }
        public HotkeyRegistrationResult? NextHotkeyFailure { get; set; }

        public IReadOnlyList<STool.Core.HotkeyRegistrationResult> ReloadHotkeys(bool notifyFailures = false)
        {
            HotkeyReloads++;
            HotkeysSuspended = false;
            var failure = NextHotkeyFailure;
            NextHotkeyFailure = null;
            return failure == null ? [] : [failure];
        }
        public void SuspendHotkeys() => HotkeysSuspended = true;
        public void ReloadTrayIconVisibility() { }
        public void ApplyClipboardSettings() { }
        public void ApplyDiagnosticsSettings() { }
    }

    private static void AutomationToggleSavesOnceAndRestoringARejectedValueDoesNotRecurse()
    {
        // Exercise the real row factory on the existing design-test Application.
        var switchBox = SettingsLayout.CreateSwitch();
        var saves = 0;
        var reject = false;
        Action<bool> save = enabled =>
        {
            saves++;
            if (reject) switchBox.IsChecked = !enabled;
        };
        var host = SettingsLayout.CreateSwitchRow("记录剪贴板历史", string.Empty, switchBox, save);

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
            ["SettingsFieldFontSize"] = 12.5d,
            ["SettingsPasswordBox"] = new Style(typeof(PasswordBox)),
            ["SettingsTextBox"] = new Style(typeof(TextBox)),
            ["SettingsPasswordRevealButton"] = new Style(typeof(System.Windows.Controls.Button)),
            ["SettingsEyeGeometry"] = Geometry.Empty,
            ["SettingsEyeOffGeometry"] = Geometry.Empty,
            ["SettingsMutedBrush"] = Brushes.Gray,
            ["SettingsControlBrush"] = Brushes.DimGray,
            ["SettingsFaintBrush"] = Brushes.LightGray,
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
