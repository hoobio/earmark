using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Earmark.App.Services;

/// <summary>Result of processing now-playing artwork into a strip backdrop: the fill-ready image and
/// whether it should be frosted (a low-res source the strip softens with an acrylic overlay) vs shown
/// sharp (high-res cover-fill).</summary>
public sealed record ProcessedArtwork(ImageSource? Source, bool Frosted);

/// <summary>Builds now-playing strip backdrops from raw SMTC thumbnail bytes (see
/// <see cref="NowPlayingArtworkService"/>). Must be called on the UI thread (creates WinUI image
/// sources).</summary>
public interface INowPlayingArtworkService
{
    Task<ProcessedArtwork> BuildAsync(byte[]? bytes, string contentHash);
}

/// <summary>
/// Turns raw SMTC thumbnail bytes into a backdrop <see cref="ImageSource"/> for the now-playing strip.
/// One <see cref="BitmapDecoder"/> pipeline: decode, trim solid black letterbox bars off the edges, then
/// cover-fill the cropped art. Art big enough to upscale cleanly is shown sharp; art that would need to
/// upscale past <see cref="SharpUpscaleLimit"/> is flagged <see cref="ProcessedArtwork.Frosted"/> so the
/// strip lays an in-app acrylic over it (the compositor blurs the fill, no Win2D). Results are cached by
/// content hash so a track's backdrop is processed once, not on every reconcile.
/// </summary>
public sealed class NowPlayingArtworkService : INowPlayingArtworkService
{
    // Nominal physical width the strip backdrop must cover. The strip is full-bleed at roughly a
    // card's width (~320 logical) at ~1.5x scale. Only art that would need to upscale past
    // SharpUpscaleLimit to cover this is frosted; typical album art (300-640px) fills sharp.
    private const double TargetPhysicalWidth = 480;
    private const double SharpUpscaleLimit = 2.0;
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

    public async Task<ProcessedArtwork> BuildAsync(byte[]? bytes, string contentHash)
    {
        if (bytes is null || bytes.Length == 0) return new ProcessedArtwork(null, false);

        if (!string.IsNullOrEmpty(contentHash) && _cache.TryGetValue(contentHash, out var cached)) return cached;

        ProcessedArtwork result;
        try
        {
            result = await ProcessAsync(bytes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Decode/crop failed (corrupt or unsupported art): fall back to showing the raw bytes.
            _logger.LogDebug(ex, "NowPlaying artwork: processing failed; showing raw");
            result = new ProcessedArtwork(await LoadRawAsync(bytes).ConfigureAwait(true), false);
        }

        if (!string.IsNullOrEmpty(contentHash))
        {
            if (_cache.Count >= CacheCap) _cache.Clear();
            _cache[contentHash] = result;
        }
        return result;
    }

    private async Task<ProcessedArtwork> ProcessAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var fullW = (int)decoder.PixelWidth;
        var fullH = (int)decoder.PixelHeight;

        // Ignore EXIF orientation so the returned buffer's layout matches PixelWidth/PixelHeight (album
        // art is virtually never oriented; respecting it would swap the dimensions out from under the trim).
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelData.DetachPixelData();

        var crop = DetectContentBounds(pixels, fullW, fullH);

        var upscale = crop.Width > 0 ? TargetPhysicalWidth / crop.Width : double.MaxValue;
        var frosted = upscale > SharpUpscaleLimit;
        _logger.LogInformation(
            "NowPlaying artwork: source={FullW}x{FullH} content={CropW}x{CropH} upscale={Upscale:0.00} frosted={Frosted}",
            fullW, fullH, crop.Width, crop.Height, upscale, frosted);

        var image = await CropToImageSourceAsync(pixels, fullW, fullH, crop).ConfigureAwait(true);
        return new ProcessedArtwork(image, frosted);
    }

    /// <summary>Copies the content rectangle out of the full BGRA buffer into a tightly-packed
    /// software-bitmap-backed image source. No GPU work: the strip's UniformToFill (and, when frosted,
    /// the acrylic overlay) does the upscale and blur.</summary>
    private static async Task<ImageSource> CropToImageSourceAsync(byte[] bgra, int fullW, int fullH, BitmapBounds crop)
    {
        var w = (int)crop.Width;
        var h = (int)crop.Height;

        byte[] cropped;
        if (crop.X == 0 && crop.Y == 0 && w == fullW && h == fullH)
        {
            cropped = bgra;
        }
        else
        {
            cropped = new byte[w * h * 4];
            var srcStride = fullW * 4;
            var dstStride = w * 4;
            for (var row = 0; row < h; row++)
            {
                Array.Copy(bgra, ((int)crop.Y + row) * srcStride + ((int)crop.X * 4), cropped, row * dstStride, dstStride);
            }
        }

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(cropped.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var imageSource = new SoftwareBitmapSource();
        await imageSource.SetBitmapAsync(bitmap);
        return imageSource;
    }

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
