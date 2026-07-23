using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace STool.Core;

internal static class MemoryDiagnostics
{
    private const uint GdiObjects = 0;
    private const uint UserObjects = 1;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    public static void LogCheckpoint(string stage, int? thumbnailCount = null, long? thumbnailBytes = null)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var windowCount = -1;
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher.CheckAccess() == true)
                windowCount = app.Windows.Count;

            var gcInfo = GC.GetGCMemoryInfo();
            Log.Information(
                "[Memory] stage={Stage} privateMb={PrivateMb:F1} workingSetMb={WorkingSetMb:F1} managedMb={ManagedMb:F1} heapMb={HeapMb:F1} gdiObjects={GdiObjects} userObjects={UserObjects} windowCount={WindowCount} thumbnailCount={ThumbnailCount} thumbnailMb={ThumbnailMb:F1}",
                stage,
                process.PrivateMemorySize64 / 1024d / 1024d,
                process.WorkingSet64 / 1024d / 1024d,
                GC.GetTotalMemory(false) / 1024d / 1024d,
                gcInfo.HeapSizeBytes / 1024d / 1024d,
                GetGuiResources(process.Handle, GdiObjects),
                GetGuiResources(process.Handle, UserObjects),
                windowCount,
                thumbnailCount ?? 0,
                (thumbnailBytes ?? 0) / 1024d / 1024d);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Memory] checkpoint failed stage={Stage}", stage);
        }
    }
}
