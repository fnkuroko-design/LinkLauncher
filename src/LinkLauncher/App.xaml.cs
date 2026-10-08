using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LinkLauncher;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _wait;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        bool startHidden = Array.IndexOf(e.Args, "--background") >= 0;
        // Session-local names avoid cross-user interference on shared Windows PCs.
        _singleInstance = new Mutex(true, "Local\\LinkLauncher.Instance", out _ownsMutex);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LinkLauncher.Show");
        if (!_ownsMutex)
        {
            if (!startHidden) _showEvent.Set();
            Shutdown();
            return;
        }
        try
        {
            string? directory = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) directory = Path.GetFullPath(e.Args[++i]);
            }
            var window = new MainWindow(directory);
            MainWindow = window;
            _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(window.ShowLauncher)), null, Timeout.Infinite, false);
            window.ShowActivated = !startHidden;
            if (startHidden)
            {
                window.Show();
                window.Hide();
                window.ShowActivated = true;
            }
            else window.ShowLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show("起動できませんでした。\n\n" + ex.Message, "LinkLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("操作を完了できませんでした。\n\n" + e.Exception.Message,
            "LinkLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wait?.Unregister(null);
        _showEvent?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Services.ThemeManager.Dispose();
        base.OnExit(e);
    }
}
