namespace STool.Core;

internal sealed class LatestOperationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _current = new();
    private bool _finalized;
    private bool _disposed;

    public async Task RunLatestAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource current;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_finalized)
                throw new InvalidOperationException("The operation coordinator has been finalized.");
            var previous = _current;
            current = new CancellationTokenSource();
            _current = current;
            previous.Cancel();
            previous.Dispose();
        }

        try
        {
            await _gate.WaitAsync(current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return;
        }

        try
        {
            current.Token.ThrowIfCancellationRequested();
            await operation(current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunFinalAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource current;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_finalized)
                throw new InvalidOperationException("The operation coordinator has already been finalized.");
            _finalized = true;
            current = _current;
            current.Cancel();
        }

        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _current.Cancel();
            _current.Dispose();
        }
        _gate.Dispose();
    }
}
