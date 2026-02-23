using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Phantom.Services;
using Phantom.Views;

namespace Phantom;

public partial class App : Application
{
    private AppBootstrap? _bootstrap;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WriteEmergencyStartupTrace($"OnStartup invoked at {DateTimeOffset.Now:O}");
        WriteEmergencyStartupTrace($"Startup args: {string.Join(' ', e.Args ?? Array.Empty<string>())}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteEmergencyStartupTrace($"ProcessExit at {DateTimeOffset.Now:O}");
        _ = StartAsync(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_bootstrap?.Main is IDisposable disposableMain)
            {
                disposableMain.Dispose();
            }
        }
        catch (Exception ex)
        {
            WriteEmergencyStartupTrace($"OnExit disposal failed: {ex}");
        }

        base.OnExit(e);
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        try
        {
            WriteEmergencyStartupTrace("StartAsync entered.");
            if (!AdminGuard.IsAdministrator())
            {
                WriteEmergencyStartupTrace("Administrator check failed.");
                if (TryRelaunchElevated(e.Args ?? Array.Empty<string>(), out var relaunchMessage))
                {
                    WriteEmergencyStartupTrace("Elevation relaunch started successfully.");
                    Shutdown(0);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(relaunchMessage))
                {
                    WriteEmergencyStartupTrace($"Elevation relaunch failed: {relaunchMessage}");
                }

                MessageBox.Show("Phantom requires Administrator privileges. Please approve the elevation prompt and relaunch.", "Phantom", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(10);
                return;
            }

            _bootstrap = new AppBootstrap();
            var initialSettings = await _bootstrap.SettingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            _bootstrap.SettingsProvider.Update(initialSettings);
            await Dispatcher.InvokeAsync(() => _bootstrap.Theme.ApplyTheme(initialSettings.UseDarkMode));
            _bootstrap.Console.Publish("Trace", $"App startup started at {DateTimeOffset.Now:O}");
            _bootstrap.Console.Publish("Trace", $"Startup args: {string.Join(' ', e.Args ?? Array.Empty<string>())}");
            await _bootstrap.Log.WriteAsync("Trace", "App bootstrap created.");

            AppDomain.CurrentDomain.UnhandledException += async (_, args) =>
            {
                try
                {
                    if (_bootstrap is not null)
                    {
                        _bootstrap.Console.Publish("Fatal", args.ExceptionObject?.ToString() ?? "Unknown fatal error");
                        await _bootstrap.Log.WriteAsync("Fatal", args.ExceptionObject?.ToString() ?? "Unknown fatal error");
                    }
                    else
                    {
                        WriteEmergencyStartupTrace($"Fatal before bootstrap: {args.ExceptionObject}");
                    }
                }
                catch
                {
                }
            };

            var args = e.Args ?? Array.Empty<string>();
            if (TryParseCli(args, out var configPath, out var run, out var forceDangerous) && run)
            {
                _bootstrap.Console.Publish("Trace", $"CLI mode requested. configPath={configPath}, forceDangerous={forceDangerous}");
                var exitCode = await _bootstrap.CliRunner.RunAsync(configPath!, forceDangerous, CancellationToken.None);
                _bootstrap.Console.Publish("Trace", $"CLI mode completed with exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            _bootstrap.Console.Publish("Trace", "Creating MainWindow instance.");
            await Dispatcher.InvokeAsync(StartAmbientAnimations);
            await Dispatcher.InvokeAsync(() =>
            {
                var window = new MainWindow
                {
                    DataContext = _bootstrap.Main
                };

                MainWindow = window;
                window.Show();
            });
            _bootstrap.Console.Publish("Trace", "MainWindow shown.");
            await _bootstrap.Main.InitializeAsync(CancellationToken.None);
            _bootstrap.Console.Publish("Trace", "Startup completed.");
        }
        catch (Exception ex)
        {
            try
            {
                if (_bootstrap is not null)
                {
                    _bootstrap.Console.Publish("StartupError", ex.ToString());
                    await _bootstrap.Log.WriteAsync("StartupError", ex.ToString());
                }
                else
                {
                    WriteEmergencyStartupTrace($"StartupError before bootstrap: {ex}");
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
                _bootstrap.Console.Publish("DispatcherUnhandled", e.Exception.ToString());
                await _bootstrap.Log.WriteAsync("DispatcherUnhandled", e.Exception.ToString());
            }
            else
            {
                WriteEmergencyStartupTrace($"DispatcherUnhandled before bootstrap: {e.Exception}");
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
                _bootstrap.Console.Publish("UnobservedTaskException", e.Exception.ToString());
                _bootstrap.Log.WriteAsync("UnobservedTaskException", e.Exception.ToString()).GetAwaiter().GetResult();
            }
            else
            {
                WriteEmergencyStartupTrace($"UnobservedTaskException before bootstrap: {e.Exception}");
            }
        }
        catch
        {
        }

        e.SetObserved();
    }

    private static void WriteEmergencyStartupTrace(string message)
    {
        try
        {
            var appRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Phantom",
                "app");
            var logsDir = Path.Combine(appRoot, "logs");
            Directory.CreateDirectory(logsDir);
            var line = $"[{DateTimeOffset.Now:O}] [Emergency] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logsDir, "startup-emergency.log"), line);
        }
        catch
        {
        }
    }

    private static bool TryRelaunchElevated(string[] args, out string message)
    {
        message = string.Empty;

        try
        {
            var fileName = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                message = "Unable to resolve current executable path.";
                return false;
            }

            var quotedArgs = string.Join(" ", args.Select(QuoteArgument));
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = quotedArgs,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "Elevation prompt was cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Contains(' ') && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private void StartAmbientAnimations()
    {
        try
        {
            if (Resources["AppBgBrush"] is not LinearGradientBrush appBrush)
            {
                return;
            }

            var duration = new Duration(TimeSpan.FromSeconds(24));
            var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

            appBrush.BeginAnimation(
                LinearGradientBrush.StartPointProperty,
                new PointAnimation
                {
                    From = new Point(0, 0),
                    To = new Point(0.12, 0.06),
                    Duration = duration,
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = easing
                });

            appBrush.BeginAnimation(
                LinearGradientBrush.EndPointProperty,
                new PointAnimation
                {
                    From = new Point(1, 1),
                    To = new Point(0.88, 0.94),
                    Duration = duration,
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = easing
                });
        }
        catch (Exception ex)
        {
            WriteEmergencyStartupTrace($"Ambient animation setup failed: {ex}");
        }
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
