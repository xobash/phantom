using System.Text.Json;
using Phantom.Models;

namespace Phantom.Services;

public sealed class CliRunner
{
    private const string RequiredDangerousAcknowledgement = "I_UNDERSTAND_NO_ROLLBACK";

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

    public async Task<int> RunAsync(string configPath, bool forceDangerous, string? dangerousAcknowledgement, bool skipCaptureCheck, CancellationToken cancellationToken)
    {
        _console.Publish("Trace", $"CliRunner.RunAsync started. configPath={configPath}, forceDangerous={forceDangerous}, skipCaptureCheck={skipCaptureCheck}");
        await _log.WriteAsync("Trace", $"CliRunner.RunAsync started. configPath={configPath}, forceDangerous={forceDangerous}, skipCaptureCheck={skipCaptureCheck}", cancellationToken).ConfigureAwait(false);

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
        AutomationConfig config;
        try
        {
            config = await _definitions.LoadSelectionConfigAsync(normalizedConfigPath, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            var error = $"CLI config validation failed: {ex.Message}";
            _console.Publish("Error", error);
            await _log.WriteAsync("Error", error, cancellationToken).ConfigureAwait(false);
            return 2;
        }
        catch (JsonException ex)
        {
            var error = $"CLI config parsing failed: {ex.Message}";
            _console.Publish("Error", error);
            await _log.WriteAsync("Error", error, cancellationToken).ConfigureAwait(false);
            return 2;
        }
        List<OperationDefinition> operations;
        try
        {
            operations = await BuildOperationsAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = $"CLI operation generation failed: {ex.Message}";
            _console.Publish("Error", error);
            await _log.WriteAsync("Error", error, cancellationToken).ConfigureAwait(false);
            return 6;
        }
        _console.Publish("Trace", $"CliRunner resolved operations: {operations.Count}");

        if (operations.Count == 0)
        {
            await _log.WriteAsync("Info", "No operations selected in config.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var hasDangerous = operations.Any(o => o.RiskTier == RiskTier.Dangerous || !o.Reversible || o.Destructive);
        var acknowledgement = (dangerousAcknowledgement ?? config.DangerousAcknowledgement ?? string.Empty).Trim();
        var forceDangerousEnabled = config.ConfirmDangerous && forceDangerous;

        if (hasDangerous && !forceDangerousEnabled)
        {
            await _log.WriteAsync("Error", "Dangerous operations requested but not confirmed. Set confirmDangerous=true and pass -ForceDangerous.", cancellationToken).ConfigureAwait(false);
            return 3;
        }

        if (hasDangerous && !string.Equals(acknowledgement, RequiredDangerousAcknowledgement, StringComparison.Ordinal))
        {
            var ackMessage = $"Dangerous operations require -AcknowledgeDangerous {RequiredDangerousAcknowledgement}.";
            _console.Publish("Error", ackMessage);
            await _log.WriteAsync("Error", ackMessage, cancellationToken).ConfigureAwait(false);
            return 3;
        }

        if (skipCaptureCheck && !forceDangerousEnabled)
        {
            var skipCaptureMessage = "The -SkipCaptureCheck override is allowed only together with confirmDangerous=true and -ForceDangerous.";
            _console.Publish("Error", skipCaptureMessage);
            await _log.WriteAsync("Error", skipCaptureMessage, cancellationToken).ConfigureAwait(false);
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
                EnableDestructiveOperations = forceDangerousEnabled || settings.EnableDestructiveOperations,
                ForceDangerous = forceDangerousEnabled,
                SkipCaptureCheck = skipCaptureCheck,
                ConfirmDangerousAsync = prompt =>
                {
                _console.Publish("Warning", $"CLI dangerous confirmation: {prompt}");
                return Task.FromResult(forceDangerousEnabled && string.Equals(acknowledgement, RequiredDangerousAcknowledgement, StringComparison.Ordinal));
            }
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
        operations.AddRange(selectedApps.Select(app => OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Install)));

        var tweaks = await _definitions.LoadTweaksAsync(cancellationToken).ConfigureAwait(false);
        var tweakLookup = tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var requested in CatalogTrustService.GetRequestedTweaks())
        {
            if (tweakLookup.ContainsKey(requested.Id))
            {
                continue;
            }

            tweaks.Add(requested);
            tweakLookup[requested.Id] = requested;
        }

        operations.AddRange(tweaks
            .Where(t => config.Tweaks.Contains(t.Id, StringComparer.OrdinalIgnoreCase))
            .Select(OperationDefinitionFactory.BuildTweakOperation));

        var features = await _definitions.LoadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        operations.AddRange(features
            .Where(f => config.Features.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
            .Select(f =>
            {
                var safeFeatureName = PowerShellInputSanitizer.EnsureFeatureName(f.FeatureName, $"feature '{f.Id}'");
                var featureLiteral = PowerShellInputSanitizer.ToSingleQuotedLiteral(safeFeatureName);
                return new OperationDefinition
                {
                    Id = $"feature.{f.Id}",
                    Title = $"Enable {f.Name}",
                    Description = f.Description,
                    RiskTier = RiskTier.Advanced,
                    Reversible = true,
                    RequiresReboot = true,
                    Compatibility = f.Compatibility ?? Array.Empty<string>(),
                    RunScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName {featureLiteral} -All -NoRestart -ErrorAction Stop" }],
                    UndoScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName {featureLiteral} -NoRestart -ErrorAction Stop" }]
                };
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
                Compatibility = f.Compatibility ?? Array.Empty<string>(),
                RunScripts = [new PowerShellStep { Name = "apply", Script = f.ApplyScript }],
                UndoScripts = [new PowerShellStep { Name = "undo", Script = f.UndoScript }]
            }));

        operations.Add(UpdateModeOperationFactory.BuildModeOperation(config.UpdateMode));

        return operations;
    }

}
