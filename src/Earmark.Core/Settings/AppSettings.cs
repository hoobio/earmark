using System.Text.Json.Serialization;

using Earmark.Core.Models;

namespace Earmark.Core.Settings;

public sealed class AppSettings
{
    public int SettingsSchemaVersion { get; set; }

    [JsonIgnore]
    public const int DeviceKeySchemaVersion = 1;

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

    public bool QuickControlsShowRules { get; set; }

    public bool QuickControlsShowNowPlaying { get; set; } = true;

    public bool QuickControlsShowDeviceBadges { get; set; }

    public bool QuickControlsShowDividers { get; set; }

    public bool QuickControlsCompact { get; set; } = true;

    public bool VerboseLogging { get; set; }

    public bool CheckForUpdates { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;

    public bool EnableWaveLink { get; set; }

    public WaveLinkChannelStyle WaveLinkChannelStyle { get; set; } = WaveLinkChannelStyle.Colours;

    // Clamped to 0-600 when read.
    public int AppChipLingerSeconds { get; set; } = 30;

    public PeakMeterColourMode PeakMeterColourMode { get; set; } = PeakMeterColourMode.Gradient;

    public string? PeakMeterSingleColour { get; set; }

    public PeakMeterChannelMode PeakMeterChannelMode { get; set; } = PeakMeterChannelMode.Split;

    public bool PeakMeterShowHold { get; set; } = true;

    public bool ShowAppIndicators { get; set; } = true;

    public bool ShowAppPeakMeters { get; set; } = true;

    public bool FilterAudioForwarders { get; set; } = true;

    public bool ShowCardDividers { get; set; } = true;

    public bool CompactCards { get; set; }

    public bool ShowDeviceBadges { get; set; } = true;

    public bool ShowTitleBarText { get; set; } = true;

    public bool ShowRules { get; set; } = true;

    public bool ShowNowPlaying { get; set; } = true;

    public bool NowPlayingCardBackground { get; set; } = true;

    public CardHeightMode CardHeight { get; set; } = CardHeightMode.Balanced;

    public bool AlwaysShowPinnedApps { get; set; } = true;

    public bool ShowDevicesPageHeader { get; set; } = true;

    public bool LockDeviceLayout { get; set; }

    public bool ShowDisconnectedDevices { get; set; }

    public Dictionary<string, DeviceConfig> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Group members are omitted: their slot is the group's and their order is DeviceGroup.MemberIds.
    public List<string> DeviceOrder { get; set; } = new();

    public List<DeviceGroup> DeviceGroups { get; set; } = new();

    public List<KnownDevice> KnownDevices { get; set; } = new();

    public List<HiddenApp> HiddenApps { get; set; } = new();

    public List<HiddenAppOnDevice> HiddenAppsOnDevice { get; set; } = new();

    public bool NavigationPaneOpen { get; set; } = true;
}

public sealed class HiddenApp
{
    public string Key { get; set; } = string.Empty;

    public string? Name { get; set; }
}

public sealed class HiddenAppOnDevice
{
    public string Key { get; set; } = string.Empty;

    public string EndpointId { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? DeviceName { get; set; }
}

public sealed class DeviceConfig
{
    public bool? Hidden { get; set; }

    public bool? Pinned { get; set; }

    public bool? PinnedToQuickControls { get; set; }

    // Windows reports a writable volume control even for endpoints where it does nothing (USB DAC/amp with an analog knob), so this is a manual opt-out.
    public bool? VolumeControlsHidden { get; set; }

    public string? Glyph { get; set; }

    public string? AccentColour { get; set; }

    public bool? ShowNowPlaying { get; set; }

    public bool? NowPlayingFill { get; set; }

    public bool? ShowAppIndicators { get; set; }

    public bool? ShowAppMeters { get; set; }

    public bool? MeterEnabled { get; set; }

    public bool? ShowPeakIndicator { get; set; }

    public bool? ShowRules { get; set; }

    public bool? ShowDeviceBadges { get; set; }

    [JsonIgnore]
    public bool IsDefault => Hidden is not true && Pinned is not true && VolumeControlsHidden is not true
        && PinnedToQuickControls is not true && Glyph is null && AccentColour is null
        && ShowNowPlaying is null && NowPlayingFill is null && ShowAppIndicators is null
        && ShowAppMeters is null && MeterEnabled is null && ShowPeakIndicator is null && ShowRules is null
        && ShowDeviceBadges is null;
}

public sealed class DeviceGroup
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> MemberIds { get; set; } = new();
}

public sealed class KnownDevice
{
    public string Key { get; set; } = string.Empty;

    public string LastEndpointId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string DeviceDescription { get; set; } = string.Empty;

    public EndpointFlow Flow { get; set; }

    public string? ContainerId { get; set; }

    public bool IsBluetooth { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}
