using STool.Core;
using Xunit;

namespace STool.Tests;

public class LatestOperationCoordinatorTests
{
    [Fact]
    public async Task RunLatestAsync_CancelsPreviousOperationAndRunsNewest()
    {
        using var coordinator = new LatestOperationCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        var first = coordinator.RunLatestAsync(async token =>
        {
            firstStarted.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { firstCanceled.SetResult(); }
        });
        await firstStarted.Task;

        var second = coordinator.RunLatestAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second);
        Assert.True(secondRan);
        Assert.True(firstCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task RunLatestAsync_CancelsOperationWaitingForGate()
    {
        using var coordinator = new LatestOperationCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleRan = false;
        var latestRan = false;

        var first = coordinator.RunLatestAsync(async _ =>
        {
            firstStarted.SetResult();
            await release.Task;
        });
        await firstStarted.Task;
        var middle = coordinator.RunLatestAsync(_ =>
        {
            middleRan = true;
            return Task.CompletedTask;
        });
        var latest = coordinator.RunLatestAsync(_ =>
        {
            latestRan = true;
            return Task.CompletedTask;
        });
        release.SetResult();

        await Task.WhenAll(first, middle, latest);
        Assert.False(middleRan);
        Assert.True(latestRan);
    }

    [Fact]
    public async Task RunFinalAsync_WaitsForCurrentOperationThenRunsExclusively()
    {
        using var coordinator = new LatestOperationCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRan = false;

        var active = coordinator.RunLatestAsync(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.SetResult(); }
        });
        await started.Task;

        await coordinator.RunFinalAsync(() =>
        {
            finalRan = true;
            return Task.CompletedTask;
        });
        await active;

        Assert.True(canceled.Task.IsCompleted);
        Assert.True(finalRan);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunLatestAsync(_ => Task.CompletedTask));
    }
}
