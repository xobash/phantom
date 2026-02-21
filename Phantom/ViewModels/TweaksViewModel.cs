using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class TweaksViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly Func<AppSettings> _settingsAccessor;

    private string _search = string.Empty;
    private bool _dryRun;

    public TweaksViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        PowerShellQueryService queryService,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _queryService = queryService;
        _settingsAccessor = settingsAccessor;

        Tweaks = new ObservableCollection<TweakDefinition>();
        TweaksView = CollectionViewSource.GetDefaultView(Tweaks);
        TweaksView.Filter = FilterTweaks;

        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync);
        UndoSelectedCommand = new AsyncRelayCommand(UndoSelectedAsync);
        ApplyBasicPresetCommand = new RelayCommand(() => ApplyPreset(RiskTier.Basic));
        ApplyAdvancedPresetCommand = new RelayCommand(() => ApplyPreset(RiskTier.Advanced));
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var tweak in Tweaks)
            {
                tweak.Selected = false;
            }
            Notify(nameof(Tweaks));
        });

        ExportSelectionCommand = new AsyncRelayCommand(ExportSelectionAsync);
        ImportSelectionCommand = new AsyncRelayCommand(ImportSelectionAsync);
    }

    public string Title => "Tweaks";

    public ObservableCollection<TweakDefinition> Tweaks { get; }
    public ICollectionView TweaksView { get; }

    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand UndoSelectedCommand { get; }
    public RelayCommand ApplyBasicPresetCommand { get; }
    public RelayCommand ApplyAdvancedPresetCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand ExportSelectionCommand { get; }
    public AsyncRelayCommand ImportSelectionCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                TweaksView.Refresh();
            }
        }
    }

    public bool DryRun
    {
        get => _dryRun;
        set => SetProperty(ref _dryRun, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var tweaks = await _catalogService.LoadTweaksAsync(cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Tweaks.Clear();
            foreach (var tweak in tweaks)
            {
                if (tweak.Destructive && !_settingsAccessor().EnableDestructiveOperations)
                {
                    continue;
                }
                Tweaks.Add(tweak);
            }
        });

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyPreset(RiskTier maxRisk)
    {
        foreach (var tweak in Tweaks)
        {
            tweak.Selected = tweak.RiskTier <= maxRisk;
        }

        Notify(nameof(Tweaks));
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        foreach (var tweak in Tweaks)
        {
            if (string.IsNullOrWhiteSpace(tweak.DetectScript))
            {
                tweak.Status = "Unknown";
                continue;
            }

            var result = await _queryService.InvokeAsync(tweak.DetectScript, cancellationToken).ConfigureAwait(false);
            var output = (result.Stdout + "\n" + result.Stderr).Trim();

            if (result.ExitCode == 0)
            {
                tweak.Status = string.IsNullOrWhiteSpace(output) ? "Detected" : output;
            }
            else
            {
                tweak.Status = DetectManagedRestriction(output) ? "Managed / Restricted" : "Error";
                _console.Publish("Error", $"{tweak.Name}: {output}");
            }
        }

        Notify(nameof(Tweaks));
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Tweaks.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No tweaks selected.");
            return;
        }

        var operations = selected.Select(BuildApplyOperation).ToList();
        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UndoSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Tweaks.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No tweaks selected.");
            return;
        }

        var operations = selected.Select(BuildApplyOperation).ToList();
        await RunOperationsAsync(operations, undo: true, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool undo, CancellationToken externalToken)
    {
        CancellationToken token;
        try
        {
            token = _executionCoordinator.Begin();
        }
        catch (InvalidOperationException ex)
        {
            _console.Publish("Warning", ex.Message);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, externalToken);

        try
        {
            var precheck = await _operationEngine.RunBatchPrecheckAsync(operations, linked.Token).ConfigureAwait(false);
            if (!precheck.IsSuccess)
            {
                _console.Publish("Error", precheck.Message);
                return;
            }

            var batch = await _operationEngine.ExecuteBatchAsync(new OperationRequest
            {
                Operations = operations,
                Undo = undo,
                DryRun = DryRun,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = false,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            foreach (var op in batch.Results)
            {
                _console.Publish(op.Success ? "Info" : "Error", $"{op.OperationId}: {op.Message}");
                var tweak = Tweaks.FirstOrDefault(x => x.Id == op.OperationId.Replace("tweak.", string.Empty, StringComparison.OrdinalIgnoreCase));
                if (tweak is not null)
                {
                    tweak.Status = op.Success ? (undo ? "Undone" : "Applied") : (DetectManagedRestriction(op.Message) ? "Managed / Restricted" : "Failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Tweak operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Tweak operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private OperationDefinition BuildApplyOperation(TweakDefinition tweak)
    {
        return new OperationDefinition
        {
            Id = $"tweak.{tweak.Id}",
            Title = tweak.Name,
            Description = tweak.Description,
            RiskTier = tweak.RiskTier,
            Reversible = tweak.Reversible,
            Destructive = tweak.Destructive,
            Tags = ["tweak", tweak.Scope],
            StateCaptureKeys = tweak.StateCaptureKeys,
            StateCaptureScripts = tweak.StateCaptureKeys.Select(key => new PowerShellStep
            {
                Name = key,
                Script = BuildCaptureScript(key)
            }).ToArray(),
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "apply",
                    Script = tweak.ApplyScript
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo",
                    Script = tweak.UndoScript
                }
            ]
        };
    }

    private static string BuildCaptureScript(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "$null";
        }

        var escaped = key.Replace("'", "''");
        return $"$p='{escaped}'; if (Test-Path $p) {{ Get-ItemProperty -Path $p | ConvertTo-Json -Depth 6 -Compress }} else {{ '' }}";
    }

    private bool FilterTweaks(object obj)
    {
        if (obj is not TweakDefinition tweak)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return tweak.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               tweak.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               tweak.Scope.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExportSelectionAsync(CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "tweaks.selection.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selected = Tweaks.Where(t => t.Selected).Select(t => t.Id).ToArray();
        var payload = JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(dialog.FileName, payload, cancellationToken).ConfigureAwait(false);
        _console.Publish("Info", $"Selection exported: {dialog.FileName}");
    }

    private async Task ImportSelectionAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var json = await File.ReadAllTextAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
        var selected = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);

        foreach (var tweak in Tweaks)
        {
            tweak.Selected = set.Contains(tweak.Id);
        }

        Notify(nameof(Tweaks));
        _console.Publish("Info", $"Selection imported: {dialog.FileName}");
    }

    private static bool DetectManagedRestriction(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("group policy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("managed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("restricted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("mdm", StringComparison.OrdinalIgnoreCase);
    }
}
