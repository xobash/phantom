using Phantom.Models;

namespace Phantom.Services;

public static class UpdateModeOperationFactory
{
    public const string OperationIdDefault = "updates.mode.default";
    public const string OperationIdSecurity = "updates.mode.security";
    public const string OperationIdDisableAll = "updates.mode.disableall";

    public const string RegistryPolicyRootPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string RegistryPolicyAuPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    public static OperationDefinition BuildModeOperation(string? mode)
    {
        return NormalizeMode(mode) switch
        {
            "Disable All" => BuildDisableAllModeOperation(),
            "Security" => BuildSecurityModeOperation(),
            _ => BuildDefaultModeOperation()
        };
    }

    public static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return "Default";
        }

        if (string.Equals(mode, "Disable All", StringComparison.OrdinalIgnoreCase))
        {
            return "Disable All";
        }

        return "Security";
    }

    public static OperationDefinition BuildDefaultModeOperation()
    {
        return new OperationDefinition
        {
            Id = OperationIdDefault,
            Title = "Restore default Windows Update behavior",
            Description = "Undo custom update policies and restore update service startup behavior.",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts = [new PowerShellStep { Name = "restore-default", Script = BuildUpdateDefaultRestoreScript() }],
            DetectScript = BuildDefaultModeDetectScript(),
            UndoScripts = [new PowerShellStep { Name = "undo-to-security", Script = BuildUpdateSecurityRunScript() }]
        };
    }

    public static OperationDefinition BuildSecurityModeOperation()
    {
        return new OperationDefinition
        {
            Id = OperationIdSecurity,
            Title = "Set Security mode",
            Description = "Delay feature updates by 365 days and quality updates by 4 days.",
            RiskTier = RiskTier.Basic,
            Reversible = true,
            RunScripts = [new PowerShellStep { Name = "apply-security", Script = BuildUpdateSecurityRunScript() }],
            DetectScript = BuildSecurityModeDetectScript(),
            UndoScripts = [new PowerShellStep { Name = "undo-default", Script = BuildUpdateSecurityUndoScript() }]
        };
    }

    public static OperationDefinition BuildDisableAllModeOperation()
    {
        return new OperationDefinition
        {
            Id = OperationIdDisableAll,
            Title = "Disable ALL updates",
            Description = "Disables Windows Update services and policies.",
            RiskTier = RiskTier.Dangerous,
            Reversible = true,
            RunScripts = [new PowerShellStep { Name = "disable-updates", Script = BuildUpdateDisableAllRunScript() }],
            DetectScript = BuildDisableAllModeDetectScript(),
            UndoScripts = [new PowerShellStep { Name = "undo-default", Script = BuildUpdateDefaultRestoreScript() }]
        };
    }

    public static string BuildDefaultModeDetectScript()
    {
        return "$au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; $noAuto=$null; if(Test-Path $au){ try { $noAuto=(Get-ItemProperty -Path $au -Name NoAutoUpdate -ErrorAction Stop).NoAutoUpdate } catch { $noAuto=$null } }; $wu=(Get-Service wuauserv -ErrorAction Stop).StartType; $bits=(Get-Service bits -ErrorAction Stop).StartType; if(($noAuto -ne 1) -and $wu -ne 'Disabled' -and $bits -ne 'Disabled'){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'}";
    }

    public static string BuildSecurityModeDetectScript()
    {
        return "$wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; $au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; if((Test-Path $wu) -and (Test-Path $au)){ $p=Get-ItemProperty -Path $wu -ErrorAction Stop; $a=Get-ItemProperty -Path $au -ErrorAction Stop; if($p.DeferFeatureUpdatesPeriodInDays -eq 365 -and $p.DeferQualityUpdatesPeriodInDays -eq 4 -and $a.NoAutoUpdate -eq 0){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'} } else {'PHANTOM_STATUS=NotApplied'}";
    }

    public static string BuildDisableAllModeDetectScript()
    {
        return "$au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; $noAuto=$null; if(Test-Path $au){ try { $noAuto=(Get-ItemProperty -Path $au -Name NoAutoUpdate -ErrorAction Stop).NoAutoUpdate } catch { $noAuto=$null } }; $wu=(Get-Service wuauserv -ErrorAction Stop).StartType; $bits=(Get-Service bits -ErrorAction Stop).StartType; if($noAuto -eq 1 -and $wu -eq 'Disabled' -and $bits -eq 'Disabled'){'PHANTOM_STATUS=Applied'} else {'PHANTOM_STATUS=NotApplied'}";
    }

    public static string BuildPolicySummaryQueryScript()
    {
        return "$wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; $au='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'; " +
               "$feature=$null; $quality=$null; $noAuto=$null; " +
               "if(Test-Path $wu){ try { $feature=(Get-ItemProperty -Path $wu -Name DeferFeatureUpdatesPeriodInDays -ErrorAction Stop).DeferFeatureUpdatesPeriodInDays } catch {}; try { $quality=(Get-ItemProperty -Path $wu -Name DeferQualityUpdatesPeriodInDays -ErrorAction Stop).DeferQualityUpdatesPeriodInDays } catch {} }; " +
               "if(Test-Path $au){ try { $noAuto=(Get-ItemProperty -Path $au -Name NoAutoUpdate -ErrorAction Stop).NoAutoUpdate } catch {} }; " +
               "[PSCustomObject]@{ DeferFeatureUpdatesPeriodInDays=$feature; DeferQualityUpdatesPeriodInDays=$quality; NoAutoUpdate=$noAuto } | ConvertTo-Json -Compress";
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
  $safeName=$serviceName.Replace("'","''")
  return (Get-CimInstance Win32_Service -Filter "Name='$safeName'" -ErrorAction Stop).StartMode
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
  $allowed=@('auto','automatic','manual','disabled')
  $lower=$mode.ToLowerInvariant()
  if($lower -notin $allowed){ Write-Warning "Unknown service mode '$mode', defaulting to Manual" }
  switch ($lower) {
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
}
