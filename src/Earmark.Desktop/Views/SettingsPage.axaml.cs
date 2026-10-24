using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _viewModel;
    private readonly TextBox _search;
    private readonly List<IDisposable> _filterHides = [];
    private readonly HashSet<Control> _filtered = [];
    private MainWindow? _window;
    private AboutViewModel _about => _viewModel.About;

    public SettingsPage(SettingsViewModel viewModel, IServiceProvider services)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        QuickControlsSection.Content = services.GetKeyedService<Control>("QuickControlsSettings");
        QuickControlsSection.IsVisible = QuickControlsSection.Content is not null;
        _search = (TextBox)Resources["SearchBox"]!;
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    public void RevealSection(string key)
    {
        _search.Text = string.Empty;
        Dispatcher.UIThread.Post(
            () => this.FindControl<Control>($"{key}Section")?.BringIntoView(),
            DispatcherPriority.Background);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            _window = window;
            window.TitleBarContent = _search;
            window.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        }

        ApplyFilter();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is { } window)
        {
            window.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            if (window.TitleBarContent == _search)
            {
                window.TitleBarContent = null;
            }

            _window = null;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text))
        {
            _search.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void ApplyFilter()
    {
        foreach (var hide in _filterHides)
        {
            hide.Dispose();
        }

        _filterHides.Clear();
        _filtered.Clear();
        var query = _search.Text?.Trim() ?? string.Empty;
        NoMatches.IsVisible = false;
        if (query.Length == 0)
        {
            return;
        }

        var groups = Sections.GetVisualDescendants().OfType<StackPanel>().Where(panel => panel.Classes.Contains("SettingsGroup")).ToList();
        var shown = new HashSet<StackPanel>();
        foreach (var group in groups)
        {
            foreach (var row in group.Children)
            {
                if ((row as SettingsCard ?? (row as ContentPresenter)?.Child as SettingsCard) is not { } card)
                {
                    continue;
                }

                if (!Matches(card.Header, query) && !Matches(card.Description, query))
                {
                    Hide(row);
                }
                else if (row.IsVisible && card.IsVisible)
                {
                    shown.Add(group);
                }
            }

            if (!shown.Contains(group))
            {
                var block = group.GetVisualParent() is ItemsPresenter ? group.FindAncestorOfType<ItemsControl>() ?? (Control)group : group;
                Hide(block);
                if (block.GetVisualParent() is Panel parent && parent.Children.IndexOf(block) is > 0 and var index
                    && parent.Children[index - 1] is TextBlock caption && caption.Classes.Contains("Caption"))
                {
                    Hide(caption);
                }
            }
        }

        foreach (var section in Sections.Children)
        {
            if (groups.Any(group => section != group && section.IsVisualAncestorOf(group)) && !shown.Any(section.IsVisualAncestorOf))
            {
                Hide(section);
            }
        }

        NoMatches.Text = $"No settings match '{query}'";
        NoMatches.IsVisible = shown.Count == 0;
    }

    private static bool Matches(object? text, string query) => text is string value && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void Hide(Control control)
    {
        if (_filtered.Add(control) && control.SetValue(IsVisibleProperty, false, BindingPriority.Animation) is { } hide)
        {
            _filterHides.Add(hide);
        }
    }

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
