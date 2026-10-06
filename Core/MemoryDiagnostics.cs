using System;
using System.Runtime.InteropServices;
using Serilog;

namespace STool.Core;

/// <summary>
/// 内存与句柄检查点日志，仅在诊断开关打开时记录。使用 GetProcessMemoryInfo，
/// 不走 Process 类（Process 的内存属性会取一次全系统进程快照，放在 UI 路径上不划算）。
/// </summary>
internal static class MemoryDiagnostics
{
    private const uint GdiObjects = 0;
    private const uint UserObjects = 1;

    public static bool Enabled { get; set; }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    public static void LogCheckpoint(string stage, int? thumbnailCount = null, long? thumbnailBytes = null)
    {
        if (!Enabled)
            return;

        try
        {
            var process = GetCurrentProcess();
            var counters = new ProcessMemoryCounters();
            var size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
            counters.Size = size;
            var hasCounters = K32GetProcessMemoryInfo(process, out counters, size);

            var windowCount = -1;
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher.CheckAccess() == true)
                windowCount = app.Windows.Count;

            var gcInfo = GC.GetGCMemoryInfo();
            Log.Information(
                "[Memory] stage={Stage} privateMb={PrivateMb:F1} workingSetMb={WorkingSetMb:F1} managedMb={ManagedMb:F1} heapMb={HeapMb:F1} gdiObjects={GdiObjects} userObjects={UserObjects} windowCount={WindowCount} thumbnailCount={ThumbnailCount} thumbnailMb={ThumbnailMb:F1} droppedLogEvents={DroppedLogEvents}",
                stage,
                hasCounters ? (ulong)counters.PrivateUsage / 1024d / 1024d : -1,
                hasCounters ? (ulong)counters.WorkingSetSize / 1024d / 1024d : -1,
                GC.GetTotalMemory(false) / 1024d / 1024d,
                gcInfo.HeapSizeBytes / 1024d / 1024d,
                GetGuiResources(process, GdiObjects),
                GetGuiResources(process, UserObjects),
                windowCount,
                thumbnailCount ?? 0,
                (thumbnailBytes ?? 0) / 1024d / 1024d,
                AppLogging.DroppedEvents);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Memory] checkpoint failed stage={Stage}", stage);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }
}
