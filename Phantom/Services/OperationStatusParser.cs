namespace Phantom.Services;

public enum OperationDetectState
{
    Unknown = 0,
    Applied = 1,
    NotApplied = 2
}

public static class OperationStatusParser
{
    private static readonly string[] ExplicitStatePrefixes =
    [
        "PHANTOM_STATUS=",
        "PHANTOM_STATUS:",
        "STATUS=",
        "STATUS:"
    ];

    public static OperationDetectState Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return OperationDetectState.Unknown;
        }

        var lines = output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        foreach (var line in lines)
        {
            if (TryParseExplicitState(line, out var explicitState))
            {
                return explicitState;
            }
        }

        var candidate = lines[^1];
        if (TryParseToken(candidate, out var tokenState))
        {
            return tokenState;
        }

        var normalized = candidate.Trim();
        if (normalized.Contains("not applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("managed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("restricted", StringComparison.OrdinalIgnoreCase))
        {
            return OperationDetectState.NotApplied;
        }

        if (normalized.Contains("applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("detected", StringComparison.OrdinalIgnoreCase))
        {
            return OperationDetectState.Applied;
        }

        return OperationDetectState.Unknown;
    }

    public static bool IsApplied(string output)
    {
        return Parse(output) == OperationDetectState.Applied;
    }

    private static bool TryParseExplicitState(string line, out OperationDetectState state)
    {
        state = OperationDetectState.Unknown;
        foreach (var prefix in ExplicitStatePrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var token = line[prefix.Length..].Trim().Trim('"', '\'');
            return TryParseToken(token, out state);
        }

        return false;
    }

    private static bool TryParseToken(string token, out OperationDetectState state)
    {
        state = OperationDetectState.Unknown;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Trim();
        if (normalized.Equals("Applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Detected", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("True", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            state = OperationDetectState.Applied;
            return true;
        }

        if (normalized.Equals("Not Applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Not Installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("False", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Managed / Restricted", StringComparison.OrdinalIgnoreCase))
        {
            state = OperationDetectState.NotApplied;
            return true;
        }

        return false;
    }
}
