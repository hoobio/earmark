using System.Text.Json.Serialization;

using Earmark.Core.Models;

namespace Earmark.App.Settings;

public sealed class AppSettings
{
    /// <summary>
    /// Persistence schema revision. 0 (the implicit value for a file written before the device-key
    /// migration) triggers the one-time re-key of <see cref="DeviceOrder"/> / <see cref="DeviceGroups"/>
    /// / <see cref="Devices"/> from endpoint id to <see cref="Earmark.Core.Models.DeviceIdentity"/>
    /// key, after which it is bumped to <see cref="DeviceKeySchemaVersion"/>. See the ADR
    /// <c>docs/adr/device-persistence-and-identity.md</c>.
    /// </summary>
    public int SettingsSchemaVersion { get; set; }

    /// <summary>The schema version once the device-key migration has run.</summary>
    [JsonIgnore]
    public const int DeviceKeySchemaVersion = 1;

    /// <summary>The schema version once default Quick Controls pins have been seeded.</summary>
    [JsonIgnore]
    public const int QuickControlsSeedSchemaVersion = 2;

    public bool LaunchOnStartup { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }

    public bool LaunchToTray { get; set; }

    public bool QuickControlsEnabled { get; set; } = true;

    public string QuickControlsHotkey { get; set; } = "Win+Alt+V";

    public QuickControlsBackdropMode QuickControlsBackdrop { get; set; } = QuickControlsBackdropMode.UseAppAppearance;

    public QuickControlsDisplayMode QuickControlsDisplay { get; set; } = QuickControlsDisplayMode.CurrentlyActive;

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Whether the standalone (MSI / unpackaged) build checks GitHub for a newer release on launch.
    /// Default true. Ignored by packaged (MSIX/Store) builds, which update through the Store. The
    /// manual "Check for updates" button works regardless of this toggle.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>App theme. <see cref="AppTheme.System"/> (default) follows Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Window background material. <see cref="BackdropMode.Mica"/> (default) matches the
    /// original look; Acrylic blurs the desktop behind the window; Solid drops the system backdrop
    /// for an opaque themed surface. Falls back to Solid on OSes that lack the chosen material.</summary>
    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;

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

    /// <summary>Whether each device card draws hairline separators between its sections (volume row /
    /// rules / apps row), stacking them like a Windows Settings card. Default true; off separates the
    /// sections by spacing alone.</summary>
    public bool ShowCardDividers { get; set; } = true;

    /// <summary>Whether each device card shows its rules section. Default true; off hides the
    /// rules chips and no-rules text on every card.</summary>
    public bool ShowRules { get; set; } = true;

    /// <summary>How device cards size their height within a row. <see cref="CardHeightMode.Balanced"/>
    /// (default) aligns plain cards to the row's tallest plain card while letting a card with apps /
    /// expanded rules keep its own height; <see cref="CardHeightMode.MatchRow"/> makes every card in a
    /// row match the tallest; <see cref="CardHeightMode.Dynamic"/> sizes each card to its own content.</summary>
    public CardHeightMode CardHeight { get; set; } = CardHeightMode.Balanced;

    /// <summary>Whether an app pinned by an <c>ApplicationOutput</c> rule always shows its chip on
    /// the pinned device (dimmed while silent), plus the rule-lock padlock badge. Default true
    /// (the original behaviour). Off makes a pinned app's chip appear only while it's actually
    /// producing audio - like any other app - and hides the padlock. Only meaningful when
    /// <see cref="ShowAppIndicators"/> is also on.</summary>
    public bool AlwaysShowPinnedApps { get; set; } = true;

    /// <summary>Whether the Devices page shows its header row (the "Devices" title). Hidden via the
    /// page's "..." / right-click menu for a cleaner look; the "..." stays visible either way.
    /// Default true.</summary>
    public bool ShowDevicesPageHeader { get; set; } = true;

    /// <summary>When true, device cards and groups can't be dragged to reorder / regroup on the
    /// Devices page (a guard against accidental rearrangement). Toggled from the page's "..." /
    /// right-click menu. Default false.</summary>
    public bool LockDeviceLayout { get; set; }

    /// <summary>Whether disconnected devices are shown on the Devices page. Default false so first
    /// launch stays focused on currently-present endpoints.</summary>
    public bool ShowDisconnectedDevices { get; set; }

    /// <summary>
    /// Per-device configuration, keyed by <see cref="Earmark.Core.Models.DeviceIdentity"/> device key
    /// (stable across a driver reinstall, unlike the endpoint id). Only devices that deviate from the
    /// defaults get an entry (all-default entries are pruned on save), so the map stays sparse.
    /// Replaces the old parallel hidden / pinned / volume-controls-hidden id lists.
    /// </summary>
    public Dictionary<string, DeviceConfig> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Top-level block arrangement on the Devices page, top-to-bottom across the grid. Each entry is
    /// either a lone device's <see cref="Earmark.Core.Models.DeviceIdentity"/> key or a
    /// <see cref="DeviceGroup.Id"/>; an entry that matches a known group id is a group block,
    /// everything else a lone card. Devices that belong to a group are <b>omitted</b> here (their
    /// slot is the group's, their order is the group's <see cref="DeviceGroup.MemberIds"/>). Empty
    /// until the user first reorders. A block not in this list slots into its default-sort position.
    /// </summary>
    public List<string> DeviceOrder { get; set; } = new();

    /// <summary>
    /// User-defined device groups on the Devices page. A group is an atomic block that bundles two
    /// or more device cards under an editable title. <see cref="DeviceGroup.MemberIds"/> (device
    /// keys) is the single source of truth for membership and intra-group order; the group's position
    /// among other blocks comes from its id's slot in <see cref="DeviceOrder"/>.
    /// </summary>
    public List<DeviceGroup> DeviceGroups { get; set; } = new();

    /// <summary>
    /// Devices Earmark has seen at least once, so a disconnected device keeps its card (rendered
    /// dimmed) in its order / group slot instead of vanishing, and reconnect is a state toggle on a
    /// surviving element rather than a card-list rebuild (which is what makes the block slide animate
    /// and removes the connect/disconnect flash). Keyed by <see cref="KnownDevice.Key"/>
    /// (<see cref="Earmark.Core.Models.DeviceIdentity"/>); capped + aged out (see the Devices
    /// view-model). Populated as devices are enumerated; "Forget device" removes a row.
    /// </summary>
    public List<KnownDevice> KnownDevices { get; set; } = new();

    /// <summary>
    /// Apps the user has permanently hidden from the device cards' app-indicator rows, via a chip's
    /// "Hide this app" context menu. Hidden everywhere (on every device card), keyed by the app's
    /// <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/> (lower-cased executable path, or
    /// process name when the path is unavailable). The app still routes / plays normally; only its
    /// chip is suppressed. Managed (unhidden) from Settings &gt; App indicators.
    /// </summary>
    public List<HiddenApp> HiddenApps { get; set; } = new();

    /// <summary>
    /// Apps hidden from a single device card's chip row, via a chip's "Hide this app &gt; On this
    /// device" context menu. Unlike <see cref="HiddenApps"/> (hidden everywhere), these are keyed by
    /// the app's <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/> paired with the card's
    /// <see cref="Earmark.Core.Models.AudioEndpoint.Id"/>, so the app still shows on every other card.
    /// Managed (unhidden) from Settings &gt; App indicators alongside the global hides.
    /// </summary>
    public List<HiddenAppOnDevice> HiddenAppsOnDevice { get; set; } = new();

    /// <summary>Persisted window size in physical pixels. Null until the user has resized
    /// at least once (so first launch picks the WinUI default).</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    /// <summary>Whether the navigation pane is expanded. Persisted so the collapse/expand state of the
    /// left sidebar survives a relaunch. Default true (expanded), matching the WinUI default.</summary>
    public bool NavigationPaneOpen { get; set; } = true;
}

/// <summary>
/// One app the user has hidden from the Devices-page chip rows. <see cref="Key"/> is the match
/// target (an <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/>); <see cref="Name"/> is the
/// friendly label captured when it was hidden, so the manage list reads "Discord" rather than a raw
/// path even when the app isn't currently running.
/// </summary>
public sealed class HiddenApp
{
    /// <summary>Match key: the app's <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/>
    /// (lower-cased executable path, or process name when no path is available).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Friendly display name captured at hide time. Null falls back to a name derived from
    /// <see cref="Key"/>.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// One app hidden from a single device card's chip row. Keyed by the app's
/// <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/> (<see cref="Key"/>) plus the card's
/// <see cref="Earmark.Core.Models.AudioEndpoint.Id"/> (<see cref="EndpointId"/>). <see cref="Name"/>
/// and <see cref="DeviceName"/> are the friendly labels captured at hide time so the manage list
/// reads "Discord - on Speakers" without the app running or the device present.
/// </summary>
public sealed class HiddenAppOnDevice
{
    /// <summary>Match key: the app's <see cref="Earmark.Core.Models.AudioSession.IdentityKey"/>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The card's <see cref="Earmark.Core.Models.AudioEndpoint.Id"/> the app is hidden on.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>Friendly app name captured at hide time. Null falls back to a name derived from
    /// <see cref="Key"/>.</summary>
    public string? Name { get; set; }

    /// <summary>Friendly endpoint name captured at hide time, shown as context in the manage list.</summary>
    public string? DeviceName { get; set; }
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

    /// <summary>User has pinned this device to the Quick Controls overlay.</summary>
    public bool? PinnedToQuickControls { get; set; }

    /// <summary>User has hidden this device's volume slider + mute toggle (the card stays visible).
    /// For endpoints whose volume control doesn't affect output (e.g. a USB DAC/amp with an analog
    /// knob), Windows still reports a writable control, so this is a manual opt-out.</summary>
    public bool? VolumeControlsHidden { get; set; }

    /// <summary>User-chosen glyph override (a single Segoe Fluent codepoint string). Null = derive
    /// the glyph automatically from the device name / Wave Link channel.</summary>
    public string? Glyph { get; set; }

    /// <summary>User-chosen accent tile colour as "#AARRGGBB". Null = use the Wave Link accent (or
    /// the default tile when there is none).</summary>
    public string? AccentColour { get; set; }

    /// <summary>True when every flag is unset/false, so the entry carries no information and can be
    /// pruned from the map on save. Not serialised (it's a derived helper, not stored state).</summary>
    [JsonIgnore]
    public bool IsDefault => Hidden is not true && Pinned is not true && VolumeControlsHidden is not true
        && PinnedToQuickControls is not true && Glyph is null && AccentColour is null;
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

    /// <summary>Member <see cref="Earmark.Core.Models.DeviceIdentity"/> keys, in left-to-right member order.</summary>
    public List<string> MemberIds { get; set; } = new();
}

/// <summary>
/// A device Earmark has seen, persisted so it survives disconnect (and a driver reinstall, which
/// changes <see cref="LastEndpointId"/> but not <see cref="Key"/>). <see cref="Key"/> is the stable
/// <see cref="Earmark.Core.Models.DeviceIdentity"/>; the rest is the last-seen state used to render a
/// disconnected card (name / flow) and to resolve it back to a live endpoint when it reconnects.
/// </summary>
public sealed class KnownDevice
{
    /// <summary>Stable identity (<c>container|flow</c>, or a friendly-name fallback). The key the
    /// order / group / config stores reference.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The endpoint id this device most recently presented as. Refreshed every time the
    /// device is seen; used for display and to find the live card while connected.</summary>
    public string LastEndpointId { get; set; } = string.Empty;

    /// <summary>Friendly name captured at last sight, shown on the disconnected card.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Device description (adapter / driver) captured at last sight, shown as the subtitle.</summary>
    public string DeviceDescription { get; set; } = string.Empty;

    /// <summary>Render or capture - a device exposing both flows is two rows (the flow is part of the key).</summary>
    public EndpointFlow Flow { get; set; }

    /// <summary>The container id captured at last sight (for the Bluetooth control mapping); may be null.</summary>
    public string? ContainerId { get; set; }

    /// <summary>Whether the device was last seen as a Bluetooth endpoint.</summary>
    public bool IsBluetooth { get; set; }

    /// <summary>When the device was last enumerated (UTC). Drives the age-out prune.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }
}
