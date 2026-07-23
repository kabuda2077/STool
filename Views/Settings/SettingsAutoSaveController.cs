using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using STool.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace STool.Views.Settings;

/// <summary>Coordinates debounced settings persistence and lightweight saved feedback.</summary>
internal sealed class SettingsAutoSaveController
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _save;
    private bool _dirty;
    private bool _saving;

    public SettingsAutoSaveController(FrameworkElement owner, Func<bool> save)
    {
        _save = save;
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
        _timer.Stop();
        _timer.Start();
    }

    private void SaveNow()
    {
        _timer.Stop();
        if (!_dirty || _saving)
        {
            return;
        }

        _saving = true;
        try
        {
            var saved = _save();
            _dirty = false;
            if (saved)
            {
                ToastNotification.Show(
                    "设置已保存",
                    type: ToastNotification.ToastType.Success,
                    duration: 1600);
            }
        }
        catch (Exception ex)
        {
            ToastNotification.Show("自动保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
        finally
        {
            _saving = false;
        }
    }

}
