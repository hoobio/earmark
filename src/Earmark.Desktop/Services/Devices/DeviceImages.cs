using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Services.Devices;

public sealed class DeviceImages(ILogger<DeviceImages> logger)
{
    private const int IconSize = 32;
    private const int ArtworkCacheSize = 16;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IImage?>> _artwork = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<(Bitmap? Bitmap, Color? Accent)>> _waveLink = new(StringComparer.Ordinal);

    public Task<Bitmap?> AppIconAsync(string path) =>
        string.IsNullOrEmpty(path) ? Task.FromResult<Bitmap?>(null) : _icons.GetOrAdd(path, p => Task.Run(() => ExtractIcon(p)));

    public Task<IImage?> ArtworkAsync(byte[]? bytes, string hash)
    {
        if (bytes is not { Length: > 0 })
        {
            return Task.FromResult<IImage?>(null);
        }

        if (_artwork.Count > ArtworkCacheSize)
        {
            _artwork.Clear();
        }

        return _artwork.GetOrAdd(hash, _ => Task.Run(() => DecodeArtwork(bytes)));
    }

    public Task<(Bitmap? Bitmap, Color? Accent)> WaveLinkAsync(string? imageData) =>
        string.IsNullOrEmpty(imageData)
            ? Task.FromResult<(Bitmap?, Color?)>((null, null))
            : _waveLink.GetOrAdd(imageData, data => Task.Run(() => DecodeWaveLink(data)));

    private Bitmap? ExtractIcon(string path)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory));
            try
            {
                Marshal.ThrowExceptionForHR(factory.GetImage(new NativeSize { cx = IconSize, cy = IconSize }, 0x4, out var hbitmap));
                try
                {
                    return FromHBitmap(hbitmap);
                }
                finally
                {
                    DeleteObject(hbitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No icon for {Path}", path);
            return null;
        }
    }

    private static Bitmap? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        var width = info.bmWidth;
        var height = info.bmHeight;
        var pixels = new byte[width * height * 4];
        var header = new BitmapInfoHeader { biSize = Marshal.SizeOf<BitmapInfoHeader>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private IImage? DecodeArtwork(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            var (pixels, w, h) = ReadPixels(bitmap);
            var crop = TrimLetterbox(pixels, w, h);
            return crop == new PixelRect(0, 0, w, h) ? bitmap : new CroppedBitmap(bitmap, crop);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Artwork decode failed");
            return null;
        }
    }

    private (Bitmap?, Color?) DecodeWaveLink(string data)
    {
        try
        {
            var comma = data.IndexOf(',', StringComparison.Ordinal);
            using var stream = new MemoryStream(Convert.FromBase64String(comma >= 0 ? data[(comma + 1)..] : data));
            var bitmap = new Bitmap(stream);
            var (pixels, w, h) = ReadPixels(bitmap);
            return (bitmap, DominantOpaque(pixels, w * h, bitmap.Format == PixelFormat.Rgba8888));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Wave Link channel image decode failed");
            return (null, null);
        }
    }

    private static (byte[] Pixels, int Width, int Height) ReadPixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var pixels = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixels, size.Width, size.Height);
    }

    private static PixelRect TrimLetterbox(byte[] p, int w, int h)
    {
        bool Dark(int x, int y)
        {
            var i = ((y * w) + x) * 4;
            return p[i] < 16 && p[i + 1] < 16 && p[i + 2] < 16;
        }

        bool RowDark(int y) => Enumerable.Range(0, w).All(x => Dark(x, y));
        bool ColDark(int x) => Enumerable.Range(0, h).All(y => Dark(x, y));
        int top = 0, bottom = h - 1, left = 0, right = w - 1;
        while (top < bottom && RowDark(top)) top++;
        while (bottom > top && RowDark(bottom)) bottom--;
        while (left < right && ColDark(left)) left++;
        while (right > left && ColDark(right)) right--;
        return right - left < w / 4 || bottom - top < h / 4 ? new PixelRect(0, 0, w, h) : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static Color? DominantOpaque(byte[] p, int count, bool rgba)
    {
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            if (p[o + 3] < 250)
            {
                continue;
            }

            var key = (p[o] & 0xF0) << 16 | (p[o + 1] & 0xF0) << 8 | (p[o + 2] & 0xF0);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        if (counts.Count == 0)
        {
            return null;
        }

        var best = counts.MaxBy(kv => kv.Value).Key;
        byte a = (byte)(best >> 16), b = (byte)(best >> 8), c = (byte)best;
        return rgba ? Color.FromRgb(a, b, c) : Color.FromRgb(c, b, a);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
