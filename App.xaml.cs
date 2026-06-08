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
            System.Windows.MessageBox.Show("STool 已在运行中", "STool", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 初始化应用
        _bootstrap = new AppBootstrap();

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

    public void SuspendHotkeys()
    {
        _bootstrap?.SuspendGlobalHotkeys();
    }
}

