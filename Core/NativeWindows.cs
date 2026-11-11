using System;
using System.Runtime.InteropServices;

namespace STool.Core;

/// <summary>多处共用的 user32 窗口函数。</summary>
internal static class NativeWindows
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
