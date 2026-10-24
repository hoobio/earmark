namespace Earmark.Desktop.Services;

internal static class StartupRegistration
{
    public const string TrayArgument = "--tray";
    public const string NoActivateArgument = "--no-activate";

    public static bool HasArgument(IEnumerable<string> args, string argument) =>
        args.Any(arg => string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase));
}
