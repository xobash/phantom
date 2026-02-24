namespace Phantom.Models;

public enum RiskTier
{
    Basic,
    Advanced,
    Dangerous
}

public sealed class PowerShellStep
{
    public string Name { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public bool RequiresNetwork { get; set; }
}

public sealed class OperationDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public RiskTier RiskTier { get; set; } = RiskTier.Basic;
    public string Description { get; set; } = string.Empty;
    public bool Reversible { get; set; }
    public bool RequiresReboot { get; set; }
    public bool Destructive { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] Compatibility { get; set; } = Array.Empty<string>();
    public string[] StateCaptureKeys { get; set; } = Array.Empty<string>();
    public PowerShellStep[] StateCaptureScripts { get; set; } = Array.Empty<PowerShellStep>();
    public PowerShellStep[] RunScripts { get; set; } = Array.Empty<PowerShellStep>();
    public PowerShellStep[] UndoScripts { get; set; } = Array.Empty<PowerShellStep>();
    public string? DetectScript { get; set; }
}

public sealed class PrecheckResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;

    public static PrecheckResult Success(string message = "Precheck passed.") => new() { IsSuccess = true, Message = message };

    public static PrecheckResult Failure(string message) => new() { IsSuccess = false, Message = message };
}

public sealed class OperationExecutionResult
{
    public string OperationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool RequiresReboot { get; set; }
    public bool CaptureFailed { get; set; }
    public bool VerificationAttempted { get; set; }
    public bool VerificationPassed { get; set; }
    public string VerificationStatus { get; set; } = "Unknown";
    public string Message { get; set; } = string.Empty;
}

public sealed class PowerShellOutputEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Stream { get; set; } = "Info";
    public string Text { get; set; } = string.Empty;
}

public sealed class PowerShellExecutionRequest
{
    public string OperationId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool PreferProcessMode { get; set; }
    public bool SkipSafetyBackup { get; set; }
}

public sealed class PowerShellExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string CombinedOutput { get; set; } = string.Empty;
}

public sealed class OperationSelection
{
    public string Id { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

public sealed class AutomationConfig
{
    public bool ConfirmDangerous { get; set; }
    public string[] StoreSelections { get; set; } = Array.Empty<string>();
    public string[] Tweaks { get; set; } = Array.Empty<string>();
    public string[] Features { get; set; } = Array.Empty<string>();
    public string[] Fixes { get; set; } = Array.Empty<string>();
    public string UpdateMode { get; set; } = "Default";
}
