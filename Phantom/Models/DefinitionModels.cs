namespace Phantom.Models;

public sealed class CatalogApp
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
    public string ChocoId { get; set; } = string.Empty;
    public string? SilentArgs { get; set; }
    public string? Homepage { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool Selected { get; set; }
    public string Status { get; set; } = "Unknown";
}

public sealed class TweakDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskTier RiskTier { get; set; }
    public string Scope { get; set; } = "HKCU";
    public bool Reversible { get; set; }
    public string DetectScript { get; set; } = string.Empty;
    public string ApplyScript { get; set; } = string.Empty;
    public string UndoScript { get; set; } = string.Empty;
    public string[] StateCaptureKeys { get; set; } = Array.Empty<string>();
    public string[] Compatibility { get; set; } = Array.Empty<string>();
    public bool Destructive { get; set; }
    public bool Selected { get; set; }
    public string Status { get; set; } = "Unknown";
    public bool IsActionButton { get; set; }
    public string ActionButtonText { get; set; } = "Run";
}

public sealed class FeatureDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string[] Compatibility { get; set; } = Array.Empty<string>();
    public bool Selected { get; set; }
    public string Status { get; set; } = "Unknown";
}

public sealed class FixDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskTier RiskTier { get; set; } = RiskTier.Basic;
    public string ApplyScript { get; set; } = string.Empty;
    public string UndoScript { get; set; } = string.Empty;
    public string[] Compatibility { get; set; } = Array.Empty<string>();
    public bool Reversible { get; set; }
    public bool Destructive { get; set; }
    public bool Selected { get; set; }
}

public sealed class LegacyPanelDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LaunchScript { get; set; } = string.Empty;
}
