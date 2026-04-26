using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Phantom.Models;

public sealed class CatalogApp
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
    public string ChocoId { get; set; } = string.Empty;
    public string ScoopId { get; set; } = string.Empty;
    public string PipId { get; set; } = string.Empty;
    public string NpmId { get; set; } = string.Empty;
    public string DotNetToolId { get; set; } = string.Empty;
    public string PowerShellGalleryId { get; set; } = string.Empty;
    public string[] PackageSourcePriority { get; set; } = Array.Empty<string>();
    public string? SilentArgs { get; set; }
    public string? Homepage { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool ManualOnly { get; set; }
    public bool Selected { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SourceSummary { get; set; } = string.Empty;
    public string PurposeSummary { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
}

public sealed class TweakDefinition : INotifyPropertyChanged
{
    private bool _selected;
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

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
    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsActionButton { get; set; }
    public string ActionButtonText { get; set; } = "Run";

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class FeatureDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string[] Compatibility { get; set; } = Array.Empty<string>();
    public bool Selected { get; set; }
    public string Status { get; set; } = string.Empty;
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
