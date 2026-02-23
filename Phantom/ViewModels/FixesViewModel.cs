using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class FixesViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly IPowerShellRunner _runner;
    private readonly Func<AppSettings> _settingsAccessor;

    private string _search = string.Empty;

    public FixesViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        IPowerShellRunner runner,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _runner = runner;
        _settingsAccessor = settingsAccessor;

        Fixes = new ObservableCollection<FixDefinition>();
        LegacyPanels = new ObservableCollection<LegacyPanelDefinition>();

        FixesView = CollectionViewSource.GetDefaultView(Fixes);
        FixesView.Filter = FilterFixes;
        LegacyPanelsView = CollectionViewSource.GetDefaultView(LegacyPanels);
        LegacyPanelsView.Filter = FilterPanels;

        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync);
        UndoSelectedCommand = new AsyncRelayCommand(UndoSelectedAsync);
        LaunchPanelCommand = new AsyncRelayCommand<LegacyPanelDefinition>(LaunchPanelAsync);
    }

    public string Title => "Fixes";

    public ObservableCollection<FixDefinition> Fixes { get; }
    public ObservableCollection<LegacyPanelDefinition> LegacyPanels { get; }
    public ICollectionView FixesView { get; }
    public ICollectionView LegacyPanelsView { get; }

    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand UndoSelectedCommand { get; }
    public AsyncRelayCommand<LegacyPanelDefinition> LaunchPanelCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                FixesView.Refresh();
                LegacyPanelsView.Refresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var fixes = await _catalogService.LoadFixesAsync(cancellationToken).ConfigureAwait(false);
        var panels = await _catalogService.LoadLegacyPanelsAsync(cancellationToken).ConfigureAwait(false);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Fixes.Clear();
            foreach (var fix in fixes)
            {
                if (fix.Destructive && !_settingsAccessor().EnableDestructiveOperations)
                {
                    continue;
                }
                Fixes.Add(fix);
            }

            LegacyPanels.Clear();
            foreach (var panel in panels)
            {
                LegacyPanels.Add(panel);
            }
        });
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Fixes.Where(f => f.Selected).Select(ToOperation).ToList();
        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task UndoSelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Fixes.Where(f => f.Selected).Select(ToOperation).ToList();
        await RunOperationsAsync(operations, undo: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task LaunchPanelAsync(LegacyPanelDefinition? panel, CancellationToken cancellationToken)
    {
        if (panel is null)
        {
            return;
        }

        string launchScript;
        try
        {
            launchScript = PowerShellInputSanitizer.EnsureSafeLegacyLaunchScript(panel.LaunchScript, $"legacy panel '{panel.Id}'");
        }
        catch (ArgumentException ex)
        {
            _console.Publish("Error", ex.Message);
            return;
        }

        await _runner.ExecuteAsync(new PowerShellExecutionRequest
        {
            OperationId = $"panel.{panel.Id}",
            StepName = "launch",
            Script = launchScript,
            DryRun = false
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool undo, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
            _console.Publish("Info", "No fixes selected.");
            return;
        }

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
                DryRun = false,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = false,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            foreach (var op in batch.Results)
            {
                _console.Publish(op.Success ? "Info" : "Error", $"{op.OperationId}: {op.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Fix operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Fix operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private static OperationDefinition ToOperation(FixDefinition fix)
    {
        return new OperationDefinition
        {
            Id = $"fix.{fix.Id}",
            Title = fix.Name,
            Description = fix.Description,
            RiskTier = fix.RiskTier,
            Reversible = fix.Reversible,
            Destructive = fix.Destructive,
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "apply",
                    Script = fix.ApplyScript
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo",
                    Script = fix.UndoScript
                }
            ]
        };
    }

    private bool FilterFixes(object obj)
    {
        if (obj is not FixDefinition fix)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return fix.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               fix.Description.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterPanels(object obj)
    {
        if (obj is not LegacyPanelDefinition panel)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return panel.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               panel.Description.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }
}
