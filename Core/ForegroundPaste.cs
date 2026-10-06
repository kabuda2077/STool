using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace STool.Core;

/// <summary>切回指定窗口并模拟 Ctrl+V，剪贴板面板的双击粘贴和翻译面板的"复制并输入"共用。</summary>
internal static class ForegroundPaste
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    /// <summary>目标窗口已不存在或无法切到前台时返回 false，调用方提示用户手动粘贴。</summary>
    public static async Task<bool> PasteToAsync(IntPtr targetWindow, int focusDelayMs = 100)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow) || !SetForegroundWindow(targetWindow))
            return false;

        // 等目标窗口真正获得焦点后再发送按键，否则 Ctrl+V 可能落在正在关闭的窗口上。
        await Task.Delay(focusDelayMs);
        // 等待期间用户可能切换窗口，不能把剪贴板内容输入到另一个应用。
        if (GetForegroundWindow() != targetWindow)
            return false;

        var inputs = new[]
        {
            KeyboardInput(VirtualKeyControl, keyUp: false),
            KeyboardInput(VirtualKeyV, keyUp: false),
            KeyboardInput(VirtualKeyV, keyUp: true),
            KeyboardInput(VirtualKeyControl, keyUp: true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input KeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // 联合体大小取决于最大的 MOUSEINPUT，缺了它 SendInput 会因结构大小不符而失败。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
