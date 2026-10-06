using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Timer = System.Threading.Timer;
using Serilog;

namespace STool.Modules.Screenshot;

/// <summary>截图窗口的诊断日志:激活/关闭时序与 UI 线程卡顿探测。仅在设置中开启诊断时生效。</summary>
public partial class CaptureOverlay
{
    private readonly string _captureId = Guid.NewGuid().ToString("N")[..8];
    private Timer? _diagnosticTimer;
    private long _lastUiResponse;
    private int _probePending;
    private int _diagnosticsStopped;

    private void StartCaptureDiagnostics()
    {
        if (!_diagnosticsEnabled)
            return;

        _lastUiResponse = Stopwatch.GetTimestamp();
        Activated += (_, _) => LogCaptureState("Activated");
        Deactivated += (_, _) => LogCaptureState("Deactivated");
        LogCaptureState("Constructing");
        _diagnosticTimer = new Timer(_ => ProbeUiResponse(), null, 1000, 1000);
    }

    private void ProbeUiResponse()
    {
        if (Volatile.Read(ref _diagnosticsStopped) != 0)
            return;

        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiResponse));
        if (elapsed.TotalSeconds >= 2)
            Log.Warning("[CaptureDiagnostic] id={CaptureId} UI response delayed {DelayMs:F0}ms", _captureId, elapsed.TotalMilliseconds);

        // Keep at most one probe queued while the UI thread is blocked.
        if (Interlocked.CompareExchange(ref _probePending, 1, 0) != 0)
            return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (Volatile.Read(ref _diagnosticsStopped) != 0)
                    return;
                var delay = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiResponse));
                if (delay.TotalSeconds >= 2)
                    LogCaptureState($"UI recovered after {delay.TotalMilliseconds:F0}ms");
                Interlocked.Exchange(ref _lastUiResponse, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _probePending, 0);
            }));
        }
        catch (InvalidOperationException)
        {
            StopCaptureDiagnostics();
        }
    }

    private void StopCaptureDiagnostics()
    {
        Interlocked.Exchange(ref _diagnosticsStopped, 1);
        Interlocked.Exchange(ref _diagnosticTimer, null)?.Dispose();
    }

    private void LogCaptureState(string stage)
    {
        if (!_diagnosticsEnabled)
            return;

        Log.Information(
            "[CaptureDiagnostic] id={CaptureId} stage={Stage} active={Active} keyboardFocus={KeyboardFocus} visible={Visible} closing={Closing} ready={Ready} confirmed={Confirmed} tool={Tool} translating={Translating}",
            _captureId, stage, IsActive, IsKeyboardFocusWithin, IsVisible, _closing,
            _interactionReady, _confirmed, _currentTool, _translationCts != null);
    }
}
