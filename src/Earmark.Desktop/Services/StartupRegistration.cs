namespace Earmark.Desktop.Services;

internal static class StartupRegistration
{
    public const string TrayArgument = "--tray";
    public const string NoActivateArgument = "--no-activate";

    public const string OpenArgumentPrefix = "--open=";
    public const string MonitorArgumentPrefix = "--monitor=";
    public const string QuickControlsPage = "QuickControls";

    public static bool HasArgument(IEnumerable<string> args, string argument) =>
        args.Any(arg => string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase));

    public static string? GetValue(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}
