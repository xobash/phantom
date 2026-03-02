using Phantom.Services;

namespace Phantom.Tests;

public sealed class CatalogTrustServiceTests
{
    [Fact]
    public void ValidateCatalogFileIntegrity_Succeeds_ForBundledDataFiles()
    {
        var paths = TestHelpers.CreateIsolatedPaths();

        var success = CatalogTrustService.ValidateCatalogFileIntegrity(paths, out var reason);

        Assert.True(success, reason);
        Assert.True(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task BuildTrustedCatalogScriptHashAllowlist_CoversAllCatalogAndRequestedTweaks()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var definitions = new DefinitionCatalogService(paths);
        var tweakCatalog = await definitions.LoadTweaksAsync(CancellationToken.None);
        var requestedTweaks = CatalogTrustService.GetRequestedTweaks();
        var allowlist = CatalogTrustService.BuildTrustedCatalogScriptHashAllowlist(paths);

        foreach (var tweak in tweakCatalog.Concat(requestedTweaks))
        {
            Assert.Contains(CatalogTrustService.ComputeScriptHash(tweak.DetectScript), allowlist);
            Assert.Contains(CatalogTrustService.ComputeScriptHash(tweak.ApplyScript), allowlist);
            Assert.Contains(CatalogTrustService.ComputeScriptHash(tweak.UndoScript), allowlist);
        }

        foreach (var runtimeScript in RuntimeOperationScriptCatalog.GetTrustedRuntimeMutationScripts())
        {
            Assert.Contains(CatalogTrustService.ComputeScriptHash(runtimeScript), allowlist);
        }
    }

    [Fact]
    public void TryValidateCatalogIntegrityAndBuildAllowlist_FailsWhenCatalogFileIsTampered()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        File.AppendAllText(paths.TweaksFile, Environment.NewLine + " ");

        var success = CatalogTrustService.TryValidateCatalogIntegrityAndBuildAllowlist(paths, out var allowlist, out var reason);

        Assert.False(success);
        Assert.Empty(allowlist);
        Assert.Contains("integrity check failed", reason, StringComparison.OrdinalIgnoreCase);
    }
}
