using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STool.Core;
using STool.Models;
using STool.Views;
using STool.Views.Settings;
using Xunit;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace STool.Tests;

internal static class SettingsVisualChecks
{
    private const double LayoutTolerance = 1.5;
    private const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";
    private static readonly Color RowLineColor = Color.FromRgb(236, 236, 240);
    private static readonly PageSpec[] DefaultPages =
    [
        new("general", "btnGeneralSettings", [new(90, 106, 1), new(238, 262, 4), new(542, 54)]),
        new("clipboard", "btnClipboardSettings", [new(90, 106, 1), new(238, 88.5)]),
        new("ocr", "btnOcrSettings", [new(90, 54)]),
        new("translation", "btnTranslationSettings", [new(90, 106, 1), new(238, 54)]),
        new("accounts", "btnAccountsSettings", [new(90, 107.5, 1), new(243.5, 107.5, 1)])
    ];

    public static async Task VerifyAndCaptureAsync()
    {
        var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STool.SettingsLayout"));
        var directory = System.IO.Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            var config = new ConfigManager(System.IO.Path.Combine(directory, "config.json"), Decrypt, Encrypt, value => value.StartsWith("v2:"));
            AssertDemoDefaults(config.Get());
            ResetAccounts(config);
            var shell = new SettingsShell();
            var autoStart = false;
            var startupWrites = 0;
            window = new SettingsWindow(config, shell,
                () => new GeneralSettingsPanel(config, shell, () => autoStart, enabled =>
                {
                    autoStart = enabled;
                    startupWrites++;
                    return true;
                }),
                target => new AccountsSettingsPanel(config, target,
                    value => new EncryptedSetting(value, Encrypt, TryDecrypt)));
            window.Show();
            await LayoutAsync(window);
            await VerifyNavigationMotionAsync(window);
            await NavigateAsync(window, "btnGeneralSettings");
            await VerifySwitchMotionAsync(window);
            Assert.False(autoStart);
            Assert.Equal(2, startupWrites);

            foreach (var (width, height) in new[] { (660d, 560d), (600d, 480d) })
            {
                window.Width = width;
                window.Height = height;
                await LayoutAsync(window);
                foreach (var page in DefaultPages)
                {
                    await NavigateAsync(window, page.ButtonName);
                    await ScrollAsync(window, bottom: false);
                    AssertNavigation(window, page.ButtonName);
                    AssertCards(window, page.Cards);
                    CaptureWindow(window, page.Name + "-default");
                }
            }
            window.Width = 660;
            window.Height = 560;
            await NavigateAsync(window, "btnGeneralSettings");
            await ScrollAsync(window, bottom: true);
            CaptureWindow(window, "general-bottom");
            await VerifyPopupCapturesAsync(window);
            await VerifyTaskPageStatesAsync(window, config);
            await VerifyAccountStatesAsync(window, config);
        }
        finally
        {
            if (window != null)
            {
                SettingsAutoSaveController.GetController(CurrentPage(window))?.DiscardChanges();
                window.Close();
            }
            // The recursive cleanup is restricted to this resolved temporary fixture directory.
            Assert.Equal(temporaryRoot, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(directory)));
            System.IO.Directory.Delete(directory, true);
        }
    }

    private static void AssertDemoDefaults(AppConfig config)
    {
        Assert.Equal(OcrProvider.WindowsLocal, config.Ocr.Provider);
        Assert.Equal(TranslationProvider.Google, config.Translation.Provider);
        Assert.Equal(ScreenshotTranslationMode.Fast, config.Translation.ScreenshotMode);
        Assert.Equal("gpt-6-luna", config.Accounts.AiVision.Model);
        Assert.Equal("gpt-6-luna", config.Accounts.AiText.Model);
        Assert.Null(config.Accounts.AiVision.ApiKeyEncrypted);
        Assert.Null(config.Accounts.AiText.ApiKeyEncrypted);
        Assert.Equal(new[] { "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe" }, config.Clipboard.ExcludedApps);
    }

    private static void ResetAccounts(ConfigManager config) => config.Update(value =>
    {
        value.Accounts = new AccountsConfig();
        value.Accounts.AiVision.ApiUrlEncrypted = Encrypt(OpenAiEndpoint);
        value.Accounts.AiText.ApiUrlEncrypted = Encrypt(OpenAiEndpoint);
    });

    private static async Task VerifyNavigationMotionAsync(SettingsWindow window)
    {
        var slider = (Border)window.FindName("navigationSlider");
        var scale = (ScaleTransform)window.FindName("navigationScale");
        var start = Bounds(slider, window);
        var destination = Bounds((Button)window.FindName("btnAccountsSettings"), window);
        Assert.True(destination.Left > start.Right);
        Assert.True(destination.Width > start.Width + 10);
        if (SystemParameters.ClientAreaAnimation)
        {
            await ObserveMiddleFrameAsync(() => Click((Button)window.FindName("btnAccountsSettings")), () =>
            {
                var middle = Bounds(slider, window);
                AssertBetween(middle.Left, start.Left, destination.Left, "navigation position");
                AssertBetween(middle.Width, start.Width, destination.Width, "navigation width");
                AssertBetween(slider.ActualWidth, start.Width, destination.Width, "navigation layout width");
                Near(1, scale.ScaleX, "navigation midpoint scale", .001);
                var destinationColor = Assert.IsType<SolidColorBrush>(((Button)window.FindName("btnAccountsSettings")).Foreground).Color;
                Assert.NotEqual(Color.FromRgb(99, 99, 107), destinationColor);
                Assert.NotEqual(Color.FromRgb(28, 28, 33), destinationColor);
                CaptureWindow(window, "navigation-midpoint");
            });
        }
        else Click((Button)window.FindName("btnAccountsSettings"));
        await SettleAsync(window);
        AssertNavigation(window, "btnAccountsSettings");
        Near(1, scale.ScaleX, "navigation scale", .001);
    }

    private static async Task VerifySwitchMotionAsync(SettingsWindow window)
    {
        var general = Assert.IsType<GeneralSettingsPanel>(CurrentPage(window));
        var toggle = Field<CheckBox>(general, "_chkAutoStart");
        toggle.ApplyTemplate();
        var track = Assert.IsType<Border>(toggle.Template.FindName("Track", toggle));
        var thumb = Assert.IsType<Border>(toggle.Template.FindName("Thumb", toggle));
        double ThumbPosition() => Assert.IsType<TranslateTransform>(thumb.RenderTransform).X;
        var off = Color.FromRgb(227, 227, 232);
        var on = Color.FromRgb(28, 28, 33);
        Near(32, track.ActualWidth, "switch width", .01);
        Near(19, track.ActualHeight, "switch height", .01);
        Near(15, thumb.ActualWidth, "switch thumb width", .01);
        Near(15, thumb.ActualHeight, "switch thumb height", .01);
        Assert.Equal(off, Assert.IsType<SolidColorBrush>(track.Background).Color);
        Near(0, ThumbPosition(), "unchecked switch", .01);
        if (SystemParameters.ClientAreaAnimation)
        {
            await ObserveMiddleFrameAsync(() => toggle.IsChecked = true, () =>
            {
                AssertBetween(ThumbPosition(), 0, 13, "switch thumb");
                var middle = Assert.IsType<SolidColorBrush>(track.Background).Color;
                Assert.NotEqual(off, middle);
                Assert.NotEqual(on, middle);
                CaptureWindow(window, "switch-midpoint");
            });
        }
        else toggle.IsChecked = true;
        await SettleAsync(window);
        Near(13, ThumbPosition(), "checked switch", .01);
        Assert.Equal(on, Assert.IsType<SolidColorBrush>(track.Background).Color);
        var toast = (Border)window.FindName("savedToast");
        Assert.Equal(Visibility.Visible, toast.Visibility);
        Near(26.5, toast.ActualHeight, "saved feedback height");
        Near(12, window.ActualHeight - Bounds(toast, window).Bottom, "saved feedback bottom gap");
        CaptureWindow(window, "general-saved-toast");
        toggle.IsChecked = false;
        await SettleAsync(window);
        Near(0, ThumbPosition(), "restored switch", .01);
        Assert.Equal(off, Assert.IsType<SolidColorBrush>(track.Background).Color);
    }

    private static async Task VerifyTaskPageStatesAsync(SettingsWindow window, ConfigManager config)
    {
        foreach (var (provider, state, cards) in new[]
        {
            (OcrProvider.WindowsLocal, "ocr-more-expanded", new[] { new CardSpec(90, 54), new CardSpec(188, 54) }),
            (OcrProvider.Tencent, "ocr-tencent-expanded", new[] { new CardSpec(90, 87), new CardSpec(221, 107, 1, [1]) }),
            (OcrProvider.AI, "ocr-ai-expanded", new[] { new CardSpec(90, 140, 1), new CardSpec(274, 107, 1, [1]) })
        })
        {
            config.Update(value => value.Ocr.Provider = provider);
            await NavigateAsync(window, "btnOcrSettings");
            var page = Assert.IsType<OcrSettingsPanel>(CurrentPage(window));
            Descendants<Expander>(page).Single().IsExpanded = true;
            await SettleAsync(window);
            await ScrollAsync(window, bottom: false);
            AssertCards(window, cards);
            CaptureWindow(window, state);
        }
        var recognitionModel = Field<Button>(CurrentPage(window), "_modelButton");
        Assert.Equal("配置识别模型", System.Windows.Automation.AutomationProperties.GetName(recognitionModel));
        Click(recognitionModel);
        await SettleAsync(window);
        AssertNavigation(window, "btnAccountsSettings");
        Assert.True(AccountExpander(Assert.IsType<AccountsSettingsPanel>(CurrentPage(window)), ServiceAccountTarget.AiVision).IsExpanded);

        config.Update(value =>
        {
            value.Ocr.Provider = OcrProvider.WindowsLocal;
            value.Translation.Provider = TranslationProvider.Tencent;
        });
        await NavigateAsync(window, "btnTranslationSettings");
        await ScrollAsync(window, bottom: false);
        AssertCards(window, [new(90, 139, 1, [86]), new(271, 54)]);
        CaptureWindow(window, "translation-tencent");

        config.Update(value =>
        {
            value.Ocr.Provider = OcrProvider.WindowsLocal;
            value.Translation.Provider = TranslationProvider.OpenAI;
            value.Translation.ScreenshotMode = ScreenshotTranslationMode.Fast;
        });
        await NavigateAsync(window, "btnTranslationSettings");
        await ScrollAsync(window, bottom: false);
        AssertCards(window, [new(90, 192, 2, [53, 139]), new(324, 54)]);
        CaptureWindow(window, "translation-ai");
        Field<ComboBox>(CurrentPage(window), "_cmbScreenshotMode").SelectedIndex = 1;
        // Reopening through the real navigation also verifies persistence and clears transient saved feedback.
        await NavigateAsync(window, "btnTranslationSettings");
        await ScrollAsync(window, bottom: false);
        Assert.Equal(ScreenshotTranslationMode.Smart, config.Get().Translation.ScreenshotMode);
        AssertCards(window, [new(90, 192, 2, [53, 139]), new(324, 140, 1)]);
        CaptureWindow(window, "translation-ai-smart");
        var translationModel = Field<Button>(CurrentPage(window), "_smartModelButton");
        Assert.Equal("配置翻译模型", System.Windows.Automation.AutomationProperties.GetName(translationModel));
        Click(translationModel);
        await SettleAsync(window);
        AssertNavigation(window, "btnAccountsSettings");
        Assert.True(AccountExpander(Assert.IsType<AccountsSettingsPanel>(CurrentPage(window)), ServiceAccountTarget.AiText).IsExpanded);
    }

    private static async Task VerifyAccountStatesAsync(SettingsWindow window, ConfigManager config)
    {
        foreach (var (target, state, detailHeight) in new[]
        {
            (ServiceAccountTarget.TencentOcr, "accounts-cloud-ocr-expanded", 209d),
            (ServiceAccountTarget.AiVision, "accounts-vision-expanded", 261d),
            (ServiceAccountTarget.TencentTranslation, "accounts-cloud-translation-expanded", 209d),
            (ServiceAccountTarget.AiText, "accounts-text-expanded", 261d)
        })
        {
            await OpenAccountAsync(window, target);
            var accounts = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
            Assert.Single(AccountExpanders(accounts), expander => expander.IsExpanded);
            AssertAccountDetail(window, accounts, target, detailHeight);
            CaptureWindow(window, state);
            await ScrollAsync(window, bottom: true);
            CaptureWindow(window, state + "-bottom");
        }

        var panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        Field<AiServiceSettingsSection>(panel, "_aiVision").ApiKey.Password = "reference-only-fixture";
        await OpenAccountAsync(window, ServiceAccountTarget.AiText);
        panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        var offer = Field<object>(panel, "_textReuse");
        Assert.Equal(Visibility.Visible, Property<StackPanel>(offer, "Panel").Visibility);
        Assert.False(Property<CheckBox>(offer, "CheckBox").IsChecked);
        AssertAccountDetail(window, panel, ServiceAccountTarget.AiText, 306);
        CaptureWindow(window, "accounts-text-reuse-offer");
        Property<CheckBox>(offer, "CheckBox").IsChecked = true;
        await OpenAccountAsync(window, ServiceAccountTarget.AiText);
        panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        var text = Field<AiServiceSettingsSection>(panel, "_aiText");
        Assert.Equal(AccountReuseSource.Ocr, config.Get().Accounts.AiText.ReuseFrom);
        Assert.Equal(Visibility.Collapsed, Field<StackPanel>(text, "_connectionFields").Visibility);
        Assert.True(text.Model.IsVisible && text.Model.IsEnabled);
        Assert.Equal("gpt-6-luna", text.ModelName);
        AssertAccountDetail(window, panel, ServiceAccountTarget.AiText, 227.5);
        CaptureWindow(window, "accounts-text-reuse-active");
        await ScrollAsync(window, bottom: true);
        Near(53, ((ScrollViewer)window.FindName("contentScroll")).VerticalOffset, "reused account bottom scroll", .01);
        var modelEditor = Assert.IsType<TextBox>(text.Model.Template.FindName("PART_EditableTextBox", text.Model));
        modelEditor.Focus();
        modelEditor.SelectAll();
        await LayoutAsync(window);
        AssertSelectedTextReadable(modelEditor);
        CaptureWindow(window, "accounts-text-reuse-active-bottom");
        Field<AiServiceSettingsSection>(panel, "_aiVision").ApiKey.Password = string.Empty;
        await OpenAccountAsync(window, ServiceAccountTarget.AiText);
        panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        offer = Field<object>(panel, "_textReuse");
        Assert.True(Property<CheckBox>(offer, "CheckBox").IsChecked);
        Assert.Equal(Visibility.Visible, Field<TextBlock>(offer, "_warning").Visibility);
        // The configuration link focuses the missing source credential before inspecting the target warning.
        var sourceSection = Field<AiServiceSettingsSection>(panel, "_aiVision");
        Assert.True(AccountExpander(panel, ServiceAccountTarget.AiVision).IsExpanded);
        Assert.True(sourceSection.ApiKey.IsKeyboardFocusWithin);
        AccountExpander(panel, ServiceAccountTarget.AiText).IsExpanded = true;
        await SettleAsync(window);
        await ScrollAsync(window, bottom: false);
        AssertAccountDetail(window, panel, ServiceAccountTarget.AiText, 247.5);
        CaptureWindow(window, "accounts-text-reuse-warning");

        SettingsAutoSaveController.GetController(panel)!.DiscardChanges();
        ResetAccounts(config);
        config.Update(value =>
        {
            value.Accounts.TencentOcr.SecretIdEncrypted = Encrypt("reference-id");
            value.Accounts.TencentOcr.SecretKeyEncrypted = Encrypt("reference-only-fixture");
        });
        await OpenAccountAsync(window, ServiceAccountTarget.TencentTranslation);
        panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        var cloud = Field<object>(panel, "_tencentTranslation");
        var cloudReuse = Property<object>(cloud, "Reuse");
        Assert.Equal(Visibility.Visible, Property<StackPanel>(cloudReuse, "Panel").Visibility);
        AssertAccountDetail(window, panel, ServiceAccountTarget.TencentTranslation, 254);
        CaptureWindow(window, "accounts-cloud-reuse-offer");
        Property<CheckBox>(cloudReuse, "CheckBox").IsChecked = true;
        await OpenAccountAsync(window, ServiceAccountTarget.TencentTranslation);
        panel = Assert.IsType<AccountsSettingsPanel>(CurrentPage(window));
        Assert.Equal(AccountReuseSource.Ocr, config.Get().Accounts.TencentTranslation.ReuseFrom);
        AssertAccountDetail(window, panel, ServiceAccountTarget.TencentTranslation, 175.5);
        CaptureWindow(window, "accounts-cloud-reuse-active");
    }

    private static async Task VerifyPopupCapturesAsync(SettingsWindow window)
    {
        foreach (var name in new[] { "PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton" })
        {
            var button = Assert.IsType<Button>(window.Template.FindName(name, window));
            Assert.Null(button.ToolTip);
            Assert.NotEmpty(System.Windows.Automation.AutomationProperties.GetName(button));
        }
        await NavigateAsync(window, "btnGeneralSettings");
        await ScrollAsync(window, bottom: false);
        var tray = Field<CheckBox>(CurrentPage(window), "_chkHideTrayIcon");
        var tip = Assert.IsType<ToolTip>(tray.ToolTip);
        Assert.Same(tray, tip.PlacementTarget);
        Assert.Same(tip, ((FrameworkElement)tip.PlacementTarget).ToolTip);
        Assert.Null(((FrameworkElement)tray.Parent).ToolTip);
        Assert.Null(((FrameworkElement)((FrameworkElement)tray.Parent).Parent).ToolTip);
        try
        {
            tip.IsOpen = true;
            await LayoutAsync(window);
            var tooltipPopup = Field<Popup>(tip, "_parentPopup");
            Assert.Equal(PopupAnimation.None, tooltipPopup.PopupAnimation);
            var content = Descendants<Border>(tip).Single(border => border.BorderThickness == new Thickness(1));
            Near(35, content.ActualHeight, "settings tooltip height");
            Assert.True(content.ActualWidth is > 0 and <= 320 + LayoutTolerance);
            CapturePopup(tip, "general-tray-tooltip-popup");
            tray.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, Environment.TickCount, System.Windows.Input.Key.A)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.False(tip.IsOpen);
            tip.IsOpen = true;
            await ScrollAsync(window, bottom: true);
            Assert.False(tip.IsOpen);
        }
        finally { tip.IsOpen = false; }
        await NavigateAsync(window, "btnClipboardSettings");
        var limit = Field<ComboBox>(CurrentPage(window), "_cmbMaxEntries");
        try
        {
            limit.IsDropDownOpen = true;
            await LayoutAsync(window);
            var popup = Assert.IsType<Popup>(limit.Template.FindName("PART_Popup", limit));
            var content = Assert.IsType<Border>(limit.Template.FindName("Menu", limit));
            Assert.True(popup.IsOpen);
            Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
            Near(120, content.ActualHeight, "four-option menu height");
            Assert.True(content.ActualWidth >= 120 - LayoutTolerance);
            CapturePopup(Assert.IsAssignableFrom<FrameworkElement>(popup.Child), "clipboard-history-limit-menu-popup");
        }
        finally { limit.IsDropDownOpen = false; }
        var limitTip = Assert.IsType<ToolTip>(limit.ToolTip);
        try
        {
            limitTip.IsOpen = true;
            await LayoutAsync(window);
            Assert.Same(limit, limitTip.PlacementTarget);
            CapturePopup(limitTip, "clipboard-history-limit-tooltip-popup");
        }
        finally { limitTip.IsOpen = false; }
    }

    private static void AssertCards(SettingsWindow window, CardSpec[] expected)
    {
        var page = CurrentPage(window);
        var style = (Style)window.FindResource("SettingsFlatSection");
        var cards = Descendants<Border>(page).Where(border => border.IsVisible && ReferenceEquals(border.Style, style))
            .OrderBy(border => Bounds(border, window).Top).ToArray();
        Assert.Equal(expected.Length, cards.Length);
        for (var index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            var actual = Bounds(card, window);
            Near(16, actual.Left, $"card {index} left");
            Near(window.Width - 16, actual.Right, $"card {index} right");
            Near(expected[index].Top, actual.Top, $"card {index} top");
            Near(expected[index].Height, actual.Height, $"card {index} height");
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.Equal(new CornerRadius(12), card.CornerRadius);
            var lines = RowLines(card).ToArray();
            Assert.Equal(expected[index].Lines, lines.Length);
            foreach (var line in lines)
                AssertLine(window, line, actual.Left + 1, actual.Right - 1);
            var positions = lines.Select(line => LineBounds(line, window).Top).Order().ToArray();
            for (var lineIndex = 0; lineIndex < positions.Length; lineIndex++)
            {
                var expectedTop = expected[index].Top + (expected[index].SeparatorOffsets is { } offsets
                    ? offsets[lineIndex] : 53 + 52 * lineIndex);
                Near(expectedTop, positions[lineIndex], $"card {index} separator {lineIndex} top", .01);
            }
        }
    }

    private static void AssertAccountDetail(SettingsWindow window, AccountsSettingsPanel accounts, ServiceAccountTarget target, double height)
    {
        var expander = AccountExpander(accounts, target);
        Assert.True(expander.IsExpanded);
        var detail = Assert.IsType<Border>(expander.Template.FindName("Detail", expander));
        var bounds = Bounds(detail, window);
        var forRecognition = target is ServiceAccountTarget.TencentOcr or ServiceAccountTarget.AiVision;
        var cloud = target is ServiceAccountTarget.TencentOcr or ServiceAccountTarget.TencentTranslation;
        var cardTop = forRecognition ? 90 : 243.5;
        var cardStyle = (Style)window.FindResource("SettingsFlatSection");
        var card = Descendants<Border>(accounts).Single(border => ReferenceEquals(border.Style, cardStyle) &&
            Descendants<Expander>(border).Contains(expander));
        var cardBounds = Bounds(card, window);
        Near(cardTop, cardBounds.Top, target + " card top");
        Near(height + 107.5, cardBounds.Height, target + " expanded card height");
        Near(17, bounds.Left, target + " detail left");
        Near(window.Width - 17, bounds.Right, target + " detail right");
        Near(cardTop + (cloud ? 53 : 107.5), bounds.Top, target + " detail top");
        Near(height, bounds.Height, target + " detail height");
        Assert.Equal(Color.FromRgb(249, 249, 251), Assert.IsType<SolidColorBrush>(detail.Background).Color);
        var lines = RowLines(detail).ToArray();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
            AssertLine(window, line, bounds.Left + 14, bounds.Right - 14);
    }

    private static IEnumerable<Border> RowLines(DependencyObject scope)
    {
        var rowBrush = (Brush)((FrameworkElement)scope).FindResource("SettingsRowLineBrush");
        return Descendants<Border>(scope).Where(border => border.IsVisible &&
            (border.Name == "SettingsRowDivider" ||
             border.Height is > 0 and <= 1.5 && ReferenceEquals(border.Background, rowBrush) ||
             border.BorderThickness.Left == 0 && border.BorderThickness.Right == 0 &&
             border.BorderThickness.Top + border.BorderThickness.Bottom > 0 && ReferenceEquals(border.BorderBrush, rowBrush)));
    }

    private static void AssertLine(SettingsWindow window, Border line, double left, double right)
    {
        var bounds = LineBounds(line, window);
        Near(left, bounds.Left, "separator left");
        Near(right, bounds.Right, "separator right");
        var brush = line.BorderThickness.Top + line.BorderThickness.Bottom > 0 ? line.BorderBrush : line.Background;
        Assert.Equal(RowLineColor, Assert.IsType<SolidColorBrush>(brush).Color);
        Near(1, bounds.Height, "separator thickness", .01);
    }

    private static Rect LineBounds(Border line, Visual ancestor)
    {
        var bounds = Bounds(line, ancestor);
        if (line.BorderThickness.Top > 0) bounds.Height = line.BorderThickness.Top;
        else if (line.BorderThickness.Bottom > 0)
        {
            bounds.Y = bounds.Bottom - line.BorderThickness.Bottom;
            bounds.Height = line.BorderThickness.Bottom;
        }
        return bounds;
    }

    private static void AssertNavigation(SettingsWindow window, string selectedName)
    {
        var tabs = (StackPanel)window.FindName("navigationTabs");
        var selected = (Button)window.FindName(selectedName);
        Assert.True(NavigationSelection.GetIsSelected(selected));
        Assert.Single(tabs.Children.OfType<Button>(), NavigationSelection.GetIsSelected);
        var buttons = tabs.Children.OfType<Button>().ToArray();
        var widths = new[] { 49d, 61.5, 74, 49, 74 };
        Assert.Equal(widths.Length, buttons.Length);
        Near(window.ActualWidth / 2, Bounds(tabs, window).Left + tabs.ActualWidth / 2, "navigation center");
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            var bounds = Bounds(button, window);
            Near(15, bounds.Top, button.Name + " top");
            Near(24, bounds.Height, button.Name + " height");
            Near(widths[index], bounds.Width, button.Name + " width");
            Assert.Equal(NavigationColor(button), Assert.IsType<SolidColorBrush>(button.Foreground).Color);
            Assert.True(bounds.Left >= 64 && bounds.Right <= window.ActualWidth - 102,
                $"{button.Name} overlaps the brand or caption buttons");
        }
        var slider = Bounds((Border)window.FindName("navigationSlider"), window);
        var target = Bounds(selected, window);
        Near(target.Left, slider.Left, "selected navigation left");
        Near(target.Width, slider.Width, "selected navigation width");
        Near(target.Top, slider.Top, "selected navigation top");
        Near(target.Height, slider.Height, "selected navigation height");
    }

    private static async Task NavigateAsync(SettingsWindow window, string name)
    {
        var button = (Button)window.FindName(name);
        Click(button);
        await SettleAsync(window);
    }

    private static async Task OpenAccountAsync(SettingsWindow window, ServiceAccountTarget target)
    {
        typeof(SettingsWindow).GetMethod("OpenAccount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [target]);
        await SettleAsync(window);
        await ScrollAsync(window, bottom: false);
        AssertNavigation(window, "btnAccountsSettings");
    }

    private static async Task SettleAsync(SettingsWindow window)
    {
        await LayoutAsync(window);
        if (!MotionSettled(window))
        {
            var settled = new TaskCompletionSource();
            void OnRendering(object? sender, EventArgs args)
            {
                if (MotionSettled(window)) settled.TrySetResult();
            }
            CompositionTarget.Rendering += OnRendering;
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
            finally { CompositionTarget.Rendering -= OnRendering; }
        }
        await LayoutAsync(window);
    }

    private static bool MotionSettled(SettingsWindow window)
    {
        var selected = ((StackPanel)window.FindName("navigationTabs")).Children.OfType<Button>()
            .Single(NavigationSelection.GetIsSelected);
        var slider = Bounds((Border)window.FindName("navigationSlider"), window);
        var target = Bounds(selected, window);
        if (Math.Abs(slider.Left - target.Left) > .01 || Math.Abs(slider.Width - target.Width) > .01) return false;
        foreach (var button in ((StackPanel)window.FindName("navigationTabs")).Children.OfType<Button>())
            if (button.Foreground is SolidColorBrush brush && brush.Color != NavigationColor(button)) return false;
        foreach (var toggle in Descendants<ToggleButton>(CurrentPage(window)).Where(toggle => toggle.IsVisible))
        {
            if (toggle.Template.FindName("Chevron", toggle) is FrameworkElement { RenderTransform: RotateTransform rotation } &&
                Math.Abs(rotation.Angle - (toggle.IsChecked == true ? 0 : -90)) > .01) return false;
            if (toggle.Template.FindName("Thumb", toggle) is Border { RenderTransform: TranslateTransform translate } &&
                Math.Abs(translate.X - (toggle.IsChecked == true ? 13 : 0)) > .01) return false;
        }
        return true;
    }

    private static Color NavigationColor(Button button) => ((SolidColorBrush)button.FindResource(
        NavigationSelection.GetIsSelected(button) ? "SettingsForegroundBrush"
        : button.IsMouseOver ? "SettingsSecondaryBrush" : "SettingsControlBrush")).Color;

    private static async Task ObserveMiddleFrameAsync(Action begin, Action inspect)
    {
        var observed = new TaskCompletionSource();
        TimeSpan? firstFrame = null;
        void OnRendering(object? sender, EventArgs args)
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            firstFrame ??= time;
            var elapsed = time - firstFrame.Value;
            if (elapsed < TimeSpan.FromMilliseconds(50) || observed.Task.IsCompleted) return;
            try
            {
                // Sample inside the frame, before a delayed Dispatcher continuation can see HoldEnd.
                Assert.True(elapsed < TimeSpan.FromMilliseconds(200), "No intermediate animation frame was rendered.");
                inspect();
                observed.TrySetResult();
            }
            catch (Exception error) { observed.TrySetException(error); }
        }
        CompositionTarget.Rendering += OnRendering;
        try
        {
            begin();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private static async Task ScrollAsync(SettingsWindow window, bool bottom)
    {
        var scroll = (ScrollViewer)window.FindName("contentScroll");
        if (bottom) scroll.ScrollToEnd(); else scroll.ScrollToTop();
        await LayoutAsync(window);
    }

    private static async Task LayoutAsync(SettingsWindow window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor)
        .TransformBounds(new Rect(new System.Windows.Point(), new System.Windows.Size(element.ActualWidth, element.ActualHeight)));
    private static FrameworkElement CurrentPage(SettingsWindow window) => (FrameworkElement)((Grid)window.FindName("contentPanel")).Children[0];
    private static Expander AccountExpander(AccountsSettingsPanel panel, ServiceAccountTarget target) =>
        (Expander)typeof(AccountsSettingsPanel).GetMethod("GetExpander", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [target])!;
    private static IEnumerable<Expander> AccountExpanders(AccountsSettingsPanel panel) =>
        Enum.GetValues<ServiceAccountTarget>().Select(target => AccountExpander(panel, target));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name)!.GetValue(owner)!;
    private static string Encrypt(string value) => "v2:" + value;
    private static string Decrypt(string value) => value[3..];
    private static bool TryDecrypt(string? value, out string plain) { plain = value == null ? string.Empty : Decrypt(value); return true; }
    private static void Near(double expected, double actual, string message, double tolerance = LayoutTolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected:0.###}, actual {actual:0.###}");
    private static void AssertBetween(double actual, double start, double finish, string message) =>
        Assert.True(actual > Math.Min(start, finish) + .01 && actual < Math.Max(start, finish) - .01,
            $"{message} jumped to an endpoint: start {start:0.###}, sample {actual:0.###}, finish {finish:0.###}");

    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void CaptureWindow(Window window, string state)
    {
        CaptureVisual(window, $"{state}-{window.Width:0}x{window.Height:0}");
        var outputDirectory = Environment.GetEnvironmentVariable("STOOL_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        var metrics = Descendants<FrameworkElement>(window).Where(element => element.IsVisible &&
            element is TextBlock or TextBox or ComboBox or PasswordBox).Select(element => new
        {
            type = element.GetType().Name,
            text = element is TextBlock label ? label.Text : element is TextBox input ? input.Text : string.Empty,
            bounds = Bounds(element, window).ToString(System.Globalization.CultureInfo.InvariantCulture),
            font = element is Control control ? control.FontFamily.ToString() : ((TextBlock)element).FontFamily.ToString(),
            fontSize = element is Control fontControl ? fontControl.FontSize : ((TextBlock)element).FontSize,
            glyphFonts = GlyphFonts(element).Distinct().ToArray()
        });
        System.IO.File.WriteAllText(System.IO.Path.Combine(outputDirectory, $"settings-native-{state}-{window.Width:0}x{window.Height:0}-metrics.json"),
            System.Text.Json.JsonSerializer.Serialize(metrics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<string> GlyphFonts(Visual element)
    {
        static IEnumerable<string> Fonts(Drawing? drawing)
        {
            if (drawing is GlyphRunDrawing glyph)
                yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
            else if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var font in Fonts(child)) yield return font;
        }
        foreach (var font in Fonts(VisualTreeHelper.GetDrawing(element))) yield return font;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (VisualTreeHelper.GetChild(element, index) is Visual child)
                foreach (var font in GlyphFonts(child)) yield return font;
    }

    private static void CapturePopup(FrameworkElement content, string name) =>
        CaptureVisual(Assert.IsAssignableFrom<FrameworkElement>(PresentationSource.FromVisual(content)!.RootVisual), name);

    private static void AssertSelectedTextReadable(TextBox editor)
    {
        Assert.Equal(editor.Text.Length, editor.SelectionLength);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var darkPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index + 3] > 192 && pixels[index] < 128 && pixels[index + 1] < 128 && pixels[index + 2] < 128)
                darkPixels++;
        Assert.True(darkPixels > 20, "Selecting the model obscured its text.");
    }

    private static void CaptureVisual(FrameworkElement visual, string name)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("STOOL_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        System.IO.Directory.CreateDirectory(outputDirectory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(outputDirectory, "settings-native-" + name + ".png"));
        encoder.Save(output);
    }

    private sealed record PageSpec(string Name, string ButtonName, CardSpec[] Cards);
    private readonly record struct CardSpec(double Top, double Height, int Lines = 0, double[]? SeparatorOffsets = null);

    private sealed class SettingsShell : IAppShell
    {
        public IReadOnlyList<HotkeyRegistrationResult> ReloadHotkeys(bool notifyFailures = false) => [];
        public void SuspendHotkeys() { }
        public void ReloadTrayIconVisibility() { }
        public void ApplyClipboardSettings() { }
        public void ApplyDiagnosticsSettings() { }
    }
}
