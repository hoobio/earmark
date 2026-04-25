using Earmark.App.ViewModels;
using Earmark.Core.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class RulesPage : Page
{
    private readonly ILogger<RulesPage>? _logger;
    private bool _editorOpen;

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<RulesPage>>();
    }

    public RulesViewModel ViewModel { get; }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("Add rule clicked");
        await OpenEditorAsync(null);
    }

    private async void OnRuleClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RoutingRule rule)
        {
            _logger?.LogInformation("Rule clicked: {Id}", rule.Id);
            await OpenEditorAsync(rule);
        }
    }

    private async Task OpenEditorAsync(RoutingRule? rule)
    {
        if (_editorOpen)
        {
            _logger?.LogWarning("OpenEditorAsync ignored: dialog already open");
            return;
        }

        _editorOpen = true;
        try
        {
            var editor = App.Current.Services.GetRequiredService<RuleEditorViewModel>();
            editor.Load(rule);

            var dialog = new RuleEditorDialog
            {
                XamlRoot = XamlRoot,
                Editor = editor,
            };

            var result = await dialog.ShowAsync();
            _logger?.LogInformation("Editor dialog closed: {Result}", result);
        }
        finally
        {
            _editorOpen = false;
        }
    }
}
