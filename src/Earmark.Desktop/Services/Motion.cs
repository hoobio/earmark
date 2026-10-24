using System.Runtime.InteropServices;

using Avalonia.Animation.Easings;

namespace Earmark.Desktop.Services;

public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static TimeSpan Duration => Enabled ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;

    public static Easing Standard { get; } = new SplineEasing(0.33, 0, 0.67, 1);

    public static bool Enabled => !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var on, 0) || on;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
