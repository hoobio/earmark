using System.Diagnostics.CodeAnalysis;

using Microsoft.Win32;

namespace Earmark.App.Settings;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Earmark";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Register(bool launchToTray)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        var command = launchToTray
            ? $"\"{exe}\" --tray"
            : $"\"{exe}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    [SuppressMessage("Performance", "CA1310:Specify StringComparison for correctness", Justification = "Command-line is case-insensitive on Windows.")]
    public static bool LaunchedWithTrayFlag(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--tray", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
