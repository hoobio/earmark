using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels.Devices;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Controls;

public sealed class DeviceCardContext(DeviceCardViewModel card, bool overlay) : Observable
{
    private bool _isDropTarget;

    public DeviceCardViewModel Card { get; } = card;

    public CardOptions Options { get; } = card.OptionsFor(overlay);

    public bool IsOverlay { get; } = overlay;

    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
}

public sealed partial class DeviceCard : UserControl
{
    public static readonly StyledProperty<DeviceCardViewModel?> CardProperty = AvaloniaProperty.Register<DeviceCard, DeviceCardViewModel?>(nameof(Card));
    public static readonly StyledProperty<bool> IsOverlayProperty = AvaloniaProperty.Register<DeviceCard, bool>(nameof(IsOverlay));

    public static readonly DataFormat<DeviceCardViewModel> CardFormat = DataFormat.CreateInProcessFormat<DeviceCardViewModel>("earmark-card");
    public static readonly DataFormat<AppChipViewModel> ChipFormat = DataFormat.CreateInProcessFormat<AppChipViewModel>("earmark-chip");
    public static readonly DataFormat<DeviceGroupViewModel> GroupFormat = DataFormat.CreateInProcessFormat<DeviceGroupViewModel>("earmark-group");

    private const double DragThreshold = 6;

    private (double Volume, bool Muted)? _sliderStart;
    private PointerPressedEventArgs? _press;
    private object? _pressSource;
    private bool _rulesShown;
    private int _rulesRun;

    public DeviceCard()
    {
        InitializeComponent();
        MeterClock.Track(this);
        VolumeSlider.AddHandler(PointerPressedEvent, OnSliderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.AddHandler(PointerReleasedEvent, OnSliderReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.AddHandler(PointerCaptureLostEvent, (_, _) => FinishSlider(ping: true), RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeSlider.KeyUp += (_, _) => FinishSlider(ping: true);
        VolumeSlider.KeyDown += (_, _) => _sliderStart ??= Card is { } c ? (c.Volume, c.IsMuted) : null;
        AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: false);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        Root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Root.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(false));
        Root.AddHandler(DragDrop.DropEvent, OnDrop);
        // Border.ClipToBounds clips children to a square rect, so the artwork needs its own rounded clip.
        Art.SizeChanged += (_, e) =>
        {
            var radius = Math.Max(0, Root.CornerRadius.TopLeft - Root.BorderThickness.Left);
            Art.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        };
    }

    public DeviceCardViewModel? Card { get => GetValue(CardProperty); set => SetValue(CardProperty, value); }

    public bool IsOverlay { get => GetValue(IsOverlayProperty); set => SetValue(IsOverlayProperty, value); }

    private DeviceCardContext? Context => Root.DataContext as DeviceCardContext;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardProperty || change.Property == IsOverlayProperty)
        {
            Root.DataContext = Card is { } card ? new DeviceCardContext(card, IsOverlay) : null;
        }

        if (change.Property == CardProperty && this.IsAttachedToVisualTree())
        {
            Watch(change.OldValue as DeviceCardViewModel, Card);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(null, Card);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(Card, null);
    }

    private void Watch(DeviceCardViewModel? old, DeviceCardViewModel? card)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnCardPropertyChanged;
        }

        if (card is not null)
        {
            card.PropertyChanged += OnCardPropertyChanged;
        }

        _rulesShown = card?.IsRulesExpanded == true;
        _rulesRun++;
        MoreRules.IsVisible = _rulesShown;
        MoreRules.ClearValue(MaxHeightProperty);
        MoreRules.ClearValue(OpacityProperty);
    }

    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceCardViewModel.IsRulesExpanded) or "" or null && Card is { } card && card.IsRulesExpanded != _rulesShown)
        {
            _ = AnimateRulesAsync(card.IsRulesExpanded);
        }
    }

    private async Task AnimateRulesAsync(bool expand)
    {
        _rulesShown = expand;
        _rulesRun++;
        if (!Motion.Enabled)
        {
            MoreRules.IsVisible = expand;
            return;
        }

        var run = ++_rulesRun;
        var from = MoreRules.IsVisible ? MoreRules.Bounds.Height : 0;
        MoreRules.IsVisible = true;
        MoreRules.ClearValue(MaxHeightProperty);
        MoreRules.Measure(new Size(MoreRules.Bounds.Width > 0 ? MoreRules.Bounds.Width : Bounds.Width, double.PositiveInfinity));
        var to = expand ? MoreRules.DesiredSize.Height : 0;
        MoreRules.MaxHeight = to;
        MoreRules.Opacity = expand ? 1 : 0;
        var animation = new Animation
        {
            Duration = Motion.Duration,
            Easing = Motion.Standard,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(MaxHeightProperty, from), new Setter(OpacityProperty, expand ? 0d : 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(MaxHeightProperty, to), new Setter(OpacityProperty, expand ? 1d : 0d) } },
            },
        };
        await animation.RunAsync(MoreRules);
        if (_rulesRun == run)
        {
            MoreRules.IsVisible = expand;
            MoreRules.ClearValue(MaxHeightProperty);
            MoreRules.ClearValue(OpacityProperty);
        }
    }

    private void OnTileClick(object? sender, RoutedEventArgs e)
    {
        if (Card is { } card)
        {
            card.Owner.ToggleMute(card);
        }
    }

    private void OnBluetoothClick(object? sender, RoutedEventArgs e) => _ = Card?.ToggleBluetoothAsync();

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => _sliderStart = Card is { } c ? (c.Volume, c.IsMuted) : null;

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e) => FinishSlider(ping: true);

    private void FinishSlider(bool ping)
    {
        if (Card is not { } card || _sliderStart is not { } start)
        {
            return;
        }

        _sliderStart = null;
        card.Owner.RecordVolumeUndo(card, start.Volume, start.Muted);
        if (ping)
        {
            card.PlayPing();
        }
    }

    private static NowPlayingViewModel? SeekStripOf(RoutedEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<Slider>(includeSelf: true) is { Tag: NowPlayingViewModel strip } ? strip : null;

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => SeekStripOf(e)?.BeginSeek();

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => _ = SeekStripOf(e)?.EndSeekAsync();

    private static NowPlayingViewModel? StripOf(object? sender) => (sender as Control)?.DataContext as NowPlayingViewModel;

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => _ = StripOf(sender)?.PreviousAsync();

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => _ = StripOf(sender)?.PlayPauseAsync();

    private void OnNextClick(object? sender, RoutedEventArgs e) => _ = StripOf(sender)?.NextAsync();

    private void OnRuleClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is RuleSummary rule)
        {
            App.Services.GetKeyedService<Action<Guid>>("OpenRule")?.Invoke(rule.RuleId);
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsOverlay || Card is not { } card || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var chip = (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { Tag: AppChipViewModel c } ? c : null;
        if (chip is null && (card.Owner.LockLayout || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null))
        {
            return;
        }

        _press = e;
        _pressSource = chip is { CanDrag: true } ? chip : chip is null ? card : null;
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressSource is null)
        {
            return;
        }

        var delta = e.GetPosition(this) - press.GetPosition(this);
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        var source = _pressSource;
        _press = null;
        var transfer = new DataTransfer();
        transfer.Add(source is AppChipViewModel chip ? DataTransferItem.Create(ChipFormat, chip) : DataTransferItem.Create(CardFormat, (DeviceCardViewModel)source));
        await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
    }

    private int RegionOf(DragEventArgs e)
    {
        var x = e.GetPosition(Root).X / Math.Max(1, Root.Bounds.Width);
        return x < 0.3 ? -1 : x > 0.7 ? 1 : 0;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (Card is not { } card || IsOverlay)
        {
            return;
        }

        if (e.DataTransfer.TryGetValue(ChipFormat) is { } chip)
        {
            if (card.IsRender && card.IsConnected && chip.Owner != card)
            {
                e.DragEffects = DragDropEffects.Move;
            }
        }
        else if ((e.DataTransfer.TryGetValue(CardFormat) is { } other && other != card) || e.DataTransfer.Contains(GroupFormat))
        {
            e.DragEffects = DragDropEffects.Move;
        }

        SetDropTarget(e.DragEffects != DragDropEffects.None && (e.DataTransfer.Contains(ChipFormat) || RegionOf(e) == 0));
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(false);
        if (Card is not { } card || IsOverlay)
        {
            return;
        }

        e.Handled = true;
        if (e.DataTransfer.TryGetValue(ChipFormat) is { } chip)
        {
            card.Owner.MoveChip(chip, card);
        }
        else if (e.DataTransfer.TryGetValue(CardFormat) is { } source)
        {
            var region = RegionOf(e);
            if (card.Owner.DropWouldDisband(source, card, region) && TopLevel.GetTopLevel(this) is { } top
                && !await AppDialogs.ShowMessageAsync(top, "Disband group?", "Removing this device leaves the group with a single device, so the group will be disbanded.", "Disband", "Cancel"))
            {
                return;
            }

            card.Owner.DropCard(source, card, region);
        }
        else if (e.DataTransfer.TryGetValue(GroupFormat) is { } group)
        {
            card.Owner.DropGroup(group.Id, card, RegionOf(e));
        }
    }

    private void SetDropTarget(bool value)
    {
        if (Context is { } context)
        {
            context.IsDropTarget = value;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Card is not { } card)
        {
            return;
        }

        e.Handled = true;
        var chip = (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { Tag: AppChipViewModel c } ? c : null;
        var items = new List<object>();
        if (IsOverlay)
        {
            items.Add(Item("Settings", 0xF00F, () => App.Services.GetKeyedService<Action<string>>("OpenSettingsSection")?.Invoke("QuickControls")));
        }
        else
        {
            if (chip is not null)
            {
                AddChipItems(items, chip);
                items.Add(new Separator());
            }

            AddDeviceItems(items, card);
        }

        var flyout = new MenuFlyout { ItemsSource = items };
        flyout.ShowAt(this, showAtPointer: true);
    }

    private static void AddChipItems(List<object> items, AppChipViewModel chip)
    {
        var owner = chip.Owner.Owner;
        var hide = Item("Hide this app", 0xE889, null);
        hide.ItemsSource = new List<object>
        {
            Tip(Item("On this device", null, () => owner.HideAppOnDevice(chip)), "Hide this app's chip on this device only; it stays on other devices. Unhide it from Settings, App indicators."),
            Tip(Item("Everywhere", null, () => owner.HideApp(chip)), "Hide this app's chip from every device. Unhide it from Settings, App indicators."),
        };
        items.Add(hide);
        if (!chip.ShowProcessActions)
        {
            return;
        }

        var close = Tip(Item(chip.CanClose ? "Close this app" : "Cannot close elevated app", 0xE6D3, () => owner.CloseApp(chip, terminate: false)), "Ask this app to close, like clicking its X (it can save or prompt first).");
        close.IsEnabled = chip.CanClose;
        items.Add(close);
        if (AppDialogs.IsKeyHeld(Key.LeftShift) || AppDialogs.IsKeyHeld(Key.RightShift))
        {
            var kill = Tip(Item(chip.CanTerminate ? "Terminate this app" : "Cannot terminate elevated app", 0xE877, () => owner.CloseApp(chip, terminate: true)), "Force this app to quit now. Unsaved work is lost.");
            kill.IsEnabled = chip.CanTerminate;
            kill.Classes.Add("Critical");
            items.Add(kill);
        }
    }

    private void AddDeviceItems(List<object> items, DeviceCardViewModel card)
    {
        var owner = card.Owner;
        items.Add(Item(card.IsEffectivelyHidden ? "Show this device" : "Hide this device", card.IsEffectivelyHidden ? 0xE883 : 0xE889, () => _ = ToggleVisibilityAsync(card)));
        if (card.CanQuickPin)
        {
            items.Add(Item(card.IsQuickPinned ? "Unpin from Quick Controls" : "Pin to Quick Controls", card.IsQuickPinned ? 0xEE79 : 0xEE75, () => owner.ToggleQuickPin(card)));
        }

        items.Add(Item("Customise...", 0xE566, () => _ = OpenCustomiseAsync(card)));
        if (card.IsDisconnected)
        {
            items.Add(Tip(Item("Forget device", 0xE66D, () => owner.ForgetDevice(card)), "Stop remembering this disconnected device, clearing its saved order, group and customisation. It returns as new if it reconnects."));
        }

        if (card.IsGroupMember && owner.GroupViewOf(card) is { } group)
        {
            items.Add(new Separator());
            items.Add(Item("Ungroup device", 0xE9C9, () => owner.UngroupDevice(card)));
            items.Add(new Separator());
            items.Add(Item("Rename group", 0xE837, () => group.IsEditing = true));
            var delete = Item("Delete group", 0xE66D, () => owner.DeleteGroup(group.Id));
            delete.Classes.Add("Critical");
            items.Add(delete);
        }
    }

    private async Task ToggleVisibilityAsync(DeviceCardViewModel card)
    {
        if (!card.IsEffectivelyHidden && card.IsQuickPinned && TopLevel.GetTopLevel(this) is { } top
            && !await AppDialogs.ShowMessageAsync(top, "Hide pinned device?", "This device is pinned to Quick Controls. Hiding it will remove it from Quick Controls.", "Hide and unpin", "Cancel"))
        {
            return;
        }

        card.Owner.ToggleVisibility(card);
    }

    private async Task OpenCustomiseAsync(DeviceCardViewModel card)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            await CustomiseDeviceDialog.ShowAsync(card, top);
        }
    }

    private static MenuItem Item(string header, int? glyph, Action? click)
    {
        var item = new MenuItem { Header = header };
        if (glyph is { } g)
        {
            item.Icon = new TextBlock { Classes = { "Glyph" }, Text = char.ConvertFromUtf32(g) };
        }

        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    private static MenuItem Tip(MenuItem item, string tip)
    {
        ToolTip.SetTip(item, tip);
        return item;
    }
}
