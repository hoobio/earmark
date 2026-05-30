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

    /// <summary>
    /// How long (in seconds) an app chip lingers on its device card after the app stops playing or
    /// closes, before it's removed. The chip dims while lingering; a closed app also shows a badge.
    /// 0 removes a chip as soon as its app stops. Clamped to 0-600 when read.
    /// </summary>
    public int AppChipLingerSeconds { get; set; } = 30;

    /// <summary>Peak meter colour scheme on the Devices page. Default
    /// <see cref="PeakMeterColourMode.Gradient"/>; <see cref="PeakMeterColourMode.Off"/> hides the
    /// meter and shows a standard volume slider.</summary>
    public PeakMeterColourMode PeakMeterColourMode { get; set; } = PeakMeterColourMode.Gradient;

    /// <summary>Chosen bar colour for <see cref="PeakMeterColourMode.Single"/>, as "#AARRGGBB".
    /// Null uses the default green.</summary>
    public string? PeakMeterSingleColour { get; set; }

    /// <summary>Whether the peak meter splits channels into stacked bars (default) or shows one
    /// combined bar.</summary>
    public PeakMeterChannelMode PeakMeterChannelMode { get; set; } = PeakMeterChannelMode.Split;

    /// <summary>Whether the peak-hold tick renders on the meter. Default true.</summary>
    public bool PeakMeterShowHold { get; set; } = true;

    /// <summary>Whether the per-app indicator chips (the row of app icons under each device card)
    /// are shown at all. Default true. Off hides the whole apps row on every card.</summary>
    public bool ShowAppIndicators { get; set; } = true;

    /// <summary>Whether each app indicator chip shows its thin peak-level underbar. Default true.
    /// Off drops the bar (and shrinks the chip). Only meaningful when <see
    /// cref="ShowAppIndicators"/> is also on.</summary>
    public bool ShowAppPeakMeters { get; set; } = true;

    /// <summary>Whether known audio forwarders / virtual cables (Wave Link, VB-Cable,
    /// Voicemeeter, SteelSeries Sonar, ...) are filtered out of the app indicators. Default true:
    /// these relay audio from other apps, so a chip for one tells the user nothing actionable. Off
    /// shows them like any other app.</summary>
    public bool FilterAudioForwarders { get; set; } = true;

    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>
    /// Devices the user has explicitly chosen to keep visible. Overrides the auto-hide rule
    /// that hides non-default devices with no rules. <see cref="HiddenDeviceIds"/> still wins
    /// if the same device somehow ends up in both lists.
    /// </summary>
    public List<string> PinnedDeviceIds { get; set; } = new();

    /// <summary>
    /// Devices whose volume slider + mute toggle the user has hidden. For endpoints whose volume
    /// control doesn't actually affect output (e.g. a USB DAC/amp where the real volume is an
    /// analog knob), Windows still reports a normal, writable control, so there's no way to
    /// auto-detect this - it's a manual per-device opt-out. The card stays visible; only the
    /// volume controls are suppressed.
    /// </summary>
    public List<string> VolumeControlsHiddenDeviceIds { get; set; } = new();

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
