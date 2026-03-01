using System.Text.Json;
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
            ConfirmDangerousAsync = _ => Task.FromResult(config.ConfirmDangerous && forceDangerous)
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
                    Compatibility = t.Compatibility ?? Array.Empty<string>(),
                    DetectScript = t.DetectScript,
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

        operations.Add(config.UpdateMode switch
        {
            "Disable All" => new OperationDefinition
            {
                Id = "updates.mode.disableall",
                Title = "Disable updates",
                Description = "Disable all updates",
                RiskTier = RiskTier.Dangerous,
                Reversible = true,
                DetectScript = "$au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; $noAuto=$null; if(Test-Path $au){ try { $noAuto=(Get-ItemProperty -Path $au -Name NoAutoUpdate -ErrorAction Stop).NoAutoUpdate } catch { $noAuto=$null } }; $wu=(Get-Service wuauserv -ErrorAction Stop).StartType; $bits=(Get-Service bits -ErrorAction Stop).StartType; if($noAuto -eq 1 -and $wu -eq 'Disabled' -and $bits -eq 'Disabled'){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'}",
                RunScripts = [new PowerShellStep { Name = "disable", Script = BuildUpdateDisableAllRunScript() }],
                UndoScripts = [new PowerShellStep { Name = "default", Script = BuildUpdateDefaultRestoreScript() }]
            },
            "Security" => new OperationDefinition
            {
                Id = "updates.mode.security",
                Title = "Security update mode",
                Description = "Security mode",
                RiskTier = RiskTier.Basic,
                Reversible = true,
                DetectScript = "$wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; $au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; if((Test-Path $wu) -and (Test-Path $au)){ $p=Get-ItemProperty -Path $wu -ErrorAction Stop; $a=Get-ItemProperty -Path $au -ErrorAction Stop; if($p.DeferFeatureUpdatesPeriodInDays -eq 365 -and $p.DeferQualityUpdatesPeriodInDays -eq 4 -and $a.NoAutoUpdate -eq 0){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'} } else {'PHANTOM_STATUS=NotApplied'}",
                RunScripts = [new PowerShellStep { Name = "security", Script = BuildUpdateSecurityRunScript() }],
                UndoScripts = [new PowerShellStep { Name = "default", Script = BuildUpdateSecurityUndoScript() }]
            },
            _ => new OperationDefinition
            {
                Id = "updates.mode.default",
                Title = "Default updates",
                Description = "Restore default update behavior",
                RiskTier = RiskTier.Basic,
                Reversible = true,
                DetectScript = "$au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; $noAuto=$null; if(Test-Path $au){ try { $noAuto=(Get-ItemProperty -Path $au -Name NoAutoUpdate -ErrorAction Stop).NoAutoUpdate } catch { $noAuto=$null } }; $wu=(Get-Service wuauserv -ErrorAction Stop).StartType; $bits=(Get-Service bits -ErrorAction Stop).StartType; if(($noAuto -ne 1) -and $wu -ne 'Disabled' -and $bits -ne 'Disabled'){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'}",
                RunScripts = [new PowerShellStep { Name = "default", Script = BuildUpdateDefaultRestoreScript() }],
                UndoScripts = [new PowerShellStep { Name = "none", Script = "Write-Output 'No-op'" }]
            }
        });

        return operations;
    }

    private static string BuildRegistryCaptureScript(string key)
    {
        var escaped = key.Replace("'", "''");
        return "$WarningPreference='Continue'; " +
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

    private static string BuildUpdateDisableAllRunScript()
    {
        return """
$ErrorActionPreference='Stop'
$auSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
$stateDir=Join-Path $env:ProgramData 'Phantom\state'
$statePath=Join-Path $stateDir 'windows-update-service-modes.json'

function Set-RegistryDword64([string]$subKey,[string]$name,[int]$value) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.CreateSubKey($subKey)
    if($null -eq $key){ throw "Unable to open HKLM:\$subKey" }
    try { $key.SetValue($name,$value,[Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
  } finally {
    $base.Dispose()
  }
}

function Get-ServiceStartMode([string]$serviceName) {
  return (Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop).StartMode
}

New-Item -Path $stateDir -ItemType Directory -Force -ErrorAction Stop | Out-Null
@{
  WuauservStartMode = Get-ServiceStartMode 'wuauserv'
  BitsStartMode = Get-ServiceStartMode 'bits'
} | ConvertTo-Json -Compress | Set-Content -Path $statePath -Encoding UTF8 -Force -ErrorAction Stop

Set-RegistryDword64 -subKey $auSubKey -name 'NoAutoUpdate' -value 1
Stop-Service -Name wuauserv -Force -ErrorAction Stop
Stop-Service -Name bits -Force -ErrorAction Stop
Set-Service -Name wuauserv -StartupType Disabled -ErrorAction Stop
Set-Service -Name bits -StartupType Disabled -ErrorAction Stop
""";
    }

    private static string BuildUpdateDefaultRestoreScript()
    {
        return """
$ErrorActionPreference='Stop'
$wuSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$auSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
$statePath=Join-Path (Join-Path $env:ProgramData 'Phantom\state') 'windows-update-service-modes.json'

function Remove-RegistryValue64([string]$subKey,[string]$name) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.OpenSubKey($subKey,$true)
    if($null -eq $key){ return }
    try {
      if($null -ne $key.GetValue($name,$null)){ $key.DeleteValue($name,$false) }
    } finally {
      $key.Dispose()
    }
  } finally {
    $base.Dispose()
  }
}

function Remove-RegistrySubKeyIfEmpty64([string]$subKey) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.OpenSubKey($subKey,$false)
    if($null -eq $key){ return }
    try {
      $valueNames=$key.GetValueNames() | Where-Object { $_ -ne '' }
      if($key.SubKeyCount -eq 0 -and $valueNames.Count -eq 0){
        $base.DeleteSubKey($subKey,$false)
      }
    } finally {
      $key.Dispose()
    }
  } finally {
    $base.Dispose()
  }
}

function Resolve-ServiceStartupType([string]$mode) {
  switch ($mode.ToLowerInvariant()) {
    'auto' { return 'Automatic' }
    'automatic' { return 'Automatic' }
    'manual' { return 'Manual' }
    'disabled' { return 'Disabled' }
    default { return 'Manual' }
  }
}

$wuMode='Manual'
$bitsMode='Manual'
if(Test-Path $statePath){
  try {
    $state=Get-Content -Path $statePath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if($null -ne $state -and $null -ne $state.WuauservStartMode -and -not [string]::IsNullOrWhiteSpace($state.WuauservStartMode)){ $wuMode=[string]$state.WuauservStartMode }
    if($null -ne $state -and $null -ne $state.BitsStartMode -and -not [string]::IsNullOrWhiteSpace($state.BitsStartMode)){ $bitsMode=[string]$state.BitsStartMode }
  } catch {
  }
}

$wuStartup=Resolve-ServiceStartupType $wuMode
$bitsStartup=Resolve-ServiceStartupType $bitsMode
Set-Service -Name wuauserv -StartupType $wuStartup -ErrorAction Stop
Set-Service -Name bits -StartupType $bitsStartup -ErrorAction Stop
if($wuStartup -ne 'Disabled'){ Start-Service -Name wuauserv -ErrorAction Stop }
if($bitsStartup -ne 'Disabled'){ Start-Service -Name bits -ErrorAction Stop }

Remove-RegistryValue64 -subKey $auSubKey -name 'NoAutoUpdate'
Remove-RegistryValue64 -subKey $wuSubKey -name 'DeferFeatureUpdatesPeriodInDays'
Remove-RegistryValue64 -subKey $wuSubKey -name 'DeferQualityUpdatesPeriodInDays'
Remove-RegistrySubKeyIfEmpty64 -subKey $auSubKey
Remove-RegistrySubKeyIfEmpty64 -subKey $wuSubKey
if(Test-Path $statePath){ Remove-Item -Path $statePath -Force -ErrorAction SilentlyContinue }
""";
    }

    private static string BuildUpdateSecurityRunScript()
    {
        return """
$ErrorActionPreference='Stop'
$wuSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$auSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'

function Set-RegistryDword64([string]$subKey,[string]$name,[int]$value) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.CreateSubKey($subKey)
    if($null -eq $key){ throw "Unable to open HKLM:\$subKey" }
    try { $key.SetValue($name,$value,[Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
  } finally {
    $base.Dispose()
  }
}

Set-RegistryDword64 -subKey $wuSubKey -name 'DeferFeatureUpdatesPeriodInDays' -value 365
Set-RegistryDword64 -subKey $wuSubKey -name 'DeferQualityUpdatesPeriodInDays' -value 4
Set-RegistryDword64 -subKey $auSubKey -name 'NoAutoUpdate' -value 0
""";
    }

    private static string BuildUpdateSecurityUndoScript()
    {
        return """
$ErrorActionPreference='Stop'
$wuSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$auSubKey='SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'

function Remove-RegistryValue64([string]$subKey,[string]$name) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.OpenSubKey($subKey,$true)
    if($null -eq $key){ return }
    try {
      if($null -ne $key.GetValue($name,$null)){ $key.DeleteValue($name,$false) }
    } finally {
      $key.Dispose()
    }
  } finally {
    $base.Dispose()
  }
}

function Remove-RegistrySubKeyIfEmpty64([string]$subKey) {
  $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$base.OpenSubKey($subKey,$false)
    if($null -eq $key){ return }
    try {
      $valueNames=$key.GetValueNames() | Where-Object { $_ -ne '' }
      if($key.SubKeyCount -eq 0 -and $valueNames.Count -eq 0){
        $base.DeleteSubKey($subKey,$false)
      }
    } finally {
      $key.Dispose()
    }
  } finally {
    $base.Dispose()
  }
}

Remove-RegistryValue64 -subKey $auSubKey -name 'NoAutoUpdate'
Remove-RegistryValue64 -subKey $wuSubKey -name 'DeferFeatureUpdatesPeriodInDays'
Remove-RegistryValue64 -subKey $wuSubKey -name 'DeferQualityUpdatesPeriodInDays'
Remove-RegistrySubKeyIfEmpty64 -subKey $auSubKey
Remove-RegistrySubKeyIfEmpty64 -subKey $wuSubKey
""";
    }

    private static OperationDefinition BuildStoreInstallOperation(CatalogApp app)
    {
        var packageQuery = PowerShellInputSanitizer.EnsurePackageQuery(app.DisplayName, $"store app '{app.DisplayName}' displayName");
        var wingetId = string.IsNullOrWhiteSpace(app.WingetId)
            ? string.Empty
            : PowerShellInputSanitizer.EnsurePackageId(app.WingetId, $"store app '{app.DisplayName}' wingetId");
        var chocoId = string.IsNullOrWhiteSpace(app.ChocoId)
            ? string.Empty
            : PowerShellInputSanitizer.EnsurePackageId(app.ChocoId, $"store app '{app.DisplayName}' chocoId");

        string winget = wingetId.Length == 0
            ? $"winget install --name {PowerShellInputSanitizer.ToSingleQuotedLiteral(packageQuery)} --exact --accept-source-agreements --accept-package-agreements --silent"
            : $"winget install --id {PowerShellInputSanitizer.ToSingleQuotedLiteral(wingetId)} -e --accept-source-agreements --accept-package-agreements --silent";
        string choco = chocoId.Length == 0
            ? string.Empty
            : $"choco install {PowerShellInputSanitizer.ToSingleQuotedLiteral(chocoId)} -y";

        var managerProbeScript =
            "$hasWinget = $null -ne (Get-Command winget -ErrorAction SilentlyContinue); " +
            "$hasChoco = $null -ne (Get-Command choco -ErrorAction SilentlyContinue); " +
            "if (-not $hasChoco) { $hasChoco = Test-Path (Join-Path $env:ProgramData 'chocolatey\\bin\\choco.exe') }; ";

        var script = !string.IsNullOrWhiteSpace(winget) && !string.IsNullOrWhiteSpace(choco)
            ? $"{managerProbeScript}if($hasWinget){{ {winget} }} elseif($hasChoco){{ {choco} }} else {{ throw 'No package manager available' }}"
            : !string.IsNullOrWhiteSpace(winget)
                ? $"{managerProbeScript}if($hasWinget){{ {winget} }} else {{ throw 'winget missing' }}"
                : $"{managerProbeScript}if($hasChoco){{ {choco} }} else {{ throw 'choco missing' }}";

        return new OperationDefinition
        {
            Id = $"store.app.{new string(app.DisplayName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()}",
            Title = $"Install {app.DisplayName}",
            Description = "Install app from catalog",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts = [new PowerShellStep { Name = "install", Script = script, RequiresNetwork = true }],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "uninstall",
                    Script = wingetId.Length == 0 && chocoId.Length > 0
                        ? $"choco uninstall {PowerShellInputSanitizer.ToSingleQuotedLiteral(chocoId)} -y"
                        : wingetId.Length == 0
                            ? $"winget uninstall --name {PowerShellInputSanitizer.ToSingleQuotedLiteral(packageQuery)} --exact --silent"
                            : $"winget uninstall --id {PowerShellInputSanitizer.ToSingleQuotedLiteral(wingetId)} -e --silent"
                }
            ]
        };
    }
}
