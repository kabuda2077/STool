using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace STool.Views.Settings;

/// <summary>Coordinates debounced settings persistence and lightweight saved feedback.</summary>
internal sealed class SettingsAutoSaveController
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(SettingsAutoSaveController), typeof(SettingsAutoSaveController));

    public static SettingsAutoSaveController? GetController(DependencyObject owner) =>
        (SettingsAutoSaveController?)owner.GetValue(ControllerProperty);

    public event EventHandler? DraftChanged;
    public bool HasPendingChanges => _dirty;
    public bool HasSaveError { get; private set; }
    public string? SaveError { get; private set; }
    public string? SaveErrorSummary { get; private set; }

    private readonly DispatcherTimer _timer;
    private readonly FrameworkElement _owner;
    private readonly Func<bool> _save;
    private bool _dirty;
    private bool _saving;

    public SettingsAutoSaveController(FrameworkElement owner, Func<bool> save)
    {
        _owner = owner;
        _save = save;
        owner.SetValue(ControllerProperty, this);
        _timer = new DispatcherTimer { Interval = SaveDelay };
        _timer.Tick += (_, _) => SaveNow();

        owner.Unloaded += (_, _) => SaveNow();
    }

    public void TrackImmediate(Selector selector)
    {
        selector.SelectionChanged += (_, _) => SaveChange();
    }

    public void TrackImmediate(System.Windows.Controls.Primitives.ToggleButton toggle)
    {
        void OnChanged(object sender, RoutedEventArgs e)
        {
            SaveChange();
        }

        toggle.Checked += OnChanged;
        toggle.Unchecked += OnChanged;
    }

    public void TrackDebounced(TextBox textBox)
    {
        textBox.TextChanged += (_, _) => Schedule();
        textBox.LostKeyboardFocus += (_, _) => SaveNow();
    }

    public void TrackDebounced(ComboBox comboBox)
    {
        var lastText = comboBox.Text;
        void OnChanged()
        {
            // Applying the editable template also raises TextChanged with the loaded value.
            if (lastText == comboBox.Text) return;
            lastText = comboBox.Text;
            Schedule();
        }
        comboBox.SelectionChanged += (_, _) => OnChanged();
        comboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => OnChanged()));
        comboBox.LostKeyboardFocus += (_, _) => SaveNow();
    }

    public void TrackDebounced(SecurePasswordField passwordField)
    {
        passwordField.PasswordChanged += (_, _) => Schedule();
        passwordField.LostKeyboardFocus += (_, _) => SaveNow();
    }

    /// <summary>保留输入中的草稿，直到显式操作、离页或关闭时提交。</summary>
    public void TrackDraft(TextBox textBox) => textBox.TextChanged += (_, _) => Schedule(debounce: false);

    private bool _discarded;

    private void Schedule(bool debounce = true)
    {
        if (_discarded) return;
        _dirty = true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        HasSaveError = false;
        SaveError = null;
        SaveErrorSummary = null;
        SettingsFeedback.Report(_owner, "等待保存…");
        _timer.Stop();
        if (debounce) _timer.Start();
    }

    /// <summary>立即保存添加、移除等不由输入控件事件触发的修改。</summary>
    public bool SaveChange()
    {
        if (_discarded) return true;
        _dirty = true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        return SaveNow();
    }

    /// <summary>放弃待保存的草稿；调用方随后重建或关闭页面。</summary>
    public void DiscardChanges()
    {
        _discarded = true;
        _timer.Stop();
        _dirty = false;
        HasSaveError = false;
        SaveError = null;
        SaveErrorSummary = null;
    }

    public bool SaveNow()
    {
        _timer.Stop();
        if (_discarded) return true;
        if (!_dirty || _saving)
        {
            return !_dirty;
        }

        _saving = true;
        try
        {
            // false means unchanged for existing panels; only an exception is a failed save.
            _save();
            _dirty = false;
            HasSaveError = false;
            SaveError = null;
            SaveErrorSummary = null;
            SettingsFeedback.Report(_owner);
            return true;
        }
        catch (Exception ex)
        {
            HasSaveError = true;
            SaveError = ex.Message;
            SaveErrorSummary = ex switch
            {
                UnauthorizedAccessException => "无法写入设置，请检查数据目录的写入权限。",
                System.IO.IOException => "无法写入设置，请检查磁盘空间或配置文件是否被占用。",
                _ => "保存设置失败，请重试。"
            };
            SettingsFeedback.Report(_owner, "保存失败，请重试");
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

}
