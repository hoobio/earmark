using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Earmark.Core.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly StreamWriter? _writer;
    private volatile LogLevel _minimumLevel = LogLevel.Information;

    public FileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // One held handle, not File.AppendAllText: startup at Debug emits hundreds of lines. FileShare.ReadWrite keeps the log tailable.
        try
        {
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            // Logging degrades to a no-op rather than crashing the app.
        }
    }

    public string FilePath => _path;

    public LogLevel MinimumLevel => _minimumLevel;

    public void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, this);

    internal void Append(string line)
    {
        if (_writer is null) return;
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
                // Logging itself must never throw.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var levelText = logLevel.ToString().ToUpperInvariant();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ");
        sb.Append(levelText.PadRight(11));
        sb.Append(' ');
        sb.Append(_category);
        sb.Append(": ");
        sb.Append(formatter(state, exception));

        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }

        _provider.Append(sb.ToString());
    }
}
