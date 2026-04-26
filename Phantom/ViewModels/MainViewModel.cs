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
    private readonly Dictionary<AppSection, Func<CancellationToken, Task>> _sectionInitializers;
    private readonly Dictionary<AppSection, Task> _sectionInitializationTasks = new();
    private readonly object _sectionInitializationGate = new();

    private NavigationItem? _selectedNavigation;
    private object? _currentSectionViewModel;
    private bool _isOperationRunning;
    private bool _initializationActivated;
    private bool _disposed;
    private int _pendingConsoleDispatches;

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
        _sectionInitializers = new Dictionary<AppSection, Func<CancellationToken, Task>>
        {
            [AppSection.Home] = Home.InitializeAsync,
            [AppSection.Apps] = Apps.InitializeAsync,
            [AppSection.Services] = Services.InitializeAsync,
            [AppSection.Store] = Store.InitializeAsync,
            [AppSection.Tweaks] = Tweaks.InitializeAsync,
            [AppSection.Features] = Features.InitializeAsync,
            [AppSection.Fixes] = Fixes.InitializeAsync,
            [AppSection.Updates] = Updates.InitializeAsync,
            [AppSection.Settings] = Settings.InitializeAsync
        };

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

            // Drop messages if dispatcher queue backlog exceeds threshold
            if (Interlocked.Increment(ref _pendingConsoleDispatches) > 500)
            {
                Interlocked.Decrement(ref _pendingConsoleDispatches);
                return;
            }

            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                Interlocked.Decrement(ref _pendingConsoleDispatches);
                AppendConsoleMessage(evt);
            });
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

            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => IsOperationRunning = running);
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
                StartSectionInitialization(value.Section);
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
        _initializationActivated = true;
        _console.Publish("Trace", "Main initialization started.");

        // Settings must load first (other VMs depend on it), then Home (visible tab).
        await EnsureSectionInitializedAsync(AppSection.Settings, cancellationToken).ConfigureAwait(false);
        await EnsureSectionInitializedAsync(AppSection.Home, cancellationToken).ConfigureAwait(false);

        _ = InitializeRemainingSectionsAsync();

        _console.Publish("Trace", "Main initialization completed.");
    }

    private void StartSectionInitialization(AppSection section)
    {
        if (!_initializationActivated)
        {
            return;
        }

        var task = EnsureSectionInitializedAsync(section, CancellationToken.None);
        if (!task.IsCompletedSuccessfully)
        {
            _ = ObserveSectionInitializationAsync(section, task);
        }
    }

    private async Task InitializeRemainingSectionsAsync()
    {
        try
        {
            _console.Publish("Trace", "Initializing remaining view models in background.");
            foreach (var section in new[]
                     {
                         AppSection.Tweaks,
                         AppSection.Store,
                         AppSection.Services,
                         AppSection.Apps,
                         AppSection.Features,
                         AppSection.Fixes,
                         AppSection.Updates
                     })
            {
                try
                {
                    await EnsureSectionInitializedAsync(section, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _console.Publish("Error", $"{section} initialization failed: {ex.Message}");
                }
            }

            _console.Publish("Trace", "Background view model initialization completed.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Background initialization failed: {ex.Message}");
        }
    }

    private Task EnsureSectionInitializedAsync(AppSection section, CancellationToken cancellationToken)
    {
        if (!_sectionInitializers.TryGetValue(section, out var initializer))
        {
            return Task.CompletedTask;
        }

        lock (_sectionInitializationGate)
        {
            if (_sectionInitializationTasks.TryGetValue(section, out var existing))
            {
                return existing;
            }

            var task = InitializeSectionAsync(section, initializer, cancellationToken);
            _sectionInitializationTasks[section] = task;
            _ = task.ContinueWith(
                completed =>
                {
                    if (!completed.IsFaulted && !completed.IsCanceled)
                    {
                        return;
                    }

                    lock (_sectionInitializationGate)
                    {
                        _sectionInitializationTasks.Remove(section);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task InitializeSectionAsync(
        AppSection section,
        Func<CancellationToken, Task> initializer,
        CancellationToken cancellationToken)
    {
        _console.Publish("Trace", $"Initializing {section} view model.");
        await initializer(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveSectionInitializationAsync(AppSection section, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", $"{section} initialization cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"{section} initialization failed: {ex.Message}");
        }
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
        if (Home is IDisposable disposableHome)
        {
            disposableHome.Dispose();
        }
        else
        {
            Home.StopTimer();
        }

        if (Tweaks is IDisposable disposableTweaks)
        {
            disposableTweaks.Dispose();
        }

        if (Features is IDisposable disposableFeatures)
        {
            disposableFeatures.Dispose();
        }

        if (Store is IDisposable disposableStore)
        {
            disposableStore.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
