using System.ComponentModel;

using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Earmark.App.Views;

public sealed partial class RulesPage : Page
{
    private readonly ILogger<RulesPage>? _logger;

    // Rules collapsed for the duration of a reorder drag, restored when it completes - so you
    // drag a compact header, not the whole expanded editor.
    private readonly List<RuleRow> _collapsedForDrag = new();

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<RulesPage>>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => TryFocusPendingRule();
    }

    public RulesViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RulesViewModel.PendingFocusRuleId))
        {
            TryFocusPendingRule();
        }
    }

    private void TryFocusPendingRule()
    {
        if (ViewModel.PendingFocusRuleId is not Guid id) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var row = ViewModel.Items.FirstOrDefault(r => r.Id == id);
            if (row is null) return;
            row.IsExpanded = true;
            try
            {
                RulesList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
                // The list might not be ready yet (initial nav). Loaded will retry.
            }
            ViewModel.PendingFocusRuleId = null;
        });
    }

    private void OnRulesDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Collapse the dragged rule(s) so the drag is a compact header, not the full editor.
        // Done synchronously here so the list reflows around a small item during the drag.
        _collapsedForDrag.Clear();
        foreach (var item in e.Items)
        {
            if (item is RuleRow row && row.IsExpanded)
            {
                row.IsExpanded = false;
                _collapsedForDrag.Add(row);
            }
        }
    }

    private void OnRulesDragCompleted(Microsoft.UI.Xaml.Controls.ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Re-expand whatever we collapsed for the drag (the RuleRow instances survive the
        // reorder, so this restores the user's open editor). Fires on drop or cancel.
        foreach (var row in _collapsedForDrag)
        {
            row.IsExpanded = true;
        }
        _collapsedForDrag.Clear();
    }

    private async void OnDeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRow row })
        {
            await ViewModel.DeleteCommand.ExecuteAsync(row);
        }
    }

    private async void OnDuplicateRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRow row })
        {
            await ViewModel.DuplicateCommand.ExecuteAsync(row);
        }
    }

    private void OnActionKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ActionRow action &&
            combo.SelectedItem is ActionKindOption option &&
            action.Kind != option.Value)
        {
            action.Kind = option.Value;
        }
    }

    private void OnActionMembershipChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ActionRow action &&
            combo.SelectedItem is MixMembershipOption option &&
            action.Membership != option.Value)
        {
            action.Membership = option.Value;
        }
    }

    private void OnConditionKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            combo.DataContext is ConditionRow condition &&
            combo.SelectedItem is ConditionKindOption option &&
            condition.Kind != option.Value)
        {
            condition.Kind = option.Value;
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
            // The action template is shared between the main and "otherwise" lists; route the
            // remove to whichever list this row belongs to.
            if (rule.ElseActions.Contains(row))
            {
                rule.RemoveElseActionCommand.Execute(row);
            }
            else
            {
                rule.RemoveActionCommand.Execute(row);
            }
        }
    }

    private void OnDuplicateActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ActionRow row } fe &&
            FindAncestorRuleRow(fe) is RuleRow rule)
        {
            rule.DuplicateActionCommand.Execute(row);
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

    private void OnDuplicateConditionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConditionRow row } fe &&
            FindAncestorRuleRow(fe) is RuleRow rule)
        {
            rule.DuplicateConditionCommand.Execute(row);
        }
    }

    // ---- Drag-and-drop of conditions / actions ----
    //
    // Each inner list is its own drag source AND drop target. A condition may only land on a
    // condition list, an action only on an action list (Actions or Otherwise) - the DragOver
    // handler rejects a mismatch with a no-drop cursor. Moves persist immediately (RuleRow.Accept*),
    // cross-rule moves rebuild the row wired to the target rule. The dragged object travels in this
    // page field (an in-process drag); the DataPackage text only carries a kind tag so cross-list
    // drops are accepted.

    private enum ItemDragKind { Condition, Action }

    private sealed record ItemDragContext(RuleRow SourceRule, object Row, ItemDragKind Kind, bool FromElse);

    private ItemDragContext? _itemDrag;

    private void OnConditionDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView list ||
            e.Items.Count == 0 || e.Items[0] is not ConditionRow row ||
            FindAncestorRuleRow(list) is not RuleRow rule)
        {
            e.Cancel = true;
            return;
        }

        _itemDrag = new ItemDragContext(rule, row, ItemDragKind.Condition, FromElse: false);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText("earmark:condition");
    }

    private void OnConditionDragOver(object sender, DragEventArgs e)
    {
        // Type enforcement: only a condition may land here.
        e.AcceptedOperation = _itemDrag?.Kind == ItemDragKind.Condition
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnConditionDrop(object sender, DragEventArgs e)
    {
        if (_itemDrag is not { Kind: ItemDragKind.Condition } ctx ||
            sender is not ListView list ||
            FindAncestorRuleRow(list) is not RuleRow target)
        {
            return;
        }

        e.Handled = true;
        var index = GetDropIndex(list, e);
        _itemDrag = null;
        try
        {
            await target.AcceptConditionAsync((ConditionRow)ctx.Row, ctx.SourceRule, index);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Condition drop failed");
        }
    }

    private void OnActionDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView list ||
            e.Items.Count == 0 || e.Items[0] is not ActionRow row ||
            FindAncestorRuleRow(list) is not RuleRow rule)
        {
            e.Cancel = true;
            return;
        }

        var fromElse = (string?)list.Tag == "Else";
        _itemDrag = new ItemDragContext(rule, row, ItemDragKind.Action, fromElse);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText("earmark:action");
    }

    private void OnActionDragOver(object sender, DragEventArgs e)
    {
        // Type enforcement: only an action may land here (either Actions or Otherwise).
        e.AcceptedOperation = _itemDrag?.Kind == ItemDragKind.Action
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnActionDrop(object sender, DragEventArgs e)
    {
        if (_itemDrag is not { Kind: ItemDragKind.Action } ctx ||
            sender is not ListView list ||
            FindAncestorRuleRow(list) is not RuleRow target)
        {
            return;
        }

        e.Handled = true;
        var toElse = (string?)list.Tag == "Else";
        var index = GetDropIndex(list, e);
        _itemDrag = null;
        try
        {
            await target.AcceptActionAsync((ActionRow)ctx.Row, ctx.SourceRule, ctx.FromElse, toElse, index);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Action drop failed");
        }
    }

    /// <summary>The insertion index (0..Count) for a pointer drop, by walking the realised
    /// containers and finding the first whose vertical midpoint sits below the pointer.</summary>
    private static int GetDropIndex(ListView list, DragEventArgs e)
    {
        var y = e.GetPosition(list).Y;
        var count = list.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(list)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Top + (bounds.Height / 2) > y)
            {
                return i;
            }
        }
        return count;
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
