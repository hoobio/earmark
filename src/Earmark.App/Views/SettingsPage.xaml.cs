using System.Collections.Generic;
using System.Runtime.InteropServices;

using Earmark.App.Services;
using Earmark.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace Earmark.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _syncingMeterColour;

    public SettingsPage(SettingsViewModel viewModel, AboutViewModel about)
    {
        ViewModel = viewModel;
        About = about;
        InitializeComponent();

        // Seed the shared colour picker from the VM and keep the two in sync. The picker's
        // SelectedColour is nullable; the peak-meter colour never goes null (no Auto), so the
        // guard below only forwards real colours.
        MeterColourPicker.SelectedColour = ViewModel.PeakMeterSingleColour;
        MeterColourPicker.RegisterPropertyChangedCallback(
            Controls.ColourSwatchPicker.SelectedColourProperty, OnMeterPickerColourChanged);
        ViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;

        // The hidden-apps count can change from the Devices page while this singleton page is
        // off-screen, so refresh its card description each time the page is shown.
        Loaded += (_, _) => ViewModel.RefreshHiddenAppsState();
    }

    public SettingsViewModel ViewModel { get; }

    public AboutViewModel About { get; }

    private void OnMeterPickerColourChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_syncingMeterColour || MeterColourPicker.SelectedColour is not Color colour) return;
        ViewModel.PeakMeterSingleColour = colour;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.PeakMeterSingleColour)) return;
        _syncingMeterColour = true;
        MeterColourPicker.SelectedColour = ViewModel.PeakMeterSingleColour;
        _syncingMeterColour = false;
    }

    private void OnResetMeterColour(object sender, RoutedEventArgs e) => ViewModel.ResetPeakMeterColour();

    private async void OnResetDeviceLayout(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset Devices page?",
            Content = "This restores the default device groups, order, and visibility on the Devices page, "
                + "and un-hides any hidden app chips. Your rules and other settings aren't changed.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ResetDeviceLayoutAsync();
        }
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await About.CheckForUpdatesAsync();

    private void OnOpenLatestRelease(object sender, RoutedEventArgs e) => About.OpenLatestRelease();

    private void OnReportBug(object sender, RoutedEventArgs e) => About.ReportBug();

    private void OnRequestFeature(object sender, RoutedEventArgs e) => About.RequestFeature();

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => About.OpenGitHub();

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e) => About.OpenLogsFolder();

    private async void OnManageHiddenApps(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadHiddenApps();
        HiddenAppsDialog.XamlRoot = XamlRoot;
        await HiddenAppsDialog.ShowAsync();
    }

    private void OnUnhideApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HiddenAppRow row })
        {
            ViewModel.UnhideApp(row);
        }
    }

    private void OnClearAllHiddenApps(object sender, RoutedEventArgs e) => ViewModel.ClearAllHiddenApps();

    private async void OnChangeQuickControlsShortcut(object sender, RoutedEventArgs e)
    {
        var shortcutText = new TextBlock
        {
            Text = ViewModel.QuickControlsHotkey,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var errorText = new TextBlock
        {
            Text = "Press a shortcut with Ctrl, Alt, or Win plus one non-modifier key.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var root = new StackPanel { Width = 320, Spacing = 12 };
        root.Children.Add(errorText);
        root.Children.Add(shortcutText);

        using var recorder = new ShortcutRecorder(text => shortcutText.Text = text);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Quick Controls shortcut",
            Content = root,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.Opened += (_, _) => recorder.Start();
        dialog.Closed += (_, _) => recorder.Dispose();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var value = recorder.CurrentText ?? shortcutText.Text;
            if (!ViewModel.TrySetQuickControlsHotkey(value))
            {
                errorText.Text = "That shortcut couldn't be registered. Try another combination.";
                args.Cancel = true;
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            shortcutText.Text = "Win+Alt+V";
            recorder.SetCurrent("Win+Alt+V");
        };

        await dialog.ShowAsync();
    }

    private sealed class ShortcutRecorder : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private readonly Action<string> _onChanged;
        private LowLevelKeyboardProc? _proc;
        private nint _hook;
        private bool _ctrl;
        private bool _alt;
        private bool _shift;
        private bool _win;

        public ShortcutRecorder(Action<string> onChanged)
        {
            _onChanged = onChanged;
        }

        public string? CurrentText { get; private set; }

        public void Start()
        {
            if (_hook != 0) return;
            _proc = HookProc;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        public void SetCurrent(string text)
        {
            CurrentText = text;
            _onChanged(text);
        }

        private nint HookProc(int code, nint wParam, nint lParam)
        {
            if (code >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = data.vkCode;
                switch (vk)
                {
                    case 0xA2 or 0xA3: _ctrl = true; return 1;
                    case 0xA4 or 0xA5: _alt = true; return 1;
                    case 0xA0 or 0xA1: _shift = true; return 1;
                    case 0x5B or 0x5C: _win = true; return 1;
                }

                var name = KeyName(vk);
                if (name is not null)
                {
                    var parts = new List<string>();
                    if (_ctrl) parts.Add("Ctrl");
                    if (_alt) parts.Add("Alt");
                    if (_shift) parts.Add("Shift");
                    if (_win) parts.Add("Win");
                    parts.Add(name);
                    SetCurrent(string.Join("+", parts));
                    return 1;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private static string? KeyName(uint vk)
        {
            if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
            if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
            if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
            return vk switch
            {
                0x20 => "Space",
                0x09 => "Tab",
                0x0D => "Enter",
                0x1B => "Escape",
                0x2E => "Delete",
                0x2D => "Insert",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x26 => "Up",
                0x28 => "Down",
                0x25 => "Left",
                0x27 => "Right",
                _ => null,
            };
        }

        public void Dispose()
        {
            if (_hook != 0)
            {
                UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }

        private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public nint dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string? lpModuleName);
    }
}
