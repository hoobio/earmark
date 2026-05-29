namespace Earmark.App.Settings;

public sealed class AppSettings
{
    public bool LaunchOnStartup { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }

    public bool LaunchToTray { get; set; }

    public bool VerboseLogging { get; set; }

    /// <summary>App theme. <see cref="AppTheme.System"/> (default) follows Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool EnableWaveLink { get; set; }

    /// <summary>
    /// When true, the periodic ticker keeps Windows endpoint FriendlyName in sync with the
    /// label Wave Link uses for the same device. Only meaningful when <see
    /// cref="EnableWaveLink"/> is also on.
    /// </summary>
    public bool ReconcileWaveLinkNames { get; set; }

    /// <summary>
    /// How device cards mapped to a Wave Link channel render their icon tile. Defaults to
    /// <see cref="WaveLinkChannelStyle.Colours"/> (tint the tile with the channel accent);
    /// <see cref="WaveLinkChannelStyle.Icons"/> swaps in the raw Wave Link bitmap, and
    /// <see cref="WaveLinkChannelStyle.Off"/> keeps the plain Fluent look. Only meaningful when
    /// <see cref="EnableWaveLink"/> is also on.
    /// </summary>
    public WaveLinkChannelStyle WaveLinkChannelStyle { get; set; } = WaveLinkChannelStyle.Colours;

    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>
    /// Devices the user has explicitly chosen to keep visible. Overrides the auto-hide rule
    /// that hides non-default devices with no rules. <see cref="HiddenDeviceIds"/> still wins
    /// if the same device somehow ends up in both lists.
    /// </summary>
    public List<string> PinnedDeviceIds { get; set; } = new();

    /// <summary>
    /// Manual device-card order (endpoint ids, top-to-bottom across the grid). Empty until the
    /// user first drags a card to reorder, which snapshots the entire current order; thereafter
    /// the user rearranges freely. Lists every card, visible or hidden, so a hidden device keeps
    /// its slot. A device not in this list slots into its default-sort position among the rest.
    /// </summary>
    public List<string> DeviceOrder { get; set; } = new();

    /// <summary>Persisted window size in physical pixels. Null until the user has resized
    /// at least once (so first launch picks the WinUI default).</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}
