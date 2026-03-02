namespace Phantom.Services;

public static class RuntimeOperationScriptCatalog
{
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
        return """
               $downloadUrl='https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe'
               $toolRoot=Join-Path $env:TEMP 'Phantom\Tools'
               $exePath=Join-Path $toolRoot 'OOSU10.exe'
               New-Item -Path $toolRoot -ItemType Directory -Force | Out-Null
               Invoke-WebRequest -Uri $downloadUrl -OutFile $exePath -UseBasicParsing -ErrorAction Stop
               if(-not (Test-Path $exePath)){ throw 'O&O ShutUp10 download failed.' }
               Start-Process -FilePath $exePath
               Write-Output "O&O ShutUp10 launched from $exePath"
               """;
    }

    public static string BuildOoShutUp10UndoScript()
    {
        return "Write-Output 'No undo action for O&O ShutUp10 execution.'";
    }
}
