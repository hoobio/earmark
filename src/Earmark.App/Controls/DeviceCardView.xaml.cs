using Earmark.App.ViewModels;
using Earmark.App.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Earmark.App.Controls;

// XAML event handlers and x:Bind function bindings must be instance members even when the body
// doesn't touch instance state.
#pragma warning disable CA1822

/// <summary>
/// The reusable device card body: tile/name/chips, volume + peak meter, rules, and app chips. Hosted
/// by the Devices page (inside a drag/drop <see cref="Border"/>) and by the Quick Controls flyout. The
/// page-level gestures (block reorder, app-chip drop target) live on the host's wrapping Border; this
/// control owns the per-card interactions and reaches the singleton <see cref="HomeViewModel"/> directly.
/// </summary>
public sealed partial class DeviceCardView : UserControl
{
    private readonly HomeViewModel _viewModel;
    private readonly RulesViewModel _rulesViewModel;
    private readonly MainWindow _mainWindow;
    private readonly ILogger<DeviceCardView>? _logger;
    private readonly Dictionary<Slider, (float Volume, bool Muted)> _sliderDragStart = new();

    public DeviceCardView()
    {
        var services = App.Current.Services;
        _viewModel = services.GetRequiredService<HomeViewModel>();
        _rulesViewModel = services.GetRequiredService<RulesViewModel>();
        _mainWindow = services.GetRequiredService<MainWindow>();
        _logger = services.GetService<ILogger<DeviceCardView>>();
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "DeviceCardView.InitializeComponent threw");
            throw;
        }
    }

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(DeviceCard), typeof(DeviceCardView), new PropertyMetadata(null, OnCardChanged));

    public DeviceCard? Card
    {
        get => (DeviceCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    // ---- Rules expand/collapse animation ----

    private Storyboard? _rulesStoryboard;
    private bool _rulesClipApplied;

    private static void OnCardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DeviceCardView view) return;
        if (e.OldValue is DeviceCard oldCard)
        {
            oldCard.PropertyChanged -= view.OnCardPropertyChanged;
            oldCard.IsRulesCollapsing = false;   // don't leave a recycled card stuck opted-out mid-collapse
        }
        if (e.NewValue is DeviceCard newCard) newCard.PropertyChanged += view.OnCardPropertyChanged;
        // Recycle / first bind: snap the rules panel to the new card's resting state, no animation.
        view.SetRulesPanelState(e.NewValue as DeviceCard, animate: false);
    }

    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceCard.IsRulesExpanded))
        {
            SetRulesPanelState(sender as DeviceCard, animate: true);
        }
    }

    /// <summary>Clips the rules panel to its own (animating) bounds so the content doesn't spill out
    /// while the panel grows from / shrinks to zero. An InsetClip tracks the visual's size, so it's a
    /// one-time set - no per-frame geometry update.</summary>
    private void EnsureRulesClip()
    {
        if (_rulesClipApplied) return;
        var visual = ElementCompositionPreview.GetElementVisual(AdditionalRulesPanel);
        visual.Clip = visual.Compositor.CreateInsetClip();
        _rulesClipApplied = true;
    }

    /// <summary>Drives the additional-rules reveal. <paramref name="animate"/> false snaps to the resting
    /// state (used on recycle); true runs a height + opacity transition. Animating layout MaxHeight makes
    /// the card reflow for real, so the row grows and siblings glide via their implicit Offset.</summary>
    private void SetRulesPanelState(DeviceCard? card, bool animate)
    {
        var expanded = card?.IsRulesExpanded == true;
        var panel = AdditionalRulesPanel;
        _rulesStoryboard?.Stop();
        _rulesStoryboard = null;

        if (!animate)
        {
            if (card is not null) card.IsRulesCollapsing = false;
            panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            panel.MaxHeight = double.PositiveInfinity;
            panel.Opacity = 1;
            return;
        }

        // Collapsing: hold the card opted out of the row baseline until the panel finishes shrinking, so
        // its still-tall content doesn't inflate the baseline and pull siblings up for the animation.
        if (card is not null) card.IsRulesCollapsing = !expanded;

        EnsureRulesClip();
        panel.Visibility = Visibility.Visible;

        double from, to;
        if (expanded)
        {
            panel.MaxHeight = double.PositiveInfinity;
            var width = panel.ActualWidth > 0 ? panel.ActualWidth : ActualWidth;
            panel.Measure(new Size(width, double.PositiveInfinity));
            from = 0;
            to = panel.DesiredSize.Height;
        }
        else
        {
            from = panel.ActualHeight > 0 ? panel.ActualHeight : panel.DesiredSize.Height;
            to = 0;
        }

        panel.MaxHeight = from;
        panel.Opacity = expanded ? 0 : 1;

        var height = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(height, panel);
        Storyboard.SetTargetProperty(height, "MaxHeight");

        var opacity = new DoubleAnimation
        {
            From = expanded ? 0 : 1,
            To = expanded ? 1 : 0,
            BeginTime = TimeSpan.FromMilliseconds(expanded ? 60 : 0),
            Duration = new Duration(TimeSpan.FromMilliseconds(expanded ? 180 : 140)),
        };
        Storyboard.SetTarget(opacity, panel);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(height);
        sb.Children.Add(opacity);
        sb.Completed += (_, _) =>
        {
            // Expanded: drop the MaxHeight cap so later content changes resize freely. Collapsed: remove
            // it from layout entirely (no StackPanel spacing for a zero-height child), reset for reuse,
            // and rejoin the row baseline now that the panel has finished shrinking.
            panel.MaxHeight = double.PositiveInfinity;
            if (!expanded)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1;
                if (card is not null) card.IsRulesCollapsing = false;
            }
        };
        _rulesStoryboard = sb;
        sb.Begin();
    }

    /// <summary>When false (the Quick Controls flyout), the rules divider / section / "no rules"
    /// message never render. Constant per host.</summary>
    public static readonly DependencyProperty ShowRulesProperty = DependencyProperty.Register(
        nameof(ShowRules), typeof(bool), typeof(DeviceCardView), new PropertyMetadata(true));

    public bool ShowRules
    {
        get => (bool)GetValue(ShowRulesProperty);
        set => SetValue(ShowRulesProperty, value);
    }

    /// <summary>When false (the Quick Controls flyout), the card and app-chip right-click menus are
    /// suppressed. Customisation belongs in the main app, not the overlay. Constant per host.</summary>
    public static readonly DependencyProperty AllowContextMenuProperty = DependencyProperty.Register(
        nameof(AllowContextMenu), typeof(bool), typeof(DeviceCardView), new PropertyMetadata(true));

    public bool AllowContextMenu
    {
        get => (bool)GetValue(AllowContextMenuProperty);
        set => SetValue(AllowContextMenuProperty, value);
    }

    /// <summary>x:Bind function: a rule element shows only when rules are enabled for this host AND the
    /// card wants it. Re-evaluates when either argument changes, replacing the old collapse watchdog.</summary>
    public Visibility RuleVis(bool showRules, bool cardWants) =>
        showRules && cardWants ? Visibility.Visible : Visibility.Collapsed;

    // Section dividers split into an over-art variant (a subtle stroke, shown when the card paints its
    // artwork background) and a plain variant (the window base fill, otherwise). Two elements rather
    // than one Border with a brush-picking converter so each can use a {ThemeResource}: a code
    // converter resolves theme brushes against the app default theme, so dividers stayed dark in light
    // mode (the app themes per-element via RootGrid.RequestedTheme, not Application.RequestedTheme).
    public Visibility DividerOverArt(bool wantsDivider, bool overArt) =>
        wantsDivider && overArt ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DividerPlain(bool wantsDivider, bool overArt) =>
        wantsDivider && !overArt ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RuleDividerOverArt(bool showRules, bool cardWants, bool overArt) =>
        showRules && cardWants && overArt ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RuleDividerPlain(bool showRules, bool cardWants, bool overArt) =>
        showRules && cardWants && !overArt ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised when "Rename group" is picked from a card/app-chip menu, so the host can focus
    /// the group's title editor (which lives in the page's tree, not here).</summary>
    public event EventHandler<DeviceGroupCard>? RenameGroupRequested;

    private DeviceGroupCard? FindGroupOf(DeviceCard card)
    {
        foreach (var block in _viewModel.Blocks)
        {
            if (block is DeviceGroupCard group && group.Members.Contains(card)) return group;
        }
        return null;
    }

    private void OnMuteToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;
        var prevVolume = card.Volume;
        var prevMuted = card.IsMuted;
        card.ToggleMuteCommand.Execute(null);
        _viewModel.RecordVolumeMuteUndo(card, prevVolume, prevMuted);
    }

    private void OnRuleChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleSummary summary }) return;
        _rulesViewModel.RequestFocusRule(summary.RuleId);
        _mainWindow.NavigateByTag("Rules");
    }

    // ---- Volume slider interaction (captures pre-drag state for a single Ctrl+Z entry) ----

    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider { Tag: DeviceCard card } slider)
        {
            _sliderDragStart[slider] = (card.Volume, card.IsMuted);
        }
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider { Tag: DeviceCard card } slider) return;
        FinaliseSliderInteraction(slider, card);
        card.PlayPing();
    }

    private void OnSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSliderNudgeKey(e.Key)) return;
        if (sender is Slider { Tag: DeviceCard card } slider && !_sliderDragStart.ContainsKey(slider))
        {
            _sliderDragStart[slider] = (card.Volume, card.IsMuted);
        }
    }

    private void OnSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSliderNudgeKey(e.Key)) return;
        if (sender is not Slider { Tag: DeviceCard card } slider) return;
        FinaliseSliderInteraction(slider, card);
        card.PlayPing();
    }

    private void OnSliderLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Slider { Tag: DeviceCard card } slider) FinaliseSliderInteraction(slider, card);
    }

    private void FinaliseSliderInteraction(Slider slider, DeviceCard card)
    {
        if (!_sliderDragStart.TryGetValue(slider, out var start)) return;
        _sliderDragStart.Remove(slider);
        _viewModel.RecordVolumeMuteUndo(card, start.Volume, start.Muted);
    }

    private static bool IsSliderNudgeKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Home or VirtualKey.End;

    private void OnLockedSliderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceCard card }) card.PlayPing();
    }

    private void OnLockedSliderPointerPressed(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.CapturePointer(e.Pointer);

    private void OnLockedSliderPointerReleased(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);

    // ---- App chip drag source + animation ----

    private const string DragPayloadPrefix = "earmark:chip:";

    private void OnAppChipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement border) return;
        if (VisualTreeHelper.GetParent(border) is not UIElement container) return;

        var visual = ElementCompositionPreview.GetElementVisual(container);
        var compositor = visual.Compositor;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
        offset.Duration = TimeSpan.FromMilliseconds(220);
        var implicits = compositor.CreateImplicitAnimationCollection();
        implicits["Offset"] = offset;
        visual.ImplicitAnimations = implicits;
    }

    private void OnAppChipDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: AppChip chip }) return;
        if (!chip.CanDrag)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetText($"{DragPayloadPrefix}{chip.ProcessId}|{chip.SourceEndpointId}");
        args.Data.RequestedOperation = DataPackageOperation.Move;
        SetDragInProgress(true);
    }

    private void OnAppChipDropCompleted(UIElement sender, DropCompletedEventArgs args) => SetDragInProgress(false);

    /// <summary>Reveals every group container's dotted outline while a drag is in flight.</summary>
    private void SetDragInProgress(bool active)
    {
        foreach (var block in _viewModel.Blocks)
        {
            if (block is DeviceGroupCard group) group.ShowOutline = active;
        }
    }

    /// <summary>Builds the app-chip menu: chip-specific actions (hide app, close/terminate process)
    /// then a separator and the shared device menu for the chip's owner card. "Terminate" only appears
    /// while Shift is held as the menu opens. Rebuilt each open, so labels/state are always current.</summary>
    private void OnAppChipFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        if (!AllowContextMenu) { flyout.Hide(); return; }
        if ((flyout.Target as FrameworkElement)?.Tag is not AppChip chip) return;
        flyout.Items.Clear();

        var hideApp = new MenuFlyoutSubItem { Text = "Hide this app", Icon = Glyph("") };
        var onDevice = new MenuFlyoutItem { Text = "On this device", Command = chip.HideOnDeviceCommand };
        ToolTipService.SetToolTip(onDevice, "Hide this app's chip on this device only; it stays on other devices. Unhide it from Settings → App indicators.");
        var everywhere = new MenuFlyoutItem { Text = "Everywhere", Command = chip.HideCommand };
        ToolTipService.SetToolTip(everywhere, "Permanently hide this app's chip from every device. Unhide it from Settings → App indicators.");
        hideApp.Items.Add(onDevice);
        hideApp.Items.Add(everywhere);
        flyout.Items.Add(hideApp);

        if (chip.ShowProcessActions)
        {
            var close = new MenuFlyoutItem
            {
                Text = chip.CloseActionLabel,
                Icon = Glyph(""),
                Command = chip.CloseCommand,
                IsEnabled = chip.CanCloseProcess,
            };
            ToolTipService.SetToolTip(close, "Ask this app to close, like clicking its X (it can save or prompt first).");
            flyout.Items.Add(close);

            var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            if (shiftDown)
            {
                var terminate = new MenuFlyoutItem
                {
                    Text = chip.TerminateActionLabel,
                    Icon = CriticalGlyph(""),
                    Command = chip.TerminateCommand,
                    IsEnabled = chip.CanControlProcess,
                };
                MakeCritical(terminate);
                ToolTipService.SetToolTip(terminate, "Force this app to quit now. Unsaved work is lost.");
                flyout.Items.Add(terminate);
            }
        }

        if (chip.OwnerCard is { } owner)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            AppendDeviceMenuItems(flyout.Items, owner);
        }
    }

    // ---- Now-playing seek slider (freeze position while dragging, seek on release) ----

    // Grabbing the Slider's Thumb marks the pointer events handled, so plain XAML handlers never fire.
    // Wiring them with handledEventsToo:true (and on the Thumb's drag, via PointerCaptureLost for the
    // end) makes the freeze engage whether the user grabs the thumb or clicks the track.
    private void OnSeekSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekSliderPressed), handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekSliderReleased), handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekSliderReleased), handledEventsToo: true);
        slider.AddHandler(KeyDownEvent, new KeyEventHandler(OnSeekSliderKeyDown), handledEventsToo: true);
        slider.AddHandler(KeyUpEvent, new KeyEventHandler(OnSeekSliderKeyUp), handledEventsToo: true);
    }

    private void OnSeekSliderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider { Tag: NowPlayingStrip strip } slider) return;
        strip.BeginSeek();
        // Focus the slider so Escape (to cancel the drag) reaches its KeyDown while the pointer is held.
        slider.Focus(FocusState.Pointer);
    }

    private void OnSeekSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider { Tag: NowPlayingStrip strip } slider) _ = strip.EndSeekAsync(slider.Value);
    }

    private void OnSeekSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Slider { Tag: NowPlayingStrip strip }) return;
        if (e.Key == VirtualKey.Escape)
        {
            strip.CancelSeek();
            e.Handled = true;
            return;
        }
        if (IsSliderNudgeKey(e.Key)) strip.BeginSeek();
    }

    private void OnSeekSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (IsSliderNudgeKey(e.Key) && sender is Slider { Tag: NowPlayingStrip strip } slider) _ = strip.EndSeekAsync(slider.Value);
    }

    // ---- Context-menu construction (the device + app-chip menus share one item list) ----

    private void OnDeviceCardFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || Card is not { } card) return;
        if (!AllowContextMenu) { flyout.Hide(); return; }
        flyout.Items.Clear();
        AppendDeviceMenuItems(flyout.Items, card);
    }

    /// <summary>The shared device/group section: show/hide, quick-pin, customise, forget, and the
    /// group actions. Appended verbatim by the card's own flyout and the app-chip flyout, so adding an
    /// item here surfaces it in both. Items are filtered by state at build time (no Visibility bindings),
    /// since the menu is rebuilt on every open.</summary>
    private void AppendDeviceMenuItems(IList<MenuFlyoutItemBase> items, DeviceCard card)
    {
        items.Add(Item(card.HideToggleTooltip, card.HideToggleGlyph, OnDeviceVisibilityClicked, card));

        if (card.CanQuickPin)
        {
            items.Add(new MenuFlyoutItem
            {
                Text = card.QuickPinToggleLabel,
                Icon = Glyph(card.QuickPinToggleGlyph),
                Command = card.ToggleQuickPinCommand,
            });
        }

        items.Add(Item("Customise…", "", OnCustomiseClicked, card));

        if (card.ShowDisconnectedBadge)
        {
            var forget = Item("Forget device", "", OnForgetDeviceClicked, card);
            ToolTipService.SetToolTip(forget, "Stop remembering this disconnected device, clearing its saved order, group, and customisation. It returns as new if it reconnects.");
            items.Add(forget);
        }

        if (card.IsGroupMember)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(Item("Ungroup device", "", OnUngroupDeviceClicked, card));
            items.Add(new MenuFlyoutSeparator());
            items.Add(Item("Rename group", "", OnRenameGroupClicked, card));

            var delete = new MenuFlyoutItem { Text = "Delete group", Icon = CriticalGlyph(""), Tag = card };
            delete.Click += OnUngroupAllClicked;
            MakeCritical(delete);
            items.Add(delete);
        }
    }

    private static MenuFlyoutItem Item(string text, string glyph, RoutedEventHandler click, DeviceCard card)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Glyph(glyph), Tag = card };
        item.Click += click;
        return item;
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    private static FontIcon CriticalGlyph(string glyph) =>
        new() { Glyph = glyph, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };

    /// <summary>Tints a destructive item red across rest/hover/pressed (matches the XAML the menus
    /// previously hard-coded). Snapshots the current theme's brush, which is fine for a transient menu.</summary>
    private static void MakeCritical(MenuFlyoutItem item)
    {
        var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        item.Resources["MenuFlyoutItemForeground"] = critical;
        item.Resources["MenuFlyoutItemForegroundPointerOver"] = critical;
        item.Resources["MenuFlyoutItemForegroundPressed"] = critical;
    }

    // ---- Context-menu actions (the device + app-chip menus share these) ----

    private async void OnDeviceVisibilityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;
        if (!card.IsEffectivelyHidden && card.IsQuickPinned)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Hide pinned device?",
                Content = "This device is pinned to Quick Controls. Hiding it will remove it from Quick Controls.",
                PrimaryButtonText = "Hide and unpin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            HomeViewModel.HideAndUnpin(card);
            return;
        }

        card.ToggleUserVisibilityCommand.Execute(null);
    }

    private void OnForgetDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card }) _viewModel.ForgetDevice(card);
    }

    private void OnUngroupDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card }) _viewModel.UngroupDevice(card.DeviceKey);
    }

    private void OnUngroupAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card } && FindGroupOf(card) is { } group)
        {
            _viewModel.UngroupAll(group.Id);
        }
    }

    private void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card } && FindGroupOf(card) is { } group)
        {
            group.IsEditingTitle = true;
            RenameGroupRequested?.Invoke(this, group);
        }
    }

    private void OnCustomiseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;
        // Defer so the context MenuFlyout finishes dismissing before the dialog opens.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = HomePage.BuildCustomiseDialog(card);
                dialog.XamlRoot = XamlRoot;
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Customise: dialog threw");
            }
        });
    }
}
