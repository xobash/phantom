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
        => LoadAsync<CatalogApp>(_paths.CatalogFile, cancellationToken);

    public Task SaveCatalogAsync(string path, IEnumerable<CatalogApp> apps, CancellationToken cancellationToken = default)
        => SaveAsync(path, apps, cancellationToken);

    public Task<List<TweakDefinition>> LoadTweaksAsync(CancellationToken cancellationToken = default)
        => LoadAsync<TweakDefinition>(_paths.TweaksFile, cancellationToken);

    public Task<List<FeatureDefinition>> LoadFeaturesAsync(CancellationToken cancellationToken = default)
        => LoadAsync<FeatureDefinition>(_paths.FeaturesFile, cancellationToken);

    public Task<List<FixDefinition>> LoadFixesAsync(CancellationToken cancellationToken = default)
        => LoadAsync<FixDefinition>(_paths.FixesFile, cancellationToken);

    public Task<List<LegacyPanelDefinition>> LoadLegacyPanelsAsync(CancellationToken cancellationToken = default)
        => LoadAsync<LegacyPanelDefinition>(_paths.LegacyPanelsFile, cancellationToken);

    public Task SaveSelectionConfigAsync(string path, AutomationConfig config, CancellationToken cancellationToken = default)
        => SaveAsync(path, config, cancellationToken);

    public async Task<AutomationConfig> LoadSelectionConfigAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new AutomationConfig();
        }

        await using var stream = File.OpenRead(path);
        var model = await JsonSerializer.DeserializeAsync<AutomationConfig>(stream, _options, cancellationToken).ConfigureAwait(false);
        return model ?? new AutomationConfig();
    }

    private async Task<List<T>> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        await using var stream = File.OpenRead(path);
        var model = await JsonSerializer.DeserializeAsync<List<T>>(stream, _options, cancellationToken).ConfigureAwait(false);
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
}
