using Earmark.App.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
