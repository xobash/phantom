using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Services;

public sealed class JsonFileStore
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<T> LoadAsync<T>(string path, Func<T> fallback, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return fallback();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
            return value ?? fallback();
        }
        catch
        {
            try
            {
                var backupPath = $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(path, backupPath, overwrite: true);
            }
            catch
            {
            }

            return fallback();
        }
    }

    public async Task SaveAsync<T>(string path, T model, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, model, _options, cancellationToken).ConfigureAwait(false);
    }
}
