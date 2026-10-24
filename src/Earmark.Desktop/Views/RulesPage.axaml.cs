using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Core.Routing;
using Earmark.Desktop.ViewModels.Rules;

namespace Earmark.Desktop.Views;

public sealed partial class RulesPage : UserControl
{
    private static readonly DataFormat<object> DragFormat = DataFormat.CreateInProcessFormat<object>("earmark-rules-drag");

    private object? _dragged;

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusRequested += row => Dispatcher.UIThread.Post(() => RulesList.ContainerFromItem(row)?.BringIntoView(), DispatcherPriority.Loaded);
        AttachedToVisualTree += (_, _) => viewModel.PageVisible = true;
        DetachedFromVisualTree += (_, _) => viewModel.PageVisible = false;

        AddHandler(PointerPressedEvent, OnPointerPressed);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropIndicator.IsVisible = false);
    }

    public RulesViewModel ViewModel { get; }

    private static T? Item<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

    private async void OnAddRule(object? sender, RoutedEventArgs e) => await ViewModel.AddAsync();

    private async void OnReapply(object? sender, RoutedEventArgs e) => await ViewModel.ReapplyAsync();

    private async void OnDuplicateRule(object? sender, RoutedEventArgs e)
    {
        if (Item<RuleRow>(sender) is { } row) await ViewModel.DuplicateAsync(row);
    }

    private async void OnDeleteRule(object? sender, RoutedEventArgs e)
    {
        if (Item<RuleRow>(sender) is { } row) await ViewModel.DeleteAsync(row);
    }

    private void OnToggleEnabled(object? sender, RoutedEventArgs e)
    {
        if (Item<RuleRow>(sender) is { } row) row.Enabled = !row.Enabled;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Item<RuleRow>(sender) is { } row) await row.SaveAsync();
    }

    private void OnDiscard(object? sender, RoutedEventArgs e) => Item<RuleRow>(sender)?.Discard();

    private void OnAddCondition(object? sender, RoutedEventArgs e) => Item<RuleRow>(sender)?.AddCondition();

    private void OnAddAction(object? sender, RoutedEventArgs e) => Item<RuleRow>(sender)?.AddAction(toElse: false);

    private void OnAddElseAction(object? sender, RoutedEventArgs e) => Item<RuleRow>(sender)?.AddAction(toElse: true);

    private void OnDuplicateCondition(object? sender, RoutedEventArgs e)
    {
        if (Item<ConditionRow>(sender) is { } row) row.Owner.Duplicate(row);
    }

    private void OnRemoveCondition(object? sender, RoutedEventArgs e)
    {
        if (Item<ConditionRow>(sender) is { } row) row.Owner.Remove(row);
    }

    private void OnDuplicateAction(object? sender, RoutedEventArgs e)
    {
        if (Item<ActionRow>(sender) is { } row) row.Owner.Duplicate(row);
    }

    private void OnRemoveAction(object? sender, RoutedEventArgs e)
    {
        if (Item<ActionRow>(sender) is { } row) row.Owner.Remove(row);
    }

    private void OnRuleHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (Item<RuleRow>(sender) is { } row && (e.Source as Visual)?.FindAncestorOfType<ToggleSwitch>(includeSelf: true) is null)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    private void OnPickerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        box.ItemFilter ??= (search, item) => string.IsNullOrEmpty(search)
            || (item is string name && name.Contains(search, StringComparison.OrdinalIgnoreCase))
            || (box.ItemsSource is IEnumerable<string> all && all.Contains(search, StringComparer.OrdinalIgnoreCase));
        box.IsDropDownOpen = true;
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBlock handle || !handle.Classes.Contains("DragHandle") || !ViewModel.CanEdit || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragged = handle.DataContext;
        var collapsed = _dragged is RuleRow { IsExpanded: true } rule ? rule : null;
        if (collapsed is not null) collapsed.IsExpanded = false;

        var item = new DataTransferItem();
        item.Set(DragFormat, _dragged!);
        var data = new DataTransfer();
        data.Add(item);
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _dragged = null;
            DropIndicator.IsVisible = false;
            if (collapsed is not null) collapsed.IsExpanded = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = FindTarget(e);
        e.DragEffects = target is null ? DragDropEffects.None : DragDropEffects.Move;
        DropIndicator.IsVisible = target is not null;
        if (target is { } t)
        {
            ShowIndicator(t.List, t.Index, t.Anchor);
        }

        var y = e.GetPosition(Scroller).Y;
        var height = Scroller.Bounds.Height;
        if (_dragged is not null && y >= 0 && y <= height && (y < 40 || y > height - 40))
        {
            Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y + (y < 40 ? -20 : 20));
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropIndicator.IsVisible = false;
        if (FindTarget(e) is not { } target || !ViewModel.CanEdit) return;

        switch (_dragged)
        {
            case RuleRow rule:
                await ViewModel.MoveAsync(rule, target.Index);
                break;
            case ConditionRow condition when target.Rule is { } owner:
                await owner.AcceptConditionAsync(condition, target.Index);
                break;
            case ActionRow action when target.Rule is { } owner:
                await owner.AcceptActionAsync(action, target.Tag == "Else", target.Index);
                break;
        }
    }

    private DropTarget? FindTarget(DragEventArgs e)
    {
        if (_dragged is null) return null;

        for (var visual = this.InputHitTest(e.GetPosition(this)) as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { Tag: string tag } control || !Accepts(tag)) continue;

            var rule = control.DataContext as RuleRow;
            if (control is ItemsControl list)
            {
                var midpoints = Enumerable.Range(0, list.ItemCount)
                    .Select(list.ContainerFromIndex)
                    .Select(c => c?.TranslatePoint(new Point(0, c.Bounds.Height / 2), list)?.Y ?? double.MaxValue)
                    .ToList();
                return new DropTarget(tag, ListReorder.InsertionIndex(midpoints, e.GetPosition(list).Y), list, control, rule);
            }

            if (control.Classes.Contains("DropTop"))
            {
                var section = tag == "Rules" ? RulesList : (control.Parent as Panel)?.Children.OfType<ItemsControl>().FirstOrDefault(i => Equals(i.Tag, tag));
                return section is null ? null : new DropTarget(tag, 0, section, control, rule);
            }
        }

        return null;
    }

    private bool Accepts(string tag) => _dragged switch
    {
        RuleRow => tag == "Rules",
        ConditionRow => tag == "Conditions",
        ActionRow => tag is "Actions" or "Else",
        _ => false,
    };

    private void ShowIndicator(ItemsControl list, int index, Control anchor)
    {
        var count = list.ItemCount;
        var origin = count == 0 ? anchor.TranslatePoint(new Point(0, anchor.Bounds.Height / 2), DropLayer)
            : index < count && list.ContainerFromIndex(index) is { } next ? next.TranslatePoint(new Point(0, -3), DropLayer)
            : list.ContainerFromIndex(count - 1) is { } last ? last.TranslatePoint(new Point(0, last.Bounds.Height + 1), DropLayer)
            : null;
        if (origin is not { } point)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        Canvas.SetLeft(DropIndicator, point.X);
        Canvas.SetTop(DropIndicator, point.Y);
        DropIndicator.Width = count == 0 ? anchor.Bounds.Width : list.Bounds.Width;
    }

    private sealed record DropTarget(string Tag, int Index, ItemsControl List, Control Anchor, RuleRow? Rule);
}
