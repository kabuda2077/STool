using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace STool.Core;

/// <summary>
/// 快捷键捕获输入框:点击聚焦后直接按下组合键即可自动记录(如 Ctrl+Alt+A)。
/// 只读显示,组合键必须包含至少一个修饰键;Esc 结束捕获,Tab 正常切换焦点。
/// 聚焦时挂起全局热键(否则常用组合会被系统级热键拦截),失焦时恢复。
/// 输出格式与 HotkeyManager 解析一致(修饰键 + 主键,用 "+" 连接)。
/// </summary>
public class HotkeyBox : TextBox
{
    static HotkeyBox()
    {
        // 子类化 TextBox 需把 DefaultStyleKey 指回 TextBox,确保沿用 TextBox 的样式/模板基础设施。
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(typeof(TextBox)));
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
        // 进入捕获:挂起全局快捷键,否则按 Ctrl+Alt+A 等会被系统级热键拦截而录不进来
        (System.Windows.Application.Current as STool.App)?.SuspendHotkeys();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        // 退出捕获:按最新配置恢复全局快捷键
        (System.Windows.Application.Current as STool.App)?.ReloadHotkeys();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt 组合时主键会走 SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 放行 Tab,保证键盘焦点可正常切换
        if (key == Key.Tab)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Esc 结束本次捕获(不改值)
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        // 单独的修饰键不记录,等待主键
        if (IsModifierKey(key))
            return;

        var token = KeyToToken(key);
        if (token == null)
            return;

        // 全局快捷键要求至少一个修饰键
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
            return;

        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(token);

        Text = string.Join("+", parts);
        CaretIndex = Text.Length;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string? KeyToToken(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key >= Key.F1 && key <= Key.F12)
            return "F" + (key - Key.F1 + 1);
        return null;
    }
}
