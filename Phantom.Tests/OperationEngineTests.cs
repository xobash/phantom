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

    [Fact]
    public async Task ExecuteBatchAsync_ForceDangerousPrompt_IncludesOperationContext_WhenDetectFails()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = true,
            CreateRestorePointBeforeDangerousOperations = false
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(request =>
        {
            if (string.Equals(request.StepName, "detect", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "Detect script failed" };
            }

            return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
        });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var capturedPrompt = string.Empty;
        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.verify.prompt",
                    Title = "Verification Prompt Test",
                    Description = "test",
                    RiskTier = RiskTier.Dangerous,
                    Reversible = true,
                    DetectScript = "Write-Output 'PHANTOM_STATUS=Applied'",
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply'" }],
                    UndoScripts = [new PowerShellStep { Name = "undo", Script = "Write-Output 'undo'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = settings.EnableDestructiveOperations,
            ForceDangerous = true,
            InteractiveDangerousPrompt = true,
            ConfirmDangerousAsync = prompt =>
            {
                capturedPrompt = prompt;
                return Task.FromResult(false);
            }
        }, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.True(result.Results[0].Cancelled);
        Assert.Contains("op.verify.prompt", capturedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verification Prompt Test", capturedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Risk: Dangerous", capturedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Detect script failed", capturedPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteBatchAsync_DoesNotBypassRestorePoint_WhenForceDangerousEnabled()
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
        var runner = new StubRunner(request =>
        {
            if (request.OperationId == "safety.restore-point")
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "restore point unavailable" };
            }

            return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
        });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.dangerous.restorepoint",
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
        Assert.False(result.Results[0].Success);
        Assert.Contains("restore point creation failed", result.Results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, r => r.OperationId == "op.dangerous.restorepoint");
    }

    [Fact]
    public async Task ExecuteBatchAsync_BlocksReversibleOperation_WhenStateCaptureFails_WithoutSkipOverride()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = true,
            CreateRestorePointBeforeDangerousOperations = false
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(request =>
        {
            if (request.StepName.StartsWith("capture:", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "capture failed" };
            }

            return new PowerShellExecutionResult { Success = true, ExitCode = 0 };
        });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.capture.guard",
                    Title = "Capture guard test",
                    Description = "test",
                    RiskTier = RiskTier.Advanced,
                    Reversible = true,
                    StateCaptureScripts = [new PowerShellStep { Name = "state", Script = "Write-Output 'capture'" }],
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply'" }],
                    UndoScripts = [new PowerShellStep { Name = "undo", Script = "Write-Output 'undo'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = true,
            ForceDangerous = true,
            SkipCaptureCheck = false,
            ConfirmDangerousAsync = _ => Task.FromResult(true)
        }, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.False(result.Results[0].Success);
        Assert.Contains("state capture failed", result.Results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request =>
            string.Equals(request.OperationId, "op.capture.guard", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.StepName, "apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteBatchAsync_AllowsReversibleOperation_WhenSkipCaptureOverrideIsEnabled()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = true,
            CreateRestorePointBeforeDangerousOperations = false
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(request =>
        {
            if (request.StepName.StartsWith("capture:", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "capture failed" };
            }

            return new PowerShellExecutionResult { Success = true, ExitCode = 0, CombinedOutput = "ok" };
        });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.capture.override",
                    Title = "Capture override test",
                    Description = "test",
                    RiskTier = RiskTier.Advanced,
                    Reversible = true,
                    StateCaptureScripts = [new PowerShellStep { Name = "state", Script = "Write-Output 'capture'" }],
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply'" }],
                    UndoScripts = [new PowerShellStep { Name = "undo", Script = "Write-Output 'undo'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = true,
            ForceDangerous = true,
            SkipCaptureCheck = true,
            ConfirmDangerousAsync = _ => Task.FromResult(true)
        }, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.True(result.Results[0].Success);
        Assert.Contains(runner.Requests, request =>
            string.Equals(request.OperationId, "op.capture.override", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.StepName, "apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteBatchAsync_ContinuesRollbackUndoSteps_AfterUndoFailure()
    {
        var settings = new AppSettings
        {
            EnableDestructiveOperations = true,
            CreateRestorePointBeforeDangerousOperations = false
        };

        var paths = TestHelpers.CreateIsolatedPaths();
        var undoStore = new UndoStateStore(new JsonFileStore(), paths);
        var console = new ConsoleStreamService();
        var log = TestHelpers.CreateLogService(paths, () => settings);
        var runner = new StubRunner(request =>
        {
            if (string.Equals(request.OperationId, "op.second", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.StepName, "apply", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "fail" };
            }

            if (string.Equals(request.OperationId, "op.first.rollback", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.StepName, "undo-1", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerShellExecutionResult { Success = false, ExitCode = 1, CombinedOutput = "undo fail" };
            }

            return new PowerShellExecutionResult { Success = true, ExitCode = 0, CombinedOutput = "ok" };
        });
        var engine = new OperationEngine(runner, undoStore, new NetworkGuardService(), console, log, () => settings);

        var result = await engine.ExecuteBatchAsync(new OperationRequest
        {
            Operations =
            [
                new OperationDefinition
                {
                    Id = "op.first",
                    Title = "first",
                    Description = "first",
                    RiskTier = RiskTier.Basic,
                    Reversible = true,
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply 1'" }],
                    UndoScripts =
                    [
                        new PowerShellStep { Name = "undo-1", Script = "Write-Output 'undo 1'" },
                        new PowerShellStep { Name = "undo-2", Script = "Write-Output 'undo 2'" }
                    ]
                },
                new OperationDefinition
                {
                    Id = "op.second",
                    Title = "second",
                    Description = "second",
                    RiskTier = RiskTier.Basic,
                    Reversible = true,
                    RunScripts = [new PowerShellStep { Name = "apply", Script = "Write-Output 'apply 2'" }],
                    UndoScripts = [new PowerShellStep { Name = "undo", Script = "Write-Output 'undo second'" }]
                }
            ],
            Undo = false,
            DryRun = false,
            EnableDestructiveOperations = true,
            ForceDangerous = false,
            SkipCaptureCheck = false,
            ConfirmDangerousAsync = _ => Task.FromResult(true)
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(runner.Requests, request =>
            string.Equals(request.OperationId, "op.first.rollback", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.StepName, "undo-1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runner.Requests, request =>
            string.Equals(request.OperationId, "op.first.rollback", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.StepName, "undo-2", StringComparison.OrdinalIgnoreCase));
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
