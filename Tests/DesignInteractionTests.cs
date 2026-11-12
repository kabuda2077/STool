using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using STool.Modules.Clipboard;
using STool.Models;
using STool.Core;
using STool.Modules.Translation;
using Xunit;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace STool.Tests;

[CollectionDefinition("Design interactions", DisableParallelization = true)]
public sealed class DesignInteractionCollection { }

[Collection("Design interactions")]
public class DesignInteractionTests
{
    [Fact]
    public void DesktopInteractions_PreserveResultsDraftsAndKeyboardActions()
    {
        Exception? failure = null;
        var phase = "starting dispatcher";
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STool.UiTests", Guid.NewGuid().ToString("N"));
                TranslationPanel? window = null;
                try
                {
                    phase = "loading resources";
                    foreach (var name in new[] { "Colors", "Typography", "Icons", "Buttons", "Inputs", "Windows", "Navigation", "LoadingIndicator" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/STool;component/Styles/{name}.xaml") });
                    System.IO.Directory.CreateDirectory(root);
                    var config = new ConfigManager(System.IO.Path.Combine(root, "config.json"), _ => "", value => "v2:" + value, value => value.StartsWith("v2:"));
                    var service = new TestService();
                    using var manager = new TranslationManager(config, (_, _) => service);
                    phase = "constructing window";
                    window = new TranslationPanel(manager, () => { });
                    phase = "failure result";
                    var source = (TextBox)window.FindName("txtSource");
                    var target = (TextBox)window.FindName("txtTarget");
                    var actions = (FrameworkElement)window.FindName("actionBar");
                    var statusPanel = (FrameworkElement)window.FindName("resultStatusPanel");
                    Assert.Equal(Visibility.Collapsed, statusPanel.Visibility);
                    Assert.Equal("谷歌", ((RadioButton)window.FindName("btnProviderGoogle")).Content);
                    VerifyProviderSelection(window);
                    var translate = (Button)window.FindName("btnTranslateSource");
                    source.Text = "Hello";
                    service.Response = new TranslationResult { Success = false, ErrorMessage = "连接超时" };
                    await Translate(window);
                    Assert.Equal(string.Empty, target.Text);
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Contains("连接超时", ((TextBlock)window.FindName("resultStatus")).Text);
                    Assert.Equal(Visibility.Visible, statusPanel.Visibility);

                    phase = "successful result and edits";
                    service.Response = new TranslationResult { Success = true, TranslatedText = "你好" };
                    await Translate(window);
                    Assert.True(actions.IsEnabled);
                    Assert.Equal("你好", target.Text);
                    Assert.Equal(Visibility.Collapsed, statusPanel.Visibility);
                    Capture(window, "translation-success");
                    source.Text = "New source";
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Contains("原文已修改", ((TextBlock)window.FindName("resultStatus")).Text);

                    phase = "cancellation";
                    service.Pending = new TaskCompletionSource<TranslationResult>();
                    var pending = Translate(window);
                    Assert.Equal("取消翻译", translate.ToolTip);
                    Assert.Equal(Visibility.Collapsed, statusPanel.Visibility);
                    source.Text = "Edited while translating";
                    service.Pending.SetResult(new TranslationResult { Success = true, TranslatedText = "不应显示" });
                    await pending;
                    Assert.Equal(string.Empty, target.Text);
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Equal("翻译 (Ctrl+Enter)", translate.ToolTip);

                    phase = "settings drafts and accessibility";
                    await SettingsInteractionChecks.VerifyAllAsync();
                    phase = "long notification layout";
                    VerifyNotificationLayout();
                    phase = "clipboard keyboard and query recovery";
                    await VerifyClipboardAsync(config, root);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    phase = "cleanup";
                    window?.Close();
                    if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
                    app.Shutdown();
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    phase = "dispatcher shutdown";
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), $"WPF interaction test timed out: {phase}; {failure}");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyProviderSelection(TranslationPanel window)
    {
        var google = (RadioButton)window.FindName("btnProviderGoogle");
        var tencent = (RadioButton)window.FindName("btnProviderTencent");
        var peer = new RadioButtonAutomationPeer(tencent);
        var selection = (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!;
        selection.Select();
        Assert.True(selection.IsSelected);
        Assert.False(google.IsChecked);
        Assert.Equal(TranslationProvider.Tencent, typeof(TranslationPanel)
            .GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(google).GetPattern(PatternInterface.SelectionItem)!).Select();
    }

    private static void VerifyNotificationLayout()
    {
        var toast = new ToastNotification();
        try
        {
            var root = (FrameworkElement)toast.FindName("rootBorder");
            root.Margin = new Thickness(0);
            root.RenderTransform = Transform.Identity;
            var message = (TextBlock)toast.FindName("messageText");
            message.Text = "截图 Alt+1、翻译 Alt+2、剪贴板 Alt+3、设置 Alt+5、局域网传输 Alt+4 已被其他程序占用或格式无效。";
            message.Visibility = Visibility.Visible;
            root.Measure(new System.Windows.Size(1000, 1000));
            root.Arrange(new Rect(new System.Windows.Point(), root.DesiredSize));
            root.UpdateLayout();
            var origin = message.TransformToAncestor(root).Transform(new System.Windows.Point());
            Assert.True(origin.X + message.ActualWidth <= root.ActualWidth);
            Assert.True(message.ActualHeight > message.FontSize * 2);
            root.Opacity = 1;
            CaptureVisual(root, "notification-long");
        }
        finally { toast.Close(); }
    }

    private static async Task VerifyClipboardAsync(ConfigManager config, string root)
    {
        using var storage = new ClipboardStorage(System.IO.Path.Combine(root, "history.db"),
            System.IO.Path.Combine(root, "images"), System.IO.Path.Combine(root, "thumbnails"));
        using var manager = new ClipboardManager(config, storage, System.IO.Path.Combine(root, "images"));
        var panel = new ClipboardPanel(manager);
        try
        {
            var load = typeof(ClipboardPanel).GetMethod("LoadPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)load.Invoke(panel, new object[] { true })!;
            panel.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var category = (RadioButton)panel.FindName("tabAll");
            Assert.True(category.Focus());
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(ClipboardPanel).GetMethod("Window_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, new object[] { panel, key });
            Assert.False(key.Handled);

            var imagePath = System.IO.Path.Combine(root, "preview.png");
            var imageEncoder = new PngBitmapEncoder();
            imageEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96,
                PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
            using (var output = System.IO.File.Create(imagePath)) imageEncoder.Save(output);
            storage.Add(new ClipboardItem { Id = "preview", Type = ClipboardItemType.Image, ImagePath = imagePath });
            await (Task)load.Invoke(panel, new object[] { true })!;
            var search = (TextBox)panel.FindName("txtSearch");
            Assert.True(search.Focus());
            var previewKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, Key.F3)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            var keyHandler = typeof(ClipboardPanel).GetMethod("Window_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            keyHandler.Invoke(panel, new object[] { panel, previewKey });
            Assert.True(previewKey.Handled);
            Assert.True(((Button)panel.FindName("closeImagePreviewButton")).IsKeyboardFocused);
            var closeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            keyHandler.Invoke(panel, new object[] { panel, closeKey });
            Assert.True(search.IsKeyboardFocused);

            var connection = (SqliteConnection)typeof(ClipboardStorage)
                .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE clipboard_items RENAME TO unavailable_items";
                command.ExecuteNonQuery();
            }
            await (Task)load.Invoke(panel, new object[] { true })!;
            var error = (FrameworkElement)panel.FindName("queryErrorState");
            Assert.Equal(Visibility.Visible, error.Visibility);
            Capture(panel, "clipboard-query-error");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE unavailable_items RENAME TO clipboard_items";
                command.ExecuteNonQuery();
            }
            await (Task)load.Invoke(panel, new object[] { true })!;
            Assert.Equal(Visibility.Collapsed, error.Visibility);
            Assert.True(((ListBox)panel.FindName("itemsList")).IsEnabled);
        }
        finally { panel.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STOOL_UI_ARTIFACTS"))) return;
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        CaptureVisual(window, name);
    }

    private static void CaptureVisual(FrameworkElement visual, string name)
    {
        var directory = Environment.GetEnvironmentVariable("STOOL_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static Task Translate(TranslationPanel panel) => (Task)typeof(TranslationPanel)
        .GetMethod("TranslateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
    private static Task<bool> Copy(TranslationPanel panel) => (Task<bool>)typeof(TranslationPanel)
        .GetMethod("CopyTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;

    private sealed class TestService : ITranslationService
    {
        public TranslationResult Response { get; set; } = new();
        public TaskCompletionSource<TranslationResult>? Pending { get; set; }
        public bool IsAvailable() => true;
        public void Dispose() { }
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Pending?.Task ?? Task.FromResult(Response);
    }
}
