using System;
using System.Threading;

namespace STool.Core;

public class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _activationWaitHandle;
    private bool _owned;
    private bool _disposed;

    public SingleInstance(string appName)
    {
        var mutexName = $"Global\\STool_{appName}_{Environment.UserName}";
        var eventName = $"Global\\STool_{appName}_{Environment.UserName}_Activate";
        _mutex = new Mutex(true, mutexName, out _owned);
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public bool IsFirstInstance => _owned;

    public void StartActivationListener(Action onActivated)
    {
        if (!_owned)
        {
            return;
        }

        _activationWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) =>
            {
                if (!_disposed)
                {
                    onActivated();
                }
            },
            null,
            Timeout.InfiniteTimeSpan,
            false
        );
    }

    public void ActivateFirstInstance()
    {
        _activationEvent.Set();
    }

    public void Dispose()
    {
        _disposed = true;
        _activationWaitHandle?.Unregister(null);
        _activationEvent.Dispose();

        if (_owned)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
