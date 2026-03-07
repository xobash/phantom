using Microsoft.Win32;

namespace Phantom.Services;

/// <summary>
/// Validates Windows version support and provides architecture-aware platform helpers.
/// </summary>
public static class WindowsSupportPolicy
{
    private const string MaximumVersionOverrideEnvironmentVariable = "PHANTOM_MAX_VALIDATED_OS_VERSION";
    private static readonly Version DefaultMaximumValidatedVersion = new(10, 0, 29999, 0);

    /// <summary>
    /// Gets the minimum supported Windows version.
    /// </summary>
    public static Version MinimumSupportedVersion { get; } = new(10, 0, 19041, 0);

    /// <summary>
    /// Gets the maximum validated Windows version.
    /// </summary>
    public static Version MaximumValidatedVersion { get; } = ResolveMaximumValidatedVersion();

    /// <summary>
    /// Gets the preferred registry view for the current operating system architecture.
    /// </summary>
    public static RegistryView PreferredRegistryView =>
        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

    /// <summary>
    /// Returns the current operating system version.
    /// </summary>
    public static Version GetCurrentOsVersion() => Environment.OSVersion.Version;

    /// <summary>
    /// Validates whether the current OS version is within the supported range.
    /// </summary>
    public static bool IsCurrentOsSupported(out string message)
    {
        return IsCurrentOsSupported(out message, out _);
    }

    /// <summary>
    /// Validates whether the current OS version is within the supported range and indicates warning-only outcomes.
    /// </summary>
    public static bool IsCurrentOsSupported(out string message, out bool warningOnly)
    {
        warningOnly = false;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            message = "Unsupported OS: Phantom requires Windows (Win32NT).";
            return false;
        }

        var current = GetCurrentOsVersion();
        if (current < MinimumSupportedVersion)
        {
            message =
                $"Unsupported Windows version {current}. Minimum supported version is {MinimumSupportedVersion} (Windows 10 build 19041).";
            return false;
        }

        if (current > MaximumValidatedVersion)
        {
            message =
                $"Windows version {current} is newer than the maximum validated version {MaximumValidatedVersion}. Continuing with caution.";
            warningOnly = true;
            return true;
        }

        message = string.Empty;
        return true;
    }

    private static Version ResolveMaximumValidatedVersion()
    {
        var overrideValue = Environment.GetEnvironmentVariable(MaximumVersionOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            Version.TryParse(overrideValue.Trim(), out var parsedOverride) &&
            parsedOverride >= MinimumSupportedVersion)
        {
            return parsedOverride;
        }

        return DefaultMaximumValidatedVersion;
    }
}
