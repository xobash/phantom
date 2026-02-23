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

public sealed class TweaksViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Description)> SectionMetadata =
        new Dictionary<string, (string Title, string Description)>(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop"] = ("Desktop", "Desktop and shell appearance behavior."),
            ["startmenu"] = ("Start menu", "Search, suggestions, and start menu behavior."),
            ["fileexplorer"] = ("File Explorer", "Explorer visibility and interaction options."),
            ["privacy"] = ("Privacy", "Telemetry, background activity, and data sharing options."),
            ["ads"] = ("Ads", "Recommendations, tips, and promotional content options."),
            ["system"] = ("System", "Performance, power, and core platform behavior."),
            ["superuser"] = ("Superuser", "Advanced tweaks that may carry operational risk.")
        };

    private static readonly string[] SectionOrder =
    [
        "desktop",
        "startmenu",
        "fileexplorer",
        "privacy",
        "ads",
        "system",
        "superuser"
    ];

    private static readonly IReadOnlyDictionary<string, string> TweakSectionById =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["show-file-extensions"] = "fileexplorer",
            ["show-hidden-files"] = "fileexplorer",
            ["disable-web-search"] = "startmenu",
            ["disable-start-recommendations"] = "startmenu",
            ["hide-search-button-taskbar"] = "startmenu",
            ["hide-task-view-button"] = "startmenu",
            ["revert-new-start-menu"] = "startmenu",
            ["disable-telemetry"] = "privacy",
            ["disable-background-apps"] = "privacy",
            ["disable-delivery-optimization"] = "privacy",
            ["disable-copilot"] = "privacy",
            ["disable-location-tracking"] = "privacy",
            ["disable-activity-history"] = "privacy",
            ["disable-powershell7-telemetry"] = "privacy",
            ["disable-notification-tray-calendar"] = "privacy",
            ["disable-consumer-features"] = "ads",
            ["disable-windows-tips"] = "ads",
            ["remove-widgets"] = "ads",
            ["disable-gamedvr"] = "system",
            ["high-performance-plan"] = "system",
            ["add-ultimate-performance-profile"] = "system",
            ["remove-ultimate-performance-profile"] = "system",
            ["disable-hibernation"] = "system",
            ["center-taskbar-items"] = "desktop",
            ["disable-cross-device-resume"] = "desktop",
            ["dark-theme-windows"] = "desktop",
            ["detailed-bsod"] = "desktop",
            ["disable-multiplane-overlay"] = "desktop",
            ["disable-mouse-acceleration"] = "desktop",
            ["disable-new-outlook"] = "desktop",
            ["numlock-on-startup"] = "desktop",
            ["remove-settings-home-page"] = "desktop",
            ["enable-s3-sleep"] = "desktop",
            ["disable-sticky-keys"] = "desktop",
            ["verbose-messages-during-logon"] = "desktop",
            ["disable-explorer-auto-folder-discovery"] = "fileexplorer",
            ["remove-gallery-from-explorer"] = "fileexplorer",
            ["remove-home-from-explorer"] = "fileexplorer",
            ["set-classic-right-click-menu"] = "fileexplorer",
            ["set-display-for-performance"] = "system",
            ["set-time-to-utc-dualboot"] = "system",
            ["disable-storage-sense"] = "system",
            ["disable-teredo"] = "system",
            ["disable-fullscreen-optimizations"] = "system",
            ["disable-ipv6"] = "system",
            ["prefer-ipv4-over-ipv6"] = "system",
            ["set-services-to-manual"] = "system",
            ["create-restore-point"] = "system",
            ["delete-temporary-files"] = "system",
            ["run-disk-cleanup"] = "system",
            ["disable-wpbt"] = "system",
            ["enable-end-task-with-right-click"] = "system",
            ["edge-debloat"] = "superuser",
            ["brave-debloat"] = "superuser",
            ["adobe-network-block"] = "superuser",
            ["block-razer-software-installs"] = "superuser",
            ["disable-smb1"] = "superuser",
            ["remove-onedrive"] = "superuser",
            ["remove-edge"] = "superuser",
            ["remove-all-ms-store-apps"] = "superuser",
            ["remove-xbox-gaming-components"] = "superuser"
        };

    private static readonly string[] DefaultDnsProfiles =
    [
        "Default",
        "DHCP",
        "Google",
        "Cloudflare",
        "Cloudflare_Malware",
        "Cloudflare_Malware_Adult",
        "Open_DNS",
        "Quad9",
        "AdGuard_Ads_Trackers",
        "AdGuard_Ads_Trackers_Malware_Adult"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> DnsServersByProfile =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Default"] = [],
            ["DHCP"] = [],
            ["Google"] = ["8.8.8.8", "8.8.4.4"],
            ["Cloudflare"] = ["1.1.1.1", "1.0.0.1"],
            ["Cloudflare_Malware"] = ["1.1.1.2", "1.0.0.2"],
            ["Cloudflare_Malware_Adult"] = ["1.1.1.3", "1.0.0.3"],
            ["Open_DNS"] = ["208.67.222.222", "208.67.220.220"],
            ["Quad9"] = ["9.9.9.9", "149.112.112.112"],
            ["AdGuard_Ads_Trackers"] = ["94.140.14.14", "94.140.15.15"],
            ["AdGuard_Ads_Trackers_Malware_Adult"] = ["94.140.14.15", "94.140.15.16"]
        };

    private readonly DefinitionCatalogService _catalogService;
    private readonly OperationEngine _operationEngine;
    private readonly ExecutionCoordinator _executionCoordinator;
    private readonly IUserPromptService _promptService;
    private readonly ConsoleStreamService _console;
    private readonly PowerShellQueryService _queryService;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly SemaphoreSlim _toggleApplyLock = new(1, 1);
    private readonly Dictionary<string, bool> _sectionExpansionState = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private string _search = string.Empty;
    private bool _dryRun;
    private string _selectedDnsProfile = DefaultDnsProfiles[0];

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
        Sections = new ObservableCollection<TweakSection>();

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

            RefreshTweaksView();
        });

        ExportSelectionCommand = new AsyncRelayCommand(ExportSelectionAsync);
        ImportSelectionCommand = new AsyncRelayCommand(ImportSelectionAsync);
        RunOoShutUp10Command = new AsyncRelayCommand(RunOoShutUp10Async);
        ApplyDnsProfileCommand = new AsyncRelayCommand(ApplyDnsProfileAsync);

        DnsProfiles = new ObservableCollection<string>(DefaultDnsProfiles);
    }

    public string Title => "Tweaks";

    public ObservableCollection<TweakDefinition> Tweaks { get; }
    public ObservableCollection<TweakSection> Sections { get; }
    public ICollectionView TweaksView { get; }

    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand UndoSelectedCommand { get; }
    public RelayCommand ApplyBasicPresetCommand { get; }
    public RelayCommand ApplyAdvancedPresetCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand ExportSelectionCommand { get; }
    public AsyncRelayCommand ImportSelectionCommand { get; }
    public AsyncRelayCommand RunOoShutUp10Command { get; }
    public AsyncRelayCommand ApplyDnsProfileCommand { get; }
    public ObservableCollection<string> DnsProfiles { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                RefreshTweaksView();
            }
        }
    }

    public bool DryRun
    {
        get => _dryRun;
        set => SetProperty(ref _dryRun, value);
    }

    public string SelectedDnsProfile
    {
        get => _selectedDnsProfile;
        set => SetProperty(ref _selectedDnsProfile, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var tweaks = await _catalogService.LoadTweaksAsync(cancellationToken).ConfigureAwait(false);
        MergeRequestedTweaks(tweaks);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Tweaks.Clear();
            foreach (var tweak in tweaks)
            {
                if (tweak.Destructive && !_settingsAccessor().EnableDestructiveOperations)
                {
                    continue;
                }

                tweak.Scope = string.IsNullOrWhiteSpace(tweak.Scope) ? "System" : tweak.Scope;
                Tweaks.Add(tweak);
            }
        });

        RefreshTweaksView();
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void MergeRequestedTweaks(List<TweakDefinition> tweaks)
    {
        var lookup = tweaks.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var requested in RequestedTweaksCatalog.CreateRequestedTweaks())
        {
            if (lookup.ContainsKey(requested.Id))
            {
                continue;
            }

            tweaks.Add(requested);
            lookup[requested.Id] = requested;
        }
    }

    public async Task ApplyToggleFromUiAsync(TweakDefinition? tweak, bool enabled, CancellationToken cancellationToken)
    {
        if (tweak is null)
        {
            return;
        }

        await _toggleApplyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsTweakInDesiredStateAsync(tweak, enabled, cancellationToken).ConfigureAwait(false))
            {
                tweak.Status = enabled ? "Applied" : "Not Applied";
                tweak.Selected = enabled;
                RefreshTweaksView();
                _console.Publish("Info", $"{tweak.Name}: already in desired state, skipping.");
                return;
            }

            tweak.Status = enabled ? "Applying..." : "Undoing...";
            RefreshTweaksView();

            var operation = BuildApplyOperation(tweak);
            await RunOperationsAsync([operation], undo: !enabled, cancellationToken).ConfigureAwait(false);
            await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _toggleApplyLock.Release();
        }
    }

    private void ApplyPreset(RiskTier maxRisk)
    {
        foreach (var tweak in Tweaks)
        {
            tweak.Selected = tweak.RiskTier <= maxRisk;
        }

        RefreshTweaksView();
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var tweakRows = await Application.Current.Dispatcher.InvokeAsync(() => Tweaks.ToList());
        var updates = new List<(string Id, string Status, bool Selected)>(tweakRows.Count);

        foreach (var tweak in tweakRows)
        {
            if (string.IsNullOrWhiteSpace(tweak.DetectScript))
            {
                updates.Add((tweak.Id, "Unknown", false));
                continue;
            }

            var result = await _queryService.InvokeAsync(tweak.DetectScript, cancellationToken, echoToConsole: false).ConfigureAwait(false);
            var output = (result.Stdout + "\n" + result.Stderr).Trim();

            if (result.ExitCode == 0)
            {
                var status = string.IsNullOrWhiteSpace(output) ? "Detected" : output;
                updates.Add((tweak.Id, status, IsAppliedStatus(status)));
            }
            else
            {
                if (DetectMissingSettingState(output))
                {
                    updates.Add((tweak.Id, "Not Applied", false));
                    continue;
                }

                var status = DetectManagedRestriction(output) ? "Managed / Restricted" : "Error";
                updates.Add((tweak.Id, status, false));
                _console.Publish("Error", $"{tweak.Name}: {output}");
            }
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var update in updates)
            {
                var tweak = Tweaks.FirstOrDefault(x => string.Equals(x.Id, update.Id, StringComparison.OrdinalIgnoreCase));
                if (tweak is null)
                {
                    continue;
                }

                tweak.Status = update.Status;
                tweak.Selected = update.Selected;
            }

            RefreshTweaksView();
        });
    }

    private async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var selected = Tweaks.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            _console.Publish("Info", "No tweaks selected.");
            return;
        }

        var operations = await BuildOperationsForDesiredStateAsync(selected, undo: false, cancellationToken).ConfigureAwait(false);
        if (operations.Count == 0)
        {
            _console.Publish("Info", "Selected tweaks are already applied.");
            return;
        }

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

        var operations = await BuildOperationsForDesiredStateAsync(selected, undo: true, cancellationToken).ConfigureAwait(false);
        if (operations.Count == 0)
        {
            _console.Publish("Info", "Selected tweaks are already not applied.");
            return;
        }

        await RunOperationsAsync(operations, undo: true, cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOoShutUp10Async(CancellationToken cancellationToken)
    {
        const string script = """
                              $downloadUrl='https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe'
                              $toolRoot=Join-Path $env:TEMP 'Phantom\Tools'
                              $exePath=Join-Path $toolRoot 'OOSU10.exe'
                              New-Item -Path $toolRoot -ItemType Directory -Force | Out-Null
                              Invoke-WebRequest -Uri $downloadUrl -OutFile $exePath -UseBasicParsing -ErrorAction Stop
                              if(-not (Test-Path $exePath)){ throw 'O&O ShutUp10 download failed.' }
                              Start-Process -FilePath $exePath
                              Write-Output "O&O ShutUp10 launched from $exePath"
                              """;

        var operation = new OperationDefinition
        {
            Id = "tweak.run-oo-shutup10",
            Title = "Run O&O ShutUp10",
            Description = "Downloads and launches O&O ShutUp10 from official source.",
            RiskTier = RiskTier.Dangerous,
            Reversible = false,
            Destructive = false,
            Tags = ["tweak", "utility"],
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "download-run",
                    Script = script,
                    RequiresNetwork = true
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo",
                    Script = "Write-Output 'No undo action for O&O ShutUp10 execution.'"
                }
            ]
        };

        await RunOperationsAsync([operation], undo: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDnsProfileAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedDnsProfile) || !DnsServersByProfile.ContainsKey(SelectedDnsProfile))
        {
            _console.Publish("Warning", "Select a valid DNS profile first.");
            return;
        }

        var profile = SelectedDnsProfile;
        var script = BuildDnsApplyScript(profile);
        var operation = new OperationDefinition
        {
            Id = $"tweak.dns.{profile.ToLowerInvariant()}",
            Title = $"Set DNS: {profile}",
            Description = $"Applies DNS profile '{profile}' to active adapter.",
            RiskTier = RiskTier.Advanced,
            Reversible = false,
            Destructive = false,
            Tags = ["tweak", "network", "dns"],
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = "set-dns",
                    Script = script
                }
            ],
            UndoScripts =
            [
                new PowerShellStep
                {
                    Name = "undo",
                    Script = "Write-Output 'Use DNS dropdown and apply Default/DHCP to revert.'"
                }
            ]
        };

        await RunOperationsAsync([operation], undo: false, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildDnsApplyScript(string profile)
    {
        var escapedProfile = profile.Replace("'", "''");
        var adapterScript = "$adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Sort-Object -Property InterfaceMetric | Select-Object -First 1; " +
                            "if ($null -eq $adapter) { $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1 }; " +
                            "if ($null -eq $adapter) { throw 'No active network adapter found.' }; ";

        if (!DnsServersByProfile.TryGetValue(profile, out var servers))
        {
            throw new InvalidOperationException($"Unsupported DNS profile: {profile}");
        }

        if (servers.Length == 0)
        {
            return adapterScript +
                   "Set-DnsClientServerAddress -InterfaceAlias $adapter.Name -ResetServerAddresses -ErrorAction Stop; " +
                   $"Write-Output 'Applied DNS profile {escapedProfile} to adapter: ' + $adapter.Name";
        }

        var addresses = string.Join(", ", servers.Select(x => $"'{x}'"));
        return adapterScript +
               $"$servers=@({addresses}); " +
               "Set-DnsClientServerAddress -InterfaceAlias $adapter.Name -ServerAddresses $servers -ErrorAction Stop; " +
               $"Write-Output 'Applied DNS profile {escapedProfile} to adapter: ' + $adapter.Name";
    }

    private async Task<List<OperationDefinition>> BuildOperationsForDesiredStateAsync(
        IReadOnlyList<TweakDefinition> selected,
        bool undo,
        CancellationToken cancellationToken)
    {
        var operations = new List<OperationDefinition>(selected.Count);
        foreach (var tweak in selected)
        {
            var desiredApplied = !undo;
            if (await IsTweakInDesiredStateAsync(tweak, desiredApplied, cancellationToken).ConfigureAwait(false))
            {
                _console.Publish("Info", $"{tweak.Name}: already {(desiredApplied ? "applied" : "not applied")}, skipping.");
                continue;
            }

            operations.Add(BuildApplyOperation(tweak));
        }

        return operations;
    }

    private async Task RunOperationsAsync(IReadOnlyList<OperationDefinition> operations, bool undo, CancellationToken externalToken)
    {
        if (operations.Count == 0)
        {
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
                DryRun = DryRun,
                EnableDestructiveOperations = _settingsAccessor().EnableDestructiveOperations,
                ForceDangerous = false,
                ConfirmDangerousAsync = _promptService.ConfirmDangerousAsync
            }, linked.Token).ConfigureAwait(false);

            var statusUpdates = new List<(string Id, string Status)>();
            foreach (var op in batch.Results)
            {
                _console.Publish(op.Success ? "Info" : "Error", $"{op.OperationId}: {op.Message}");
                var tweakId = op.OperationId.Replace("tweak.", string.Empty, StringComparison.OrdinalIgnoreCase);
                var status = op.Success ? (undo ? "Undone" : "Applied") : (DetectManagedRestriction(op.Message) ? "Managed / Restricted" : "Failed");
                statusUpdates.Add((tweakId, status));
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var update in statusUpdates)
                {
                    var tweak = Tweaks.FirstOrDefault(x => string.Equals(x.Id, update.Id, StringComparison.OrdinalIgnoreCase));
                    if (tweak is null)
                    {
                        continue;
                    }

                    tweak.Status = update.Status;
                }

                RefreshTweaksView();
            });
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
        return "$WarningPreference='Continue'; " +
               $"$p='{escaped}'; " +
               "if (Test-Path $p) { " +
               "$item = Get-ItemProperty -Path $p -ErrorAction Stop; " +
               "$out = [ordered]@{}; " +
               "foreach ($prop in $item.PSObject.Properties) { " +
               "if ($prop.MemberType -ne 'NoteProperty' -or $prop.Name -like 'PS*') { continue }; " +
               "$value = $prop.Value; " +
               "if ($value -is [byte[]]) { $out[$prop.Name] = [Convert]::ToBase64String($value) } else { $out[$prop.Name] = $value } " +
               "}; " +
               "$out | ConvertTo-Json -Depth 8 -Compress " +
               "} else { '' }";
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

        var section = ResolveSectionKey(tweak);
        var sectionTitle = SectionMetadata.TryGetValue(section, out var meta) ? meta.Title : section;
        return tweak.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               tweak.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               tweak.Scope.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               sectionTitle.Contains(Search, StringComparison.OrdinalIgnoreCase);
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

        RefreshTweaksView();
        _console.Publish("Info", $"Selection imported: {dialog.FileName}");
    }

    private async Task<bool> IsTweakInDesiredStateAsync(TweakDefinition tweak, bool desiredApplied, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tweak.Status))
        {
            if (IsAppliedStatus(tweak.Status) == desiredApplied)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(tweak.DetectScript))
        {
            return false;
        }

        var result = await _queryService.InvokeAsync(tweak.DetectScript, cancellationToken, echoToConsole: false).ConfigureAwait(false);
        var output = (result.Stdout + "\n" + result.Stderr).Trim();

        if (result.ExitCode == 0)
        {
            var status = string.IsNullOrWhiteSpace(output) ? "Detected" : output;
            return IsAppliedStatus(status) == desiredApplied;
        }

        if (DetectMissingSettingState(output))
        {
            return !desiredApplied;
        }

        return false;
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

    private static bool DetectMissingSettingState(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("does not exist at path", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot find path", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot find registry key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot find property", StringComparison.OrdinalIgnoreCase)
            || message.Contains("itemnotfoundexception", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppliedStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalized = status.Trim();
        if (normalized.Equals("Applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Detected", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Installed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Equals("Not Applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Not Installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Managed / Restricted", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Contains("not applied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("managed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("restricted", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized.Contains("applied", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("installed", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveSectionKey(TweakDefinition tweak)
    {
        if (TweakSectionById.TryGetValue(tweak.Id, out var mapped))
        {
            return mapped;
        }

        if (tweak.Destructive || tweak.RiskTier == RiskTier.Dangerous)
        {
            return "superuser";
        }

        return tweak.Scope.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            ? "desktop"
            : "system";
    }

    private void RebuildSections()
    {
        foreach (var section in Sections)
        {
            _sectionExpansionState[section.Key] = section.IsExpanded;
        }

        Sections.Clear();
        var filtered = Tweaks.Where(t => FilterTweaks(t)).ToList();
        if (filtered.Count == 0)
        {
            Notify(nameof(Sections));
            return;
        }

        var bySection = filtered
            .GroupBy(ResolveSectionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SectionOrder)
        {
            if (!bySection.TryGetValue(key, out var tweaksForSection) || tweaksForSection.Count == 0)
            {
                continue;
            }

            var meta = SectionMetadata.TryGetValue(key, out var value)
                ? value
                : (Title: key, Description: "Tweaks");
            var expanded = _sectionExpansionState.TryGetValue(key, out var savedExpanded)
                ? savedExpanded
                : Sections.Count == 0;

            Sections.Add(new TweakSection(key, meta.Title, meta.Description, tweaksForSection, expanded));
            addedKeys.Add(key);
        }

        foreach (var pair in bySection.Where(x => !addedKeys.Contains(x.Key)))
        {
            var meta = SectionMetadata.TryGetValue(pair.Key, out var value)
                ? value
                : (Title: pair.Key, Description: "Tweaks");
            var expanded = _sectionExpansionState.TryGetValue(pair.Key, out var savedExpanded) && savedExpanded;
            Sections.Add(new TweakSection(pair.Key, meta.Title, meta.Description, pair.Value, expanded));
        }

        Notify(nameof(Sections));
    }

    private void RefreshTweaksView()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Notify(nameof(Tweaks));
            Notify(nameof(Sections));
            return;
        }

        if (dispatcher.CheckAccess())
        {
            TweaksView.Refresh();
            RebuildSections();
            Notify(nameof(Tweaks));
            return;
        }

        dispatcher.Invoke(() =>
        {
            TweaksView.Refresh();
            RebuildSections();
            Notify(nameof(Tweaks));
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toggleApplyLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public sealed class TweakSection : ObservableObject
    {
        private bool _isExpanded;

        public TweakSection(string key, string title, string description, IEnumerable<TweakDefinition> tweaks, bool isExpanded)
        {
            Key = key;
            Title = title;
            Description = description;
            Tweaks = new ObservableCollection<TweakDefinition>(tweaks);
            _isExpanded = isExpanded;
        }

        public string Key { get; }
        public string Title { get; }
        public string Description { get; }
        public ObservableCollection<TweakDefinition> Tweaks { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }
    }
}
