using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly AppPaths _paths;
    private readonly ConsoleStreamService _console;
    private readonly EventHandler<PowerShellOutputEvent> _consoleMessageReceivedHandler;
    private readonly EventHandler<bool> _runningChangedHandler;
    private readonly NavigationItem _settingsNavigationItem;

    private NavigationItem? _selectedNavigation;
    private object? _currentSectionViewModel;
    private bool _isOperationRunning;
    private bool _disposed;

    public MainViewModel(
        HomeViewModel home,
        AppsViewModel apps,
        ServicesViewModel services,
        StoreViewModel store,
        TweaksViewModel tweaks,
        FeaturesViewModel features,
        FixesViewModel fixes,
        UpdatesViewModel updates,
        AutomationViewModel automation,
        SettingsViewModel settings,
        ConsoleStreamService console,
        ExecutionCoordinator executionCoordinator,
        AppPaths paths)
    {
        Home = home;
        Apps = apps;
        Services = services;
        Store = store;
        Tweaks = tweaks;
        Features = features;
        Fixes = fixes;
        Updates = updates;
        Automation = automation;
        Settings = settings;

        _executionCoordinator = executionCoordinator;
        _paths = paths;
        _console = console;

        Navigation = new ObservableCollection<NavigationItem>
        {
            new() { Section = AppSection.Home, Label = "Home", Icon = "\uE80F" },
            new() { Section = AppSection.Apps, Label = "Apps", Icon = "\uE8F1" },
            new() { Section = AppSection.Services, Label = "Services", Icon = "\uE895" },
            new() { Section = AppSection.Store, Label = "Store", Icon = "\uE719" },
            new() { Section = AppSection.Tweaks, Label = "Tweaks", Icon = "\uE713" },
            new() { Section = AppSection.Features, Label = "Features", Icon = "\uE115" },
            new() { Section = AppSection.Fixes, Label = "Fixes", Icon = "\uE90F" },
            new() { Section = AppSection.Updates, Label = "Updates", Icon = "\uE895" }
        };
        _settingsNavigationItem = new NavigationItem { Section = AppSection.Settings, Label = "Settings", Icon = "\uE713" };

        ConsoleMessages = new ObservableCollection<PowerShellOutputEvent>();
        ConsoleView = CollectionViewSource.GetDefaultView(ConsoleMessages);
        foreach (var evt in console.Snapshot)
        {
            ConsoleMessages.Add(evt);
        }

        _consoleMessageReceivedHandler = (_, evt) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                AppendConsoleMessage(evt);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => AppendConsoleMessage(evt)));
        };
        console.MessageReceived += _consoleMessageReceivedHandler;

        _runningChangedHandler = (_, running) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                IsOperationRunning = running;
                return;
            }

            dispatcher.BeginInvoke(new Action(() => IsOperationRunning = running));
        };
        _executionCoordinator.RunningChanged += _runningChangedHandler;

        CancelCurrentOperationCommand = new RelayCommand(() => _executionCoordinator.Cancel());
        CopyLogCommand = new AsyncRelayCommand(CopyLogAsync);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        OpenSettingsCommand = new RelayCommand(() => SelectedNavigation = _settingsNavigationItem);

        SelectedNavigation = Navigation.First();
        _console.Publish("Trace", "MainViewModel constructor complete.");
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public NavigationItem SettingsNavigationItem => _settingsNavigationItem;

    public ObservableCollection<PowerShellOutputEvent> ConsoleMessages { get; }

    public ICollectionView ConsoleView { get; }

    public HomeViewModel Home { get; }
    public AppsViewModel Apps { get; }
    public ServicesViewModel Services { get; }
    public StoreViewModel Store { get; }
    public TweaksViewModel Tweaks { get; }
    public FeaturesViewModel Features { get; }
    public FixesViewModel Fixes { get; }
    public UpdatesViewModel Updates { get; }
    public AutomationViewModel Automation { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand CancelCurrentOperationCommand { get; }
    public AsyncRelayCommand CopyLogCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (SetProperty(ref _selectedNavigation, value) && value is not null)
            {
                _console.Publish("Trace", $"Navigation selected: {value.Section}");
                Notify(nameof(IsSettingsSelected));
                CurrentSectionViewModel = value.Section switch
                {
                    AppSection.Home => Home,
                    AppSection.Apps => Apps,
                    AppSection.Services => Services,
                    AppSection.Store => Store,
                    AppSection.Tweaks => Tweaks,
                    AppSection.Features => Features,
                    AppSection.Fixes => Fixes,
                    AppSection.Updates => Updates,
                    AppSection.Settings => Settings,
                    _ => Home
                };
            }
        }
    }

    public object? CurrentSectionViewModel
    {
        get => _currentSectionViewModel;
        set => SetProperty(ref _currentSectionViewModel, value);
    }

    public bool IsOperationRunning
    {
        get => _isOperationRunning;
        set => SetProperty(ref _isOperationRunning, value);
    }

    public bool IsSettingsSelected => SelectedNavigation?.Section == AppSection.Settings;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _console.Publish("Trace", "Main initialization started.");
        _console.Publish("Trace", "Initializing Settings view model.");
        await Settings.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Home view model.");
        await Home.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Apps view model.");
        await Apps.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Services view model.");
        await Services.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Store view model.");
        await Store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Tweaks view model.");
        await Tweaks.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Features view model.");
        await Features.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Fixes view model.");
        await Fixes.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Initializing Updates view model.");
        await Updates.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", "Main initialization completed.");
    }

    private void OpenLogsFolder()
    {
        try
        {
            if (!Directory.Exists(_paths.LogsDirectory))
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Failed to open logs folder: {ex.Message}");
        }
    }

    private async Task CopyLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = ConsoleMessages.ToArray();
            var text = await Task.Run(() => BuildLogSnapshotText(snapshot), cancellationToken);
            var copied = await TrySetClipboardTextAsync(text, cancellationToken);
            if (!copied)
            {
                _console.Publish("Warning", "Copy log skipped: clipboard is currently busy.");
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Copy log cancelled.");
        }
        catch (Exception ex)
        {
            if (IsClipboardBusyException(ex))
            {
                _console.Publish("Warning", "Copy log skipped: clipboard is currently busy.");
                return;
            }

            _console.Publish("Error", $"Copy log failed: {ex.Message}");
        }
    }

    private static string BuildLogSnapshotText(IEnumerable<PowerShellOutputEvent> snapshot)
    {
        var text = string.Join(Environment.NewLine, snapshot.Select(m => $"[{m.Timestamp:HH:mm:ss}] [{m.Stream}] {m.Text}"));
        return string.IsNullOrWhiteSpace(text) ? "No console log entries." : text;
    }

    private void AppendConsoleMessage(PowerShellOutputEvent evt)
    {
        ConsoleMessages.Add(evt);
        if (ConsoleMessages.Count > 4000)
        {
            ConsoleMessages.RemoveAt(0);
        }
    }

    private static Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                const int maxAttempts = 12;
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Clipboard.SetDataObject(text, true);
                        tcs.TrySetResult(true);
                        return;
                    }
                    catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
                    {
                        Thread.Sleep(20 + (attempt * 10));
                    }
                    catch (ExternalException ex) when ((uint)ex.HResult == 0x800401D0)
                    {
                        Thread.Sleep(20 + (attempt * 10));
                    }
                }

                tcs.TrySetResult(false);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                tcs.TrySetResult(false);
            }
            catch (ExternalException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                tcs.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static bool IsClipboardBusyException(Exception ex)
    {
        if (ex is COMException comEx && (uint)comEx.HResult == 0x800401D0)
        {
            return true;
        }

        if (ex is ExternalException externalEx && (uint)externalEx.HResult == 0x800401D0)
        {
            return true;
        }

        return ex.InnerException is not null && IsClipboardBusyException(ex.InnerException);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _console.MessageReceived -= _consoleMessageReceivedHandler;
        _executionCoordinator.RunningChanged -= _runningChangedHandler;
        Home.StopTimer();
        if (Tweaks is IDisposable disposableTweaks)
        {
            disposableTweaks.Dispose();
        }

        if (Features is IDisposable disposableFeatures)
        {
            disposableFeatures.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
