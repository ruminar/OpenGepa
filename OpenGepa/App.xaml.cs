using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using OpenGepa.Services;

namespace OpenGepa;

public partial class App : System.Windows.Application
{
    public static bool IsExiting { get; private set; }
    private const string MutexName = @"Local\OpenGepa.Singleton.v1";
    private const string ShowEventName = @"Local\OpenGepa.Show.v1";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private TrayService? _tray;
    private HwndSource? _hotKeyWindow;
    private const int WmHotKey = 0x0312;

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
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => ThemePalette.ApplyWindowChrome((Window)sender, Services.Data.Appearance)));
            Services.PrepareLauncher();
            RegisterShowHotKey();
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
        IsExiting = true;
        foreach (var window in Windows.Cast<Window>().ToArray()) window.Close();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotKeyWindow is not null) { UnregisterHotKey(_hotKeyWindow.Handle, 1); _hotKeyWindow.Dispose(); }
        _showRegistration?.Unregister(null);
        _showEvent?.Dispose();
        if (_mutex is not null) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
        base.OnExit(e);
    }
    private void RegisterShowHotKey()
    {
        _hotKeyWindow = new HwndSource(new HwndSourceParameters("OpenGepaHotKey") { Width = 0, Height = 0, WindowStyle = 0 });
        _hotKeyWindow.AddHook(HotKeyHook);
        RegisterHotKey(_hotKeyWindow.Handle, 1, 0x0002 | 0x0001, 0x47);
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    private IntPtr HotKeyHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) { if (message == WmHotKey) { Dispatcher.BeginInvoke(Services.ShowLauncher); handled = true; } return IntPtr.Zero; }
}
