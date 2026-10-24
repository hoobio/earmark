using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Logging;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly StreamWriter? _writer;
    private volatile LogLevel _minimumLevel = LogLevel.Information;

    public FileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string FilePath { get; }

    public LogLevel MinimumLevel => _minimumLevel;

    public void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Append(string line)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
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

internal sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ");
        sb.Append(logLevel.ToString().ToUpperInvariant().PadRight(11));
        sb.Append(' ');
        sb.Append(category);
        sb.Append(": ");
        sb.Append(formatter(state, exception));
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }

        provider.Append(sb.ToString());
    }
}
