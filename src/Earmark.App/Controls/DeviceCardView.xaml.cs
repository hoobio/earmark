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

using Windows.ApplicationModel.DataTransfer;
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
        InitializeComponent();
    }

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(DeviceCard), typeof(DeviceCardView), new PropertyMetadata(null));

    public DeviceCard? Card
    {
        get => (DeviceCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
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

    /// <summary>x:Bind function: a rule element shows only when rules are enabled for this host AND the
    /// card wants it. Re-evaluates when either argument changes, replacing the old collapse watchdog.</summary>
    public Visibility RuleVis(bool showRules, bool cardWants) =>
        showRules && cardWants ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Reveals the chip's "Terminate this app" item only while Shift is held as the menu opens.</summary>
    private void OnAppChipFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem { Tag: AppChip chip } terminate)
            {
                terminate.Visibility = shiftDown && chip.ShowProcessActions
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
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
