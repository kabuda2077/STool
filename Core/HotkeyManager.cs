using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace STool.Core;

public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private HwndSource? _hwndSource;
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private int _currentId = 1;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Initialize()
    {
        if (_hwndSource != null)
            return;

        var parameters = new HwndSourceParameters("HotkeyWindow")
        {
            WindowStyle = 0,
            Height = 0,
            Width = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    public bool RegisterHotkey(string hotkeyString, Action action)
    {
        if (_hwndSource == null)
            return false;

        if (!TryParseHotkey(hotkeyString, out var modifiers, out var key))
            return false;

        var id = _currentId++;
        if (RegisterHotKey(_hwndSource.Handle, id, modifiers, key))
        {
            _hotkeyActions[id] = action;
            return true;
        }

        return false;
    }

    public static bool IsValidHotkey(string hotkeyString)
    {
        return TryParseHotkey(hotkeyString, out _, out _);
    }

    private static bool TryParseHotkey(string hotkeyString, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 0x0002u,
                "alt" => 0x0001u,
                "shift" => 0x0004u,
                "win" or "windows" => 0x0008u,
                _ => 0u
            };

            if (modifier == 0)
                return false;

            modifiers |= modifier;
        }

        var keyStr = parts[^1].ToUpperInvariant();
        if (keyStr.Length == 1)
        {
            key = (uint)keyStr[0];
        }
        else
        {
            key = keyStr switch
            {
                "F1" => 0x70,
                "F2" => 0x71,
                "F3" => 0x72,
                "F4" => 0x73,
                "F5" => 0x74,
                "F6" => 0x75,
                "F7" => 0x76,
                "F8" => 0x77,
                "F9" => 0x78,
                "F10" => 0x79,
                "F11" => 0x7A,
                "F12" => 0x7B,
                _ => 0
            };
        }

        return key != 0;
    }

    public void UnregisterAll()
    {
        if (_hwndSource != null)
        {
            foreach (var id in _hotkeyActions.Keys)
            {
                UnregisterHotKey(_hwndSource.Handle, id);
            }
        }

        _hotkeyActions.Clear();
        _currentId = 1;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                action?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwndSource != null)
        {
            UnregisterAll();
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
