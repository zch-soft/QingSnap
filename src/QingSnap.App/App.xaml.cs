using System.Windows;
using System.IO;
using System.Windows.Threading;
using QingSnap.App.Services;
using QingSnap.App.Views;
using MessageBox = System.Windows.MessageBox;

namespace QingSnap.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\QingSnap.7F29E134-6D9A-49EE-8CF5-CF40EEC3C9E1";
    private MainWindow? _hostWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    static App()
    {
        System.Windows.Forms.Application.SetHighDpiMode(
            System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLog.Initialize();
        if (e.Args.Any(argument => argument.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase)) ||
            e.Args.Any(argument => argument.Equals("--resource-window-stress", StringComparison.OrdinalIgnoreCase)))
        {
            ResourceDiagnostics.Enable();
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        var uninstallRequested = e.Args.Any(argument =>
            argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            DiagnosticLog.Info("Application", "A second instance was rejected.");
            if (uninstallRequested)
            {
                MessageBox.Show(
                    "QingSnap 正在运行，请先从托盘菜单退出 QingSnap，再重试卸载。",
                    "QingSnap",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        if (uninstallRequested)
        {
            RunUninstall();
            return;
        }

        PortableModeService.TryApplyPendingSwitch(AppContext.BaseDirectory);

        _hostWindow = new MainWindow();
        DiagnosticLog.Info("Application", "Main window and tray host created.");
        _ = Task.Run(() => new ClipboardTempFileStore().CleanupExpired());
        MainWindow = _hostWindow;
        _hostWindow.Show();
        ResourceDiagnostics.Sample("AppStarted");
        Dispatcher.BeginInvoke(
            () => ResourceDiagnostics.Sample("IdleBaseline"),
            DispatcherPriority.ApplicationIdle);

        if (e.Args.Any(argument => argument.Equals("--resource-window-stress", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(() => _ = _hostWindow.RunResourceWindowStressAsync());
        }
        else if (e.Args.Any(argument => argument.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.OpenSettingsWindow);
        }
        else if (e.Args.Any(argument => argument.Equals("--history", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.OpenHistoryWindow);
        }
        else if (e.Args.Any(argument => argument.Equals("--repeat", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.RepeatLastCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--pin-latest", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.PinLatestCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--ocr-latest", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.RecognizeLatestCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--long-capture-manual", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.StartManualLongCapture);
        }
        else if (e.Args.Any(argument => argument.Equals("--long-capture", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(_hostWindow.StartLongCapture);
        }
    }

    private void RunUninstall()
    {
        try
        {
            var settingsService = new AppSettingsService();
            var integrationService = new SystemIntegrationService();
            var uninstallService = new UninstallService();
            var dialog = new UninstallWindow(
                uninstallService.Describe(settingsService.Current, settingsService.DataDirectory))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            if (dialog.ShowDialog() != true || dialog.Options is not { } options)
            {
                return;
            }

            var result = UninstallRunner.Run(
                options,
                settingsService,
                integrationService,
                uninstallService);
            UninstallRunner.ShowSummary(result);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Uninstall", exception, "Headless uninstall failed.");
            MessageBox.Show(
                $"卸载失败：{exception.Message}",
                "QingSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ResourceDiagnostics.Sample("FinalIdle");
        DiagnosticLog.Info("Application", $"Application exiting with code {e.ApplicationExitCode}.");
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Dispatcher", e.Exception);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("TaskScheduler", e.Exception);
        e.SetObserved();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog("AppDomain", exception);
        }
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        DiagnosticLog.Error(source, exception, "Unhandled exception.");
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "QingSnap-crash.log"),
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
