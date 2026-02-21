using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Phantom.Commands;
using Phantom.Models;
using Phantom.Services;

namespace Phantom.ViewModels;

public sealed class FeaturesViewModel : ObservableObject, ISectionViewModel
{
    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly IPowerShellRunner _runner;
    private readonly Func<AppSettings> _settingsAccessor;

    private string _search = string.Empty;

    public FeaturesViewModel(
        DefinitionCatalogService catalogService,
        OperationEngine operationEngine,
        ExecutionCoordinator executionCoordinator,
        IUserPromptService promptService,
        ConsoleStreamService console,
        PowerShellQueryService queryService,
        IPowerShellRunner runner,
        Func<AppSettings> settingsAccessor)
    {
        _catalogService = catalogService;
        _operationEngine = operationEngine;
        _executionCoordinator = executionCoordinator;
        _promptService = promptService;
        _console = console;
        _queryService = queryService;
        _runner = runner;
        _settingsAccessor = settingsAccessor;

        Features = new ObservableCollection<FeatureDefinition>();
        FeaturesView = CollectionViewSource.GetDefaultView(Features);
        FeaturesView.Filter = FilterFeature;

        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync);
        UndoSelectedCommand = new AsyncRelayCommand(UndoSelectedAsync);
    }

    public string Title => "Features";

    public ObservableCollection<FeatureDefinition> Features { get; }
    public ICollectionView FeaturesView { get; }

    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand UndoSelectedCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                FeaturesView.Refresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var features = await _catalogService.LoadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Features.Clear();
            foreach (var feature in features)
            {
                Features.Add(feature);
            }
        });

        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        foreach (var feature in Features)
        {
            var script = $"$f=Get-WindowsOptionalFeature -Online -FeatureName '{feature.FeatureName}' -ErrorAction SilentlyContinue; if($f){{$f.State}} else {{'Unknown'}}";
            var result = await _queryService.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
            feature.Status = result.ExitCode == 0 ? result.Stdout.Trim() : "Managed / Restricted";
        }

        Notify(nameof(Features));
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Features
            .Where(f => f.Selected)
            .Select(f => new OperationDefinition
            {
                Id = $"feature.{f.Id}",
                Title = $"Enable {f.Name}",
                Description = f.Description,
                RiskTier = RiskTier.Advanced,
                Reversible = true,
                RequiresReboot = true,
                RunScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -All -NoRestart -ErrorAction Stop" }],
                UndoScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -NoRestart -ErrorAction Stop" }]
            })
            .ToList();

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UndoSelectedAsync(CancellationToken cancellationToken)
    {
        var operations = Features
            .Where(f => f.Selected)
            .Select(f => new OperationDefinition
            {
                Id = $"feature.{f.Id}",
                Title = $"Disable {f.Name}",
                Description = f.Description,
                RiskTier = RiskTier.Advanced,
                Reversible = true,
                RequiresReboot = true,
                RunScripts = [new PowerShellStep { Name = "disable", Script = $"Disable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -NoRestart -ErrorAction Stop" }],
                UndoScripts = [new PowerShellStep { Name = "enable", Script = $"Enable-WindowsOptionalFeature -Online -FeatureName '{f.FeatureName}' -All -NoRestart -ErrorAction Stop" }]
            })
            .ToList();

        await RunOperationsAsync(operations, undo: false, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool undo, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
            _console.Publish("Info", "No features selected.");
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

            if (batch.RequiresReboot)
            {
                var rebootNow = await _promptService.PromptRebootAsync().ConfigureAwait(false);
                if (rebootNow)
                {
                    await _runner.ExecuteAsync(new PowerShellExecutionRequest
                    {
                        OperationId = "system.reboot",
                        StepName = "restart",
                        Script = "Restart-Computer -Force",
                        DryRun = false
                    }, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    _console.Publish("Info", "Reboot postponed by user choice.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _console.Publish("Warning", "Feature operation cancelled.");
        }
        catch (Exception ex)
        {
            _console.Publish("Error", $"Feature operation failed: {ex.Message}");
        }
        finally
        {
            _executionCoordinator.Complete();
        }
    }

    private bool FilterFeature(object obj)
    {
        if (obj is not FeatureDefinition feature)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        return feature.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               feature.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               feature.FeatureName.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }
}
