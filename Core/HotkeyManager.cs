using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace STool.Core;

public enum HotkeyRegistrationFailure
{
    None,
    InvalidFormat,
    SystemRejected
}

public sealed record HotkeyRegistrationResult(
    string FeatureName,
    string Hotkey,
    bool Success,
    HotkeyRegistrationFailure Failure = HotkeyRegistrationFailure.None);

internal readonly record struct ParsedHotkey(uint Modifiers, uint VirtualKey, string NormalizedText);

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

    public HotkeyRegistrationResult RegisterHotkey(string featureName, string hotkeyString, Action action)
    {
        if (_hwndSource == null || !TryParseHotkey(hotkeyString, out var parsed))
        {
            return new HotkeyRegistrationResult(
                featureName,
                hotkeyString,
                false,
                HotkeyRegistrationFailure.InvalidFormat);
        }

        var id = _currentId++;
        if (RegisterHotKey(_hwndSource.Handle, id, parsed.Modifiers, parsed.VirtualKey))
        {
            _hotkeyActions[id] = action;
            return new HotkeyRegistrationResult(featureName, parsed.NormalizedText, true);
        }

        return new HotkeyRegistrationResult(
            featureName,
            parsed.NormalizedText,
            false,
            HotkeyRegistrationFailure.SystemRejected);
    }

    public static bool IsValidHotkey(string hotkeyString) =>
        TryParseHotkey(hotkeyString, out _);

    public static string? NormalizeHotkey(string hotkeyString) =>
        TryParseHotkey(hotkeyString, out var parsed) ? parsed.NormalizedText : null;

    internal static bool TryParseHotkey(string hotkeyString, out ParsedHotkey parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        var parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            return false;

        uint modifiers = 0;
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

            if (modifier == 0 || (modifiers & modifier) != 0)
                return false;

            modifiers |= modifier;
        }

        if (modifiers == 0 || !TryParseKey(parts[^1], out var key, out var keyToken))
            return false;

        var normalizedParts = new List<string>(5);
        if ((modifiers & 0x0002) != 0) normalizedParts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) normalizedParts.Add("Alt");
        if ((modifiers & 0x0004) != 0) normalizedParts.Add("Shift");
        if ((modifiers & 0x0008) != 0) normalizedParts.Add("Win");
        normalizedParts.Add(keyToken);

        parsed = new ParsedHotkey(modifiers, key, string.Join("+", normalizedParts));
        return true;
    }

    private static bool TryParseKey(string value, out uint key, out string token)
    {
        key = 0;
        token = string.Empty;
        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            key = normalized[0];
            token = normalized;
            return true;
        }

        if (normalized.Length is 2 or 3 &&
            normalized[0] == 'F' &&
            int.TryParse(normalized[1..], out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = (uint)(0x70 + functionKey - 1);
            token = $"F{functionKey}";
            return true;
        }

        (key, token) = normalized switch
        {
            "LEFT" => (0x25u, "Left"),
            "UP" => (0x26u, "Up"),
            "RIGHT" => (0x27u, "Right"),
            "DOWN" => (0x28u, "Down"),
            "HOME" => (0x24u, "Home"),
            "END" => (0x23u, "End"),
            "PAGEUP" or "PGUP" => (0x21u, "PageUp"),
            "PAGEDOWN" or "PGDN" => (0x22u, "PageDown"),
            "INSERT" or "INS" => (0x2Du, "Insert"),
            "DELETE" or "DEL" => (0x2Eu, "Delete"),
            "SPACE" => (0x20u, "Space"),
            _ => (0u, string.Empty)
        };
        return key != 0;
    }

    public void UnregisterAll()
    {
        if (_hwndSource != null)
        {
            foreach (var id in _hotkeyActions.Keys)
                UnregisterHotKey(_hwndSource.Handle, id);
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
                action();
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
