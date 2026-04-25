using Phantom.Models;

namespace Phantom.Services;

public sealed class PackageExecutionService
{
    private static readonly TimeSpan PackageOperationTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PackageQueryTimeout = TimeSpan.FromSeconds(45);

    private readonly PackageManagerResolver _resolver;
    private readonly ExternalProcessRunner _processRunner;
    private readonly ConsoleStreamService _console;

    public PackageExecutionService(PackageManagerResolver resolver, ExternalProcessRunner processRunner, ConsoleStreamService console)
    {
        _resolver = resolver;
        _processRunner = processRunner;
        _console = console;
    }

    public async Task<IReadOnlyList<PackageExecutionResult>> ExecuteAsync(
        IReadOnlyList<CatalogApp> apps,
        PackageAction action,
        CancellationToken cancellationToken)
    {
        var results = new List<PackageExecutionResult>(apps.Count);
        foreach (var app in apps)
        {
            var source = await ResolveSourceAsync(app, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                var message = app.ManualOnly
                    ? "Manual-only entry has no automatic package source."
                    : "No configured package manager is available for this package.";
                _console.Publish(app.ManualOnly ? "Warning" : "Error", $"{app.DisplayName}: {message}");
                results.Add(new PackageExecutionResult(app, false, message));
                continue;
            }

            var request = BuildRequest(app, source, action, echoOutput: true);
            var processResult = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var output = FirstNonEmpty(processResult.Stderr, processResult.Stdout);
            var resultMessage = processResult.Success
                ? $"{source.DisplayName} {action.ToString().ToLowerInvariant()} completed."
                : $"{source.DisplayName} {action.ToString().ToLowerInvariant()} failed with exit code {processResult.ExitCode}. {output}".Trim();
            results.Add(new PackageExecutionResult(app, processResult.Success, resultMessage));
        }

        return results;
    }

    public async Task<IReadOnlyList<PackageExecutionResult>> DiscoverAsync(
        IReadOnlyList<CatalogApp> apps,
        CancellationToken cancellationToken)
    {
        var results = new List<PackageExecutionResult>(apps.Count);
        foreach (var app in apps)
        {
            var source = await ResolveSourceAsync(app, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                var message = app.ManualOnly
                    ? "Manual-only entry has no package manager discovery target."
                    : "No configured package manager is available for this package.";
                _console.Publish(app.ManualOnly ? "Warning" : "Error", $"{app.DisplayName}: {message}");
                results.Add(new PackageExecutionResult(app, false, message));
                continue;
            }

            var request = BuildDiscoveryRequest(app, source);
            var processResult = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var output = FirstNonEmpty(processResult.Stdout, processResult.Stderr);
            results.Add(new PackageExecutionResult(app, processResult.Success, output));
        }

        return results;
    }

    public async Task<IReadOnlyList<PackageStatusUpdate>> GetStatusAsync(
        IReadOnlyList<CatalogApp> apps,
        CancellationToken cancellationToken)
    {
        var updates = new List<PackageStatusUpdate>(apps.Count);
        foreach (var app in apps)
        {
            if (app.ManualOnly)
            {
                updates.Add(new PackageStatusUpdate(app, "Manual-only", string.Empty, string.Empty, "manual"));
                continue;
            }

            var source = await ResolveSourceAsync(app, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                updates.Add(new PackageStatusUpdate(app, "Unavailable", string.Empty, string.Empty, BuildSourceSummary(app)));
                continue;
            }

            var request = BuildStatusRequest(app, source);
            var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var installed = result.Success && OutputContainsPackageId(result.Stdout, source.PackageId);
            updates.Add(new PackageStatusUpdate(
                app,
                installed ? "Installed" : "Ready",
                installed ? "Detected" : string.Empty,
                string.Empty,
                $"{source.Name}:{source.PackageId}"));
        }

        return updates;
    }

    private async Task<ResolvedPackageSource?> ResolveSourceAsync(CatalogApp app, CancellationToken cancellationToken)
    {
        foreach (var source in EnumerateSources(app))
        {
            var resolution = await ResolveManagerAsync(source.Name, cancellationToken).ConfigureAwait(false);
            if (resolution.IsAvailable)
            {
                return source with { ExecutablePath = resolution.ExecutablePath };
            }
        }

        return null;
    }

    private async Task<PackageManagerResolution> ResolveManagerAsync(string manager, CancellationToken cancellationToken)
    {
        return manager switch
        {
            "winget" => await _resolver.ResolveWingetAsync(cancellationToken).ConfigureAwait(false),
            "scoop" => await _resolver.ResolveScoopAsync(cancellationToken).ConfigureAwait(false),
            "choco" => await _resolver.ResolveChocolateyAsync(cancellationToken).ConfigureAwait(false),
            "pip" => await _resolver.ResolvePipAsync(cancellationToken).ConfigureAwait(false),
            "npm" => await _resolver.ResolveNpmAsync(cancellationToken).ConfigureAwait(false),
            "dotnet" => await _resolver.ResolveDotNetToolAsync(cancellationToken).ConfigureAwait(false),
            "psgallery" => await _resolver.ResolvePowerShellGalleryAsync(cancellationToken).ConfigureAwait(false),
            _ => new PackageManagerResolution { Message = $"Unsupported package manager '{manager}'." }
        };
    }

    private static IReadOnlyList<ResolvedPackageSource> EnumerateSources(CatalogApp app)
    {
        var sources = new Dictionary<string, ResolvedPackageSource>(StringComparer.OrdinalIgnoreCase);
        Add(sources, "winget", "WinGet", app.WingetId);
        Add(sources, "scoop", "Scoop", app.ScoopId);
        Add(sources, "choco", "Chocolatey", app.ChocoId);
        Add(sources, "pip", "pip", app.PipId);
        Add(sources, "npm", "npm", app.NpmId);
        Add(sources, "dotnet", ".NET Tool", app.DotNetToolId);
        Add(sources, "psgallery", "PowerShell Gallery", app.PowerShellGalleryId);

        var priority = app.PackageSourcePriority is { Length: > 0 }
            ? app.PackageSourcePriority
            : ["winget", "scoop", "choco", "pip", "npm", "dotnet", "psgallery"];

        var ordered = new List<ResolvedPackageSource>();
        foreach (var raw in priority)
        {
            var key = NormalizeManagerKey(raw);
            if (sources.TryGetValue(key, out var source) &&
                ordered.All(existing => !existing.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add(source);
            }
        }

        foreach (var source in sources.Values)
        {
            if (ordered.All(existing => !existing.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add(source);
            }
        }

        return ordered;
    }

    private static void Add(IDictionary<string, ResolvedPackageSource> sources, string name, string displayName, string? packageId)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            sources[name] = new ResolvedPackageSource(name, displayName, packageId, string.Empty);
        }
    }

    private static ExternalProcessRequest BuildRequest(CatalogApp app, ResolvedPackageSource source, PackageAction action, bool echoOutput)
    {
        var args = source.Name switch
        {
            "winget" => BuildWingetArgs(source.PackageId, action),
            "scoop" => BuildScoopArgs(source.PackageId, action),
            "choco" => BuildChocoArgs(source.PackageId, action),
            "pip" => BuildPipArgs(source.PackageId, action),
            "npm" => BuildNpmArgs(source.PackageId, action),
            "dotnet" => BuildDotNetToolArgs(source.PackageId, action),
            "psgallery" => BuildPowerShellGalleryArgs(source.PackageId, action),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Name, "Unsupported package manager.")
        };

        return new ExternalProcessRequest
        {
            OperationId = $"store.{action.ToString().ToLowerInvariant()}.{SanitizeId(app.DisplayName)}",
            StepName = source.Name,
            FilePath = source.ExecutablePath,
            Arguments = args,
            Timeout = PackageOperationTimeout,
            EchoOutput = echoOutput
        };
    }

    private static ExternalProcessRequest BuildDiscoveryRequest(CatalogApp app, ResolvedPackageSource source)
    {
        var args = source.Name switch
        {
            "winget" => ["search", "--id", source.PackageId, "--exact", "--source", "winget", "--disable-interactivity"],
            "scoop" => ["search", source.PackageId],
            "choco" => ["search", source.PackageId, "--exact", "--no-color"],
            "pip" => ["index", "versions", source.PackageId],
            "npm" => ["view", source.PackageId, "version"],
            "dotnet" => ["tool", "search", source.PackageId],
            "psgallery" => ["-NoProfile", "-NonInteractive", "-Command", $"Find-Module -Name {PowerShellInputSanitizer.ToSingleQuotedLiteral(source.PackageId)}"],
            _ => Array.Empty<string>()
        };

        return new ExternalProcessRequest
        {
            OperationId = $"store.discover.{SanitizeId(app.DisplayName)}",
            StepName = source.Name,
            FilePath = source.ExecutablePath,
            Arguments = args,
            Timeout = PackageQueryTimeout
        };
    }

    private static ExternalProcessRequest BuildStatusRequest(CatalogApp app, ResolvedPackageSource source)
    {
        var args = source.Name switch
        {
            "winget" => ["list", "--id", source.PackageId, "--exact", "--source", "winget", "--disable-interactivity"],
            "scoop" => ["list", source.PackageId],
            "choco" => ["list", "--local-only", "--exact", source.PackageId, "--limit-output", "--no-color"],
            "pip" => ["show", source.PackageId],
            "npm" => ["list", "-g", source.PackageId, "--depth=0"],
            "dotnet" => ["tool", "list", "--global"],
            "psgallery" => ["-NoProfile", "-NonInteractive", "-Command", $"Get-InstalledModule -Name {PowerShellInputSanitizer.ToSingleQuotedLiteral(source.PackageId)} -ErrorAction Stop"],
            _ => Array.Empty<string>()
        };

        return new ExternalProcessRequest
        {
            OperationId = $"store.status.{SanitizeId(app.DisplayName)}",
            StepName = source.Name,
            FilePath = source.ExecutablePath,
            Arguments = args,
            Timeout = PackageQueryTimeout,
            EchoCommand = false,
            EchoOutput = false
        };
    }

    private static string[] BuildWingetArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install => ["install", "--id", packageId, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            PackageAction.Uninstall => ["uninstall", "--id", packageId, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            PackageAction.Upgrade => ["upgrade", "--id", packageId, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildScoopArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install => ["install", packageId],
            PackageAction.Uninstall => ["uninstall", packageId],
            PackageAction.Upgrade => ["update", packageId],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildChocoArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install => ["install", packageId, "-y", "--no-progress"],
            PackageAction.Uninstall => ["uninstall", packageId, "-y", "--no-progress"],
            PackageAction.Upgrade => ["upgrade", packageId, "-y", "--no-progress"],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildPipArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install or PackageAction.Upgrade => ["install", "--upgrade", packageId],
            PackageAction.Uninstall => ["uninstall", "-y", packageId],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildNpmArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install => ["install", "-g", packageId],
            PackageAction.Uninstall => ["uninstall", "-g", packageId],
            PackageAction.Upgrade => ["update", "-g", packageId],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildDotNetToolArgs(string packageId, PackageAction action)
    {
        return action switch
        {
            PackageAction.Install => ["tool", "install", "--global", packageId],
            PackageAction.Uninstall => ["tool", "uninstall", "--global", packageId],
            PackageAction.Upgrade => ["tool", "update", "--global", packageId],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string[] BuildPowerShellGalleryArgs(string packageId, PackageAction action)
    {
        var id = PowerShellInputSanitizer.ToSingleQuotedLiteral(packageId);
        var command = action switch
        {
            PackageAction.Install => $"Install-Module -Name {id} -Scope CurrentUser -Force -AllowClobber",
            PackageAction.Uninstall => $"Uninstall-Module -Name {id} -AllVersions -Force",
            PackageAction.Upgrade => $"Update-Module -Name {id} -Force",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        return ["-NoProfile", "-NonInteractive", "-Command", command];
    }

    private static bool OutputContainsPackageId(string output, string packageId)
        => !string.IsNullOrWhiteSpace(output) &&
           output.Contains(packageId, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeManagerKey(string? key)
    {
        return (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "chocolatey" => "choco",
            "dotnettool" => "dotnet",
            "dotnet-tool" => "dotnet",
            "powershellgallery" => "psgallery",
            "powershell-gallery" => "psgallery",
            "psgallery" => "psgallery",
            var value => value
        };
    }

    private static string BuildSourceSummary(CatalogApp app)
    {
        var sources = EnumerateSources(app).Select(source => $"{source.Name}:{source.PackageId}").ToArray();
        return sources.Length == 0 ? "missing source" : string.Join(", ", sources);
    }

    private static string FirstNonEmpty(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first;

    private static string SanitizeId(string source)
        => new(source.Where(char.IsLetterOrDigit).ToArray());

    private sealed record ResolvedPackageSource(string Name, string DisplayName, string PackageId, string ExecutablePath);
}

public sealed record PackageExecutionResult(CatalogApp App, bool Success, string Message);

public sealed record PackageStatusUpdate(CatalogApp App, string Status, string InstalledVersion, string AvailableVersion, string SourceSummary);
