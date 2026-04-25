using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Phantom.Services;

internal static partial class CompiledTweakScriptService
{
    public static bool TryEvaluateDetect(string? script, out string status)
    {
        status = "Unknown";
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        var body = ExtractNormalizedDetectBody(script);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            var context = new ScriptContext();
            LearnPathVariables(body, context);
            LearnPropertyVariables(body, context);
            var condition = AppliedIfRegex().Match(body);
            if (!condition.Success)
            {
                return false;
            }

            status = EvaluateCondition(condition.Groups["condition"].Value, context) ? "Applied" : "Not Applied";
            return true;
        }
        catch
        {
            status = "Not Applied";
            return true;
        }
    }

    public static bool TryExecuteMutation(string? script, bool dryRun, out string message)
    {
        message = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        var commands = SplitCommands(script)
            .Select(command => command.Trim())
            .Where(command => command.Length > 0)
            .ToArray();
        if (commands.Length == 0)
        {
            return false;
        }

        var context = new ScriptContext();
        var actions = new List<Action>();
        foreach (var command in commands)
        {
            if (IsWrapperCommand(command))
            {
                continue;
            }

            var assignment = PathVariableRegex().Match(command);
            if (assignment.Success)
            {
                context.Paths[assignment.Groups["var"].Value] = UnescapePowerShell(assignment.Groups["path"].Value);
                continue;
            }

            var newItem = NewItemRegex().Match(command);
            if (newItem.Success)
            {
                var path = ResolvePathToken(newItem.Groups["path"].Value, context);
                if (!TryParseRegistryPath(path, out var target))
                {
                    return false;
                }

                actions.Add(() => CreateKey(target));
                continue;
            }

            var setItem = SetItemRegex().Match(command);
            if (setItem.Success)
            {
                var path = ResolvePathToken(setItem.Groups["path"].Value, context);
                var name = NormalizeRegistryValueName(Unquote(setItem.Groups["name"].Value));
                var type = setItem.Groups["type"].Success ? setItem.Groups["type"].Value : string.Empty;
                var value = ParsePowerShellValue(setItem.Groups["value"].Value, type);
                if (!TryParseRegistryPath(path, out var target))
                {
                    return false;
                }

                actions.Add(() => SetValue(target, name, value.Value, value.Kind));
                continue;
            }

            var removeItem = RemoveItemRegex().Match(command);
            if (removeItem.Success)
            {
                var path = ResolvePathToken(removeItem.Groups["path"].Value, context);
                var name = NormalizeRegistryValueName(Unquote(removeItem.Groups["name"].Value));
                if (!TryParseRegistryPath(path, out var target))
                {
                    return false;
                }

                actions.Add(() => RemoveValue(target, name));
                continue;
            }

            return false;
        }

        if (!dryRun)
        {
            foreach (var action in actions)
            {
                action();
            }
        }

        message = actions.Count == 0
            ? "No compiled registry mutations found."
            : $"Executed {actions.Count} compiled registry mutation{(actions.Count == 1 ? string.Empty : "s")}.";
        return actions.Count > 0;
    }

    public static bool TryCaptureRegistryState(string keyPath, out string json)
    {
        json = string.Empty;
        if (!OperatingSystem.IsWindows() || !TryParseRegistryPath(keyPath, out var path))
        {
            return false;
        }

        using var baseKey = RegistryKey.OpenBaseKey(path.Hive, path.View);
        using var key = baseKey.OpenSubKey(path.SubKey, writable: false);
        if (key is null)
        {
            json = JsonSerializer.Serialize(new RegistryCapture("Registry", false, []));
            return true;
        }

        var values = new List<RegistryCaptureValue>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var value = raw is byte[] bytes
                ? Convert.ToHexString(bytes)
                : raw;
            values.Add(new RegistryCaptureValue(name, kind.ToString(), value));
        }

        json = JsonSerializer.Serialize(new RegistryCapture("Registry", true, values));
        return true;
    }

    private static void LearnPathVariables(string body, ScriptContext context)
    {
        foreach (Match match in PathVariableRegex().Matches(body))
        {
            context.Paths[match.Groups["var"].Value] = UnescapePowerShell(match.Groups["path"].Value);
        }
    }

    private static void LearnPropertyVariables(string body, ScriptContext context)
    {
        foreach (Match match in PropertyAssignmentRegex().Matches(body))
        {
            var variable = match.Groups["var"].Value;
            var path = ResolvePathToken(match.Groups["path"].Value, context);
            var name = Unquote(match.Groups["name"].Value);
            context.Values[variable] = ReadValue(path, name);
        }
    }

    private static bool EvaluateCondition(string condition, ScriptContext context)
    {
        var orGroups = Regex.Split(condition, @"\s+-or\s+", RegexOptions.IgnoreCase);
        foreach (var orGroup in orGroups)
        {
            var andParts = Regex.Split(orGroup, @"\s+-and\s+", RegexOptions.IgnoreCase);
            var andResult = true;
            foreach (var part in andParts)
            {
                andResult &= EvaluateComparison(part.Trim(), context);
            }

            if (andResult)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateComparison(string comparison, ScriptContext context)
    {
        var match = ComparisonRegex().Match(comparison);
        if (!match.Success)
        {
            return false;
        }

        object? actual;
        if (match.Groups["var"].Success)
        {
            var variableName = match.Groups["var"].Value.TrimStart('$');
            actual = context.Values.TryGetValue(variableName, out var value) ? value : null;
        }
        else
        {
            var path = ResolvePathToken(match.Groups["path"].Value, context);
            actual = ReadValue(path, Unquote(match.Groups["name"].Value));
        }

        var expected = ParseComparisonValue(match.Groups["value"].Value);
        var equal = ValuesEqual(actual, expected);
        return match.Groups["op"].Value.Equals("-ne", StringComparison.OrdinalIgnoreCase) ? !equal : equal;
    }

    private static object? ReadValue(string path, string name)
    {
        if (!TryParseRegistryPath(path, out var target))
        {
            throw new InvalidOperationException("Unsupported registry path.");
        }

        using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
        using var key = baseKey.OpenSubKey(target.SubKey, writable: false);
        return key?.GetValue(NormalizeRegistryValueName(name), null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static bool ValuesEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (TryConvertLong(actual, out var actualLong) && TryConvertLong(expected, out var expectedLong))
        {
            return actualLong == expectedLong;
        }

        return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertLong(object value, out long parsed)
    {
        switch (value)
        {
            case byte b:
                parsed = b;
                return true;
            case short s:
                parsed = s;
                return true;
            case int i:
                parsed = i;
                return true;
            case long l:
                parsed = l;
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed):
                return true;
            default:
                parsed = 0;
                return false;
        }
    }

    private static string ExtractNormalizedDetectBody(string script)
    {
        var startToken = "$___phantomRaw = @(";
        var start = script.IndexOf(startToken, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return script;
        }

        start += startToken.Length;
        var end = script.IndexOf(") | Out-String", start, StringComparison.OrdinalIgnoreCase);
        return end <= start ? string.Empty : script[start..end].Trim();
    }

    private static IEnumerable<string> SplitCommands(string script)
    {
        var current = new List<char>();
        var quote = '\0';
        foreach (var ch in script)
        {
            if (quote == '\0' && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                current.Add(ch);
                continue;
            }

            if (quote != '\0')
            {
                current.Add(ch);
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is ';' or '\n' or '\r')
            {
                var item = new string(current.ToArray()).Trim();
                if (item.Length > 0)
                {
                    yield return item;
                }

                current.Clear();
                continue;
            }

            current.Add(ch);
        }

        var tail = new string(current.ToArray()).Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    private static bool IsWrapperCommand(string command)
    {
        return command.StartsWith("$ErrorActionPreference=", StringComparison.OrdinalIgnoreCase) ||
               command.StartsWith("$PSDefaultParameterValues", StringComparison.OrdinalIgnoreCase) ||
               command.StartsWith("Set-StrictMode ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePathToken(string token, ScriptContext context)
    {
        var trimmed = token.Trim();
        if (trimmed.StartsWith('$') && context.Paths.TryGetValue(trimmed[1..], out var path))
        {
            return path;
        }

        return Unquote(trimmed);
    }

    private static (object Value, RegistryValueKind Kind) ParsePowerShellValue(string raw, string type)
    {
        var value = ParseComparisonValue(raw);
        var kind = type.ToLowerInvariant() switch
        {
            "dword" => RegistryValueKind.DWord,
            "qword" => RegistryValueKind.QWord,
            "string" => RegistryValueKind.String,
            "expandstring" => RegistryValueKind.ExpandString,
            _ => value is int or long ? RegistryValueKind.DWord : RegistryValueKind.String
        };

        if (kind == RegistryValueKind.DWord)
        {
            value = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        return (value ?? string.Empty, kind);
    }

    private static object? ParseComparisonValue(string raw)
    {
        var text = raw.Trim();
        if (text.Equals("$null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.Equals("$true", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (text.Equals("$false", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if ((text.StartsWith('\'') && text.EndsWith('\'')) ||
            (text.StartsWith('"') && text.EndsWith('"')))
        {
            return UnescapePowerShell(text[1..^1]);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : text;
    }

    private static bool TryParseRegistryPath(string rawPath, out RegistryPath path)
    {
        path = default;
        var raw = rawPath.Trim().TrimEnd('\\');
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        if (raw.StartsWith(@"HKCU:\", StringComparison.OrdinalIgnoreCase))
        {
            path = new RegistryPath(RegistryHive.CurrentUser, view, raw[6..]);
            return true;
        }

        if (raw.StartsWith(@"HKLM:\", StringComparison.OrdinalIgnoreCase))
        {
            path = new RegistryPath(RegistryHive.LocalMachine, view, raw[6..]);
            return true;
        }

        const string users = @"Registry::HKEY_USERS\";
        if (raw.StartsWith(users, StringComparison.OrdinalIgnoreCase))
        {
            path = new RegistryPath(RegistryHive.Users, view, raw[users.Length..]);
            return true;
        }

        return false;
    }

    private static void CreateKey(RegistryPath path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(path.Hive, path.View);
        using var _ = baseKey.CreateSubKey(path.SubKey, writable: true);
    }

    private static void SetValue(RegistryPath path, string name, object value, RegistryValueKind kind)
    {
        using var baseKey = RegistryKey.OpenBaseKey(path.Hive, path.View);
        using var key = baseKey.CreateSubKey(path.SubKey, writable: true)
            ?? throw new InvalidOperationException($"Unable to open registry key '{path.SubKey}'.");
        key.SetValue(name, value, kind);
    }

    private static void RemoveValue(RegistryPath path, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(path.Hive, path.View);
        using var key = baseKey.OpenSubKey(path.SubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string NormalizeRegistryValueName(string name)
        => name.Equals("(Default)", StringComparison.OrdinalIgnoreCase) ? string.Empty : name;

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        if ((trimmed.StartsWith('\'') && trimmed.EndsWith('\'')) ||
            (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            return UnescapePowerShell(trimmed[1..^1]);
        }

        return trimmed;
    }

    private static string UnescapePowerShell(string text)
        => text.Replace("''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"\$(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*'(?<path>(?:HKCU|HKLM):\\[^']+|Registry::HKEY_USERS\\[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex PathVariableRegex();

    [GeneratedRegex(@"New-Item\s+-Path\s+(?<path>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+')\s+-Force(?:\s*\|\s*Out-Null)?", RegexOptions.IgnoreCase)]
    private static partial Regex NewItemRegex();

    [GeneratedRegex(@"Set-ItemProperty\s+-Path\s+(?<path>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+')\s+-Name\s+(?<name>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+'|[A-Za-z0-9_\-\(\)]+)(?:\s+-Type\s+(?<type>[A-Za-z]+))?\s+-Value\s+(?<value>'[^']*'|""[^""]*""|\$true|\$false|-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SetItemRegex();

    [GeneratedRegex(@"Remove-ItemProperty\s+-Path\s+(?<path>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+')\s+-Name\s+(?<name>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+'|[A-Za-z0-9_\-\(\)]+)(?:\s+-ErrorAction\s+\w+)?", RegexOptions.IgnoreCase)]
    private static partial Regex RemoveItemRegex();

    [GeneratedRegex(@"\$(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\(Get-ItemProperty\s+-Path\s+(?<path>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+')\s+-Name\s+(?<name>'[^']+'|[A-Za-z0-9_\-]+)[^)]*\)\.(?:'[^']+'|[A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PropertyAssignmentRegex();

    [GeneratedRegex(@"if\s*\((?<condition>.*?)\)\s*\{\s*'Applied'\s*\}\s*else\s*\{\s*'Not Applied'\s*\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AppliedIfRegex();

    [GeneratedRegex(@"(?:(?<var>\$[A-Za-z_][A-Za-z0-9_]*)|\(Get-ItemProperty\s+-Path\s+(?<path>\$[A-Za-z_][A-Za-z0-9_]*|'[^']+')\s+-Name\s+(?<name>'[^']+'|[A-Za-z0-9_\-]+)[^)]*\)\.(?:'[^']+'|[A-Za-z0-9_\-]+))\s*(?<op>-eq|-ne)\s*(?<value>'[^']*'|""[^""]*""|\$null|\$true|\$false|-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonRegex();

    private sealed class ScriptContext
    {
        public Dictionary<string, string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct RegistryPath(RegistryHive Hive, RegistryView View, string SubKey);

    private sealed record RegistryCapture(string Type, bool Exists, IReadOnlyList<RegistryCaptureValue> Values);

    private sealed record RegistryCaptureValue(string Name, string Kind, object? Value);
}
