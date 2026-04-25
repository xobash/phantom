using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Phantom.Models;

namespace Phantom.Services;

public sealed class HomeDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _query;
    private readonly TelemetryStore _telemetryStore;
    private TelemetryState? _telemetry;

    public HomeDataService(ConsoleStreamService console, PowerShellQueryService query, TelemetryStore telemetryStore)
    {
        _console = console;
        _query = query;
        _telemetryStore = telemetryStore;
    }

    public async Task<HomeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken, bool includeDetails = true)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            _console.Publish("Warning", "HomeDataService.GetSnapshotAsync unavailable on non-Windows host.");
            return new HomeSnapshot
            {
                Motherboard = "Unavailable on non-Windows host",
                Windows = Environment.OSVersion.VersionString
            };
        }

        _telemetry ??= await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var json = await RunPowerShellForJsonAsync(BuildSnapshotScript(includeDetails: false), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _console.Publish("Warning", "HomeDataService.GetSnapshotAsync returned empty JSON.");
            return new HomeSnapshot();
        }

        var snapshot = JsonSerializer.Deserialize<HomeSnapshot>(json, JsonOptions) ?? new HomeSnapshot();
        if (includeDetails)
        {
            snapshot.Apps = (await GetInstalledAppsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            snapshot.Services = (await GetServicesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            snapshot.AppsCount = snapshot.Apps.Count;
            snapshot.ServicesCount = snapshot.Services.Count;
        }

        snapshot.NetworkUsage = ComputeNetworkUsage(_telemetry);
        await _telemetryStore.SaveAsync(_telemetry, cancellationToken).ConfigureAwait(false);

        return snapshot;
    }

    public Task<IReadOnlyList<InstalledAppInfo>> GetInstalledAppsAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return Task.FromResult<IReadOnlyList<InstalledAppInfo>>(Array.Empty<InstalledAppInfo>());
        }

        return Task.Run<IReadOnlyList<InstalledAppInfo>>(EnumerateInstalledApps, cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceInfoRow>> GetServicesAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return Array.Empty<ServiceInfoRow>();
        }

        var json = await RunPowerShellForJsonAsync(BuildServicesScript(), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ServiceInfoRow>();
        }

        return DeserializeJsonList<ServiceInfoRow>(json);
    }

    public async Task<(double CpuUsage, double MemoryUsage, double GpuUsage, string NetworkUsage)> GetLiveMetricsAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return (0, 0, 0, "↑ 0 B/s  ↓ 0 B/s  • 0 B");
        }

        _telemetry ??= await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        const string script = @"
$ErrorActionPreference = 'Continue'
$os = $null
try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch {}
$cpu = 0
try {
  $cpuSample = (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples | Select-Object -First 1
  if ($cpuSample) { $cpu = $cpuSample.CookedValue }
} catch {}
$memoryPct = 0
if ($os -and $os.TotalVisibleMemorySize -gt 0) {
  $memoryPct = (($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100
}
$gpuCtr = 0
try {
  $gpuSample = (Get-Counter '\GPU Engine(_Total)\Utilization Percentage' -ErrorAction Stop).CounterSamples | Select-Object -First 1
  if ($gpuSample) { $gpuCtr = $gpuSample.CookedValue }
}
catch {
  $gpuCtr = 0
}
[PSCustomObject]@{
  CpuUsage = [math]::Round($cpu, 2)
  MemoryUsage = [math]::Round($memoryPct, 2)
  GpuUsage = [math]::Round([math]::Min([math]::Max($gpuCtr, 0), 100), 2)
} | ConvertTo-Json -Compress";

        var json = await RunPowerShellForJsonAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _console.Publish("Warning", "HomeDataService.GetLiveMetricsAsync returned empty JSON.");
            return (0, 0, 0, ComputeNetworkUsage(_telemetry));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var cpu = doc.RootElement.GetProperty("CpuUsage").GetDouble();
            var memory = doc.RootElement.GetProperty("MemoryUsage").GetDouble();
            var gpu = doc.RootElement.GetProperty("GpuUsage").GetDouble();
            var network = ComputeNetworkUsage(_telemetry);
            return (cpu, memory, gpu, network);
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics parse failed: {ex.Message}");
            return (0, 0, 0, ComputeNetworkUsage(_telemetry));
        }
    }

    public async Task<string> RunWinsatScoreAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return "Unavailable";
        }

        var ranFormal = await RunWinsatFormalAsync(cancellationToken).ConfigureAwait(false);
        if (!ranFormal)
        {
            return "Unavailable";
        }

        try
        {
            var dataStorePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Performance",
                "WinSAT",
                "DataStore");
            if (!Directory.Exists(dataStorePath))
            {
                throw new DirectoryNotFoundException($"WinSAT data store not found: {dataStorePath}");
            }

            var latestXml = Directory
                .EnumerateFiles(dataStorePath, "*Formal*.WinSAT.xml", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latestXml is null)
            {
                throw new FileNotFoundException("WinSAT XML not found.");
            }

            await using var stream = File.OpenRead(latestXml.FullName);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var score = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("SystemScore", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
            return string.IsNullOrWhiteSpace(score) ? "Unavailable" : score;
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"WinSAT parse failed: {ex.Message}");
            return "Unavailable";
        }
    }

    private async Task<string> RunPowerShellForJsonAsync(string script, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await _query.InvokeAsync(script, cancellationToken, echoToConsole: false, logExecution: false).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return ExtractJsonPayload(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _console.Publish("Error", stderr.Trim());
        }

        return string.Empty;
    }

    private static string ExtractJsonPayload(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return string.Empty;
        }

        var trimmed = stdout.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            return trimmed;
        }

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        var start = objectStart < 0
            ? arrayStart
            : arrayStart < 0
                ? objectStart
                : Math.Min(objectStart, arrayStart);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = Math.Max(trimmed.LastIndexOf('}'), trimmed.LastIndexOf(']'));
        return end > start ? trimmed[start..(end + 1)] : string.Empty;
    }

    private static IReadOnlyList<T> DeserializeJsonList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<T>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var item = JsonSerializer.Deserialize<T>(json, JsonOptions);
                return item is null ? Array.Empty<T>() : [item];
            }
        }
        catch
        {
        }

        return Array.Empty<T>();
    }

    private static IReadOnlyList<InstalledAppInfo> EnumerateInstalledApps()
    {
        const string uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var apps = new Dictionary<string, InstalledAppInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in GetRegistryViewsToScan())
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallRoot = baseKey.OpenSubKey(uninstallSubKey, writable: false);
                    if (uninstallRoot is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                    {
                        using var appKey = uninstallRoot.OpenSubKey(subKeyName, writable: false);
                        if (appKey is null || IsHiddenUninstallEntry(appKey))
                        {
                            continue;
                        }

                        var name = GetRegistryString(appKey, "DisplayName");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var app = new InstalledAppInfo
                        {
                            Name = name,
                            Version = GetRegistryString(appKey, "DisplayVersion"),
                            Publisher = GetRegistryString(appKey, "Publisher"),
                            InstallDate = FormatInstallDate(GetRegistryString(appKey, "InstallDate")),
                            SizeOnDisk = FormatEstimatedSize(GetRegistryLong(appKey, "EstimatedSize")),
                            InstallLocation = GetRegistryString(appKey, "InstallLocation"),
                            UninstallCommand = FirstNonEmpty(
                                GetRegistryString(appKey, "QuietUninstallString"),
                                GetRegistryString(appKey, "UninstallString")),
                            DisplayIcon = GetRegistryString(appKey, "DisplayIcon")
                        };

                        var key = $"{app.Name}|{app.Version}|{app.Publisher}";
                        apps.TryAdd(key, app);
                    }
                }
                catch
                {
                }
            }
        }

        return apps.Values
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsHiddenUninstallEntry(RegistryKey appKey)
    {
        var systemComponent = GetRegistryLong(appKey, "SystemComponent");
        if (systemComponent == 1)
        {
            return true;
        }

        var releaseType = GetRegistryString(appKey, "ReleaseType");
        return releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
               releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RegistryView> GetRegistryViewsToScan()
    {
        if (Environment.Is64BitOperatingSystem)
        {
            yield return RegistryView.Registry64;
            yield return RegistryView.Registry32;
            yield break;
        }

        yield return RegistryView.Registry32;
    }

    private static string GetRegistryString(RegistryKey key, string name)
    {
        return key.GetValue(name)?.ToString() ?? string.Empty;
    }

    private static long GetRegistryLong(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first;
    }

    private static string FormatInstallDate(string raw)
    {
        if (raw.Length == 8 &&
            DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw;
    }

    private static string FormatEstimatedSize(long estimatedSizeKb)
    {
        return estimatedSizeKb <= 0 ? "Unknown" : FormatBytes(estimatedSizeKb * 1024, decimals: 1);
    }

    private async Task<bool> RunWinsatFormalAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winsat.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("formal");

        using var process = Process.Start(psi);
        if (process is null)
        {
            _console.Publish("Error", "HomeDataService.RunWinsatScoreAsync failed to start winsat.exe.");
            return false;
        }

        var processCompleted = 0;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref processCompleted, 0, 0) != 0)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref processCompleted, 1);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            return true;
        }

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (!string.IsNullOrWhiteSpace(output))
        {
            _console.Publish("Error", output.Trim());
        }

        return false;
    }

    private static string ComputeNetworkUsage(TelemetryState telemetry)
    {
        // Single pass: enumerate NICs, accumulate totals, and update baselines in one loop.
        // Avoids .Where().Where().ToList() LINQ chain that allocates intermediate collections.
        long totalSent = 0;
        long totalRecv = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var stats = nic.GetIPv4Statistics();
            totalSent += stats.BytesSent;
            totalRecv += stats.BytesReceived;

            if (!telemetry.NetworkBaselines.ContainsKey(nic.Id))
            {
                telemetry.NetworkBaselines[nic.Id] = new NetworkBaseline
                {
                    SentBytes = stats.BytesSent,
                    ReceivedBytes = stats.BytesReceived
                };
            }
        }

        long baselineSent = 0;
        long baselineRecv = 0;
        foreach (var baseline in telemetry.NetworkBaselines.Values)
        {
            baselineSent += baseline.SentBytes;
            baselineRecv += baseline.ReceivedBytes;
        }

        var deltaSent = Math.Max(0, totalSent - baselineSent);
        var deltaRecv = Math.Max(0, totalRecv - baselineRecv);

        var now = DateTimeOffset.UtcNow;
        var sampleWindowSeconds = telemetry.LastNetworkSampleAt is null
            ? 1
            : Math.Max(1, (now - telemetry.LastNetworkSampleAt.Value).TotalSeconds);

        var uploadRate = telemetry.LastNetworkSampleAt is null
            ? 0
            : Math.Max(0, (totalSent - telemetry.LastNetworkSentBytes) / sampleWindowSeconds);
        var downloadRate = telemetry.LastNetworkSampleAt is null
            ? 0
            : Math.Max(0, (totalRecv - telemetry.LastNetworkReceivedBytes) / sampleWindowSeconds);

        telemetry.LastNetworkSentBytes = totalSent;
        telemetry.LastNetworkReceivedBytes = totalRecv;
        telemetry.LastNetworkSampleAt = now;

        var uploadText = FormatBytes((long)uploadRate, decimals: 0);
        var downloadText = FormatBytes((long)downloadRate, decimals: 0);
        var sessionText = FormatBytes(deltaSent + deltaRecv, decimals: 1);
        return $"↑ {uploadText}/s  ↓ {downloadText}/s  • {sessionText}";
    }

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private static string FormatBytes(long bytes, int decimals = 2)
    {
        double value = bytes;
        var unit = 0;
        while (value > 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var safeDecimals = unit == 0 ? 0 : Math.Max(0, decimals);
        return $"{value.ToString($"F{safeDecimals}")} {ByteUnits[unit]}";
    }

    private static string BuildServicesScript()
    {
        return """
               $ErrorActionPreference = 'Continue'
               $rows = @()
               try {
                 $rows = @(Get-CimInstance Win32_Service -ErrorAction Stop | ForEach-Object {
                   [PSCustomObject]@{
                     Name = if($_.Name){[string]$_.Name}else{''}
                     DisplayName = if($_.DisplayName){[string]$_.DisplayName}else{[string]$_.Name}
                     StartupType = if($_.StartMode){[string]$_.StartMode}else{'Unknown'}
                     Status = if($_.State){[string]$_.State}else{'Unknown'}
                     PathName = if($_.PathName){[string]$_.PathName}else{''}
                     Description = if($_.Description){[string]$_.Description}else{''}
                     Summary = if([string]::IsNullOrWhiteSpace([string]$_.Description)){'No description provided by this service.'}else{[string]$_.Description}
                   }
                 })
               } catch {
                 $rows = @(Get-Service -ErrorAction SilentlyContinue | ForEach-Object {
                   [PSCustomObject]@{
                     Name = if($_.Name){[string]$_.Name}else{''}
                     DisplayName = if($_.DisplayName){[string]$_.DisplayName}else{[string]$_.Name}
                     StartupType = if($_.StartType){[string]$_.StartType}else{'Unknown'}
                     Status = if($_.Status){[string]$_.Status}else{'Unknown'}
                     PathName = ''
                     Description = ''
                     Summary = 'No description provided by this service.'
                   }
                 })
               }
               @($rows) | ConvertTo-Json -Depth 4 -Compress
               """;
    }

    private static string BuildSnapshotScript(bool includeDetails)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$board = $null; try { $board = (Get-CimInstance Win32_BaseBoard -ErrorAction Stop | Select-Object -First 1) } catch {}");
        sb.AppendLine("$gpu = $null; try { $gpu = (Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object -First 1) } catch {}");
        sb.AppendLine("$os = $null; try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch {}");
        sb.AppendLine("$cpu = $null; try { $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1 } catch {}");
        sb.AppendLine("$memGb = if($os -and $os.TotalVisibleMemorySize){ [math]::Round(($os.TotalVisibleMemorySize * 1KB)/1GB, 2) } else { 0 }");
        sb.AppendLine("$disks = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Select-Object Name, Free, Used)");
        sb.AppendLine("$totalUsed = ($disks | Measure-Object -Property Used -Sum).Sum; if($null -eq $totalUsed){ $totalUsed = 0 }");
        sb.AppendLine("$totalFree = ($disks | Measure-Object -Property Free -Sum).Sum; if($null -eq $totalFree){ $totalFree = 0 }");
        sb.AppendLine("$total = $totalUsed + $totalFree");
        sb.AppendLine("$free = $totalFree");
        sb.AppendLine("function Format-Size([double]$bytes) { if ($bytes -le 0) { return 'Unknown' }; $units = @('B','KB','MB','GB','TB'); $i = 0; while ($bytes -ge 1024 -and $i -lt ($units.Length - 1)) { $bytes = $bytes / 1024; $i++ }; return ('{0:N2} {1}' -f $bytes, $units[$i]) }");

        if (includeDetails)
        {
            sb.AppendLine("$apps = @(Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction Continue; Get-ItemProperty HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction Continue) | Where-Object { $_.DisplayName } | ForEach-Object {");
            sb.AppendLine("  $installDate = 'Unknown'");
            sb.AppendLine("  if ($_.InstallDate -and $_.InstallDate -match '^\\d{8}$') {");
            sb.AppendLine("    try { $installDate = [datetime]::ParseExact($_.InstallDate, 'yyyyMMdd', $null).ToString('yyyy-MM-dd') } catch { $installDate = 'Unknown' }");
            sb.AppendLine("  }");
            sb.AppendLine("  $sizeBytes = 0");
            sb.AppendLine("  if ($_.EstimatedSize -as [double]) { $sizeBytes = [double]$_.EstimatedSize * 1KB }");
            sb.AppendLine("  $uninstall = if ($_.QuietUninstallString) { $_.QuietUninstallString } else { $_.UninstallString }");
            sb.AppendLine("  [PSCustomObject]@{");
            sb.AppendLine("    DisplayName = $_.DisplayName");
            sb.AppendLine("    DisplayVersion = $_.DisplayVersion");
            sb.AppendLine("    Publisher = $_.Publisher");
            sb.AppendLine("    InstallDate = $installDate");
            sb.AppendLine("    SizeOnDisk = (Format-Size $sizeBytes)");
            sb.AppendLine("    InstallLocation = $_.InstallLocation");
            sb.AppendLine("    UninstallCommand = $uninstall");
            sb.AppendLine("    DisplayIcon = $_.DisplayIcon");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("$procs = Get-Process | Sort-Object CPU -Descending | Select-Object -First 100 Name, Id, CPU, @{Name='MemoryMb';Expression={[math]::Round($_.WorkingSet64/1MB,2)}}");
            sb.AppendLine("$services = Get-CimInstance Win32_Service | Select-Object Name, DisplayName, StartMode, State, PathName, Description");
            sb.AppendLine("$appsCount = $apps.Count");
            sb.AppendLine("$processesCount = (Get-Process).Count");
            sb.AppendLine("$servicesCount = $services.Count");
        }
        else
        {
            sb.AppendLine("$appsCount = 0; try { $appsCount = (@((Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue); (Get-ItemProperty HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue); (Get-ItemProperty HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue)) | Where-Object { $_.DisplayName } | Measure-Object).Count } catch {}");
            sb.AppendLine("$processesCount = 0; try { $processesCount = (Get-Process -ErrorAction Stop).Count } catch {}");
            sb.AppendLine("$servicesCount = 0; try { $servicesCount = (Get-CimInstance Win32_Service -ErrorAction Stop | Measure-Object).Count } catch { try { $servicesCount = (Get-Service -ErrorAction Stop | Measure-Object).Count } catch {} }");
        }

        sb.AppendLine("$cpuCtr = 0");
        sb.AppendLine("try { $cpuSample = (Get-Counter '\\Processor(_Total)\\% Processor Time' -ErrorAction Stop).CounterSamples | Select-Object -First 1; if($cpuSample){ $cpuCtr = $cpuSample.CookedValue } } catch {}");
        sb.AppendLine("function Get-GpuCounterAverage([string]$counterPath) {");
        sb.AppendLine("  try {");
        sb.AppendLine("    $null = Get-Counter $counterPath -ErrorAction Stop");
        sb.AppendLine("    Start-Sleep -Milliseconds 120");
        sb.AppendLine("    $gpuCounters = Get-Counter $counterPath -ErrorAction Stop");
        sb.AppendLine("    $samples = @($gpuCounters.CounterSamples | Where-Object { $_.CookedValue -ge 0 } | Select-Object -ExpandProperty CookedValue)");
        sb.AppendLine("    if ($samples.Count -gt 0) {");
        sb.AppendLine("      return ($samples | Measure-Object -Average).Average");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  return $null");
        sb.AppendLine("}");
        sb.AppendLine("$gpuCtr = Get-GpuCounterAverage '\\GPU Engine(_Total)\\Utilization Percentage'");
        sb.AppendLine("if ($null -eq $gpuCtr) { $gpuCtr = Get-GpuCounterAverage '\\GPU Engine(*)\\Utilization Percentage' }");
        sb.AppendLine("if ($null -eq $gpuCtr) {");
        sb.AppendLine("  try {");
        sb.AppendLine("    $engineSamples = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction Stop | Where-Object { $_.Name -notlike '*_Total*' -and $_.UtilizationPercentage -ne $null } | Select-Object -ExpandProperty UtilizationPercentage)");
        sb.AppendLine("    if ($engineSamples.Count -gt 0) { $gpuCtr = ($engineSamples | Measure-Object -Average).Average }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("}");
        sb.AppendLine("if ($null -eq $gpuCtr) { $gpuCtr = 0 }");
        sb.AppendLine("$memoryPct = if($os -and $os.TotalVisibleMemorySize -gt 0){ [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100, 2) } else { 0 }");
        sb.AppendLine("$uptime = if($os -and $os.LastBootUpTime){ (Get-Date) - $os.LastBootUpTime } else { [TimeSpan]::Zero }");
        sb.AppendLine("$obj = [PSCustomObject]@{");
        sb.AppendLine(" Motherboard = if($board){$board.Product}else{'Unknown'}");
        sb.AppendLine(" Graphics = if($gpu){$gpu.Name}else{'Unknown'}");
        sb.AppendLine(" GraphicsDriverVersion = if($gpu){$gpu.DriverVersion}else{'Unknown'}");
        sb.AppendLine(" GraphicsDriverDate = if($gpu){$gpu.DriverDate}else{'Unknown'}");
        sb.AppendLine(" Storage = if($total -gt 0){ ('Total ' + [math]::Round($total/1GB,2) + ' GB, Free ' + [math]::Round($free/1GB,2) + ' GB') } else { 'Unknown' }");
        sb.AppendLine(" Uptime = ([string]::Format('{0:00}:{1:00}:{2:00}', [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds))");
        sb.AppendLine(" Processor = if($cpu){$cpu.Name + ' (' + $cpu.NumberOfCores + 'C/' + $cpu.NumberOfLogicalProcessors + 'T)'}else{'Unknown'}");
        sb.AppendLine(" Memory = ($memGb.ToString() + ' GB')");
        sb.AppendLine(" Windows = if($os){ ($os.Caption + ' ' + $os.Version + ' (Build ' + $os.BuildNumber + ')') } else { 'Unknown' }");
        sb.AppendLine(" AppsCount = $appsCount");
        sb.AppendLine(" ProcessesCount = $processesCount");
        sb.AppendLine(" ServicesCount = $servicesCount");
        sb.AppendLine(" CpuUsage = [math]::Round($cpuCtr,2)");
        sb.AppendLine(" GpuUsage = [math]::Round([math]::Min([math]::Max($gpuCtr, 0), 100),2)");
        sb.AppendLine(" MemoryUsage = $memoryPct");
        if (includeDetails)
        {
            sb.AppendLine(" Apps = @($apps | ForEach-Object { [PSCustomObject]@{ Name = $_.DisplayName; Version = $_.DisplayVersion; Publisher = $_.Publisher; InstallDate = if($_.InstallDate){$_.InstallDate}else{'Unknown'}; SizeOnDisk = if($_.SizeOnDisk){$_.SizeOnDisk}else{'Unknown'}; InstallLocation = if($_.InstallLocation){$_.InstallLocation}else{''}; UninstallCommand = if($_.UninstallCommand){$_.UninstallCommand}else{''}; DisplayIcon = if($_.DisplayIcon){$_.DisplayIcon}else{''} } })");
            sb.AppendLine(" Processes = @($procs | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Id = $_.Id; Cpu = $_.CPU; MemoryMb = $_.MemoryMb } })");
            sb.AppendLine(" Services = @($services | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; DisplayName = $_.DisplayName; StartupType = $_.StartMode; Status = $_.State; PathName = if($_.PathName){$_.PathName}else{''}; Description = if($_.Description){$_.Description}else{''}; Summary = if([string]::IsNullOrWhiteSpace($_.Description)){'No description provided by this service.'}else{$_.Description} } })");
        }
        else
        {
            sb.AppendLine(" Apps = @()");
            sb.AppendLine(" Processes = @()");
            sb.AppendLine(" Services = @()");
        }
        sb.AppendLine("}");
        sb.AppendLine("$obj | ConvertTo-Json -Depth 5 -Compress");
        return sb.ToString();
    }
}
