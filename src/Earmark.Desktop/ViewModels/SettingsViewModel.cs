using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

using Avalonia.Media;

using Earmark.Audio.Services;
using Earmark.Core;
using Earmark.Core.Services;
using Earmark.Core.Settings;
using Earmark.Core.WaveLink;
using Earmark.Desktop.Services;

using Microsoft.Extensions.Logging;

using StartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace Earmark.Desktop.ViewModels;

public sealed class SettingsViewModel : NotifyBase
{
    public const string DefaultMeterColour = "#FF6CCB5F";

    public static IReadOnlyList<string> MeterSwatches { get; } = [DefaultMeterColour, "#FF73D0FF", "#FFFFCC66", "#FFF28779", "#FFDFBFFF", "#FFFFFFFF"];

    private readonly SettingsService _settings;
    private readonly IWaveLinkService _waveLink;
    private readonly DeviceDefaultsService _defaults;
    private readonly StartupSettingsApplier _startup;
    private readonly ILogger<SettingsViewModel> _logger;
    private IReadOnlyList<OverrideRow> _overrides = [];

    public SettingsViewModel(SettingsService settings, IWaveLinkService waveLink, DeviceDefaultsService defaults, StartupSettingsApplier startup, AboutViewModel about, ILogger<SettingsViewModel> logger)
    {
        About = about;
        _settings = settings;
        _waveLink = waveLink;
        _defaults = defaults;
        _startup = startup;
        _logger = logger;
        settings.SettingsChanged += (_, _) => Ui.Post(Reload);
        startup.StartupTaskStateChanged += (_, _) => Ui.Post(() => Raise(nameof(LaunchOnStartupDescription)));
        waveLink.StateChanged += (_, _) => Ui.Post(() =>
        {
            Raise(nameof(WaveLinkStatus));
            Raise(nameof(WaveLinkBrush));
        });
        HiddenApps.CollectionChanged += (_, _) =>
        {
            Raise(nameof(NoHiddenApps));
            Raise(nameof(HiddenAppsDescription));
        };
        RefreshOverrides();
    }

    public AboutViewModel About { get; }

    public ObservableCollection<HiddenAppRow> HiddenApps { get; } = [];

    public bool NoHiddenApps => HiddenApps.Count == 0;

    public IReadOnlyList<OverrideRow> Overrides
    {
        get => _overrides;
        private set
        {
            _overrides = value;
            Raise();
            Raise(nameof(HasOverrides));
        }
    }

    public bool HasOverrides => _overrides.Count > 0;

    public bool LaunchOnStartup { get => CanLaunchOnStartup && S.LaunchOnStartup; set => Set(s => s.LaunchOnStartup = value); }

    public static bool CanLaunchOnStartup => AppInfo.IsRelease;

    public string LaunchOnStartupDescription => !AppInfo.IsRelease
        ? $"Off for {AppInfo.BuildChannel} builds. Only release builds start at sign-in."
        : _startup.StartupTaskState switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Task Manager > Startup apps. Turn Earmark on there to start it at sign-in.",
            StartupTaskState.DisabledByPolicy => "Turned off by your organisation's policy.",
            StartupTaskState.EnabledByPolicy => "Turned on by your organisation's policy.",
            _ => "Start Earmark automatically when you sign in to Windows.",
        };

    public bool ShowTrayIcon { get => S.ShowTrayIcon; set => Set(s => s.ShowTrayIcon = value, nameof(ShowTrayIcon), nameof(TrayChildrenEnabled)); }

    public bool MinimizeToTray { get => S.MinimizeToTray; set => Set(s => s.MinimizeToTray = value); }

    public bool CloseToTray { get => S.CloseToTray; set => Set(s => s.CloseToTray = value); }

    public bool LaunchToTray { get => S.LaunchToTray; set => Set(s => s.LaunchToTray = value); }

    public bool TrayChildrenEnabled => S.ShowTrayIcon;

    public bool VerboseLogging { get => S.VerboseLogging; set => Set(s => s.VerboseLogging = value); }

    public int BackdropIndex { get => S.Backdrop == BackdropMode.Solid ? 1 : 0; set => Set(s => s.Backdrop = value == 1 ? BackdropMode.Solid : BackdropMode.Mica); }

    public bool ShowTitleBarText { get => S.ShowTitleBarText; set => Set(s => s.ShowTitleBarText = value); }

    public int CardHeightIndex { get => (int)S.CardHeight; set => Set(s => s.CardHeight = (CardHeightMode)value); }

    public bool CompactCards { get => S.CompactCards; set => Set(s => s.CompactCards = value); }

    public bool ShowCardDividers { get => S.ShowCardDividers; set => Set(s => s.ShowCardDividers = value); }

    public bool ShowDeviceBadges { get => S.ShowDeviceBadges; set => Set(s => s.ShowDeviceBadges = value); }

    public bool ShowRules { get => S.ShowRules; set => Set(s => s.ShowRules = value); }

    public bool ShowAppIndicators { get => S.ShowAppIndicators; set => Set(s => s.ShowAppIndicators = value); }

    public bool ShowAppPeakMeters { get => S.ShowAppPeakMeters; set => Set(s => s.ShowAppPeakMeters = value); }

    public bool FilterAudioForwarders { get => S.FilterAudioForwarders; set => Set(s => s.FilterAudioForwarders = value); }

    public bool AlwaysShowPinnedApps { get => S.AlwaysShowPinnedApps; set => Set(s => s.AlwaysShowPinnedApps = value); }

    public decimal? AppChipLingerSeconds { get => S.AppChipLingerSeconds; set => Set(s => s.AppChipLingerSeconds = (int)Math.Clamp(Math.Round(value ?? 0), 0, 600)); }

    public string HiddenAppsDescription
    {
        get
        {
            var count = S.HiddenApps.Count + S.HiddenAppsOnDevice.Count;
            return count == 0
                ? "Apps hidden from the device cards with a chip's menu (everywhere or on one device). None hidden yet."
                : $"{count} app{(count == 1 ? "" : "s")} hidden from the device cards. Manage to unhide.";
        }
    }

    public bool ShowNowPlaying { get => S.ShowNowPlaying; set => Set(s => s.ShowNowPlaying = value); }

    public bool NowPlayingCardBackground { get => S.NowPlayingCardBackground; set => Set(s => s.NowPlayingCardBackground = value); }

    public int PeakMeterColourModeIndex { get => (int)S.PeakMeterColourMode; set => Set(s => s.PeakMeterColourMode = (PeakMeterColourMode)value); }

    public bool IsSingleColourMode => S.PeakMeterColourMode == PeakMeterColourMode.Solid;

    public bool IsMeterEnabled => S.PeakMeterColourMode != PeakMeterColourMode.Off;

    public int PeakMeterChannelModeIndex { get => (int)S.PeakMeterChannelMode; set => Set(s => s.PeakMeterChannelMode = (PeakMeterChannelMode)value); }

    public bool PeakMeterShowHold { get => S.PeakMeterShowHold; set => Set(s => s.PeakMeterShowHold = value); }

    public string PeakMeterSingleColour
    {
        get => S.PeakMeterSingleColour ?? DefaultMeterColour;
        set
        {
            if (Color.TryParse(value, out _))
            {
                Set(s => s.PeakMeterSingleColour = value);
            }
        }
    }

    public IBrush PeakMeterBrush => new SolidColorBrush(Color.TryParse(PeakMeterSingleColour, out var colour) ? colour : Colors.Gray);

    public bool EnableWaveLink { get => S.EnableWaveLink; set => Set(s => s.EnableWaveLink = value); }

    public int WaveLinkChannelStyleIndex { get => (int)S.WaveLinkChannelStyle; set => Set(s => s.WaveLinkChannelStyle = (WaveLinkChannelStyle)value); }

    public string WaveLinkStatus => _waveLink.State switch
    {
        WaveLinkConnectionState.Connected => "Connected",
        WaveLinkConnectionState.Unavailable => "Wave Link not running",
        _ => "Off",
    };

    public string WaveLinkBrush => _waveLink.State switch
    {
        WaveLinkConnectionState.Connected => "SystemFillColorSuccessBrush",
        WaveLinkConnectionState.Unavailable => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    private AppSettings S => _settings.Current;

    public Task ResetDeviceLayoutAsync() => _defaults.ResetDeviceLayoutAsync();

    public void LoadHiddenApps()
    {
        HiddenApps.Clear();
        foreach (var app in S.HiddenApps)
        {
            HiddenApps.Add(new HiddenAppRow(app.Key, string.Empty, app.Name, null));
        }

        foreach (var app in S.HiddenAppsOnDevice)
        {
            HiddenApps.Add(new HiddenAppRow(app.Key, app.EndpointId, app.Name, app.DeviceName));
        }
    }

    public void Unhide(HiddenAppRow row)
    {
        HiddenApps.Remove(row);
        Mutate(s =>
        {
            if (row.IsPerDevice)
            {
                s.HiddenAppsOnDevice.RemoveAll(h => string.Equals(h.Key, row.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(h.EndpointId, row.EndpointId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                s.HiddenApps.RemoveAll(h => string.Equals(h.Key, row.Key, StringComparison.OrdinalIgnoreCase));
            }
        });
    }

    public void UnhideAll()
    {
        HiddenApps.Clear();
        Mutate(s =>
        {
            s.HiddenApps.Clear();
            s.HiddenAppsOnDevice.Clear();
        });
    }

    private void Set(Action<AppSettings> mutate, [CallerMemberName] string? name = null)
    {
        Mutate(mutate);
        Raise(name);
    }

    private void Set(Action<AppSettings> mutate, params string[] names)
    {
        Mutate(mutate);
        foreach (var name in names)
        {
            Raise(name);
        }
    }

    private async void Mutate(Action<AppSettings> mutate)
    {
        mutate(S);
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
        }
    }

    private void Reload()
    {
        Raise(string.Empty);
        RefreshOverrides();
    }

    private void RefreshOverrides()
    {
        var rows = new List<OverrideRow>();
        AddOverride(rows, "Now playing", c => c.ShowNowPlaying, c => c.ShowNowPlaying = null);
        AddOverride(rows, "Fill card background", c => c.NowPlayingFill, c => c.NowPlayingFill = null);
        AddOverride(rows, "App indicators", c => c.ShowAppIndicators, c => c.ShowAppIndicators = null);
        AddOverride(rows, "Volume meters", c => c.ShowAppMeters, c => c.ShowAppMeters = null);
        AddOverride(rows, "Peak meter", c => c.MeterEnabled, c => c.MeterEnabled = null);
        AddOverride(rows, "Peak-hold tick", c => c.ShowPeakIndicator, c => c.ShowPeakIndicator = null);
        AddOverride(rows, "Show rules", c => c.ShowRules, c => c.ShowRules = null);
        Overrides = rows;
    }

    private void AddOverride(List<OverrideRow> rows, string label, Func<DeviceConfig, bool?> selector, Action<DeviceConfig> clear)
    {
        var names = S.Devices
            .Where(pair => selector(pair.Value) is not null)
            .Select(pair => S.KnownDevices.FirstOrDefault(known => string.Equals(known.Key, pair.Key, StringComparison.OrdinalIgnoreCase))?.FriendlyName is { Length: > 0 } name ? name : pair.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        rows.Add(new OverrideRow(
            label,
            names.Count == 1 ? "1 device overrides this setting" : $"{names.Count} devices override this setting",
            string.Join('\n', names),
            () => Mutate(s =>
            {
                foreach (var key in s.Devices.Keys.ToList())
                {
                    clear(s.Devices[key]);
                    if (s.Devices[key].IsDefault)
                    {
                        s.Devices.Remove(key);
                    }
                }
            })));
    }
}

public sealed record OverrideRow(string Label, string Message, string Tooltip, Action Clear);

public sealed class HiddenAppRow(string key, string endpointId, string? name, string? deviceName)
{
    public string Key { get; } = key;

    public string EndpointId { get; } = endpointId;

    public bool IsPerDevice => EndpointId.Length > 0;

    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? (Path.GetFileName(key) is { Length: > 0 } file ? file : key) : name;

    public string Detail => IsPerDevice ? $"on {(string.IsNullOrWhiteSpace(deviceName) ? "this device" : deviceName)} - {Key}" : Key;

    public bool HasDetail => IsPerDevice || !string.Equals(Key, Name, StringComparison.OrdinalIgnoreCase);
}
