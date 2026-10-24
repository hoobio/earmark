using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Earmark.Desktop.Styles;

public static class SegmentedSelection
{
    public static readonly AttachedProperty<bool> SelectsOnReleaseProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("SelectsOnRelease", typeof(SegmentedSelection));

    private static ListBoxItem? _pressed;

    static SegmentedSelection()
    {
        SelectsOnReleaseProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            list.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            list.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            list.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            if (e.GetNewValue<bool>())
            {
                list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
                list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
                list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            }
        });
    }

    public static bool GetSelectsOnRelease(ListBox list) => list.GetValue(SelectsOnReleaseProperty);

    public static void SetSelectsOnRelease(ListBox list, bool value) => list.SetValue(SelectsOnReleaseProperty, value);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item)
        {
            return;
        }

        e.Handled = true;
        SetPressed(item);
        e.Pointer.Capture(item);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBox list || _pressed is not { } item)
        {
            return;
        }

        e.Handled = true;
        SetPressed(null);
        e.Pointer.Capture(null);
        if (new Rect(item.Bounds.Size).Contains(e.GetPosition(item)) && list.IndexFromContainer(item) is var index and >= 0)
        {
            list.SelectedIndex = index;
        }
    }

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => SetPressed(null);

    private static void SetPressed(ListBoxItem? item)
    {
        if (_pressed is { } previous)
        {
            ((IPseudoClasses)previous.Classes).Set(":pressed", false);
        }

        _pressed = item;
        if (item is not null)
        {
            ((IPseudoClasses)item.Classes).Set(":pressed", true);
        }
    }
}
