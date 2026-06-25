using System;
using System.Windows;
using System.Windows.Threading;
using Serilog;
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
        // 全局异常兜底:托盘常驻应用不能因为单个未捕获异常(尤其 async void 事件处理器)而整体崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

    /// <summary>
    /// UI 线程未捕获异常:记录日志并提示用户,标记已处理以保持进程存活。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            TryNotifyError(e.Exception);
        }
        catch
        {
            // 异常处理器自身不能抛异常
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 非 UI 线程未捕获异常:无法阻止终止(IsTerminating),仅尽量留下日志。
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Log.Error(e.ExceptionObject as Exception, "Unhandled non-UI exception (terminating={Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        }
        catch
        {
            // 异常处理器自身不能抛异常
        }
    }

    /// <summary>
    /// 未观察的 Task 异常(被 GC 回收的 faulted Task):记录并标记已观察,避免进程被终止。
    /// </summary>
    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        }
        catch
        {
            // 异常处理器自身不能抛异常
        }
    }

    private static void TryNotifyError(Exception ex)
    {
        try
        {
            ToastNotification.Show("出现错误", ex.Message, ToastNotification.ToastType.Error);
        }
        catch (Exception toastEx)
        {
            Log.Warning(toastEx, "Failed to show error toast");
        }
    }
}

