using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

using Earmark.Core.Settings;
using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels.Devices;
using Earmark.Desktop.Views;

using FluentAvalonia.UI.Controls;

namespace Earmark.Desktop.Controls;

public static class CustomiseDeviceDialog
{
    public static async Task ShowAsync(DeviceCardViewModel card, TopLevel owner)
    {
        var s = card.Owner.Settings;
        var cfg = DevicesViewModel.Clone(card.Config);
        var glyphKey = DeviceVisuals.StoredKeyOf(cfg.Glyph);
        var accent = cfg.AccentColour;

        var search = new TextBox { PlaceholderText = "Search glyphs" };
        var glyphs = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        var glyphButtons = new List<(ToggleButton Button, string? Key, string Label)>();
        void SyncGlyphs()
        {
            foreach (var (button, key, label) in glyphButtons)
            {
                button.IsChecked = key == glyphKey;
                button.IsVisible = string.IsNullOrWhiteSpace(search.Text) || label.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var (key, label, glyph) in new[] { ((string?)null, "Auto", card.AutoGlyph) }.Concat(DeviceVisuals.Catalog.Select(e => ((string?)e.Key, e.Label, e.Glyph))))
        {
            var button = new ToggleButton { Classes = { "GlyphPick" }, Content = new TextBlock { Classes = { "Glyph" }, Text = glyph } };
            ToolTip.SetTip(button, label);
            button.Click += (_, _) =>
            {
                glyphKey = key;
                SyncGlyphs();
            };
            glyphButtons.Add((button, key, label));
            glyphs.Children.Add(button);
        }

        search.TextChanged += (_, _) => SyncGlyphs();
        SyncGlyphs();

        var swatches = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        var swatchButtons = new List<(ToggleButton Button, string? Value)>();
        void SyncSwatches()
        {
            foreach (var (button, value) in swatchButtons)
            {
                button.IsChecked = string.Equals(value, accent, StringComparison.OrdinalIgnoreCase);
            }
        }

        void AddSwatch(string? value, IBrush fill, string tip, string? glyph = null)
        {
            var button = new ToggleButton { Classes = { "Swatch" }, Background = fill, Content = glyph is null ? null : new TextBlock { Classes = { "Glyph" }, Text = glyph } };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) =>
            {
                accent = value;
                SyncSwatches();
            };
            swatchButtons.Add((button, value));
            swatches.Children.Add(button);
        }

        AddSwatch(null, card.AutoAccent is { } auto ? new SolidColorBrush(auto) : Brushes.Transparent, "Auto", char.ConvertFromUtf32(0xE175));
        AddSwatch(DeviceVisuals.AccentNone, Brushes.Transparent, "None", char.ConvertFromUtf32(0xE6D3));
        foreach (var colour in DeviceVisuals.Swatches)
        {
            AddSwatch(DeviceVisuals.ToHex(colour), new SolidColorBrush(colour), DeviceVisuals.ToHex(colour));
        }

        SyncSwatches();

        var rows = new StackPanel { Classes = { "SettingsGroup" } };
        var apply = new List<Action>();
        ComboBox Choice(string header, string[] options, int selected, Action<int> commit)
        {
            var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected, MinWidth = 160 };
            rows.Children.Add(new SettingsCard { Header = header, Content = combo, MinHeight = 52 });
            apply.Add(() => commit(combo.SelectedIndex));
            return combo;
        }

        void TriState(string header, bool? value, bool global, Action<bool?> commit) =>
            Choice(header, [global ? "Use global (On)" : "Use global (Off)", "On", "Off"], value switch { null => 0, true => 1, false => 2 }, i => commit(i switch { 1 => true, 2 => false, _ => null }));

        Choice("Volume slider", ["Show", "Hide"], cfg.VolumeControlsHidden == true ? 1 : 0, i => cfg.VolumeControlsHidden = i == 1 ? true : null);
        TriState("Now playing strip", cfg.ShowNowPlaying, s.ShowNowPlaying, v => cfg.ShowNowPlaying = v);
        TriState("Fill card background", cfg.NowPlayingFill, s.NowPlayingCardBackground, v => cfg.NowPlayingFill = v);
        TriState("App chips", cfg.ShowAppIndicators, s.ShowAppIndicators, v => cfg.ShowAppIndicators = v);
        TriState("App metering bars", cfg.ShowAppMeters, s.ShowAppPeakMeters, v => cfg.ShowAppMeters = v);
        TriState("Level meter", cfg.MeterEnabled, s.PeakMeterColourMode != PeakMeterColourMode.Off, v => cfg.MeterEnabled = v);
        TriState("Peak indicator", cfg.ShowPeakIndicator, s.PeakMeterShowHold, v => cfg.ShowPeakIndicator = v);
        TriState("Rules section", cfg.ShowRules, s.ShowRules, v => cfg.ShowRules = v);
        TriState("Device badges", cfg.ShowDeviceBadges, s.ShowDeviceBadges, v => cfg.ShowDeviceBadges = v);

        var content = new StackPanel
        {
            Classes = { "CustomiseDialog" },
            Spacing = 12,
            Children =
            {
                new TextBlock { Classes = { "FieldHeader" }, Text = "Glyph" },
                search,
                glyphs,
                new TextBlock { Classes = { "FieldHeader" }, Text = "Accent colour" },
                swatches,
                new TextBlock { Classes = { "FieldHeader" }, Text = "Overrides" },
                rows,
            },
        };
        var dialog = new FAContentDialog
        {
            Title = $"Customise {card.Name}",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset to default",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };

        var result = await AppDialogs.ShowAsync(dialog, owner);
        if (result == FAContentDialogResult.Primary)
        {
            foreach (var commit in apply)
            {
                commit();
            }

            cfg.Glyph = glyphKey is null ? null : DeviceVisuals.GlyphPrefix + glyphKey;
            cfg.AccentColour = accent;
            card.Owner.UpdateConfig(card, cfg);
        }
        else if (result == FAContentDialogResult.Secondary)
        {
            var reset = DevicesViewModel.Clone(card.Config);
            (reset.Glyph, reset.AccentColour, reset.VolumeControlsHidden, reset.ShowNowPlaying, reset.NowPlayingFill, reset.ShowAppIndicators,
                reset.ShowAppMeters, reset.MeterEnabled, reset.ShowPeakIndicator, reset.ShowRules, reset.ShowDeviceBadges) =
                (null, null, null, null, null, null, null, null, null, null, null);
            card.Owner.UpdateConfig(card, reset);
        }
    }
}
