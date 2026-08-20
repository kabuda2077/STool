using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace STool.Core;

internal static class StartupWarmup
{
    public static void Schedule(Func<STool.Modules.Clipboard.ClipboardPanel?> clipboardFactory)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        // Screenshot has the largest first-use cost. Other panels wait until application idle.
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => WarmUp(
            "Screenshot",
            STool.Modules.Screenshot.CaptureOverlay.CreateForWarmUp)));
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => WarmUp(
            "Clipboard",
            clipboardFactory)));
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            MemoryDiagnostics.LogCheckpoint("StartupReady")));
    }

    private static void WarmUp(string name, Func<Window?> factory)
    {
        Window? window = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            window = factory();
            if (window != null)
                Log.Information("[WarmUp] {Name} prewarmed in {Ms}ms", name, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WarmUp] {Name} prewarm failed (non-fatal)", name);
        }
        finally
        {
            try
            {
                window?.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[WarmUp] {Name} cleanup failed", name);
            }
        }
    }
}
