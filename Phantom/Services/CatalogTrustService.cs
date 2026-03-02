using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phantom.Models;

namespace Phantom.Services;

public static class CatalogTrustService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // These hashes are compiled into the binary so pre-elevation catalog tampering is detectable.
    private static readonly IReadOnlyDictionary<string, string> ExpectedDataFileHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["catalog.apps.json"] = "2A17C9685F1FA84939231E49B9AD063847055045A73F84A78C0A6C73CD148E79",
            ["tweaks.json"] = "F485D12C7D8F6D7D684D913BE857CA5A3E021BFB54DC1997068D35D841E38DBF",
            ["features.json"] = "2182F2172EF035A5CC2995DBCCD4172E5CCC91957F6DD1EFDFDDCEB5CBD87462",
            ["fixes.json"] = "21F4CCFD8699F27FE6B20DA2BCCCE751245553FF654BFB46B475752F9760D928",
            ["legacy-panels.json"] = "687791E8C17DDC6E90F00665851A08E0A501CB0DDCADF0B8FBF77C56FD38FEB4"
        };

    public static bool ValidateCatalogFileIntegrity(AppPaths paths, out string reason)
    {
        foreach (var pair in ExpectedDataFileHashes)
        {
            var fullPath = Path.Combine(paths.BaseDirectory, "Data", pair.Key);
            if (!File.Exists(fullPath))
            {
                reason = $"Required catalog file is missing: {pair.Key}.";
                return false;
            }

            var actual = ComputeFileHash(fullPath);
            if (!actual.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Catalog integrity check failed for {pair.Key}. Expected {pair.Value}, got {actual}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public static HashSet<string> BuildTrustedCatalogScriptHashAllowlist(AppPaths paths)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(paths.TweaksFile))
        {
            var tweaksJson = File.ReadAllText(paths.TweaksFile);
            var tweaks = JsonSerializer.Deserialize<List<TweakDefinition>>(tweaksJson, JsonOptions) ?? new List<TweakDefinition>();
            foreach (var tweak in tweaks)
            {
                AddScriptHash(hashes, TweakScriptNormalizer.WrapDetectScript(tweak.DetectScript));
                AddScriptHash(hashes, TweakScriptNormalizer.WrapMutationScript(tweak.ApplyScript));
                AddScriptHash(hashes, TweakScriptNormalizer.WrapMutationScript(tweak.UndoScript));
            }
        }

        if (File.Exists(paths.FixesFile))
        {
            var fixesJson = File.ReadAllText(paths.FixesFile);
            var fixes = JsonSerializer.Deserialize<List<FixDefinition>>(fixesJson, JsonOptions) ?? new List<FixDefinition>();
            foreach (var fix in fixes)
            {
                AddScriptHash(hashes, fix.ApplyScript);
                AddScriptHash(hashes, fix.UndoScript);
            }
        }

        if (File.Exists(paths.LegacyPanelsFile))
        {
            var panelsJson = File.ReadAllText(paths.LegacyPanelsFile);
            var panels = JsonSerializer.Deserialize<List<LegacyPanelDefinition>>(panelsJson, JsonOptions) ?? new List<LegacyPanelDefinition>();
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
