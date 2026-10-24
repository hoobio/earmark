using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Earmark.Desktop.Controls;
using Earmark.Desktop.ViewModels.Devices;

namespace Earmark.Desktop.Views;

public sealed partial class DevicesPage : UserControl
{
    private readonly DevicesViewModel _vm;

    public DevicesPage(DevicesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        MeterClock.Track(this);
        void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control && e.Source is not TextBox)
            {
                vm.Undo();
                e.Handled = true;
            }
        }

        TopLevel? top = null;
        AttachedToVisualTree += (_, _) => (top = TopLevel.GetTopLevel(this))?.AddHandler(KeyDownEvent, OnKey);
        DetachedFromVisualTree += (_, _) => top?.RemoveHandler(KeyDownEvent, OnKey);
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                BuildOptions().ShowAt(this, showAtPointer: true);
            }
        });
        BlocksHost.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = !vm.LockLayout && (e.DataTransfer.Contains(DeviceCard.CardFormat) || e.DataTransfer.Contains(DeviceCard.GroupFormat)) ? DragDropEffects.Move : DragDropEffects.None;
        });
        BlocksHost.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (vm.LockLayout)
            {
                return;
            }

            if (e.DataTransfer.TryGetValue(DeviceCard.CardFormat) is { } card)
            {
                vm.LeaveGroup(card.Key, null);
            }
            else if (e.DataTransfer.TryGetValue(DeviceCard.GroupFormat) is { } group)
            {
                vm.ReorderBlock(group.Id, null);
            }
        });
    }

    private void OnOptionsClick(object? sender, RoutedEventArgs e) => BuildOptions().ShowAt(OptionsButton);

    private MenuFlyout BuildOptions()
    {
        MenuItem Toggle(string header, bool value, Action<bool> set)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };
            item.Click += (_, _) => set(!value);
            return item;
        }

        var undo = new MenuItem { Header = "Undo", InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control), IsEnabled = _vm.CanUndo };
        undo.Click += (_, _) => _vm.Undo();
        return new MenuFlyout
        {
            ItemsSource = new List<object>
            {
                new MenuItem { Header = "Device card", IsEnabled = false },
                Toggle("Show rules", _vm.ShowRules, v => _vm.ShowRules = v),
                Toggle("Card dividers", _vm.ShowDividers, v => _vm.ShowDividers = v),
                Toggle("Device badges", _vm.ShowBadges, v => _vm.ShowBadges = v),
                Toggle("Compact cards", _vm.CompactCards, v => _vm.CompactCards = v),
                new Separator(),
                new MenuItem { Header = "Devices page", IsEnabled = false },
                Toggle("Show title bar", _vm.ShowHeader, v => _vm.ShowHeader = v),
                Toggle("Show hidden devices", _vm.ShowHiddenDevices, v => _vm.ShowHiddenDevices = v),
                Toggle("Show disconnected devices", _vm.ShowDisconnectedDevices, v => _vm.ShowDisconnectedDevices = v),
                Toggle("Lock layout", _vm.LockLayout, v => _vm.LockLayout = v),
                new Separator(),
                undo,
            },
        };
    }
}
