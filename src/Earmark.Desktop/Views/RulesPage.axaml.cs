using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Core.Routing;
using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels.Rules;

namespace Earmark.Desktop.Views;

public sealed partial class RulesPage : UserControl
{
    private Drag? _drag;

    public RulesPage(RulesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusRequested += row => Dispatcher.UIThread.Post(() => RulesList.ContainerFromItem(row)?.BringIntoView(), DispatcherPriority.Loaded);
        AttachedToVisualTree += (_, _) => viewModel.PageVisible = true;
        DetachedFromVisualTree += (_, _) => viewModel.PageVisible = false;

        AddHandler(PointerPressedEvent, OnPointerPressed);
        AddHandler(PointerMovedEvent, OnPointerMoved);
        AddHandler(PointerReleasedEvent, OnPointerReleased);
        AddHandler(PointerCaptureLostEvent, (_, _) => Cancel());
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
        if (Item<RuleRow>(sender) is { } row && ViewModel.CanEdit) row.Enabled = !row.Enabled;
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
        var source = e.Source as Visual;
        if (Item<RuleRow>(sender) is { } row && source?.FindAncestorOfType<ToggleSwitch>(includeSelf: true) is null && !IsHandle(source))
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

    private static bool IsHandle(Visual? visual) => visual is TextBlock handle && handle.Classes.Contains("RulesDragHandle");

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_drag is not null || !IsHandle(e.Source as Visual) || !ViewModel.CanEdit || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var handle = (TextBlock)e.Source!;
        if (handle.FindAncestorOfType<ItemsControl>() is not { } source || source.ContainerFromItem(handle.DataContext!) is not { } row) return;

        _drag = new Drag(handle.DataContext!, row, source, source.IndexFromContainer(row), e.GetPosition(row), e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag) return;

        if (!drag.Lifted)
        {
            var moved = e.GetPosition(this) - drag.Start;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) < 6) return;
            Lift(drag);
        }

        var y = e.GetPosition(Scroller).Y;
        if (y < 40 || y > Scroller.Bounds.Height - 40)
        {
            Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y + (y < 40 ? -12 : 12));
        }

        if (drag.Row.RenderTransform is TranslateTransform follow && drag.Row.GetVisualParent() is Visual panel)
        {
            follow.Y = e.GetPosition(panel).Y - drag.Grab.Y - drag.Row.Bounds.Y;
        }

        drag.Target = FindTarget(drag, e) ?? new DropTarget(drag.Source, drag.From, string.Empty, null);
        drag.Touched.Add(drag.Target.List);
        foreach (var list in drag.Touched)
        {
            var from = list == drag.Source ? drag.From : -1;
            var insertAt = list == drag.Target.List ? drag.Target.Index : -1;
            for (var i = 0; i < list.ItemCount; i++)
            {
                if (i != from && list.ContainerFromIndex(i) is { } row)
                {
                    Slide(row, ListReorder.Offset(i, from, insertAt, Slot(drag, drag.Source), Slot(drag, list)));
                }
            }
        }
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        e.Pointer.Capture(null);
        if (!drag.Lifted) return;

        if (drag.Target is not { Tag.Length: > 0 } target)
        {
            Settle(drag);
            return;
        }

        Reset(drag);
        switch (drag.Item)
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

    private void OnDragKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is null) return;
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        if (drag.Lifted) Settle(drag);
    }

    private void Lift(Drag drag)
    {
        drag.Lifted = true;
        if (drag.Item is RuleRow { IsExpanded: true } rule)
        {
            rule.IsExpanded = false;
            drag.Collapsed = rule;
            RulesList.UpdateLayout();
        }

        var owner = drag.Item switch { ConditionRow c => c.Owner, ActionRow a => a.Owner, _ => null };
        drag.Card = owner is null ? null : RulesList.ContainerFromItem(owner);
        foreach (var raised in new[] { drag.Row, drag.Card })
        {
            raised?.SetValue(ZIndexProperty, 1);
        }

        drag.Row.RenderTransform = new TranslateTransform();
        drag.Row.IsHitTestVisible = false;
        drag.Row.Classes.Add("RulesLifted");
        drag.Touched.Add(drag.Source);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
    }

    private void Settle(Drag drag)
    {
        foreach (var row in drag.Touched.SelectMany(Containers))
        {
            if (row != drag.Row) Slide(row, 0);
        }

        if (drag.Row.RenderTransform is TranslateTransform follow)
        {
            Slide(drag.Row, follow.Y);
            Slide(drag.Row, 0);
        }

        DispatcherTimer.RunOnce(() => Reset(drag), Motion.Duration + TimeSpan.FromMilliseconds(16));
    }

    private void Reset(Drag drag)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
        foreach (var row in drag.Touched.SelectMany(Containers).Append(drag.Row))
        {
            row.RenderTransform = null;
        }

        foreach (var raised in new[] { drag.Row, drag.Card })
        {
            raised?.ClearValue(ZIndexProperty);
        }

        drag.Row.IsHitTestVisible = true;
        drag.Row.Classes.Remove("RulesLifted");
        if (drag.Collapsed is { } rule) rule.IsExpanded = true;
    }

    private static IEnumerable<Control> Containers(ItemsControl list) =>
        Enumerable.Range(0, list.ItemCount).Select(list.ContainerFromIndex).OfType<Control>();

    private static double Slot(Drag drag, ItemsControl list) => drag.Row.Bounds.Height + ((list.ItemsPanelRoot as StackPanel)?.Spacing ?? 0);

    private static void Slide(Control row, double y)
    {
        if (row.RenderTransform is not TranslateTransform { Transitions: not null } slide)
        {
            var from = (row.RenderTransform as TranslateTransform)?.Y ?? 0;
            row.RenderTransform = slide = new TranslateTransform(0, from)
            {
                Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = Motion.Duration, Easing = Motion.Standard }],
            };
        }

        slide.Y = y;
    }

    private DropTarget? FindTarget(Drag drag, PointerEventArgs e)
    {
        for (var visual = this.InputHitTest(e.GetPosition(this)) as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control { Tag: string tag } control || !Accepts(drag.Item, tag)) continue;

            var rule = control.DataContext as RuleRow;
            if (control is ItemsControl list && list.ItemsPanelRoot is { } panel)
            {
                var rows = Enumerable.Range(0, list.ItemCount).Select(i => list.ContainerFromIndex(i)?.Bounds ?? default).ToList();
                var from = list == drag.Source ? drag.From : -1;
                var index = ListReorder.InsertionIndex(rows.Select(r => r.Y).ToList(), rows.Select(r => r.Height).ToList(), from, Slot(drag, list), e.GetPosition(panel).Y);
                return new DropTarget(list, index, tag, rule);
            }

            if (control.Classes.Contains("RulesDropTop"))
            {
                var section = tag == "Rules" ? RulesList : (control.Parent as Panel)?.Children.OfType<ItemsControl>().FirstOrDefault(i => Equals(i.Tag, tag));
                return section is null ? null : new DropTarget(section, 0, tag, rule);
            }
        }

        return null;
    }

    private static bool Accepts(object item, string tag) => item switch
    {
        RuleRow => tag == "Rules",
        ConditionRow => tag == "Conditions",
        ActionRow => tag is "Actions" or "Else",
        _ => false,
    };

    private sealed record DropTarget(ItemsControl List, int Index, string Tag, RuleRow? Rule);

    private sealed class Drag(object item, Control row, ItemsControl source, int from, Point grab, Point start)
    {
        public object Item { get; } = item;

        public Control Row { get; } = row;

        public ItemsControl Source { get; } = source;

        public int From { get; } = from;

        public Point Grab { get; } = grab;

        public Point Start { get; } = start;

        public bool Lifted { get; set; }

        public RuleRow? Collapsed { get; set; }

        public Control? Card { get; set; }

        public DropTarget? Target { get; set; }

        public HashSet<ItemsControl> Touched { get; } = [];
    }
}
