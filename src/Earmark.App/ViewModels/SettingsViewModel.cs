using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.Core.WaveLink;

using Windows.UI;

namespace Earmark.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IWaveLinkService _waveLink;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly IDeviceDefaultsService _defaults;
    private bool _suppress;

    public SettingsViewModel(
        ISettingsService settings,
        IWaveLinkService waveLink,
        IDispatcherQueueProvider dispatcher,
        IDeviceDefaultsService defaults)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _waveLink.StateChanged += OnWaveLinkStateChanged;
        SyncFromSettings();
        SyncFromWaveLink();
    }

    /// <summary>Restores the Devices page to its default groups, order, and visibility. Rules and all
    /// other settings are left untouched. The Devices page rebuilds via the service's event.</summary>
    public Task ResetDeviceLayoutAsync() => _defaults.ResetDeviceLayoutAsync();

    [ObservableProperty]
    public partial bool LaunchOnStartup { get; set; }

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool LaunchToTray { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    /// <summary>Master toggle for the per-app indicator chips under each device card.</summary>
    [ObservableProperty]
    public partial bool ShowAppIndicators { get; set; }

    /// <summary>Whether each app chip shows its thin peak-level underbar. Only meaningful when
    /// <see cref="ShowAppIndicators"/> is on.</summary>
    [ObservableProperty]
    public partial bool ShowAppPeakMeters { get; set; }

    /// <summary>Whether known audio forwarders (Wave Link, VB-Cable, ...) are hidden from the app
    /// indicators. Only meaningful when <see cref="ShowAppIndicators"/> is on.</summary>
    [ObservableProperty]
    public partial bool FilterAudioForwarders { get; set; }

    /// <summary>Whether a rule-pinned app always shows its chip (and the lock padlock badge), or only
    /// while it's audible. Only meaningful when <see cref="ShowAppIndicators"/> is on.</summary>
    [ObservableProperty]
    public partial bool AlwaysShowPinnedApps { get; set; }

    /// <summary>Seconds a Devices-page app chip lingers (dimmed) after its app stops playing or
    /// closes, before it's removed. Bound to a NumberBox, so it's a double here; persisted as a
    /// clamped int.</summary>
    [ObservableProperty]
    public partial double AppChipLingerSeconds { get; set; }

    /// <summary>Card-height ComboBox index. Maps 1:1 to <see cref="CardHeightMode"/>
    /// (Balanced=0, MatchRow=1, Dynamic=2).</summary>
    [ObservableProperty]
    public partial int CardHeightModeIndex { get; set; }

    /// <summary>Whether device cards draw hairline separators between their sections. Default on.</summary>
    [ObservableProperty]
    public partial bool ShowCardDividers { get; set; }

    /// <summary>Whether each device card shows its rules section and no-rules text. Default on.</summary>
    [ObservableProperty]
    public partial bool ShowRules { get; set; }

    /// <summary>Bound to the theme ComboBox SelectedIndex. Maps 1:1 to <see cref="AppTheme"/>
    /// (System=0, Light=1, Dark=2).</summary>
    [ObservableProperty]
    public partial int AppThemeIndex { get; set; }

    /// <summary>Bound to the backdrop ComboBox SelectedIndex. Maps 1:1 to <see cref="BackdropMode"/>
    /// (Mica=0, Acrylic=1, Solid=2).</summary>
    [ObservableProperty]
    public partial int BackdropIndex { get; set; }

    [ObservableProperty]
    public partial bool EnableWaveLink { get; set; }

    [ObservableProperty]
    public partial bool ReconcileWaveLinkNames { get; set; }

    /// <summary>Bound to the channel-style ComboBox SelectedIndex. Maps 1:1 to
    /// <see cref="WaveLinkChannelStyle"/> (Off=0, Colours=1, Icons=2). Index binding sidesteps
    /// the value-type SelectedItem NRE during ComboBox item recycling.</summary>
    [ObservableProperty]
    public partial int WaveLinkChannelStyleIndex { get; set; }

    /// <summary>Peak meter colour mode ComboBox index. Maps 1:1 to <see cref="PeakMeterColourMode"/>
    /// (Gradient=0, Blocks=1, Single=2, Off=3).</summary>
    [ObservableProperty]
    public partial int PeakMeterColourModeIndex { get; set; }

    /// <summary>Peak meter channel mode ComboBox index. Maps 1:1 to <see cref="PeakMeterChannelMode"/>
    /// (Split=0, Single=1).</summary>
    [ObservableProperty]
    public partial int PeakMeterChannelModeIndex { get; set; }

    [ObservableProperty]
    public partial bool PeakMeterShowHold { get; set; }

    /// <summary>Chosen bar colour for the Single colour mode. Bound to the colour picker / swatches.</summary>
    [ObservableProperty]
    public partial Color PeakMeterSingleColour { get; set; }

    /// <summary>Gates the "Bar colour" picker - only meaningful in the Single colour mode.</summary>
    public bool IsSingleColourMode => PeakMeterColourModeIndex == (int)PeakMeterColourMode.Solid;

    /// <summary>Gates the channels / peak-hold settings - meaningless when the meter is Off.</summary>
    public bool IsMeterEnabled => PeakMeterColourModeIndex != (int)PeakMeterColourMode.Off;

    [ObservableProperty]
    public partial WaveLinkConnectionState WaveLinkState { get; set; }

    public string WaveLinkStatusText => WaveLinkState switch
    {
        WaveLinkConnectionState.Connected => "Connected",
        WaveLinkConnectionState.Unavailable => "Wave Link not running",
        _ => "Off",
    };

    public string WaveLinkStatusGlyph => WaveLinkState switch
    {
        WaveLinkConnectionState.Connected => "",   // checkmark
        WaveLinkConnectionState.Unavailable => "", // warning triangle
        _ => "",                                    // cancel / dot
    };

    public string WaveLinkStatusBrushKey => WaveLinkState switch
    {
        WaveLinkConnectionState.Connected => "SystemFillColorSuccessBrush",
        WaveLinkConnectionState.Unavailable => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    public void SyncFromSettings()
    {
        _suppress = true;
        try
        {
            LaunchOnStartup = _settings.Current.LaunchOnStartup;
            ShowTrayIcon = _settings.Current.ShowTrayIcon;
            MinimizeToTray = _settings.Current.MinimizeToTray;
            CloseToTray = _settings.Current.CloseToTray;
            LaunchToTray = _settings.Current.LaunchToTray;
            VerboseLogging = _settings.Current.VerboseLogging;
            ShowAppIndicators = _settings.Current.ShowAppIndicators;
            ShowAppPeakMeters = _settings.Current.ShowAppPeakMeters;
            FilterAudioForwarders = _settings.Current.FilterAudioForwarders;
            AlwaysShowPinnedApps = _settings.Current.AlwaysShowPinnedApps;
            AppChipLingerSeconds = _settings.Current.AppChipLingerSeconds;
            CardHeightModeIndex = (int)_settings.Current.CardHeight;
            ShowCardDividers = _settings.Current.ShowCardDividers;
            ShowRules = _settings.Current.ShowRules;
            AppThemeIndex = (int)_settings.Current.Theme;
            BackdropIndex = (int)_settings.Current.Backdrop;
            EnableWaveLink = _settings.Current.EnableWaveLink;
            ReconcileWaveLinkNames = _settings.Current.ReconcileWaveLinkNames;
            WaveLinkChannelStyleIndex = (int)_settings.Current.WaveLinkChannelStyle;
            PeakMeterColourModeIndex = (int)_settings.Current.PeakMeterColourMode;
            PeakMeterChannelModeIndex = (int)_settings.Current.PeakMeterChannelMode;
            PeakMeterShowHold = _settings.Current.PeakMeterShowHold;
            PeakMeterSingleColour = PeakMeterOptions.ColourFromHex(_settings.Current.PeakMeterSingleColour);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SyncFromWaveLink() => WaveLinkState = _waveLink.State;

    private void OnWaveLinkStateChanged(object? sender, EventArgs e) =>
        _dispatcher.Enqueue(() => WaveLinkState = _waveLink.State);

    partial void OnLaunchOnStartupChanged(bool value) => Persist(s => s.LaunchOnStartup = value);
    partial void OnShowTrayIconChanged(bool value) => Persist(s => s.ShowTrayIcon = value);
    partial void OnMinimizeToTrayChanged(bool value) => Persist(s => s.MinimizeToTray = value);
    partial void OnCloseToTrayChanged(bool value) => Persist(s => s.CloseToTray = value);
    partial void OnLaunchToTrayChanged(bool value) => Persist(s => s.LaunchToTray = value);
    partial void OnVerboseLoggingChanged(bool value) => Persist(s => s.VerboseLogging = value);

    partial void OnShowAppIndicatorsChanged(bool value)
    {
        Persist(s => s.ShowAppIndicators = value);
        // The child cards (volume meters, forwarder filter, linger) are meaningless with no chips.
        OnPropertyChanged(nameof(AppIndicatorChildrenEnabled));
    }
    partial void OnShowAppPeakMetersChanged(bool value) => Persist(s => s.ShowAppPeakMeters = value);
    partial void OnFilterAudioForwardersChanged(bool value) => Persist(s => s.FilterAudioForwarders = value);
    partial void OnAlwaysShowPinnedAppsChanged(bool value) => Persist(s => s.AlwaysShowPinnedApps = value);

    /// <summary>Gates the app-indicator child settings - they only matter while the chips show.</summary>
    public bool AppIndicatorChildrenEnabled => ShowAppIndicators;

    // ---- Hidden apps (chip "Hide this app" context menu) ----

    /// <summary>Apps the user has permanently hidden from the device cards' chip rows. Populated by
    /// <see cref="LoadHiddenApps"/> when the manage modal opens, and mutated by unhide / clear.</summary>
    public ObservableCollection<HiddenAppRow> HiddenApps { get; } = new();

    private int HiddenAppsTotal => _settings.Current.HiddenApps.Count + _settings.Current.HiddenAppsOnDevice.Count;
    public bool HasHiddenApps => HiddenAppsTotal > 0;
    public bool NoHiddenApps => !HasHiddenApps;

    /// <summary>Card description that reflects how many apps are hidden.</summary>
    public string HiddenAppsDescription
    {
        get
        {
            var n = HiddenAppsTotal;
            return n == 0
                ? "Apps you've hidden from the device cards with a chip's right-click menu (everywhere or on one device). None hidden yet."
                : $"{n} app{(n == 1 ? "" : "s")} hidden from the device cards. Manage to unhide.";
        }
    }

    /// <summary>(Re)loads the manage list from settings - called when the modal opens so it reflects
    /// hides made on the Devices page since the page was last shown.</summary>
    public void LoadHiddenApps()
    {
        HiddenApps.Clear();
        foreach (var app in _settings.Current.HiddenApps)
        {
            HiddenApps.Add(new HiddenAppRow(app.Key, app.Name));
        }
        foreach (var app in _settings.Current.HiddenAppsOnDevice)
        {
            HiddenApps.Add(new HiddenAppRow(app.Key, app.EndpointId, app.Name, app.DeviceName));
        }
        RefreshHiddenAppsState();
    }

    /// <summary>Unhides one app: its chip can reappear on the device cards. Persists immediately;
    /// <c>HomeViewModel</c> reconciles via the settings-changed event.</summary>
    public void UnhideApp(HiddenAppRow row)
    {
        if (row is null) return;
        HiddenApps.Remove(row);
        if (row.IsPerDevice)
        {
            Persist(s => s.HiddenAppsOnDevice.RemoveAll(h =>
                string.Equals(h.Key, row.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(h.EndpointId, row.EndpointId, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            Persist(s => s.HiddenApps.RemoveAll(h => string.Equals(h.Key, row.Key, StringComparison.OrdinalIgnoreCase)));
        }
        RefreshHiddenAppsState();
    }

    /// <summary>Unhides every app at once (both global and per-device hides).</summary>
    public void ClearAllHiddenApps()
    {
        if (HiddenAppsTotal == 0) return;
        HiddenApps.Clear();
        Persist(s => { s.HiddenApps.Clear(); s.HiddenAppsOnDevice.Clear(); });
        RefreshHiddenAppsState();
    }

    /// <summary>Refreshes the count-derived bindings. Also called by the page on navigation so the
    /// card description tracks hides made from the Devices page.</summary>
    public void RefreshHiddenAppsState()
    {
        OnPropertyChanged(nameof(HasHiddenApps));
        OnPropertyChanged(nameof(NoHiddenApps));
        OnPropertyChanged(nameof(HiddenAppsDescription));
    }

    partial void OnAppChipLingerSecondsChanged(double value)
    {
        // NumberBox hands back NaN when cleared; ignore until it carries a real number again.
        if (double.IsNaN(value)) return;
        var seconds = (int)Math.Clamp(Math.Round(value), 0, 600);
        Persist(s => s.AppChipLingerSeconds = seconds);
    }
    partial void OnCardHeightModeIndexChanged(int value) => Persist(s => s.CardHeight = (CardHeightMode)value);
    partial void OnShowCardDividersChanged(bool value) => Persist(s => s.ShowCardDividers = value);
    partial void OnShowRulesChanged(bool value) => Persist(s => s.ShowRules = value);
    partial void OnAppThemeIndexChanged(int value) => Persist(s => s.Theme = (AppTheme)value);
    partial void OnBackdropIndexChanged(int value) => Persist(s => s.Backdrop = (BackdropMode)value);
    partial void OnEnableWaveLinkChanged(bool value) => Persist(s => s.EnableWaveLink = value);
    partial void OnReconcileWaveLinkNamesChanged(bool value) => Persist(s => s.ReconcileWaveLinkNames = value);
    partial void OnWaveLinkChannelStyleIndexChanged(int value) => Persist(s => s.WaveLinkChannelStyle = (WaveLinkChannelStyle)value);

    partial void OnPeakMeterColourModeIndexChanged(int value)
    {
        Persist(s => s.PeakMeterColourMode = (PeakMeterColourMode)value);
        OnPropertyChanged(nameof(IsSingleColourMode));
        OnPropertyChanged(nameof(IsMeterEnabled));
    }
    partial void OnPeakMeterChannelModeIndexChanged(int value) => Persist(s => s.PeakMeterChannelMode = (PeakMeterChannelMode)value);
    partial void OnPeakMeterShowHoldChanged(bool value) => Persist(s => s.PeakMeterShowHold = value);
    partial void OnPeakMeterSingleColourChanged(Color value) => Persist(s => s.PeakMeterSingleColour = PeakMeterOptions.ToHex(value));

    /// <summary>Resets the Single-mode bar colour to the default green (clears the persisted
    /// override). Invoked from the colour picker's reset button.</summary>
    public void ResetPeakMeterColour() => PeakMeterSingleColour = PeakMeterOptions.DefaultColour;

    partial void OnWaveLinkStateChanged(WaveLinkConnectionState value)
    {
        OnPropertyChanged(nameof(WaveLinkStatusText));
        OnPropertyChanged(nameof(WaveLinkStatusGlyph));
        OnPropertyChanged(nameof(WaveLinkStatusBrushKey));
    }

    private async void Persist(Action<AppSettings> mutate)
    {
        if (_suppress)
        {
            return;
        }

        mutate(_settings.Current);
        try { await _settings.SaveAsync(); }
        catch { /* SettingsService logs/retries internally; a save failure must not crash the UI thread (async void). */ }
    }

    public void Dispose()
    {
        _waveLink.StateChanged -= OnWaveLinkStateChanged;
    }
}

/// <summary>A row in the "Hidden apps" manage list. <see cref="Name"/> is the friendly label (the
/// session display name captured at hide time, falling back to the executable filename);
/// <see cref="Detail"/> is the full identity key, shown as muted subtext when it differs.</summary>
public sealed class HiddenAppRow
{
    /// <summary>Global hide (hidden on every device).</summary>
    public HiddenAppRow(string key, string? name)
    {
        Key = key ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? DeriveName(Key) : name!;
    }

    /// <summary>Per-device hide (hidden on one endpoint only).</summary>
    public HiddenAppRow(string key, string endpointId, string? name, string? deviceName)
        : this(key, name)
    {
        EndpointId = endpointId ?? string.Empty;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "this device" : deviceName!;
    }

    private readonly string? _deviceName;

    /// <summary>Match key (the app's identity key) used to remove the entry on unhide.</summary>
    public string Key { get; }

    /// <summary>Endpoint id for a per-device hide; empty for a global hide.</summary>
    public string EndpointId { get; } = string.Empty;

    public bool IsPerDevice => !string.IsNullOrEmpty(EndpointId);

    public string Name { get; }

    /// <summary>Muted subtext: the hidden-on device for a per-device row, else the identity key.</summary>
    public string Detail => IsPerDevice ? $"on {_deviceName} - {Key}" : Key;

    public bool HasDetail => IsPerDevice ||
        (!string.IsNullOrEmpty(Key) && !string.Equals(Key, Name, StringComparison.OrdinalIgnoreCase));

    private static string DeriveName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "Unknown app";
        try
        {
            var file = System.IO.Path.GetFileName(key);
            return string.IsNullOrEmpty(file) ? key : file;
        }
        catch
        {
            return key;
        }
    }
}
