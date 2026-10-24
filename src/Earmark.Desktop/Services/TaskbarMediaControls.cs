using System.Globalization;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Services;

public sealed class TaskbarMediaControls(INowPlayingService nowPlaying, ILogger<TaskbarMediaControls> logger)
{
    private const int PrevId = 1;
    private const int PlayPauseId = 2;
    private const int NextId = 3;
    private const uint WmCommand = 0x0111;
    private const int ThbnClicked = 0x1800;

    private static readonly string PrevGlyph = char.ConvertFromUtf32(0xEED6);
    private static readonly string NextGlyph = char.ConvertFromUtf32(0xECAB);
    private static readonly string PlayGlyph = char.ConvertFromUtf32(61068);
    private static readonly string PauseGlyph = char.ConvertFromUtf32(60762);
    private static readonly TimeSpan PauseHideDelay = TimeSpan.FromSeconds(30);

    private readonly uint _buttonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    private nint _hwnd;
    private double _scaling = 1;
    private ITaskbarList3? _taskbar;
    private bool _buttonsAdded;
    private nint _prev, _play, _pause, _next, _overlayPlay, _overlayPause;
    private Timer? _pauseTimer;
    private string? _pausedKey;
    private bool _pauseHidden;

    public void Attach(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
        {
            return;
        }

        if (!CreateTaskbar())
        {
            return;
        }

        _hwnd = hwnd;
        _scaling = window.RenderScaling;
        Win32Properties.AddWndProcHookCallback(window, WndProc);
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Ui.Post(Rebuild);
        }

        RebuildIcons();
        nowPlaying.Changed += (_, _) => Ui.Post(() =>
        {
            RefreshButtons();
            UpdateOverlay();
        });
        if (window.IsVisible)
        {
            EnsureButtons();
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // Hide() and an Explorer restart destroy the taskbar button with its thumbnail buttons and overlay, and Explorer restarting also drops the old ITaskbarList3.
        if (msg == _buttonCreatedMessage && CreateTaskbar())
        {
            _buttonsAdded = false;
            EnsureButtons();
        }
        else if (msg == WmCommand && ((wParam >> 16) & 0xFFFF) == ThbnClicked)
        {
            OnClicked((int)(wParam & 0xFFFF));
            handled = true;
        }

        return 0;
    }

    private bool CreateTaskbar()
    {
        try
        {
            var taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"))!)!;
            taskbar.HrInit();
            _taskbar = taskbar;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Taskbar: ITaskbarList3 unavailable; thumbnail media controls disabled");
            _taskbar = null;
            return false;
        }
    }

    private void Rebuild()
    {
        RebuildIcons();
        RefreshButtons();
        UpdateOverlay();
    }

    private void OnClicked(int id)
    {
        if (nowPlaying.GetPrimary()?.SessionKey is not { } key)
        {
            return;
        }

        var task = id switch
        {
            PrevId => nowPlaying.PreviousAsync(key),
            PlayPauseId => nowPlaying.TogglePlayPauseAsync(key),
            NextId => nowPlaying.NextAsync(key),
            _ => Task.CompletedTask,
        };
        _ = task.ContinueWith(t => logger.LogWarning(t.Exception, "Taskbar: media command failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void EnsureButtons()
    {
        if (_taskbar is null)
        {
            return;
        }

        if (!_buttonsAdded)
        {
            var buttons = BuildButtons();
            var hr = _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = hr == 0;
            if (_buttonsAdded)
            {
                logger.LogInformation("Taskbar: thumbnail media buttons added");
            }
            else
            {
                logger.LogWarning("Taskbar: ThumbBarAddButtons failed (hr=0x{Hr:X8})", hr);
            }
        }
        else
        {
            RefreshButtons();
        }

        UpdateOverlay();
    }

    private void RefreshButtons()
    {
        if (_taskbar is not null && _buttonsAdded)
        {
            var buttons = BuildButtons();
            _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
        }
    }

    private THUMBBUTTON[] BuildButtons()
    {
        var primary = nowPlaying.GetPrimary();
        return
        [
            Button(PrevId, _prev, "Previous", primary?.CanPrevious ?? false),
            Button(PlayPauseId, primary?.IsPlaying == true ? _pause : _play, PlayPauseTip(primary), primary?.CanPlayPause ?? false),
            Button(NextId, _next, "Next", primary?.CanNext ?? false),
        ];
    }

    private static string PlayPauseTip(NowPlayingInfo? primary)
    {
        if (primary is null)
        {
            return "Play";
        }

        var verb = primary.IsPlaying ? "Pause" : "Play";
        var track = string.IsNullOrWhiteSpace(primary.Artist) ? primary.Title : $"{primary.Title} · {primary.Artist}";
        var tip = string.IsNullOrWhiteSpace(track) ? verb : $"{verb} - {track}";
        return tip.Length > 259 ? tip[..259] : tip;
    }

    private static THUMBBUTTON Button(int id, nint icon, string tip, bool enabled) => new()
    {
        dwMask = 0x2 | 0x4 | 0x8,
        iId = (uint)id,
        hIcon = icon,
        szTip = tip,
        dwFlags = enabled ? 0u : 1u,
    };

    private void UpdateOverlay()
    {
        var primary = nowPlaying.GetPrimary();
        if (primary is null || primary.IsPlaying)
        {
            _pauseTimer?.Dispose();
            _pauseTimer = null;
            (_pausedKey, _pauseHidden) = (null, false);
            SetOverlay(primary is null ? 0 : _overlayPlay, primary is null ? string.Empty : "Playing");
            return;
        }

        if (primary.SessionKey != _pausedKey)
        {
            _pauseTimer?.Dispose();
            _pauseTimer = null;
            (_pausedKey, _pauseHidden) = (primary.SessionKey, false);
        }

        if (_pauseHidden)
        {
            SetOverlay(0, string.Empty);
            return;
        }

        SetOverlay(_overlayPause, "Paused");
        _pauseTimer ??= new Timer(_ => Ui.Post(() =>
        {
            if (nowPlaying.GetPrimary() is { IsPlaying: false } still && still.SessionKey == _pausedKey)
            {
                _pauseHidden = true;
                SetOverlay(0, string.Empty);
            }
        }), null, PauseHideDelay, Timeout.InfiniteTimeSpan);
    }

    private void SetOverlay(nint icon, string description)
    {
        try
        {
            _taskbar?.SetOverlayIcon(_hwnd, icon, description);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Taskbar: overlay icon update failed");
        }
    }

    private void RebuildIcons()
    {
        var size = Math.Max(16, (int)Math.Round(16 * _scaling));
        var glyph = SystemUsesLightTheme() ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var accent = Application.Current?.PlatformSettings?.GetColorValues().AccentColor1 ?? Color.FromRgb(0, 120, 215);
        foreach (var icon in new[] { _prev, _play, _pause, _next, _overlayPlay, _overlayPause }.Where(i => i != 0))
        {
            DestroyIcon(icon);
        }

        _prev = RenderIcon(PrevGlyph, glyph, null, size);
        _play = RenderIcon(PlayGlyph, glyph, null, size);
        _pause = RenderIcon(PauseGlyph, glyph, null, size);
        _next = RenderIcon(NextGlyph, glyph, null, size);
        _overlayPlay = RenderIcon(PlayGlyph, Colors.White, accent, size);
        _overlayPause = RenderIcon(PauseGlyph, Colors.White, accent, size);
    }

    private nint RenderIcon(string glyph, Color foreground, Color? disc, int size)
    {
        try
        {
            var font = Application.Current?.FindResource("IconFontFamily") as FontFamily ?? FontFamily.Default;
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var context = bitmap.CreateDrawingContext())
            {
                if (disc is { } d)
                {
                    context.DrawEllipse(new SolidColorBrush(d), null, new Rect(0, 0, size, size));
                }

                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(font), size * (disc is null ? 0.75 : 0.55), new SolidColorBrush(foreground));
                context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var pixels = new byte[size * size * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size, size), handle.AddrOfPinnedObject(), pixels.Length, size * 4);
            }
            finally
            {
                handle.Free();
            }

            // RenderTargetBitmap is premultiplied; a 32bpp icon colour bitmap is read as straight alpha.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] is var a and > 0 and < 255)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
                    }
                }
            }

            return CreateIcon(pixels, size);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Taskbar: glyph icon render failed");
            return 0;
        }
    }

    private static nint CreateIcon(byte[] bgra, int size)
    {
        var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var colour = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (colour == 0 || bits == 0)
        {
            return 0;
        }

        Marshal.Copy(bgra, 0, bits, bgra.Length);

        // CreateBitmap does not zero its bits, and a set mask bit makes the shell treat that pixel as transparent.
        var mask = new byte[((size + 15) / 16) * 2 * size];
        var pinned = GCHandle.Alloc(mask, GCHandleType.Pinned);
        nint maskBitmap;
        try
        {
            maskBitmap = CreateBitmap(size, size, 1, 1, pinned.AddrOfPinnedObject());
        }
        finally
        {
            pinned.Free();
        }

        var info = new ICONINFO { fIcon = true, hbmMask = maskBitmap, hbmColor = colour };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(colour);
        DeleteObject(maskBitmap);
        return icon;
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, int state);
        [PreserveSig] int RegisterTab(nint hwnd, nint hwndMdi);
        [PreserveSig] int UnregisterTab(nint hwndTab);
        [PreserveSig] int SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        [PreserveSig] int SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(nint hwnd, nint himl);
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
