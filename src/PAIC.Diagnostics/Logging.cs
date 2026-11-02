using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PAIC.Diagnostics;

/// <summary>One captured log record.</summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }

    public string Text =>
        $"{Timestamp.LocalDateTime:HH:mm:ss.fff} [{Level}] {Category}: {Message}" +
        (Exception is null ? "" : Environment.NewLine + Exception);
}

/// <summary>Bounded in-memory log buffer shown in the Diagnostics page.</summary>
public sealed class InMemoryLogSink
{
    private const int Capacity = 2000;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    public event Action<LogEntry>? EntryAdded;

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() { lock (_gate) return _entries.ToList(); }

    public void Clear() { lock (_gate) _entries.Clear(); }
}

internal sealed class DelegateLogger : ILogger
{
    private readonly string _category;
    private readonly Action<LogEntry> _write;

    public DelegateLogger(string category, Action<LogEntry> write) { _category = category; _write = write; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _write(new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = logLevel,
            Category = _category,
            Message = formatter(state, exception),
            Exception = exception?.ToString()
        });
    }
}

public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly InMemoryLogSink _sink;
    public InMemoryLoggerProvider(InMemoryLogSink sink) => _sink = sink;
    public ILogger CreateLogger(string categoryName) => new DelegateLogger(categoryName, _sink.Add);
    public void Dispose() { }
}

/// <summary>Writes structured JSON-lines logs to paic-yyyyMMdd.log and prunes files older than 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "paic-*.log")
                         .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
                File.Delete(old);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public ILogger CreateLogger(string categoryName) => new DelegateLogger(categoryName, Write);

    private void Write(LogEntry e)
    {
        var line = JsonSerializer.Serialize(new
        {
            time = e.Timestamp, level = e.Level.ToString(), category = e.Category,
            message = e.Message, exception = e.Exception
        });
        try
        {
            lock (_gate)
                File.AppendAllText(Path.Combine(_directory, $"paic-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { }
}

public static class LoggingExtensions
{
    public static ILoggingBuilder AddPaicLogging(this ILoggingBuilder builder, string logDirectory, InMemoryLogSink sink)
    {
        builder.SetMinimumLevel(LogLevel.Debug);
        builder.AddProvider(new InMemoryLoggerProvider(sink));
        builder.AddProvider(new FileLoggerProvider(logDirectory));
        return builder;
    }
}
