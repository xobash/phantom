using System.Diagnostics;

namespace Phantom.Services;

public sealed class PackageManagerResolution
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);
    public string ExecutablePath { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class PackageManagerResolver
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, CancellationToken, Task<string?>> _pathResolver;

    public PackageManagerResolver(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        Func<string, CancellationToken, Task<string?>>? pathResolver = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _pathResolver = pathResolver ?? ResolveFromPathAsync;
    }

    public async Task<PackageManagerResolution> ResolveWingetAsync(CancellationToken cancellationToken)
    {
        var localAppData = _getEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var candidate = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
            if (_fileExists(candidate))
            {
                return new PackageManagerResolution
                {
                    ExecutablePath = candidate,
                    Source = "LOCALAPPDATA\\Microsoft\\WindowsApps",
                    Message = "Resolved winget from local WindowsApps path."
                };
            }
        }

        var fromPath = await _pathResolver("winget", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = fromPath,
                Source = "PATH(where)",
                Message = "Resolved winget from PATH fallback."
            };
        }

        return new PackageManagerResolution
        {
            Message = "WinGet not available. Install Microsoft App Installer or repair PATH."
        };
    }

    public async Task<PackageManagerResolution> ResolveChocolateyAsync(CancellationToken cancellationToken)
    {
        var chocoInstall = _getEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrWhiteSpace(chocoInstall))
        {
            var preferred = Path.Combine(chocoInstall, "bin", "choco.exe");
            if (_fileExists(preferred))
            {
                return new PackageManagerResolution
                {
                    ExecutablePath = preferred,
                    Source = "ChocolateyInstall\\bin",
                    Message = "Resolved Chocolatey from ChocolateyInstall."
                };
            }

            var root = Path.Combine(chocoInstall, "choco.exe");
            if (_fileExists(root))
            {
                return new PackageManagerResolution
                {
                    ExecutablePath = root,
                    Source = "ChocolateyInstall",
                    Message = "Resolved Chocolatey from ChocolateyInstall root."
                };
            }
        }

        var programData = _getEnvironmentVariable("ProgramData");
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }

        if (!string.IsNullOrWhiteSpace(programData))
        {
            var defaultPath = Path.Combine(programData, "chocolatey", "bin", "choco.exe");
            if (_fileExists(defaultPath))
            {
                return new PackageManagerResolution
                {
                    ExecutablePath = defaultPath,
                    Source = "ProgramData\\chocolatey\\bin",
                    Message = "Resolved Chocolatey from ProgramData default location."
                };
            }

            var legacyPath = Path.Combine(programData, "chocolatey", "choco.exe");
            if (_fileExists(legacyPath))
            {
                return new PackageManagerResolution
                {
                    ExecutablePath = legacyPath,
                    Source = "ProgramData\\chocolatey",
                    Message = "Resolved Chocolatey from ProgramData fallback location."
                };
            }
        }

        var fromPath = await _pathResolver("choco", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = fromPath,
                Source = "PATH(where)",
                Message = "Resolved Chocolatey from PATH fallback."
            };
        }

        return new PackageManagerResolution
        {
            Message = "Chocolatey not available. Install Chocolatey or repair PATH."
        };
    }

    public Task<PackageManagerResolution> ResolveScoopAsync(CancellationToken cancellationToken)
        => ResolveFromPathWithMessageAsync(
            "scoop",
            "Resolved Scoop from PATH.",
            "Scoop not available. Install Scoop or repair PATH.",
            cancellationToken);

    public Task<PackageManagerResolution> ResolvePipAsync(CancellationToken cancellationToken)
        => ResolveFromPathWithMessageAsync(
            "pip",
            "Resolved pip from PATH.",
            "pip not available. Install Python/pip or repair PATH.",
            cancellationToken);

    public Task<PackageManagerResolution> ResolveNpmAsync(CancellationToken cancellationToken)
        => ResolveFromPathWithMessageAsync(
            "npm",
            "Resolved npm from PATH.",
            "npm not available. Install Node.js/npm or repair PATH.",
            cancellationToken);

    public async Task<PackageManagerResolution> ResolveDotNetToolAsync(CancellationToken cancellationToken)
    {
        var dotnet = await _pathResolver("dotnet", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(dotnet))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = dotnet,
                Source = "PATH(where)",
                Message = "Resolved .NET SDK CLI from PATH."
            };
        }

        return new PackageManagerResolution
        {
            Message = ".NET SDK CLI not available. Install .NET SDK or repair PATH."
        };
    }

    public async Task<PackageManagerResolution> ResolvePowerShellGalleryAsync(CancellationToken cancellationToken)
    {
        var pwsh = await _pathResolver("pwsh", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pwsh))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = pwsh,
                Source = "PATH(where)",
                Message = "Resolved PowerShell 7 for PowerShell Gallery operations."
            };
        }

        var powershell = await _pathResolver("powershell", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(powershell))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = powershell,
                Source = "PATH(where)",
                Message = "Resolved Windows PowerShell for PowerShell Gallery operations."
            };
        }

        return new PackageManagerResolution
        {
            Message = "PowerShell host not available for PowerShell Gallery operations."
        };
    }

    private async Task<PackageManagerResolution> ResolveFromPathWithMessageAsync(
        string executableName,
        string availableMessage,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {
        var fromPath = await _pathResolver(executableName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return new PackageManagerResolution
            {
                ExecutablePath = fromPath,
                Source = "PATH(where)",
                Message = availableMessage
            };
        }

        return new PackageManagerResolution
        {
            Message = unavailableMessage
        };
    }

    private static async Task<string?> ResolveFromPathAsync(string executableName, CancellationToken cancellationToken)
    {
        var whereExe = OperatingSystem.IsWindows() ? "where.exe" : "which";
        var request = OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName + ".exe"
            : executableName;

        var psi = new ProcessStartInfo
        {
            FileName = whereExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(request);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return null;
        }

        var first = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }
}
