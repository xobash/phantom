using Phantom.Services;
using Phantom.Models;

namespace Phantom.Tests;

public sealed class StoreInstallPipelineTests
{
    [Fact]
    public async Task ResolveWingetAsync_PrefersLocalWindowsAppsPath()
    {
        var localAppData = "/tmp/test-local";
        var expectedPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expectedPath };

        var resolver = new PackageManagerResolver(
            getEnvironmentVariable: name => name == "LOCALAPPDATA" ? localAppData : null,
            fileExists: path => files.Contains(path),
            pathResolver: (_, _) => Task.FromResult<string?>("/usr/bin/winget"));

        var result = await resolver.ResolveWingetAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(expectedPath, result.ExecutablePath);
        Assert.Equal("LOCALAPPDATA\\Microsoft\\WindowsApps", result.Source);
    }

    [Fact]
    public async Task ResolveWingetAsync_FallsBackToPathResolverWhenLocalCandidateMissing()
    {
        var fallback = "/usr/local/bin/winget.exe";
        var resolver = new PackageManagerResolver(
            getEnvironmentVariable: name => name == "LOCALAPPDATA" ? "/tmp/missing" : null,
            fileExists: _ => false,
            pathResolver: (_, _) => Task.FromResult<string?>(fallback));

        var result = await resolver.ResolveWingetAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(fallback, result.ExecutablePath);
        Assert.Equal("PATH(where)", result.Source);
    }

    [Fact]
    public async Task ResolveChocolateyAsync_PrefersChocolateyInstallBin()
    {
        var chocoInstall = "/opt/choco";
        var preferred = Path.Combine(chocoInstall, "bin", "choco.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { preferred };

        var resolver = new PackageManagerResolver(
            getEnvironmentVariable: name => name switch
            {
                "ChocolateyInstall" => chocoInstall,
                "ProgramData" => "/var/programdata",
                _ => null
            },
            fileExists: path => files.Contains(path),
            pathResolver: (_, _) => Task.FromResult<string?>("/usr/bin/choco"));

        var result = await resolver.ResolveChocolateyAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(preferred, result.ExecutablePath);
        Assert.Equal("ChocolateyInstall\\bin", result.Source);
    }

    [Fact]
    public async Task ResolveAdditionalManagersAsync_UsesPathResolver()
    {
        var resolver = new PackageManagerResolver(
            fileExists: _ => false,
            pathResolver: (name, _) => Task.FromResult<string?>($"/tools/{name}"));

        Assert.True((await resolver.ResolveScoopAsync(CancellationToken.None)).IsAvailable);
        Assert.True((await resolver.ResolvePipAsync(CancellationToken.None)).IsAvailable);
        Assert.True((await resolver.ResolveNpmAsync(CancellationToken.None)).IsAvailable);
        Assert.True((await resolver.ResolveDotNetToolAsync(CancellationToken.None)).IsAvailable);
        Assert.True((await resolver.ResolvePowerShellGalleryAsync(CancellationToken.None)).IsAvailable);
    }

    [Fact]
    public void BuildPackageOperation_UsesSourcePriorityAndMarksInstallsNonReversible()
    {
        var app = new CatalogApp
        {
            DisplayName = "Tool",
            WingetId = "Vendor.Tool",
            ScoopId = "tool",
            PackageSourcePriority = ["scoop", "winget"]
        };

        var operation = OperationDefinitionFactory.BuildPackageOperation(app, PackageAction.Install);

        Assert.Equal(RiskTier.Advanced, operation.RiskTier);
        Assert.False(operation.Reversible);
        var script = Assert.Single(operation.RunScripts).Script;
        Assert.Contains("scoop install 'tool'", script, StringComparison.OrdinalIgnoreCase);
        Assert.True(script.IndexOf("scoop install", StringComparison.OrdinalIgnoreCase) <
                    script.IndexOf("winget install", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Smoke_ManagerVersionCommands_RunWhenManagersArePresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolver = new PackageManagerResolver();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths);
        var runner = new ExternalProcessRunner(console, log);

        var winget = await resolver.ResolveWingetAsync(CancellationToken.None);
        if (winget.IsAvailable)
        {
            var result = await runner.RunAsync(new ExternalProcessRequest
            {
                OperationId = "store.smoke.winget",
                StepName = "version",
                FilePath = winget.ExecutablePath,
                Arguments = ["--version"],
                Timeout = TimeSpan.FromSeconds(20)
            }, CancellationToken.None);
            Assert.True(result.Success, $"winget --version failed: {result.Stderr}");
        }

        var choco = await resolver.ResolveChocolateyAsync(CancellationToken.None);
        if (choco.IsAvailable)
        {
            var result = await runner.RunAsync(new ExternalProcessRequest
            {
                OperationId = "store.smoke.choco",
                StepName = "version",
                FilePath = choco.ExecutablePath,
                Arguments = ["--version"],
                Timeout = TimeSpan.FromSeconds(20)
            }, CancellationToken.None);
            Assert.True(result.Success, $"choco --version failed: {result.Stderr}");
        }
    }
}
