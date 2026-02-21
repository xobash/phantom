using System.Windows;
using System.Windows.Threading;
using Phantom.Services;
using Phantom.Views;

namespace Phantom;

public partial class App : Application
{
    private AppBootstrap? _bootstrap;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _ = StartAsync(e);
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        try
        {
            if (!AdminGuard.IsAdministrator())
            {
                MessageBox.Show("Phantom requires Administrator privileges. Relaunch from an elevated session.", "Phantom", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(10);
                return;
            }

            _bootstrap = new AppBootstrap();

            AppDomain.CurrentDomain.UnhandledException += async (_, args) =>
            {
                try
                {
                    if (_bootstrap is not null)
                    {
                        await _bootstrap.Log.WriteAsync("Fatal", args.ExceptionObject?.ToString() ?? "Unknown fatal error");
                    }
                }
                catch
                {
                }
            };

            var args = e.Args ?? Array.Empty<string>();
            if (TryParseCli(args, out var configPath, out var run, out var forceDangerous) && run)
            {
                var exitCode = await _bootstrap.CliRunner.RunAsync(configPath!, forceDangerous, CancellationToken.None);
                Shutdown(exitCode);
                return;
            }

            var window = new MainWindow
            {
                DataContext = _bootstrap.Main
            };

            MainWindow = window;
            window.Show();
            await _bootstrap.Main.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                if (_bootstrap is not null)
                {
                    await _bootstrap.Log.WriteAsync("StartupError", ex.ToString());
                }
            }
            catch
            {
            }

            MessageBox.Show(ex.ToString(), "Phantom Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            if (_bootstrap is not null)
            {
                await _bootstrap.Log.WriteAsync("DispatcherUnhandled", e.Exception.ToString());
            }
        }
        catch
        {
        }

        MessageBox.Show(e.Exception.ToString(), "Phantom Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            if (_bootstrap is not null)
            {
                _bootstrap.Log.WriteAsync("UnobservedTaskException", e.Exception.ToString()).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }

        e.SetObserved();
    }

    private static bool TryParseCli(string[] args, out string? configPath, out bool run, out bool forceDangerous)
    {
        configPath = null;
        run = false;
        forceDangerous = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (string.Equals(current, "-Config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
                continue;
            }

            if (string.Equals(current, "-Run", StringComparison.OrdinalIgnoreCase))
            {
                run = true;
                continue;
            }

            if (string.Equals(current, "-ForceDangerous", StringComparison.OrdinalIgnoreCase))
            {
                forceDangerous = true;
            }
        }

        return !string.IsNullOrWhiteSpace(configPath);
    }
}
