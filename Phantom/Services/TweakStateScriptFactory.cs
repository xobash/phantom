namespace Phantom.Services;

internal static class TweakStateScriptFactory
{
    private const string RegistryCapturePrefix = "Registry";

    public static string BuildCaptureScript(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "$null";
        }

        if (key.Equals("PowerScheme", StringComparison.OrdinalIgnoreCase))
        {
            return """
                   $active = (powercfg /GetActiveScheme 2>$null | Out-String)
                   $guid = ''
                   if($active -match '([0-9a-fA-F\-]{36})'){ $guid = $matches[1] }
                   [PSCustomObject]@{ Type='PowerScheme'; ActiveScheme=$guid } | ConvertTo-Json -Compress
                   """;
        }

        if (key.Equals("Hibernate", StringComparison.OrdinalIgnoreCase))
        {
            return """
                   $state = (powercfg /a 2>$null | Out-String)
                   $enabled = ($state -match '(?im)^\s*Hibernate\b') -and ($state -notmatch '(?i)has not been enabled')
                   [PSCustomObject]@{ Type='Hibernate'; Enabled=[bool]$enabled } | ConvertTo-Json -Compress
                   """;
        }

        if (key.Equals("SMB1", StringComparison.OrdinalIgnoreCase))
        {
            return """
                   $enabled = $null
                   try { $enabled = [bool](Get-SmbServerConfiguration -ErrorAction Stop).EnableSMB1Protocol } catch {}
                   [PSCustomObject]@{ Type='SMB1'; Enabled=$enabled } | ConvertTo-Json -Compress
                   """;
        }

        var escaped = EscapePowerShellSingleQuoted(key);
        return "$WarningPreference='Continue'; " +
               $"$p='{escaped}'; " +
               "if (Test-Path $p) { " +
               "$key = Get-Item -Path $p -ErrorAction Stop; " +
               "$values = @(); " +
               "foreach ($name in $key.GetValueNames()) { " +
               "$kind = [string]$key.GetValueKind($name); " +
               "$raw = $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames'); " +
               "$value = if ($raw -is [byte[]]) { [System.BitConverter]::ToString($raw).Replace('-','') } else { $raw }; " +
               "$values += [PSCustomObject]@{ Name=$name; Kind=$kind; Value=$value }; " +
               "}; " +
               $"[PSCustomObject]@{{ Type='{RegistryCapturePrefix}'; Exists=$true; Values=$values }} | ConvertTo-Json -Depth 8 -Compress " +
               $"}} else {{ [PSCustomObject]@{{ Type='{RegistryCapturePrefix}'; Exists=$false; Values=@() }} | ConvertTo-Json -Depth 8 -Compress }}";
    }

    public static string BuildRestoreScript(string key, string capturedJson)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(capturedJson))
        {
            return "Write-Output 'No captured state to restore.'";
        }

        var escapedKey = EscapePowerShellSingleQuoted(key);
        var escapedJson = EscapePowerShellSingleQuoted(capturedJson);

        if (key.Equals("PowerScheme", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSpecialRestoreScript(escapedJson, """
                   if($state.ActiveScheme){
                     powercfg /setactive ([string]$state.ActiveScheme) 2>$null | Out-Null
                     Write-Output 'Restored captured power scheme.'
                   } else {
                     Write-Output 'Captured power scheme was empty; no state restored.'
                   }
                   """);
        }

        if (key.Equals("Hibernate", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSpecialRestoreScript(escapedJson, """
                   if($state.Enabled -eq $true){
                     powercfg /hibernate on 2>$null | Out-Null
                     Write-Output 'Restored captured hibernation state: on.'
                   } elseif($state.Enabled -eq $false) {
                     powercfg /hibernate off 2>$null | Out-Null
                     Write-Output 'Restored captured hibernation state: off.'
                   } else {
                     Write-Output 'Captured hibernation state was unknown; no state restored.'
                   }
                   """);
        }

        if (key.Equals("SMB1", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSpecialRestoreScript(escapedJson, """
                   if($state.Enabled -eq $true){
                     Set-SmbServerConfiguration -EnableSMB1Protocol $true -Force
                     Write-Output 'Restored captured SMBv1 server state: enabled.'
                   } elseif($state.Enabled -eq $false) {
                     Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force
                     Write-Output 'Restored captured SMBv1 server state: disabled.'
                   } else {
                     Write-Output 'Captured SMBv1 state was unknown; no state restored.'
                   }
                   """);
        }

        return "$ErrorActionPreference='Stop'; " +
               $"$p='{escapedKey}'; " +
               $"$json='{escapedJson}'; " +
               "if([string]::IsNullOrWhiteSpace($json)){ Write-Output 'Captured registry state was empty.'; return }; " +
               "$state=$json | ConvertFrom-Json; " +
               "if($state.Exists -eq $false){ if(Test-Path $p){ Remove-Item -Path $p -Recurse -Force }; Write-Output 'Restored captured missing registry key.'; return }; " +
               "if(!(Test-Path $p)){ New-Item -Path $p -Force | Out-Null }; " +
               "$desired=@{}; " +
               "foreach($entry in @($state.Values)){ " +
               "$name=[string]$entry.Name; " +
               "$kind=[string]$entry.Kind; $raw=$entry.Value; $desired[$name]=$true; " +
               "$value=$raw; " +
               "switch($kind){ " +
               "'Binary' { $hex=[string]$raw; $bytes=New-Object byte[] ($hex.Length / 2); for($i=0; $i -lt $bytes.Length; $i++){ $bytes[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16) }; $value=$bytes } " +
               "'DWord' { $value=[int]$raw } " +
               "'QWord' { $value=[long]$raw } " +
               "'MultiString' { $value=[string[]]@($raw) } " +
               "default { if($null -ne $raw){ $value=[string]$raw } } " +
               "} " +
               "New-ItemProperty -Path $p -Name $name -PropertyType $kind -Value $value -Force | Out-Null; " +
               "} " +
               "$current=Get-Item -Path $p -ErrorAction Stop; " +
               "foreach($name in $current.GetValueNames()){ if(-not $desired.ContainsKey($name)){ Remove-ItemProperty -Path $p -Name $name -Force -ErrorAction SilentlyContinue } }; " +
               "Write-Output 'Restored captured registry state.'";
    }

    private static string BuildSpecialRestoreScript(string escapedJson, string body)
    {
        return "$ErrorActionPreference='Stop'; " +
               $"$json='{escapedJson}'; " +
               "if([string]::IsNullOrWhiteSpace($json)){ Write-Output 'Captured state was empty.'; return }; " +
               "$state=$json | ConvertFrom-Json; " +
               body;
    }

    private static string EscapePowerShellSingleQuoted(string text)
    {
        return text.Replace("'", "''", StringComparison.Ordinal);
    }
}
