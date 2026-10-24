using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.VisualTree;

namespace Earmark.Desktop.Styles;

public enum AcrylicMode
{
    Blur,
    Fallback,
}

public static class Acrylic
{
    // Mirage tints with --color-bg at 72%, which is this window's own ground, so a flyout over plain page vanished into it; this darker tint at 57.5% keeps it apart.
    public const double TintOpacity = 0.575;
    public const double BlurSigma = 24;
    public const double Saturation = 1.4;
    // Avalonia.Skia 12.1.3 turns a blur radius into a sigma as 0.288675 * radius + 0.5, so this is CSS blur(24px).
    public const double BlurRadius = (BlurSigma - 0.5) / 0.288675;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(Acrylic));

    public static readonly AttachedProperty<AcrylicMode> ModeProperty =
        AvaloniaProperty.RegisterAttached<Control, AcrylicMode>("Mode", typeof(Acrylic));

    private static readonly ConditionalWeakTable<Control, Surface> Surfaces = [];

    static Acrylic()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((presenter, e) =>
        {
            presenter.AttachedToVisualTree -= OnAttached;
            presenter.DetachedFromVisualTree -= OnDetached;
            if (e.GetNewValue<bool>())
            {
                presenter.AttachedToVisualTree += OnAttached;
                presenter.DetachedFromVisualTree += OnDetached;
                if (TopLevel.GetTopLevel(presenter) is not null)
                {
                    Attach(presenter);
                }
            }
        });
    }

    public static bool GetIsEnabled(Control presenter) => presenter.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control presenter, bool value) => presenter.SetValue(IsEnabledProperty, value);

    public static AcrylicMode GetMode(Control presenter) => presenter.GetValue(ModeProperty);

    public static Color Composite(Color tint, Color? under) => under is { } ground
        ? Color.FromRgb(Mix(tint.R, ground.R), Mix(tint.G, ground.G), Mix(tint.B, ground.B))
        : Color.FromArgb(Byte(TintOpacity), tint.R, tint.G, tint.B);

    // The CSS saturate() matrix, applied to sRGB values as browsers do.
    public static Color Saturate(Color color)
    {
        var (r, g, b) = Saturate(color.R, color.G, color.B);
        return Color.FromRgb(r, g, b);
    }

    private static (byte R, byte G, byte B) Saturate(double r, double g, double b)
    {
        const double s = Saturation;
        return (
            Clamp((0.213 + 0.787 * s) * r + (0.715 - 0.715 * s) * g + (0.072 - 0.072 * s) * b),
            Clamp((0.213 - 0.213 * s) * r + (0.715 + 0.285 * s) * g + (0.072 - 0.072 * s) * b),
            Clamp((0.213 - 0.213 * s) * r + (0.715 - 0.715 * s) * g + (0.072 + 0.928 * s) * b));
    }

    private static byte Mix(byte tint, byte under) => Clamp(tint * TintOpacity + under * (1 - TintOpacity));

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static byte Byte(double unit) => Clamp(unit * 255);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach((Control)sender!);

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var presenter = (Control)sender!;
        if (Surfaces.TryGetValue(presenter, out var surface))
        {
            surface.Dispose();
            Surfaces.Remove(presenter);
        }
    }

    private static void Attach(Control presenter)
    {
        OnDetached(presenter, null!);
        if (TopLevel.GetTopLevel(presenter) is { } top)
        {
            Surfaces.Add(presenter, new Surface(presenter, top));
        }
    }

    private static WindowBase? Owner(TopLevel top)
    {
        var current = top;
        while (current is PopupRoot popup && popup.ParentTopLevel is { } parent)
        {
            current = parent;
        }

        return current as WindowBase;
    }

    private static List<TopLevel> Behind(TopLevel top)
    {
        var layers = new List<TopLevel>();
        for (var current = top as PopupRoot; current?.ParentTopLevel is { } parent; current = parent as PopupRoot)
        {
            layers.Add(parent);
        }

        if (top is not PopupRoot)
        {
            layers.Add(top);
        }

        layers.Reverse();
        return layers;
    }

    private static Color Resource(Control presenter, string key) =>
        presenter.TryFindResource(key, presenter.ActualThemeVariant, out var value) && value is Color color ? color : Colors.Transparent;

    // hoobi-design's Mirage flyout, drawn here rather than asked of the OS: the page behind is this app's own window, Linux and macOS popups get no tinted blur, and Avalonia has no saturate effect.
    private sealed class Surface : IDisposable
    {
        private readonly Control _presenter;
        private readonly TopLevel _top;
        private readonly WindowBase? _owner;
        private readonly IDisposable? _activation;
        private WriteableBitmap? _bitmap;
        private PixelRect _applied;
        private bool _active;

        public Surface(Control presenter, TopLevel top)
        {
            _presenter = presenter;
            _top = top;
            _owner = Owner(top);
            _presenter.LayoutUpdated += OnLayoutUpdated;
            _activation = _owner?.GetObservable(WindowBase.IsActiveProperty).Subscribe(new AnonymousObserver<bool>(_ => Apply(force: true)));
            Apply(force: true);
        }

        public void Dispose()
        {
            _presenter.LayoutUpdated -= OnLayoutUpdated;
            _activation?.Dispose();
            _bitmap?.Dispose();
            _bitmap = null;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e) => Apply(force: false);

        private void Apply(bool force)
        {
            var active = _owner?.IsActive != false;
            var scale = _top.RenderScaling;
            var rect = new PixelRect(_presenter.PointToScreen(default), PixelSize.FromSize(_presenter.Bounds.Size, scale));
            if (!force && rect == _applied && active == _active)
            {
                return;
            }

            (_applied, _active) = (rect, active);
            var tint = Resource(_presenter, "FlyoutAcrylicTintColor");
            var fallback = Resource(_presenter, "FlyoutAcrylicFallbackColor");
            var mode = active ? AcrylicMode.Blur : AcrylicMode.Fallback;
            _presenter.SetValue(ModeProperty, mode);
            // A tooltip opens on every hover, too often to render and blur the window behind it each time.
            _presenter.SetValue(Border.BackgroundProperty, mode == AcrylicMode.Blur && _presenter is not ToolTip && Snapshot(rect, scale, tint, fallback) is { } snapshot
                ? new ImageBrush(snapshot) { Stretch = Stretch.Fill }
                : new SolidColorBrush(mode == AcrylicMode.Blur ? Composite(tint, fallback) : fallback));
        }

        private WriteableBitmap? Snapshot(PixelRect rect, double scale, Color tint, Color fallback)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return null;
            }

            var dpi = new Vector(96 * scale, 96 * scale);
            var size = new Size(rect.Width / scale, rect.Height / scale);
            var bounds = new Rect(size).Inflate(BlurSigma * 3);
            using var backdrop = new RenderTargetBitmap(rect.Size, dpi);
            using (var context = backdrop.CreateDrawingContext())
            {
                using (context.PushEffect(new ImmutableBlurEffect(BlurRadius), bounds))
                {
                    context.FillRectangle(new ImmutableSolidColorBrush(fallback), bounds);
                    var layers = Behind(_top);
                    foreach (var layer in layers)
                    {
                        // The window's ground runs on past its edges, as CSS repeats the backdrop's edge, so a flyout by the edge does not blur in the fallback colour.
                        if (layer.Background is { } ground)
                        {
                            context.FillRectangle(ground, layer == layers[0] ? bounds : Place(layer, rect, scale));
                        }

                        if (layer.Content is not Visual content || content == _presenter || content.IsVisualAncestorOf(_presenter))
                        {
                            continue;
                        }

                        var pixels = PixelSize.FromSize(content.Bounds.Size, scale);
                        if (pixels.Width <= 0 || pixels.Height <= 0)
                        {
                            continue;
                        }

                        using var image = new RenderTargetBitmap(pixels, dpi);
                        image.Render(content);
                        context.DrawImage(image, Place(content, rect, scale));
                    }
                }
            }

            var format = backdrop.Format ?? PixelFormat.Bgra8888;
            var stride = rect.Width * 4;
            var data = new byte[stride * rect.Height];
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                backdrop.CopyPixels(new PixelRect(rect.Size), handle.AddrOfPinnedObject(), data.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            var (red, blue) = format == PixelFormat.Rgba8888 ? (0, 2) : (2, 0);
            for (var i = 0; i < data.Length; i += 4)
            {
                var (r, g, b) = Saturate(data[i + red], data[i + 1], data[i + blue]);
                data[i + red] = Mix(tint.R, r);
                data[i + 1] = Mix(tint.G, g);
                data[i + blue] = Mix(tint.B, b);
                data[i + 3] = 255;
            }

            var target = new WriteableBitmap(rect.Size, dpi, format, AlphaFormat.Premul);
            using (var buffer = target.Lock())
            {
                for (var row = 0; row < rect.Height; row++)
                {
                    Marshal.Copy(data, row * stride, buffer.Address + row * buffer.RowBytes, stride);
                }
            }

            _bitmap?.Dispose();
            _bitmap = target;
            return target;
        }

        private static Rect Place(Visual layer, PixelRect rect, double scale)
        {
            var offset = layer.PointToScreen(default) - rect.Position;
            return new Rect(offset.X / scale, offset.Y / scale, layer.Bounds.Width, layer.Bounds.Height);
        }
    }
}
