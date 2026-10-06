using System.IO;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Async;

namespace STool.Core;

internal static class AppLogging
{
    internal const int DefaultBufferSize = 2048;
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [T{ThreadId}] {Message:lj}{NewLine}{Exception}";
    private static readonly AsyncLogMonitor Monitor = new();

    public static long DroppedEvents => Monitor.DroppedEvents;

    /// <summary>格式化和文件写入放到有界后台队列；满时丢弃新事件，不阻塞 UI。</summary>
    public static void Configure(string? logLevel)
    {
        // Async sink 在关闭时排空已接收事件，并释放内部文件 sink。
        Log.CloseAndFlush();
        Log.Logger = CreateLogger(logLevel, sink => sink.File(
            Path.Combine(AppPaths.LogsDirectory, "app.log"),
            outputTemplate: OutputTemplate,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            buffered: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1)), monitor: Monitor);
    }

    // 测试使用独立 logger 和受控 sink，不修改进程级 Log.Logger。
    internal static Logger CreateLogger(
        string? logLevel,
        Action<LoggerSinkConfiguration> configureSink,
        int bufferSize = DefaultBufferSize,
        IAsyncLogEventSinkMonitor? monitor = null) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(logLevel))
            // 必须在入队前记录调用线程，而不是后台写入线程。
            .Enrich.With(new ThreadIdEnricher())
            .WriteTo.Async(configureSink, bufferSize: bufferSize, blockWhenFull: false, monitor: monitor)
            .CreateLogger();

    internal static LogEventLevel ParseLevel(string? value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;
}

internal sealed class AsyncLogMonitor : IAsyncLogEventSinkMonitor
{
    private readonly object _gate = new();
    private IAsyncLogEventSinkInspector? _inspector;
    private long _completedDrops;

    public int BufferedEvents
    {
        get { lock (_gate) return _inspector?.Count ?? 0; }
    }

    public long DroppedEvents
    {
        get { lock (_gate) return _completedDrops + (_inspector?.DroppedMessagesCount ?? 0); }
    }

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        lock (_gate)
            _inspector = inspector;
    }

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_inspector, inspector))
                return;
            _completedDrops += inspector.DroppedMessagesCount;
            _inspector = null;
        }
    }
}

internal sealed class ThreadIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
