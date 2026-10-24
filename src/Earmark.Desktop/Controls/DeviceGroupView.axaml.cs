using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Earmark.Desktop.ViewModels.Devices;

namespace Earmark.Desktop.Controls;

public sealed partial class DeviceGroupView : UserControl
{
    public static readonly StyledProperty<DeviceGroupViewModel?> GroupProperty = AvaloniaProperty.Register<DeviceGroupView, DeviceGroupViewModel?>(nameof(Group));

    private PointerPressedEventArgs? _press;

    public DeviceGroupView()
    {
        InitializeComponent();
        Header.AddHandler(PointerPressedEvent, (_, e) => _press = Group?.Owner.LockLayout == false && !Group.IsEditing ? e : null, RoutingStrategies.Bubble, handledEventsToo: true);
        Header.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Bubble, handledEventsToo: true);
        Header.AddHandler(PointerMovedEvent, OnHeaderMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Header.AddHandler(DragDrop.DragOverEvent, OnHeaderDragOver);
        Header.AddHandler(DragDrop.DropEvent, OnHeaderDrop);
    }

    public DeviceGroupViewModel? Group { get => GetValue(GroupProperty); set => SetValue(GroupProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupProperty)
        {
            if (change.OldValue is DeviceGroupViewModel old)
            {
                old.PropertyChanged -= OnGroupChanged;
            }

            if (Group is not null)
            {
                Group.PropertyChanged += OnGroupChanged;
            }
        }
    }

    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceGroupViewModel.IsEditing) && Group?.IsEditing == true)
        {
            TitleEditor.Text = Group.Title;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TitleEditor.Focus();
                TitleEditor.SelectAll();
            });
        }
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Group is { } group)
        {
            group.IsEditing = true;
        }
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (Group is not { } group)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            group.CommitTitle(TitleEditor.Text ?? string.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            group.IsEditing = false;
            e.Handled = true;
        }
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Group is { IsEditing: true } group)
        {
            group.CommitTitle(TitleEditor.Text ?? string.Empty);
        }
    }

    private async void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || Group is not { } group)
        {
            return;
        }

        var delta = e.GetPosition(this) - press.GetPosition(this);
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _press = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DeviceCard.GroupFormat, group));
        await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
    }

    private void OnHeaderDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DeviceCard.CardFormat) || e.DataTransfer.Contains(DeviceCard.GroupFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnHeaderDrop(object? sender, DragEventArgs e)
    {
        if (Group is not { } group)
        {
            return;
        }

        e.Handled = true;
        if (e.DataTransfer.TryGetValue(DeviceCard.CardFormat) is { } card)
        {
            group.Owner.DropOnGroupHeader(card, group.Id);
        }
        else if (e.DataTransfer.TryGetValue(DeviceCard.GroupFormat) is { } other)
        {
            group.Owner.DropGroupOnGroup(other.Id, group.Id);
        }
    }
}
