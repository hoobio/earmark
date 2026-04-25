using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Logging;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public string FilePath => _path;

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, this);

    internal void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging itself must never throw.
            }
        }
    }

    public void Dispose()
    {
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

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

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
