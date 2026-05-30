using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.Controls;
using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;

namespace Earmark.App.Views;

public sealed partial class HomePage : Page
{
    private readonly ILogger<HomePage>? _logger;
    private readonly RulesViewModel _rulesViewModel;
    private readonly MainWindow _mainWindow;

    /// <summary>
    /// Pre-drag volume / mute captured per slider. Indexed by the Slider instance because
    /// the same DeviceCard could theoretically host concurrent interactions; in practice this
    /// also dodges any "card replaced mid-drag" edge cases by keying off the live control.
    /// </summary>
    private readonly Dictionary<Slider, (float Volume, bool Muted)> _sliderDragStart = new();

    public HomePage(HomeViewModel viewModel, RulesViewModel rulesViewModel, MainWindow mainWindow)
    {
        ViewModel = viewModel;
        _rulesViewModel = rulesViewModel;
        _mainWindow = mainWindow;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<HomePage>>();

        // The page + VM are singletons, so the 20Hz peak/meter poll would otherwise run for the
        // whole app lifetime. Only run it while the page is in the visual tree: this keeps its
        // UI-thread COM reads from starving the navigate-away transition and from burning CPU
        // on other pages. Loaded/Unloaded fire on every Frame content swap.
        Loaded += (_, _) => ViewModel.ResumePeakPolling();
        Unloaded += (_, _) => ViewModel.PausePeakPolling();
    }

    public HomeViewModel ViewModel { get; }

    private BlockWrapLayout? Layout => DevicesRepeater.Layout as BlockWrapLayout;

    // ---- Block reorder + move-whole-group ----
    //
    // The top level is a list of blocks (a lone DeviceCard or a DeviceGroupCard section). A reorder
    // drag lifts one block and the others slide to open a gap; the dropped block lands at the gap.
    // A group is one block, so a reorder can never split it. Drag sources: a lone card's Border, and
    // a group's title band (header handle). Payloads: "earmark:card:{endpointId}" /
    // "earmark:group:{groupId}". The drop is committed at the container (OnBlocksDrop) using the
    // layout's frozen no-gap geometry, so the insert point is stable while blocks slide.

    private const string DragPayloadCardPrefix = "earmark:card:";
    private const string DragPayloadGroupPrefix = "earmark:group:";

    /// <summary>The card being dragged (a lone card or a group member), or null. App-chip drags leave
    /// this null (they're handled per card).</summary>
    private DeviceCard? _draggedCard;

    /// <summary>The group the dragged card belongs to when it's a member; null for a lone card.</summary>
    private DeviceGroupCard? _draggedCardGroup;

    /// <summary>The group being dragged by its header handle, or null.</summary>
    private DeviceGroupCard? _draggedGroup;

    /// <summary>The lone card currently highlighted as a create-group target (accent dotted outline).</summary>
    private DeviceCard? _createTarget;

    /// <summary>The group currently highlighted as a join target (accent outline).</summary>
    private DeviceGroupCard? _joinTarget;

    /// <summary>True between a reorder drag start and its completion; gates whether newly realised
    /// blocks get the implicit slide animation attached as they scroll into view.</summary>
    private bool _reorderActive;

    /// <summary>The group's inner layout currently showing a member make-space gap (within-group
    /// reorder) or a phantom join slot, or null.</summary>
    private WrapByRowLayout? _activeInnerLayout;

    /// <summary>The inner member repeater currently carrying the implicit slide animation, or null.</summary>
    private ItemsRepeater? _animatedInner;

    private async void OnDeviceCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card } element) return;

        _draggedCard = card;
        _draggedCardGroup = card.IsGroupMember ? FindGroupOf(card) : null;
        args.Data.SetText($"{DragPayloadCardPrefix}{card.Endpoint.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        EnableReorderAnimations(true);
        SetDragInProgress(true);

        // Opaque drag bitmap (the card fill is translucent, so lifted off the backdrop it reads as
        // see-through). Render before hiding the source.
        var deferral = args.GetDeferral();
        try
        {
            var bitmap = await RenderCardOpaqueAsync(element);
            if (bitmap is not null) args.DragUI.SetContentFromSoftwareBitmap(bitmap);
        }
        catch { /* keep the default visual if the snapshot fails */ }
        finally { deferral.Complete(); }

        card.IsBeingDragged = true;
    }

    private void OnDeviceCardDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_draggedCard is not null) _draggedCard.IsBeingDragged = false;
        EndDrag();
    }

    private async void OnGroupHeaderDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: DeviceGroupCard group }) return;

        _draggedGroup = group;
        args.Data.SetText($"{DragPayloadGroupPrefix}{group.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        EnableReorderAnimations(true);
        SetDragInProgress(true);   // reveals the dotted outline + drag padding on every group

        // Drag visual: a snapshot of the group BOX (cards + title + its dotted outline), bounded to
        // the members' extent - not the full-width section. The box is the block element's first
        // child (the left-aligned, member-width Grid). Render after the outline + padding apply, and
        // before hiding the source.
        var index = ViewModel.Blocks.IndexOf(group);
        var element = index >= 0 ? DevicesRepeater.TryGetElement(index) : null;
        var box = (element as Panel)?.Children.FirstOrDefault() as FrameworkElement ?? element as FrameworkElement;
        if (box is not null)
        {
            var deferral = args.GetDeferral();
            try
            {
                box.UpdateLayout();
                var bitmap = await RenderCardOpaqueAsync(box);
                if (bitmap is not null) args.DragUI.SetContentFromSoftwareBitmap(bitmap);
            }
            catch { /* keep the default visual if the snapshot fails */ }
            finally { deferral.Complete(); }
        }

        group.IsBeingDragged = true;
    }

    private void OnGroupHeaderDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_draggedGroup is not null) _draggedGroup.IsBeingDragged = false;
        EndDrag();
    }

    // ---- Group title editing + context menu ----

    private void OnGroupTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceGroupCard group } header) return;
        group.IsEditingTitle = true;
        FocusTitleEditor(header);
        e.Handled = true;
    }

    private void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceGroupCard group }) return;
        group.IsEditingTitle = true;
        // The flyout item isn't in the header's tree; focus the editor via the realised block element.
        var idx = ViewModel.Blocks.IndexOf(group);
        if (idx >= 0 && DevicesRepeater.TryGetElement(idx) is FrameworkElement blockEl)
        {
            FocusTitleEditor(blockEl);
        }
    }

    private void OnUngroupAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceGroupCard group })
        {
            ViewModel.UngroupAll(group.Id);
        }
    }

    private void OnUngroupDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card })
        {
            ViewModel.UngroupDevice(card.Endpoint.Id);
        }
    }

    private void OnGroupTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        // Move focus off the TextBox so the two-way binding commits (via LostFocus), as if clicked away.
        DevicesRepeater.Focus(FocusState.Programmatic);
    }

    private void OnGroupTitleEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceGroupCard group })
        {
            group.IsEditingTitle = false;   // the two-way binding already committed the title on focus loss
        }
    }

    /// <summary>Focuses + selects the group's title text box once it becomes visible.</summary>
    private void FocusTitleEditor(FrameworkElement root)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindDescendant<TextBox>(root) is { } box)
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        });
    }

    /// <summary>Shared teardown for any reorder / reparent drag (committed or cancelled): drop the gap,
    /// clear highlights + outlines, detach the slide animation, and reset the dragged state.</summary>
    private void EndDrag()
    {
        _draggedCard = null;
        _draggedCardGroup = null;
        _draggedGroup = null;
        ClearHighlights();
        ClearActiveInnerGap();
        ClearInnerAnimations();
        Layout?.ClearReorderState();
        SetDragInProgress(false);
        EnableReorderAnimations(false);
    }

    private void OnBlocksDragOver(object sender, DragEventArgs e)
    {
        if (Layout is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var point = e.GetPosition(DevicesRepeater);

        // Whole-group reorder (dragging the header).
        if (_draggedGroup is not null)
        {
            ClearHighlights();
            SetReorderGap(_draggedGroup, point);
            SetDragCaption(e, "Move group");
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
            return;
        }

        if (_draggedCard is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (_draggedCardGroup is not null)
        {
            // Member drag: inside its group box -> reorder within (members bump to show the gap);
            // outside -> leave.
            var box = Layout.GetContentRect(ViewModel.Blocks.IndexOf(_draggedCardGroup));
            ClearHighlights();
            Layout.ClearReorderState();   // member moves don't open a block-level gap

            if (box.Contains(point)
                && InnerRepeaterOf(_draggedCardGroup) is { Layout: WrapByRowLayout innerLayout } inner)
            {
                EnsureInnerAnimations(inner);
                var draggedIdx = _draggedCardGroup.Members.IndexOf(_draggedCard);
                var raw = InnerInsertionIndex(inner, innerLayout, point);
                innerLayout.SetReorderState(draggedIdx, raw > draggedIdx ? raw - 1 : raw);
                SetActiveInnerGap(innerLayout);
                SetDragCaption(e, "Move within group");
            }
            else
            {
                ClearActiveInnerGap();   // left the box - close the in-group gap
                SetDragCaption(e, "Remove from group");
            }

            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
            return;
        }

        // Lone card: create (onto a card's centre) / join (onto a group) / reorder (elsewhere).
        var targetIdx = Layout.GetBlockIndexAt(point);
        var target = targetIdx >= 0 && targetIdx < ViewModel.Blocks.Count ? ViewModel.Blocks[targetIdx] : null;

        if (target is DeviceGroupCard joinGroup && ResolveGroupIntent(targetIdx, point) == GroupDropIntent.Join)
        {
            Layout.ClearReorderState();
            ClearCreateTarget();
            SetJoinTarget(joinGroup);
            // Open a phantom slot in the group so its members bump to preview where the card lands.
            if (InnerRepeaterOf(joinGroup) is { Layout: WrapByRowLayout joinLayout } joinInner)
            {
                EnsureInnerAnimations(joinInner);
                joinLayout.SetPhantomGap(InnerInsertionIndex(joinInner, joinLayout, point));
                SetActiveInnerGap(joinLayout);
            }
            SetDragCaption(e, "Add to group");
        }
        else if (target is DeviceGroupCard)
        {
            // Top / bottom strip of a group section = insert the card before / after the group (a
            // block reorder). This is the only way to drop above a first-in-row group.
            ClearHighlights();
            ClearActiveInnerGap();
            var src = ViewModel.Blocks.IndexOf(_draggedCard);
            var insertIdx = ResolveGroupIntent(targetIdx, point) == GroupDropIntent.Before ? targetIdx : targetIdx + 1;
            if (src >= 0) Layout.SetReorderState(src, ToCompactIndex(insertIdx, src));
            SetDragCaption(e, "Move");
        }
        else if (target is DeviceCard targetCard
                 && !ReferenceEquals(targetCard, _draggedCard)
                 && IsCentreZone(point, Layout.GetContentRect(targetIdx)))
        {
            Layout.ClearReorderState();
            ClearActiveInnerGap();
            ClearJoinTarget();
            SetCreateTarget(targetCard);
            SetDragCaption(e, "Group");
        }
        else
        {
            ClearHighlights();
            ClearActiveInnerGap();
            SetReorderGap(_draggedCard, point);
            SetDragCaption(e, "Move");
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void OnBlocksDrop(object sender, DragEventArgs e)
    {
        if (Layout is null) return;
        var point = e.GetPosition(DevicesRepeater);
        e.Handled = true;

        // Whole-group reorder.
        if (_draggedGroup is not null)
        {
            var groupSource = ViewModel.Blocks.IndexOf(_draggedGroup);
            Layout.ClearReorderState();
            if (groupSource >= 0)
            {
                _logger?.LogInformation("Group reorder: {Group}", _draggedGroup.Id);
                ViewModel.ReorderBlock(_draggedGroup.Id, ToCompactIndex(Layout.GetInsertionIndex(point), groupSource));
            }
            return;
        }

        if (_draggedCard is null) return;
        var sourceId = _draggedCard.Endpoint.Id;

        // Member drag: leave the group (with disband confirm) or reorder within it.
        if (_draggedCardGroup is not null)
        {
            var group = _draggedCardGroup;
            var box = Layout.GetContentRect(ViewModel.Blocks.IndexOf(group));
            Layout.ClearReorderState();
            ClearHighlights();

            if (box.Contains(point))
            {
                var anchor = MemberAnchorBefore(group, point, sourceId);
                _logger?.LogInformation("Reorder within group {Group}: {Member}", group.Id, sourceId);
                ViewModel.ReorderWithinGroup(sourceId, anchor);
            }
            else
            {
                var anchorId = InsertionAnchorBlockId(point);
                if (ViewModel.GroupMemberCount(group.Id) <= 2 && !await ConfirmDisbandAsync())
                {
                    return;   // cancelled - the member stays in the group
                }
                _logger?.LogInformation("Leave group {Group}: {Member}", group.Id, sourceId);
                ViewModel.RemoveFromGroup(sourceId, anchorId);
            }
            return;
        }

        // Lone card: create / join / reorder.
        var targetIdx = Layout.GetBlockIndexAt(point);
        var target = targetIdx >= 0 && targetIdx < ViewModel.Blocks.Count ? ViewModel.Blocks[targetIdx] : null;
        Layout.ClearReorderState();
        ClearHighlights();

        if (target is DeviceGroupCard joinGroup)
        {
            var intent = ResolveGroupIntent(targetIdx, point);
            if (intent == GroupDropIntent.Join)
            {
                var anchor = MemberAnchorBefore(joinGroup, point, draggedMemberId: null);
                _logger?.LogInformation("Join group {Group}: {Source}", joinGroup.Id, sourceId);
                ViewModel.AddToGroup(sourceId, joinGroup.Id, anchor);
            }
            else
            {
                // Top / bottom strip: reorder the card before / after the group block.
                var src = ViewModel.Blocks.IndexOf(_draggedCard);
                var insertIdx = intent == GroupDropIntent.Before ? targetIdx : targetIdx + 1;
                if (src >= 0)
                {
                    _logger?.LogInformation("Reorder around group {Group}: {Source} ({Intent})", joinGroup.Id, sourceId, intent);
                    ViewModel.ReorderBlock(sourceId, ToCompactIndex(insertIdx, src));
                }
            }
            return;
        }
        if (target is DeviceCard targetCard
            && !ReferenceEquals(targetCard, _draggedCard)
            && IsCentreZone(point, Layout.GetContentRect(targetIdx)))
        {
            _logger?.LogInformation("Create group: {Source} + {Target}", sourceId, targetCard.Endpoint.Id);
            ViewModel.CreateGroup(sourceId, targetCard.Endpoint.Id);
            return;
        }

        var cardSource = ViewModel.Blocks.IndexOf(_draggedCard);
        if (cardSource >= 0)
        {
            _logger?.LogInformation("Block reorder: {Source}", sourceId);
            ViewModel.ReorderBlock(sourceId, ToCompactIndex(Layout.GetInsertionIndex(point), cardSource));
        }
    }

    /// <summary>Opens the block-level make-space gap for <paramref name="block"/> at the pointer.</summary>
    private void SetReorderGap(object block, Point point)
    {
        var source = ViewModel.Blocks.IndexOf(block);
        if (source < 0) { Layout?.ClearReorderState(); return; }
        Layout?.SetReorderState(source, ToCompactIndex(Layout.GetInsertionIndex(point), source));
    }

    /// <summary>The block id to insert before for a member leaving its group (null = at the end).</summary>
    private string? InsertionAnchorBlockId(Point point)
    {
        var raw = Layout!.GetInsertionIndex(point);
        return raw >= 0 && raw < ViewModel.Blocks.Count ? PageBlockId(ViewModel.Blocks[raw]) : null;
    }

    /// <summary>The group's inner member repeater, or null if not realised.</summary>
    private ItemsRepeater? InnerRepeaterOf(DeviceGroupCard group)
    {
        var blockIndex = ViewModel.Blocks.IndexOf(group);
        return blockIndex >= 0 && DevicesRepeater.TryGetElement(blockIndex) is FrameworkElement blockEl
            ? FindDescendant<ItemsRepeater>(blockEl)
            : null;
    }

    /// <summary>The member insertion index ([0, memberCount]) for a pointer, using the inner layout's
    /// stable no-gap geometry (so an open gap / phantom doesn't make the answer jitter).</summary>
    private int InnerInsertionIndex(ItemsRepeater inner, WrapByRowLayout layout, Point point)
    {
        var local = DevicesRepeater.TransformToVisual(inner).TransformPoint(point);
        return layout.GetInsertionIndex(local);
    }

    /// <summary>The member endpoint id the pointer sits before within <paramref name="group"/> (null =
    /// at the end), skipping <paramref name="draggedMemberId"/> if set (within-group reorder).</summary>
    private string? MemberAnchorBefore(DeviceGroupCard group, Point point, string? draggedMemberId)
    {
        if (InnerRepeaterOf(group) is not { Layout: WrapByRowLayout layout } inner) return null;
        var raw = InnerInsertionIndex(inner, layout, point);
        for (var k = raw; k < group.Members.Count; k++)
        {
            var id = group.Members[k].Endpoint.Id;
            if (draggedMemberId is null || !string.Equals(id, draggedMemberId, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>Switches which inner layout is showing a member gap / phantom, clearing the previous.</summary>
    private void SetActiveInnerGap(WrapByRowLayout layout)
    {
        if (ReferenceEquals(_activeInnerLayout, layout)) return;
        _activeInnerLayout?.ClearReorderState();
        _activeInnerLayout = layout;
    }

    private void ClearActiveInnerGap()
    {
        _activeInnerLayout?.ClearReorderState();
        _activeInnerLayout = null;
    }

    /// <summary>Attaches the implicit slide animation to a group's member elements so they bump
    /// smoothly when the gap / phantom moves. One inner repeater is animated per drag.</summary>
    private void EnsureInnerAnimations(ItemsRepeater inner)
    {
        if (ReferenceEquals(_animatedInner, inner)) return;
        ClearInnerAnimations();
        _animatedInner = inner;
        var count = inner.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (inner.TryGetElement(i) is UIElement el) ApplyReorderAnimation(el, true);
        }
    }

    private void ClearInnerAnimations()
    {
        if (_animatedInner is null) return;
        var count = _animatedInner.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (_animatedInner.TryGetElement(i) is UIElement el) ApplyReorderAnimation(el, false);
        }
        _animatedInner = null;
    }

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

    private static string? PageBlockId(object block) => block switch
    {
        DeviceCard card => card.Endpoint.Id,
        DeviceGroupCard group => group.Id,
        _ => null,
    };

    private DeviceGroupCard? FindGroupOf(DeviceCard card)
    {
        foreach (var block in ViewModel.Blocks)
        {
            if (block is DeviceGroupCard group && group.Members.Contains(card)) return group;
        }
        return null;
    }

    /// <summary>Inner 40% of a rect counts as "centre" (30% inset each side); the surrounding frame
    /// reads as reorder so the two gestures don't fight at the boundary.</summary>
    private static bool IsCentreZone(Point p, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        var insetX = r.Width * 0.3;
        var insetY = r.Height * 0.3;
        return p.X >= r.Left + insetX && p.X <= r.Right - insetX
            && p.Y >= r.Top + insetY && p.Y <= r.Bottom - insetY;
    }

    private enum GroupDropIntent { Before, Join, After }

    /// <summary>For a lone card dragged over a group section: the thin top strip (its title band) =
    /// insert before, the bottom strip = insert after, the middle = join. The top strip is the only
    /// way to drop a card above a first-in-row group (nothing sits above it to aim at).</summary>
    private GroupDropIntent ResolveGroupIntent(int groupIndex, Point point)
    {
        var r = Layout!.GetContentRect(groupIndex);
        if (r.Height <= 0) return GroupDropIntent.Join;
        var edge = Math.Min(28.0, r.Height * 0.25);
        if (point.Y < r.Top + edge) return GroupDropIntent.Before;
        if (point.Y > r.Bottom - edge) return GroupDropIntent.After;
        return GroupDropIntent.Join;
    }

    private void SetCreateTarget(DeviceCard card)
    {
        if (ReferenceEquals(_createTarget, card)) return;
        ClearCreateTarget();
        _createTarget = card;
        card.IsGroupDropTarget = true;
    }

    private void ClearCreateTarget()
    {
        if (_createTarget is null) return;
        _createTarget.IsGroupDropTarget = false;
        _createTarget = null;
    }

    private void SetJoinTarget(DeviceGroupCard group)
    {
        if (ReferenceEquals(_joinTarget, group)) return;
        ClearJoinTarget();
        _joinTarget = group;
        group.IsJoinTarget = true;
    }

    private void ClearJoinTarget()
    {
        if (_joinTarget is null) return;
        _joinTarget.IsJoinTarget = false;
        _joinTarget = null;
    }

    private void ClearHighlights()
    {
        ClearCreateTarget();
        ClearJoinTarget();
    }

    /// <summary>Confirms a group disband (removing this member leaves only one). Returns true to proceed.</summary>
    private async Task<bool> ConfirmDisbandAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Disband group?",
            Content = "Removing this device leaves the group with a single device, so the group will be disbanded.",
            PrimaryButtonText = "Disband",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Compacts a raw "insert before block index" into the source-excluded space the gap and
    /// <see cref="HomeViewModel.ReorderBlock"/> both use.</summary>
    private int ToCompactIndex(int raw, int source)
    {
        var compact = raw > source ? raw - 1 : raw;
        return Math.Clamp(compact, 0, Math.Max(0, ViewModel.Blocks.Count - 1));
    }

    /// <summary>Shows a drag caption (e.g. "Move", "Move group") on the OS drag cursor.</summary>
    private static void SetDragCaption(DragEventArgs e, string caption)
    {
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    /// <summary>Attaches (or removes) a Composition implicit Offset animation on each realised block so
    /// any layout re-arrange slides smoothly. On only during a reorder drag.</summary>
    private void EnableReorderAnimations(bool enable)
    {
        _reorderActive = enable;
        for (var i = 0; i < ViewModel.Blocks.Count; i++)
        {
            if (DevicesRepeater.TryGetElement(i) is UIElement element)
            {
                ApplyReorderAnimation(element, enable);
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

    private void OnBlockElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_reorderActive) ApplyReorderAnimation(args.Element, true);
    }

    private void OnUndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.UndoVisibilityChangeCommand.Execute(null);
        args.Handled = true;
    }

    private void OnMuteToggleClicked(object sender, RoutedEventArgs e)
    {
        // ItemsRepeater doesn't propagate DataContext to x:Bind templates - the button
        // carries the DeviceCard via Tag="{x:Bind}" instead.
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        var prevVolume = card.Volume;
        var prevMuted = card.IsMuted;
        card.ToggleMuteCommand.Execute(null);
        // Mute icon clicks only change IsMuted; carry the unchanged volume so Ctrl+Z
        // restores both together as one entry.
        ViewModel.RecordVolumeMuteUndo(card, prevVolume, prevMuted);
    }

    private void OnRuleChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleSummary summary }) return;

        _rulesViewModel.RequestFocusRule(summary.RuleId);
        _mainWindow.NavigateByTag("Rules");
    }

    // CA1822 suppressed: XAML event hookup requires instance methods even when the body
    // doesn't touch instance state.
#pragma warning disable CA1822

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
        if (sender is Slider { Tag: DeviceCard card } slider &&
            !_sliderDragStart.ContainsKey(slider))
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
        // Belt-and-suspenders: if focus moves away mid-interaction (e.g. window deactivated),
        // commit whatever change we have so the undo entry isn't lost.
        if (sender is Slider { Tag: DeviceCard card } slider)
        {
            FinaliseSliderInteraction(slider, card);
        }
    }

    private void FinaliseSliderInteraction(Slider slider, DeviceCard card)
    {
        if (!_sliderDragStart.TryGetValue(slider, out var start)) return;
        _sliderDragStart.Remove(slider);
        ViewModel.RecordVolumeMuteUndo(card, start.Volume, start.Muted);
    }

    private static bool IsSliderNudgeKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Home or VirtualKey.End;

    private void OnLockedSliderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceCard card })
        {
            card.PlayPing();
        }
    }

    // A rule-locked (disabled) slider doesn't capture the pointer the way an enabled one does, so
    // a press-drag over it would otherwise bubble to the card's CanDrag and start a reorder. The
    // transparent lock overlay captures the pointer on press (mirroring the enabled slider) to keep
    // the gesture off the card; the tooltip and tap-to-ping still work.
    private void OnLockedSliderPointerPressed(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.CapturePointer(e.Pointer);

    private void OnLockedSliderPointerReleased(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);

#pragma warning restore CA1822

    // ---- App chip drag / drop ----
    //
    // In-process drag of an AppChip onto a render DeviceCard rebinds the session's per-app
    // default endpoint. The DataPackage Text carries an "earmark:chip:{pid}:{sourceEndpointId}"
    // sentinel; the Drop handler parses it back into a chip + target card and asks the VM
    // to apply the override via IAudioPolicyService.
    //
    // Cursor feedback is OS-native via DataPackageOperation.None - WinUI draws the slashed
    // circle the user expects when DragOver decides the drop isn't valid (capture endpoint
    // target, or dropping back on the source card). No custom cursor work needed.

    private const string DragPayloadPrefix = "earmark:chip:";

    private void OnAppChipDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: AppChip chip }) return;
        if (!chip.CanDrag)
        {
            args.Cancel = true;
            return;
        }

        // Payload is parsed in OnDeviceCardDrop. Keep it small; the AppChip itself doesn't
        // have to round-trip - the page resolves PID + source endpoint back to the live chip
        // via the HomeViewModel's card list, which is the source of truth.
        var payload = $"{DragPayloadPrefix}{chip.ProcessId}|{chip.SourceEndpointId}";
        args.Data.SetText(payload);
        args.Data.RequestedOperation = DataPackageOperation.Move;

        SetDragInProgress(true);
    }

    private void OnAppChipDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        SetDragInProgress(false);
    }

    /// <summary>Reveals every group container's dotted outline while a drag is in flight, so groups
    /// read as transparent at rest and show their bounds only while dragging.</summary>
    private void SetDragInProgress(bool active)
    {
        foreach (var block in ViewModel.Blocks)
        {
            if (block is DeviceGroupCard group) group.ShowOutline = active;
        }
    }

    private void OnDeviceCardDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        // A card / group drag (our own) is positioned at the container (OnBlocksDragOver); bubble
        // immediately without touching the DataView so the blocking payload read only runs for chips.
        if (_draggedCard is not null || _draggedGroup is not null) return;

        // Bail early when the drag isn't ours. Other drags (file drops onto the window, etc.)
        // shouldn't get our acceptance.
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var text = TryReadText(e.DataView);
        if (text is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (TryParseChipPayload(text, out var pid, out var sourceEndpointId))
        {
            _ = pid;
            // Capture endpoint -> cursor shows slashed circle. Same goes for dropping on the
            // source card (no-op). Anything else accepts as Move.
            if (card.IsCapture ||
                string.Equals(card.Endpoint.Id, sourceEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.Move;
            }
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void OnDeviceCardDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        // Card / group drops are committed at the container (OnBlocksDrop); bubble immediately.
        if (_draggedCard is not null || _draggedGroup is not null) return;

        var text = TryReadText(e.DataView);
        if (text is null) return;

        if (card.IsCapture) return;
        if (!TryParseChipPayload(text, out var pid, out var sourceEndpointId)) return;
        if (string.Equals(card.Endpoint.Id, sourceEndpointId, StringComparison.OrdinalIgnoreCase)) return;

        var chip = FindChipByPid(pid);
        if (chip is null)
        {
            _logger?.LogInformation("Drop: chip with pid={Pid} no longer present, ignoring", pid);
            return;
        }

        _logger?.LogInformation(
            "Drop: pid={Pid} {Source} -> {Target}",
            pid, sourceEndpointId, card.Endpoint.Id);
        ViewModel.MoveSessionToEndpoint(chip, card.Endpoint);
        e.Handled = true;
    }

    private AppChip? FindChipByPid(uint pid)
    {
        foreach (var card in ViewModel.VisibleCards)
        {
            foreach (var chip in card.Apps)
            {
                if (chip.ProcessId == pid) return chip;
            }
        }
        return null;
    }

    /// <summary>Reads the in-process drag payload text once. GetTextAsync is async; the
    /// DragOver/Drop handlers can't await without losing the synchronous accept decision, so we
    /// block on it - the DataPackage source is in-process and already resolved. Returns null when
    /// there's no text or it can't be read.</summary>
    private static string? TryReadText(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.Text)) return null;
        try
        {
            return view.GetTextAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseChipPayload(string text, out uint pid, out string sourceEndpointId)
    {
        pid = 0;
        sourceEndpointId = string.Empty;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(DragPayloadPrefix, StringComparison.Ordinal)) return false;

        var body = text.Substring(DragPayloadPrefix.Length);
        var sep = body.IndexOf('|');
        if (sep <= 0 || sep == body.Length - 1) return false;
        if (!uint.TryParse(body.AsSpan(0, sep), System.Globalization.CultureInfo.InvariantCulture, out pid)) return false;
        sourceEndpointId = body.Substring(sep + 1);
        return true;
    }

    /// <summary>Renders the card to an opaque bitmap for use as the drag visual. The card's own
    /// fill is a translucent layer brush, so each premultiplied pixel is composited over the
    /// theme's solid background colour to make the lifted card read as solid. The card's rounded
    /// corners are then re-applied as an alpha mask: compositing over an opaque base fills the
    /// transparent corner cut-outs with solid colour and squares the card off, so we punch them
    /// back out.</summary>
    private static async Task<SoftwareBitmap?> RenderCardOpaqueAsync(FrameworkElement element)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var w = rtb.PixelWidth;
        var h = rtb.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        var bytes = (await rtb.GetPixelsAsync()).ToArray();   // BGRA8, premultiplied alpha
        var baseColor = element.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 0xF3, 0xF3, 0xF3)           // SolidBackgroundFillColorBase (light)
            : Color.FromArgb(255, 0x20, 0x20, 0x20);          // SolidBackgroundFillColorBase (dark)

        // Corner radius in physical pixels: the card's DIP radius scaled by the render's
        // rasterization scale (rendered pixel width / layout width).
        var radiusDip = (element as Border)?.CornerRadius.TopLeft ?? 8.0;
        var scale = element.ActualWidth > 0 ? w / element.ActualWidth : 1.0;
        var radius = radiusDip * scale;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var coverage = RoundedRectCoverage(x + 0.5, y + 0.5, w, h, radius);
                var a = bytes[i + 3];
                if (a == 255 && coverage >= 1.0) continue;   // opaque interior - leave it

                // Composite the (premultiplied) source over the opaque base, then re-premultiply
                // by the corner coverage so the rounded cut-outs stay transparent.
                var inv = 255 - a;
                var b = bytes[i + 0] + baseColor.B * inv / 255.0;
                var g = bytes[i + 1] + baseColor.G * inv / 255.0;
                var r = bytes[i + 2] + baseColor.R * inv / 255.0;
                bytes[i + 0] = (byte)(b * coverage);
                bytes[i + 1] = (byte)(g * coverage);
                bytes[i + 2] = (byte)(r * coverage);
                bytes[i + 3] = (byte)(255 * coverage);
            }
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }

    /// <summary>Anti-aliased coverage [0,1] of a pixel centre against a rounded rectangle: 1 over
    /// the straight edges and interior, a soft ramp across each corner arc, 0 outside it.</summary>
    private static double RoundedRectCoverage(double px, double py, double w, double h, double r)
    {
        if (r <= 0) return 1.0;
        // Pick the nearest corner-arc centre; bail to full coverage on the straight-edge bands.
        double cx;
        if (px < r) cx = r; else if (px > w - r) cx = w - r; else return 1.0;
        double cy;
        if (py < r) cy = r; else if (py > h - r) cy = h - r; else return 1.0;
        var dx = px - cx;
        var dy = py - cy;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(r - dist + 0.5, 0.0, 1.0);
    }
}
