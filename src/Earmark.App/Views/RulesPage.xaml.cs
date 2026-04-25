using Earmark.App.ViewModels;
using Earmark.Core.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Earmark.App.Views;

public sealed partial class RulesPage : Page
{
    private readonly ILogger<RulesPage>? _logger;

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<RulesPage>>();
    }

    public RulesViewModel ViewModel { get; }

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row })
        {
            row.IsExpanded = !row.IsExpanded;
            _logger?.LogInformation("Rule {Id} expanded={Expanded}", row.Id, row.IsExpanded);
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRow row })
        {
            await ViewModel.DeleteCommand.ExecuteAsync(row);
        }
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is RuleRow row &&
            combo.SelectedItem is RuleTypeOption option &&
            row.Type != option.Value)
        {
            row.Type = option.Value;
        }
    }
}
