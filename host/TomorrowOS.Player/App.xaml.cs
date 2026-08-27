using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TomorrowOS.Player;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\TomorrowOS.Player", out var createdNew);
        if (!createdNew)
        {
            // Second instance would fight the WebView2 profile lock and crash-loop with Watchdog.
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        TryLogLifecycle("startup");
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        TryLogCrash(args.Exception);
        // Keep the player alive — an unhandled UI exception must not exit the process.
        args.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception ex)
        {
            TryLogCrash(ex);
        }
        else
        {
            TryLogCrash(new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        TryLogCrash(args.Exception);
        args.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TryLogLifecycle($"exit code={e.ApplicationExitCode}");
        try
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }

    private static void TryLogCrash(Exception ex)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "player-crash.log"),
                $"[{DateTime.Now:O}] {ex}\n");
        }
        catch
        {
            // ignore
        }
    }

    private static void TryLogLifecycle(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "player.log"),
                $"[{DateTime.Now:O}] lifecycle: {message}\n");
        }
        catch
        {
            // ignore
        }
    }
}
