using Phantom.Models;
using Phantom.Services;

namespace Phantom.Tests;

public sealed class OperationEngineTests
{
    [Fact]
    public async Task ExecuteBatchAsync_BlocksDestructiveOperation_WhenSettingDisabled()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = false,
            CreateRestorePointBeforeDangerousOperations = false
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(_ => new PowerShellExecutionResult { Success = true, ExitCode = 0 });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.destructive",
                    Title = "Destructive op",
                    Description = "test",
                    Destructive = true,
                    RiskTier = RiskTier.Advanced,
                    Reversible = false,
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'hi'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = settings.EnableDestructiveOperations,
            ForceDangerous = false,
            ConfirmDangerousAsync = _ => Task.FromResult(true)
        }, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.False(result.Results[0].Success);
        Assert.Contains("destructive operations disabled", result.Results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ExecuteBatchAsync_CreatesRestorePoint_ForDangerousOperation_WhenEnabled()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = true,
            CreateRestorePointBeforeDangerousOperations = true
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(_ => new PowerShellExecutionResult { Success = true, ExitCode = 0 });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.dangerous",
                    Title = "Dangerous op",
                    Description = "test",
                    RiskTier = RiskTier.Dangerous,
                    Reversible = false,
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = settings.EnableDestructiveOperations,
            ForceDangerous = true,
            ConfirmDangerousAsync = _ => Task.FromResult(true)
        }, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.True(result.Results[0].Success);
        Assert.Contains(runner.Requests, r => r.OperationId == "safety.restore-point");
        Assert.Contains(runner.Requests, r => r.OperationId == "op.dangerous");
    }

    private sealed class StubRunner : IPowerShellRunner
    {
        private readonly Func<PowerShellExecutionRequest, PowerShellExecutionResult> _handler;

        public StubRunner(Func<PowerShellExecutionRequest, PowerShellExecutionResult> handler)
        {
            _handler = handler;
        }

        public List<PowerShellExecutionRequest> Requests { get; } = new();

        public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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
