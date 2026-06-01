using System.ComponentModel;

using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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

    // ---- Drag-and-drop with a live phantom slot ----
    //
    // Matching the Devices page: picking an item up collapses its slot (so the list closes around
    // it) and a make-space gap opens at the hover index, with every realised row gliding via a
    // Composition Offset implicit. Applies to the rule list and to the condition / action lists.
    // Items use manual drag (not CanReorderItems) so a condition can only land on a condition list
    // and an action only on an action list (Actions or Otherwise), and so a row can move to another
    // rule. A drop commits immediately (RuleRow.Accept* / Items.Move -> ReorderAsync).

    private enum ItemDragKind { Condition, Action }

    private sealed record ItemDragContext(RuleRow SourceRule, object Row, ItemDragKind Kind, bool FromElse);

    private ItemDragContext? _itemDrag;
    private bool _ruleDragging;

    // Shared gap state (one drag at a time): the source list + its collapsed container, the gap
    // height, and the container currently carrying the make-space margin.
    private ListViewBase? _dragSourceList;
    private int _dragSourceIndex = -1;
    private ListViewItem? _dragSourceContainer;
    private double _dragGapHeight;
    private ListViewItem? _gapContainer;
    // The gap container's own margin before we widened it, so clearing the gap restores the
    // ItemContainerStyle baseline (the rule list uses Margin="0,4" for inter-card spacing) instead
    // of flattening it to zero.
    private Thickness _gapOriginalMargin;

    // ---- Rule (outer list) drag ----

    private void OnRulesDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Collapse the dragged rule(s) so the drag is a compact header, not the full editor. Done
        // synchronously so the list reflows around a small item during the drag.
        _collapsedForDrag.Clear();
        foreach (var item in e.Items)
        {
            if (item is RuleRow row && row.IsExpanded)
            {
                row.IsExpanded = false;
                _collapsedForDrag.Add(row);
            }
        }

        _ruleDragging = true;
        if (e.Items.Count > 0)
        {
            BeginContainerDrag(RulesList, RulesList.Items.IndexOf(e.Items[0]));
        }
    }

    private void OnRulesDragOver(object sender, DragEventArgs e)
    {
        if (!_ruleDragging)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        ShowGap(RulesList, GetDropIndex(RulesList, e));
    }

    private void OnRulesDrop(object sender, DragEventArgs e)
    {
        if (!_ruleDragging) return;
        e.Handled = true;
        var gapIdx = GetDropIndex(RulesList, e);
        var src = _dragSourceIndex;
        ResetDragVisuals();
        if (src < 0 || src >= ViewModel.Items.Count) return;
        var to = Math.Clamp(src < gapIdx ? gapIdx - 1 : gapIdx, 0, ViewModel.Items.Count - 1);
        if (to != src)
        {
            // Move in the bound collection -> OnItemsCollectionChanged(Move) -> ReorderAsync persists.
            ViewModel.Items.Move(src, to);
        }
    }

    private void OnRulesDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Re-expand whatever we collapsed for the drag (the RuleRow instances survive the reorder,
        // so this restores the user's open editor). Fires on drop or cancel.
        foreach (var row in _collapsedForDrag)
        {
            row.IsExpanded = true;
        }
        _collapsedForDrag.Clear();
        ResetDragVisuals();
        _ruleDragging = false;
    }

    // ---- Condition / action (inner list) drag ----

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
        BeginContainerDrag(list, list.Items.IndexOf(row));
    }

    private void OnConditionDragOver(object sender, DragEventArgs e)
    {
        // Type enforcement: only a condition may land here.
        if (_itemDrag?.Kind != ItemDragKind.Condition || sender is not ListView list)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        ShowGap(list, GetDropIndex(list, e));
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
        ResetDragVisuals();
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
        BeginContainerDrag(list, list.Items.IndexOf(row));
    }

    private void OnActionDragOver(object sender, DragEventArgs e)
    {
        // Type enforcement: only an action may land here (either Actions or Otherwise).
        if (_itemDrag?.Kind != ItemDragKind.Action || sender is not ListView list)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        ShowGap(list, GetDropIndex(list, e));
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
        ResetDragVisuals();
        try
        {
            await target.AcceptActionAsync((ActionRow)ctx.Row, ctx.SourceRule, ctx.FromElse, toElse, index);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Action drop failed");
        }
    }

    private void OnItemDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Fires on the source list on drop or cancel - restore the collapsed slot and clear the gap
        // (the Drop handler already did this on a successful drop; this covers a cancelled drag).
        _itemDrag = null;
        ResetDragVisuals();
    }

    // ---- Title / empty-list drop hotspots (insert at index 0) ----
    //
    // A section title ("Conditions" / "Actions" / "Otherwise") and the empty-list placeholder are
    // drop targets that land the row at the top of that section's list. The placeholder is the only
    // target when a rule has none of that kind yet; the title gives a clear "drop at the top" zone
    // otherwise. The element's Tag ("Condition" / "Action") gates the type; the sibling ListView in
    // the same section is the target.

    private void OnSectionHeaderDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement header || !HeaderAcceptsDrag(header))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        if (FindSectionList(header) is ListView list)
        {
            ShowGap(list, 0);
        }
    }

    private async void OnSectionHeaderDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement header || _itemDrag is not { } ctx ||
            !HeaderAcceptsDrag(header) ||
            FindSectionList(header) is not ListView list ||
            FindAncestorRuleRow(header) is not RuleRow target)
        {
            return;
        }

        e.Handled = true;
        _itemDrag = null;
        ResetDragVisuals();
        try
        {
            if (ctx.Kind == ItemDragKind.Condition)
            {
                await target.AcceptConditionAsync((ConditionRow)ctx.Row, ctx.SourceRule, 0);
            }
            else
            {
                await target.AcceptActionAsync((ActionRow)ctx.Row, ctx.SourceRule, ctx.FromElse, (string?)list.Tag == "Else", 0);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Header drop failed");
        }
    }

    private bool HeaderAcceptsDrag(FrameworkElement header) => (string?)header.Tag switch
    {
        "Condition" => _itemDrag?.Kind == ItemDragKind.Condition,
        "Action" => _itemDrag?.Kind == ItemDragKind.Action,
        _ => false,
    };

    private static ListView? FindSectionList(FrameworkElement header) =>
        VisualTreeHelper.GetParent(header) is DependencyObject section ? FindDescendant<ListView>(section) : null;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnRulesHeaderDragOver(object sender, DragEventArgs e)
    {
        if (!_ruleDragging)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        ShowGap(RulesList, 0);
    }

    private void OnRulesHeaderDrop(object sender, DragEventArgs e)
    {
        if (!_ruleDragging) return;
        e.Handled = true;
        var src = _dragSourceIndex;
        ResetDragVisuals();
        if (src < 0 || src >= ViewModel.Items.Count) return;
        if (src != 0) ViewModel.Items.Move(src, 0);
    }

    // ---- Phantom-slot gap + glide ----

    /// <summary>Picks an item up: remember the source list/index, capture the slot height, and (once
    /// the drag's own visual is captured) collapse the source container so the list closes around
    /// it. The realised rows have the Offset implicit, so they glide into the gap.</summary>
    private void BeginContainerDrag(ListViewBase list, int index)
    {
        _dragSourceList = list;
        _dragSourceIndex = index;
        EnsureImplicits(list);
        // Defer the collapse one tick so the framework captures the drag image from the live
        // container first; collapsing then closes the slot with a glide rather than a blank image.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (index < 0 || _dragSourceIndex != index) return;
            if (list.ContainerFromIndex(index) is not ListViewItem c) return;
            _dragSourceContainer = c;
            _dragGapHeight = c.ActualHeight > 0 ? c.ActualHeight : 64;
            c.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Opens a make-space gap (height = the picked-up slot) at the hover index by adding a
    /// margin to the container there; the layout reflow glides via the Offset implicit. Skips the
    /// gap at the source's own resting position (a no-op drop).</summary>
    private void ShowGap(ListViewBase list, int gapIndex)
    {
        EnsureImplicits(list);
        ClearGapMargin();

        var count = list.Items.Count;
        var sameList = ReferenceEquals(list, _dragSourceList);
        if (sameList && (gapIndex == _dragSourceIndex || gapIndex == _dragSourceIndex + 1))
        {
            return; // dropping back where it started - no gap
        }
        if (count == 0)
        {
            return; // empty list: the drop still inserts at 0, there's just no row to push aside
        }

        var bottom = gapIndex >= count;
        if ((bottom ? list.ContainerFromIndex(count - 1) : list.ContainerFromIndex(gapIndex)) is not ListViewItem target)
        {
            return;
        }

        // Widen the existing margin rather than replacing it, so clearing restores the container's
        // ItemContainerStyle baseline (the rule list spaces cards with Margin="0,4").
        _gapContainer = target;
        _gapOriginalMargin = target.Margin;
        target.Margin = bottom
            ? new Thickness(_gapOriginalMargin.Left, _gapOriginalMargin.Top, _gapOriginalMargin.Right, _gapOriginalMargin.Bottom + _dragGapHeight)
            : new Thickness(_gapOriginalMargin.Left, _gapOriginalMargin.Top + _dragGapHeight, _gapOriginalMargin.Right, _gapOriginalMargin.Bottom);
    }

    private void ClearGapMargin()
    {
        if (_gapContainer is not null)
        {
            _gapContainer.Margin = _gapOriginalMargin;
            _gapContainer = null;
        }
    }

    /// <summary>Drops the gap and un-collapses the source container. Called before the collection
    /// mutates on drop, and again on drag-complete to cover a cancel.</summary>
    private void ResetDragVisuals()
    {
        ClearGapMargin();
        if (_dragSourceContainer is not null)
        {
            // Only its Visibility was changed (collapsed); leave Margin alone so the baseline survives.
            _dragSourceContainer.Visibility = Visibility.Visible;
            _dragSourceContainer = null;
        }
        _dragSourceList = null;
        _dragSourceIndex = -1;
    }

    /// <summary>Attaches the Offset implicit to a container once it has been placed (deferred so the
    /// first arrange or a scroll-recycle reuse snaps into place); every later layout move glides.
    /// Mirrors the Devices page's reorder animation.</summary>
    private void OnItemContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        var el = args.ItemContainer;
        ApplyReorderAnimation(el, false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyReorderAnimation(el, true));
    }

    private static void EnsureImplicits(ListViewBase list)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is UIElement el)
            {
                ApplyReorderAnimation(el, true);
            }
        }
    }

    private static void ApplyReorderAnimation(UIElement element, bool enable)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!enable)
        {
            visual.ImplicitAnimations = null;
            return;
        }

        var compositor = visual.Compositor;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
        offset.Duration = TimeSpan.FromMilliseconds(220);

        var animations = compositor.CreateImplicitAnimationCollection();
        animations["Offset"] = offset;
        visual.ImplicitAnimations = animations;
    }

    /// <summary>The insertion index (0..Count) for a pointer drop, by walking the realised
    /// containers and finding the first whose vertical midpoint sits below the pointer. A collapsed
    /// (picked-up) container has zero height, so it is skipped naturally.</summary>
    private static int GetDropIndex(ListViewBase list, DragEventArgs e)
    {
        var y = e.GetPosition(list).Y;
        var count = list.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not ListViewItem container || container.Visibility != Visibility.Visible)
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
