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
    private const string WingetPresenceProbeScript = "$ok=$false; try { Get-Command winget -ErrorAction Stop | Out-Null; $ok=$true } catch { $ok=$false }; if ($ok) { '1' }";
    private const string ChocoPresenceProbeScript = "$ok=$false; try { Get-Command choco -ErrorAction Stop | Out-Null; $ok=$true } catch { $ok=$false }; if (-not $ok) { $chocoExe = Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe'; if (Test-Path $chocoExe) { $ok=$true } }; if ($ok) { '1' }";
    private const string ManagerProbeScript = "$hasWinget=$false; try { Get-Command winget -ErrorAction Stop | Out-Null; $hasWinget=$true } catch { $hasWinget=$false }; $hasChoco=$false; try { Get-Command choco -ErrorAction Stop | Out-Null; $hasChoco=$true } catch { $hasChoco=$false }; if (-not $hasChoco) { $hasChoco = Test-Path (Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe') }; ";

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
        using (CatalogView.DeferRefresh())
        {
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.Category), ListSortDirection.Ascending));
            CatalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogApp.DisplayName), ListSortDirection.Ascending));
            CatalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogApp.Category)));
        }

        RefreshManagersCommand = new AsyncRelayCommand(ct => RefreshManagersAsync(ct, echoToConsole: true));
        InstallMissingManagersCommand = new AsyncRelayCommand(InstallMissingManagersAsync);
        InstallWingetCommand = new AsyncRelayCommand(InstallWingetAsync);
        UninstallWingetCommand = new AsyncRelayCommand(UninstallWingetAsync);
        InstallChocoCommand = new AsyncRelayCommand(InstallChocoAsync);
        UninstallChocoCommand = new AsyncRelayCommand(UninstallChocoAsync);
        ToggleWingetCommand = new AsyncRelayCommand(ToggleWingetAsync);
        ToggleChocoCommand = new AsyncRelayCommand(ToggleChocoAsync);
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
    public AsyncRelayCommand ToggleWingetCommand { get; }
    public AsyncRelayCommand ToggleChocoCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand UninstallSelectedCommand { get; }
    public AsyncRelayCommand UpgradeSelectedCommand { get; }
    public AsyncRelayCommand ImportCatalogCommand { get; }
    public AsyncRelayCommand ExportCatalogCommand { get; }

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

        await RefreshManagersAsync(cancellationToken, echoToConsole: false).ConfigureAwait(false);
    }

    private async Task RefreshManagersAsync(CancellationToken cancellationToken, bool echoToConsole = true)
    {
        var winget = await _queryService.InvokeAsync(WingetPresenceProbeScript, cancellationToken, echoToConsole: echoToConsole).ConfigureAwait(false);
        var choco = await _queryService.InvokeAsync(ChocoPresenceProbeScript, cancellationToken, echoToConsole: echoToConsole).ConfigureAwait(false);

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
        var operations = BuildOperationsForSelected(selected, BuildInstallOperation);
        await ExecuteStoreOperationsAsync(operations, dryRun: false, cancellationToken).ConfigureAwait(false);
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

        List<CatalogApp> apps;
        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
            apps = JsonSerializer.Deserialize<List<CatalogApp>>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CatalogApp>();
            ValidateCatalogEntries(apps);
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Catalog import failed: {ex.Message}");
            return;
        }

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

    private IReadOnlyList<OperationDefinition> BuildOperationsForSelected(
        IReadOnlyList<CatalogApp> selected,
        Func<CatalogApp, OperationDefinition> operationBuilder)
    {
        var operations = new List<OperationDefinition>();
        foreach (var app in selected)
        {
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
                    Script = "$wingetPresent=$false; try { Get-Command winget -ErrorAction Stop | Out-Null; $wingetPresent=$true } catch { $wingetPresent=$false }; if ($wingetPresent) { Write-Output 'winget already installed.'; return }; $bundlePath = Join-Path $env:TEMP 'AppInstaller.msixbundle'; Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile $bundlePath -ErrorAction Stop; $sig = Get-AuthenticodeSignature $bundlePath; if ($sig.Status -ne 'Valid') { throw \"App Installer signature validation failed: $($sig.Status)\" }; Add-AppxPackage -Path $bundlePath -ErrorAction Stop"
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
                    Script = "$packages = Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -AllUsers -ErrorAction Stop; if ($packages) { $packages | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop } }; $provisioned = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like 'Microsoft.DesktopAppInstaller*' }; foreach ($pkg in $provisioned) { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -ErrorAction Stop | Out-Null }"
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
                    Script = "$chocoCandidates=@((Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe'),(Join-Path $env:ProgramData 'chocolatey\\choco.exe')); if($env:ChocolateyInstall){ $chocoCandidates += (Join-Path $env:ChocolateyInstall 'bin\\choco.exe'); $chocoCandidates += (Join-Path $env:ChocolateyInstall 'choco.exe') }; $chocoCandidates=$chocoCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique; $chocoPresent=$false; try { Get-Command choco -ErrorAction Stop | Out-Null; $chocoPresent=$true } catch { $chocoPresent=$false }; if (-not $chocoPresent) { foreach($candidate in $chocoCandidates){ if (Test-Path $candidate) { $chocoPresent=$true; break } } }; if ($chocoPresent) { Write-Output 'Chocolatey already installed.'; return }; $wingetPresent=$false; try { Get-Command winget -ErrorAction Stop | Out-Null; $wingetPresent=$true } catch { $wingetPresent=$false }; if (-not $wingetPresent) { throw 'winget is required to install Chocolatey in safe mode.' }; $wingetOut = (winget install --id Chocolatey.Chocolatey -e --accept-source-agreements --accept-package-agreements --silent 2>&1 | Out-String); $alreadyInstalled = ($wingetOut -match '(?im)already installed|No available upgrade found|No newer package versions are available'); $chocoPresent=$false; try { Get-Command choco -ErrorAction Stop | Out-Null; $chocoPresent=$true } catch { $chocoPresent=$false }; if (-not $chocoPresent) { foreach($candidate in $chocoCandidates){ if (Test-Path $candidate) { $chocoPresent=$true; break } } }; if ($chocoPresent -or $alreadyInstalled) { Write-Output 'Chocolatey installed.'; return }; throw ('Chocolatey installation did not produce a detectable choco binary. ' + $wingetOut.Trim())"
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
                    Script = "$chocoExe = Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe'; $hasChocoCmd=$false; try { Get-Command choco -ErrorAction Stop | Out-Null; $hasChocoCmd=$true } catch { $hasChocoCmd=$false }; $hasChocoExe = Test-Path $chocoExe; if ($hasChocoCmd) { choco uninstall chocolatey -y --remove-dependencies | Out-Null } elseif ($hasChocoExe) { & $chocoExe uninstall chocolatey -y --remove-dependencies | Out-Null }; $root = Join-Path $env:ProgramData 'chocolatey'; if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction Stop }; $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine'); if ($machinePath -and $machinePath -match '(?i)chocolatey\\\\bin') { $updatedPath = (($machinePath -split ';') | Where-Object { $_ -and ($_ -notmatch '(?i)chocolatey\\\\bin') }) -join ';'; [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'Machine') }; $env:PATH = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')"
                }
            ]
        };
    }

    private OperationDefinition BuildInstallOperation(CatalogApp app)
    {
        var context = $"store app '{app.DisplayName}'";
        var packageQuery = NormalizePackageQuery(app.DisplayName, $"{context} displayName");
        var wingetId = NormalizePackageId(app.WingetId, $"{context} wingetId");
        var chocoId = NormalizePackageId(app.ChocoId, $"{context} chocoId");

        var silentArgs = NormalizeSilentArgs(app.SilentArgs, $"{context} silentArgs");
        var silentArgsSegment = silentArgs.Length == 0 ? string.Empty : $" {silentArgs}";

        var wingetScript = wingetId.Length == 0
            ? $"winget install --name {PowerShellInputSanitizer.ToSingleQuotedLiteral(packageQuery)} --exact --accept-package-agreements --accept-source-agreements --silent{silentArgsSegment}"
            : $"winget install --id {PowerShellInputSanitizer.ToSingleQuotedLiteral(wingetId)} -e --accept-package-agreements --accept-source-agreements --silent{silentArgsSegment}";

        var chocoScript = chocoId.Length > 0
            ? $"choco install {PowerShellInputSanitizer.ToSingleQuotedLiteral(chocoId)} -y{silentArgsSegment}"
            : string.Empty;

        var wingetUninstallScript = wingetId.Length == 0
            ? $"winget uninstall --name {PowerShellInputSanitizer.ToSingleQuotedLiteral(packageQuery)} --exact --silent"
            : $"winget uninstall --id {PowerShellInputSanitizer.ToSingleQuotedLiteral(wingetId)} -e --silent";

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
                        wingetUninstallScript,
                        chocoId.Length > 0 ? $"choco uninstall {PowerShellInputSanitizer.ToSingleQuotedLiteral(chocoId)} -y" : string.Empty)
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
        var context = $"store app '{app.DisplayName}'";
        var packageQuery = NormalizePackageQuery(app.DisplayName, $"{context} displayName");
        var wingetId = NormalizePackageId(app.WingetId, $"{context} wingetId");
        var chocoId = NormalizePackageId(app.ChocoId, $"{context} chocoId");

        var wingetScript = wingetId.Length == 0
            ? $"winget upgrade --name {PowerShellInputSanitizer.ToSingleQuotedLiteral(packageQuery)} --exact --accept-package-agreements --accept-source-agreements"
            : $"winget upgrade --id {PowerShellInputSanitizer.ToSingleQuotedLiteral(wingetId)} -e --accept-package-agreements --accept-source-agreements";

        var chocoScript = chocoId.Length > 0
            ? $"choco upgrade {PowerShellInputSanitizer.ToSingleQuotedLiteral(chocoId)} -y"
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
            return $"{ManagerProbeScript}if ($hasWinget) {{ {wingetScript} }} elseif ($hasChoco) {{ {chocoScript} }} else {{ throw 'Neither winget nor choco is installed.' }}";
        }

        if (hasWinget)
        {
            return $"{ManagerProbeScript}if ($hasWinget) {{ {wingetScript} }} else {{ throw 'winget is not installed.' }}";
        }

        if (hasChoco)
        {
            return $"{ManagerProbeScript}if ($hasChoco) {{ {chocoScript} }} else {{ throw 'Chocolatey is not installed.' }}";
        }

        return "throw 'No installer metadata defined for this app.'";
    }

    private static string NormalizePackageId(string? raw, string context)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : PowerShellInputSanitizer.EnsurePackageId(raw, context);
    }

    private static string NormalizePackageQuery(string? raw, string context)
    {
        return PowerShellInputSanitizer.EnsurePackageQuery(raw, context);
    }

    private static string NormalizeSilentArgs(string? raw, string context)
    {
        return PowerShellInputSanitizer.EnsureSafeCliArguments(raw, context);
    }

    private static void ValidateCatalogEntries(IEnumerable<CatalogApp> apps)
    {
        var index = 0;
        foreach (var app in apps)
        {
            index++;
            var context = $"catalog entry #{index}";
            if (string.IsNullOrWhiteSpace(app.DisplayName))
            {
                throw new ArgumentException($"{context}: displayName is required.");
            }

            _ = NormalizePackageQuery(app.DisplayName, $"{context} displayName");
            _ = NormalizePackageId(app.WingetId, $"{context} wingetId");
            _ = NormalizePackageId(app.ChocoId, $"{context} chocoId");

            _ = NormalizeSilentArgs(app.SilentArgs, $"{context} silentArgs");
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
               app.Tags.Any(tag => tag.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }
}
