using Phantom.Models;

namespace Phantom.Services;

public enum PackageAction
{
    Install,
    Uninstall,
    Upgrade
}

public static class OperationDefinitionFactory
{
    public static OperationDefinition BuildTweakOperation(TweakDefinition tweak)
    {
        return new OperationDefinition
        {
            Id = $"tweak.{tweak.Id}",
            Title = tweak.Name,
            Description = tweak.Description,
            RiskTier = tweak.RiskTier,
            Reversible = tweak.Reversible,
            Destructive = tweak.Destructive,
            DetectScript = tweak.DetectScript,
            Compatibility = tweak.Compatibility ?? Array.Empty<string>(),
            Tags = ["tweak", tweak.Scope],
            StateCaptureKeys = tweak.StateCaptureKeys ?? Array.Empty<string>(),
            StateCaptureScripts = (tweak.StateCaptureKeys ?? Array.Empty<string>())
                .Select(key => new PowerShellStep
                {
                    Name = key,
                    Script = TweakStateScriptFactory.BuildCaptureScript(key)
                })
                .ToArray(),
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

    public static OperationDefinition BuildPackageOperation(CatalogApp app, PackageAction action)
    {
        var plan = PackageCommandPlan.FromCatalogApp(app);
        var actionText = action.ToString().ToLowerInvariant();
        var script = action switch
        {
            PackageAction.Install => plan.BuildInstallScript(),
            PackageAction.Uninstall => plan.BuildUninstallScript(),
            PackageAction.Upgrade => plan.BuildUpgradeScript(),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        return new OperationDefinition
        {
            Id = $"store.{actionText}.{SanitizeId(app.DisplayName)}",
            Title = $"{action} {app.DisplayName}",
            Description = BuildPackageDescription(app, action),
            RiskTier = RiskTier.Advanced,
            Reversible = false,
            DetectScript = plan.BuildDetectScript(),
            Tags = ["store", "package", actionText, .. plan.Managers.Select(manager => manager.Name)],
            RunScripts =
            [
                new PowerShellStep
                {
                    Name = actionText,
                    RequiresNetwork = action is PackageAction.Install or PackageAction.Upgrade,
                    Script = script
                }
            ]
        };
    }

    public static string BuildPackageStatusScript(CatalogApp app)
    {
        return PackageCommandPlan.FromCatalogApp(app).BuildStatusScript();
    }

    public static string BuildPackageDiscoveryScript(CatalogApp app)
    {
        return PackageCommandPlan.FromCatalogApp(app).BuildDiscoveryScript();
    }

    private static string BuildPackageDescription(CatalogApp app, PackageAction action)
    {
        if (app.ManualOnly)
        {
            return $"Manual-only catalog entry. Phantom will not attempt to {action.ToString().ToLowerInvariant()} it automatically.";
        }

        var sources = PackageCommandPlan.FromCatalogApp(app)
            .Managers
            .Select(manager => $"{manager.Name}:{manager.PackageId}")
            .ToArray();
        return sources.Length == 0
            ? "No package source metadata is available."
            : $"Package sources: {string.Join(", ", sources)}.";
    }

    private static string SanitizeId(string source)
    {
        return new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private sealed class PackageCommandPlan
    {
        private PackageCommandPlan(IReadOnlyList<PackageSourceRef> managers, bool manualOnly)
        {
            Managers = managers;
            ManualOnly = manualOnly;
        }

        public IReadOnlyList<PackageSourceRef> Managers { get; }
        private bool ManualOnly { get; }

        public static PackageCommandPlan FromCatalogApp(CatalogApp app)
        {
            var sources = new Dictionary<string, PackageSourceRef>(StringComparer.OrdinalIgnoreCase);
            AddSource(sources, "winget", app.WingetId, id => PowerShellInputSanitizer.EnsurePackageId(id, $"store app '{app.DisplayName}' wingetId"));
            AddSource(sources, "scoop", app.ScoopId, id => PowerShellInputSanitizer.EnsureEcosystemPackageId(id, $"store app '{app.DisplayName}' scoopId"));
            AddSource(sources, "choco", app.ChocoId, id => PowerShellInputSanitizer.EnsurePackageId(id, $"store app '{app.DisplayName}' chocoId"));
            AddSource(sources, "pip", app.PipId, id => PowerShellInputSanitizer.EnsureEcosystemPackageId(id, $"store app '{app.DisplayName}' pipId"));
            AddSource(sources, "npm", app.NpmId, id => PowerShellInputSanitizer.EnsureEcosystemPackageId(id, $"store app '{app.DisplayName}' npmId"));
            AddSource(sources, "dotnet", app.DotNetToolId, id => PowerShellInputSanitizer.EnsureEcosystemPackageId(id, $"store app '{app.DisplayName}' dotNetToolId"));
            AddSource(sources, "psgallery", app.PowerShellGalleryId, id => PowerShellInputSanitizer.EnsureEcosystemPackageId(id, $"store app '{app.DisplayName}' powerShellGalleryId"));

            var priority = app.PackageSourcePriority is { Length: > 0 }
                ? app.PackageSourcePriority
                : ["winget", "scoop", "choco", "pip", "npm", "dotnet", "psgallery"];

            var ordered = new List<PackageSourceRef>();
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

            if (ordered.Count == 0 && !app.ManualOnly)
            {
                throw new ArgumentException($"store app '{app.DisplayName}': no package source id is defined. Mark manualOnly=true or add a package id.");
            }

            return new PackageCommandPlan(ordered, app.ManualOnly);
        }

        public string BuildInstallScript()
            => BuildFallbackScript("install", source => source.InstallCommand);

        public string BuildUninstallScript()
            => BuildFallbackScript("uninstall", source => source.UninstallCommand);

        public string BuildUpgradeScript()
            => BuildFallbackScript("upgrade", source => source.UpgradeCommand);

        public string BuildDetectScript()
        {
            if (ManualOnly && Managers.Count == 0)
            {
                return "Write-Output 'Manual'";
            }

            var branches = Managers.Select(source =>
                $"if({source.AvailabilityExpression}){{ {source.DetectCommand}; if($LASTEXITCODE -eq 0){{ Write-Output 'Applied'; return }} }}");
            return string.Join(" ", branches) + " Write-Output 'Not Applied'";
        }

        public string BuildStatusScript()
        {
            if (ManualOnly && Managers.Count == 0)
            {
                return "[PSCustomObject]@{ Status='Manual'; Manager='manual'; PackageId=''; Installed=$false; InstalledVersion=''; AvailableVersion=''; Message='Manual-only catalog entry.' } | ConvertTo-Json -Compress";
            }

            var branches = Managers.Select(source =>
                $"if({source.AvailabilityExpression}){{ {source.StatusCommand}; return }}");
            return string.Join(" ", branches) +
                   " [PSCustomObject]@{ Status='Unavailable'; Manager=''; PackageId=''; Installed=$false; InstalledVersion=''; AvailableVersion=''; Message='No configured package manager is available.' } | ConvertTo-Json -Compress";
        }

        public string BuildDiscoveryScript()
        {
            if (ManualOnly && Managers.Count == 0)
            {
                return "Write-Output 'Manual-only catalog entry: no package manager discovery target is configured.'";
            }

            var branches = Managers.Select(source =>
                $"if({source.AvailabilityExpression}){{ Write-Output '== {source.DisplayName}: {source.PackageId} =='; {source.DiscoveryCommand} }}");
            return string.Join(" ", branches) + " Write-Output 'Discovery complete.'";
        }

        private string BuildFallbackScript(string action, Func<PackageSourceRef, string> commandSelector)
        {
            if (ManualOnly && Managers.Count == 0)
            {
                return "throw 'Manual-only catalog entry. No automatic package operation is available.'";
            }

            var branches = Managers
                .Select(source =>
                    $"if({source.AvailabilityExpression}){{ {commandSelector(source)}; $phantomExit=$LASTEXITCODE; if($phantomExit -eq 0 -or $null -eq $phantomExit){{ return }}; throw '{source.DisplayName} {action} failed with exit code ' + $phantomExit }}")
                .ToArray();

            if (branches.Length == 0)
            {
                return "throw 'No package source metadata is available.'";
            }

            return string.Join(" ", branches) +
                   " throw 'No configured package manager is available for this package.'";
        }

        private static void AddSource(
            IDictionary<string, PackageSourceRef> sources,
            string key,
            string? rawPackageId,
            Func<string, string> validate)
        {
            if (string.IsNullOrWhiteSpace(rawPackageId))
            {
                return;
            }

            var id = validate(rawPackageId);
            sources[key] = PackageSourceRef.Create(key, id);
        }

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
    }

    private sealed record PackageSourceRef(
        string Name,
        string DisplayName,
        string PackageId,
        string AvailabilityExpression,
        string InstallCommand,
        string UninstallCommand,
        string UpgradeCommand,
        string DetectCommand,
        string DiscoveryCommand,
        string StatusCommand)
    {
        public static PackageSourceRef Create(string manager, string packageId)
        {
            var id = PowerShellInputSanitizer.ToSingleQuotedLiteral(packageId);
            return manager switch
            {
                "winget" => new PackageSourceRef(
                    "winget",
                    "WinGet",
                    packageId,
                    "$null -ne (Get-Command winget -ErrorAction SilentlyContinue)",
                    $"winget install --id {id} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity",
                    $"winget uninstall --id {id} --exact --disable-interactivity",
                    $"winget upgrade --id {id} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity",
                    $"winget list --id {id} --exact --disable-interactivity 2>&1 | Out-Null",
                    $"winget search --id {id} --exact --disable-interactivity 2>&1 | Out-String",
                    BuildStatusCommand("winget", packageId, $"$out=(winget list --id {id} --exact --disable-interactivity 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match [regex]::Escape({id})); $available=''")),
                "scoop" => new PackageSourceRef(
                    "scoop",
                    "Scoop",
                    packageId,
                    "$null -ne (Get-Command scoop -ErrorAction SilentlyContinue)",
                    $"scoop install {id}",
                    $"scoop uninstall {id}",
                    $"scoop update {id}",
                    $"scoop list {id} 2>&1 | Out-Null",
                    $"scoop search {id} 2>&1 | Out-String",
                    BuildStatusCommand("scoop", packageId, $"$out=(scoop list {id} 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match [regex]::Escape({id})); $available=''")),
                "choco" => new PackageSourceRef(
                    "choco",
                    "Chocolatey",
                    packageId,
                    "$null -ne (Get-Command choco -ErrorAction SilentlyContinue)",
                    $"choco install {id} -y --no-progress",
                    $"choco uninstall {id} -y --no-progress",
                    $"choco upgrade {id} -y --no-progress",
                    $"choco list --local-only --exact {id} --limit-output --no-color 2>&1 | Out-Null",
                    $"choco search {id} --exact --no-color 2>&1 | Out-String",
                    BuildStatusCommand("choco", packageId, $"$out=(choco list --local-only --exact {id} --limit-output --no-color 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match ('^' + [regex]::Escape({id}) + '\\|')); $upgrade=(choco outdated --limit-output --no-color 2>$null | Out-String); $available=if($upgrade -match ('^' + [regex]::Escape({id}) + '\\|')){{'Update available'}}else{{''}}")),
                "pip" => new PackageSourceRef(
                    "pip",
                    "pip",
                    packageId,
                    "$null -ne (Get-Command pip -ErrorAction SilentlyContinue)",
                    $"pip install --upgrade {id}",
                    $"pip uninstall -y {id}",
                    $"pip install --upgrade {id}",
                    $"pip show {id} 2>&1 | Out-Null",
                    $"pip index versions {id} 2>&1 | Out-String",
                    BuildStatusCommand("pip", packageId, $"$out=(pip show {id} 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match '^Name:\\s*' + [regex]::Escape({id})); $available=''")),
                "npm" => new PackageSourceRef(
                    "npm",
                    "npm",
                    packageId,
                    "$null -ne (Get-Command npm -ErrorAction SilentlyContinue)",
                    $"npm install -g {id}",
                    $"npm uninstall -g {id}",
                    $"npm update -g {id}",
                    $"npm list -g {id} --depth=0 2>&1 | Out-Null",
                    $"npm view {id} version 2>&1 | Out-String",
                    BuildStatusCommand("npm", packageId, $"$out=(npm list -g {id} --depth=0 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match [regex]::Escape({id})); $available=''")),
                "dotnet" => new PackageSourceRef(
                    "dotnet",
                    ".NET Tool",
                    packageId,
                    "$null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)",
                    $"dotnet tool install --global {id}",
                    $"dotnet tool uninstall --global {id}",
                    $"dotnet tool update --global {id}",
                    $"dotnet tool list --global | Select-String -SimpleMatch {id} | Out-Null",
                    $"dotnet tool search {id} 2>&1 | Out-String",
                    BuildStatusCommand("dotnet", packageId, $"$out=(dotnet tool list --global 2>&1 | Out-String); $installed=($LASTEXITCODE -eq 0 -and $out -match [regex]::Escape({id})); $available=''")),
                "psgallery" => new PackageSourceRef(
                    "psgallery",
                    "PowerShell Gallery",
                    packageId,
                    "$null -ne (Get-Command Install-Module -ErrorAction SilentlyContinue)",
                    $"Install-Module -Name {id} -Scope CurrentUser -Force -AllowClobber",
                    $"Uninstall-Module -Name {id} -AllVersions -Force",
                    $"Update-Module -Name {id} -Force",
                    $"Get-InstalledModule -Name {id} -ErrorAction Stop | Out-Null",
                    $"Find-Module -Name {id} -ErrorAction SilentlyContinue | Select-Object Name, Version, Repository | Format-Table -AutoSize | Out-String",
                    BuildStatusCommand("psgallery", packageId, $"$module=Get-InstalledModule -Name {id} -ErrorAction SilentlyContinue; $installed=$null -ne $module; $available=''")),
                _ => throw new ArgumentOutOfRangeException(nameof(manager), manager, "Unsupported package manager.")
            };
        }

        private static string BuildStatusCommand(string manager, string packageId, string body)
        {
            var managerLiteral = PowerShellInputSanitizer.ToSingleQuotedLiteral(manager);
            var idLiteral = PowerShellInputSanitizer.ToSingleQuotedLiteral(packageId);
            return $"{body}; [PSCustomObject]@{{ Status=if($installed){{if([string]::IsNullOrWhiteSpace($available)){{'Installed'}}else{{'Update available'}}}}else{{'Not installed'}}; Manager={managerLiteral}; PackageId={idLiteral}; Installed=[bool]$installed; InstalledVersion=''; AvailableVersion=$available; Message='' }} | ConvertTo-Json -Compress";
        }
    }
}
