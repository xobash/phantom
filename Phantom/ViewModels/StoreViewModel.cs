using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class StoreViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly NetworkGuardService _networkGuard;
    private readonly PowerShellQueryService _queryService;
    private readonly Func<AppSettings> _settingsAccessor;

    private bool _wingetInstalled;
    private bool _chocoInstalled;
    private string _search = string.Empty;

    public StoreViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        NetworkGuardService networkGuard,
        PowerShellQueryService queryService,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _networkGuard = networkGuard;
        _queryService = queryService;
        _settingsAccessor = settingsAccessor;

        Catalog = new ObservableCollection<CatalogApp>();
        CatalogView = CollectionViewSource.GetDefaultView(Catalog);
        CatalogView.Filter = FilterCatalog;

        RefreshManagersCommand = new AsyncRelayCommand(RefreshManagersAsync);
        InstallMissingManagersCommand = new AsyncRelayCommand(InstallMissingManagersAsync);
        InstallWingetCommand = new AsyncRelayCommand(InstallWingetAsync);
        UninstallWingetCommand = new AsyncRelayCommand(UninstallWingetAsync);
        InstallChocoCommand = new AsyncRelayCommand(InstallChocoAsync);
        UninstallChocoCommand = new AsyncRelayCommand(UninstallChocoAsync);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync);
        UpgradeSelectedCommand = new AsyncRelayCommand(UpgradeSelectedAsync);
        ImportCatalogCommand = new AsyncRelayCommand(ImportCatalogAsync);
        ExportCatalogCommand = new AsyncRelayCommand(ExportCatalogAsync);
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
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand UninstallSelectedCommand { get; }
    public AsyncRelayCommand UpgradeSelectedCommand { get; }
    public AsyncRelayCommand ImportCatalogCommand { get; }
    public AsyncRelayCommand ExportCatalogCommand { get; }

    public bool WingetInstalled
    {
        get => _wingetInstalled;
        set => SetProperty(ref _wingetInstalled, value);
    }

    public bool ChocoInstalled
    {
        get => _chocoInstalled;
        set => SetProperty(ref _chocoInstalled, value);
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
                Catalog.Add(app);
            }
        });

        await RefreshManagersAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshManagersAsync(CancellationToken cancellationToken)
    {
        var winget = await _queryService.InvokeAsync("if (Get-Command winget -ErrorAction SilentlyContinue) { '1' }", cancellationToken).ConfigureAwait(false);
        var choco = await _queryService.InvokeAsync("if (Get-Command choco -ErrorAction SilentlyContinue) { '1' }", cancellationToken).ConfigureAwait(false);

        WingetInstalled = winget.Stdout.Trim() == "1";
        ChocoInstalled = choco.Stdout.Trim() == "1";

        _console.Publish("Info", $"Package managers: winget={(WingetInstalled ? "present" : "missing")}, choco={(ChocoInstalled ? "present" : "missing")}");
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

        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
        await RefreshManagersAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task InstallSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = selected.Select(BuildInstallOperation).ToList();
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task UninstallSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = selected.Select(BuildUninstallOperation).ToList();
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpgradeSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Catalog.Where(x => x.Selected).ToList();
        var operations = selected.Select(BuildUpgradeOperation).ToList();
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportCatalogAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            Title = "Import app catalog"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var text = await File.ReadAllTextAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
        var apps = JsonSerializer.Deserialize<List<CatalogApp>>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CatalogApp>();

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Catalog.Clear();
            foreach (var app in apps)
            {
                Catalog.Add(app);
            }
        });

        _console.Publish("Info", $"Imported catalog with {Catalog.Count} entries.");
    }

    private async Task ExportCatalogAsync(CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "catalog.apps.json",
            Title = "Export app catalog"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _catalogService.SaveCatalogAsync(dialog.FileName, Catalog.ToList(), cancellationToken).ConfigureAwait(false);
        _console.Publish("Info", $"Exported catalog to {dialog.FileName}");
    }

    private async Task ExecuteStoreOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool dryRun, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
            _console.Publish("Info", "No items selected.");
            return;
        }

        if (!_networkGuard.IsOnline())
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
            var precheck = await _operationEngine.RunBatchPrecheckAsync(operations, linked.Token).ConfigureAwait(false);
            if (!precheck.IsSuccess)
            {
                _console.Publish("Error", precheck.Message);
                return;
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
                var item = Catalog.FirstOrDefault(x => $"store.app.{SanitizeId(x.DisplayName)}" == op.OperationId);
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
    }

    private static string SanitizeId(string source)
    {
        return new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private async Task ExecuteManagerOperationAsync(OperationDefinition operation, CancellationToken cancellationToken)
    {
        await ExecuteStoreOperationsAsync([operation], dryRun: false, cancellationToken).ConfigureAwait(false);
        await RefreshManagersAsync(cancellationToken).ConfigureAwait(false);
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
                    Script = "Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile \"$env:TEMP\\AppInstaller.msixbundle\"; Add-AppxPackage -Path \"$env:TEMP\\AppInstaller.msixbundle\""
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
                    Script = "$packages = Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -AllUsers -ErrorAction SilentlyContinue; if ($packages) { $packages | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue } }; $provisioned = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like 'Microsoft.DesktopAppInstaller*' }; foreach ($pkg in $provisioned) { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -ErrorAction SilentlyContinue | Out-Null }"
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
                    Script = "Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))"
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
                    Script = "if (Get-Command choco -ErrorAction SilentlyContinue) { choco uninstall chocolatey -y --remove-dependencies | Out-Null }; $root = Join-Path $env:ProgramData 'chocolatey'; if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }; $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine'); if ($machinePath -and $machinePath -match '(?i)chocolatey\\\\bin') { $updatedPath = (($machinePath -split ';') | Where-Object { $_ -and ($_ -notmatch '(?i)chocolatey\\\\bin') }) -join ';'; [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'Machine') }; $env:PATH = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')"
                }
            ]
        };
    }

    private OperationDefinition BuildInstallOperation(CatalogApp app)
    {
        var wingetScript = !string.IsNullOrWhiteSpace(app.WingetId)
            ? $"winget install --id {app.WingetId} -e --accept-package-agreements --accept-source-agreements --silent {app.SilentArgs}".Trim()
            : string.Empty;

        var chocoScript = !string.IsNullOrWhiteSpace(app.ChocoId)
            ? $"choco install {app.ChocoId} -y {app.SilentArgs}".Trim()
            : string.Empty;

        return new OperationDefinition
        {
            Id = $"store.app.{SanitizeId(app.DisplayName)}",
            Title = $"Install {app.DisplayName}",
            Description = "Install app from catalog.",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "install",
                    RequiresNetwork = true,
                    Script = BuildManagerFallbackScript(wingetScript, chocoScript)
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "uninstall",
                    RequiresNetwork = false,
                    Script = BuildManagerFallbackScript(
                        !string.IsNullOrWhiteSpace(app.WingetId) ? $"winget uninstall --id {app.WingetId} -e --silent" : string.Empty,
                        !string.IsNullOrWhiteSpace(app.ChocoId) ? $"choco uninstall {app.ChocoId} -y" : string.Empty)
                }
            ]
        };
    }

    private OperationDefinition BuildUninstallOperation(CatalogApp app)
    {
        var operation = BuildInstallOperation(app);
        operation.Title = $"Uninstall {app.DisplayName}";
        operation.RunScripts = operation.UndoScripts;
        operation.UndoScripts = Array.Empty<PowerShellStep>();
        operation.Reversible = false;
        operation.RiskTier = RiskTier.Advanced;
        return operation;
    }

    private OperationDefinition BuildUpgradeOperation(CatalogApp app)
    {
        var wingetScript = !string.IsNullOrWhiteSpace(app.WingetId)
            ? $"winget upgrade --id {app.WingetId} -e --accept-package-agreements --accept-source-agreements"
            : string.Empty;

        var chocoScript = !string.IsNullOrWhiteSpace(app.ChocoId)
            ? $"choco upgrade {app.ChocoId} -y"
            : string.Empty;

        return new OperationDefinition
        {
            Id = $"store.upgrade.{SanitizeId(app.DisplayName)}",
            Title = $"Upgrade {app.DisplayName}",
            Description = "Upgrade app from catalog.",
            RiskTier = RiskTier.Basic,
            Reversible = false,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "upgrade",
                    RequiresNetwork = true,
                    Script = BuildManagerFallbackScript(wingetScript, chocoScript)
                }
            ]
        };
    }

    private static string BuildManagerFallbackScript(string wingetScript, string chocoScript)
    {
        var hasWinget = !string.IsNullOrWhiteSpace(wingetScript);
        var hasChoco = !string.IsNullOrWhiteSpace(chocoScript);

        if (hasWinget && hasChoco)
        {
            return $"if (Get-Command winget -ErrorAction SilentlyContinue) {{ {wingetScript} }} elseif (Get-Command choco -ErrorAction SilentlyContinue) {{ {chocoScript} }} else {{ throw 'Neither winget nor choco is installed.' }}";
        }

        if (hasWinget)
        {
            return $"if (Get-Command winget -ErrorAction SilentlyContinue) {{ {wingetScript} }} else {{ throw 'winget is not installed.' }}";
        }

        if (hasChoco)
        {
            return $"if (Get-Command choco -ErrorAction SilentlyContinue) {{ {chocoScript} }} else {{ throw 'Chocolatey is not installed.' }}";
        }

        return "throw 'No installer metadata defined for this app.'";
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
               app.Tags.Any(tag => tag.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }
}
