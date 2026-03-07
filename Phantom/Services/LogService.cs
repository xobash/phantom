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
        Directory.CreateDirectory(_paths.LogsDirectory);
        CurrentLogPath = Path.Combine(_paths.LogsDirectory, $"phantom-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(CurrentLogPath, $"Phantom session started {DateTimeOffset.Now:O}{Environment.NewLine}", Encoding.UTF8);
    }

    public async Task WriteAsync(string level, string message, CancellationToken cancellationToken = default, bool echoToConsole = true)
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    public async Task EnforceRetentionAsync(CancellationToken cancellationToken = default)
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

        await Task.CompletedTask;
    }
}
