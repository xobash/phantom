using System.Diagnostics;

namespace Phantom.Services;

public sealed class PowerShellQueryService
{
    public async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string script, CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return (1, string.Empty, "Not running on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (1, string.Empty, "Failed to start powershell.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
