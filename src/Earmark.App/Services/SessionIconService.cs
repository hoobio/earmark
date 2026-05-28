using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Earmark.App.Services;

/// <summary>
/// Resolves and caches application icons keyed by executable path. First call for a path
/// kicks an async Win32 load (SHGetFileInfo + GetDIBits) and returns null; the load posts
/// the resulting <see cref="WriteableBitmap"/> back onto the UI thread, caches it, and
/// invokes any pending continuations so the calling VM can refresh.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class SessionIconService : ISessionIconService
{
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly ILogger<SessionIconService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SessionIconService(IDispatcherQueueProvider dispatcher, ILogger<SessionIconService> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Fires when a previously-pending icon load completes for <paramref name="path"/>.
    /// VMs subscribe to invalidate their <c>Icon</c> binding without polling.</summary>
    public event Action<string, ImageSource?>? IconLoaded;

    public ImageSource? TryGetIcon(uint processId, string executablePath, int sizePx = 32)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        var key = executablePath + "|" + sizePx;
        if (_cache.TryGetValue(key, out var entry))
        {
            return entry.Icon;
        }

        // Reserve the slot so concurrent callers don't both kick a load.
        if (!_cache.TryAdd(key, new CacheEntry(null, Loading: true)))
        {
            return _cache.TryGetValue(key, out entry) ? entry.Icon : null;
        }

        _ = Task.Run(() => Load(processId, executablePath, sizePx, key));
        return null;
    }

    private void Load(uint processId, string path, int sizePx, string cacheKey)
    {
        // File path FIRST. IShellItemImageFactory uses the shell's cache that includes
        // high-res icons that the taskbar would show (the .exe's largest embedded icon
        // resource, .ico files alongside it, AppX manifests, etc.). Win32 WM_GETICON on
        // the process's window returns whatever the app set, which is often a smaller
        // (32x32) helper icon - downgrading what we'd otherwise pull from the file. Use
        // the window path only when the file route has nothing to offer (background
        // services, processes without a real exe path).
        var pixels = TryExtractPixels(path, sizePx, out var width, out var height, out var fileFailure);
        var source = "file";
        if (pixels is null || width <= 0 || height <= 0)
        {
            pixels = TryProcessWindowIcon(processId, out width, out height);
            source = "window";
            if (pixels is null || width <= 0 || height <= 0)
            {
                _logger.LogInformation(
                    "Icon load failed for pid={Pid} path={Path}: file={FileReason}, window=null",
                    processId, path, fileFailure);
                _cache[cacheKey] = new CacheEntry(null, Loading: false);
                _dispatcher.Enqueue(() => IconLoaded?.Invoke(path, null));
                return;
            }
        }
        _logger.LogInformation(
            "Icon for pid={Pid} path={Path} resolved via {Source} at {W}x{H}",
            processId, path, source, width, height);

        // WriteableBitmap's PixelBuffer is BGRA8 with PREMULTIPLIED alpha. GDI's GetDIBits
        // returns straight (non-premultiplied) alpha. Without this conversion the WinUI
        // compositor produces dark fringes / halos around any semi-transparent edge - which
        // is exactly the "extremely aliased" appearance the user reported on every icon.
        PremultiplyAlpha(pixels);

        // High-quality software downscale to the chip's effective resolution. WinUI's Image
        // compositor uses bilinear filtering for runtime scaling, which aliases on >2x
        // downscale (128 source -> 32dp chip = 4x). A box filter (area averaging) at icon
        // load time handles the bulk of the reduction at proper antialiased quality, and
        // leaves the compositor only the small final DPI fit which bilinear handles well.
        const int TargetSize = 64;
        if (width > TargetSize && height > TargetSize)
        {
            pixels = BoxFilterDownscale(pixels, width, height, TargetSize, TargetSize);
            width = TargetSize;
            height = TargetSize;
        }

        var pixelsToWrite = pixels;
        var finalWidth = width;
        var finalHeight = height;
        _dispatcher.Enqueue(() =>
        {
            try
            {
                var bitmap = new WriteableBitmap(finalWidth, finalHeight);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(pixelsToWrite, 0, pixelsToWrite.Length);
                }
                _cache[cacheKey] = new CacheEntry(bitmap, Loading: false);
                IconLoaded?.Invoke(path, bitmap);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WriteableBitmap build failed for {Path}", path);
                _cache[cacheKey] = new CacheEntry(null, Loading: false);
                IconLoaded?.Invoke(path, null);
            }
        });
    }

    /// <summary>
    /// Box-filter downscale (area averaging). Each output pixel is the mean of the source
    /// region it maps to, weighted by the fractional overlap on the source-region edges.
    /// Proper antialiasing - no source pixel is dropped, all contribute proportionally to
    /// the nearest output pixels. Works on premultiplied BGRA8 buffers, which is what the
    /// WinUI compositor expects.
    /// </summary>
    private static byte[] BoxFilterDownscale(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        var xRatio = (double)srcW / dstW;
        var yRatio = (double)srcH / dstH;

        for (var dy = 0; dy < dstH; dy++)
        {
            var srcY0 = dy * yRatio;
            var srcY1 = (dy + 1) * yRatio;
            var sy0 = (int)Math.Floor(srcY0);
            var sy1 = Math.Min(srcH, (int)Math.Ceiling(srcY1));

            for (var dx = 0; dx < dstW; dx++)
            {
                var srcX0 = dx * xRatio;
                var srcX1 = (dx + 1) * xRatio;
                var sx0 = (int)Math.Floor(srcX0);
                var sx1 = Math.Min(srcW, (int)Math.Ceiling(srcX1));

                double sumB = 0, sumG = 0, sumR = 0, sumA = 0, totalWeight = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var yWeight = Math.Min(sy + 1, srcY1) - Math.Max(sy, srcY0);
                    if (yWeight <= 0) continue;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var xWeight = Math.Min(sx + 1, srcX1) - Math.Max(sx, srcX0);
                        if (xWeight <= 0) continue;
                        var w = yWeight * xWeight;
                        var si = (sy * srcW + sx) * 4;
                        sumB += src[si + 0] * w;
                        sumG += src[si + 1] * w;
                        sumR += src[si + 2] * w;
                        sumA += src[si + 3] * w;
                        totalWeight += w;
                    }
                }

                var di = (dy * dstW + dx) * 4;
                if (totalWeight > 0)
                {
                    dst[di + 0] = (byte)Math.Clamp(sumB / totalWeight + 0.5, 0, 255);
                    dst[di + 1] = (byte)Math.Clamp(sumG / totalWeight + 0.5, 0, 255);
                    dst[di + 2] = (byte)Math.Clamp(sumR / totalWeight + 0.5, 0, 255);
                    dst[di + 3] = (byte)Math.Clamp(sumA / totalWeight + 0.5, 0, 255);
                }
            }
        }
        return dst;
    }

    private static void PremultiplyAlpha(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255) continue; // fully opaque - already correct
            if (a == 0)
            {
                bgra[i + 0] = 0;
                bgra[i + 1] = 0;
                bgra[i + 2] = 0;
                continue;
            }
            bgra[i + 0] = (byte)((bgra[i + 0] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }

    private static byte[]? TryExtractPixels(string path, int sizePx, out int width, out int height, out string failureReason)
    {
        width = 0;
        height = 0;
        failureReason = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            failureReason = "empty path";
            return null;
        }
        if (!File.Exists(path))
        {
            failureReason = "file does not exist";
            return null;
        }

        // IShellItemImageFactory is the modern shell API for resolving an icon: it consults
        // the same cache Explorer's "extra large icons" view uses (including app-tile assets,
        // .ico files alongside the exe, and the highest-res frame embedded in the binary),
        // and renders to the exact pixel size requested. Going via Win32 ExtractIconEx /
        // PrivateExtractIcons routinely upscaled small embedded icons and produced the
        // chunky output the user reported.
        const int RequestSize = 128; // 4x our typical 32dp chip, clean at all DPI scales
        var iiBitmap = TryShellItemImage(path, RequestSize);
        if (iiBitmap != IntPtr.Zero)
        {
            try
            {
                var pixels = ExtractBitmapPixels(iiBitmap, out width, out height);
                if (pixels is not null) return pixels;
                failureReason = "ExtractBitmapPixels returned null";
            }
            finally
            {
                DeleteObject(iiBitmap);
            }
        }

        // Fallbacks for paths the shell factory can't handle (rare, but PyInstaller-style
        // wrappers without proper file-association registrations have triggered it).
        var fallbackTarget = sizePx <= 20 ? 32 : 64;
        IntPtr hIcon = TryPrivateExtractIcons(path, fallbackTarget);
        if (hIcon == IntPtr.Zero)
        {
            var info = default(SHFILEINFO);
            var flags = SHGFI_ICON | (sizePx <= 20 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero)
            {
                hIcon = info.hIcon;
            }
        }
        if (hIcon == IntPtr.Zero)
        {
            var pathBuf = new char[260];
            path.CopyTo(0, pathBuf, 0, Math.Min(path.Length, pathBuf.Length - 1));
            ushort idx = 0;
            hIcon = ExtractAssociatedIcon(IntPtr.Zero, pathBuf, ref idx);
        }
        if (hIcon == IntPtr.Zero)
        {
            if (string.IsNullOrEmpty(failureReason))
            {
                failureReason = "no icon handle (IShellItemImageFactory + PrivateExtractIcons + SHGetFileInfo + ExtractAssociatedIcon all null)";
            }
            return null;
        }

        try
        {
            var pixels = ExtractIconPixels(hIcon, out width, out height);
            if (pixels is null) failureReason = "ExtractIconPixels returned null";
            return pixels;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static byte[]? TryProcessWindowIcon(uint processId, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (processId == 0) return null;

        // Pick the process's MAIN player window. A process can own many windows - hidden
        // message-only windows, tray-icon helpers, SMTC (System Media Transport Controls)
        // overlays, popup dialogs - and their icons are usually wrong (SMTC overlays in
        // particular ship the generic play-button icon that Windows associates with media
        // sessions, which is what landed on the chip earlier).
        //
        // Filters: visible, owned by the target PID, no GW_OWNER (true top-level), not a
        // tool window, has a non-empty title, AND not DWM-cloaked (modern hidden windows
        // report IsWindowVisible=true but are invisible via DWM cloaking). Among survivors
        // pick the LARGEST by area - SMTC overlays are typically 1x1 or tiny; the player
        // window is the real visible surface and is biggest. This is roughly how the
        // taskbar picks the "main HWND" it reads WM_GETICON from.
        var bestHwnd = IntPtr.Zero;
        long bestArea = 0;
        EnumWindowsProc callback = (hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            _ = GetWindowThreadProcessId(hwnd, out var owningPid);
            if (owningPid != processId) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;

            // DWM cloak check - filters Windows-side hidden overlay windows that pass
            // IsWindowVisible. SMTC and other system surfaces routinely cloak.
            int cloaked = 0;
            _ = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (cloaked != 0) return true;

            // Drop windows that don't have a real client surface; SMTC and other tiny
            // helpers fall well below this.
            if (!GetWindowRect(hwnd, out var rect)) return true;
            var w = rect.right - rect.left;
            var h = rect.bottom - rect.top;
            if (w < 100 || h < 100) return true;

            var area = (long)w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestHwnd = hwnd;
            }
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);

        var foundHwnd = bestHwnd;
        if (foundHwnd == IntPtr.Zero) return null;

        // ICON_BIG (1) is the 32px+ icon the taskbar uses. ICON_SMALL2 (2) is what Windows
        // synthesises for the title bar. Fall through to GCLP_HICON for window class icon
        // when neither WM_GETICON variant returns one. Each returns an HICON we DON'T own
        // (no DestroyIcon) - they belong to the source window.
        IntPtr hIcon = SendMessage(foundHwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = SendMessage(foundHwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = SendMessage(foundHwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(foundHwnd, GCLP_HICON);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(foundHwnd, GCLP_HICONSM);
        if (hIcon == IntPtr.Zero) return null;

        return ExtractIconPixels(hIcon, out width, out height);
    }

    private static IntPtr TryShellItemImage(string path, int size)
    {
        try
        {
            var iidImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidImageFactory, out var factoryObj);
            if (hr < 0 || factoryObj is not IShellItemImageFactory factory) return IntPtr.Zero;
            try
            {
                var sz = new SIZE { cx = size, cy = size };
                // SIIGBF_ICONONLY (4): skip thumbnails so a generated thumbnail (e.g. for an
                // image-bearing exe) doesn't outrank the real icon.
                // SIIGBF_BIGGERSIZEOK (1): allow the shell to return a larger source image
                // if its own scaling would be lossier than passing it back at native size.
                hr = factory.GetImage(sz, 1 | 4, out var hbitmap);
                if (hr < 0) return IntPtr.Zero;
                return hbitmap;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr TryPrivateExtractIcons(string path, int size)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        var count = PrivateExtractIcons(path, 0, size, size, icons, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue) return IntPtr.Zero;
        return icons[0];
    }

    /// <summary>
    /// Copies an HBITMAP's BGRA pixels into a managed array. The bitmap is returned by
    /// <c>IShellItemImageFactory.GetImage</c> in 32bpp ARGB, so a straight <c>GetDIBits</c>
    /// with a top-down BITMAPINFO matches <see cref="WriteableBitmap"/>'s expected layout.
    /// </summary>
    private static byte[]? ExtractBitmapPixels(IntPtr hbmp, out int width, out int height)
    {
        width = 0;
        height = 0;

        var bmp = default(BITMAP);
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bmp) == 0) return null;
        width = bmp.bmWidth;
        height = bmp.bmHeight;
        if (width <= 0 || height <= 0) return null;

        var stride = width * 4;
        var buffer = new byte[stride * height];

        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bi.bmiHeader.biWidth = width;
        bi.bmiHeader.biHeight = -height;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbmp, 0, (uint)height, buffer, ref bi, DIB_RGB_COLORS) == 0) return null;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }
        return buffer;
    }

    private static byte[]? ExtractIconPixels(IntPtr hIcon, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return null;
        }

        IntPtr hColorBitmap = iconInfo.hbmColor;
        IntPtr hMaskBitmap = iconInfo.hbmMask;
        try
        {
            if (hColorBitmap == IntPtr.Zero)
            {
                // Monochrome icons (1-bit, no colour bitmap) aren't worth the conversion
                // effort - fall back to the default chip glyph instead.
                return null;
            }

            var bmp = default(BITMAP);
            if (GetObject(hColorBitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0)
            {
                return null;
            }

            width = bmp.bmWidth;
            height = bmp.bmHeight;
            if (width <= 0 || height <= 0) return null;

            var stride = width * 4;
            var buffer = new byte[stride * height];

            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth = width;
            // Negative height = top-down. WriteableBitmap expects rows top-down; without the
            // sign flip the icon arrives upside-down.
            bi.bmiHeader.biHeight = -height;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;

            var hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hColorBitmap, 0, (uint)height, buffer, ref bi, DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }

            // Some older 24bpp icon resources hand back zero alpha across the whole buffer;
            // synthesise alpha from the mask in that case so the icon isn't fully invisible.
            if (IsAllZeroAlpha(buffer))
            {
                ApplyMaskAsAlpha(buffer, width, height, hMaskBitmap);
            }

            return buffer;
        }
        finally
        {
            if (hColorBitmap != IntPtr.Zero) DeleteObject(hColorBitmap);
            if (hMaskBitmap != IntPtr.Zero) DeleteObject(hMaskBitmap);
        }
    }

    private static bool IsAllZeroAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return false;
        }
        return true;
    }

    private static void ApplyMaskAsAlpha(byte[] bgra, int width, int height, IntPtr hMask)
    {
        if (hMask == IntPtr.Zero) return;

        // 1bpp mask: stride is rounded up to a 4-byte (DWORD) boundary per Win32 GDI rules.
        var maskStride = ((width + 31) / 32) * 4;
        var maskBuf = new byte[maskStride * height];

        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bi.bmiHeader.biWidth = width;
        bi.bmiHeader.biHeight = -height;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 1;
        bi.bmiHeader.biCompression = BI_RGB;

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hMask, 0, (uint)height, maskBuf, ref bi, DIB_RGB_COLORS) == 0) return;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var maskBit = (maskBuf[y * maskStride + (x >> 3)] >> (7 - (x & 7))) & 1;
                var pi = (y * width + x) * 4 + 3;
                // Mask bit 0 = opaque (icon pixel), 1 = transparent (background).
                bgra[pi] = maskBit == 0 ? (byte)0xFF : (byte)0x00;
            }
        }
    }

    private readonly record struct CacheEntry(ImageSource? Icon, bool Loading);

    // ---- Win32 ----

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
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
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // Colour table for paletted images; unused for 32bpp BGRA but the struct still has
        // to round out to the same size GDI expects.
        public uint bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractAssociatedIconW")]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, [In, Out] char[] lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    // IShellItemImageFactory : IUnknown - the shell's modern thumbnail / icon extraction
    // API. Used by Explorer's "Extra large icons" view; returns a properly-scaled HBITMAP at
    // any requested pixel size, sourcing from the highest-quality icon available (embedded
    // .ico frames, .png AppX assets, .ico files in the same dir, thumbnail cache).
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObject")]
    private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[]? lpvBits, ref BITMAPINFO lpbi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
