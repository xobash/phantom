using System.Text.RegularExpressions;
using Phantom.Models;

namespace Phantom.Services;

public enum StorePackageManager
{
    Winget,
    Chocolatey
}

public sealed class StoreManagerAvailability
{
    public required PackageManagerResolution Winget { get; init; }
    public required PackageManagerResolution Chocolatey { get; init; }
}

public sealed class StoreInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Suggestion { get; init; } = string.Empty;
    public StorePackageManager? Manager { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public ExternalProcessResult? InstallExecution { get; init; }
    public ExternalProcessResult? VerificationExecution { get; init; }
}

public static class StoreCommandBuilder
{
    private static readonly IReadOnlyDictionary<int, string> WingetExitCodeMessages = new Dictionary<int, string>
    {
        [-1978335230] = "Invalid winget arguments.",
        [-1978335229] = "winget command execution failed.",
        [-1978335224] = "winget failed to download installer content.",
        [-1978335217] = "winget source data is missing or corrupt.",
        [-1978335216] = "No applicable installer is available for this system.",
        [-1978335212] = "No package found matching the specified identifier.",
        [-1978335205] = "Microsoft Store is blocked by policy.",
        [-1978335204] = "The Microsoft Store app is blocked by policy.",
        [-1978335189] = "No applicable update is available.",
        [-1978335167] = "Package agreements were not accepted.",
        [-1978335163] = "winget REST endpoint was not found for the selected source.",
        [-1978335162] = "Source agreements were not accepted.",
        [-1978335159] = "MSI installer execution failed.",
        [-1978335146] = "Installer cannot run from elevated context."
    };

    public static IReadOnlyList<string> BuildWingetInstallArguments(string wingetId, IReadOnlyList<string> extraArgs)
    {
        var args = new List<string>
        {
            "install",
            "--id",
            wingetId,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };
        if (extraArgs.Count > 0)
        {
            args.AddRange(extraArgs);
        }

        return args;
    }

    public static IReadOnlyList<string> BuildWingetVerificationArguments(string wingetId)
    {
        return
        [
            "list",
            "--id",
            wingetId,
            "--exact",
            "--disable-interactivity"
        ];
    }

    public static IReadOnlyList<string> BuildChocoInstallArguments(string chocoId, IReadOnlyList<string> extraArgs)
    {
        var args = new List<string>
        {
            "install",
            chocoId,
            "-y",
            "--no-progress"
        };
        if (extraArgs.Count > 0)
        {
            args.AddRange(extraArgs);
        }

        return args;
    }

    public static IReadOnlyList<string> BuildChocoVerificationArguments(string chocoId)
    {
        return
        [
            "list",
            "--local-only",
            "--exact",
            chocoId,
            "--limit-output",
            "--no-color"
        ];
    }

    public static IReadOnlyList<string> SplitSafeArguments(string safeArgs)
    {
        if (string.IsNullOrWhiteSpace(safeArgs))
        {
            return Array.Empty<string>();
        }

        return safeArgs
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public static string DescribeWingetExitCode(int exitCode)
    {
        if (WingetExitCodeMessages.TryGetValue(exitCode, out var message))
        {
            return message;
        }

        return $"winget failed with exit code {exitCode} (0x{unchecked((uint)exitCode):X8}).";
    }

    public static bool OutputContainsWingetPackage(string packageId, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var pattern = $@"(^|\s){Regex.Escape(packageId)}(\s|$)";
        return Regex.IsMatch(output, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    public static bool OutputContainsChocoPackage(string packageId, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.StartsWith(packageId + "|", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class StoreInstallService
{
    private static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(10);

    private readonly PackageManagerResolver _resolver;
    private readonly ExternalProcessRunner _processRunner;
    private readonly ConsoleStreamService _console;

    public StoreInstallService(
        PackageManagerResolver resolver,
        ExternalProcessRunner processRunner,
        ConsoleStreamService console)
    {
        _resolver = resolver;
        _processRunner = processRunner;
        _console = console;
    }

    public async Task<StoreManagerAvailability> GetManagerAvailabilityAsync(CancellationToken cancellationToken)
    {
        var winget = await _resolver.ResolveWingetAsync(cancellationToken).ConfigureAwait(false);
        var choco = await _resolver.ResolveChocolateyAsync(cancellationToken).ConfigureAwait(false);
        return new StoreManagerAvailability
        {
            Winget = winget,
            Chocolatey = choco
        };
    }

    public async Task<StoreInstallResult> InstallAsync(CatalogApp app, CancellationToken cancellationToken)
    {
        var context = $"store app '{app.DisplayName}'";
        var wingetId = string.IsNullOrWhiteSpace(app.WingetId)
            ? string.Empty
            : PowerShellInputSanitizer.EnsurePackageId(app.WingetId, $"{context} wingetId");
        var chocoId = string.IsNullOrWhiteSpace(app.ChocoId)
            ? string.Empty
            : PowerShellInputSanitizer.EnsurePackageId(app.ChocoId, $"{context} chocoId");
        var additionalArgs = StoreCommandBuilder.SplitSafeArguments(
            PowerShellInputSanitizer.EnsureSafeCliArguments(app.SilentArgs, $"{context} silentArgs"));

        var availability = await GetManagerAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        var selection = SelectManager(availability, wingetId, chocoId);
        if (!selection.Success)
        {
            _console.Publish("Error", selection.Message);
            return new StoreInstallResult
            {
                Success = false,
                Message = selection.Message,
                Suggestion = selection.Suggestion
            };
        }

        _console.Publish("Info", $"Store installer selected manager={selection.ManagerText}, id={selection.PackageId}");
        _console.Publish("Trace", $"Store installer executable path={selection.ExecutablePath}, source={selection.Source}");

        var installArgs = selection.Manager == StorePackageManager.Winget
            ? StoreCommandBuilder.BuildWingetInstallArguments(selection.PackageId, additionalArgs)
            : StoreCommandBuilder.BuildChocoInstallArguments(selection.PackageId, additionalArgs);

        var installResult = await _processRunner.RunAsync(new ExternalProcessRequest
        {
            OperationId = $"store.app.{SanitizeId(app.DisplayName)}",
            StepName = "install",
            FilePath = selection.ExecutablePath,
            Arguments = installArgs,
            Timeout = DefaultInstallTimeout
        }, cancellationToken).ConfigureAwait(false);

        if (!installResult.Success)
        {
            var failure = BuildExecutionFailure(selection.Manager, installResult);
            return new StoreInstallResult
            {
                Success = false,
                Message = failure.Message,
                Suggestion = failure.Suggestion,
                Manager = selection.Manager,
                PackageId = selection.PackageId,
                InstallExecution = installResult
            };
        }

        var verifyArgs = selection.Manager == StorePackageManager.Winget
            ? StoreCommandBuilder.BuildWingetVerificationArguments(selection.PackageId)
            : StoreCommandBuilder.BuildChocoVerificationArguments(selection.PackageId);

        var verifyResult = await _processRunner.RunAsync(new ExternalProcessRequest
        {
            OperationId = $"store.app.{SanitizeId(app.DisplayName)}",
            StepName = "verify",
            FilePath = selection.ExecutablePath,
            Arguments = verifyArgs,
            Timeout = TimeSpan.FromMinutes(2)
        }, cancellationToken).ConfigureAwait(false);

        if (!verifyResult.Success || !IsVerificationSuccessful(selection.Manager, selection.PackageId, string.Concat(verifyResult.Stdout, Environment.NewLine, verifyResult.Stderr)))
        {
            var verificationMessage = $"Install verification failed for {selection.ManagerText} package '{selection.PackageId}'.";
            var verificationHint = verifyResult.Success
                ? "Install command completed, but verification did not find the package."
                : BuildExecutionFailure(selection.Manager, verifyResult).Message;
            return new StoreInstallResult
            {
                Success = false,
                Message = verificationMessage,
                Suggestion = verificationHint,
                Manager = selection.Manager,
                PackageId = selection.PackageId,
                InstallExecution = installResult,
                VerificationExecution = verifyResult
            };
        }

        return new StoreInstallResult
        {
            Success = true,
            Message = $"Installed and verified via {selection.ManagerText}: {selection.PackageId}.",
            Suggestion = string.Empty,
            Manager = selection.Manager,
            PackageId = selection.PackageId,
            InstallExecution = installResult,
            VerificationExecution = verifyResult
        };
    }

    private static (bool Success, StorePackageManager Manager, string ManagerText, string ExecutablePath, string Source, string PackageId, string Message, string Suggestion) SelectManager(
        StoreManagerAvailability availability,
        string wingetId,
        string chocoId)
    {
        if (!string.IsNullOrWhiteSpace(wingetId) && availability.Winget.IsAvailable)
        {
            return (true, StorePackageManager.Winget, "winget", availability.Winget.ExecutablePath, availability.Winget.Source, wingetId, string.Empty, string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(chocoId) && availability.Chocolatey.IsAvailable)
        {
            return (true, StorePackageManager.Chocolatey, "choco", availability.Chocolatey.ExecutablePath, availability.Chocolatey.Source, chocoId, string.Empty, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(wingetId) && string.IsNullOrWhiteSpace(chocoId))
        {
            return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "No available installer manager for this app: catalog entry has no winget/choco id.", "Update catalog metadata with a valid package id.");
        }

        if (!string.IsNullOrWhiteSpace(wingetId) && !availability.Winget.IsAvailable &&
            !string.IsNullOrWhiteSpace(chocoId) && !availability.Chocolatey.IsAvailable)
        {
            return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "No available installer manager for this app. winget and choco are both unavailable.", $"{availability.Winget.Message} {availability.Chocolatey.Message}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(wingetId) && !availability.Winget.IsAvailable && !string.IsNullOrWhiteSpace(chocoId))
        {
            return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "winget id exists, but winget is unavailable; choco fallback id is present but choco is unavailable.", $"{availability.Winget.Message} {availability.Chocolatey.Message}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(wingetId) && !availability.Winget.IsAvailable)
        {
            return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "No available installer manager for this app: winget id exists but winget is unavailable.", availability.Winget.Message);
        }

        if (!string.IsNullOrWhiteSpace(chocoId) && !availability.Chocolatey.IsAvailable)
        {
            return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "No available installer manager for this app: choco id exists but choco is unavailable.", availability.Chocolatey.Message);
        }

        return (false, default, string.Empty, string.Empty, string.Empty, string.Empty, "No available installer manager for this app.", "Check package manager installation and catalog metadata.");
    }

    private static (string Message, string Suggestion) BuildExecutionFailure(StorePackageManager manager, ExternalProcessResult result)
    {
        if (result.TimedOut)
        {
            return ("Install process timed out.", "Retry the install or run the manager command manually with logs.");
        }

        if (manager == StorePackageManager.Winget)
        {
            return (
                StoreCommandBuilder.DescribeWingetExitCode(result.ExitCode),
                BuildSuggestionFromOutput(result.Stderr, result.Stdout));
        }

        return (
            $"Chocolatey failed with exit code {result.ExitCode}.",
            BuildSuggestionFromOutput(result.Stderr, result.Stdout));
    }

    private static bool IsVerificationSuccessful(StorePackageManager manager, string packageId, string output)
    {
        if (manager == StorePackageManager.Winget)
        {
            return StoreCommandBuilder.OutputContainsWingetPackage(packageId, output);
        }

        return StoreCommandBuilder.OutputContainsChocoPackage(packageId, output);
    }

    private static string BuildSuggestionFromOutput(string stderr, string stdout)
    {
        var combined = $"{stderr}\n{stdout}";
        if (combined.Contains("No package found matching input criteria.", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("No packages found", StringComparison.OrdinalIgnoreCase))
        {
            return "Package id may be outdated or unavailable in the selected source.";
        }

        if (combined.Contains("source agreements", StringComparison.OrdinalIgnoreCase))
        {
            return "Source agreements were not accepted. Retry and verify winget source configuration.";
        }

        if (combined.Contains("requires administrator", StringComparison.OrdinalIgnoreCase))
        {
            return "Installer requires administrator privileges.";
        }

        var stderrTail = GetLastLines(stderr, 6);
        return string.IsNullOrWhiteSpace(stderrTail)
            ? "Inspect command output in logs for details."
            : $"Last stderr lines: {stderrTail}";
    }

    private static string GetLastLines(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(Math.Max(1, maxLines))
            .ToArray();
        return string.Join(" | ", lines);
    }

    private static string SanitizeId(string source)
    {
        return new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
