using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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

    private async void OnDeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRow row })
        {
            await ViewModel.DeleteCommand.ExecuteAsync(row);
        }
    }

    private void OnActionTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ActionRow action &&
            combo.SelectedItem is ActionTypeOption option &&
            action.Type != option.Value)
        {
            action.Type = option.Value;
        }
    }

    private void OnConditionTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ConditionRow condition &&
            combo.SelectedItem is ConditionTypeOption option &&
            condition.Type != option.Value)
        {
            condition.Type = option.Value;
        }
    }

    private void OnConditionFlowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ConditionRow condition &&
            combo.SelectedItem is ConditionFlowOption option &&
            condition.Flow != option.Value)
        {
            condition.Flow = option.Value;
        }
    }

    private void OnRemoveActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ActionRow row &&
            FindAncestorRuleRow(fe) is RuleRow rule)
        {
            rule.RemoveActionCommand.Execute(row);
        }
    }

    private void OnRemoveConditionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConditionRow row &&
            FindAncestorRuleRow(fe) is RuleRow rule)
        {
            rule.RemoveConditionCommand.Execute(row);
        }
    }

    private static RuleRow? FindAncestorRuleRow(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is RuleRow rule)
            {
                return rule;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // CA1822 suppressed: XAML event hookup requires instance methods even when the body
    // doesn't touch instance state.
#pragma warning disable CA1822
    private void OnDevicePatternTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (sender.DataContext is not ActionRow row) return;
        sender.ItemsSource = FilterCandidates(row.DeviceCandidates, sender.Text);
    }

    private void OnDevicePatternGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box && box.DataContext is ActionRow row)
        {
            box.ItemsSource = FilterCandidates(row.DeviceCandidates, box.Text);
        }
    }

    private void OnDeviceSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string name)
        {
            // Insert the literal name. PatternMatcher.Matches treats an exact-name pattern
            // as a string equality match without compiling, so no regex escaping needed.
            sender.Text = name;
        }
    }

    private void OnMixPatternTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (sender.DataContext is not ActionRow row) return;
        sender.ItemsSource = FilterCandidates(row.MixCandidates, sender.Text);
    }

    private void OnMixPatternGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box && box.DataContext is ActionRow row)
        {
            box.ItemsSource = FilterCandidates(row.MixCandidates, box.Text);
        }
    }

    private void OnMixSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string name)
        {
            sender.Text = name;
        }
    }
#pragma warning restore CA1822

    private static List<string> FilterCandidates(IReadOnlyList<string> candidates, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return candidates.Take(20).ToList();
        }

        var matches = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(candidate);
                if (matches.Count >= 20) break;
            }
        }
        return matches;
    }

}
