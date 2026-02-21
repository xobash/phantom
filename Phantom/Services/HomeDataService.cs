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
    private DateTimeOffset _lastNetworkSampleAt = DateTimeOffset.UtcNow;
    private long _lastNetworkSentBytes;
    private long _lastNetworkReceivedBytes;

    public HomeDataService(ConsoleStreamService console, TelemetryStore telemetryStore)
    {
        _console = console;
        _telemetryStore = telemetryStore;
    }

    public async Task<HomeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
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
            return new HomeSnapshot();
        }

        var snapshot = JsonSerializer.Deserialize<HomeSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new HomeSnapshot();

        var network = ComputeNetworkSnapshot(_telemetry);
        snapshot.NetworkUsage = BuildNetworkDisplay(network.UploadSpeed, network.DownloadSpeed, network.TotalTransferred);
        await _telemetryStore.SaveAsync(_telemetry, cancellationToken).ConfigureAwait(false);

        return snapshot;
    }

    public async Task<(double CpuUsage, double MemoryUsage, double GpuUsage, string Uptime, string NetworkUsage)> GetLiveMetricsAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return (0, 0, 0, "00:00:00", "↑ 0 B/s ↓ 0 B/s\n0 B");
        }

        const string script = @"
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples.CookedValue
$gpu = 0
try {
  $gpuSamples = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction Stop).CounterSamples
  if ($gpuSamples) {
    $gpu = ($gpuSamples | Measure-Object -Property CookedValue -Average).Average
  }
} catch {
  try {
    $fallback = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples
    if ($fallback) {
      $gpu = [math]::Min(100, ($fallback | Measure-Object -Property CookedValue -Sum).Sum)
    }
  } catch {
    $gpu = 0
  }
}
$memoryPct = (($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100
$uptime = (Get-Date) - $os.LastBootUpTime
[PSCustomObject]@{
  CpuUsage = [math]::Round($cpu, 2)
  MemoryUsage = [math]::Round($memoryPct, 2)
  GpuUsage = [math]::Round($gpu, 2)
  Uptime = ([string]::Format('{0:00}:{1:00}:{2:00}', [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds))
} | ConvertTo-Json -Compress";

        var json = await RunPowerShellForJsonAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, 0, 0, "00:00:00", BuildNetworkDisplay(0, 0, 0));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var cpu = doc.RootElement.GetProperty("CpuUsage").GetDouble();
            var memory = doc.RootElement.GetProperty("MemoryUsage").GetDouble();
            var gpu = doc.RootElement.TryGetProperty("GpuUsage", out var gpuProperty) ? gpuProperty.GetDouble() : 0;
            var uptime = doc.RootElement.TryGetProperty("Uptime", out var uptimeProperty) ? uptimeProperty.GetString() ?? "00:00:00" : "00:00:00";
            var network = ComputeNetworkSnapshot(_telemetry ?? new TelemetryState());
            return (cpu, memory, gpu, uptime, BuildNetworkDisplay(network.UploadSpeed, network.DownloadSpeed, network.TotalTransferred));
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Live metrics parse failed: {ex.Message}");
            return (0, 0, 0, "00:00:00", BuildNetworkDisplay(0, 0, 0));
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
        var wrapped = $"$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';& {{ {script} }}";
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

        return process.ExitCode == 0 ? stdout.Trim() : string.Empty;
    }

    private (double UploadSpeed, double DownloadSpeed, long TotalTransferred) ComputeNetworkSnapshot(TelemetryState telemetry)
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

        long baselineSent = telemetry.NetworkBaselines.Values.Sum(x => x.SentBytes);
        long baselineRecv = telemetry.NetworkBaselines.Values.Sum(x => x.ReceivedBytes);

        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Max(0.25, (now - _lastNetworkSampleAt).TotalSeconds);
        var uploadSpeed = _lastNetworkSentBytes == 0 ? 0 : Math.Max(0, (totalSent - _lastNetworkSentBytes) / elapsed);
        var downloadSpeed = _lastNetworkReceivedBytes == 0 ? 0 : Math.Max(0, (totalRecv - _lastNetworkReceivedBytes) / elapsed);

        _lastNetworkSampleAt = now;
        _lastNetworkSentBytes = totalSent;
        _lastNetworkReceivedBytes = totalRecv;

        var totalTransferred = Math.Max(0, (totalSent - baselineSent) + (totalRecv - baselineRecv));
        return (uploadSpeed, downloadSpeed, totalTransferred);
    }

    private static string BuildNetworkDisplay(double uploadBytesPerSecond, double downloadBytesPerSecond, long totalTransferred)
        => $"↑ {FormatBytes((long)uploadBytesPerSecond)}/s ↓ {FormatBytes((long)downloadBytesPerSecond)}/s\n{FormatBytes(totalTransferred)}";

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
        sb.AppendLine("$apps = @(Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue; Get-ItemProperty HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue) | Where-Object { $_.DisplayName } | Select-Object DisplayName, DisplayVersion, Publisher");
        sb.AppendLine("$procs = Get-Process | Sort-Object CPU -Descending | Select-Object -First 100 Name, Id, CPU, @{Name='MemoryMb';Expression={[math]::Round($_.WorkingSet64/1MB,2)}}");
        sb.AppendLine("$services = Get-CimInstance Win32_Service | Where-Object {$_.State -eq 'Running'} | Select-Object Name, DisplayName, StartMode, State");
        sb.AppendLine("$cpuCtr = (Get-Counter '\\Processor(_Total)\\% Processor Time').CounterSamples.CookedValue");
        sb.AppendLine("$gpuCtr = 0");
        sb.AppendLine("try { $gpuSamples = (Get-Counter '\\GPU Engine(*engtype_3D)\\Utilization Percentage' -ErrorAction Stop).CounterSamples; if ($gpuSamples) { $gpuCtr = ($gpuSamples | Measure-Object -Property CookedValue -Average).Average } } catch { try { $fallback = (Get-Counter '\\GPU Engine(*)\\Utilization Percentage' -ErrorAction Stop).CounterSamples; if ($fallback) { $gpuCtr = [math]::Min(100, ($fallback | Measure-Object -Property CookedValue -Sum).Sum) } } catch { $gpuCtr = 0 } }");
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
        sb.AppendLine(" GpuUsage = [math]::Round($gpuCtr,2)");
        sb.AppendLine(" MemoryUsage = $memoryPct");
        sb.AppendLine(" Apps = @($apps | ForEach-Object { [PSCustomObject]@{ Name = $_.DisplayName; Version = $_.DisplayVersion; Publisher = $_.Publisher } })");
        sb.AppendLine(" Processes = @($procs | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Id = $_.Id; Cpu = $_.CPU; MemoryMb = $_.MemoryMb } })");
        sb.AppendLine(" Services = @($services | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; DisplayName = $_.DisplayName; StartupType = $_.StartMode; Status = $_.State } })");
        sb.AppendLine("}");
        sb.AppendLine("$obj | ConvertTo-Json -Depth 5 -Compress");
        return sb.ToString();
    }
}
