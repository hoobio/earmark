using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Core.Settings;
using Earmark.Desktop.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private const double TitleBarSlotGap = 8;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);

    private static readonly Dictionary<string, Type> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Devices"] = typeof(DevicesPage),
        ["Rules"] = typeof(RulesPage),
        ["Sessions"] = typeof(SessionsPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private readonly IServiceProvider _services;
    private readonly SettingsService _settingsService;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastDuration };
    private Button? _selectedNav;
    private bool _quitting;
    private bool _hideWhenMinimized;
    private bool _shown;
    private bool _renderLogged;
    private DispatcherTimer? _minimizeHideTimer;
    private DispatcherTimer? _trimTimer;

    public MainWindow(IServiceProvider services, SettingsService settings, ILogger<MainWindow> logger)
    {
        _services = services;
        _settingsService = settings;
        _logger = logger;
        InitializeComponent();

        // WinUI keeps the 1px top resize border outside its client area, where Avalonia's extended client area draws over it.
        Root.Margin = new Thickness(0, 1, 0, 0);
        TitleText.Text = Title;
        ApplyShellSettings();
        settings.SettingsChanged += (_, _) => Ui.Post(ApplyShellSettings);
        _toastTimer.Tick += (_, _) => ShowToast(null);

        Activated += (_, _) =>
        {
            if (ForegroundGate.Release())
            {
                _logger.LogInformation("Window activated after a start without focus");
            }

            UpdateCaptionButtons();
        };
        Deactivated += (_, _) => UpdateCaptionButtons();
        Opened += (_, _) =>
        {
            UpdateCaptionButtons();
            _ = LogRenderModeAsync();
        };
        Closing += OnWindowClosing;
        PropertyChanged += OnWindowPropertyChanged;
        Win32Properties.AddWndProcHookCallback(this, OnWndProc);
        AppTitleBar.LayoutUpdated += (_, _) => PositionTitleBarSlot();

        Navigate("Devices");
    }

    public event EventHandler? ReapplyRulesRequested;

    public ForegroundGate ForegroundGate { get; } = new();

    public Control? CurrentPage => PageHost.Content as Control;

    public Button UpdateButton => UpdatePill;

    public object? TitleBarContent
    {
        get => TitleBarSlot.Content;
        set => TitleBarSlot.Content = value;
    }

    private AppSettings Settings => _settingsService.Current;

    public void Navigate(string tag)
    {
        var item = new[] { DevicesItem, RulesItem, SessionsItem, SettingsItem }.FirstOrDefault(button => string.Equals(button.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No nav item is tagged '{tag}'", nameof(tag));
        Navigate(item);
    }

    public void ShowToast(string? message)
    {
        _toastTimer.Stop();
        if (message is not null)
        {
            ToastText.Text = message;
            _toastTimer.Start();
        }

        ToastHost.Opacity = message is null ? 0 : 1;
        ToastHost.RenderTransform = TransformOperations.Parse(message is null ? "translateY(16px)" : "translateY(0px)");
    }

    public Task<bool> ShowNoticeAsync(string message, string? primary, string close)
    {
        var done = new TaskCompletionSource<bool>();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var notice = new Border
        {
            Classes = { "Notice" },
            Child = new DockPanel
            {
                Children =
                {
                    buttons,
                    new TextBlock { Classes = { "Glyph" }, Text = (string)this.FindResource("Glyph.Warning")! },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        DockPanel.SetDock(buttons, Dock.Right);
        void Add(string label, bool result, ControlTheme? theme)
        {
            var button = new Button { Content = label, Theme = theme };
            button.Click += (_, _) =>
            {
                NoticeHost.Children.Remove(notice);
                done.TrySetResult(result);
            };
            buttons.Children.Add(button);
        }

        if (primary is not null)
        {
            Add(primary, true, (ControlTheme?)this.FindResource("CautionButtonStyle"));
        }

        Add(close, false, null);
        NoticeHost.Children.Add(notice);
        return done.Task;
    }

    public void CreateTrayIcon()
    {
        TrayMenuFocus.Attach();
        using var icon = AssetLoader.Open(new Uri("avares://Earmark/Assets/Earmark.ico"));
        var open = new NativeMenuItem("Open Earmark");
        open.Click += (_, _) => ShowFromTray();
        var quick = new NativeMenuItem("Quick Controls");
        quick.Click += (_, _) => _services.GetRequiredService<QuickControlsWindow>().Open();
        var reapply = new NativeMenuItem("Reapply rules");
        reapply.Click += (_, _) =>
        {
            _logger.LogInformation("Reapply rules requested from the tray");
            ReapplyRulesRequested?.Invoke(this, EventArgs.Empty);
        };
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => QuitCompletely();
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(icon),
            ToolTipText = "Earmark",
            IsVisible = Settings.ShowTrayIcon,
            Menu = [open, quick, reapply, new NativeMenuItemSeparator(), exit],
        };
        trayIcon.Clicked += (_, _) => ShowFromTray();
        TrayIcon.SetIcons(Application.Current!, [trayIcon]);
    }

    public void HideToTray()
    {
        if (!Settings.ShowTrayIcon)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        Native.SetEfficiencyMode(true);
        _trimTimer?.Stop();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            TrimIfHidden();
        };
        _trimTimer.Start();
    }

    public void TrimIfHidden()
    {
        if (!IsVisible)
        {
            Native.TrimWorkingSet();
        }
    }

    public void ShowFromTray()
    {
        ForegroundGate.Release();
        _minimizeHideTimer?.Stop();
        _trimTimer?.Stop();
        Native.SetEfficiencyMode(false);
        ShowActivated = true;
        ShowRendered();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Native.ForceForeground(TryGetPlatformHandle()?.Handle ?? 0);
    }

    public void ShowWithoutFocus()
    {
        _trimTimer?.Stop();
        Native.SetEfficiencyMode(false);
        ShowActivated = false;
        ShowRendered();
    }

    public void QuitCompletely()
    {
        _quitting = true;
        _logger.LogInformation("Exit requested");
        foreach (var trayIcon in TrayIcon.GetIcons(Application.Current!) ?? [])
        {
            trayIcon.Dispose();
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ApplyShellSettings()
    {
        GroundGlow.IsVisible = Settings.Backdrop != BackdropMode.Solid;
        foreach (var trayIcon in TrayIcon.GetIcons(Application.Current!) ?? [])
        {
            trayIcon.IsVisible = Settings.ShowTrayIcon;
        }
    }

    private void Navigate(Button item)
    {
        if (_selectedNav == item)
        {
            return;
        }

        _selectedNav?.Classes.Remove("Selected");
        item.Classes.Add("Selected");
        _selectedNav = item;
        var page = (Control)_services.GetRequiredService(Pages[(string)item.Tag!]);
        var animate = PageHost.Content is not null && _shown;
        PageHost.Content = page;
        if (animate)
        {
            PlayEntrance(page);
        }
    }

    private void PositionTitleBarSlot()
    {
        var host = TitleBarSlotHost.Bounds.Width;
        if (host <= 0 || TitleBarSlot.Content is not Control content)
        {
            return;
        }

        var width = Math.Min(content.MaxWidth, Math.Max(0, host - 2 * TitleBarSlotGap));
        if (TitleBarSlot.Width != width)
        {
            TitleBarSlot.Width = width;
        }

        var room = host - width;
        var centred = (AppTitleBar.Bounds.Width - width) / 2 - TitleBarSlotHost.Bounds.X;
        var x = room >= 2 * TitleBarSlotGap ? Math.Clamp(centred, TitleBarSlotGap, room - TitleBarSlotGap) : Math.Max(0, room);
        var offset = (TranslateTransform)TitleBarSlot.RenderTransform!;
        if (offset.X != x)
        {
            offset.X = x;
        }
    }

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button item)
        {
            Navigate(item);
        }
    }

    private static void PlayEntrance(Control page)
    {
        page.RenderTransform = new TranslateTransform(0, 18);
        _ = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 18d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        }.RunAsync(page);
    }

    private async Task LogRenderModeAsync()
    {
        if (_renderLogged || ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor)
        {
            return;
        }

        _renderLogged = true;
        var gpu = await compositor.TryGetCompositionGpuInterop();
        // Avalonia exposes no public read of the chosen Win32 rendering or composition mode; a GPU interop exists only on the AngleEgl path.
        _logger.LogInformation(
            "Render mode: {Mode} (chain AngleEgl, Software; composition WinUIComposition, DirectComposition, RedirectionSurface)",
            gpu is null ? "Software" : $"GPU AngleEgl, device LUID {Convert.ToHexString(gpu.DeviceLuid ?? [])}");
    }

    // FluentAvalonia binds the caption buttons' inactive class to an ancestor Window, but the decorations layer is the window's sibling under its host.
    private void UpdateCaptionButtons()
    {
        foreach (var button in this.GetVisualParent()?.GetVisualDescendants().OfType<Button>() ?? [])
        {
            if (button.Classes.Contains("CaptionButton"))
            {
                button.Classes.Set("inactive", !IsActive);
            }
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        e.Cancel = true;
        if (Settings.CloseToTray && Settings.ShowTrayIcon)
        {
            MinimizeThenHideToTray();
            return;
        }

        QuitCompletely();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty
            && WindowState == WindowState.Minimized
            && Settings.ShowTrayIcon
            && (Settings.MinimizeToTray || _hideWhenMinimized))
        {
            _hideWhenMinimized = false;
            HideAfterMinimizeAnimation();
        }
    }

    private void HideAfterMinimizeAnimation()
    {
        if (!Native.MinimizeAnimationEnabled())
        {
            HideToTray();
            return;
        }

        // The OS raises no event when the minimise animation ends, and hiding earlier cancels it.
        _minimizeHideTimer?.Stop();
        _minimizeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _minimizeHideTimer.Tick += (_, _) =>
        {
            _minimizeHideTimer.Stop();
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        };
        _minimizeHideTimer.Start();
    }

    private void MinimizeThenHideToTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            _hideWhenMinimized = true;
            WindowState = WindowState.Minimized;
            return;
        }

        HideToTray();
    }

    private void ShowRendered()
    {
        if (_shown)
        {
            if (!IsVisible)
            {
                Show();
            }

            return;
        }

        _shown = true;
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        Native.Cloak(handle, cloaked: true);
        Show();
        RequestAnimationFrame(_ => RequestAnimationFrame(_ => Native.Cloak(handle, cloaked: false)));
    }

    // Windows sends these to request or confirm a logoff, reboot or app-update shutdown; unhandled, close-to-tray would hold the app until Windows force-kills it.
    private nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // Moving onto a drawn caption button ends client tracking with WM_MOUSELEAVE right after Avalonia's non-client move set the hover, and Windows resends the move only 0.6s to 1.7s later.
        if (msg == Native.WM_MOUSELEAVE)
        {
            Native.RepostCaptionButtonMove(hWnd);
        }

        if (msg is Native.WM_QUERYENDSESSION or Native.WM_ENDSESSION)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_quitting)
                {
                    _logger.LogInformation("Session ending; quitting");
                    QuitCompletely();
                }
            });
        }

        return 0;
    }
}
