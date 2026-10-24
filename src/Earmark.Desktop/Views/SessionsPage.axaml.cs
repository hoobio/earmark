using Avalonia.Controls;
using Avalonia.Interactivity;

using Earmark.Desktop.ViewModels;

namespace Earmark.Desktop.Views;

public sealed partial class SessionsPage : UserControl
{
    private readonly SessionsViewModel _viewModel;

    public SessionsPage(SessionsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => _viewModel.Refresh();

    private async void OnReapplyClick(object? sender, RoutedEventArgs e) => await _viewModel.ReapplyAsync();
}
