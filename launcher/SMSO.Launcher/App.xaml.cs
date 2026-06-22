using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SMSO.Launcher;

public partial class App : Application
{
    private static readonly string ErrorLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.EnsureSessionShutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI thread", e.Exception);
        TryShowStatus($"Recovered from an error: {e.Exception.Message}");
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("Background thread", ex);
            Current?.Dispatcher.BeginInvoke(() =>
                TryShowStatus($"Recovered from a background error: {ex.Message}"));
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Background task", e.Exception);
        e.SetObserved();
    }

    internal static void LogException(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(ErrorLogDir);
            var path = Path.Combine(ErrorLogDir, $"smso-errors-{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex}\n";
            File.AppendAllText(path, line);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static void TryShowStatus(string message)
    {
        if (Current?.MainWindow is MainWindow window)
            window.ShowTransientStatus(message);
    }
}
