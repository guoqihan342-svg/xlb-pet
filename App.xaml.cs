using System.Windows;
using System.Threading;

namespace LubanDesktopPet;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\LubanDesktopPet.SingleInstance.v1";

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private TrayIconService? _trayIconService;

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
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        try
        {
            if (StartupRegistration.TryCreateForCurrentProcess(
                    out var startupRegistration,
                    out _) &&
                startupRegistration is not null)
            {
                mainWindow.ConfigureStartupRegistration(startupRegistration);
            }
        }
        catch (Exception)
        {
            // Autostart is optional. Never prevent the desktop pet from
            // launching because the current host cannot access its Run key.
        }

        MainWindow = mainWindow;
        mainWindow.Show();

        try
        {
            _trayIconService = new TrayIconService();
            _trayIconService.ExitRequested +=
                TrayIconService_ExitRequested;
        }
        catch (Exception)
        {
            // The pet remains fully usable if Explorer or the notification
            // area is unavailable in the current Windows session.
            _trayIconService?.Dispose();
            _trayIconService = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_trayIconService is { } trayIconService)
            {
                trayIconService.ExitRequested -=
                    TrayIconService_ExitRequested;
                trayIconService.Dispose();
                _trayIconService = null;
            }

            base.OnExit(e);
        }
        finally
        {
            if (_ownsSingleInstanceMutex)
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

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private void TrayIconService_ExitRequested(
        object? sender,
        EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(
                    new Action(() =>
                        TrayIconService_ExitRequested(sender, e)));
            }

            return;
        }

        if (MainWindow is { } mainWindow)
        {
            mainWindow.Close();
        }
        else
        {
            Shutdown();
        }
    }
}
