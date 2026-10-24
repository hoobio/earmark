using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Earmark.Core.Settings;
using Earmark.Desktop.ViewModels.Devices;

namespace Earmark.Desktop.Controls;

public sealed class PeakMeter : Control
{
    public static readonly StyledProperty<object?> SourceProperty = AvaloniaProperty.Register<PeakMeter, object?>(nameof(Source));
    public static readonly StyledProperty<CardOptions?> OptionsProperty = AvaloniaProperty.Register<PeakMeter, CardOptions?>(nameof(Options));
    public static readonly StyledProperty<double> VolumeProperty = AvaloniaProperty.Register<PeakMeter, double>(nameof(Volume), 100);
    public static readonly StyledProperty<bool> IsChipProperty = AvaloniaProperty.Register<PeakMeter, bool>(nameof(IsChip));

    private const double MinDb = -60;
    private const double AmberAt = 0.80;
    private const double RedAt = 0.90;

    private static readonly Color Green = Color.FromRgb(0xBA, 0xE6, 0x7E);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xD5, 0x80);
    private static readonly Color Red = Color.FromRgb(0xF2, 0x87, 0x79);
    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush HoldWhite = new SolidColorBrush(Color.FromRgb(0xCB, 0xCC, 0xC6));
    private static readonly IBrush HoldRed = new SolidColorBrush(Red);
    private static readonly CardOptions DefaultOptions = new();

    static PeakMeter() => AffectsRender<PeakMeter>(SourceProperty, OptionsProperty, VolumeProperty);

    public PeakMeter()
    {
        AttachedToVisualTree += (_, _) => MeterClock.Tick += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => MeterClock.Tick -= InvalidateVisual;
    }

    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public CardOptions? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

    public double Volume { get => GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }

    public bool IsChip { get => GetValue(IsChipProperty); set => SetValue(IsChipProperty, value); }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var options = Options ?? DefaultOptions;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        float[] levels;
        float hold = 0;
        var volume = 1.0;
        switch (Source)
        {
            case DeviceCardViewModel card:
                levels = options.ChannelMode == PeakMeterChannelMode.Combined || card.ChannelCount <= 1 ? [MathF.Max(card.LeftLevel, MathF.Max(card.RightLevel, card.CentreLevel))]
                    : card.ChannelCount == 2 ? [card.LeftLevel, card.RightLevel]
                    : [card.LeftLevel, card.CentreLevel, card.RightLevel];
                hold = options.ShowHold ? card.Hold : 0;
                volume = Math.Clamp(Volume / 100, 0, 1);
                break;
            case AppChipViewModel chip:
                levels = [chip.Peak];
                break;
            default:
                return;
        }

        var radius = IsChip ? bounds.Height / 2 : 4;
        using var clip = context.PushClip(new RoundedRect(bounds, radius));
        context.FillRectangle(Track, bounds);
        var span = bounds.Width * volume;
        var brush = BrushFor(options, span);
        var barHeight = bounds.Height / levels.Length;
        for (var i = 0; i < levels.Length; i++)
        {
            var width = Position(levels[i]) * span;
            if (width > 0)
            {
                context.FillRectangle(brush, new Rect(0, i * barHeight, width, barHeight));
            }
        }

        if (hold > 0.001f)
        {
            var at = Position(hold);
            context.FillRectangle(at >= RedAt + 0.02 ? HoldRed : HoldWhite, new Rect(Math.Max(0, (at * span) - 2), 0, 2, bounds.Height));
        }
    }

    private static IBrush BrushFor(CardOptions options, double span)
    {
        if (options.MeterMode == PeakMeterColourMode.Solid)
        {
            return new SolidColorBrush(options.MeterColour);
        }

        var blend = options.MeterMode == PeakMeterColourMode.Blocks ? 0 : 0.02;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Math.Max(1, span), 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Green, 0), new GradientStop(Green, AmberAt - blend),
                new GradientStop(Amber, AmberAt + blend), new GradientStop(Amber, RedAt - blend),
                new GradientStop(Red, RedAt + blend), new GradientStop(Red, 1),
            },
        };
    }

    private static double Position(float amplitude)
    {
        if (amplitude <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(amplitude);
        return db <= MinDb ? 0 : Math.Clamp((db - MinDb) / -MinDb, 0, 1);
    }
}
