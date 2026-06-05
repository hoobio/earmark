using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Earmark.App.Services;

/// <summary>Result of processing now-playing artwork into a strip backdrop: the fill-ready image and
/// whether it was blurred (low-res fallback) vs shown sharp (high-res cover-fill).</summary>
public sealed record ProcessedArtwork(ImageSource? Source, bool WasBlurred);

/// <summary>Builds now-playing strip backdrops from raw SMTC thumbnail bytes (see
/// <see cref="NowPlayingArtworkService"/>). Must be called on the UI thread (creates WinUI image
/// sources).</summary>
public interface INowPlayingArtworkService
{
    Task<ProcessedArtwork> BuildAsync(byte[]? bytes, string contentHash, NowPlayingBackdropBlurMode mode);
}

/// <summary>
/// Turns raw SMTC thumbnail bytes into a backdrop <see cref="ImageSource"/> for the now-playing strip.
/// One Win2D pipeline for every mode: decode, trim solid black letterbox bars off the edges, then either
/// cover-fill sharp (source big enough to upscale cleanly) or blur-to-fill (Gaussian, or a cheap
/// decode-tiny soft fill) when it isn't. Results are cached by content hash + mode so a track's backdrop
/// is processed once, not on every reconcile.
/// </summary>
public sealed class NowPlayingArtworkService : INowPlayingArtworkService
{
    // Nominal physical width the strip backdrop must cover. The strip is full-bleed at roughly a
    // card's width (~320 logical) at ~1.5x scale. Only art that would need to upscale past
    // SharpUpscaleLimit to cover this is blurred; typical album art (300-640px) fills sharp.
    private const double TargetPhysicalWidth = 480;
    private const double SharpUpscaleLimit = 2.0;
    private const int DownscaleWidth = 32;
    private const int CacheCap = 32;

    // Black-bar trim: an edge row/column counts as a bar when nearly all its pixels are near-black.
    private const int BlackThreshold = 24;
    private const double BarPixelFraction = 0.992;
    private const double MaxTrimFraction = 0.45; // never trim more than this per dimension (sanity)

    private readonly ILogger<NowPlayingArtworkService> _logger;
    private readonly Dictionary<string, ProcessedArtwork> _cache = new(StringComparer.Ordinal);

    public NowPlayingArtworkService(ILogger<NowPlayingArtworkService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProcessedArtwork> BuildAsync(byte[]? bytes, string contentHash, NowPlayingBackdropBlurMode mode)
    {
        if (bytes is null || bytes.Length == 0) return new ProcessedArtwork(null, false);

        var cacheKey = $"{contentHash}|{(int)mode}";
        if (!string.IsNullOrEmpty(contentHash) && _cache.TryGetValue(cacheKey, out var cached)) return cached;

        ProcessedArtwork result;
        try
        {
            result = await ProcessAsync(bytes, mode).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Decode/blur failed (corrupt or unsupported art): fall back to showing the raw bytes.
            _logger.LogDebug(ex, "NowPlaying artwork: processing failed; showing raw");
            result = new ProcessedArtwork(await LoadRawAsync(bytes).ConfigureAwait(true), false);
        }

        if (!string.IsNullOrEmpty(contentHash))
        {
            if (_cache.Count >= CacheCap) _cache.Clear();
            _cache[cacheKey] = result;
        }
        return result;
    }

    private async Task<ProcessedArtwork> ProcessAsync(byte[] bytes, NowPlayingBackdropBlurMode mode)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        using var source = await CanvasBitmap.LoadAsync(device, stream);

        var fullW = (int)source.SizeInPixels.Width;
        var fullH = (int)source.SizeInPixels.Height;
        var pixels = source.GetPixelBytes();
        var crop = DetectContentBounds(pixels, fullW, fullH);
        var srcRect = new Rect(crop.X, crop.Y, crop.Width, crop.Height);

        var upscale = crop.Width > 0 ? TargetPhysicalWidth / crop.Width : double.MaxValue;
        var sharp = upscale <= SharpUpscaleLimit;
        _logger.LogInformation(
            "NowPlaying artwork: source={FullW}x{FullH} content={CropW}x{CropH} upscale={Upscale:0.00} sharp={Sharp} mode={Mode}",
            fullW, fullH, crop.Width, crop.Height, upscale, sharp, mode);

        ImageSource image = sharp
            ? RenderToImageSource(device, source, srcRect, (int)crop.Width, (int)crop.Height, blur: null)
            : mode == NowPlayingBackdropBlurMode.Downscale
                ? RenderDownscale(device, source, srcRect, (int)crop.Width, (int)crop.Height)
                : RenderToImageSource(device, source, srcRect, (int)crop.Width, (int)crop.Height, blur: BlurAmountFor((int)crop.Width));

        return new ProcessedArtwork(image, !sharp);
    }

    /// <summary>Renders the cropped source (optionally Gaussian-blurred) into a render target at the
    /// crop's native size, returning a software-bitmap-backed image source.</summary>
    private static SoftwareBitmapSource RenderToImageSource(CanvasDevice device, CanvasBitmap source, Rect srcRect, int w, int h, float? blur)
    {
        using var target = new CanvasRenderTarget(device, w, h, 96);
        using (var ds = target.CreateDrawingSession())
        {
            if (blur is { } amount)
            {
                using var crop = new CropEffect { Source = source, SourceRectangle = srcRect };
                using var effect = new GaussianBlurEffect { Source = crop, BlurAmount = amount, BorderMode = EffectBorderMode.Hard };
                ds.DrawImage(effect, new Rect(0, 0, w, h), srcRect);
            }
            else
            {
                ds.DrawImage(source, new Rect(0, 0, w, h), srcRect);
            }
        }
        return SoftwareBitmapFrom(target, w, h);
    }

    /// <summary>Cheap soft-fill: draw the cropped source into a tiny target with linear sampling; the
    /// strip's UniformToFill then upscales it into a soft blur, no GPU effect graph.</summary>
    private static SoftwareBitmapSource RenderDownscale(CanvasDevice device, CanvasBitmap source, Rect srcRect, int w, int h)
    {
        var smallW = Math.Min(DownscaleWidth, w);
        var smallH = Math.Max(1, (int)Math.Round(smallW * (double)h / Math.Max(1, w)));
        using var target = new CanvasRenderTarget(device, smallW, smallH, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(source, new Rect(0, 0, smallW, smallH), srcRect, 1f, CanvasImageInterpolation.Linear);
        }
        return SoftwareBitmapFrom(target, smallW, smallH);
    }

    private static SoftwareBitmapSource SoftwareBitmapFrom(CanvasRenderTarget target, int w, int h)
    {
        var bytes = target.GetPixelBytes();
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var imageSource = new SoftwareBitmapSource();
        _ = imageSource.SetBitmapAsync(bitmap); // fire-and-forget: the source paints once the copy lands
        return imageSource;
    }

    private static float BlurAmountFor(int sourceWidth) => (float)Math.Clamp(sourceWidth / 40.0, 6.0, 24.0);

    /// <summary>Finds the non-black content rectangle by walking in from each edge while the edge
    /// row/column is (almost) entirely near-black. Capped so it can never eat real content; returns the
    /// full frame when no bars are found (or the result looks degenerate).</summary>
    private static BitmapBounds DetectContentBounds(byte[] bgra, int w, int h)
    {
        var full = new BitmapBounds { X = 0, Y = 0, Width = (uint)w, Height = (uint)h };
        if (w <= 2 || h <= 2) return full;

        var maxTrimX = (int)(w * MaxTrimFraction);
        var maxTrimY = (int)(h * MaxTrimFraction);

        int top = 0;
        while (top < maxTrimY && RowIsBar(bgra, w, top)) top++;
        int bottom = h - 1;
        while (bottom > h - 1 - maxTrimY && bottom > top && RowIsBar(bgra, w, bottom)) bottom--;
        int left = 0;
        while (left < maxTrimX && ColumnIsBar(bgra, w, h, left)) left++;
        int right = w - 1;
        while (right > w - 1 - maxTrimX && right > left && ColumnIsBar(bgra, w, h, right)) right--;

        if (left == 0 && top == 0 && right == w - 1 && bottom == h - 1) return full;

        var cropW = right - left + 1;
        var cropH = bottom - top + 1;
        // Reject a degenerate trim (e.g. an almost-entirely-black image) - keep the full frame.
        if (cropW < w * 0.4 || cropH < h * 0.4) return full;
        return new BitmapBounds { X = (uint)left, Y = (uint)top, Width = (uint)cropW, Height = (uint)cropH };
    }

    private static bool RowIsBar(byte[] bgra, int w, int y)
    {
        var rowStart = y * w * 4;
        var black = 0;
        for (var x = 0; x < w; x++)
        {
            var i = rowStart + (x * 4);
            if (bgra[i] < BlackThreshold && bgra[i + 1] < BlackThreshold && bgra[i + 2] < BlackThreshold) black++;
        }
        return black >= w * BarPixelFraction;
    }

    private static bool ColumnIsBar(byte[] bgra, int w, int h, int x)
    {
        var black = 0;
        for (var y = 0; y < h; y++)
        {
            var i = ((y * w) + x) * 4;
            if (bgra[i] < BlackThreshold && bgra[i + 1] < BlackThreshold && bgra[i + 2] < BlackThreshold) black++;
        }
        return black >= h * BarPixelFraction;
    }

    private static async Task<ImageSource> LoadRawAsync(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        await bmp.SetSourceAsync(stream);
        return bmp;
    }
}
