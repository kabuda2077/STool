using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    public void Translation_FailureEditsAndCancellationNeverExposeCopyableResults()
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
                    var actions = (StackPanel)window.FindName("actionBar");
                    var translate = (Button)window.FindName("btnTranslateSource");
                    source.Text = "Hello";
                    service.Response = new TranslationResult { Success = false, ErrorMessage = "连接超时" };
                    await Translate(window);
                    Assert.Equal(string.Empty, target.Text);
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Contains("连接超时", ((TextBlock)window.FindName("resultStatus")).Text);

                    phase = "successful result and edits";
                    service.Response = new TranslationResult { Success = true, TranslatedText = "你好" };
                    await Translate(window);
                    Assert.True(actions.IsEnabled);
                    Assert.Equal("你好", target.Text);
                    source.Text = "New source";
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Contains("原文已修改", ((TextBlock)window.FindName("resultStatus")).Text);

                    phase = "cancellation";
                    service.Pending = new TaskCompletionSource<TranslationResult>();
                    var pending = Translate(window);
                    Assert.Equal("取消", translate.Content);
                    source.Text = "Edited while translating";
                    service.Pending.SetResult(new TranslationResult { Success = true, TranslatedText = "不应显示" });
                    await pending;
                    Assert.Equal(string.Empty, target.Text);
                    Assert.False(actions.IsEnabled);
                    Assert.False(await Copy(window));
                    Assert.Equal("翻译", translate.Content);
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
