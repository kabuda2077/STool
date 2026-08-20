using System.Windows;
using Serilog;

namespace STool.Core;

internal sealed class WindowCoordinator : IDisposable
{
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public T ShowSingle<T>(string key, Func<T> factory) where T : Window
    {
        if (_windows.TryGetValue(key, out var existing))
        {
            ShowInForeground(existing);
            return (T)existing;
        }

        var window = factory();
        _windows[key] = window;
        window.Closed += (_, _) =>
        {
            if (_windows.TryGetValue(key, out var current) && ReferenceEquals(current, window))
                _windows.Remove(key);
        };
        ShowInForeground(window);
        return window;
    }

    public T? Get<T>(string key) where T : Window =>
        _windows.TryGetValue(key, out var window) ? window as T : null;

    private static void ShowInForeground(Window window)
    {
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Activate();
        window.Focus();
        window.Topmost = wasTopmost;
        window.Activate();
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to close coordinated window {WindowType}", window.GetType().Name);
            }
        }
        _windows.Clear();
    }
}
