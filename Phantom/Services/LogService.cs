using System.Text;
using System.Text.Json;
using Phantom.Models;

namespace Phantom.Services;

public sealed class LogService
{
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConsoleStreamService? _console;

    public LogService(AppPaths paths, Func<AppSettings> settingsAccessor)
    {
        _paths = paths;
        _settingsAccessor = settingsAccessor;
    }

    public string CurrentLogPath { get; private set; } = string.Empty;

    public void AttachConsole(ConsoleStreamService console)
    {
        _console = console;
    }

    public void OpenSessionLog()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            CurrentLogPath = Path.Combine(_paths.LogsDirectory, $"phantom-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(CurrentLogPath, $"Phantom session started {DateTimeOffset.Now:O}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"LogService.OpenSessionLog failed: {ex.Message}");
            try
            {
                var fallbackDir = Path.Combine(Path.GetTempPath(), "Phantom", "logs");
                Directory.CreateDirectory(fallbackDir);
                CurrentLogPath = Path.Combine(fallbackDir, $"phantom-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(CurrentLogPath, $"Phantom session started (fallback) {DateTimeOffset.Now:O}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Last resort: logging is unavailable. App continues without file logging.
            }
        }
    }

    /// <summary>
    /// Synchronous, non-blocking crash log entry for use in AppDomain.UnhandledException
    /// where async calls risk deadlocking. Bypasses the semaphore — acceptable because
    /// the process is about to terminate.
    /// </summary>
    public void WriteCrashEntry(string level, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CurrentLogPath))
            {
                OpenSessionLog();
            }

            var line = JsonSerializer.Serialize(new LogEnvelope
            {
                Level = level,
                Message = message,
                Timestamp = DateTimeOffset.Now
            });
            File.AppendAllText(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Crash path — swallowing is acceptable; emergency trace already logged by caller.
        }
    }

    public async Task WriteAsync(string level, string message, CancellationToken cancellationToken = default, bool echoToConsole = true)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(CurrentLogPath))
            {
                OpenSessionLog();
            }

            var line = JsonSerializer.Serialize(new LogEnvelope
            {
                Level = level,
                Message = message,
                Timestamp = DateTimeOffset.Now
            });

            await File.AppendAllTextAsync(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (echoToConsole)
        {
            _console?.Publish(level, message, persist: false);
        }
    }

    public Task EnforceRetentionAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsAccessor();
        var files = new DirectoryInfo(_paths.LogsDirectory)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => x.CreationTimeUtc)
            .ToList();

        long total = files.Sum(f => f.Length);

        for (var i = settings.MaxLogFiles; i < files.Count; i++)
        {
            files[i].Delete();
        }

        // Re-enumerate after count-based deletion, ordered oldest-first for size-based pruning.
        files = new DirectoryInfo(_paths.LogsDirectory)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x.CreationTimeUtc)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= settings.MaxTotalLogSizeBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, CurrentLogPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total -= file.Length;
            file.Delete();
        }

        return Task.CompletedTask;
    }
}
