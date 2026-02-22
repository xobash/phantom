using Phantom.Services;

namespace Phantom.Tests;

public sealed class DefinitionCatalogServiceTests
{
    [Fact]
    public async Task LoadTweaksAsync_ThrowsInvalidDataException_WhenSchemaIsInvalid()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var dataDir = Path.Combine(paths.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDir);

        var invalidTweaksJson = """
        [
          {
            "id": "broken-tweak",
            "name": "Broken",
            "riskTier": "Basic",
            "scope": "HKCU",
            "reversible": true,
            "detectScript": "Write-Output 'x'",
            "applyScript": "Write-Output 'y'",
            "undoScript": "Write-Output 'z'",
            "destructive": false
          }
        ]
        """;

        await File.WriteAllTextAsync(Path.Combine(dataDir, "tweaks.json"), invalidTweaksJson);
        var service = new DefinitionCatalogService(paths);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadTweaksAsync(CancellationToken.None));
        Assert.Contains("Schema validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
