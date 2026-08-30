using System.Threading;
using System.Windows;
using OpenGepa.Services;

namespace OpenGepa;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\OpenGepa.Singleton.v1";
    private const string ShowEventName = @"Local\OpenGepa.Show.v1";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private TrayService? _tray;

    public static AppService Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        try
        {
            Services = AppService.Create();
            Services.Initialize();
            Services.PrepareLauncher();
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent, (_, _) => Dispatcher.BeginInvoke(Services.ShowLauncher), null, Timeout.Infinite, false);
            _tray = new TrayService(Services);
            _tray.Show();
            Services.StartupService.RepairShortcutIfEnabled();
            Services.Store.MarkLastGood(Services.Data);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OpenGepaを開始できませんでした。\n\n{ex.Message}", "OpenGepa",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ExitApplication()
    {
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showEvent?.Dispose();
        if (_mutex is not null) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
        base.OnExit(e);
    }
}
