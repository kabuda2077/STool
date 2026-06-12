using System.Windows;
using STool.Core;

namespace STool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstance? _singleInstance;
    private AppBootstrap? _bootstrap;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例检查
        _singleInstance = new SingleInstance("Main");
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.ActivateFirstInstance();
            Shutdown();
            return;
        }

        // 初始化应用
        _bootstrap = new AppBootstrap();
        _singleInstance.StartActivationListener(() =>
        {
            Dispatcher.Invoke(() => _bootstrap?.ShowSettings());
        });

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrap?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public T? GetService<T>() where T : class
    {
        return _bootstrap?.GetService<T>();
    }

    public void ReloadHotkeys()
    {
        _bootstrap?.ReloadHotkeys();
    }

    public void ReloadTrayIconVisibility()
    {
        _bootstrap?.ReloadTrayIconVisibility();
    }

    public void SuspendHotkeys()
    {
        _bootstrap?.SuspendGlobalHotkeys();
    }

    public void ShowSettings()
    {
        _bootstrap?.ShowSettings();
    }
}

