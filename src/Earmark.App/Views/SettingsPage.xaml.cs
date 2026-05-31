using System.Collections.Generic;

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
}
