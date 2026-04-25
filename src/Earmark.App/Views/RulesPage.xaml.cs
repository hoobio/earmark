using Earmark.App.ViewModels;
using Earmark.Core.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class RulesPage : Page
{
    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public RulesViewModel ViewModel { get; }

    private async void OnAddClicked(object sender, RoutedEventArgs e) => await OpenEditorAsync(null);

    private async void OnRuleClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RoutingRule rule)
        {
            await OpenEditorAsync(rule);
        }
    }

    private async Task OpenEditorAsync(RoutingRule? rule)
    {
        var editor = App.Current.Services.GetRequiredService<RuleEditorViewModel>();
        editor.Load(rule);

        var dialog = new RuleEditorDialog
        {
            XamlRoot = XamlRoot,
            Editor = editor,
        };

        await dialog.ShowAsync();
    }
}
