using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class AppLoggingTests
{
    [Fact]
    public async Task FullBuffer_DropsNewEventsWithoutBlockingAndDrainsOnDispose()
    {
        using var sink = new ControlledSink(blockFirst: true);
        var monitor = new AsyncLogMonitor();
        var logger = AppLogging.CreateLogger("Information", config => config.Sink(sink), 2, monitor);
        try
        {
            logger.Information("first");
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                logger.Information("queued one");
                logger.Information("queued two");
                logger.Information("dropped");
            }).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, monitor.BufferedEvents);
            Assert.Equal(1, monitor.DroppedEvents);
        }
        finally
        {
            sink.Release.Set();
            logger.Dispose();
        }
        Assert.Equal(3, sink.Events.Count);
        Assert.Equal(1, sink.DisposeCount);
        Assert.Equal(1, monitor.DroppedEvents);
        Assert.Equal(0, monitor.BufferedEvents);
        logger.Dispose();
        Assert.Equal(1, sink.DisposeCount);
    }

    [Fact]
    public async Task ThreadId_IsCapturedBeforeBackgroundDispatch()
    {
        using var sink = new ControlledSink(blockFirst: false);
        using var logger = AppLogging.CreateLogger("Information", config => config.Sink(sink));
        var producerThread = Environment.CurrentManagedThreadId;
        logger.Information("thread identity");
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        logger.Dispose();

        var item = Assert.Single(sink.Events);
        Assert.Equal(producerThread, Assert.IsType<ScalarValue>(item.Properties["ThreadId"]).Value);
        Assert.NotEqual(producerThread, sink.ConsumerThreadId);
    }

    [Fact]
    public async Task ConcurrentEmitAndDispose_DoesNotThrowOrDisposeDownstreamTwice()
    {
        using var sink = new ControlledSink(blockFirst: false);
        var logger = AppLogging.CreateLogger("Information", config => config.Sink(sink), 16);
        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
                logger.Information("producer={Producer} item={Item}", producer, index);
        })).ToArray();
        try
        {
            await Task.WhenAll(producers.Append(Task.Run(logger.Dispose))).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            logger.Dispose();
        }
        Assert.Equal(1, sink.DisposeCount);
    }

    private sealed class ControlledSink(bool blockFirst) : ILogEventSink, IDisposable
    {
        private int _eventsReceived;
        private int _disposeCount;
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int ConsumerThreadId { get; private set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Emit(LogEvent logEvent)
        {
            Events.Enqueue(logEvent);
            if (Interlocked.Increment(ref _eventsReceived) != 1)
                return;
            ConsumerThreadId = Environment.CurrentManagedThreadId;
            Started.TrySetResult();
            if (blockFirst)
                Release.Wait();
        }

        public void Dispose()
        {
            // Serilog.Logger.Dispose 每次调用都会转发一次（探针实测 disposeCount=4/4），
            // 下游 sink 必须自行幂等；这里同样只计第一次。
            if (Interlocked.Exchange(ref _disposeCount, 1) != 0)
                return;
            // 供测试 finally 解锁；不在此处释放可能仍被断言使用的同步对象。
            Release.Set();
        }
    }
}
