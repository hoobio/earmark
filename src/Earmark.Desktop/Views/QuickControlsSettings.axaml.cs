using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Earmark.Core.Settings;
using Earmark.Desktop.Services;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.Views;

public sealed partial class QuickControlsSettings : UserControl
{
    private const string Hint = "Press a shortcut with Ctrl, Alt, or Win plus one non-modifier key.";

    private readonly SettingsService _settings;
    private readonly QuickControlsWindow _overlay;
    private readonly ILogger<QuickControlsSettings> _logger;

    public QuickControlsSettings(SettingsService settings, QuickControlsWindow overlay, ILogger<QuickControlsSettings> logger)
    {
        _settings = settings;
        _overlay = overlay;
        _logger = logger;
        InitializeComponent();
        Bind(EnabledToggle, s => s.QuickControlsEnabled, (s, v) => s.QuickControlsEnabled = v);
        Bind(CompactToggle, s => s.QuickControlsCompact, (s, v) => s.QuickControlsCompact = v);
        Bind(NowPlayingToggle, s => s.QuickControlsShowNowPlaying, (s, v) => s.QuickControlsShowNowPlaying = v);
        Bind(BadgesToggle, s => s.QuickControlsShowDeviceBadges, (s, v) => s.QuickControlsShowDeviceBadges = v);
        Bind(DividersToggle, s => s.QuickControlsShowDividers, (s, v) => s.QuickControlsShowDividers = v);
        Bind(RulesToggle, s => s.QuickControlsShowRules, (s, v) => s.QuickControlsShowRules = v);
        Bind(BackdropBox, s => (int)s.QuickControlsBackdrop, (s, v) => s.QuickControlsBackdrop = (QuickControlsBackdropMode)v);
        Bind(DisplayBox, s => (int)s.QuickControlsDisplay, (s, v) => s.QuickControlsDisplay = (QuickControlsDisplayMode)v);
        ShortcutCard.Description = overlay.HotkeyStatus;
        overlay.HotkeyStatusChanged += (_, _) => ShortcutCard.Description = overlay.HotkeyStatus;
    }

    private void Bind(ToggleSwitch toggle, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        toggle.IsChecked = get(_settings.Current);
        toggle.IsCheckedChanged += (_, _) => Persist(s => set(s, toggle.IsChecked == true));
    }

    private void Bind(ComboBox box, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        box.SelectedIndex = get(_settings.Current);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                Persist(s => set(s, box.SelectedIndex));
            }
        };
    }

    private void Persist(Action<AppSettings> mutate)
    {
        mutate(_settings.Current);
        _ = _settings.SaveAsync().ContinueWith(t => _logger.LogError(t.Exception, "Saving Quick Controls settings failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async void OnChangeShortcut(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } owner)
        {
            return;
        }

        var shortcut = new TextBlock
        {
            Text = _settings.Current.QuickControlsHotkey,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var message = new TextBlock { Text = Hint, Classes = { "Caption" }, TextWrapping = TextWrapping.Wrap };
        using var recorder = new ShortcutRecorder(text => shortcut.Text = text);
        var dialog = new FAContentDialog
        {
            Title = "Quick Controls shortcut",
            Content = new StackPanel { Width = 320, Spacing = 12, Children = { message, shortcut } },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };

        // Subscribed on Opened so these run after DialogIntent's handlers, which cancel a click that was not a deliberate press.
        dialog.Opened += (_, _) =>
        {
            recorder.Start();
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!args.Cancel && !TrySave(shortcut.Text))
                {
                    message.Text = QuickControlsWindow.RegistrationFailed;
                    args.Cancel = true;
                }
            };
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                shortcut.Text = HotkeyGesture.Default;
            };
        };
        dialog.Closed += (_, _) => recorder.Dispose();
        await AppDialogs.ShowAsync(dialog, owner);
    }

    private bool TrySave(string? text)
    {
        if (HotkeyGesture.Parse(text) is not { } gesture || !_overlay.RegisterHotkey(gesture.ToString(), toastOnFailure: false))
        {
            return false;
        }

        Persist(s => s.QuickControlsHotkey = gesture.ToString());
        return true;
    }

    private sealed class ShortcutRecorder(Action<string> changed) : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private LowLevelKeyboardProc? _proc;
        private nint _hook;
        private bool _ctrl;
        private bool _alt;
        private bool _shift;
        private bool _win;

        private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

        public void Start()
        {
            if (_hook == 0)
            {
                _proc = HookProc;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            }
        }

        public void Dispose()
        {
            if (_hook != 0)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }

        private nint HookProc(int code, nint wParam, nint lParam)
        {
            if (code < 0)
            {
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            var down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (!down && wParam is not (WM_KEYUP or WM_SYSKEYUP))
            {
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            var vk = (uint)Marshal.ReadInt32(lParam);
            switch (vk)
            {
                case 0xA2 or 0xA3: _ctrl = down; return 1;
                case 0xA4 or 0xA5: _alt = down; return 1;
                case 0xA0 or 0xA1: _shift = down; return 1;
                case 0x5B or 0x5C: _win = down; return 1;
            }

            if (HotkeyGesture.KeyName(vk) is null)
            {
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            if (down)
            {
                changed(new HotkeyGesture(_ctrl, _alt, _shift, _win, vk).ToString());
            }

            return 1;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, nint module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string? moduleName);
    }
}
