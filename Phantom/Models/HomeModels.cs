namespace Phantom.Models;

public sealed class HomeCard
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Tooltip { get; set; }
}

public sealed class KpiTile
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SecondaryValue { get; set; } = string.Empty;
}

public sealed class InstalledAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallDate { get; set; } = "Unknown";
    public string SizeOnDisk { get; set; } = "Unknown";
    public string InstallLocation { get; set; } = string.Empty;
    public string UninstallCommand { get; set; } = string.Empty;
    public string DisplayIcon { get; set; } = string.Empty;
}

public sealed class ProcessInfoRow
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public double Cpu { get; set; }
    public double MemoryMb { get; set; }
}

public sealed class ServiceInfoRow
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string StartupType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PathName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class HomeSnapshot
{
    public string Motherboard { get; set; } = "Unknown";
    public string Graphics { get; set; } = "Unknown";
    public string GraphicsDriverVersion { get; set; } = "Unknown";
    public string GraphicsDriverDate { get; set; } = "Unknown";
    public string Storage { get; set; } = "Unknown";
    public string Uptime { get; set; } = "Unknown";
    public string Processor { get; set; } = "Unknown";
    public string Memory { get; set; } = "Unknown";
    public string Windows { get; set; } = "Unknown";
    public string PerformanceScore { get; set; } = "Unavailable";

    public int AppsCount { get; set; }
    public int ProcessesCount { get; set; }
    public int ServicesCount { get; set; }

    public double CpuUsage { get; set; }
    public double GpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public string NetworkUsage { get; set; } = "0 B/s";

    public List<InstalledAppInfo> Apps { get; set; } = new();
    public List<ProcessInfoRow> Processes { get; set; } = new();
    public List<ServiceInfoRow> Services { get; set; } = new();
}
