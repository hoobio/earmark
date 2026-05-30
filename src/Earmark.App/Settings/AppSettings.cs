using System.Text.Json.Serialization;

namespace Earmark.App.Settings;

public sealed class AppSettings
{
    public bool LaunchOnStartup { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }

    public bool LaunchToTray { get; set; }

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Whether the standalone (MSI / unpackaged) build checks GitHub for a newer release on launch.
    /// Default true. Ignored by packaged (MSIX/Store) builds, which update through the Store. The
    /// manual "Check for updates" button works regardless of this toggle.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

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

    /// <summary>
    /// Per-device configuration, keyed by endpoint id. Only devices that deviate from the
    /// defaults get an entry (all-default entries are pruned on save), so the map stays sparse.
    /// Replaces the old parallel hidden / pinned / volume-controls-hidden id lists.
    /// </summary>
    public Dictionary<string, DeviceConfig> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Top-level block arrangement on the Devices page, top-to-bottom across the grid. Each entry
    /// is either a lone device's endpoint id or a <see cref="DeviceGroup.Id"/>; an entry that
    /// matches a known group id is a group block, everything else a lone card. Devices that belong
    /// to a group are <b>omitted</b> here (their slot is the group's, their order is the group's
    /// <see cref="DeviceGroup.MemberIds"/>). Empty until the user first reorders. A block not in
    /// this list slots into its default-sort position among the rest.
    /// </summary>
    public List<string> DeviceOrder { get; set; } = new();

    /// <summary>
    /// User-defined device groups on the Devices page. A group is an atomic block that bundles two
    /// or more device cards under an editable title. <see cref="DeviceGroup.MemberIds"/> is the
    /// single source of truth for membership and intra-group order; the group's position among
    /// other blocks comes from its id's slot in <see cref="DeviceOrder"/>.
    /// </summary>
    public List<DeviceGroup> DeviceGroups { get; set; } = new();

    /// <summary>Persisted window size in physical pixels. Null until the user has resized
    /// at least once (so first launch picks the WinUI default).</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}

/// <summary>
/// Per-device user configuration. Flags are nullable so an unset flag is omitted from the JSON
/// (null = the default), keeping persisted entries compact; null is read as false.
/// </summary>
public sealed class DeviceConfig
{
    /// <summary>User has explicitly hidden this card. Wins over auto / pin.</summary>
    public bool? Hidden { get; set; }

    /// <summary>User has explicitly pinned this card visible, overriding the auto-hide-no-rules
    /// rule (but not <see cref="Hidden"/>).</summary>
    public bool? Pinned { get; set; }

    /// <summary>User has hidden this device's volume slider + mute toggle (the card stays visible).
    /// For endpoints whose volume control doesn't affect output (e.g. a USB DAC/amp with an analog
    /// knob), Windows still reports a writable control, so this is a manual opt-out.</summary>
    public bool? VolumeControlsHidden { get; set; }

    /// <summary>True when every flag is unset/false, so the entry carries no information and can be
    /// pruned from the map on save. Not serialised (it's a derived helper, not stored state).</summary>
    [JsonIgnore]
    public bool IsDefault => Hidden is not true && Pinned is not true && VolumeControlsHidden is not true;
}

/// <summary>
/// A device group: an editable title over two-or-more device cards rendered as one atomic,
/// full-width section. <see cref="MemberIds"/> are endpoint ids in member (left-to-right) order and
/// are the single source of truth for membership; the group's position among other blocks comes from
/// its id's slot in <see cref="AppSettings.DeviceOrder"/>. Groups with fewer than two present members
/// are disbanded.
/// </summary>
public sealed class DeviceGroup
{
    /// <summary>Stable group identity (a GUID string), independent of title or membership.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Member endpoint ids, in left-to-right member order.</summary>
    public List<string> MemberIds { get; set; } = new();
}
