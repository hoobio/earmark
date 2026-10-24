using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

using SkiaSharp;

namespace Earmark.Desktop.Views;

public sealed class DitheredRadialGradient : Control
{
    public static readonly StyledProperty<IRadialGradientBrush?> FillProperty =
        AvaloniaProperty.Register<DitheredRadialGradient, IRadialGradientBrush?>(nameof(Fill));

    static DitheredRadialGradient()
    {
        AffectsRender<DitheredRadialGradient>(FillProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DitheredRadialGradient>(false);
    }

    public IRadialGradientBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is { } fill && Bounds.Width > 0 && Bounds.Height > 0)
        {
            context.Custom(new Operation(new Rect(Bounds.Size), fill));
        }
    }

    private static SKShader Shader(IRadialGradientBrush fill, Size size)
    {
        var bounds = new Rect(size);
        var center = fill.Center.ToPixels(bounds);
        var radiusX = (float)fill.RadiusX.ToValue(size.Width);
        var radiusY = (float)fill.RadiusY.ToValue(size.Height);
        var colors = fill.GradientStops.Select(s => s.Color.ToSKColor().WithAlpha((byte)Math.Round(s.Color.A * fill.Opacity))).ToArray();
        var offsets = fill.GradientStops.Select(s => (float)s.Offset).ToArray();
        return SKShader.CreateRadialGradient(
            SKPoint.Empty,
            1,
            colors,
            offsets,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScaleTranslation(radiusX, radiusY, (float)center.X, (float)center.Y));
    }

    private sealed class Operation(Rect bounds, IRadialGradientBrush fill) : ICustomDrawOperation
    {
        private readonly IRadialGradientBrush _fill = fill;
        private readonly SKShader _shader = Shader(fill, bounds.Size);

        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is Operation o && o.Bounds == bounds && ReferenceEquals(o._fill, _fill);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            using var paint = new SKPaint { Shader = _shader, IsDither = true };
            lease.SkCanvas.DrawRect(bounds.ToSKRect(), paint);
        }

        public void Dispose() => _shader.Dispose();
    }
}
