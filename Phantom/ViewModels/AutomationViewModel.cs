using System.Text;
using Microsoft.Win32;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class AutomationViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly StoreViewModel _store;
    private readonly TweaksViewModel _tweaks;
    private readonly FeaturesViewModel _features;
    private readonly FixesViewModel _fixes;
    private readonly UpdatesViewModel _updates;

    private string _previewText = "No config loaded.";

    public AutomationViewModel(
        DefinitionCatalogService catalogService,
        StoreViewModel store,
        TweaksViewModel tweaks,
        FeaturesViewModel features,
        FixesViewModel fixes,
        UpdatesViewModel updates)
    {
        _catalogService = catalogService;
        _store = store;
        _tweaks = tweaks;
        _features = features;
        _fixes = fixes;
        _updates = updates;

        ExportConfigCommand = new AsyncRelayCommand(ExportConfigAsync);
        ImportConfigCommand = new AsyncRelayCommand(ImportConfigAsync);
    }

    public string Title => "Automation";

    public AsyncRelayCommand ExportConfigCommand { get; }
    public AsyncRelayCommand ImportConfigCommand { get; }

    public string PreviewText
    {
        get => _previewText;
        set => SetProperty(ref _previewText, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task ExportConfigAsync(CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "automation-config.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var config = BuildCurrentConfig();
        await _catalogService.SaveSelectionConfigAsync(dialog.FileName, config, cancellationToken).ConfigureAwait(false);
        PreviewText = BuildPreview(config);
    }

    private async Task ImportConfigAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var config = await _catalogService.LoadSelectionConfigAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
        PreviewText = BuildPreview(config);
    }

    public AutomationConfig BuildCurrentConfig()
    {
        return new AutomationConfig
        {
            ConfirmDangerous = false,
            StoreSelections = _store.Catalog.Where(x => x.Selected).Select(x => x.DisplayName).ToArray(),
            Tweaks = _tweaks.Tweaks.Where(x => x.Selected).Select(x => x.Id).ToArray(),
            Features = _features.Features.Where(x => x.Selected).Select(x => x.Id).ToArray(),
            Fixes = _fixes.Fixes.Where(x => x.Selected).Select(x => x.Id).ToArray(),
            UpdateMode = _updates.SelectedMode
        };
    }

    private static string BuildPreview(AutomationConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Automation preview:");
        sb.AppendLine($"- Confirm dangerous in config: {config.ConfirmDangerous}");
        sb.AppendLine($"- Store apps: {string.Join(", ", config.StoreSelections)}");
        sb.AppendLine($"- Tweaks: {string.Join(", ", config.Tweaks)}");
        sb.AppendLine($"- Features: {string.Join(", ", config.Features)}");
        sb.AppendLine($"- Fixes: {string.Join(", ", config.Fixes)}");
        sb.AppendLine($"- Update mode: {config.UpdateMode}");
        sb.AppendLine("CLI: Phantom.exe -Config <path> -Run [-ForceDangerous]");
        return sb.ToString();
    }
}
