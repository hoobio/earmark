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

    public SettingsPage(SettingsViewModel viewModel, AboutViewModel about)
    {
        ViewModel = viewModel;
        About = about;
        MeterSwatches = BuildMeterSwatches();
        InitializeComponent();

        // The hidden-apps count can change from the Devices page while this singleton page is
        // off-screen, so refresh its card description each time the page is shown.
        Loaded += (_, _) => ViewModel.RefreshHiddenAppsState();
    }

    public SettingsViewModel ViewModel { get; }

    public AboutViewModel About { get; }

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

    private async void OnResetDeviceLayout(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset Devices page?",
            Content = "This restores the default device groups, order, and visibility on the Devices page. "
                + "Your rules and other settings aren't changed.",
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
}
