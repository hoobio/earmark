using System.Collections.Specialized;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Earmark.Desktop.Views;

public static class GroupEdges
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<StackPanel, bool>("IsEnabled", typeof(GroupEdges));

    private static readonly AttachedProperty<NotifyCollectionChangedEventHandler?> HandlerProperty =
        AvaloniaProperty.RegisterAttached<StackPanel, NotifyCollectionChangedEventHandler?>("Handler", typeof(GroupEdges));

    static GroupEdges()
    {
        IsEnabledProperty.Changed.AddClassHandler<StackPanel>((panel, e) =>
        {
            if (panel.GetValue(HandlerProperty) is { } old)
            {
                panel.Children.CollectionChanged -= old;
            }

            NotifyCollectionChangedEventHandler? handler = e.GetNewValue<bool>() ? (_, _) => Refresh(panel) : null;
            panel.SetValue(HandlerProperty, handler);
            if (handler is not null)
            {
                panel.Children.CollectionChanged += handler;
                Refresh(panel);
            }
        });
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((row, _) =>
        {
            if (row.GetVisualParent() is StackPanel panel && panel.GetValue(IsEnabledProperty))
            {
                Refresh(panel);
            }
        });
    }

    public static bool GetIsEnabled(StackPanel panel) => panel.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(StackPanel panel, bool value) => panel.SetValue(IsEnabledProperty, value);

    private static void Refresh(StackPanel panel)
    {
        var first = panel.Children.FirstOrDefault(row => row.IsVisible);
        var last = panel.Children.LastOrDefault(row => row.IsVisible);
        foreach (var row in panel.Children)
        {
            row.Classes.Set("FirstVisible", row == first);
            row.Classes.Set("LastVisible", row == last);
        }
    }
}
