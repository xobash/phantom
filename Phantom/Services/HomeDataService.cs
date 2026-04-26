using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Phantom.Models;

namespace Phantom.Services;

public sealed class HomeDataService
{
    private const string WinsatTooltipText = "Bare-metal benchmark uses Windows System Assessment Tool (WinSAT). Base score is the lowest subscore, max 9.9.";
    private const string VmBenchmarkTooltipText = "Virtual machine benchmark uses Phantom's in-process CPU, memory, and disk sample because WinSAT is blocked by many VM platforms.";
    private const int DirectorySizeFileLimit = 5000;
    private static readonly TimeSpan DirectorySizeTimeLimit = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan BenchmarkRuntimeLimit = TimeSpan.FromSeconds(60);

    private readonly ConsoleStreamService _console;
    private readonly TelemetryStore _telemetryStore;
    private readonly object _cpuSampleSync = new();
    private TelemetryState? _telemetry;
    private CpuSample? _lastCpuSample;

    public HomeDataService(ConsoleStreamService console, TelemetryStore telemetryStore)
    {
        _console = console;
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

        var snapshot = await Task.Run(() => BuildCompiledSnapshot(countInventory: !includeDetails), cancellationToken).ConfigureAwait(false);
        if (includeDetails)
        {
            var appsTask = GetInstalledAppsAsync(cancellationToken);
            var servicesTask = GetServicesAsync(cancellationToken);
            await Task.WhenAll(appsTask, servicesTask).ConfigureAwait(false);

            snapshot.Apps = (await appsTask.ConfigureAwait(false)).ToList();
            snapshot.Services = (await servicesTask.ConfigureAwait(false)).ToList();
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

    public Task<IReadOnlyList<ServiceInfoRow>> GetServicesAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return Task.FromResult<IReadOnlyList<ServiceInfoRow>>(Array.Empty<ServiceInfoRow>());
        }

        return Task.Run<IReadOnlyList<ServiceInfoRow>>(EnumerateServices, cancellationToken);
    }

    public async Task<(double CpuUsage, double MemoryUsage, double GpuUsage, string NetworkUsage)> GetLiveMetricsAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return (0, 0, 0, "↑ 0 B/s  ↓ 0 B/s  • 0 B");
        }

        _telemetry ??= await _telemetryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var cpu = GetCpuUsagePercent();
        var memory = GetMemoryUsagePercent();
        var network = ComputeNetworkUsage(_telemetry);
        return (cpu, memory, 0, network);
    }

    public async Task<string> RunWinsatScoreAsync(CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return "Unavailable";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BenchmarkRuntimeLimit);
        var benchmarkToken = timeout.Token;
        try
        {
            if (IsVirtualMachine())
            {
                return await RunVmBenchmarkScoreAsync(benchmarkToken).ConfigureAwait(false);
            }

            if (!File.Exists(GetWinsatPath()))
            {
                _console.Publish("Warning", "WinSAT is not available on this system.");
                return "Unavailable";
            }

            var ranFormal = await RunWinsatFormalAsync(benchmarkToken).ConfigureAwait(false);
            if (!ranFormal)
            {
                return "Unavailable";
            }

            return await ReadLatestWinsatScoreAsync(benchmarkToken).ConfigureAwait(false) ?? "Unavailable";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _console.Publish("Warning", "Performance benchmark stopped after the 60 second runtime limit.");
            return "Unavailable";
        }
    }

    private async Task<string?> ReadLatestWinsatScoreAsync(CancellationToken cancellationToken)
    {
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
            return string.IsNullOrWhiteSpace(score) ? null : score;
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"WinSAT parse failed: {ex.Message}");
            return null;
        }
    }

    private HomeSnapshot BuildCompiledSnapshot(bool countInventory)
    {
        var performance = BuildPerformanceSummary();
        var appsCount = 0;
        var servicesCount = 0;
        if (countInventory)
        {
            try
            {
                appsCount = CountInstalledApps();
            }
            catch
            {
                appsCount = 0;
            }

            try
            {
                servicesCount = CountServices();
            }
            catch
            {
                servicesCount = 0;
            }
        }

        return new HomeSnapshot
        {
            Motherboard = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct", string.Empty),
            Graphics = string.Empty,
            GraphicsDriverVersion = string.Empty,
            GraphicsDriverDate = string.Empty,
            Storage = BuildStorageSummary(),
            Uptime = FormatUptime(TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64))),
            Processor = BuildProcessorSummary(),
            Memory = BuildMemorySummary(),
            Windows = BuildWindowsSummary(),
            PerformanceScore = performance.DisplayText,
            PerformanceTooltip = performance.Tooltip,
            IsPerformanceAvailable = performance.IsAvailable,
            AppsCount = appsCount,
            ProcessesCount = SafeProcessCount(),
            ServicesCount = servicesCount,
            CpuUsage = GetCpuUsagePercent(),
            GpuUsage = 0,
            MemoryUsage = GetMemoryUsagePercent(),
            Apps = [],
            Services = []
        };
    }

    private HomePerformanceInfo BuildPerformanceSummary()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return new HomePerformanceInfo("Unavailable", "Performance benchmark is available only on Windows.", IsAvailable: false);
        }

        if (IsVirtualMachine())
        {
            return new HomePerformanceInfo("VM benchmark ready", VmBenchmarkTooltipText, IsAvailable: true);
        }

        if (!File.Exists(GetWinsatPath()))
        {
            return new HomePerformanceInfo("Unavailable", "WinSAT is not installed or is unavailable on this Windows image.", IsAvailable: false);
        }

        try
        {
            var dataStorePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Performance",
                "WinSAT",
                "DataStore");
            if (Directory.Exists(dataStorePath))
            {
                var latestXml = Directory
                    .EnumerateFiles(dataStorePath, "*Formal*.WinSAT.xml", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latestXml is not null)
                {
                    var document = XDocument.Load(latestXml.FullName);
                    var score = document
                        .Descendants()
                        .FirstOrDefault(element => element.Name.LocalName.Equals("SystemScore", StringComparison.OrdinalIgnoreCase))
                        ?.Value
                        ?.Trim();
                    if (!string.IsNullOrWhiteSpace(score))
                    {
                        return new HomePerformanceInfo(score, WinsatTooltipText, IsAvailable: true);
                    }
                }
            }
        }
        catch
        {
            // Keep the home snapshot fast. Manual benchmark execution reports detailed failures.
        }

        return new HomePerformanceInfo("Run benchmark", WinsatTooltipText, IsAvailable: true);
    }

    private static string BuildStorageSummary()
    {
        long total = 0;
        long free = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                total += drive.TotalSize;
                free += drive.AvailableFreeSpace;
            }
            catch
            {
            }
        }

        return total <= 0
            ? string.Empty
            : $"Total {Math.Round(total / 1024d / 1024d / 1024d, 2)} GB, Free {Math.Round(free / 1024d / 1024d / 1024d, 2)} GB";
    }

    private static string BuildProcessorSummary()
    {
        var name = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", string.Empty);
        return string.IsNullOrWhiteSpace(name)
            ? $"{Environment.ProcessorCount} logical processors"
            : $"{name.Trim()} ({Environment.ProcessorCount}T)";
    }

    private static string BuildMemorySummary()
    {
        return TryGetMemoryStatus(out var status)
            ? $"{Math.Round(status.ullTotalPhys / 1024d / 1024d / 1024d, 2)} GB"
            : string.Empty;
    }

    private static string BuildWindowsSummary()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, key, "ProductName", "Windows");
        var displayVersion = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, key, "DisplayVersion", string.Empty);
        var build = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, key, "CurrentBuildNumber", string.Empty);
        var ubr = ReadRegistryLong(RegistryHive.LocalMachine, RegistryView.Registry64, key, "UBR");
        var buildText = string.IsNullOrWhiteSpace(build)
            ? Environment.OSVersion.Version.ToString()
            : ubr > 0 ? $"{build}.{ubr}" : build;
        return string.IsNullOrWhiteSpace(displayVersion)
            ? $"{product} (Build {buildText})"
            : $"{product} {displayVersion} (Build {buildText})";
    }

    private static string GetWinsatPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winsat.exe");

    private static bool IsVirtualMachine()
    {
        var fields = new[]
        {
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", string.Empty),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", string.Empty),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVendor", string.Empty),
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"HARDWARE\DESCRIPTION\System", "SystemBiosVersion", string.Empty)
        };

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            if (field.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("virtualbox", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("hyper-v", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("qemu", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("kvm", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("parallels", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("xen", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int SafeProcessCount()
    {
        try
        {
            return Process.GetProcesses().Length;
        }
        catch
        {
            return 0;
        }
    }

    private double GetCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var sample = new CpuSample(ToLong(idleTime), ToLong(kernelTime), ToLong(userTime));
        lock (_cpuSampleSync)
        {
            if (_lastCpuSample is not { } previous)
            {
                _lastCpuSample = sample;
                return 0;
            }

            var idle = sample.Idle - previous.Idle;
            var kernel = sample.Kernel - previous.Kernel;
            var user = sample.User - previous.User;
            var total = kernel + user;
            _lastCpuSample = sample;
            if (total <= 0)
            {
                return 0;
            }

            return Math.Round(Math.Clamp((total - idle) * 100d / total, 0, 100), 2);
        }
    }

    private static double GetMemoryUsagePercent()
    {
        if (!TryGetMemoryStatus(out var status) || status.ullTotalPhys == 0)
        {
            return 0;
        }

        var used = status.ullTotalPhys - status.ullAvailPhys;
        return Math.Round(Math.Clamp(used * 100d / status.ullTotalPhys, 0, 100), 2);
    }

    private static IReadOnlyList<ServiceInfoRow> EnumerateServices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<ServiceInfoRow>();
        }

        var manager = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (manager == IntPtr.Zero)
        {
            return Array.Empty<ServiceInfoRow>();
        }

        try
        {
            var resume = 0;
            _ = EnumServicesStatusEx(
                manager,
                SC_ENUM_PROCESS_INFO,
                SERVICE_WIN32,
                SERVICE_STATE_ALL,
                IntPtr.Zero,
                0,
                out var bytesNeeded,
                out _,
                ref resume,
                null);

            if (bytesNeeded <= 0)
            {
                return Array.Empty<ServiceInfoRow>();
            }

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(
                        manager,
                        SC_ENUM_PROCESS_INFO,
                        SERVICE_WIN32,
                        SERVICE_STATE_ALL,
                        buffer,
                        bytesNeeded,
                        out _,
                        out var servicesReturned,
                        ref resume,
                        null))
                {
                    return Array.Empty<ServiceInfoRow>();
                }

                var rows = new List<ServiceInfoRow>(servicesReturned);
                var itemSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (var i = 0; i < servicesReturned; i++)
                {
                    var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * itemSize));
                    var registry = ReadServiceRegistry(item.ServiceName);
                    var displayName = ResolveIndirectString(registry.DisplayName);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = item.DisplayName;
                    }

                    var description = ResolveIndirectString(registry.Description);
                    var row = new ServiceInfoRow
                    {
                        Name = item.ServiceName,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? item.ServiceName : displayName,
                        StartupType = registry.StartupType,
                        Status = FormatServiceStatus(item.Status.CurrentState),
                        PathName = registry.PathName,
                        Description = description,
                    };
                    row.Summary = BuildServiceSummary(row);
                    rows.Add(row);
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static int CountServices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var manager = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (manager == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var resume = 0;
            _ = EnumServicesStatusEx(
                manager,
                SC_ENUM_PROCESS_INFO,
                SERVICE_WIN32,
                SERVICE_STATE_ALL,
                IntPtr.Zero,
                0,
                out var bytesNeeded,
                out _,
                ref resume,
                null);

            if (bytesNeeded <= 0)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                resume = 0;
                return EnumServicesStatusEx(
                    manager,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32,
                    SERVICE_STATE_ALL,
                    buffer,
                    bytesNeeded,
                    out _,
                    out var servicesReturned,
                    ref resume,
                    null)
                    ? servicesReturned
                    : 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static ServiceRegistryInfo ReadServiceRegistry(string serviceName)
    {
        const string root = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey($@"{root}\{serviceName}", writable: false);
            if (key is null)
            {
                return new ServiceRegistryInfo(string.Empty, string.Empty, string.Empty, string.Empty);
            }

            return new ServiceRegistryInfo(
                FormatStartupType(GetRegistryLong(key, "Start")),
                GetRegistryString(key, "ImagePath"),
                GetRegistryString(key, "Description"),
                GetRegistryString(key, "DisplayName"));
        }
        catch
        {
            return new ServiceRegistryInfo(string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private static string BuildServiceSummary(ServiceInfoRow service)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(service.Description))
        {
            parts.Add(service.Description);
        }

        parts.Add($"Service name: {service.Name}");
        if (!string.IsNullOrWhiteSpace(service.Status))
        {
            parts.Add($"Status: {service.Status}");
        }

        if (!string.IsNullOrWhiteSpace(service.StartupType))
        {
            parts.Add($"Startup: {service.StartupType}");
        }

        if (!string.IsNullOrWhiteSpace(service.PathName))
        {
            parts.Add($"Path: {service.PathName}");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string FormatStartupType(long start)
    {
        return start switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => string.Empty
        };
    }

    private static string FormatServiceStatus(uint state)
    {
        return state switch
        {
            1 => "Stopped",
            2 => "Start Pending",
            3 => "Stop Pending",
            4 => "Running",
            5 => "Continue Pending",
            6 => "Pause Pending",
            7 => "Paused",
            _ => string.Empty
        };
    }

    private static string ResolveIndirectString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith('@'))
        {
            return value;
        }

        var builder = new StringBuilder(2048);
        return SHLoadIndirectString(value, builder, (uint)builder.Capacity, IntPtr.Zero) == 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string ReadRegistryString(RegistryHive hive, RegistryView view, string subKey, string name, string fallback)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name)?.ToString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static long ReadRegistryLong(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key is null ? 0 : GetRegistryLong(key, name);
        }
        catch
        {
            return 0;
        }
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
                        EnrichInstalledAppFromFileSystem(app);

                        var key = $"{app.Name}|{app.Version}|{app.Publisher}";
                        apps.TryAdd(key, app);
                    }
                }
                catch
                {
                }
            }
        }

        AddAppxPackages(apps);

        return apps.Values
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CountInstalledApps()
    {
        const string uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        var version = GetRegistryString(appKey, "DisplayVersion");
                        var publisher = GetRegistryString(appKey, "Publisher");
                        apps.Add($"{name}|{version}|{publisher}");
                    }
                }
                catch
                {
                }
            }
        }

        CountAppxPackages(apps);
        return apps.Count;
    }

    private static void CountAppxPackages(ISet<string> apps)
    {
        const string currentUserRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        CountAppxPackagesFromKey(apps, RegistryHive.CurrentUser, RegistryView.Default, currentUserRepository);
        CountAppxPackagesFromKey(apps, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
    }

    private static void CountAppxPackagesFromKey(
        ISet<string> apps,
        RegistryHive hive,
        RegistryView view,
        string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(subKey, writable: false);
            if (root is null)
            {
                return;
            }

            foreach (var packageFullName in root.GetSubKeyNames())
            {
                if (!string.IsNullOrWhiteSpace(packageFullName))
                {
                    apps.Add($"appx|{packageFullName}");
                }
            }
        }
        catch
        {
        }
    }

    private static void AddAppxPackages(IDictionary<string, InstalledAppInfo> apps)
    {
        const string currentUserRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        AddAppxPackagesFromKey(apps, RegistryHive.CurrentUser, RegistryView.Default, currentUserRepository);
        AddAppxPackagesFromKey(apps, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
    }

    private static void AddAppxPackagesFromKey(
        IDictionary<string, InstalledAppInfo> apps,
        RegistryHive hive,
        RegistryView view,
        string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(subKey, writable: false);
            if (root is null)
            {
                return;
            }

            foreach (var packageFullName in root.GetSubKeyNames())
            {
                using var packageKey = root.OpenSubKey(packageFullName, writable: false);
                if (packageKey is null)
                {
                    continue;
                }

                var displayName = FirstNonEmpty(
                    ResolveIndirectString(GetRegistryString(packageKey, "DisplayName")),
                    ParseAppxDisplayName(packageFullName));
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var app = new InstalledAppInfo
                {
                    Name = displayName,
                    Version = ParseAppxVersion(packageFullName),
                    Publisher = FirstNonEmpty(
                        ResolveIndirectString(GetRegistryString(packageKey, "PublisherDisplayName")),
                        "Microsoft Store package"),
                    InstallLocation = FirstNonEmpty(
                        GetRegistryString(packageKey, "PackageRootFolder"),
                        GetRegistryString(packageKey, "Path")),
                    SizeOnDisk = string.Empty,
                    InstallDate = string.Empty,
                    DisplayIcon = string.Empty,
                    UninstallCommand = string.Empty
                };
                EnrichInstalledAppFromFileSystem(app);

                apps.TryAdd($"appx|{packageFullName}", app);
            }
        }
        catch
        {
        }
    }

    private static string ParseAppxDisplayName(string packageFullName)
    {
        var index = packageFullName.IndexOf('_', StringComparison.Ordinal);
        return index <= 0 ? packageFullName : packageFullName[..index];
    }

    private static string ParseAppxVersion(string packageFullName)
    {
        var parts = packageFullName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        return $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
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

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string FormatInstallDate(string raw)
    {
        if (raw.Length == 8 &&
            DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw;
    }

    private static string FormatEstimatedSize(long estimatedSizeKb)
    {
        return estimatedSizeKb <= 0 ? string.Empty : FormatBytes(estimatedSizeKb * 1024, decimals: 1);
    }

    private static void EnrichInstalledAppFromFileSystem(InstalledAppInfo app)
    {
        var folder = ResolveInstalledAppFolder(app);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(app.InstallDate))
        {
            app.InstallDate = FormatFileSystemDate(folder);
        }

        if (string.IsNullOrWhiteSpace(app.SizeOnDisk))
        {
            app.SizeOnDisk = TryFormatDirectorySize(folder);
        }
    }

    private static string ResolveInstalledAppFolder(InstalledAppInfo app)
    {
        var candidates = new[]
        {
            app.InstallLocation,
            GetDirectoryNameOrEmpty(NormalizePathWithOptionalIndex(app.DisplayIcon)),
            GetDirectoryNameOrEmpty(ExtractExecutablePath(app.UninstallCommand))
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                if (Directory.Exists(expanded))
                {
                    return expanded;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string GetDirectoryNameOrEmpty(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizePathWithOptionalIndex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Trim('"');
        var commaIndex = normalized.LastIndexOf(',');
        if (commaIndex > 2 && int.TryParse(normalized[(commaIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            normalized = normalized[..commaIndex];
        }

        return Environment.ExpandEnvironmentVariables(normalized.Trim().Trim('"'));
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text[0] == '"')
        {
            var endQuote = text.IndexOf('"', 1);
            return endQuote > 1 ? text[1..endQuote] : string.Empty;
        }

        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return text[..(exeIndex + 4)].Trim();
        }

        var firstSpace = text.IndexOf(' ');
        return firstSpace > 0 ? text[..firstSpace].Trim() : text;
    }

    private static string FormatFileSystemDate(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            var date = info.CreationTime;
            if (date.Year < 1990)
            {
                date = info.LastWriteTime;
            }

            return date.Year < 1990 ? string.Empty : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryFormatDirectorySize(string folder)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var pending = new Stack<string>();
            pending.Push(folder);
            long total = 0;
            var fileCount = 0;

            while (pending.Count > 0)
            {
                if (fileCount >= DirectorySizeFileLimit || stopwatch.Elapsed > DirectorySizeTimeLimit)
                {
                    return string.Empty;
                }

                var current = pending.Pop();
                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (fileCount >= DirectorySizeFileLimit || stopwatch.Elapsed > DirectorySizeTimeLimit)
                    {
                        return string.Empty;
                    }

                    try
                    {
                        total += new FileInfo(file).Length;
                        fileCount++;
                    }
                    catch
                    {
                    }
                }

                try
                {
                    foreach (var directory in Directory.GetDirectories(current))
                    {
                        pending.Push(directory);
                    }
                }
                catch
                {
                }
            }

            return total > 0 ? FormatBytes(total, decimals: 1) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> RunVmBenchmarkScoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cpuTask = Task.Run(() => MeasureCpuWork(cancellationToken), cancellationToken);
            var memoryTask = Task.Run(() => MeasureMemoryCopy(cancellationToken), cancellationToken);
            var diskTask = MeasureDiskIoAsync(cancellationToken);
            await Task.WhenAll(cpuTask, memoryTask, diskTask).ConfigureAwait(false);

            var cpuScore = ScaleBenchmark(cpuTask.Result, 500_000d, 9_000_000d);
            var memoryScore = ScaleBenchmark(memoryTask.Result, 700d, 9_000d);
            var diskScore = ScaleBenchmark(diskTask.Result, 40d, 1_800d);
            var score = Math.Round((cpuScore + memoryScore + diskScore) / 3d, 1, MidpointRounding.AwayFromZero);
            return $"VM {score:0.0}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"VM benchmark failed: {ex.Message}");
            return "Unavailable";
        }
    }

    private static double MeasureCpuWork(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var iterations = 0L;
        var value = 0x9E3779B97F4A7C15UL;

        while (stopwatch.ElapsedMilliseconds < 350)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < 10_000; i++)
            {
                value ^= value << 13;
                value ^= value >> 7;
                value ^= value << 17;
                iterations++;
            }
        }

        GC.KeepAlive(value);
        return iterations / Math.Max(0.001d, stopwatch.Elapsed.TotalSeconds);
    }

    private static double MeasureMemoryCopy(CancellationToken cancellationToken)
    {
        var source = new byte[8 * 1024 * 1024];
        var target = new byte[source.Length];
        Array.Fill<byte>(source, 0x5A);
        var copiedBytes = 0L;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < 350)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Buffer.BlockCopy(source, 0, target, 0, source.Length);
            copiedBytes += source.Length;
        }

        GC.KeepAlive(target);
        return (copiedBytes / 1024d / 1024d) / Math.Max(0.001d, stopwatch.Elapsed.TotalSeconds);
    }

    private static async Task<double> MeasureDiskIoAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phantom-vm-benchmark-{Guid.NewGuid():N}.tmp");
        var buffer = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(buffer);
        var bytes = 0L;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1024 * 1024, FileOptions.SequentialScan | FileOptions.DeleteOnClose))
            {
                while (stopwatch.ElapsedMilliseconds < 350)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    bytes += buffer.Length;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                while (stream.Position < stream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    bytes += read;
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        return (bytes / 1024d / 1024d) / Math.Max(0.001d, stopwatch.Elapsed.TotalSeconds);
    }

    private static double ScaleBenchmark(double value, double low, double high)
    {
        if (value <= low)
        {
            return 1;
        }

        if (value >= high)
        {
            return 9.9;
        }

        return 1 + ((value - low) / (high - low) * 8.9);
    }

    private async Task<bool> RunWinsatFormalAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetWinsatPath(),
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

    private const int SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int SERVICE_WIN32 = 0x00000030;
    private const int SERVICE_STATE_ALL = 0x00000003;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceControlManagerHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(
        IntPtr serviceControlManagerHandle,
        int infoLevel,
        int serviceType,
        int serviceState,
        IntPtr services,
        int bufferSize,
        out int bytesNeeded,
        out int servicesReturned,
        ref int resumeHandle,
        string? groupName);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outputBuffer, uint outputBufferCharacters, IntPtr reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private static bool TryGetMemoryStatus(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        return GlobalMemoryStatusEx(ref status);
    }

    private static long ToLong(FileTime fileTime)
        => ((long)fileTime.HighDateTime << 32) + fileTime.LowDateTime;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ServiceName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DisplayName;

        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    private sealed record ServiceRegistryInfo(string StartupType, string PathName, string Description, string DisplayName);

    private sealed record CpuSample(long Idle, long Kernel, long User);

    private sealed record HomePerformanceInfo(string DisplayText, string Tooltip, bool IsAvailable);
}
