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

    public bool HasPendingChanges => _dirty;
    public bool HasSaveError { get; private set; }
    public string? SaveError { get; private set; }

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
        selector.SelectionChanged += (_, _) =>
        {
            _dirty = true;
            SaveNow();
        };
    }

    public void TrackImmediate(System.Windows.Controls.Primitives.ToggleButton toggle)
    {
        void OnChanged(object sender, RoutedEventArgs e)
        {
            _dirty = true;
            SaveNow();
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
        comboBox.SelectionChanged += (_, _) => Schedule();
        comboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Schedule()));
        comboBox.LostKeyboardFocus += (_, _) => SaveNow();
    }

    public void TrackDebounced(SecurePasswordField passwordField)
    {
        passwordField.PasswordChanged += (_, _) => Schedule();
        passwordField.LostKeyboardFocus += (_, _) => SaveNow();
    }

    private void Schedule()
    {
        _dirty = true;
        HasSaveError = false;
        SaveError = null;
        SettingsFeedback.Report(_owner, "等待保存…");
        _timer.Stop();
        _timer.Start();
    }

    public bool SaveNow()
    {
        _timer.Stop();
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
            SettingsFeedback.Report(_owner);
            return true;
        }
        catch (Exception ex)
        {
            HasSaveError = true;
            SaveError = ex.Message;
            SettingsFeedback.Report(_owner, "保存失败，请重试");
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

}
