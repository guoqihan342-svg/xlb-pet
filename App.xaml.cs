using System.Windows;
using System.Windows.Threading;
using System.Threading;

namespace LubanDesktopPet;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\LubanDesktopPet.SingleInstance.v1";

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: false,
            SingleInstanceMutexName,
            out _);
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }
        AppLogger.Initialize();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppLogger.Info($"应用启动，版本 {typeof(App).Assembly.GetName().Version}");
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var loggerStopped = !_ownsSingleInstanceMutex;
        try
        {
            if (_ownsSingleInstanceMutex)
            {
                AppLogger.Info($"应用退出，代码 {e.ApplicationExitCode}");
                DispatcherUnhandledException -= App_DispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
                TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
                loggerStopped = AppLogger.Shutdown(TimeSpan.FromSeconds(2));
            }

            base.OnExit(e);
        }
        finally
        {
            if (_ownsSingleInstanceMutex && loggerStopped)
            {
                try
                {
                    _singleInstanceMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The process is already exiting; never mask the original exit path.
                }

                _ownsSingleInstanceMutex = false;
            }

            if (loggerStopped)
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
        }
    }

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("UI 线程发生未处理异常", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogger.Error("应用域发生未处理异常", exception);
        }
        else
        {
            AppLogger.Info(
                $"应用域发生未知未处理异常，类型：{e.ExceptionObject.GetType().FullName}");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("后台任务发生未观察异常", e.Exception);
    }
}
