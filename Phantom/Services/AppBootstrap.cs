using Phantom.ViewModels;

namespace Phantom.Services;

public sealed class AppBootstrap
{
    public AppBootstrap()
    {
        Paths = new AppPaths();
        JsonStore = new JsonFileStore();
        SettingsProvider = new SettingsProvider();
        Theme = new ThemeService();
        SettingsStore = new SettingsStore(JsonStore, Paths);
        TelemetryStore = new TelemetryStore(JsonStore, Paths);
        UndoStore = new UndoStateStore(JsonStore, Paths);

        Console = new ConsoleStreamService();
        Log = new LogService(Paths, () => SettingsProvider.Current);
        Log.OpenSessionLog();
        Console.MessageReceived += (_, evt) =>
        {
            try
            {
                Log.WriteAsync($"Console.{evt.Stream}", evt.Text).GetAwaiter().GetResult();
            }
            catch
            {
            }
        };
        Log.WriteAsync("Lifecycle", "App bootstrap initialized.").GetAwaiter().GetResult();

        Network = new NetworkGuardService();
        Query = new PowerShellQueryService();
        Runner = new PowerShellRunner(Console, Log);
        Operations = new OperationEngine(Runner, UndoStore, Network, Console, Log);
        Definitions = new DefinitionCatalogService(Paths);
        Prompt = new UserPromptService();
        ExecutionCoordinator = new ExecutionCoordinator();
        HomeData = new HomeDataService(Console, TelemetryStore);

        Settings = new SettingsViewModel(SettingsStore, Log, SettingsProvider, Theme);
        Home = new HomeViewModel(HomeData, TelemetryStore, () => SettingsProvider.Current, Console);
        Apps = new AppsViewModel(HomeData, Console);
        Services = new ServicesViewModel(HomeData, Console);
        Store = new StoreViewModel(Definitions, Operations, ExecutionCoordinator, Prompt, Console, Network, Query, () => SettingsProvider.Current);
        Tweaks = new TweaksViewModel(Definitions, Operations, ExecutionCoordinator, Prompt, Console, Query, () => SettingsProvider.Current);
        Features = new FeaturesViewModel(Definitions, Operations, ExecutionCoordinator, Prompt, Console, Query, Runner, () => SettingsProvider.Current);
        Fixes = new FixesViewModel(Definitions, Operations, ExecutionCoordinator, Prompt, Console, Runner, () => SettingsProvider.Current);
        Updates = new UpdatesViewModel(Operations, ExecutionCoordinator, Prompt, Console, Query, () => SettingsProvider.Current);
        LogsAbout = new LogsAboutViewModel(Paths);
        Automation = new AutomationViewModel(Definitions, Store, Tweaks, Features, Fixes, Updates);

        Main = new MainViewModel(Home, Apps, Services, Store, Tweaks, Features, Fixes, Updates, Automation, LogsAbout, Settings, Console, ExecutionCoordinator, Paths);
        CliRunner = new CliRunner(Paths, Definitions, Operations, Console, Log, Network, Query, SettingsStore);
    }

    public AppPaths Paths { get; }
    public JsonFileStore JsonStore { get; }
    public SettingsProvider SettingsProvider { get; }
    public ThemeService Theme { get; }
    public SettingsStore SettingsStore { get; }
    public TelemetryStore TelemetryStore { get; }
    public UndoStateStore UndoStore { get; }
    public ConsoleStreamService Console { get; }
    public LogService Log { get; }
    public NetworkGuardService Network { get; }
    public PowerShellQueryService Query { get; }
    public IPowerShellRunner Runner { get; }
    public OperationEngine Operations { get; }
    public DefinitionCatalogService Definitions { get; }
    public IUserPromptService Prompt { get; }
    public ExecutionCoordinator ExecutionCoordinator { get; }
    public HomeDataService HomeData { get; }

    public SettingsViewModel Settings { get; }
    public HomeViewModel Home { get; }
    public AppsViewModel Apps { get; }
    public ServicesViewModel Services { get; }
    public StoreViewModel Store { get; }
    public TweaksViewModel Tweaks { get; }
    public FeaturesViewModel Features { get; }
    public FixesViewModel Fixes { get; }
    public UpdatesViewModel Updates { get; }
    public LogsAboutViewModel LogsAbout { get; }
    public AutomationViewModel Automation { get; }

    public MainViewModel Main { get; }
    public CliRunner CliRunner { get; }
}
