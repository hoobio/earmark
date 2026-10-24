using Avalonia.Media;

using Earmark.Core.Settings;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed class CardOptions : Observable
{
    public static readonly Color DefaultMeterColour = Color.FromRgb(0xBA, 0xE6, 0x7E);

    public bool ShowRules { get; private set; }

    public bool ShowDividers { get; private set; }

    public bool ShowBadges { get; private set; }

    public bool Compact { get; private set; }

    public bool ShowNowPlaying { get; private set; }

    public bool NowPlayingFill { get; private set; }

    public bool ShowAppIndicators { get; private set; }

    public bool ShowAppMeters { get; private set; }

    public bool AlwaysShowPinnedApps { get; private set; }

    public bool ShowHold { get; private set; }

    public PeakMeterColourMode MeterMode { get; private set; }

    public PeakMeterChannelMode ChannelMode { get; private set; }

    public Color MeterColour { get; private set; } = DefaultMeterColour;

    public CardHeightMode CardHeight { get; private set; }

    public bool ShowMeter => MeterMode != PeakMeterColourMode.Off;

    public static CardOptions For(AppSettings s, bool overlay, DeviceConfig? cfg)
    {
        var o = new CardOptions();
        o.Apply(s, overlay, cfg);
        return o;
    }

    public void Apply(AppSettings s, bool overlay, DeviceConfig? cfg)
    {
        ShowRules = cfg?.ShowRules ?? (overlay ? s.QuickControlsShowRules : s.ShowRules);
        ShowDividers = overlay ? s.QuickControlsShowDividers : s.ShowCardDividers;
        ShowBadges = cfg?.ShowDeviceBadges ?? (overlay ? s.QuickControlsShowDeviceBadges : s.ShowDeviceBadges);
        Compact = overlay ? s.QuickControlsCompact : s.CompactCards;
        ShowNowPlaying = cfg?.ShowNowPlaying ?? (overlay ? s.QuickControlsShowNowPlaying : s.ShowNowPlaying);
        NowPlayingFill = cfg?.NowPlayingFill ?? s.NowPlayingCardBackground;
        ShowAppIndicators = cfg?.ShowAppIndicators ?? s.ShowAppIndicators;
        ShowAppMeters = cfg?.ShowAppMeters ?? s.ShowAppPeakMeters;
        AlwaysShowPinnedApps = s.AlwaysShowPinnedApps;
        ShowHold = cfg?.ShowPeakIndicator ?? s.PeakMeterShowHold;
        MeterMode = cfg?.MeterEnabled switch
        {
            false => PeakMeterColourMode.Off,
            true when s.PeakMeterColourMode == PeakMeterColourMode.Off => PeakMeterColourMode.Gradient,
            _ => s.PeakMeterColourMode,
        };
        ChannelMode = s.PeakMeterChannelMode;
        MeterColour = Color.TryParse(s.PeakMeterSingleColour, out var c) ? c : DefaultMeterColour;
        CardHeight = s.CardHeight;
        RaiseAll();
    }
}
