namespace Phantom.Models;

public sealed class AppSettings
{
    public bool UseDarkMode { get; set; } = true;
    public string ThemeMode { get; set; } = AppThemeModes.Auto;
    public bool EnableDestructiveOperations { get; set; }
    public bool CreateRestorePointBeforeDangerousOperations { get; set; }
    public int HomeRefreshSeconds { get; set; } = 5;
    public int MaxLogFiles { get; set; } = 20;
    public long MaxTotalLogSizeBytes { get; set; } = 50 * 1024 * 1024;
}

public static class AppThemeModes
{
    public const string Auto = "Auto";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(mode, Dark, StringComparison.OrdinalIgnoreCase))
        {
            return Dark;
        }

        return Auto;
    }
}

public sealed class TelemetryState
{
    public long SpaceCleanedBytes { get; set; }
    public DateTimeOffset FirstRunAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, NetworkBaseline> NetworkBaselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long LastNetworkSentBytes { get; set; }
    public long LastNetworkReceivedBytes { get; set; }
    public DateTimeOffset? LastNetworkSampleAt { get; set; }
}

public sealed class NetworkBaseline
{
    public long SentBytes { get; set; }
    public long ReceivedBytes { get; set; }
}

public sealed class UndoStateDocument
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, Dictionary<string, string>> OperationState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LogEnvelope
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}
