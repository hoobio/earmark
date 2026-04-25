using Earmark.App.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class SessionsPage : Page
{
    public SessionsPage(SessionsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SessionsViewModel ViewModel { get; }
}
