using System.Collections.Generic;

using Earmark.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace Earmark.App.Views;

public sealed partial class SettingsPage : Page
{
    // Current default green first, then a distinct spread of Windows/Fluent accent hues (one each
    // of teal, blue, purple, magenta, red, orange, gold) for quick picks above the colour wheel.
    private static readonly Color[] FluentSwatches =
    {
        Color.FromArgb(0xFF, 0x00, 0xB7, 0xC3), // teal
        Color.FromArgb(0xFF, 0x00, 0x78, 0xD4), // blue
        Color.FromArgb(0xFF, 0x87, 0x64, 0xB8), // purple
        Color.FromArgb(0xFF, 0xE3, 0x00, 0x8C), // magenta
        Color.FromArgb(0xFF, 0xE8, 0x11, 0x23), // red
        Color.FromArgb(0xFF, 0xF7, 0x63, 0x0C), // orange
        Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00), // gold
    };

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        MeterSwatches = BuildMeterSwatches();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Quick-pick colours bound by the swatch strip in the bar-colour flyout.</summary>
    public IReadOnlyList<Color> MeterSwatches { get; }

    private static List<Color> BuildMeterSwatches()
    {
        var colours = new List<Color> { PeakMeterOptions.DefaultColour };
        colours.AddRange(FluentSwatches);
        return colours;
    }

    private void OnMeterSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Color colour })
        {
            ViewModel.PeakMeterSingleColour = colour;
        }
    }

    private void OnResetMeterColour(object sender, RoutedEventArgs e) => ViewModel.ResetPeakMeterColour();
}
