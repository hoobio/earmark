using System.Runtime.InteropServices;

using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.Windows.BadgeNotifications;

using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

using WinRT.Interop;

namespace Earmark.App.Services;

public interface ITaskbarMediaControls
{
    /// <summary>Wires the thumbnail toolbar to the given window's taskbar button. No-op until the
    /// window actually gets a taskbar button (the toolbar only exists while it's on the taskbar, not
    /// while hidden to tray).</summary>
    void Attach(Window window);
}

/// <summary>
/// Adds prev / play-pause / next buttons to Earmark's taskbar thumbnail toolbar (the popup shown when
/// you hover the taskbar button), driving the primary SMTC session via <see cref="INowPlayingService"/>.
/// Mirrors what Groove / WMP do. The toolbar exists only while the main window has a taskbar button, so
/// it's dormant whenever the app is hidden to tray.
/// </summary>
internal sealed class TaskbarMediaControlsManager : ITaskbarMediaControls, IDisposable
{
    private const int PrevButtonId = 1;
    private const int PlayPauseButtonId = 2;
    private const int NextButtonId = 3;

    // Segoe Fluent Icons transport glyphs, matching the in-app now-playing strip.
    private const string PrevGlyph = "";
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string NextGlyph = "";

    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const int THBN_CLICKED = 0x1800;
    private const uint SubclassId = 0xEAB1;

    private readonly INowPlayingService _nowPlaying;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly ILogger<TaskbarMediaControlsManager> _logger;
    private readonly uint _taskbarButtonCreatedMessage;

    private SUBCLASSPROC? _subclassProc;
    private nint _hwnd;
    private ITaskbarList3? _taskbar;
    private bool _buttonsAdded;
    private bool _disposed;

    private nint _iconPrev;
    private nint _iconPlay;
    private nint _iconPause;
    private nint _iconNext;
    private nint _overlayPlay;
    private nint _overlayPause;

    public TaskbarMediaControlsManager(
        INowPlayingService nowPlaying,
        IDispatcherQueueProvider dispatcher,
        ILogger<TaskbarMediaControlsManager> logger)
    {
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _hwnd = WindowNative.GetWindowHandle(window);
        if (_hwnd == 0) return;

        // Install the subclass before the window is activated so we catch the first
        // TaskbarButtonCreated message (sent once the taskbar button appears).
        _subclassProc = WindowSubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);

        try
        {
            _taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TaskbarList)!)!;
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Taskbar: ITaskbarList3 unavailable; thumbnail media controls disabled");
            _taskbar = null;
            return;
        }

        RebuildIcons();
        _nowPlaying.Changed += OnNowPlayingChanged;
        UpdatePlaybackBadge();
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e) =>
        _dispatcher.Enqueue(() =>
        {
            RefreshButtons();
            UpdatePlaybackBadge();
        });

    /// <summary>Mirrors the primary session's playback state onto the taskbar icon (play glyph while
    /// playing, pause bars while paused, cleared otherwise). Picks the surface by install kind:
    /// a packaged build uses the OS <see cref="BadgeNotificationManager"/> media glyph; an unpackaged
    /// build (where that API throws) falls back to a corner overlay icon via ITaskbarList3. Either way
    /// it only shows while the main window has a taskbar button.</summary>
    private void UpdatePlaybackBadge()
    {
        var primary = _nowPlaying.GetPrimary();
        if (AppInfo.IsPackaged)
        {
            try
            {
                var manager = BadgeNotificationManager.Current;
                if (primary is null) manager.ClearBadge();
                else manager.SetBadgeAsGlyph(primary.IsPlaying ? BadgeNotificationGlyph.Playing : BadgeNotificationGlyph.Paused);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Taskbar: badge update failed");
            }
            return;
        }

        if (_taskbar is null) return;
        try
        {
            if (primary is null) _taskbar.SetOverlayIcon(_hwnd, 0, string.Empty);
            else if (primary.IsPlaying) _taskbar.SetOverlayIcon(_hwnd, _overlayPlay, "Playing");
            else _taskbar.SetOverlayIcon(_hwnd, _overlayPause, "Paused");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Taskbar: overlay icon update failed");
        }
    }

    private nint WindowSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == _taskbarButtonCreatedMessage)
        {
            EnsureButtons();
        }
        else if (uMsg == WM_COMMAND && ((wParam.ToInt64() >> 16) & 0xFFFF) == THBN_CLICKED)
        {
            OnButtonClicked((int)(wParam.ToInt64() & 0xFFFF));
            return 0;
        }
        else if (uMsg == WM_SETTINGCHANGE && lParam != 0 &&
                 string.Equals(Marshal.PtrToStringUni(lParam), "ImmersiveColorSet", StringComparison.Ordinal))
        {
            // Taskbar theme follows the system (not app) theme; rebuild the glyphs so they stay legible.
            RebuildIcons();
            RefreshButtons();
            UpdatePlaybackBadge();
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void OnButtonClicked(int buttonId)
    {
        var primary = _nowPlaying.GetPrimary();
        if (primary is null) return;
        var key = primary.SessionKey;
        _ = buttonId switch
        {
            PrevButtonId => _nowPlaying.PreviousAsync(key),
            PlayPauseButtonId => _nowPlaying.TogglePlayPauseAsync(key),
            NextButtonId => _nowPlaying.NextAsync(key),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Adds the three buttons the first time the taskbar button is created; afterwards just
    /// refreshes their state (ThumbBarAddButtons can only run once per window).</summary>
    private void EnsureButtons()
    {
        if (_taskbar is null) return;
        if (_buttonsAdded)
        {
            RefreshButtons();
            return;
        }

        var buttons = BuildButtons();
        var hr = _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
        if (hr == 0)
        {
            _buttonsAdded = true;
            _logger.LogInformation("Taskbar: thumbnail media buttons added");
        }
        else
        {
            _logger.LogWarning("Taskbar: ThumbBarAddButtons failed (hr=0x{Hr:X8})", hr);
        }

        // The taskbar button was just (re)created, so (re)apply the playback badge for it.
        UpdatePlaybackBadge();
    }

    private void RefreshButtons()
    {
        if (_taskbar is null || !_buttonsAdded) return;
        var buttons = BuildButtons();
        _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
    }

    private THUMBBUTTON[] BuildButtons()
    {
        var primary = _nowPlaying.GetPrimary();
        var playing = primary?.IsPlaying ?? false;

        return new[]
        {
            MakeButton(PrevButtonId, _iconPrev, "Previous", primary?.CanPrevious ?? false),
            MakeButton(PlayPauseButtonId, playing ? _iconPause : _iconPlay,
                PlayPauseTip(primary), primary?.CanPlayPause ?? false),
            MakeButton(NextButtonId, _iconNext, "Next", primary?.CanNext ?? false),
        };
    }

    private static string PlayPauseTip(NowPlayingInfo? primary)
    {
        if (primary is null) return "Play";
        var verb = primary.IsPlaying ? "Pause" : "Play";
        var track = primary.Title;
        if (!string.IsNullOrWhiteSpace(primary.Artist)) track = $"{track} · {primary.Artist}";
        var tip = string.IsNullOrWhiteSpace(track) ? verb : $"{verb} – {track}";
        return tip.Length > 259 ? tip[..259] : tip;
    }

    private static THUMBBUTTON MakeButton(int id, nint icon, string tip, bool enabled) => new()
    {
        dwMask = THUMBBUTTONMASK.THB_ICON | THUMBBUTTONMASK.THB_TOOLTIP | THUMBBUTTONMASK.THB_FLAGS,
        iId = (uint)id,
        hIcon = icon,
        szTip = tip,
        dwFlags = enabled ? THUMBBUTTONFLAGS.THBF_ENABLED : THUMBBUTTONFLAGS.THBF_DISABLED,
    };

    // -------- Icon rendering (Win2D glyph -> HICON) --------

    private void RebuildIcons()
    {
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var size = Math.Max(16, (int)Math.Round(16 * scale));
        var colour = SystemUsesLightTheme()
            ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)   // light taskbar: near-black glyph
            : Color.FromArgb(255, 0xFF, 0xFF, 0xFF);  // dark taskbar: white glyph

        DestroyIcons();
        _iconPrev = RenderGlyphIcon(PrevGlyph, colour, size);
        _iconPlay = RenderGlyphIcon(PlayGlyph, colour, size);
        _iconPause = RenderGlyphIcon(PauseGlyph, colour, size);
        _iconNext = RenderGlyphIcon(NextGlyph, colour, size);

        // Overlay badge icons (a filled accent disc with a white glyph) only back the unpackaged
        // fallback; a packaged build uses the OS BadgeNotificationManager glyph instead, so skip them.
        if (!AppInfo.IsPackaged)
        {
            _overlayPlay = RenderOverlayIcon(PlayGlyph, size);
            _overlayPause = RenderOverlayIcon(PauseGlyph, size);
        }
    }

    private nint RenderGlyphIcon(string glyph, Color colour, int size)
    {
        try
        {
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96f);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                using var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe Fluent Icons",
                    FontSize = size * 0.7f,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                ds.DrawText(glyph, new Rect(0, 0, size, size), colour, format);
            }

            // Win2D renders to a premultiplied BGRA target, but CreateIconIndirect's colour bitmap
            // expects straight (non-premultiplied) alpha (like Bitmap.GetHicon). Feeding premultiplied
            // bytes darkens the anti-aliased strokes (the "faded glyph" look), so un-premultiply first.
            var pixels = target.GetPixelBytes();
            UnPremultiply(pixels);
            return CreateHIcon(pixels, size, size);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Taskbar: glyph icon render failed");
            return 0;
        }
    }

    private nint RenderOverlayIcon(string glyph, int size)
    {
        try
        {
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96f);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                var radius = size / 2f;
                ds.FillCircle(radius, radius, radius, GetAccentColour());
                using var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe Fluent Icons",
                    FontSize = size * 0.5f,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                ds.DrawText(glyph, new Rect(0, 0, size, size), Microsoft.UI.Colors.White, format);
            }

            var pixels = target.GetPixelBytes();
            UnPremultiply(pixels);
            return CreateHIcon(pixels, size, size);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Taskbar: overlay icon render failed");
            return 0;
        }
    }

    private static Color GetAccentColour()
    {
        try { return new UISettings().GetColorValue(UIColorType.Accent); }
        catch { return Color.FromArgb(255, 0, 120, 215); }
    }

    /// <summary>Converts premultiplied BGRA (Win2D's output) to straight alpha in place: c = c * 255 / a.
    /// Fully opaque and fully transparent pixels are untouched.</summary>
    private static void UnPremultiply(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 0 || a == 255) continue;
            bgra[i] = (byte)(bgra[i] * 255 / a);
            bgra[i + 1] = (byte)(bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)(bgra[i + 2] * 255 / a);
        }
    }

    private static nint CreateHIcon(byte[] bgra, int width, int height)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var hbmColor = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (hbmColor == 0 || bits == 0) return 0;
        Marshal.Copy(bgra, 0, bits, bgra.Length);

        // The AND-mask must be all-zero so the colour bitmap's alpha channel is used (a 1-bit set in
        // the mask makes the shell treat that pixel as transparent/inverted, which shows up as faded /
        // trimmed glyph edges). CreateBitmap does NOT zero its bits, so hand it a zeroed buffer.
        var maskStride = ((width + 15) / 16) * 2; // word-aligned monochrome scanline
        var maskBytes = new byte[maskStride * height];
        var pinned = GCHandle.Alloc(maskBytes, GCHandleType.Pinned);
        nint hbmMask;
        try { hbmMask = CreateBitmap(width, height, 1, 1, pinned.AddrOfPinnedObject()); }
        finally { pinned.Free(); }

        var info = new ICONINFO { fIcon = true, hbmMask = hbmMask, hbmColor = hbmColor };
        var hIcon = CreateIconIndirect(ref info);

        DeleteObject(hbmColor);
        DeleteObject(hbmMask);
        return hIcon;
    }

    private void DestroyIcons()
    {
        if (_iconPrev != 0) { DestroyIcon(_iconPrev); _iconPrev = 0; }
        if (_iconPlay != 0) { DestroyIcon(_iconPlay); _iconPlay = 0; }
        if (_iconPause != 0) { DestroyIcon(_iconPause); _iconPause = 0; }
        if (_iconNext != 0) { DestroyIcon(_iconNext); _iconNext = 0; }
        if (_overlayPlay != 0) { DestroyIcon(_overlayPlay); _overlayPlay = 0; }
        if (_overlayPause != 0) { DestroyIcon(_overlayPause); _overlayPause = 0; }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _nowPlaying.Changed -= OnNowPlayingChanged;
        if (AppInfo.IsPackaged)
        {
            try { BadgeNotificationManager.Current.ClearBadge(); } catch { /* platform may be gone */ }
        }
        else if (_taskbar is not null)
        {
            try { _taskbar.SetOverlayIcon(_hwnd, 0, string.Empty); } catch { /* taskbar may be gone */ }
        }
        if (_subclassProc is not null && _hwnd != 0)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }
        DestroyIcons();
        if (_taskbar is not null)
        {
            Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    // -------- Interop --------

    private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, int state);
        [PreserveSig] int RegisterTab(nint hwnd, nint hwndMDI);
        [PreserveSig] int UnregisterTab(nint hwndTab);
        [PreserveSig] int SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        [PreserveSig] int SetTabActive(nint hwndTab, nint hwndMDI, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(nint hwnd, nint himl);
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint hIcon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        [PreserveSig] int SetThumbnailClip(nint hwnd, nint clip);
    }

    [Flags]
    private enum THUMBBUTTONMASK : uint
    {
        THB_BITMAP = 0x1,
        THB_ICON = 0x2,
        THB_TOOLTIP = 0x4,
        THB_FLAGS = 0x8,
    }

    [Flags]
    private enum THUMBBUTTONFLAGS : uint
    {
        THBF_ENABLED = 0x0,
        THBF_DISABLED = 0x1,
        THBF_DISMISSONCLICK = 0x2,
        THBF_NOBACKGROUND = 0x4,
        THBF_HIDDEN = 0x8,
        THBF_NONINTERACTIVE = 0x10,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public THUMBBUTTONMASK dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public THUMBBUTTONFLAGS dwFlags;
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
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("Comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("Comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("Comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);
}
