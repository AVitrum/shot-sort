using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ScreenSorter.Infrastructure.Logging;

/// <summary>
/// Minimal logger that appends to one file per day (<c>screensorter-yyyy-MM-dd.log</c>).
/// A tray app has no console, so this is the main diagnostic output. Writes are serialized with a lock.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    /// <summary>Creates the provider; <paramref name="folder"/> is created if missing.</summary>
    /// <param name="folder">Target folder for log files.</param>
    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    /// <inheritdoc />
    public void Dispose() => _loggers.Clear();

    private void Write(string line)
    {
        var file = Path.Combine(_folder, $"screensorter-{DateTime.Now:yyyy-MM-dd}.log");
        lock (_gate)
        {
            try
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
                .Append(logLevel.ToString()[..4].ToUpperInvariant()).Append(' ')
                .Append(_shortCategory).Append(": ")
                .AppendLine(formatter(state, exception));
            if (exception is not null)
                sb.AppendLine(exception.ToString());
            provider.Write(sb.ToString());
        }
    }
}
