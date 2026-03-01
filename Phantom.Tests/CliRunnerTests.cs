using Phantom.Models;
using Phantom.Services;
using System.Reflection;

namespace Phantom.Tests;

public sealed class CliRunnerTests
{
    [Fact]
    public async Task RunAsync_BlocksRelativePathTraversal_ForConfigPath()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var settings = new AppSettings();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var settingsStore = new SettingsStore(new JsonFileStore(), paths);
        await settingsStore.SaveAsync(settings, CancellationToken.None);

        var runner = new StubRunner(_ => new PowerShellExecutionResult { Success = true, ExitCode = 0 });
        var engine = new OperationEngine(
            runner,
            new UndoStateStore(new JsonFileStore(), paths),
            new NetworkGuardService(),
            console,
            log,
            () => settings);
        var query = new PowerShellQueryService(console, log);
        var cli = new CliRunner(
            paths,
            new DefinitionCatalogService(paths),
            engine,
            console,
            log,
            new NetworkGuardService(),
            query,
            settingsStore);

        var exitCode = await cli.RunAsync(@"..\..\outside.json", forceDangerous: false, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(console.Snapshot, e =>
            string.Equals(e.Stream, "Error", StringComparison.OrdinalIgnoreCase) &&
            e.Text.Contains("Path traversal detected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildOperationsAsync_UsesStateBackupAndNonDestructiveUndo_ForUpdateModes()
    {
        var cli = await CreateCliRunnerAsync();
        var method = typeof(CliRunner).GetMethod("BuildOperationsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var disableConfig = new AutomationConfig { UpdateMode = "Disable All" };
        var disableTask = method!.Invoke(cli, [disableConfig, CancellationToken.None]) as Task<List<OperationDefinition>>;
        Assert.NotNull(disableTask);
        var disableOps = await disableTask!;
        var disable = Assert.Single(disableOps, o => o.Id == "updates.mode.disableall");

        var disableRun = Assert.Single(disable.RunScripts).Script;
        var disableUndo = Assert.Single(disable.UndoScripts).Script;
        Assert.Contains("windows-update-service-modes.json", disableRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RegistryView]::Registry64", disableRun, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Recurse -Force", disableUndo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-RegistryValue64", disableUndo, StringComparison.OrdinalIgnoreCase);

        var securityConfig = new AutomationConfig { UpdateMode = "Security" };
        var securityTask = method.Invoke(cli, [securityConfig, CancellationToken.None]) as Task<List<OperationDefinition>>;
        Assert.NotNull(securityTask);
        var securityOps = await securityTask!;
        var security = Assert.Single(securityOps, o => o.Id == "updates.mode.security");

        var securityUndo = Assert.Single(security.UndoScripts).Script;
        Assert.DoesNotContain("Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate' -Recurse -Force", securityUndo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-RegistrySubKeyIfEmpty64", securityUndo, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CliRunner> CreateCliRunnerAsync()
    {
        var paths = TestHelpers.CreateIsolatedPaths();
        var settings = new AppSettings();
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var settingsStore = new SettingsStore(new JsonFileStore(), paths);
        await settingsStore.SaveAsync(settings, CancellationToken.None);

        var runner = new StubRunner(_ => new PowerShellExecutionResult { Success = true, ExitCode = 0 });
        var engine = new OperationEngine(
            runner,
            new UndoStateStore(new JsonFileStore(), paths),
            new NetworkGuardService(),
            console,
            log,
            () => settings);
        var query = new PowerShellQueryService(console, log);
        return new CliRunner(
            paths,
            new DefinitionCatalogService(paths),
            engine,
            console,
            log,
            new NetworkGuardService(),
            query,
            settingsStore);
    }

    private sealed class StubRunner : IPowerShellRunner
    {
        private readonly Func<PowerShellExecutionRequest, PowerShellExecutionResult> _handler;

        public StubRunner(Func<PowerShellExecutionRequest, PowerShellExecutionResult> handler)
        {
            _handler = handler;
        }

        public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }

        public Task<BackupCompensationResult> TryCompensateFromSafetyBackupsAsync(string operationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new BackupCompensationResult
            {
                Attempted = false,
                Success = false,
                Message = "stub"
            });
        }
    }
}
