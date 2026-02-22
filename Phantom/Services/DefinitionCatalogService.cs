using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Models;

namespace Phantom.Services;

public sealed class DefinitionCatalogService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DefinitionCatalogService(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<List<CatalogApp>> LoadCatalogAsync(CancellationToken cancellationToken = default)
        => LoadValidatedArrayAsync<CatalogApp>(_paths.CatalogFile, "catalog.apps", ValidateCatalogApp, cancellationToken);

    public Task SaveCatalogAsync(string path, IEnumerable<CatalogApp> apps, CancellationToken cancellationToken = default)
        => SaveAsync(path, apps, cancellationToken);

    public Task<List<TweakDefinition>> LoadTweaksAsync(CancellationToken cancellationToken = default)
        => LoadValidatedArrayAsync<TweakDefinition>(_paths.TweaksFile, "tweaks", ValidateTweak, cancellationToken);

    public Task<List<FeatureDefinition>> LoadFeaturesAsync(CancellationToken cancellationToken = default)
        => LoadValidatedArrayAsync<FeatureDefinition>(_paths.FeaturesFile, "features", ValidateFeature, cancellationToken);

    public Task<List<FixDefinition>> LoadFixesAsync(CancellationToken cancellationToken = default)
        => LoadValidatedArrayAsync<FixDefinition>(_paths.FixesFile, "fixes", ValidateFix, cancellationToken);

    public Task<List<LegacyPanelDefinition>> LoadLegacyPanelsAsync(CancellationToken cancellationToken = default)
        => LoadValidatedArrayAsync<LegacyPanelDefinition>(_paths.LegacyPanelsFile, "legacy-panels", ValidateLegacyPanel, cancellationToken);

    public Task SaveSelectionConfigAsync(string path, AutomationConfig config, CancellationToken cancellationToken = default)
        => SaveAsync(path, config, cancellationToken);

    public async Task<AutomationConfig> LoadSelectionConfigAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new AutomationConfig();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateSelectionConfig(path, json);
        var model = JsonSerializer.Deserialize<AutomationConfig>(json, _options);
        return model ?? new AutomationConfig();
    }

    private async Task<List<T>> LoadValidatedArrayAsync<T>(
        string path,
        string schemaName,
        Action<JsonElement, int, List<string>> validateItem,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateArraySchema(path, schemaName, json, validateItem);
        var model = JsonSerializer.Deserialize<List<T>>(json, _options);
        return model ?? new List<T>();
    }

    private async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateArraySchema(
        string path,
        string schemaName,
        string json,
        Action<JsonElement, int, List<string>> validateItem)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON in {path}: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Schema validation failed for {schemaName} ({path}): root must be an array.");
            }

            var errors = new List<string>();
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                validateItem(item, index, errors);
                index++;
            }

            if (errors.Count == 0)
            {
                return;
            }

            var summary = string.Join(" | ", errors.Take(8));
            throw new InvalidDataException($"Schema validation failed for {schemaName} ({path}). {summary}");
        }
    }

    private static void ValidateSelectionConfig(string path, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON in {path}: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Schema validation failed for selection config ({path}): root must be an object.");
            }

            var errors = new List<string>();
            var root = doc.RootElement;
            ValidateBoolean(root, "confirmDangerous", "selection-config", errors, required: false);
            ValidateStringArray(root, "storeSelections", "selection-config", errors, required: false);
            ValidateStringArray(root, "tweaks", "selection-config", errors, required: false);
            ValidateStringArray(root, "features", "selection-config", errors, required: false);
            ValidateStringArray(root, "fixes", "selection-config", errors, required: false);
            ValidateString(root, "updateMode", "selection-config", errors, required: false, allowEmpty: false);

            if (errors.Count > 0)
            {
                throw new InvalidDataException($"Schema validation failed for selection config ({path}). {string.Join(" | ", errors.Take(8))}");
            }
        }
    }

    private static void ValidateCatalogApp(JsonElement item, int index, List<string> errors)
    {
        var ctx = $"catalog.apps[{index}]";
        if (!EnsureObject(item, ctx, errors))
        {
            return;
        }

        ValidateString(item, "category", ctx, errors, required: true, allowEmpty: false);
        var displayName = ValidateString(item, "displayName", ctx, errors, required: true, allowEmpty: false);
        var wingetId = ValidateString(item, "wingetId", ctx, errors, required: false, allowEmpty: true);
        var chocoId = ValidateString(item, "chocoId", ctx, errors, required: false, allowEmpty: true);
        ValidateString(item, "silentArgs", ctx, errors, required: false, allowEmpty: true);
        ValidateString(item, "homepage", ctx, errors, required: false, allowEmpty: true);
        ValidateStringArray(item, "tags", ctx, errors, required: false);

        if (string.IsNullOrWhiteSpace(wingetId) && string.IsNullOrWhiteSpace(chocoId))
        {
            errors.Add($"{ctx}.wingetId/chocoId: at least one package manager identifier is required.");
        }

        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Length > 128)
        {
            errors.Add($"{ctx}.displayName: exceeds max length 128.");
        }
    }

    private static void ValidateTweak(JsonElement item, int index, List<string> errors)
    {
        var ctx = $"tweaks[{index}]";
        if (!EnsureObject(item, ctx, errors))
        {
            return;
        }

        ValidateString(item, "id", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "name", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "description", ctx, errors, required: true, allowEmpty: false);
        var riskTier = ValidateString(item, "riskTier", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "scope", ctx, errors, required: true, allowEmpty: false);
        ValidateBoolean(item, "reversible", ctx, errors, required: true);
        ValidateString(item, "detectScript", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "applyScript", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "undoScript", ctx, errors, required: true, allowEmpty: false);
        ValidateStringArray(item, "stateCaptureKeys", ctx, errors, required: false);
        ValidateBoolean(item, "destructive", ctx, errors, required: true);

        if (!string.IsNullOrWhiteSpace(riskTier) && !Enum.TryParse<RiskTier>(riskTier, true, out _))
        {
            errors.Add($"{ctx}.riskTier: invalid value '{riskTier}'.");
        }
    }

    private static void ValidateFeature(JsonElement item, int index, List<string> errors)
    {
        var ctx = $"features[{index}]";
        if (!EnsureObject(item, ctx, errors))
        {
            return;
        }

        ValidateString(item, "id", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "name", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "featureName", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "description", ctx, errors, required: true, allowEmpty: false);
    }

    private static void ValidateFix(JsonElement item, int index, List<string> errors)
    {
        var ctx = $"fixes[{index}]";
        if (!EnsureObject(item, ctx, errors))
        {
            return;
        }

        ValidateString(item, "id", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "name", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "description", ctx, errors, required: true, allowEmpty: false);
        var riskTier = ValidateString(item, "riskTier", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "applyScript", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "undoScript", ctx, errors, required: true, allowEmpty: false);
        ValidateBoolean(item, "reversible", ctx, errors, required: true);
        ValidateBoolean(item, "destructive", ctx, errors, required: true);

        if (!string.IsNullOrWhiteSpace(riskTier) && !Enum.TryParse<RiskTier>(riskTier, true, out _))
        {
            errors.Add($"{ctx}.riskTier: invalid value '{riskTier}'.");
        }
    }

    private static void ValidateLegacyPanel(JsonElement item, int index, List<string> errors)
    {
        var ctx = $"legacy-panels[{index}]";
        if (!EnsureObject(item, ctx, errors))
        {
            return;
        }

        ValidateString(item, "id", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "name", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "description", ctx, errors, required: true, allowEmpty: false);
        ValidateString(item, "launchScript", ctx, errors, required: true, allowEmpty: false);
    }

    private static bool EnsureObject(JsonElement element, string context, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        errors.Add($"{context}: entry must be an object.");
        return false;
    }

    private static string ValidateString(
        JsonElement objectElement,
        string propertyName,
        string context,
        List<string> errors,
        bool required,
        bool allowEmpty)
    {
        if (!TryGetPropertyIgnoreCase(objectElement, propertyName, out var prop))
        {
            if (required)
            {
                errors.Add($"{context}.{propertyName}: missing required property.");
            }

            return string.Empty;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{propertyName}: must be a string.");
            return string.Empty;
        }

        var value = prop.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{context}.{propertyName}: must not be empty.");
        }

        return value;
    }

    private static void ValidateBoolean(
        JsonElement objectElement,
        string propertyName,
        string context,
        List<string> errors,
        bool required)
    {
        if (!TryGetPropertyIgnoreCase(objectElement, propertyName, out var prop))
        {
            if (required)
            {
                errors.Add($"{context}.{propertyName}: missing required property.");
            }

            return;
        }

        if (prop.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add($"{context}.{propertyName}: must be a boolean.");
        }
    }

    private static void ValidateStringArray(
        JsonElement objectElement,
        string propertyName,
        string context,
        List<string> errors,
        bool required)
    {
        if (!TryGetPropertyIgnoreCase(objectElement, propertyName, out var prop))
        {
            if (required)
            {
                errors.Add($"{context}.{propertyName}: missing required property.");
            }

            return;
        }

        if (prop.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{context}.{propertyName}: must be an array.");
            return;
        }

        var idx = 0;
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{context}.{propertyName}[{idx}]: must be a string.");
            }

            idx++;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement objectElement, string propertyName, out JsonElement value)
    {
        foreach (var prop in objectElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
