using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
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
    private readonly PowerShellQueryService _queryService;
    private readonly Func<AppSettings> _settingsAccessor;

    private bool _wingetInstalled;
    private bool _chocoInstalled;
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
        PowerShellQueryService queryService,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _networkGuard = networkGuard;
        _storeInstallService = storeInstallService;
        _queryService = queryService;
        _settingsAccessor = settingsAccessor;

        Catalog = new ObservableCollection<CatalogApp>();
        CatalogView = CollectionViewSource.GetDefaultView(Catalog);
        CatalogView.Filter = FilterCatalog;
        using (CatalogView.DeferRefresh())
        {
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.Category), ListSortDirection.Ascending));
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.DisplayName), ListSortDirection.Ascending));
            CatalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogApp.Category)));
        }

        RefreshManagersCommand = new AsyncRelayCommand(ct => RefreshManagersAsync(ct, echoToConsole: true), CanRunStoreOperation);
        InstallMissingManagersCommand = new AsyncRelayCommand(InstallMissingManagersAsync, CanRunStoreOperation);
        InstallWingetCommand = new AsyncRelayCommand(InstallWingetAsync, CanRunStoreOperation);
        UninstallWingetCommand = new AsyncRelayCommand(UninstallWingetAsync, CanRunStoreOperation);
        InstallChocoCommand = new AsyncRelayCommand(InstallChocoAsync, CanRunStoreOperation);
        UninstallChocoCommand = new AsyncRelayCommand(UninstallChocoAsync, CanRunStoreOperation);
        ToggleWingetCommand = new AsyncRelayCommand(ToggleWingetAsync, CanRunStoreOperation);
        ToggleChocoCommand = new AsyncRelayCommand(ToggleChocoAsync, CanRunStoreOperation);
        RefreshPackageStatusCommand = new AsyncRelayCommand(RefreshPackageStatusAsync, CanRunStoreOperation);
        DiscoverSelectedCommand = new AsyncRelayCommand(DiscoverSelectedAsync, CanRunStoreOperation);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, CanRunStoreOperation);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, CanRunStoreOperation);
        UpgradeSelectedCommand = new AsyncRelayCommand(UpgradeSelectedAsync, CanRunStoreOperation);

        _executionCoordinator.RunningChanged += OnExecutionCoordinatorRunningChanged;
    }

    public string Title => "Store";

    public ObservableCollection<CatalogApp> Catalog { get; }
    public ICollectionView CatalogView { get; }

    public AsyncRelayCommand RefreshManagersCommand { get; }
    public AsyncRelayCommand InstallMissingManagersCommand { get; }
    public AsyncRelayCommand InstallWingetCommand { get; }
    public AsyncRelayCommand UninstallWingetCommand { get; }
    public AsyncRelayCommand InstallChocoCommand { get; }
    public AsyncRelayCommand UninstallChocoCommand { get; }
    public AsyncRelayCommand ToggleWingetCommand { get; }
    public AsyncRelayCommand ToggleChocoCommand { get; }
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

            Notify(nameof(WingetToggleLabel));
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

            Notify(nameof(ChocoToggleLabel));
        }
    }

    public string WingetToggleLabel => WingetInstalled ? "Uninstall winget" : "Install winget";
    public string ChocoToggleLabel => ChocoInstalled ? "Uninstall choco" : "Install choco";

    public string ManagerSummary
    {
        get => _managerSummary;
        set => SetProperty(ref _managerSummary, value);
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
        if (echoToConsole)
        {
            _console.Publish("Trace", $"winget resolver: {(WingetInstalled ? availability.Winget.ExecutablePath : availability.Winget.Message)}");
            _console.Publish("Trace", $"choco resolver: {(ChocoInstalled ? availability.Chocolatey.ExecutablePath : availability.Chocolatey.Message)}");
        }

        _console.Publish("Info", ManagerSummary);
    }

    private async Task InstallMissingManagersAsync(CancellationToken cancellationToken)
    {
        var operations = new List<OperationDefinition>();

        if (!WingetInstalled)
        {
            operations.Add(BuildInstallWingetOperation());
        }

        if (!ChocoInstalled)
        {
            operations.Add(BuildInstallChocoOperation());
        }

        if (operations.Count == 0)
        {
            _console.Publish("Info", "Nothing to install. winget and Chocolatey are already present.");
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
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = BuildOperationsForSelected(selected, app => OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Install));
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = await Application.Current.Dispatcher.InvokeAsync(() => Catalog.Where(x => x.Selected).ToList());
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No items selected.");
            return;
        }

        foreach (var app in selected)
        {
            try
            {
                var script = OperationDefinitionFactory.BuildPackageDiscoveryScript(app);
                var result = await _queryService.InvokeAsync(script, cancellationToken, echoToConsole: false).ConfigureAwait(false);
                var output = (result.Stdout + Environment.NewLine + result.Stderr).Trim();
                _console.Publish(result.ExitCode == 0 ? "Info" : "Error", $"{app.DisplayName} discovery: {output}");
            }
            catch (Exception ex)
            {
                _console.Publish("Error", $"{app.DisplayName} discovery failed: {ex.Message}");
            }
        }
    }

    private async Task UninstallSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = BuildOperationsForSelected(selected, BuildUninstallOperation);
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpgradeSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = BuildOperationsForSelected(selected, BuildUpgradeOperation);
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshPackageStatusAsync(CancellationToken cancellationToken)
    {
        var apps = await Application.Current.Dispatcher.InvokeAsync(() => Catalog.ToList());
        var updates = new List<(CatalogApp App, PackageStatusResult Status)>(apps.Count);

        foreach (var app in apps)
        {
            try
            {
                var script = OperationDefinitionFactory.BuildPackageStatusScript(app);
                var result = await _queryService.InvokeAsync(script, cancellationToken, echoToConsole: false).ConfigureAwait(false);
                var output = (result.Stdout + Environment.NewLine + result.Stderr).Trim();
                var status = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                    ? JsonSerializer.Deserialize<PackageStatusResult>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? PackageStatusResult.Unknown("Empty package status JSON.")
                    : PackageStatusResult.Unknown(string.IsNullOrWhiteSpace(output) ? "No package status output." : output);
                updates.Add((app, status));
            }
            catch (Exception ex)
            {
                updates.Add((app, PackageStatusResult.Unknown(ex.Message)));
            }
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var update in updates)
            {
                update.App.Status = update.Status.Status;
                update.App.InstalledVersion = update.Status.InstalledVersion;
                update.App.AvailableVersion = update.Status.AvailableVersion;
                update.App.SourceSummary = string.IsNullOrWhiteSpace(update.Status.Manager)
                    ? BuildSourceSummary(update.App)
                    : $"{update.Status.Manager}:{update.Status.PackageId}";
            }

            CatalogView.Refresh();
        });

        _console.Publish("Info", $"Package state refreshed: {updates.Count} catalog entries.");
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

    private IReadOnlyList<OperationDefinition> BuildOperationsForSelected(
        IReadOnlyList<CatalogApp> selected,
        Func<CatalogApp, OperationDefinition> operationBuilder)
    {
        var operations = new List<OperationDefinition>();
        foreach (var app in selected)
        {
            if (!HasAutomaticPackageSource(app))
            {
                _console.Publish("Warning", $"{app.DisplayName}: manual-only entry has no automatic package source.");
                continue;
            }

            try
            {
                operations.Add(operationBuilder(app));
            }
            catch (ArgumentException ex)
            {
                _console.Publish("Error", ex.Message);
            }
        }

        return operations;
    }

    private static bool HasAutomaticPackageSource(CatalogApp app)
    {
        return !app.ManualOnly &&
               (!string.IsNullOrWhiteSpace(app.WingetId) ||
                !string.IsNullOrWhiteSpace(app.ScoopId) ||
                !string.IsNullOrWhiteSpace(app.ChocoId) ||
                !string.IsNullOrWhiteSpace(app.PipId) ||
                !string.IsNullOrWhiteSpace(app.NpmId) ||
                !string.IsNullOrWhiteSpace(app.DotNetToolId) ||
                !string.IsNullOrWhiteSpace(app.PowerShellGalleryId));
    }

    private static OperationDefinition BuildInstallWingetOperation()
    {
        return new OperationDefinition
        {
            Id = "store.manager.install.winget",
            Title = "Install winget",
            Description = "Installs winget using Microsoft App Installer package.",
            RiskTier = RiskTier.Dangerous,
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
            RiskTier = RiskTier.Dangerous,
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
                             $wingetOut = (winget install --id Chocolatey.Chocolatey -e --accept-source-agreements --accept-package-agreements --silent 2>&1 | Out-String)
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

    private OperationDefinition BuildInstallOperation(CatalogApp app)
        => OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Install);

    private OperationDefinition BuildUninstallOperation(CatalogApp app)
        => OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Uninstall);

    private OperationDefinition BuildUpgradeOperation(CatalogApp app)
        => OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Upgrade);

    private async Task UpdateAppStatusAsync(CatalogApp app, string status)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                app.Status = status;
                CatalogView.Refresh();
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                app.Status = status;
                CatalogView.Refresh();
            });
            return;
        }

        app.Status = status;
        CatalogView.Refresh();
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

    private sealed class PackageStatusResult
    {
        public string Status { get; set; } = "Unknown";
        public string Manager { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public bool Installed { get; set; }
        public string InstalledVersion { get; set; } = string.Empty;
        public string AvailableVersion { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public static PackageStatusResult Unknown(string message) => new()
        {
            Status = "Unknown",
            Message = message
        };
    }
}
