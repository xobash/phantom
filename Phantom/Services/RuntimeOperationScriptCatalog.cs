namespace Phantom.Services;

public static class RuntimeOperationScriptCatalog
{
    private const string OoShutUp10ExpectedPublisher = "O&O Software GmbH";

    private static readonly IReadOnlyDictionary<string, string[]> DnsServersByProfile =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Default"] = [],
            ["DHCP"] = [],
            ["Google"] = ["8.8.8.8", "8.8.4.4"],
            ["Cloudflare"] = ["1.1.1.1", "1.0.0.1"],
            ["Cloudflare_Malware"] = ["1.1.1.2", "1.0.0.2"],
            ["Cloudflare_Malware_Adult"] = ["1.1.1.3", "1.0.0.3"],
            ["Open_DNS"] = ["208.67.222.222", "208.67.220.220"],
            ["Quad9"] = ["9.9.9.9", "149.112.112.112"],
            ["AdGuard_Ads_Trackers"] = ["94.140.14.14", "94.140.15.15"],
            ["AdGuard_Ads_Trackers_Malware_Adult"] = ["94.140.14.15", "94.140.15.16"]
        };

    public static IReadOnlyList<string> GetDnsProfiles()
    {
        return DnsServersByProfile.Keys
            .OrderBy(static profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsSupportedDnsProfile(string profile)
    {
        return !string.IsNullOrWhiteSpace(profile) && DnsServersByProfile.ContainsKey(profile.Trim());
    }

    public static IEnumerable<string> GetTrustedRuntimeMutationScripts()
    {
        foreach (var profile in GetDnsProfiles())
        {
            yield return BuildDnsApplyScript(profile);
        }

        yield return BuildOoShutUp10RunScript();
        yield return BuildOoShutUp10UndoScript();
    }

    public static string BuildDnsApplyScript(string profile)
    {
        if (!DnsServersByProfile.TryGetValue(profile, out var servers))
        {
            throw new InvalidOperationException($"Unsupported DNS profile: {profile}");
        }

        var safeProfileLiteral = PowerShellInputSanitizer.ToSingleQuotedLiteral(profile);
        var adapterScript = "$adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Sort-Object -Property InterfaceMetric | Select-Object -First 1; " +
                            "if ($null -eq $adapter) { $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1 }; " +
                            "if ($null -eq $adapter) { throw 'No active network adapter found.' }; " +
                            "$profileName = " + safeProfileLiteral + "; ";

        if (servers.Length == 0)
        {
            return adapterScript +
                   "Set-DnsClientServerAddress -InterfaceAlias $adapter.Name -ResetServerAddresses -ErrorAction Stop; " +
                   "Write-Output ('Applied DNS profile ' + $profileName + ' to adapter: ' + $adapter.Name)";
        }

        var addresses = string.Join(", ", servers.Select(static server => PowerShellInputSanitizer.ToSingleQuotedLiteral(server)));
        return adapterScript +
               $"$servers=@({addresses}); " +
               "Set-DnsClientServerAddress -InterfaceAlias $adapter.Name -ServerAddresses $servers -ErrorAction Stop; " +
               "Write-Output ('Applied DNS profile ' + $profileName + ' to adapter: ' + $adapter.Name)";
    }

    public static string BuildOoShutUp10RunScript()
    {
        return $$"""
                 $ErrorActionPreference='Stop'
                 Set-StrictMode -Version Latest
                 $toolRoot=Join-Path $env:ProgramData 'Phantom\Tools'
                 $exePath=Join-Path $toolRoot 'OOSU10.exe'
                 New-Item -Path $toolRoot -ItemType Directory -Force | Out-Null
                 try {
                   $acl = Get-Acl -Path $toolRoot -ErrorAction Stop
                   $acl.SetAccessRuleProtection($true, $false)
                   foreach($entry in @($acl.Access)){ [void]$acl.RemoveAccessRule($entry) }
                   $admins = New-Object System.Security.Principal.NTAccount('BUILTIN', 'Administrators')
                   $system = New-Object System.Security.Principal.NTAccount('NT AUTHORITY', 'SYSTEM')
                   $ruleAdmins = New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                   $ruleSystem = New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                   [void]$acl.SetAccessRule($ruleAdmins)
                   [void]$acl.SetAccessRule($ruleSystem)
                   Set-Acl -Path $toolRoot -AclObject $acl -ErrorAction Stop
                 } catch {
                   throw "Failed to enforce secure ACL on $toolRoot. $($_.Exception.Message)"
                 }
                 Invoke-WebRequest -Uri 'https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe' -OutFile $exePath -UseBasicParsing -ErrorAction Stop
                 if(-not (Test-Path $exePath)){ throw 'O&O ShutUp10 download failed.' }
                 $hash=(Get-FileHash -Path $exePath -Algorithm SHA256 -ErrorAction Stop).Hash
                 $sig=Get-AuthenticodeSignature -FilePath $exePath
                 if($sig.Status -ne 'Valid'){ throw "O&O ShutUp10 signature validation failed: $($sig.Status)." }
                 if($null -eq $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch [Regex]::Escape('{{OoShutUp10ExpectedPublisher}}')){ throw "Unexpected O&O ShutUp10 signer: $($sig.SignerCertificate.Subject)." }
                 Start-Process -FilePath 'OOSU10.exe' -WorkingDirectory $toolRoot
                 Write-Output "O&O ShutUp10 launched from $exePath (SHA256 $hash)"
                 """;
    }

    public static string BuildOoShutUp10UndoScript()
    {
        return "$toolRoot=Join-Path $env:ProgramData 'Phantom\\Tools'; $exePath=Join-Path $toolRoot 'OOSU10.exe'; if(Test-Path $exePath){ Remove-Item -Path $exePath -Force -ErrorAction Stop; Write-Output \"Removed cached O&O ShutUp10 from $exePath\" } else { Write-Output 'O&O ShutUp10 is not cached.' }";
    }
}
