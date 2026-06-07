using System.Collections.ObjectModel;

using Earmark.App.Settings;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// Renders a device's peak level as up to three stacked, colour-banded bars folded by channel
/// count: 1 = mono, 2 = Left/Right, 3 = Left/Centre+LFE/Right (top to bottom, so Centre+LFE sits
/// between the two sides). Sits behind the volume slider on a
/// device card (slider track brushes are made transparent in that scope so the bars show through).
///
/// Also doubles as the app-chip underbar: set <see cref="ChannelCount"/> = 1, a small
/// <see cref="BarHeightOverride"/>, and <see cref="ShowHold"/> = false for a single short bar.
/// </summary>
public sealed partial class ChannelPeakMeter : UserControl
{
    // The stacked bars butt together (no gap) and always fill this fixed total height (~the
    // Slider thumb size), so each bar is total/count thick - 20 / 10 / 6.67 for mono / stereo /
    // surround - and the whole block is the same height regardless of channel count.
    private const double TotalMeterHeight = 20.0;

    public ChannelPeakMeter()
    {
        InitializeComponent();
        RebuildBars();
    }

    /// <summary>The 1-3 stacked channel bars rendered by the template.</summary>
    public ObservableCollection<MeterBar> Bars { get; } = new();

    /// <summary>Drives the single peak-hold line that overlays the whole stack. Its
    /// <see cref="MeterBar.Hold"/> is the max hold across the active channels.</summary>
    public MeterBar HoldBar { get; } = new() { ShowHold = true };

    public static readonly DependencyProperty LeftLevelProperty = DependencyProperty.Register(
        nameof(LeftLevel), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double LeftLevel
    {
        get => (double)GetValue(LeftLevelProperty);
        set => SetValue(LeftLevelProperty, value);
    }

    public static readonly DependencyProperty RightLevelProperty = DependencyProperty.Register(
        nameof(RightLevel), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double RightLevel
    {
        get => (double)GetValue(RightLevelProperty);
        set => SetValue(RightLevelProperty, value);
    }

    public static readonly DependencyProperty CentreLfeLevelProperty = DependencyProperty.Register(
        nameof(CentreLfeLevel), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double CentreLfeLevel
    {
        get => (double)GetValue(CentreLfeLevelProperty);
        set => SetValue(CentreLfeLevelProperty, value);
    }

    public static readonly DependencyProperty LeftHoldProperty = DependencyProperty.Register(
        nameof(LeftHold), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double LeftHold
    {
        get => (double)GetValue(LeftHoldProperty);
        set => SetValue(LeftHoldProperty, value);
    }

    public static readonly DependencyProperty RightHoldProperty = DependencyProperty.Register(
        nameof(RightHold), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double RightHold
    {
        get => (double)GetValue(RightHoldProperty);
        set => SetValue(RightHoldProperty, value);
    }

    public static readonly DependencyProperty CentreLfeHoldProperty = DependencyProperty.Register(
        nameof(CentreLfeHold), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnLevelsChanged));

    public double CentreLfeHold
    {
        get => (double)GetValue(CentreLfeHoldProperty);
        set => SetValue(CentreLfeHoldProperty, value);
    }

    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
        nameof(Volume), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(1.0, OnLevelsChanged));

    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public static readonly DependencyProperty ChannelCountProperty = DependencyProperty.Register(
        nameof(ChannelCount), typeof(int), typeof(ChannelPeakMeter), new PropertyMetadata(2, OnShapeChanged));

    public int ChannelCount
    {
        get => (int)GetValue(ChannelCountProperty);
        set => SetValue(ChannelCountProperty, value);
    }

    /// <summary>When &gt; 0, overrides the auto per-count bar thickness (used by the app chip).</summary>
    public static readonly DependencyProperty BarHeightOverrideProperty = DependencyProperty.Register(
        nameof(BarHeightOverride), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnShapeChanged));

    public double BarHeightOverride
    {
        get => (double)GetValue(BarHeightOverrideProperty);
        set => SetValue(BarHeightOverrideProperty, value);
    }

    /// <summary>When &gt; 0, overrides the total stacked-meter height (default 20) that the channel bars
    /// divide between them - used by compact cards to slim the meter. Ignored if
    /// <see cref="BarHeightOverride"/> is set (the app-chip underbar path).</summary>
    public static readonly DependencyProperty TotalHeightOverrideProperty = DependencyProperty.Register(
        nameof(TotalHeightOverride), typeof(double), typeof(ChannelPeakMeter), new PropertyMetadata(0.0, OnShapeChanged));

    public double TotalHeightOverride
    {
        get => (double)GetValue(TotalHeightOverrideProperty);
        set => SetValue(TotalHeightOverrideProperty, value);
    }

    public static readonly DependencyProperty ShowHoldProperty = DependencyProperty.Register(
        nameof(ShowHold), typeof(bool), typeof(ChannelPeakMeter), new PropertyMetadata(true, OnShapeChanged));

    public bool ShowHold
    {
        get => (bool)GetValue(ShowHoldProperty);
        set => SetValue(ShowHoldProperty, value);
    }

    /// <summary>Colour scheme for the bars (gradient / blocks / single / off). Off is handled by
    /// the host hiding the whole meter, so the meter itself just renders gradient when told Off.</summary>
    public static readonly DependencyProperty ColourModeProperty = DependencyProperty.Register(
        nameof(ColourMode), typeof(PeakMeterColourMode), typeof(ChannelPeakMeter),
        new PropertyMetadata(PeakMeterColourMode.Gradient, OnColourChanged));

    public PeakMeterColourMode ColourMode
    {
        get => (PeakMeterColourMode)GetValue(ColourModeProperty);
        set => SetValue(ColourModeProperty, value);
    }

    /// <summary>Flat fill colour used when <see cref="ColourMode"/> is
    /// <see cref="PeakMeterColourMode.Single"/>.</summary>
    public static readonly DependencyProperty SingleColourProperty = DependencyProperty.Register(
        nameof(SingleColour), typeof(Color), typeof(ChannelPeakMeter),
        new PropertyMetadata(default(Color), OnColourChanged));

    public Color SingleColour
    {
        get => (Color)GetValue(SingleColourProperty);
        set => SetValue(SingleColourProperty, value);
    }

    /// <summary>Split (per-channel bars) or Single (one bar from the loudest channel).</summary>
    public static readonly DependencyProperty ChannelModeProperty = DependencyProperty.Register(
        nameof(ChannelMode), typeof(PeakMeterChannelMode), typeof(ChannelPeakMeter),
        new PropertyMetadata(PeakMeterChannelMode.Split, OnShapeChanged));

    public PeakMeterChannelMode ChannelMode
    {
        get => (PeakMeterChannelMode)GetValue(ChannelModeProperty);
        set => SetValue(ChannelModeProperty, value);
    }

    private static void OnLevelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChannelPeakMeter)d).UpdateBarValues();

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChannelPeakMeter)d).RebuildBars();

    private static void OnColourChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChannelPeakMeter)d).ApplyColour();

    private void RebuildBars()
    {
        var count = ChannelMode == PeakMeterChannelMode.Combined
            ? 1
            : Math.Clamp(ChannelCount <= 0 ? 1 : ChannelCount, 1, 3);
        if (Bars.Count != count)
        {
            Bars.Clear();
            for (var i = 0; i < count; i++)
            {
                Bars.Add(new MeterBar());
            }
        }

        var totalHeight = TotalHeightOverride > 0 ? TotalHeightOverride : TotalMeterHeight;
        var barHeight = BarHeightOverride > 0
            ? BarHeightOverride
            : totalHeight / count;

        // Rounded ends on the outer corners only so the stack reads as one block; radius =
        // half the stack height for pill ends matching the old single bar.
        var radius = barHeight * count / 2.0;
        for (var i = 0; i < Bars.Count; i++)
        {
            var bar = Bars[i];
            bar.BarHeight = barHeight;
            // The stacked channel bars never draw their own hold tick; the overlay owns it.
            bar.ShowHold = false;
            bar.Corners = CornersFor(i, count, radius);
        }

        HoldBar.BarHeight = barHeight * count;
        HoldBar.ShowHold = ShowHold;
        ApplyColour();
        UpdateBarValues();
    }

    /// <summary>Pushes the colour scheme onto every bar (and the hold bar, so its clip-red
    /// threshold tracks the mode).</summary>
    private void ApplyColour()
    {
        foreach (var bar in Bars)
        {
            bar.ColourMode = ColourMode;
            bar.SingleColour = SingleColour;
        }

        HoldBar.ColourMode = ColourMode;
        HoldBar.SingleColour = SingleColour;
    }

    private static CornerRadius CornersFor(int index, int count, double radius)
    {
        if (count == 1) return new CornerRadius(radius);
        if (index == 0) return new CornerRadius(radius, radius, 0, 0);
        if (index == count - 1) return new CornerRadius(0, 0, radius, radius);
        return new CornerRadius(0);
    }

    private void UpdateBarValues()
    {
        if (Bars.Count == 0)
        {
            return;
        }

        double maxHold;
        if (Bars.Count == 1)
        {
            // Mono device, or Single channel mode: one bar driven by the loudest channel.
            Bars[0].Volume = Volume;
            Bars[0].Level = Math.Max(LeftLevel, Math.Max(RightLevel, CentreLfeLevel));
            maxHold = Math.Max(LeftHold, Math.Max(RightHold, CentreLfeHold));
        }
        else
        {
            Bars[0].Volume = Volume;
            Bars[0].Level = LeftLevel;
            maxHold = LeftHold;

            if (Bars.Count == 2)
            {
                // Stereo: Left on top, Right below.
                Bars[1].Volume = Volume;
                Bars[1].Level = RightLevel;
                maxHold = Math.Max(maxHold, RightHold);
            }
            else
            {
                // Surround: Centre+LFE sits between Left and Right so the stack reads L / C / R.
                Bars[1].Volume = Volume;
                Bars[1].Level = CentreLfeLevel;
                Bars[2].Volume = Volume;
                Bars[2].Level = RightLevel;
                maxHold = Math.Max(maxHold, Math.Max(CentreLfeHold, RightHold));
            }
        }

        // One hold line for the whole stack, latched at the loudest channel.
        HoldBar.Volume = Volume;
        HoldBar.Hold = maxHold;
    }
}
