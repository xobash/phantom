using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class StoreViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly NetworkGuardService _networkGuard;
    private readonly StoreInstallService _storeInstallService;
    private readonly PackageExecutionService _packageExecution;
    private readonly Func<AppSettings> _settingsAccessor;

    private bool _wingetInstalled;
    private bool _chocoInstalled;
    private bool _managerInstallerExpanded;
    private string _managerSummary = "Package managers: unknown";
    private string _search = string.Empty;
    private bool _disposed;

    public StoreViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        NetworkGuardService networkGuard,
        StoreInstallService storeInstallService,
        PackageExecutionService packageExecution,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _networkGuard = networkGuard;
        _storeInstallService = storeInstallService;
        _packageExecution = packageExecution;
        _settingsAccessor = settingsAccessor;

        Catalog = new ObservableCollection<CatalogApp>();
        PackageManagers = new ObservableCollection<StorePackageManagerOption>();
        CatalogView = CollectionViewSource.GetDefaultView(Catalog);
        CatalogView.Filter = FilterCatalog;
        using (CatalogView.DeferRefresh())
        {
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.Category), ListSortDirection.Ascending));
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.DisplayName), ListSortDirection.Ascending));
            CatalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogApp.Category)));
        }

        RefreshManagersCommand = new AsyncRelayCommand(ct => RefreshManagersAsync(ct, echoToConsole: true), CanRunStoreOperation);
        InstallSelectedManagersCommand = new AsyncRelayCommand(InstallSelectedManagersAsync, CanRunStoreOperation);
        RefreshPackageStatusCommand = new AsyncRelayCommand(RefreshPackageStatusAsync, CanRunStoreOperation);
        DiscoverSelectedCommand = new AsyncRelayCommand(DiscoverSelectedAsync, CanRunStoreOperation);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, CanRunStoreOperation);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, CanRunStoreOperation);
        UpgradeSelectedCommand = new AsyncRelayCommand(UpgradeSelectedAsync, CanRunStoreOperation);

        _executionCoordinator.RunningChanged += OnExecutionCoordinatorRunningChanged;
    }

    public string Title => "Store";

    public ObservableCollection<CatalogApp> Catalog { get; }
    public ObservableCollection<StorePackageManagerOption> PackageManagers { get; }
    public ICollectionView CatalogView { get; }

    public AsyncRelayCommand RefreshManagersCommand { get; }
    public AsyncRelayCommand InstallSelectedManagersCommand { get; }
    public AsyncRelayCommand RefreshPackageStatusCommand { get; }
    public AsyncRelayCommand DiscoverSelectedCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand UninstallSelectedCommand { get; }
    public AsyncRelayCommand UpgradeSelectedCommand { get; }

    public bool WingetInstalled
    {
        get => _wingetInstalled;
        set
        {
            if (!SetProperty(ref _wingetInstalled, value))
            {
                return;
            }
        }
    }

    public bool ChocoInstalled
    {
        get => _chocoInstalled;
        set
        {
            if (!SetProperty(ref _chocoInstalled, value))
            {
                return;
            }
        }
    }

    public string ManagerInstallerChevron => ManagerInstallerExpanded ? "▾" : "▸";

    public string ManagerSummary
    {
        get => _managerSummary;
        set => SetProperty(ref _managerSummary, value);
    }

    public bool ManagerInstallerExpanded
    {
        get => _managerInstallerExpanded;
        set
        {
            if (SetProperty(ref _managerInstallerExpanded, value))
            {
                Notify(nameof(ManagerInstallerChevron));
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                CatalogView.Refresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var apps = await _catalogService.LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Catalog.Clear();
            foreach (var app in apps)
            {
                app.SourceSummary = BuildSourceSummary(app);
                app.PurposeSummary = BuildPurposeSummary(app);
                app.Status = app.ManualOnly ? "Manual-only" : "Ready";
                Catalog.Add(app);
            }
        });

        await RefreshManagersAsync(cancellationToken, echoToConsole: false).ConfigureAwait(false);
    }

    private async Task RefreshManagersAsync(CancellationToken cancellationToken, bool echoToConsole = true)
    {
        var availability = await _storeInstallService.GetManagerAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        WingetInstalled = availability.Winget.IsAvailable;
        ChocoInstalled = availability.Chocolatey.IsAvailable;
        ManagerSummary = BuildManagerSummary(availability);
        await Application.Current.Dispatcher.InvokeAsync(() => UpdateManagerOptions(availability));
        if (echoToConsole)
        {
            foreach (var (manager, resolution) in availability.All())
            {
                _console.Publish(
                    "Trace",
                    $"{GetManagerDisplayName(manager)} resolver: {(resolution.IsAvailable ? resolution.ExecutablePath : resolution.Message)}");
            }
        }

        _console.Publish("Info", ManagerSummary);
        InstallSelectedManagersCommand.RaiseCanExecuteChanged();
    }

    private async Task InstallMissingManagersAsync(CancellationToken cancellationToken)
    {
        await InstallSelectedManagersAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallSelectedManagersAsync(CancellationToken cancellationToken)
    {
        var selectedManagers = await Application.Current.Dispatcher.InvokeAsync(() =>
            PackageManagers
                .Where(option => option.Selected && option.CanInstall)
                .Select(option => option.Manager)
                .Distinct()
                .ToList());

        if (selectedManagers.Count == 0)
        {
            _console.Publish("Info", "No missing package managers selected.");
            return;
        }

        var availability = await _storeInstallService.GetManagerAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        var installed = availability.All()
            .Where(item => item.Resolution.IsAvailable)
            .Select(item => item.Manager)
            .ToHashSet();

        var operations = new List<OperationDefinition>();
        var needsWinget = selectedManagers.Any(RequiresWingetInstaller) &&
                          !installed.Contains(StorePackageManager.Winget) &&
                          !selectedManagers.Contains(StorePackageManager.Winget);

        if (needsWinget)
        {
            operations.Add(BuildInstallWingetOperation());
        }

        foreach (var manager in selectedManagers)
        {
            if (installed.Contains(manager))
            {
                continue;
            }

            operations.Add(BuildInstallManagerOperation(manager));
        }

        if (operations.Count == 0)
        {
            _console.Publish("Info", "Selected package managers are already installed.");
            return;
        }

        if (await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false))
        {
            await RefreshManagersAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InstallWingetAsync(CancellationToken cancellationToken)
    {
        await ExecuteManagerOperationAsync(BuildInstallWingetOperation(), cancellationToken).ConfigureAwait(false);
    }

    private async Task UninstallWingetAsync(CancellationToken cancellationToken)
    {
        await ExecuteManagerOperationAsync(BuildUninstallWingetOperation(), cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallChocoAsync(CancellationToken cancellationToken)
    {
        await ExecuteManagerOperationAsync(BuildInstallChocoOperation(), cancellationToken).ConfigureAwait(false);
    }

    private async Task UninstallChocoAsync(CancellationToken cancellationToken)
    {
        await ExecuteManagerOperationAsync(BuildUninstallChocoOperation(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ToggleWingetAsync(CancellationToken cancellationToken)
    {
        if (WingetInstalled)
        {
            await UninstallWingetAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await InstallWingetAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ToggleChocoAsync(CancellationToken cancellationToken)
    {
        if (ChocoInstalled)
        {
            await UninstallChocoAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await InstallChocoAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallSelectedAsync(CancellationToken cancellationToken)
    {
        await ExecutePackageSelectionAsync(PackageAction.Install, cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = await Application.Current.Dispatcher.InvokeAsync(() => Catalog.Where(x => x.Selected).ToList());
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No items selected.");
            return;
        }

        var results = await _packageExecution.DiscoverAsync(selected, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            _console.Publish(result.Success ? "Info" : "Error", $"{result.App.DisplayName} discovery: {result.Message}");
        }
    }

    private async Task UninstallSelectedAsync(CancellationToken cancellationToken)
    {
        await ExecutePackageSelectionAsync(PackageAction.Uninstall, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpgradeSelectedAsync(CancellationToken cancellationToken)
    {
        await ExecutePackageSelectionAsync(PackageAction.Upgrade, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshPackageStatusAsync(CancellationToken cancellationToken)
    {
        var apps = await Application.Current.Dispatcher.InvokeAsync(() => Catalog.ToList());
        var updates = await _packageExecution.GetStatusAsync(apps, cancellationToken).ConfigureAwait(false);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var update in updates)
            {
                update.App.Status = update.Status;
                update.App.InstalledVersion = update.InstalledVersion;
                update.App.AvailableVersion = update.AvailableVersion;
                update.App.SourceSummary = update.SourceSummary;
            }

            CatalogView.Refresh();
        });

        _console.Publish("Info", $"Package state refreshed: {updates.Count} catalog entries.");
    }

    private async Task ExecutePackageSelectionAsync(PackageAction action, CancellationToken externalToken)
    {
        var selected = await Application.Current.Dispatcher.InvokeAsync(() => Catalog.Where(x => x.Selected).ToList());
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No items selected.");
            return;
        }

        if (action != PackageAction.Uninstall && !_networkGuard.IsOnline())
        {
            _console.Publish("Error", "Offline detected. Store action blocked before execution.");
            return;
        }

        CancellationToken token;
        try
        {
            token = _executionCoordinator.Begin();
        }
        catch (InvalidOperationException ex)
        {
            _console.Publish("Warning", ex.Message);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, externalToken);
        try
        {
            var results = await _packageExecution.ExecuteAsync(selected, action, linked.Token).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var result in results)
                {
                    result.App.Status = result.Success
                        ? action switch
                        {
                            PackageAction.Install => "Installed",
                            PackageAction.Uninstall => "Ready",
                            PackageAction.Upgrade => "Updated",
                            _ => "Done"
                        }
                        : "Failed";
                }

                CatalogView.Refresh();
            });

            foreach (var result in results)
            {
                _console.Publish(result.Success ? "Info" : "Error", $"{result.App.DisplayName}: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Store package operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Store package operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private async Task<bool> ExecuteStoreOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool dryRun, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
            _console.Publish("Info", "No automatic package operations selected.");
            return false;
        }

        if (!_networkGuard.IsOnline())
        {
            _console.Publish("Error", "Offline detected. Store action blocked before execution.");
            return false;
        }

        CancellationToken token;
        try
        {
            token = _executionCoordinator.Begin();
        }
        catch (InvalidOperationException ex)
        {
            _console.Publish("Warning", ex.Message);
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, externalToken);
        try
        {
            var precheck = await _operationEngine.RunBatchPrecheckAsync(operations, linked.Token).ConfigureAwait(false);
            if (!precheck.IsSuccess)
            {
                _console.Publish("Error", precheck.Message);
                return true;
            }

            var response = await _operationEngine.ExecuteBatchAsync(new OperationRequest
            {
                Operations = operations,
                Undo = false,
                DryRun = dryRun,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = false,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            foreach (var op in response.Results)
            {
                _console.Publish(op.Success ? "Info" : "Error", $"{op.OperationId}: {op.Message}");
                var item = Catalog.FirstOrDefault(x =>
                    op.OperationId.EndsWith("." + SanitizeId(x.DisplayName), StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    item.Status = op.Success ? "Done" : "Failed";
                }
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Store operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Store operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }

        return true;
    }

    private static string SanitizeId(string source)
    {
        return new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private async Task ExecuteManagerOperationAsync(OperationDefinition operation, CancellationToken cancellationToken)
    {
        if (await ExecuteStoreOperationsAsync([operation], dryRun: false, cancellationToken).ConfigureAwait(false))
        {
            await RefreshManagersAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool CanRunStoreOperation() => !_executionCoordinator.IsRunning;

    private void OnExecutionCoordinatorRunningChanged(object? sender, bool _)
    {
        RefreshManagersCommand.RaiseCanExecuteChanged();
        InstallSelectedManagersCommand.RaiseCanExecuteChanged();
        InstallMissingManagersCommand.RaiseCanExecuteChanged();
        InstallWingetCommand.RaiseCanExecuteChanged();
        UninstallWingetCommand.RaiseCanExecuteChanged();
        InstallChocoCommand.RaiseCanExecuteChanged();
        UninstallChocoCommand.RaiseCanExecuteChanged();
        ToggleWingetCommand.RaiseCanExecuteChanged();
        ToggleChocoCommand.RaiseCanExecuteChanged();
        RefreshPackageStatusCommand.RaiseCanExecuteChanged();
        DiscoverSelectedCommand.RaiseCanExecuteChanged();
        InstallSelectedCommand.RaiseCanExecuteChanged();
        UninstallSelectedCommand.RaiseCanExecuteChanged();
        UpgradeSelectedCommand.RaiseCanExecuteChanged();
    }

    private void UpdateManagerOptions(StoreManagerAvailability availability)
    {
        var selected = PackageManagers
            .Where(option => option.Selected)
            .Select(option => option.Manager)
            .ToHashSet();

        PackageManagers.Clear();
        foreach (var (manager, resolution) in availability.All())
        {
            var isInstalled = resolution.IsAvailable;
            PackageManagers.Add(new StorePackageManagerOption(manager)
            {
                DisplayName = GetManagerDisplayName(manager),
                Description = GetManagerDescription(manager),
                Status = isInstalled ? "Present" : "Missing",
                Source = isInstalled ? resolution.Source : resolution.Message,
                IsInstalled = isInstalled,
                CanInstall = !isInstalled,
                Selected = !isInstalled && selected.Contains(manager)
            });
        }
    }

    private static OperationDefinition BuildInstallManagerOperation(StorePackageManager manager)
    {
        return manager switch
        {
            StorePackageManager.Winget => BuildInstallWingetOperation(),
            StorePackageManager.Scoop => BuildWingetPackageInstallOperation(
                manager,
                "Install Scoop",
                "Installs Scoop package manager for user-scoped CLI packages.",
                "ScoopInstaller.Scoop",
                "scoop"),
            StorePackageManager.Chocolatey => BuildInstallChocoOperation(),
            StorePackageManager.Pip => BuildWingetPackageInstallOperation(
                manager,
                "Install Python and pip",
                "Installs Python 3.12, which includes pip package management.",
                "Python.Python.3.12",
                "pip"),
            StorePackageManager.Npm => BuildWingetPackageInstallOperation(
                manager,
                "Install Node.js and npm",
                "Installs Node.js LTS, which includes npm package management.",
                "OpenJS.NodeJS.LTS",
                "npm"),
            StorePackageManager.DotNetTool => BuildWingetPackageInstallOperation(
                manager,
                "Install .NET SDK",
                "Installs .NET SDK 8 for dotnet tool package management.",
                "Microsoft.DotNet.SDK.8",
                "dotnet"),
            StorePackageManager.PowerShellGallery => BuildWingetPackageInstallOperation(
                manager,
                "Install PowerShell",
                "Installs PowerShell 7 for PowerShell Gallery operations.",
                "Microsoft.PowerShell",
                "pwsh"),
            _ => throw new ArgumentOutOfRangeException(nameof(manager), manager, "Unsupported package manager.")
        };
    }

    private static bool RequiresWingetInstaller(StorePackageManager manager) => manager != StorePackageManager.Winget;

    private static string GetManagerDisplayName(StorePackageManager manager)
    {
        return manager switch
        {
            StorePackageManager.Winget => "WinGet",
            StorePackageManager.Scoop => "Scoop",
            StorePackageManager.Chocolatey => "Chocolatey",
            StorePackageManager.Pip => "pip",
            StorePackageManager.Npm => "npm",
            StorePackageManager.DotNetTool => ".NET tools",
            StorePackageManager.PowerShellGallery => "PowerShell Gallery",
            _ => manager.ToString()
        };
    }

    private static string GetManagerDescription(StorePackageManager manager)
    {
        return manager switch
        {
            StorePackageManager.Winget => "Microsoft's Windows package manager. Used as Phantom's preferred installer source.",
            StorePackageManager.Scoop => "User-scoped command-line package manager for portable tools and developer utilities.",
            StorePackageManager.Chocolatey => "Machine-wide Windows package manager used as a fallback for supported catalog items.",
            StorePackageManager.Pip => "Python package manager. Phantom installs Python 3.12 when pip is selected.",
            StorePackageManager.Npm => "Node.js package manager. Phantom installs Node.js LTS when npm is selected.",
            StorePackageManager.DotNetTool => ".NET SDK tooling for installing dotnet global tools.",
            StorePackageManager.PowerShellGallery => "PowerShell module source used for PowerShell Gallery package operations.",
            _ => "Package manager used by Store catalog entries."
        };
    }

    private static OperationDefinition BuildWingetPackageInstallOperation(
        StorePackageManager manager,
        string title,
        string description,
        string wingetId,
        string validationCommand)
    {
        var safeId = manager.ToString().ToLowerInvariant();
        return new OperationDefinition
        {
            Id = $"store.manager.install.{safeId}",
            Title = title,
            Description = description,
            RiskTier = RiskTier.Advanced,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "install",
                    RequiresNetwork = true,
                    Script = $$"""
                             $ErrorActionPreference='Stop'
                             Set-StrictMode -Version Latest

                             function Resolve-PhantomWinget {
                               $cmd = Get-Command winget -ErrorAction SilentlyContinue
                               if($null -ne $cmd){ return $cmd.Source }
                               $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
                               if(-not [string]::IsNullOrWhiteSpace($localAppData)){
                                 $candidate = Join-Path $localAppData 'Microsoft\WindowsApps\winget.exe'
                                 if(Test-Path $candidate){ return $candidate }
                               }
                               throw 'winget is required for this package manager installer. Install WinGet first, then retry.'
                             }

                             if($null -ne (Get-Command '{{validationCommand}}' -ErrorAction SilentlyContinue)){
                               Write-Output '{{title}} skipped: already installed.'
                               return
                             }

                             $winget = Resolve-PhantomWinget
                             & $winget install --id '{{wingetId}}' --exact --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity
                             $phantomExit = $LASTEXITCODE
                             if($null -ne $phantomExit -and $phantomExit -ne 0){
                               throw '{{title}} failed with exit code ' + $phantomExit
                             }

                             Write-Output '{{title}} completed. Restart Phantom if PATH changes are not visible immediately.'
                             """
                }
            ]
        };
    }

    private static OperationDefinition BuildInstallWingetOperation()
    {
        return new OperationDefinition
        {
            Id = "store.manager.install.winget",
            Title = "Install winget",
            Description = "Installs winget using Microsoft App Installer package.",
            RiskTier = RiskTier.Advanced,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "install-winget",
                    RequiresNetwork = true,
                    Script = "$wingetPresent=$false; try { Get-Command winget -ErrorAction Stop | Out-Null; $wingetPresent=$true } catch { $wingetPresent=$false }; if ($wingetPresent) { Write-Output 'winget already installed.'; return }; $bundlePath = Join-Path $env:TEMP ('AppInstaller-' + [Guid]::NewGuid().ToString('N') + '.msixbundle'); try { Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile $bundlePath -ErrorAction Stop; $sig = Get-AuthenticodeSignature $bundlePath; if ($sig.Status -ne 'Valid') { throw \"App Installer signature validation failed: $($sig.Status)\" }; Add-AppxPackage -Path $bundlePath -ErrorAction Stop } finally { Remove-Item -Path $bundlePath -Force -ErrorAction SilentlyContinue }"
                }
            ]
        };
    }

    private static OperationDefinition BuildUninstallWingetOperation()
    {
        return new OperationDefinition
        {
            Id = "store.manager.uninstall.winget",
            Title = "Uninstall winget",
            Description = "Removes Microsoft App Installer (winget).",
            RiskTier = RiskTier.Dangerous,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "uninstall-winget",
                    RequiresNetwork = false,
                    Script = "$packages = @(Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -AllUsers -ErrorAction SilentlyContinue); foreach ($pkg in $packages) { Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop }; $provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Microsoft.DesktopAppInstaller*' }); foreach ($pkg in $provisioned) { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -ErrorAction Stop | Out-Null }"
                }
            ]
        };
    }

    private static OperationDefinition BuildInstallChocoOperation()
    {
        return new OperationDefinition
        {
            Id = "store.manager.install.choco",
            Title = "Install Chocolatey",
            Description = "Installs Chocolatey package manager.",
            RiskTier = RiskTier.Advanced,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "install-choco",
                    RequiresNetwork = true,
                    Script = """
                             $chocoCandidates=@(
                               (Join-Path $env:ProgramData 'chocolatey\bin\choco.exe'),
                               (Join-Path $env:ProgramData 'chocolatey\choco.exe')
                             )
                             if($env:ChocolateyInstall){
                               $chocoCandidates += (Join-Path $env:ChocolateyInstall 'bin\choco.exe')
                               $chocoCandidates += (Join-Path $env:ChocolateyInstall 'choco.exe')
                             }
                             $chocoCandidates = $chocoCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
                             $chocoPresent = $null -ne (Get-Command choco -ErrorAction SilentlyContinue)
                             if(-not $chocoPresent){
                               foreach($candidate in $chocoCandidates){
                                 if(Test-Path $candidate){
                                   $chocoPresent=$true
                                   break
                                 }
                               }
                             }
                             if($chocoPresent){
                               Write-Output 'Chocolatey already installed.'
                               return
                             }
                             $wingetPresent = $null -ne (Get-Command winget -ErrorAction SilentlyContinue)
                             if(-not $wingetPresent){
                               throw 'winget is required to install Chocolatey in safe mode.'
                             }
                             $wingetOut = (winget install --id Chocolatey.Chocolatey --exact --source winget --accept-source-agreements --accept-package-agreements --silent --disable-interactivity 2>&1 | Out-String)
                             $wingetExit = $LASTEXITCODE
                             $alreadyInstalled = $wingetOut -match '(?im)already installed|No available upgrade found|No newer package versions are available'
                             if($wingetExit -ne 0 -and -not $alreadyInstalled){
                               throw ('Chocolatey installation failed. ' + $wingetOut.Trim())
                             }
                             $chocoPresent = $null -ne (Get-Command choco -ErrorAction SilentlyContinue)
                             if(-not $chocoPresent){
                               foreach($candidate in $chocoCandidates){
                                 if(Test-Path $candidate){
                                   $chocoPresent=$true
                                   break
                                 }
                               }
                             }
                             if($chocoPresent){
                               Write-Output 'Chocolatey installed.'
                               return
                             }
                             if($alreadyInstalled){
                               throw 'Chocolatey is marked installed by winget, but choco.exe was not found on disk. Repair or reinstall Chocolatey and retry.'
                             }
                             throw ('Chocolatey installation did not produce a detectable choco binary. ' + $wingetOut.Trim())
                             """
                }
            ]
        };
    }

    private static OperationDefinition BuildUninstallChocoOperation()
    {
        return new OperationDefinition
        {
            Id = "store.manager.uninstall.choco",
            Title = "Uninstall Chocolatey",
            Description = "Removes Chocolatey package manager binaries and PATH entry.",
            RiskTier = RiskTier.Dangerous,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "uninstall-choco",
                    RequiresNetwork = false,
                    Script = "$chocoExe = Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe'; $hasChocoCmd = $null -ne (Get-Command choco -ErrorAction SilentlyContinue); if (-not $hasChocoCmd -and (Test-Path $chocoExe)) { $chocoDir = [System.IO.Path]::GetDirectoryName($chocoExe); if ($chocoDir) { $env:PATH = $chocoDir + ';' + $env:PATH }; $hasChocoCmd = $null -ne (Get-Command choco -ErrorAction SilentlyContinue) }; if ($hasChocoCmd) { choco uninstall chocolatey -y --remove-dependencies | Out-Null }; $root = Join-Path $env:ProgramData 'chocolatey'; if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction Stop }; $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine'); if ($machinePath -and $machinePath -match '(?i)chocolatey\\\\bin') { $updatedPath = (($machinePath -split ';') | Where-Object { $_ -and ($_ -notmatch '(?i)chocolatey\\\\bin') }) -join ';'; [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'Machine') }; $env:PATH = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')"
                }
            ]
        };
    }

    private static string BuildManagerSummary(StoreManagerAvailability availability)
    {
        var parts = availability.All()
            .Select(item => $"{item.Manager.ToString().ToLowerInvariant()}={(item.Resolution.IsAvailable ? "present" : "missing")}");
        return "Package managers: " + string.Join(", ", parts);
    }

    private static string BuildSourceSummary(CatalogApp app)
    {
        if (app.ManualOnly)
        {
            return "manual";
        }

        var sources = new List<string>();
        AddSource(sources, "winget", app.WingetId);
        AddSource(sources, "scoop", app.ScoopId);
        AddSource(sources, "choco", app.ChocoId);
        AddSource(sources, "pip", app.PipId);
        AddSource(sources, "npm", app.NpmId);
        AddSource(sources, "dotnet", app.DotNetToolId);
        AddSource(sources, "psgallery", app.PowerShellGalleryId);
        return sources.Count == 0 ? "missing source" : string.Join(", ", sources);
    }

    private static string BuildPurposeSummary(CatalogApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Description))
        {
            return app.Description.Trim();
        }

        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? "This app" : app.DisplayName.Trim();
        var category = app.Category.Trim();
        var tagText = app.Tags.Length == 0
            ? string.Empty
            : string.Join(", ", app.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase));

        return category switch
        {
            "Browsers" => $"{name} is a web browser for browsing sites, signing in to web apps, and managing web workflows.",
            "Communications" => $"{name} is used for messaging, voice/video calls, collaboration, or community chat.",
            "Development" => $"{name} supports software development, terminals, code editing, version control, runtime tooling, or developer workflows.",
            "Document" => $"{name} is used to create, edit, read, convert, or manage documents and office files.",
            "Games" => $"{name} is a game, game launcher, or gaming-related utility.",
            "Microsoft Tools" => $"{name} is a Microsoft utility or runtime used to manage Windows, developer tooling, or Microsoft services.",
            "Multimedia Tools" => $"{name} is used for media playback, recording, streaming, image work, video, or audio workflows.",
            "Pro Tools" => $"{name} is a professional or creator utility for advanced productivity, design, media, or system work.",
            "Utilities" => $"{name} is a general-purpose utility for system maintenance, file management, security, or everyday desktop tasks.",
            _ when !string.IsNullOrWhiteSpace(tagText) => $"{name} is listed for {tagText} workflows.",
            _ => $"{name} installs from the configured package source for this catalog entry."
        };
    }

    private static void AddSource(ICollection<string> sources, string name, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            sources.Add($"{name}:{id}");
        }
    }

    private bool FilterCatalog(object obj)
    {
        if (obj is not CatalogApp app)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return app.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.Category.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.SourceSummary.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.PurposeSummary.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               app.Tags.Any(tag => tag.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _executionCoordinator.RunningChanged -= OnExecutionCoordinatorRunningChanged;
        GC.SuppressFinalize(this);
    }
}

public sealed class StorePackageManagerOption : ObservableObject
{
    private bool _selected;
    private bool _isInstalled;
    private bool _canInstall;
    private string _displayName = string.Empty;
    private string _description = string.Empty;
    private string _status = string.Empty;
    private string _source = string.Empty;

    public StorePackageManagerOption(StorePackageManager manager)
    {
        Manager = manager;
    }

    public StorePackageManager Manager { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetProperty(ref _isInstalled, value);
    }

    public bool CanInstall
    {
        get => _canInstall;
        set => SetProperty(ref _canInstall, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }
}
