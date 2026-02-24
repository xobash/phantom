using Microsoft.Win32;

namespace Phantom.Services;

/// <summary>
/// Validates Windows version support and provides architecture-aware platform helpers.
/// </summary>
public static class WindowsSupportPolicy
{
    /// <summary>
    /// Gets the minimum supported Windows version.
    /// </summary>
    public static Version MinimumSupportedVersion { get; } = new(10, 0, 19041, 0);

    /// <summary>
    /// Gets the maximum validated Windows version.
    /// </summary>
    public static Version MaximumValidatedVersion { get; } = new(10, 0, 29999, 0);

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
                $"Unsupported Windows version {current}. Maximum validated version is {MaximumValidatedVersion}.";
            return false;
        }

        message = string.Empty;
        return true;
    }
}
