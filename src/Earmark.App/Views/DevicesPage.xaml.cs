using Earmark.App.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage(DevicesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }
}
