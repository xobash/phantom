using Phantom.Services;

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
    public void BuildWingetInstallArguments_IncludesRequiredNonInteractiveFlags()
    {
        var args = StoreCommandBuilder.BuildWingetInstallArguments("Microsoft.VisualStudioCode", ["--scope", "machine"]);

        Assert.Equal("install", args[0]);
        Assert.Contains("--id", args);
        Assert.Contains("Microsoft.VisualStudioCode", args);
        Assert.Contains("--exact", args);
        Assert.Contains("--accept-package-agreements", args);
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.Contains("--scope", args);
        Assert.Contains("machine", args);
    }

    [Fact]
    public void BuildChocoInstallArguments_IncludesRequiredFlags()
    {
        var args = StoreCommandBuilder.BuildChocoInstallArguments("7zip", ["--ignore-checksums"]);

        Assert.Equal("install", args[0]);
        Assert.Contains("7zip", args);
        Assert.Contains("-y", args);
        Assert.Contains("--no-progress", args);
        Assert.Contains("--ignore-checksums", args);
    }

    [Fact]
    public void DescribeWingetExitCode_ReturnsMappedMessageForKnownCode()
    {
        var message = StoreCommandBuilder.DescribeWingetExitCode(-1978335212);
        Assert.Contains("No package found", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputContainsWingetPackage_DetectsPackageInListOutput()
    {
        var output = """
                     Name                  Id                           Version
                     --------------------------------------------------------------
                     Visual Studio Code    Microsoft.VisualStudioCode   1.99.0
                     """;
        Assert.True(StoreCommandBuilder.OutputContainsWingetPackage("Microsoft.VisualStudioCode", output));
    }

    [Fact]
    public void OutputContainsChocoPackage_DetectsPackageInLimitOutput()
    {
        var output = """
                     7zip|24.09
                     git|2.49.0
                     """;
        Assert.True(StoreCommandBuilder.OutputContainsChocoPackage("7zip", output));
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
