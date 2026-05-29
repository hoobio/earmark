using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Earmark.App.Services;

/// <summary>
/// Turns a Wave Link channel's base64 PNG artwork into the two things the UI wants: the
/// channel's accent <see cref="Color"/> (the bitmap's dominant opaque pixel - Wave Link
/// stores no colour field) and an <see cref="ImageSource"/> of the bitmap itself. Both are
/// cached by image data so repeated card rebuilds and the 5s snapshot poll don't re-decode.
/// </summary>
public interface IWaveLinkVisualService
{
    Task<Color?> GetAccentColourAsync(string? imageData);

    Task<ImageSource?> GetIconSourceAsync(string? imageData);
}

internal sealed class WaveLinkVisualService : IWaveLinkVisualService
{
    private const byte OpaqueThreshold = 200;

    private readonly ILogger<WaveLinkVisualService> _logger;
    private readonly ConcurrentDictionary<string, Color?> _accentCache = new(StringComparer.Ordinal);
    // ImageSource is UI-thread affine; this cache is only ever touched from GetIconSourceAsync,
    // which the Home view-model calls on the dispatcher.
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.Ordinal);

    public WaveLinkVisualService(ILogger<WaveLinkVisualService> logger) => _logger = logger;

    public async Task<Color?> GetAccentColourAsync(string? imageData)
    {
        if (string.IsNullOrEmpty(imageData)) return null;
        if (_accentCache.TryGetValue(imageData, out var cached)) return cached;

        Color? accent = null;
        try
        {
            using var stream = await ToStreamAsync(imageData).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            accent = DominantOpaqueColour(pixels.DetachPixelData());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wave Link: failed to derive accent colour from channel artwork");
        }

        _accentCache[imageData] = accent;
        return accent;
    }

    public async Task<ImageSource?> GetIconSourceAsync(string? imageData)
    {
        if (string.IsNullOrEmpty(imageData)) return null;
        if (_iconCache.TryGetValue(imageData, out var cached)) return cached;

        ImageSource? source = null;
        try
        {
            using var stream = await ToStreamAsync(imageData).ConfigureAwait(true);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            source = image;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wave Link: failed to build channel icon bitmap");
        }

        _iconCache[imageData] = source;
        return source;
    }

    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    // BGRA8 little-endian: byte order is B, G, R, A. The channel tile is a solid accent fill
    // behind a small glyph, so the single most-frequent fully-opaque colour is the accent.
    private static Color? DominantOpaqueColour(byte[] bgra)
    {
        var tally = new Dictionary<int, int>();
        var bestKey = -1;
        var bestCount = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < OpaqueThreshold) continue;
            var key = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
            var count = tally.TryGetValue(key, out var c) ? c + 1 : 1;
            tally[key] = count;
            if (count > bestCount)
            {
                bestCount = count;
                bestKey = key;
            }
        }

        if (bestKey < 0) return null;
        return Color.FromArgb(255, (byte)(bestKey >> 16), (byte)(bestKey >> 8), (byte)bestKey);
    }
}
