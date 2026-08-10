namespace STool.Modules.LanTransfer;

internal sealed class AsyncPauseGate
{
    private volatile TaskCompletionSource _source = CreateCompletedSource();

    public bool IsPaused => !_source.Task.IsCompleted;

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        var task = _source.Task;
        return task.IsCompleted ? Task.CompletedTask : task.WaitAsync(cancellationToken);
    }

    public void Pause()
    {
        while (true)
        {
            var current = _source;
            if (!current.Task.IsCompleted)
                return;
            var replacement = CreateSource();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _source, replacement, current), current))
                return;
        }
    }

    public void Resume() => _source.TrySetResult();

    private static TaskCompletionSource CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = CreateSource();
        source.SetResult();
        return source;
    }
}
