using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Logging;

public enum LogLevel { Info, Warning, Error }

public sealed record LogLine(DateTime LocalTime, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{LocalTime:yyyy-MM-dd HH:mm:ss} {(Level == LogLevel.Info ? "" : Level.ToString().ToUpperInvariant() + ": ")}{Message}";
}

public interface ILog
{
    void Write(LogLevel level, string message);
    IReadOnlyList<LogLine> Recent(int max = 200);

    /// <summary>Set when the last write to disk failed; cleared by the next successful write.</summary>
    string? WriteFailure { get; }
}

public static class LogExtensions
{
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message) => log.Write(LogLevel.Warning, message);
    public static void Error(this ILog log, string message) => log.Write(LogLevel.Error, message);
}

/// <summary>
/// Plain-text log, one file per day, kept 14 days. Write failures are never swallowed silently:
/// they are kept in <see cref="WriteFailure"/> so the tray can show "needs attention".
/// </summary>
public sealed class FileLog : ILog
{
    public const int KeepDays = 14;
    private const int RecentCapacity = 500;

    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly string _folder;
    private readonly LinkedList<LogLine> _recent = new();
    private readonly object _lock = new();
    private DateOnly _lastPrunedFor;

    public FileLog(IFileSystem fs, IClock clock, string folder)
    {
        _fs = fs;
        _clock = clock;
        _folder = folder;
    }

    public string Folder => _folder;
    public string? WriteFailure { get; private set; }

    public event Action<string>? WriteFailed;

    public string CurrentFile => PathUtil.Join(_folder, $"WoWSync-{_clock.UtcNow.ToLocalTime():yyyy-MM-dd}.log");

    public void Write(LogLevel level, string message)
    {
        var line = new LogLine(_clock.UtcNow.ToLocalTime(), level, message);
        string? failure = null;
        lock (_lock)
        {
            _recent.AddLast(line);
            while (_recent.Count > RecentCapacity) _recent.RemoveFirst();
            try
            {
                _fs.AppendAllText(CurrentFile, line + Environment.NewLine);
                WriteFailure = null;
                PruneOncePerDay();
            }
            catch (Exception e)
            {
                failure = $"The log could not be written ({e.Message}). Check that {_folder} is writable and the disk isn't full.";
                WriteFailure = failure;
            }
        }
        if (failure != null) WriteFailed?.Invoke(failure);
    }

    public IReadOnlyList<LogLine> Recent(int max = 200)
    {
        lock (_lock) return _recent.Skip(Math.Max(0, _recent.Count - max)).ToList();
    }

    private void PruneOncePerDay()
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.ToLocalTime());
        if (_lastPrunedFor == today) return;
        _lastPrunedFor = today;
        var cutoff = today.AddDays(-KeepDays);
        foreach (var file in _fs.EnumerateFiles(_folder))
        {
            var name = PathUtil.Name(file);
            if (name.Length == "WoWSync-yyyy-MM-dd.log".Length &&
                name.StartsWith("WoWSync-", StringComparison.Ordinal) &&
                DateOnly.TryParseExact(name[8..18], "yyyy-MM-dd", out var day) &&
                day < cutoff)
            {
                try { _fs.DeleteFile(file); } catch (IOException) { }
            }
        }
    }
}

/// <summary>In-memory log for tests.</summary>
public sealed class MemoryLog : ILog
{
    private readonly List<LogLine> _lines = [];
    public string? WriteFailure => null;
    public IReadOnlyList<LogLine> Lines { get { lock (_lines) return _lines.ToList(); } }
    public void Write(LogLevel level, string message) { lock (_lines) _lines.Add(new LogLine(DateTime.Now, level, message)); }
    public IReadOnlyList<LogLine> Recent(int max = 200) { lock (_lines) return _lines.TakeLast(max).ToList(); }
}
