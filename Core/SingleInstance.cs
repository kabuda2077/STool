using System;
using System.Threading;

namespace STool.Core;

public class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    public SingleInstance(string appName)
    {
        var mutexName = $"Global\\STool_{appName}_{Environment.UserName}";
        _mutex = new Mutex(true, mutexName, out _owned);
    }

    public bool IsFirstInstance => _owned;

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
