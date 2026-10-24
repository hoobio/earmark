using Avalonia.Logging;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Services;

public sealed class AvaloniaLogSink(ILogger logger) : ILogSink
{
    private static readonly HashSet<string> Areas = [LogArea.Visual, LogArea.Win32Platform, LogArea.Binding, LogArea.Property, "OpenGL", "WinUIComposition"];

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && Areas.Contains(area);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsEnabled(level, area))
        {
            logger.LogWarning("Avalonia {Area} {Level}: {Message} {Values}", area, level, messageTemplate, string.Join(", ", propertyValues));
        }
    }
}
