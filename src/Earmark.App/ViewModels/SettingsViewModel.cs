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
    private bool _suppress;

    public SettingsViewModel(
        ISettingsService settings,
        IWaveLinkService waveLink,
        IDispatcherQueueProvider dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _waveLink.StateChanged += OnWaveLinkStateChanged;
        SyncFromSettings();
        SyncFromWaveLink();
    }

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

    /// <summary>Seconds a Devices-page app chip lingers (dimmed) after its app stops playing or
    /// closes, before it's removed. Bound to a NumberBox, so it's a double here; persisted as a
    /// clamped int.</summary>
    [ObservableProperty]
    public partial double AppChipLingerSeconds { get; set; }

    /// <summary>Bound to the theme ComboBox SelectedIndex. Maps 1:1 to <see cref="AppTheme"/>
    /// (System=0, Light=1, Dark=2).</summary>
    [ObservableProperty]
    public partial int AppThemeIndex { get; set; }

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
            AppChipLingerSeconds = _settings.Current.AppChipLingerSeconds;
            AppThemeIndex = (int)_settings.Current.Theme;
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

    partial void OnAppChipLingerSecondsChanged(double value)
    {
        // NumberBox hands back NaN when cleared; ignore until it carries a real number again.
        if (double.IsNaN(value)) return;
        var seconds = (int)Math.Clamp(Math.Round(value), 0, 600);
        Persist(s => s.AppChipLingerSeconds = seconds);
    }
    partial void OnAppThemeIndexChanged(int value) => Persist(s => s.Theme = (AppTheme)value);
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
