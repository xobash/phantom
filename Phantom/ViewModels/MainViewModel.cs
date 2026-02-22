using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly AppPaths _paths;

    private NavigationItem? _selectedNavigation;
    private object? _currentSectionViewModel;
    private bool _isOperationRunning;

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
        LogsAboutViewModel logsAbout,
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
        LogsAbout = logsAbout;
        Settings = settings;

        _executionCoordinator = executionCoordinator;
        _paths = paths;

        Navigation = new ObservableCollection<NavigationItem>
        {
            new() { Section = AppSection.Home, Label = "Home", Icon = "\uE80F" },
            new() { Section = AppSection.Apps, Label = "Apps", Icon = "\uE8F1" },
            new() { Section = AppSection.Services, Label = "Services", Icon = "\uE895" },
            new() { Section = AppSection.Store, Label = "Store", Icon = "\uE719" },
            new() { Section = AppSection.Tweaks, Label = "Tweaks", Icon = "\uE713" },
            new() { Section = AppSection.Features, Label = "Features", Icon = "\uE115" },
            new() { Section = AppSection.Fixes, Label = "Fixes", Icon = "\uE90F" },
            new() { Section = AppSection.Updates, Label = "Updates", Icon = "\uE895" },
            new() { Section = AppSection.LogsAbout, Label = "Logs/About", Icon = "\uE9D2" },
            new() { Section = AppSection.Settings, Label = "Settings", Icon = "\uE713" }
        };

        ConsoleMessages = new ObservableCollection<PowerShellOutputEvent>();
        ConsoleView = CollectionViewSource.GetDefaultView(ConsoleMessages);

        console.MessageReceived += (_, evt) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ConsoleMessages.Add(evt);
                if (ConsoleMessages.Count > 4000)
                {
                    ConsoleMessages.RemoveAt(0);
                }
            });
        };

        _executionCoordinator.RunningChanged += (_, running) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                IsOperationRunning = running;
                return;
            }

            dispatcher.BeginInvoke(new Action(() => IsOperationRunning = running));
        };

        CancelCurrentOperationCommand = new RelayCommand(() => _executionCoordinator.Cancel());
        CopyLogCommand = new RelayCommand(() =>
        {
            var text = string.Join(Environment.NewLine, ConsoleMessages.Select(m => $"[{m.Timestamp:HH:mm:ss}] [{m.Stream}] {m.Text}"));
            Clipboard.SetText(text);
        });
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);

        SelectedNavigation = Navigation.First();
    }

    public ObservableCollection<NavigationItem> Navigation { get; }

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
    public LogsAboutViewModel LogsAbout { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand CancelCurrentOperationCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (SetProperty(ref _selectedNavigation, value) && value is not null)
            {
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
                    AppSection.LogsAbout => LogsAbout,
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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Settings.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Home.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Apps.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Services.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Tweaks.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Features.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Fixes.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Updates.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await LogsAbout.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OpenLogsFolder()
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
}
