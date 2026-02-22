using Phantom.Models;
using Phantom.Services;

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
    }
}
