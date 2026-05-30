using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Settings;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// One colour-banded peak bar inside a <see cref="ChannelPeakMeter"/>. Holds the live level,
/// optional latched peak-hold, and the volume scale, and exposes the per-band
/// <see cref="GridLength"/> column widths the bar template binds to.
///
/// The meter is log-scaled so it reads in dB like a pro-audio VU meter. Linear amplitude is
/// converted to dBFS over a [-60, 0] window, mapped to bar position [0..1], then bounded on the
/// right by the volume thumb (final width = barPosition(level) * volume). Colour thresholds are
/// fixed dBFS values; each boundary carries a narrow ±BlendHalf gradient band so the transition
/// is smooth while the dB-accurate centre stays on the threshold:
///   -inf .. -12 dBFS -> green, -12 .. -6 -> amber (speech sweet spot), -6 .. 0 -> red (clip).
/// </summary>
public partial class MeterBar : ObservableObject
{
    private const double MinDb = -60.0;
    private const double YellowCentre = 0.80;       // = (-12 - MinDb) / -MinDb
    private const double RedCentre = 0.90;          // = (-6  - MinDb) / -MinDb
    private const double GradientBlendHalf = 0.02;  // ±2% on either side of each threshold

    // Blocks mode collapses the gradient blend bands to zero width, so green/amber/red butt
    // together with hard edges. Gradient keeps the ±2% blends; Single mode is rendered by a
    // separate overlay (see SingleFillStars) so these bands are hidden and their values unused.
    private double BlendHalf => ColourMode == PeakMeterColourMode.Blocks ? 0.0 : GradientBlendHalf;
    private double GreenEnd => YellowCentre - BlendHalf;
    private double YellowStart => YellowCentre + BlendHalf;
    private double YellowEnd => RedCentre - BlendHalf;
    private double RedStart => RedCentre + BlendHalf;

    /// <summary>Live peak (0..1 linear amplitude).</summary>
    [ObservableProperty]
    public partial double Level { get; set; }

    /// <summary>Latched peak hold (0..1 linear amplitude); 0 hides the hold tick.</summary>
    [ObservableProperty]
    public partial double Hold { get; set; }

    /// <summary>Volume scale (0..1): the bar fill is bounded by the current volume thumb.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 1.0;

    /// <summary>Bar thickness in px, set by the parent meter (auto from channel count, or override).</summary>
    [ObservableProperty]
    public partial double BarHeight { get; set; }

    /// <summary>Whether this bar's hold tick renders. Off for the stacked channel bars (a single
    /// overlay line owns the hold); on for the dedicated overlay bar.</summary>
    [ObservableProperty]
    public partial bool ShowHold { get; set; }

    /// <summary>Per-bar corner radius, set by the parent meter so the stack reads as one block:
    /// the top bar rounds its top corners, the bottom bar its bottom corners, the middle none.</summary>
    [ObservableProperty]
    public partial CornerRadius Corners { get; set; }

    /// <summary>How the fill is painted: gradient/blocks use the colour-banded columns; single
    /// uses the flat <see cref="SingleBrush"/> overlay. Set by the parent meter from settings.</summary>
    [ObservableProperty]
    public partial PeakMeterColourMode ColourMode { get; set; } = PeakMeterColourMode.Gradient;

    /// <summary>The flat fill colour for <see cref="PeakMeterColourMode.Solid"/>.</summary>
    [ObservableProperty]
    public partial Color SingleColour { get; set; }

    private static double DbBar(double amplitude)
    {
        if (amplitude <= 0) return 0;
        var db = 20.0 * Math.Log10(amplitude);
        if (db <= MinDb) return 0;
        return Math.Clamp((db - MinDb) / -MinDb, 0.0, 1.0);
    }

    private static GridLength Star(double value) => new(Math.Max(value, 0.0001), GridUnitType.Star);

    public GridLength GreenStars =>
        Star(Math.Min(DbBar(Level), GreenEnd) * Volume);

    public GridLength GreenYellowBlendStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(Level), YellowStart) - GreenEnd) * Volume);

    public GridLength YellowStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(Level), YellowEnd) - YellowStart) * Volume);

    public GridLength YellowRedBlendStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(Level), RedStart) - YellowEnd) * Volume);

    public GridLength RedStars =>
        Star(Math.Max(0.0, DbBar(Level) - RedStart) * Volume);

    public GridLength RemainderStars =>
        Star(1.0 - Math.Clamp(DbBar(Level) * Volume, 0.0, 1.0));

    // Single-colour overlay: a flat fill from 0 to the current level (volume-bounded), painted in
    // SingleBrush. Only this overlay shows in Single mode; the colour-banded columns are hidden.
    public GridLength SingleFillStars =>
        Star(Math.Clamp(DbBar(Level) * Volume, 0.0, 1.0));

    public GridLength SingleRemainderStars =>
        Star(1.0 - Math.Clamp(DbBar(Level) * Volume, 0.0, 1.0));

    public Brush SingleBrush => new SolidColorBrush(SingleColour);

    public Visibility SingleVisibility =>
        ColourMode == PeakMeterColourMode.Solid ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BandedVisibility =>
        ColourMode == PeakMeterColourMode.Solid ? Visibility.Collapsed : Visibility.Visible;

    public GridLength HoldLeftStars =>
        Star(Math.Clamp(DbBar(Hold) * Volume, 0.0, 1.0));

    public GridLength HoldRightStars =>
        Star(1.0 - Math.Clamp(DbBar(Hold) * Volume, 0.0, 1.0));

    public Visibility HoldVisibility =>
        ShowHold && Hold > 0.001 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The held peak has reached the red (clipping) band - flags a potential clip.
    /// Volume cancels out (the marker and the red band scale together), so this is a pure dB
    /// threshold on the held signal; it clears itself as the hold decays back below the band.</summary>
    public bool HoldInRed => DbBar(Hold) >= RedStart;

    /// <summary>The hold tick is white below the red band and red once it reaches it. Driven by
    /// two overlaid borders (not a code-resolved brush) so both colours stay theme-correct.</summary>
    public Visibility HoldWhiteVisibility => HoldInRed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HoldRedVisibility => HoldInRed ? Visibility.Visible : Visibility.Collapsed;

    partial void OnLevelChanged(double value) => NotifyBands();
    partial void OnVolumeChanged(double value) => NotifyBands();
    partial void OnShowHoldChanged(bool value) => OnPropertyChanged(nameof(HoldVisibility));

    partial void OnColourModeChanged(PeakMeterColourMode value)
    {
        // Mode shifts the band boundaries (gradient vs blocks) and toggles banded vs single fill.
        NotifyBands();
        OnPropertyChanged(nameof(SingleVisibility));
        OnPropertyChanged(nameof(BandedVisibility));
    }

    partial void OnSingleColourChanged(Color value) => OnPropertyChanged(nameof(SingleBrush));

    partial void OnHoldChanged(double value)
    {
        OnPropertyChanged(nameof(HoldLeftStars));
        OnPropertyChanged(nameof(HoldRightStars));
        OnPropertyChanged(nameof(HoldVisibility));
        OnPropertyChanged(nameof(HoldInRed));
        OnPropertyChanged(nameof(HoldWhiteVisibility));
        OnPropertyChanged(nameof(HoldRedVisibility));
    }

    private void NotifyBands()
    {
        OnPropertyChanged(nameof(GreenStars));
        OnPropertyChanged(nameof(GreenYellowBlendStars));
        OnPropertyChanged(nameof(YellowStars));
        OnPropertyChanged(nameof(YellowRedBlendStars));
        OnPropertyChanged(nameof(RedStars));
        OnPropertyChanged(nameof(RemainderStars));
        OnPropertyChanged(nameof(SingleFillStars));
        OnPropertyChanged(nameof(SingleRemainderStars));
        OnPropertyChanged(nameof(HoldLeftStars));
        OnPropertyChanged(nameof(HoldRightStars));
        OnPropertyChanged(nameof(HoldVisibility));
    }
}
