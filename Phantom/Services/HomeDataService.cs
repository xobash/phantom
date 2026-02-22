using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Phantom.Models;

namespace Phantom.Services;

public sealed class HomeDataService
{
    private readonly ConsoleStreamService _console;
    private readonly TelemetryStore _telemetryStore;
    private TelemetryState? _telemetry;

    public HomeDataService(ConsoleStreamService console, TelemetryStore telemetryStore)
    {
        _console = console;
        _telemetryStore = telemetryStore;
    }

    public async Task<HomeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        _console.Publish("Trace", "HomeDataService.GetSnapshotAsync started.");
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

        var json = await RunPowerShellForJsonAsync(BuildSnapshotScript(), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _console.Publish("Warning", "HomeDataService.GetSnapshotAsync returned empty JSON.");
            return new HomeSnapshot();
        }

        var snapshot = JsonSerializer.Deserialize<HomeSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new HomeSnapshot();

        snapshot.NetworkUsage = ComputeNetworkUsage(_telemetry);
        await _telemetryStore.SaveAsync(_telemetry, cancellationToken).ConfigureAwait(false);
        _console.Publish("Trace", $"HomeDataService.GetSnapshotAsync completed. apps={snapshot.AppsCount}, processes={snapshot.ProcessesCount}, services={snapshot.ServicesCount}");

        return snapshot;
    }

    public async Task<(double CpuUsage, double MemoryUsage, string Uptime)> GetFastMetricsAsync(CancellationToken cancellationToken)
    {
        _console.Publish("Trace", "HomeDataService.GetFastMetricsAsync started.");
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            _console.Publish("Warning", "HomeDataService.GetFastMetricsAsync unavailable on non-Windows host.");
            return (0, 0, "Unavailable");
        }

        const string script = @"
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples.CookedValue
$memoryPct = (($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100
$uptime = (Get-Date) - $os.LastBootUpTime
[PSCustomObject]@{
  CpuUsage = [math]::Round($cpu, 2)
  MemoryUsage = [math]::Round($memoryPct, 2)
  Uptime = [string]::Format('{0:00}:{1:00}:{2:00}', [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds)
} | ConvertTo-Json -Compress";

        var json = await RunPowerShellForJsonAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _console.Publish("Warning", "HomeDataService.GetFastMetricsAsync returned empty JSON.");
            return (0, 0, "Unavailable");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var cpu = doc.RootElement.GetProperty("CpuUsage").GetDouble();
            var memory = doc.RootElement.GetProperty("MemoryUsage").GetDouble();
            var uptime = doc.RootElement.GetProperty("Uptime").GetString() ?? "Unavailable";
            _console.Publish("Trace", $"HomeDataService.GetFastMetricsAsync completed. cpu={cpu:F2}, memory={memory:F2}, uptime={uptime}");
            return (cpu, memory, uptime);
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics parse failed: {ex.Message}");
            return (0, 0, "Unavailable");
        }
    }

    public async Task<(double CpuUsage, double MemoryUsage, double GpuUsage, long UptimeSeconds, string NetworkUsage)> GetLiveMetricsAsync(CancellationToken cancellationToken)
    {
        _console.Publish("Trace", "HomeDataService.GetLiveMetricsAsync started.");
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            _console.Publish("Warning", "HomeDataService.GetLiveMetricsAsync unavailable on non-Windows host.");
            return (0, 0, 0, 0, "↑ 0.00 B/s ↓ 0.00 B/s");
        }

        _telemetry ??= await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        const string script = @"
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples.CookedValue
$memoryPct = (($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100
$uptime = [long]((Get-Date) - $os.LastBootUpTime).TotalSeconds
$gpuCtr = 0
try {
  $gpuCounters = Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction Stop
  $samples = @($gpuCounters.CounterSamples | Select-Object -ExpandProperty CookedValue)
  if ($samples.Count -gt 0) {
    $gpuCtr = ($samples | Measure-Object -Average).Average
  }
} catch {
  $gpuCtr = 0
}
[PSCustomObject]@{
  CpuUsage = [math]::Round($cpu, 2)
  MemoryUsage = [math]::Round($memoryPct, 2)
  GpuUsage = [math]::Round([math]::Min([math]::Max($gpuCtr, 0), 100), 2)
  UptimeSeconds = [math]::Max($uptime, 0)
} | ConvertTo-Json -Compress";

        var json = await RunPowerShellForJsonAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _console.Publish("Warning", "HomeDataService.GetLiveMetricsAsync returned empty JSON.");
            return (0, 0, 0, 0, ComputeNetworkUsage(_telemetry));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var cpu = doc.RootElement.GetProperty("CpuUsage").GetDouble();
            var memory = doc.RootElement.GetProperty("MemoryUsage").GetDouble();
            var gpu = doc.RootElement.GetProperty("GpuUsage").GetDouble();
            var uptimeSeconds = doc.RootElement.GetProperty("UptimeSeconds").GetInt64();
            var network = ComputeNetworkUsage(_telemetry);
            _console.Publish("Trace", $"HomeDataService.GetLiveMetricsAsync completed. cpu={cpu:F2}, memory={memory:F2}, gpu={gpu:F2}, uptimeSeconds={uptimeSeconds}, network={network}");
            return (cpu, memory, gpu, uptimeSeconds, network);
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics parse failed: {ex.Message}");
            return (0, 0, 0, 0, ComputeNetworkUsage(_telemetry));
        }
    }

    public async Task<string> RunWinsatScoreAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return "Unavailable";
        }

        var command = @"
$ErrorActionPreference = 'Stop'
try {
  Start-Process -FilePath 'winsat.exe' -ArgumentList 'formal' -Wait -NoNewWindow | Out-Null
  $f = Get-ChildItem ""$env:WinDir\Performance\WinSAT\DataStore\*Formal*.WinSAT.xml"" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $f) { throw 'WinSAT XML not found.' }
  [xml]$x = Get-Content $f.FullName
  $score = $x.WinSAT.WinSPR.SystemScore
  if (-not $score) { throw 'SystemScore not found.' }
  [PSCustomObject]@{ Score = $score } | ConvertTo-Json -Compress
}
catch {
  Write-Error $_
  exit 1
}";

        var json = await RunPowerShellForJsonAsync(command, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return "Unavailable";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("Score").GetString() ?? "Unavailable";
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"WinSAT parse failed: {ex.Message}");
            return "Unavailable";
        }
    }

    private async Task<string> RunPowerShellForJsonAsync(string script, CancellationToken cancellationToken)
    {
        _console.Publish("Trace", $"HomeDataService.RunPowerShellForJsonAsync start. scriptLength={script.Length}");
        var wrapped = $"$ProgressPreference='Continue';$ErrorActionPreference='Stop';& {{ {script} }}";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{wrapped.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            _console.Publish("Error", "HomeDataService.RunPowerShellForJsonAsync failed to start powershell.exe.");
            return string.Empty;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _console.Publish("Error", stderr.Trim());
        }

        _console.Publish("Trace", $"HomeDataService.RunPowerShellForJsonAsync exit={process.ExitCode}, stdoutChars={stdout.Length}, stderrChars={stderr.Length}");

        return process.ExitCode == 0 ? stdout.Trim() : string.Empty;
    }

    private static string ComputeNetworkUsage(TelemetryState telemetry)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .Where(x => x.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .ToList();

        long totalSent = 0;
        long totalRecv = 0;

        foreach (var nic in nics)
        {
            var stats = nic.GetIPv4Statistics();
            totalSent += stats.BytesSent;
            totalRecv += stats.BytesReceived;

            if (!telemetry.NetworkBaselines.TryGetValue(nic.Id, out var baseline))
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

        return $"↑ {FormatBytes((long)uploadRate)}/s ↓ {FormatBytes((long)downloadRate)}/s|{FormatBytes(deltaSent + deltaRecv)}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value > 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2} {units[unit]}";
    }

    private static string BuildSnapshotScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$board = (Get-CimInstance Win32_BaseBoard | Select-Object -First 1)");
        sb.AppendLine("$gpu = (Get-CimInstance Win32_VideoController | Select-Object -First 1)");
        sb.AppendLine("$os = Get-CimInstance Win32_OperatingSystem");
        sb.AppendLine("$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1");
        sb.AppendLine("$memGb = [math]::Round(($os.TotalVisibleMemorySize * 1KB)/1GB, 2)");
        sb.AppendLine("$disks = Get-PSDrive -PSProvider FileSystem | Select-Object Name, Free, Used");
        sb.AppendLine("$total = ($disks | Measure-Object -Property Used -Sum).Sum + ($disks | Measure-Object -Property Free -Sum).Sum");
        sb.AppendLine("$free = ($disks | Measure-Object -Property Free -Sum).Sum");
        sb.AppendLine("function Format-Size([double]$bytes) { if ($bytes -le 0) { return 'Unknown' }; $units = @('B','KB','MB','GB','TB'); $i = 0; while ($bytes -ge 1024 -and $i -lt ($units.Length - 1)) { $bytes = $bytes / 1024; $i++ }; return ('{0:N2} {1}' -f $bytes, $units[$i]) }");
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
        sb.AppendLine("$cpuCtr = (Get-Counter '\\Processor(_Total)\\% Processor Time').CounterSamples.CookedValue");
        sb.AppendLine("$gpuCtr = 0");
        sb.AppendLine("try {");
        sb.AppendLine("  $gpuCounters = Get-Counter '\\GPU Engine(*engtype_3D)\\Utilization Percentage' -ErrorAction Stop");
        sb.AppendLine("  $samples = @($gpuCounters.CounterSamples | Select-Object -ExpandProperty CookedValue)");
        sb.AppendLine("  if ($samples.Count -gt 0) {");
        sb.AppendLine("    $gpuCtr = ($samples | Measure-Object -Average).Average");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { $gpuCtr = 0 }");
        sb.AppendLine("$memoryPct = [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100, 2)");
        sb.AppendLine("$uptime = (Get-Date) - $os.LastBootUpTime");
        sb.AppendLine("$obj = [PSCustomObject]@{");
        sb.AppendLine(" Motherboard = if($board){$board.Product}else{'Unknown'}");
        sb.AppendLine(" Graphics = if($gpu){$gpu.Name}else{'Unknown'}");
        sb.AppendLine(" GraphicsDriverVersion = if($gpu){$gpu.DriverVersion}else{'Unknown'}");
        sb.AppendLine(" GraphicsDriverDate = if($gpu){$gpu.DriverDate}else{'Unknown'}");
        sb.AppendLine(" Storage = ('Total ' + [math]::Round($total/1GB,2) + ' GB, Free ' + [math]::Round($free/1GB,2) + ' GB')");
        sb.AppendLine(" Uptime = ([string]::Format('{0:00}:{1:00}:{2:00}', [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds))");
        sb.AppendLine(" Processor = if($cpu){$cpu.Name + ' (' + $cpu.NumberOfCores + 'C/' + $cpu.NumberOfLogicalProcessors + 'T)'}else{'Unknown'}");
        sb.AppendLine(" Memory = ($memGb.ToString() + ' GB')");
        sb.AppendLine(" Windows = ($os.Caption + ' ' + $os.Version + ' (Build ' + $os.BuildNumber + ')')");
        sb.AppendLine(" AppsCount = $apps.Count");
        sb.AppendLine(" ProcessesCount = (Get-Process).Count");
        sb.AppendLine(" ServicesCount = $services.Count");
        sb.AppendLine(" CpuUsage = [math]::Round($cpuCtr,2)");
        sb.AppendLine(" GpuUsage = [math]::Round([math]::Min([math]::Max($gpuCtr, 0), 100),2)");
        sb.AppendLine(" MemoryUsage = $memoryPct");
        sb.AppendLine(" Apps = @($apps | ForEach-Object { [PSCustomObject]@{ Name = $_.DisplayName; Version = $_.DisplayVersion; Publisher = $_.Publisher; InstallDate = if($_.InstallDate){$_.InstallDate}else{'Unknown'}; SizeOnDisk = if($_.SizeOnDisk){$_.SizeOnDisk}else{'Unknown'}; InstallLocation = if($_.InstallLocation){$_.InstallLocation}else{''}; UninstallCommand = if($_.UninstallCommand){$_.UninstallCommand}else{''}; DisplayIcon = if($_.DisplayIcon){$_.DisplayIcon}else{''} } })");
        sb.AppendLine(" Processes = @($procs | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Id = $_.Id; Cpu = $_.CPU; MemoryMb = $_.MemoryMb } })");
        sb.AppendLine(" Services = @($services | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; DisplayName = $_.DisplayName; StartupType = $_.StartMode; Status = $_.State; PathName = if($_.PathName){$_.PathName}else{''}; Description = if($_.Description){$_.Description}else{''}; Summary = if([string]::IsNullOrWhiteSpace($_.Description)){'No description provided by this service.'}else{$_.Description} } })");
        sb.AppendLine("}");
        sb.AppendLine("$obj | ConvertTo-Json -Depth 5 -Compress");
        return sb.ToString();
    }
}
