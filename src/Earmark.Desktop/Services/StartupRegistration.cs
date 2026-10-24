using Microsoft.Win32;

namespace Earmark.Desktop.Services;

internal static class StartupRegistration
{
    public const string TrayArgument = "--tray";
    public const string NoActivateArgument = "--no-activate";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Earmark";

    public static bool HasArgument(IEnumerable<string> args, string argument) =>
        args.Any(arg => string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase));

    public static string? Register(bool launchToTray)
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
        {
            return null;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var command = launchToTray ? $"\"{exe}\" {TrayArgument}" : $"\"{exe}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
        return command;
    }
}
