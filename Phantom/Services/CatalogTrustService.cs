using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Models;

namespace Phantom.Services;

public static class CatalogTrustService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // These hashes are compiled into the binary so pre-elevation catalog tampering is detectable.
    private static readonly IReadOnlyDictionary<string, string> ExpectedDataFileHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["catalog.apps.json"] = "2A17C9685F1FA84939231E49B9AD063847055045A73F84A78C0A6C73CD148E79",
            ["tweaks.json"] = "F485D12C7D8F6D7D684D913BE857CA5A3E021BFB54DC1997068D35D841E38DBF",
            ["features.json"] = "2182F2172EF035A5CC2995DBCCD4172E5CCC91957F6DD1EFDFDDCEB5CBD87462",
            ["fixes.json"] = "2C63D665D1C959EF42E38F0425F12A8112EF6E16BDCD5B46A67EDF52F66DB904",
            ["legacy-panels.json"] = "687791E8C17DDC6E90F00665851A08E0A501CB0DDCADF0B8FBF77C56FD38FEB4"
        };

    public static bool ValidateCatalogFileIntegrity(AppPaths paths, out string reason)
    {
        var valid = TryValidateCatalogIntegrityAndBuildAllowlist(paths, out _, out reason);
        return valid;
    }

    public static bool TryValidateCatalogIntegrityAndBuildAllowlist(AppPaths paths, out HashSet<string> hashes, out string reason)
    {
        hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogBytesByName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in ExpectedDataFileHashes)
        {
            var fullPath = Path.Combine(paths.BaseDirectory, "Data", pair.Key);
            if (!File.Exists(fullPath))
            {
                reason = $"Required catalog file is missing: {pair.Key}.";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex)
            {
                reason = $"Failed to read catalog file {pair.Key}: {ex.Message}";
                return false;
            }

            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Catalog integrity check failed for {pair.Key}. Expected {pair.Value}, got {actual}.";
                return false;
            }

            catalogBytesByName[pair.Key] = bytes;
        }

        try
        {
            if (catalogBytesByName.TryGetValue("tweaks.json", out var tweaksBytes))
            {
                var tweaks = JsonSerializer.Deserialize<List<TweakDefinition>>(tweaksBytes, JsonOptions) ?? new List<TweakDefinition>();
                foreach (var tweak in tweaks)
                {
                    AddScriptHash(hashes, TweakScriptNormalizer.WrapDetectScript(tweak.DetectScript));
                    AddScriptHash(hashes, TweakScriptNormalizer.WrapMutationScript(tweak.ApplyScript));
                    AddScriptHash(hashes, TweakScriptNormalizer.WrapMutationScript(tweak.UndoScript));
                }
            }

            if (catalogBytesByName.TryGetValue("fixes.json", out var fixesBytes))
            {
                var fixes = JsonSerializer.Deserialize<List<FixDefinition>>(fixesBytes, JsonOptions) ?? new List<FixDefinition>();
                foreach (var fix in fixes)
                {
                    AddScriptHash(hashes, fix.ApplyScript);
                    AddScriptHash(hashes, fix.UndoScript);
                }
            }

            if (catalogBytesByName.TryGetValue("legacy-panels.json", out var panelsBytes))
            {
                var panels = JsonSerializer.Deserialize<List<LegacyPanelDefinition>>(panelsBytes, JsonOptions) ?? new List<LegacyPanelDefinition>();
                foreach (var panel in panels)
                {
                    AddScriptHash(hashes, panel.LaunchScript);
                }
            }

            foreach (var tweak in RequestedTweaksCatalog.CreateRequestedTweaks())
            {
                AddScriptHash(hashes, tweak.DetectScript);
                AddScriptHash(hashes, tweak.ApplyScript);
                AddScriptHash(hashes, tweak.UndoScript);
            }

            foreach (var script in RuntimeOperationScriptCatalog.GetTrustedRuntimeMutationScripts())
            {
                AddScriptHash(hashes, script);
            }
        }
        catch (Exception ex)
        {
            reason = $"Failed to build trusted script hash allowlist: {ex.Message}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static HashSet<string> BuildTrustedCatalogScriptHashAllowlist(AppPaths paths)
    {
        if (!TryValidateCatalogIntegrityAndBuildAllowlist(paths, out var hashes, out var reason))
        {
            throw new InvalidDataException(reason);
        }

        return hashes;
    }

    public static IReadOnlyList<TweakDefinition> GetRequestedTweaks()
    {
        return RequestedTweaksCatalog.CreateRequestedTweaks();
    }

    public static string ComputeScriptHash(string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static void AddScriptHash(HashSet<string> hashes, string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        hashes.Add(ComputeScriptHash(script));
    }
}
