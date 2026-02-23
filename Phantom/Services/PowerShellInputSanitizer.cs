using System.Text.RegularExpressions;

namespace Phantom.Services;

public static class PowerShellInputSanitizer
{
    private static readonly Regex PackageIdRegex = new("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex FeatureNameRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex SafeArgumentRegex = new("^[A-Za-z0-9\\s._:/=+,%\\\\-]{0,256}$", RegexOptions.Compiled);

    public static string EnsurePackageId(string? value, string context)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{context}: package identifier is required.");
        }

        if (!PackageIdRegex.IsMatch(trimmed))
        {
            throw new ArgumentException($"{context}: invalid package identifier '{trimmed}'.");
        }

        return trimmed;
    }

    public static string EnsureFeatureName(string? value, string context)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{context}: feature name is required.");
        }

        if (!FeatureNameRegex.IsMatch(trimmed))
        {
            throw new ArgumentException($"{context}: invalid feature name '{trimmed}'.");
        }

        return trimmed;
    }

    public static string EnsureSafeCliArguments(string? value, string context)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Contains("--%", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{context}: '--%' is not allowed in catalog arguments.");
        }

        if (!SafeArgumentRegex.IsMatch(trimmed))
        {
            throw new ArgumentException($"{context}: arguments contain unsupported characters.");
        }

        return trimmed;
    }

    public static string EnsureServiceStartupMode(string? mode)
    {
        return (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "automatic" => "Automatic",
            "manual" => "Manual",
            "disabled" => "Disabled",
            _ => throw new ArgumentException($"Invalid service startup mode '{mode}'.")
        };
    }

    public static string EnsureSafeLegacyLaunchScript(string? script, string context)
    {
        var trimmed = (script ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{context}: launch script is required.");
        }

        if (!trimmed.StartsWith("Start-Process ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{context}: only Start-Process launch scripts are allowed.");
        }

        if (trimmed.IndexOfAny(['\r', '\n', ';', '|', '&', '`', '$']) >= 0)
        {
            throw new ArgumentException($"{context}: launch script contains blocked metacharacters.");
        }

        return trimmed;
    }

    public static string EscapeSingleQuotes(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    public static string ToSingleQuotedLiteral(string value)
    {
        return $"'{EscapeSingleQuotes(value)}'";
    }
}
