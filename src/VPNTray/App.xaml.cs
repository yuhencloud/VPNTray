using System.Windows;
using VPNTray.Util;

namespace VPNTray;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppCoordinator? _coordinator;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (!SingleInstance.TryAcquire(out _instanceMutex))
        {
            MessageBox.Show("VPNTray is already running.", "VPNTray");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Shared.Error("unhandled: " + args.Exception);
            args.Handled = true;
        };

        _coordinator = new AppCoordinator();
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Shared.Info("exit: OnExit entered");
        // No-op when Quit already ran it; still the only teardown on paths that bypass the
        // tray menu (session ending, Shutdown called from elsewhere).
        _coordinator?.StopForShutdown();
        _instanceMutex?.Dispose();
        base.OnExit(e);
        AppLogger.Shared.Info("exit: OnExit done");
    }
}
