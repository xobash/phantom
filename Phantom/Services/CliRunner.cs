using Phantom.Models;

namespace Phantom.Services;

public sealed class CliRunner
{
    private readonly AppPaths _paths;
    private readonly DefinitionCatalogService _definitions;
    private readonly OperationEngine _engine;
    private readonly ConsoleStreamService _console;
    private readonly LogService _log;
    private readonly NetworkGuardService _network;
    private readonly PowerShellQueryService _query;
    private readonly SettingsStore _settingsStore;

    public CliRunner(
        AppPaths paths,
        DefinitionCatalogService definitions,
        OperationEngine engine,
        ConsoleStreamService console,
        LogService log,
        NetworkGuardService network,
        PowerShellQueryService query,
        SettingsStore settingsStore)
    {
        _paths = paths;
        _definitions = definitions;
        _engine = engine;
        _console = console;
        _log = log;
        _network = network;
        _query = query;
        _settingsStore = settingsStore;
    }

    public async Task<int> RunAsync(string configPath, bool forceDangerous, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            await _log.WriteAsync("Error", $"Config not found: {configPath}", cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var config = await _definitions.LoadSelectionConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
        var operations = await BuildOperationsAsync(config, cancellationToken).ConfigureAwait(false);

        if (operations.Count == 0)
        {
            await _log.WriteAsync("Info", "No operations selected in config.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var hasDangerous = operations.Any(o => o.RiskTier == RiskTier.Dangerous || !o.Reversible);
        if (hasDangerous && !(config.ConfirmDangerous && forceDangerous))
        {
            await _log.WriteAsync("Error", "Dangerous operations requested but not confirmed. Set confirmDangerous=true and pass -ForceDangerous.", cancellationToken).ConfigureAwait(false);
            return 3;
        }

        if (operations.SelectMany(o => o.RunScripts).Any(s => s.RequiresNetwork) && !_network.IsOnline())
        {
            await _log.WriteAsync("Error", "Offline detected. Network-required actions blocked.", cancellationToken).ConfigureAwait(false);
            return 4;
        }

        var precheck = await _engine.RunBatchPrecheckAsync(operations, cancellationToken).ConfigureAwait(false);
        if (!precheck.IsSuccess)
        {
            await _log.WriteAsync("Error", precheck.Message, cancellationToken).ConfigureAwait(false);
            return 5;
        }

        var result = await _engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations = operations,
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = settings.EnableDestructiveOperations,
            ForceDangerous = config.ConfirmDangerous && forceDangerous,
            InteractiveDangerousPrompt = false,
            ConfirmDangerousAsync = _ => Task.FromResult(false)
        }, cancellationToken).ConfigureAwait(false);

        foreach (var item in result.Results)
        {
            await _log.WriteAsync(item.Success ? "Info" : "Error", $"{item.OperationId}: {item.Message}", cancellationToken).ConfigureAwait(false);
            _console.Publish(item.Success ? "Info" : "Error", $"{item.OperationId}: {item.Message}");
        }

        return result.Success ? 0 : 1;
    }

    private async Task<List<OperationDefinition>> BuildOperationsAsync(AutomationConfig config, CancellationToken cancellationToken)
    {
        var operations = new List<OperationDefinition>();

        var catalog = await _definitions.LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
        var selectedApps = catalog.Where(a => config.StoreSelections.Contains(a.DisplayName, StringComparer.OrdinalIgnoreCase)).ToList();
        operations.AddRange(selectedApps.Select(BuildStoreInstallOperation));

        var tweaks = await _definitions.LoadTweaksAsync(cancellationToken).ConfigureAwait(false);
        operations.AddRange(tweaks
            .Where(t => config.Tweaks.Contains(t.Id, StringComparer.OrdinalIgnoreCase))
            .Select(t => new OperationDefinition
            {
                Id = $"tweak.{t.Id}",
                Title = t.Name,
                Description = t.Description,
                RiskTier = t.RiskTier,
                Reversible = t.Reversible,
                RunScripts = [new PowerShellStep { Name = "apply", Script = t.ApplyScript }],
                UndoScripts = [new PowerShellStep { Name = "undo", Script = t.UndoScript }],
                StateCaptureScripts = t.StateCaptureKeys.Select(k => new PowerShellStep
                {
                    Name = k,
                    Script = $"$p='{k.Replace("'", "''")}'; if(Test-Path $p){{ Get-ItemProperty -Path $p | ConvertTo-Json -Depth 6 -Compress }}"
                }).ToArray()
            }));

        var features = await _definitions.LoadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        operations.AddRange(features
            .Where(f => config.Features.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
            .Select(f => new OperationDefinition
            {
                Id = $"feature.{f.Id}",
                Title = $"Enable {f.Name}",
                Description = f.Description,
                RiskTier = RiskTier.Advanced,
                Reversible = true,
                RequiresReboot = true,
                RunScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -All -NoRestart -ErrorAction Stop" }],
                UndoScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -NoRestart -ErrorAction Stop" }]
            }));

        var fixes = await _definitions.LoadFixesAsync(cancellationToken).ConfigureAwait(false);
        operations.AddRange(fixes
            .Where(f => config.Fixes.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
            .Select(f => new OperationDefinition
            {
                Id = $"fix.{f.Id}",
                Title = f.Name,
                Description = f.Description,
                RiskTier = f.RiskTier,
                Reversible = f.Reversible,
                RunScripts = [new PowerShellStep { Name = "apply", Script = f.ApplyScript }],
                UndoScripts = [new PowerShellStep { Name = "undo", Script = f.UndoScript }]
            }));

        operations.Add(config.UpdateMode switch
        {
            "Disable All" => new OperationDefinition
            {
                Id = "updates.mode.disableall",
                Title = "Disable updates",
                Description = "Disable all updates",
                RiskTier = RiskTier.Dangerous,
                Reversible = true,
                RunScripts = [new PowerShellStep { Name = "disable", Script = "New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU' -Name NoAutoUpdate -Value 1 -Type DWord; Stop-Service wuauserv -Force -ErrorAction SilentlyContinue; Set-Service wuauserv -StartupType Disabled" }],
                UndoScripts = [new PowerShellStep { Name = "default", Script = "Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Recurse -Force -ErrorAction SilentlyContinue; Set-Service wuauserv -StartupType Manual; Start-Service wuauserv -ErrorAction SilentlyContinue" }]
            },
            "Security" => new OperationDefinition
            {
                Id = "updates.mode.security",
                Title = "Security update mode",
                Description = "Security mode",
                RiskTier = RiskTier.Basic,
                Reversible = true,
                RunScripts = [new PowerShellStep { Name = "security", Script = "New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferFeatureUpdatesPeriodInDays -Value 365 -Type DWord; Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Name DeferQualityUpdatesPeriodInDays -Value 4 -Type DWord" }],
                UndoScripts = [new PowerShellStep { Name = "default", Script = "Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Recurse -Force -ErrorAction SilentlyContinue" }]
            },
            _ => new OperationDefinition
            {
                Id = "updates.mode.default",
                Title = "Default updates",
                Description = "Restore default update behavior",
                RiskTier = RiskTier.Basic,
                Reversible = true,
                RunScripts = [new PowerShellStep { Name = "default", Script = "Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Recurse -Force -ErrorAction SilentlyContinue; Set-Service wuauserv -StartupType Manual; Start-Service wuauserv -ErrorAction SilentlyContinue" }],
                UndoScripts = [new PowerShellStep { Name = "none", Script = "Write-Output 'No-op'" }]
            }
        });

        return operations;
    }

    private static OperationDefinition BuildStoreInstallOperation(CatalogApp app)
    {
        string winget = string.IsNullOrWhiteSpace(app.WingetId) ? string.Empty : $"winget install --id {app.WingetId} -e --accept-source-agreements --accept-package-agreements --silent";
        string choco = string.IsNullOrWhiteSpace(app.ChocoId) ? string.Empty : $"choco install {app.ChocoId} -y";

        var script = !string.IsNullOrWhiteSpace(winget) && !string.IsNullOrWhiteSpace(choco)
            ? $"if(Get-Command winget -ErrorAction SilentlyContinue){{ {winget} }} elseif(Get-Command choco -ErrorAction SilentlyContinue){{ {choco} }} else {{ throw 'No package manager available' }}"
            : !string.IsNullOrWhiteSpace(winget)
                ? $"if(Get-Command winget -ErrorAction SilentlyContinue){{ {winget} }} else {{ throw 'winget missing' }}"
                : $"if(Get-Command choco -ErrorAction SilentlyContinue){{ {choco} }} else {{ throw 'choco missing' }}";

        return new OperationDefinition
        {
            Id = $"store.app.{new string(app.DisplayName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()}",
            Title = $"Install {app.DisplayName}",
            Description = "Install app from catalog",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts = [new PowerShellStep { Name = "install", Script = script, RequiresNetwork = true }],
            UndoScripts = [new PowerShellStep { Name = "uninstall", Script = string.IsNullOrWhiteSpace(app.WingetId) ? $"choco uninstall {app.ChocoId} -y" : $"winget uninstall --id {app.WingetId} -e --silent" }]
        };
    }
}
