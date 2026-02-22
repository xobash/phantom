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
        _console.Publish("Trace", $"CliRunner.RunAsync started. configPath={configPath}, forceDangerous={forceDangerous}");
        await _log.WriteAsync("Trace", $"CliRunner.RunAsync started. configPath={configPath}, forceDangerous={forceDangerous}", cancellationToken).ConfigureAwait(false);

        if (!TryNormalizeConfigPath(configPath, out var normalizedConfigPath, out var validationError))
        {
            _console.Publish("Error", validationError);
            await _log.WriteAsync("Error", validationError, cancellationToken).ConfigureAwait(false);
            return 2;
        }

        _console.Publish("Trace", $"CliRunner normalized config path: {normalizedConfigPath}");
        if (!File.Exists(normalizedConfigPath))
        {
            await _log.WriteAsync("Error", $"Config not found: {normalizedConfigPath}", cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var config = await _definitions.LoadSelectionConfigAsync(normalizedConfigPath, cancellationToken).ConfigureAwait(false);
        var operations = await BuildOperationsAsync(config, cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", $"CliRunner resolved operations: {operations.Count}");

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

        _console.Publish("Trace", $"CliRunner.RunAsync completed. success={result.Success}");
        return result.Success ? 0 : 1;
    }

    private bool TryNormalizeConfigPath(string configPath, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configPath))
        {
            error = "CLI config path is required.";
            return false;
        }

        if (configPath.IndexOf('\0') >= 0)
        {
            error = "CLI config path contains invalid null characters.";
            return false;
        }

        if (configPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = $"CLI config path contains invalid characters: {configPath}";
            return false;
        }

        var trimmed = configPath.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "UNC paths are blocked for CLI config. Use a local file path.";
            return false;
        }

        var rooted = Path.IsPathRooted(trimmed);
        var candidate = rooted
            ? trimmed
            : Path.Combine(_paths.RuntimeDirectory, trimmed);

        try
        {
            normalizedPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = $"CLI config path is invalid: {ex.Message}";
            return false;
        }

        if (!normalizedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            error = "CLI config path must point to a .json file.";
            return false;
        }

        if (!rooted)
        {
            var runtimeRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(_paths.RuntimeDirectory));
            if (!normalizedPath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "Path traversal detected in CLI config path. Relative paths must stay under runtime/.";
                return false;
            }
        }

        return true;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
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
                    Script = BuildRegistryCaptureScript(k)
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

    private static string BuildRegistryCaptureScript(string key)
    {
        var escaped = key.Replace("'", "''");
        return "$WarningPreference='SilentlyContinue'; " +
               $"$p='{escaped}'; " +
               "if (Test-Path $p) { " +
               "$item = Get-ItemProperty -Path $p -ErrorAction Stop; " +
               "$out = [ordered]@{}; " +
               "foreach ($prop in $item.PSObject.Properties) { " +
               "if ($prop.MemberType -ne 'NoteProperty' -or $prop.Name -like 'PS*') { continue }; " +
               "$value = $prop.Value; " +
               "if ($value -is [byte[]]) { $out[$prop.Name] = [Convert]::ToBase64String($value) } else { $out[$prop.Name] = $value } " +
               "}; " +
               "$out | ConvertTo-Json -Depth 8 -Compress " +
               "} else { '' }";
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
