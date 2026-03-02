namespace Phantom.Services;

public static class HomeCardIconCatalog
{
    public static readonly IReadOnlyList<string> RequiredTopCardTitles =
    [
        "System",
        "Graphics",
        "Storage",
        "Uptime",
        "Processor",
        "Memory",
        "Windows",
        "Performance"
    ];

    public static readonly IReadOnlyList<string> RequiredKpiTitles =
    [
        "Apps",
        "Processes",
        "Services",
        "Space cleaned",
        "CPU %",
        "GPU %",
        "Memory %",
        "Network"
    ];

    private static readonly IReadOnlyDictionary<string, string> TopCardGlyphByTitle =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System"] = "\uE770",
            ["Graphics"] = "\uE7F4",
            ["Storage"] = "\uEDA2",
            ["Uptime"] = "\uE823",
            ["Processor"] = "\uEA80",
            ["Memory"] = "\uE950",
            ["Windows"] = "\uE782",
            ["Performance"] = "\uE9D9"
        };

    private static readonly IReadOnlyDictionary<string, string> KpiGlyphByTitle =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Apps"] = "\uE8FD",
            ["Processes"] = "\uE9F5",
            ["Services"] = "\uE895",
            ["Space cleaned"] = "\uE82D",
            ["CPU %"] = "\uEA80",
            ["GPU %"] = "\uE7FC",
            ["Memory %"] = "\uE950",
            ["Network"] = "\uE968"
        };

    public static string GetTopCardGlyph(string title)
    {
        return GetRequiredGlyph(title, TopCardGlyphByTitle, "top");
    }

    public static string GetKpiGlyph(string title)
    {
        return GetRequiredGlyph(title, KpiGlyphByTitle, "KPI");
    }

    public static void ValidateRequiredMappings()
    {
        ValidateMappings(RequiredTopCardTitles, TopCardGlyphByTitle, "top");
        ValidateMappings(RequiredKpiTitles, KpiGlyphByTitle, "KPI");
    }

    private static void ValidateMappings(
        IEnumerable<string> requiredTitles,
        IReadOnlyDictionary<string, string> mapping,
        string groupName)
    {
        foreach (var title in requiredTitles)
        {
            _ = GetRequiredGlyph(title, mapping, groupName);
        }
    }

    private static string GetRequiredGlyph(string title, IReadOnlyDictionary<string, string> mapping, string groupName)
    {
        var normalized = title?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"Home {groupName} icon lookup failed: title is empty.");
        }

        if (!mapping.TryGetValue(normalized, out var glyph) || string.IsNullOrWhiteSpace(glyph))
        {
            throw new InvalidOperationException(
                $"Home {groupName} icon mapping is missing for '{normalized}'. Add an explicit icon mapping.");
        }

        return glyph;
    }
}
