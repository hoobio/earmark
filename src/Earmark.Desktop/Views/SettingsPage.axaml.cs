using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _viewModel;
    private AboutViewModel _about => _viewModel.About;

    public SettingsPage(SettingsViewModel viewModel, IServiceProvider services)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        QuickControlsSection.Content = services.GetKeyedService<Control>("QuickControlsSettings");
        QuickControlsSection.IsVisible = QuickControlsSection.Content is not null;
    }

    public void RevealSection(string key) => Dispatcher.UIThread.Post(
        () => this.FindControl<Control>($"{key}Section")?.BringIntoView(),
        DispatcherPriority.Background);

    private async void OnManageHiddenAppsClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.LoadHiddenApps();
        var dialog = (FAContentDialog)Resources["HiddenAppsDialog"]!;
        dialog.DataContext = _viewModel;
        if (await AppDialogs.ShowAsync(dialog, TopLevel.GetTopLevel(this)!) == FAContentDialogResult.Secondary)
        {
            _viewModel.UnhideAll();
        }
    }

    private void OnUnhideClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HiddenAppRow row)
        {
            _viewModel.Unhide(row);
        }
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e) => _viewModel.PeakMeterSingleColour = (string)((Control)sender!).Tag!;

    private void OnResetSwatchClick(object? sender, RoutedEventArgs e) => _viewModel.PeakMeterSingleColour = SettingsViewModel.DefaultMeterColour;

    private void OnClearOverridesClick(object? sender, RoutedEventArgs e) => ((OverrideRow)((Control)sender!).DataContext!).Clear();

    private async void OnResetDevicesClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await AppDialogs.ShowMessageAsync(
            TopLevel.GetTopLevel(this)!,
            "Reset Devices page?",
            "This restores the default device groups, order and visibility on the Devices page, and unhides any hidden app chips. Your rules and other settings do not change.",
            "Reset",
            "Cancel");
        if (confirmed)
        {
            await _viewModel.ResetDeviceLayoutAsync();
        }
    }

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) => _about.OpenLogs();

    private void OnReportBugClick(object? sender, RoutedEventArgs e) => _about.ReportBug();

    private void OnRequestFeatureClick(object? sender, RoutedEventArgs e) => _about.RequestFeature();

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => _about.OpenGitHub();

    private void OnDonateClick(object? sender, RoutedEventArgs e) => _about.Donate();

    private void OnViewReleaseClick(object? sender, RoutedEventArgs e) => _about.OpenLatestRelease();

    private async void OnCheckNowClick(object? sender, RoutedEventArgs e) => await _about.CheckAsync();
}
