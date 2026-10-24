using System.Diagnostics;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

using Earmark.Core.Settings;
using Earmark.Desktop.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Views;

public sealed partial class QuickControlsWindow : Window
{
    public const string RegistrationFailed = "That shortcut couldn't be registered. Try another combination.";

    private const double PanelWidth = 424;
    private const double EdgeMargin = 8;
    private const double Gap = 8;
    private const double MinimumOverflowHeight = 220;
    private const int HotkeyId = 0xEA51;
    private const uint WM_HOTKEY = 0x0312;

    private readonly IServiceProvider _services;
    private readonly SettingsService _settings;
    private readonly QuickControlsVisibility _visibility;
    private readonly ILogger<QuickControlsWindow> _logger;
    private readonly nint _handle;
    private HotkeyGesture? _registered;
    private PixelRect _workArea;
    private (string, bool)? _appliedSetting;
    private int _generation;

    public QuickControlsWindow(IServiceProvider services, SettingsService settings, QuickControlsVisibility visibility, ILogger<QuickControlsWindow> logger)
    {
        _services = services;
        _settings = settings;
        _visibility = visibility;
        _logger = logger;
        InitializeComponent();
        _handle = TryGetPlatformHandle()?.Handle ?? 0;
        Win32Properties.AddWndProcHookCallback(this, OnWndProc);
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive)
            {
                Dismiss();
            }
        });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
        }, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                Dismiss();
            }
        };
    }

    public event EventHandler? HotkeyStatusChanged;

    public string HotkeyStatus { get; private set; } = string.Empty;

    public void Start()
    {
        _settings.SettingsChanged += (_, _) => Ui.Post(ApplyHotkeySetting);
        ApplyHotkeySetting();
        Warm();
    }

    private void ApplyHotkeySetting()
    {
        var setting = (_settings.Current.QuickControlsHotkey, _settings.Current.QuickControlsEnabled);
        if (setting != _appliedSetting)
        {
            _appliedSetting = setting;
            RegisterHotkey(setting.QuickControlsHotkey, toastOnFailure: true);
        }
    }

    public void Open() => Open(Stopwatch.GetTimestamp());

    public bool RegisterHotkey(string text, bool toastOnFailure)
    {
        var gesture = HotkeyGesture.Parse(text);
        if (!_settings.Current.QuickControlsEnabled)
        {
            Unregister();
            return SetStatus($"Global shortcut: {gesture?.ToString() ?? text}", ok: true);
        }

        if (gesture is not null && gesture == _registered)
        {
            return true;
        }

        var previous = _registered;
        Unregister();
        if (gesture is not null && RegisterHotKey(_handle, HotkeyId, gesture.Modifiers, gesture.VirtualKey))
        {
            _registered = gesture;
            _logger.LogInformation("Registered Quick Controls hotkey {Hotkey} on window 0x{Handle:X}", gesture, _handle);
            return SetStatus($"Global shortcut: {gesture}", ok: true);
        }

        _logger.LogWarning("Could not register Quick Controls hotkey {Hotkey} (error {Error})", text, Marshal.GetLastPInvokeError());
        if (previous is not null && RegisterHotKey(_handle, HotkeyId, previous.Modifiers, previous.VirtualKey))
        {
            _registered = previous;
        }

        if (toastOnFailure)
        {
            _services.GetRequiredService<MainWindow>().ShowToast(RegistrationFailed);
        }

        return SetStatus(RegistrationFailed, ok: false);
    }

    private bool SetStatus(string status, bool ok)
    {
        HotkeyStatus = status;
        HotkeyStatusChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    private void Unregister()
    {
        if (_registered is not null)
        {
            _ = UnregisterHotKey(_handle, HotkeyId);
            _registered = null;
        }
    }

    private void Warm()
    {
        Position = new PixelPoint(-32000, -32000);
        ShowActivated = false;
        Build(maxHeight: 600);
        Native.Cloak(_handle, cloaked: true);
        Show();
        RequestAnimationFrame(_ => RequestAnimationFrame(_ =>
        {
            _logger.LogInformation("Quick Controls window warmed (transparency {Level})", ActualTransparencyLevel);
            if (!_visibility.IsVisible)
            {
                Hide();
            }
        }));
    }

    private void Toggle(long started)
    {
        if (_visibility.IsVisible)
        {
            Dismiss();
        }
        else
        {
            Open(started);
        }
    }

    private void Open(long started)
    {
        if (_visibility.IsVisible)
        {
            Activate();
            Native.ForceForeground(_handle);
            return;
        }

        _visibility.IsVisible = true;
        var generation = ++_generation;
        bool Current() => generation == _generation && _visibility.IsVisible;
        var screen = ResolveScreen();
        _workArea = screen.WorkingArea;
        Native.Cloak(_handle, cloaked: true);
        Position = _workArea.TopLeft;
        Width = Math.Min(PanelWidth, (_workArea.Width / screen.Scaling) - (EdgeMargin * 2));
        var blocks = Build(_workArea.Height / screen.Scaling);
        Place(screen.Scaling);
        ShowActivated = true;
        Show();
        RequestAnimationFrame(_ =>
        {
            if (!Current())
            {
                return;
            }

            Place(RenderScaling);
            PlayEntrance();
            RequestAnimationFrame(_ =>
            {
                if (!Current())
                {
                    return;
                }

                Native.Cloak(_handle, cloaked: false);
                Activate();
                Native.ForceForeground(_handle);
                _logger.LogInformation("Quick Controls shown with {Blocks} blocks in {Ms:F0} ms", blocks, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            });
        });
    }

    private void Dismiss()
    {
        if (!_visibility.IsVisible)
        {
            return;
        }

        _visibility.IsVisible = false;
        _generation++;
        Native.Cloak(_handle, cloaked: true);
        Hide();
    }

    private Screen ResolveScreen() =>
        (_settings.Current.QuickControlsDisplay == QuickControlsDisplayMode.CurrentlyActive && GetCursorPos(out var cursor)
            ? Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))
            : null)
        ?? Screens.Primary
        ?? Screens.All[0];

    private void Place(double scaling)
    {
        var margin = (int)Math.Round(EdgeMargin * scaling);
        var size = PixelSize.FromSize(new Size(Width, Height), scaling);
        Position = new PixelPoint(
            Math.Max(_workArea.X + margin, _workArea.Right - size.Width - margin),
            Math.Max(_workArea.Y + margin, _workArea.Bottom - size.Height - margin));
    }

    private int Build(double maxHeight)
    {
        foreach (var old in Stack.Children.OfType<ContentControl>())
        {
            old.Content = null;
        }

        Stack.Children.Clear();
        var blocks = _services.GetKeyedService<Func<IReadOnlyList<Control>>>("QuickControlsBlocks")?.Invoke() is { Count: > 0 } produced
            ? produced
            : [Placeholder()];
        var theme = (ControlTheme)this.FindResource("QuickControlsPanel")!;
        var solid = IsSolidBackdrop();
        var panels = blocks.Reverse().Select(block => new ContentControl { Theme = theme, Content = block, ContextMenu = SettingsMenu() }).ToList();
        foreach (var panel in panels)
        {
            panel.Classes.Set("Solid", solid);
            Stack.Children.Add(panel);
        }

        var heights = panels.Select(panel =>
        {
            panel.Measure(new Size(Width, double.PositiveInfinity));
            return Math.Ceiling(panel.DesiredSize.Height);
        }).ToList();
        FitBlockHeights(heights, maxHeight - (EdgeMargin * 2) - (Gap * (heights.Count - 1)));
        for (var i = 0; i < panels.Count; i++)
        {
            panels[i].Height = heights[i];
        }

        Height = heights.Sum() + (Gap * (heights.Count - 1));
        return blocks.Count;
    }

    private bool IsSolidBackdrop() => _settings.Current.QuickControlsBackdrop switch
    {
        QuickControlsBackdropMode.Solid => true,
        QuickControlsBackdropMode.UseAppAppearance => _settings.Current.Backdrop == BackdropMode.Solid,
        _ => false,
    };

    private static Border Placeholder() => new()
    {
        Background = (IBrush?)Application.Current?.FindResource("CardBackgroundFillColorDefaultBrush"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Child = new TextBlock { Text = "Pin devices from the Devices page", TextWrapping = TextWrapping.Wrap },
    };

    private ContextMenu SettingsMenu()
    {
        var settings = new MenuItem
        {
            Header = "Settings",
            Icon = new TextBlock { Classes = { "Glyph" }, Text = this.FindResource("Glyph.Setting") as string },
        };
        settings.Click += (_, _) => OpenSettings();
        return new ContextMenu { Items = { settings } };
    }

    private void OpenSettings()
    {
        Dismiss();
        var main = _services.GetRequiredService<MainWindow>();
        main.ShowFromTray();
        if (_services.GetKeyedService<Action<string>>("OpenSettingsSection") is { } open)
        {
            open("QuickControls");
        }
        else
        {
            main.Navigate("Settings");
        }
    }

    private void PlayEntrance()
    {
        Stack.RenderTransform = new TranslateTransform(12, 0);
        _ = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.XProperty, 12d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.XProperty, 0d) } },
            },
        }.RunAsync(Stack);
    }

    private static void FitBlockHeights(List<double> heights, double available)
    {
        var overflow = heights.Sum() - available;
        while (overflow > 0 && heights.Select((height, index) => (height, index)).Where(item => item.height > MinimumOverflowHeight).OrderByDescending(item => item.height).FirstOrDefault() is { height: > 0 } tallest)
        {
            var reduction = Math.Min(overflow, tallest.height - MinimumOverflowHeight);
            heights[tallest.index] -= reduction;
            overflow -= reduction;
        }

        while (overflow > 0 && heights.IndexOf(heights.Max()) is var index && heights[index] > 1)
        {
            var reduction = Math.Min(overflow, heights[index] - 1);
            heights[index] -= reduction;
            overflow -= reduction;
        }
    }

    private nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            var started = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() => Toggle(started));
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
}
