using System.Runtime.InteropServices;

using Earmark.App.Settings;
using Earmark.App.ViewModels;

using H.NotifyIcon;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using WinRT.Interop;

namespace Earmark.App.Services;

public interface IWindowChromeManager
{
    void Attach(Window window);
    void RestoreWindow();
    void HideToTray();
    void RequestExit();
}

internal sealed class WindowChromeManager : IWindowChromeManager, IDisposable
{
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_MINIMIZE = 0xF020;
    private const uint WM_GETMINMAXINFO = 0x0024;
    // One device-card column at its min width plus the Devices page side padding (DevicesScrollPadding,
    // 16+16). At this width the NavigationView is in its minimal mode, so the content region spans the
    // full window and a single card fits without clipping. Compact cards use a ~20%-narrower column
    // (PeakMeterOptions.ColumnMinWidth), so the floor follows the live Compact setting.
    private const int DevicesSidePaddingDip = 32;
    private static int MinWindowWidthDip(bool compact) =>
        (int)(compact ? PeakMeterOptions.CompactColumnMinWidth : PeakMeterOptions.DefaultColumnMinWidth) + DevicesSidePaddingDip;
    private const int MinWindowHeightDip = 340;

    private readonly ISettingsService _settings;
    private TaskbarIcon? _trayIcon;
    private Window? _window;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _exitRequested;
    private SUBCLASSPROC? _subclassProc;
    private nint _windowHandle;

    public WindowChromeManager(ISettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _windowHandle = WindowNative.GetWindowHandle(window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _presenter = _appWindow?.Presenter as OverlappedPresenter;

        if (_appWindow is not null)
        {
            _appWindow.Closing += OnAppWindowClosing;
        }

        InstallSubclass();
        SyncTrayIcon();
    }

    public void RestoreWindow()
    {
        if (_window is null || _appWindow is null)
        {
            return;
        }

        _appWindow.Show();
        _presenter?.Restore();
        SetForegroundWindow(_windowHandle);
    }

    public void HideToTray()
    {
        if (_appWindow is null || _settings.Current is { ShowTrayIcon: false })
        {
            // Without tray, falling back to a normal minimize.
            _presenter?.Minimize();
            return;
        }

        _appWindow.Hide();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        _trayIcon?.Dispose();
        _trayIcon = null;

        // Window.Close() fires Window.Closed synchronously, which runs App.DisposeHost()
        // and tears down COM. Belt-and-suspenders: also call DisposeHost directly in case
        // the close path is ever changed to be async.
        _window?.Close();
        (Microsoft.UI.Xaml.Application.Current as App)?.DisposeHost();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // SettingsService raises this after `ConfigureAwait(false)` so we may be
        // off the UI thread. The tray icon is a XAML FrameworkElement and its
        // constructor / Dispose must run on the dispatcher.
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            SyncTrayIcon();
        }
        else
        {
            dispatcher.TryEnqueue(SyncTrayIcon);
        }
    }

    private void SyncTrayIcon()
    {
        if (_window is null)
        {
            return;
        }

        if (_settings.Current.ShowTrayIcon)
        {
            EnsureTrayIcon();
        }
        else
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var icon = new TaskbarIcon
        {
            ToolTipText = "Earmark",
            NoLeftClickDelay = true,
        };

        try
        {
            // A packaged app can't load a BitmapImage from a file:// path under WindowsApps; ms-appx is
            // the sanctioned scheme there. Unpackaged (MSI) has no ms-appx root, so use the on-disk path.
            if (AppInfo.IsPackaged)
            {
                icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico"));
            }
            else
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }
            }
        }
        catch
        {
            // Icon load failure is non-fatal.
        }

        icon.LeftClickCommand = new RelayCommandSimple(_ => RestoreWindow());

        // H.NotifyIcon's default ContextMenuMode (PopupMenu, native Win32) wires
        // native item clicks to MenuFlyoutItem.Command only - Click events are
        // never raised. Bind Command, not Click.
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Open Earmark",
            Command = new RelayCommandSimple(_ => RestoreWindow()),
        });
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "Quit",
            Command = new RelayCommandSimple(_ => RequestExit()),
        });
        icon.ContextFlyout = menu;

        icon.ForceCreate();
        _trayIcon = icon;
    }

    // AppWindow.Closing is the cancellable close event in WinUI 3.
    // Window.Closed fires after the close is committed and its Handled flag does
    // not cancel anything, so route through here instead.
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        if (_settings.Current.CloseToTray && _settings.Current.ShowTrayIcon)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void InstallSubclass()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        _subclassProc = WindowSubclassProc;
        SetWindowSubclass(_windowHandle, _subclassProc, 0xEAA0, 0);
    }

    private nint WindowSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            if (_settings.Current.MinimizeToTray && _settings.Current.ShowTrayIcon)
            {
                HideToTray();
                return 0;
            }
        }

        if (uMsg == WM_GETMINMAXINFO && lParam != 0)
        {
            var dpi = GetDpiForWindow(hWnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.x = (int)Math.Round(MinWindowWidthDip(_settings.Current.CompactCards) * scale);
            mmi.ptMinTrackSize.y = (int)Math.Round(MinWindowHeightDip * scale);
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            return 0;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
        }

        if (_subclassProc is not null && _windowHandle != 0)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProc, 0xEAA0);
        }

        _trayIcon?.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetForegroundWindow(nint hWnd);

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private sealed class RelayCommandSimple : System.Windows.Input.ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommandSimple(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public event EventHandler? CanExecuteChanged;
        public void Execute(object? parameter) => _execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
