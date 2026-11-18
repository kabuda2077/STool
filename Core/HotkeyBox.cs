using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace STool.Core;

/// <summary>
/// 快捷键录制输入框：点击后等待组合键或 F1–F24，调用方校验后结束录制。
/// 录制期间挂起全局热键；Esc、Tab 和失焦取消，恢复原值与全局热键。
/// Text 保留实际热键，录制占位由模板通过 IsRecording 和 RecordingText 显示。
/// </summary>
public class HotkeyBox : TextBox
{
    private static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRecording), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;
    public static readonly DependencyProperty RecordingTextProperty = DependencyProperty.Register(
        nameof(RecordingText), typeof(string), typeof(HotkeyBox), new PropertyMetadata("按下快捷键…"));

    public bool IsRecording => (bool)GetValue(IsRecordingProperty);
    public string RecordingText
    {
        get => (string)GetValue(RecordingTextProperty);
        set => SetValue(RecordingTextProperty, value);
    }

    public event EventHandler? HotkeyCaptured;
    public event EventHandler? RecordingRejected;
    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingEnded;

    private string _beforeRecording = string.Empty;
    private Window? _recordingWindow;

    static HotkeyBox()
    {
        // 子类化 TextBox 需把 DefaultStyleKey 指回 TextBox,确保沿用 TextBox 的样式/模板基础设施。
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(typeof(TextBox)));
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        Cursor = Cursors.Hand;
        Unloaded += (_, _) => StopRecording(cancel: true);
    }

    /// <summary>用于在录入期间挂起、结束后恢复全局热键；未设置时只做录入。</summary>
    public IAppShell? Shell { get; set; }

    public void StartRecording()
    {
        if (IsRecording) return;
        _beforeRecording = Text;
        SetValue(IsRecordingPropertyKey, true);
        Select(0, 0);
        _recordingWindow = Window.GetWindow(this);
        _recordingWindow?.AddHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(RecordingWindow_MouseDown), handledEventsToo: true);
        Shell?.SuspendHotkeys();
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    public void StopRecording(bool cancel = false, bool reloadHotkeys = true)
    {
        if (!IsRecording) return;
        SetValue(IsRecordingPropertyKey, false);
        _recordingWindow?.RemoveHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(RecordingWindow_MouseDown));
        _recordingWindow = null;
        if (cancel) Text = _beforeRecording;
        if (reloadHotkeys) Shell?.ReloadHotkeys();
        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void RecordingWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && (ReferenceEquals(source, this) || IsAncestorOf(source))) return;
        StopRecording(cancel: true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopRecording(cancel: true);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Focus();
        StartRecording();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt 组合时主键会走 SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (!IsRecording)
        {
            if (key is Key.Enter or Key.Space)
            {
                StartRecording();
                e.Handled = true;
            }
            else base.OnPreviewKeyDown(e);
            return;
        }

        // Tab 取消本次录制，同时保留原生焦点导航。
        if (key == Key.Tab)
        {
            StopRecording(cancel: true);
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Esc 结束本次捕获(不改值)
        if (key == Key.Escape)
        {
            StopRecording(cancel: true);
            return;
        }

        // 单独的修饰键不记录,等待主键
        if (IsModifierKey(key))
            return;

        var token = KeyToToken(key);
        var mods = Keyboard.Modifiers;
        if (token == null || (mods == ModifierKeys.None && (key < Key.F1 || key > Key.F24)))
        {
            RecordingRejected?.Invoke(this, EventArgs.Empty);
            return;
        }

        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(token);

        Text = string.Join("+", parts);
        CaretIndex = Text.Length;
        HotkeyCaptured?.Invoke(this, EventArgs.Empty);
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
        if (key >= Key.F1 && key <= Key.F24)
            return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Space => "Space",
            _ => null
        };
    }
}
