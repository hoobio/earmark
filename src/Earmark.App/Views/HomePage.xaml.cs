using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.Controls;
using Earmark.App.ViewModels;
using Earmark.Core.Models;

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

    /// <summary>Per-group editable title chrome, positioned above each group's first row segment.
    /// Stable across layout passes so an in-progress title edit keeps focus.</summary>
    public ObservableCollection<GroupOverlay> GroupOverlays { get; } = new();

    /// <summary>Per-row dotted outline segments (one per row a group spans), reconciled in place
    /// from the layout's group segments. Non-interactive.</summary>
    public ObservableCollection<GroupOutlineSegment> GroupOutlines { get; } = new();

    public HomePage(HomeViewModel viewModel, RulesViewModel rulesViewModel, MainWindow mainWindow)
    {
        ViewModel = viewModel;
        _rulesViewModel = rulesViewModel;
        _mainWindow = mainWindow;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<HomePage>>();

        // Feed group membership into the layout (for the title band) and keep the overlay chrome
        // positioned as the grid re-arranges. The layout's Arranged event is more reliable than
        // LayoutUpdated, which can miss the gap re-arrange during a Composition-animated drag.
        if (Layout is not null)
        {
            Layout.GroupInfo = new GroupLayoutInfo(ViewModel);
            Layout.Arranged += OnLayoutArranged;
        }
        ViewModel.GroupInfosChanged += OnGroupInfosChanged;

        // The page + VM are singletons, so the 20Hz peak/meter poll would otherwise run for the
        // whole app lifetime. Only run it while the page is in the visual tree: this keeps its
        // UI-thread COM reads from starving the navigate-away transition and from burning CPU
        // on other pages. Loaded/Unloaded fire on every Frame content swap.
        Loaded += (_, _) => ViewModel.ResumePeakPolling();
        Unloaded += (_, _) => ViewModel.PausePeakPolling();
    }

    public HomeViewModel ViewModel { get; }

    /// <summary>Adapts the view-model's per-card group ids + group infos to the layout's group hook.</summary>
    private sealed class GroupLayoutInfo(HomeViewModel vm) : IGroupLayoutInfo
    {
        public string? GroupIdForIndex(int index) =>
            index >= 0 && index < vm.Devices.Count ? vm.Devices[index].GroupId : null;

        public bool IsDedicatedRow(string groupId) =>
            vm.GroupInfos.TryGetValue(groupId, out var info) && info.DedicatedRow;
    }

    // Arranged fires at the end of the layout's arrange pass. Reposition titles + outlines
    // synchronously so they track the group every frame of a drag. Adding/removing title overlays is
    // left to the (dispatched) full reconcile, which can't safely mutate the bound collection
    // mid-layout; outline segments are non-interactive so they reconcile in place here.
    private void OnLayoutArranged()
    {
        UpdateGroupOverlayPositions();
        if (NeedsOverlayReconcile())
        {
            DispatcherQueue.TryEnqueue(SyncGroupOverlays);
        }
    }

    // The group set changed (created / joined / ungrouped / disbanded). Re-measure the layout so the
    // title bands + outlines recompute even when the card order didn't change, then reconcile chrome.
    private void OnGroupInfosChanged()
    {
        Layout?.RefreshLayout();
        SyncGroupOverlays();
    }

    /// <summary>True if the set of title overlays no longer matches the present groups, so a full
    /// reconcile is needed.</summary>
    private bool NeedsOverlayReconcile()
    {
        var segs = Layout?.GroupSegments;
        if (segs is null) return false;
        var present = 0;
        foreach (var (id, _) in segs)
        {
            if (!ViewModel.GroupInfos.ContainsKey(id)) continue;
            present++;
            if (GroupOverlays.All(o => o.Group.Id != id)) return true;   // a group with no title overlay yet
        }
        return present != GroupOverlays.Count;
    }

    /// <summary>Repositions each title above its group's first row segment and rebuilds the per-row
    /// outline segments. Safe to call inside the arrange pass (positions only on titles).</summary>
    private void UpdateGroupOverlayPositions()
    {
        var segs = Layout?.GroupSegments;
        if (segs is null) return;
        foreach (var overlay in GroupOverlays)
        {
            if (!segs.TryGetValue(overlay.Group.Id, out var list) || list.Count == 0) continue;
            var first = list[0];
            overlay.X = first.Left;
            overlay.Y = first.Top - WrapByRowLayout.TitleBandHeight;
            overlay.Width = first.Width;
        }
        RebuildOutlines();
    }

    /// <summary>Reconciles the flat per-row outline-segment collection against the layout's group
    /// segments + the current drag / join-target state, updating in place by index to avoid churn.</summary>
    private void RebuildOutlines()
    {
        var segs = Layout?.GroupSegments;
        if (segs is null) return;
        var infos = ViewModel.GroupInfos;

        var desired = new List<(string Gid, Rect Rect)>();
        foreach (var (gid, list) in segs)
        {
            if (!infos.ContainsKey(gid)) continue;
            foreach (var rect in list) desired.Add((gid, rect));
        }

        while (GroupOutlines.Count > desired.Count) GroupOutlines.RemoveAt(GroupOutlines.Count - 1);
        while (GroupOutlines.Count < desired.Count) GroupOutlines.Add(new GroupOutlineSegment());

        for (var i = 0; i < desired.Count; i++)
        {
            var (gid, rect) = desired[i];
            var seg = GroupOutlines[i];
            seg.GroupId = gid;
            seg.X = rect.Left;
            seg.Y = rect.Top;
            seg.Width = rect.Width;
            seg.Height = rect.Height;
            seg.ShowOutline = _dragInProgress;
            seg.IsJoinTarget = _joinTargetGroupId is not null
                && string.Equals(gid, _joinTargetGroupId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Full reconcile of the per-group title overlays: adds for new groups, drops gone ones,
    /// repositions the rest (so an in-progress title edit keeps focus). Not called mid-layout.</summary>
    private void SyncGroupOverlays()
    {
        var segs = Layout?.GroupSegments;
        if (segs is null) return;
        var infos = ViewModel.GroupInfos;

        for (var i = GroupOverlays.Count - 1; i >= 0; i--)
        {
            var id = GroupOverlays[i].Group.Id;
            if (!segs.ContainsKey(id) || !infos.ContainsKey(id))
            {
                GroupOverlays.RemoveAt(i);
            }
        }

        foreach (var (id, _) in segs)
        {
            if (!infos.TryGetValue(id, out var info)) continue;
            if (GroupOverlays.All(o => o.Group.Id != id))
            {
                GroupOverlays.Add(new GroupOverlay(info));
            }
        }

        UpdateGroupOverlayPositions();
    }

    /// <summary>Tracks whether any drag is in flight; outlines show only during a drag, so groups
    /// read as transparent at rest and reveal their bounds only while dragging.</summary>
    private void SetDragInProgress(bool active)
    {
        _dragInProgress = active;
        RebuildOutlines();
    }

    private void OnGroupTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            // Move focus off the TextBox so the two-way binding commits (LostFocus), as if clicked away.
            DevicesRepeater.Focus(FocusState.Programmatic);
        }
    }

    private void OnUngroupAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupOverlay overlay })
        {
            ViewModel.UngroupAll(overlay.Group.Id);
        }
    }

    private void OnUngroupDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card })
        {
            ViewModel.UngroupDevice(card.Endpoint.Id);
        }
    }

    // ---- Group title: drag to move the whole group, double-click / context to rename ----

    private void OnGroupTitleDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: GroupOverlay overlay }) return;
        var members = GroupMemberCards(overlay.Group.Id);
        if (members.Count < 2)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetText($"{DragPayloadGroupPrefix}{overlay.Group.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        _draggedGroupId = overlay.Group.Id;
        _draggedGroupCards = members;
        foreach (var card in members) card.IsBeingDragged = true;   // lift the whole block out of flow
        EnableReorderAnimations(true);
        SetDragInProgress(true);
    }

    private void OnGroupTitleDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_draggedGroupCards is not null)
        {
            foreach (var card in _draggedGroupCards) card.IsBeingDragged = false;
        }
        _draggedGroupCards = null;
        _draggedGroupId = null;
        Layout?.ClearReorderState();
        ClearGroupHighlights();
        SetDragInProgress(false);
        EnableReorderAnimations(false);
    }

    private void OnGroupTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GroupOverlay overlay } element) return;
        overlay.IsEditing = true;
        FocusTitleEditor(element, overlay);
        e.Handled = true;
    }

    private void OnGroupTitleEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupOverlay overlay })
        {
            overlay.IsEditing = false;   // two-way binding already committed the title on focus loss
        }
    }

    private void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GroupOverlay overlay }) return;
        overlay.IsEditing = true;
        if (GroupTitleHost.ContainerFromItem(overlay) is FrameworkElement container)
        {
            FocusTitleEditor(container, overlay);
        }
    }

    /// <summary>Focuses (and selects) the title text box once it becomes visible.</summary>
    private void FocusTitleEditor(FrameworkElement container, GroupOverlay overlay)
    {
        _ = overlay;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindDescendant<TextBox>(container) is { } box)
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        });
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

    /// <summary>Member cards of a group, in member (left-to-right) order, present in the visible list.</summary>
    private List<DeviceCard> GroupMemberCards(string groupId) =>
        ViewModel.Devices
            .Where(c => string.Equals(c.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Visible-list indices of a group's members, in order.</summary>
    private List<int> GroupMemberIndices(string groupId)
    {
        var list = new List<int>();
        for (var i = 0; i < ViewModel.Devices.Count; i++)
        {
            if (string.Equals(ViewModel.Devices[i].GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(i);
            }
        }
        return list;
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
    private const string DragPayloadCardPrefix = "earmark:card:";
    private const string DragPayloadGroupPrefix = "earmark:group:";

    /// <summary>The card being dragged for a reorder, captured on drag start so DragOver can
    /// compute the live gap position relative to it. Null when no card reorder is in flight.</summary>
    private DeviceCard? _draggedCard;

    /// <summary>Group id being dragged (by its title) to move the whole group, or null.</summary>
    private string? _draggedGroupId;

    /// <summary>The lifted member cards during a whole-group drag, to restore on completion.</summary>
    private List<DeviceCard>? _draggedGroupCards;

    /// <summary>True between a card-reorder drag start and its completion. Gates whether newly
    /// realized cards get the implicit slide animation attached as they scroll into view.</summary>
    private bool _reorderActive;

    /// <summary>The card currently highlighted as a group drop target (centre-hover), or null.</summary>
    private DeviceCard? _groupDropTargetCard;

    /// <summary>Group id currently highlighted (accent outline) as the join drop target, or null.</summary>
    private string? _joinTargetGroupId;

    /// <summary>True while any card / chip drag is in flight. Drives the group outlines: shown only
    /// during a drag so groups blend into the backdrop at rest.</summary>
    private bool _dragInProgress;

    private WrapByRowLayout? Layout => DevicesRepeater.Layout as WrapByRowLayout;

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

        // Reveal group outlines while any item is being dragged.
        SetDragInProgress(true);
    }

    private void OnAppChipDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        SetDragInProgress(false);
    }

    // ---- Device card reorder drag ----
    //
    // The card Border is both a drag source (reorder) and a drop target (app chips + reorder).
    // CanDrag="True" yields the drag to interactive children that capture the pointer (slider,
    // mute button, app chips, rule chips) while a grab on the card background / labels starts a
    // reorder. Payload is "earmark:card:{endpointId}"; the Drop handler disambiguates strictly
    // by prefix against the chip payload above.

    private async void OnDeviceCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card } element) return;
        args.Data.SetText($"{DragPayloadCardPrefix}{card.Endpoint.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        _draggedCard = card;
        // Attach the implicit slide animation to every realized card so the neighbours animate as
        // they flow around the gap (the "make space" affordance), and reveal group outlines.
        EnableReorderAnimations(true);
        SetDragInProgress(true);

        // The default drag bitmap is translucent: the card fill is a semi-transparent layer brush
        // meant to sit over Mica, so lifted off the backdrop it reads as see-through. Render an
        // opaque snapshot and use that as the drag visual instead. Render BEFORE hiding the source
        // so the snapshot captures the visible card.
        var deferral = args.GetDeferral();
        try
        {
            var bitmap = await RenderCardOpaqueAsync(element);
            if (bitmap is not null)
            {
                args.DragUI.SetContentFromSoftwareBitmap(bitmap);
            }
        }
        catch
        {
            // Keep the default (translucent) visual if the snapshot fails.
        }
        finally
        {
            deferral.Complete();
        }

        // Lift the source out of flow: it renders invisible (IsBeingDragged -> CardOpacity 0) and
        // the layout slots it at its own position (no gap) until the first DragOver moves the gap.
        card.IsBeingDragged = true;
    }

    private void OnDeviceCardDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        // Drag finished (dropped on a card, dropped on empty space, or cancelled). The Drop handler
        // commits the reorder and clears the gap, but a cancelled drag never reaches it, so this is
        // the catch-all that restores the source card and tears down the animation hooks.
        if (_draggedCard is not null) _draggedCard.IsBeingDragged = false;
        _draggedCard = null;
        Layout?.ClearReorderState();
        ClearGroupHighlights();
        SetDragInProgress(false);
        EnableReorderAnimations(false);
    }

    // ---- Container-level card reorder ----
    //
    // Card-reorder DragOver/Drop are handled on the transparent Grid around the repeater, not per
    // card: the insertion point is computed from the layout's frozen no-gap geometry, so it depends
    // only on the pointer position and stays put while cards slide to open the gap. The dragged
    // card's own (invisible) handlers and any hovered card bubble their card-payload events up to
    // here. App-chip payloads are handled per card and don't reach this.

    private void OnDevicesDragOver(object sender, DragEventArgs e)
    {
        if (Layout is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Whole-group drag (by the title) is its own path.
        if (_draggedGroupId is not null)
        {
            OnGroupDragOver(e);
            return;
        }

        // Only our card reorder/group drag is handled here. _draggedCard is set on card drag start;
        // app-chip and foreign drags leave it null (chips are handled per card).
        if (_draggedCard is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        var source = ViewModel.Devices.IndexOf(_draggedCard);
        if (source < 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var point = e.GetPosition(DevicesRepeater);
        var sourceGroup = _draggedCard.GroupId;

        if (sourceGroup is null && TryResolveGroupTarget(point, source, out var targetCard))
        {
            // Ungrouped source with group intent. No gap. Joining an EXISTING group highlights the
            // whole group's outline; pairing two lone cards highlights the target card.
            Layout.IncludeDraggedInGroupRect = false;
            Layout.ClearReorderState();
            if (targetCard.GroupId is { } joinGid)
            {
                ClearGroupDropTarget();
                SetGroupJoinTarget(joinGid);
                SetDragCaption(e, "Add to group");
            }
            else
            {
                ClearGroupJoinTarget();
                SetGroupDropTarget(targetCard);
                SetDragCaption(e, "Group");
            }
        }
        else if (sourceGroup is not null && !GroupIdentityBounds(sourceGroup).Contains(point))
        {
            // Member dragged past its group's outline: ungroup + move. Exclude it so the outline
            // hugs the remaining members (it's leaving).
            ClearGroupHighlights();
            Layout.IncludeDraggedInGroupRect = false;
            var snapped = SnapInsertion(Layout.GetInsertionIndex(point), source);
            Layout.SetReorderState(source, ToCompactIndex(snapped, source));
            SetDragCaption(e, "Ungroup and move");
        }
        else if (sourceGroup is not null)
        {
            // Member reordering within its own group: keep it in the outline (full footprint, with
            // the gap as the droppable space) and open the make-space gap among the members.
            ClearGroupHighlights();
            Layout.IncludeDraggedInGroupRect = true;
            var snapped = SnapInsertion(Layout.GetInsertionIndex(point), source);
            Layout.SetReorderState(source, ToCompactIndex(snapped, source));
            SetDragCaption(e, "Move");
        }
        else
        {
            // Ungrouped card reordering around the grid. Snap out of other groups' interiors so a
            // non-member never splits a group; the group it passes shifts as a unit.
            ClearGroupHighlights();
            Layout.IncludeDraggedInGroupRect = false;
            var snapped = SnapInsertion(Layout.GetInsertionIndex(point), source);
            Layout.SetReorderState(source, ToCompactIndex(snapped, source));
            SetDragCaption(e, "Move");
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void OnDevicesDrop(object sender, DragEventArgs e)
    {
        if (Layout is null) return;

        if (_draggedGroupId is not null)
        {
            OnGroupDrop(e);
            return;
        }

        if (_draggedCard is null) return;
        var sourceCard = _draggedCard;
        var source = ViewModel.Devices.IndexOf(sourceCard);
        if (source < 0) return;
        e.Handled = true;

        var point = e.GetPosition(DevicesRepeater);
        var sourceId = sourceCard.Endpoint.Id;
        var sourceGroup = sourceCard.GroupId;

        var groupHit = false;
        DeviceCard? targetCard = null;
        if (sourceGroup is null && TryResolveGroupTarget(point, source, out var t))
        {
            groupHit = true;
            targetCard = t;
        }
        var snapped = SnapInsertion(Layout.GetInsertionIndex(point), source);

        // Clear transient drag visuals before any await (the disband confirm).
        Layout.ClearReorderState();
        ClearGroupHighlights();

        if (groupHit && targetCard is not null)
        {
            if (targetCard.GroupId is { } gid)
            {
                _logger?.LogInformation("Group join: {Source} -> group {Group}", sourceId, gid);
                ViewModel.AddToGroup(sourceId, gid);
            }
            else
            {
                _logger?.LogInformation("Group create: {Source} + {Target}", sourceId, targetCard.Endpoint.Id);
                ViewModel.CreateGroup(sourceId, targetCard.Endpoint.Id);
            }
            return;
        }

        if (sourceGroup is not null)
        {
            // Member: out of the group's bounds = ungroup + move; inside = reorder within the group.
            if (!GroupIdentityBounds(sourceGroup).Contains(point))
            {
                var compact = ToCompactIndex(snapped, source);
                if (ViewModel.GroupMemberCount(sourceGroup) <= 2 && !await ConfirmDisbandAsync())
                {
                    return;   // cancelled - the member stays put
                }
                _logger?.LogInformation("Group drag-out: {Source} from {Group}", sourceId, sourceGroup);
                ViewModel.RemoveFromGroup(sourceId, compact);
            }
            else
            {
                _logger?.LogInformation("Group reorder within {Group}: {Source}", sourceGroup, sourceId);
                ViewModel.ReorderWithinGroup(sourceId, ToCompactIndex(snapped, source));
            }
            return;
        }

        var reorderTo = ToCompactIndex(snapped, source);
        _logger?.LogInformation("Reorder: {Source} -> compact index {Index}", sourceId, reorderTo);
        ViewModel.ReorderDeviceToCompactIndex(sourceId, reorderTo);
    }

    // ---- Whole-group drag (block reorder) ----

    private void OnGroupDragOver(DragEventArgs e)
    {
        var gid = _draggedGroupId!;
        var indices = GroupMemberIndices(gid);
        if (indices.Count == 0 || Layout is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        Layout.SetReorderState(indices, ComputeGroupInsertIndex(e.GetPosition(DevicesRepeater), gid, indices));
        SetDragCaption(e, "Move group");
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private void OnGroupDrop(DragEventArgs e)
    {
        var gid = _draggedGroupId!;
        var indices = GroupMemberIndices(gid);
        var insert = ComputeGroupInsertIndex(e.GetPosition(DevicesRepeater), gid, indices);

        Layout!.ClearReorderState();
        ClearGroupHighlights();
        e.Handled = true;

        _logger?.LogInformation("Group move: {Group} -> index {Index}", gid, insert);
        ViewModel.ReorderGroup(gid, insert);
    }

    /// <summary>Maps the pointer to an insert index in the block-excluded visible list, then snaps it
    /// out of any other group's run so the dragged group can't be dropped inside one.</summary>
    private int ComputeGroupInsertIndex(Point point, string gid, List<int> blockIndices)
    {
        var raw = Layout!.GetInsertionIndex(point);
        var before = 0;
        foreach (var i in blockIndices)
        {
            if (i < raw) before++;
        }
        var others = ViewModel.Devices
            .Where(c => !string.Equals(c.GroupId, gid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var compact = Math.Clamp(raw - before, 0, others.Count);
        return SnapOutOfGroups(others, compact);
    }

    /// <summary>Nudges an insert index out of the interior of any group's run in <paramref name="others"/>
    /// to the nearer boundary, so a block dropped there lands before/after that group, never inside it.</summary>
    private static int SnapOutOfGroups(List<DeviceCard> others, int index)
    {
        if (index <= 0 || index >= others.Count) return index;
        var gid = others[index - 1].GroupId;
        if (gid is null || !string.Equals(gid, others[index].GroupId, StringComparison.OrdinalIgnoreCase))
        {
            return index;   // not inside a group run
        }

        var start = index - 1;
        while (start > 0 && string.Equals(others[start - 1].GroupId, gid, StringComparison.OrdinalIgnoreCase)) start--;
        var end = index;
        while (end < others.Count && string.Equals(others[end].GroupId, gid, StringComparison.OrdinalIgnoreCase)) end++;
        return (index - start) <= (end - index) ? start : end;
    }

    /// <summary>Shows a drag caption (e.g. "Group", "Ungroup and move") on the OS drag cursor.</summary>
    private static void SetDragCaption(DragEventArgs e, string caption)
    {
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    /// <summary>Union of ALL a group's member card rects (no-gap geometry, including the dragged
    /// member's own slot). The leave-the-group test compares against this full footprint: a drag
    /// staying anywhere over the group stays; only crossing past the whole footprint ungroups.</summary>
    private Rect GroupIdentityBounds(string groupId)
    {
        var acc = default(Rect);
        var any = false;
        for (var i = 0; i < ViewModel.Devices.Count; i++)
        {
            if (!string.Equals(ViewModel.Devices[i].GroupId, groupId, StringComparison.OrdinalIgnoreCase)) continue;
            var r = Layout!.GetCardRect(i);
            if (r.Width <= 0 && r.Height <= 0) continue;
            acc = any ? UnionRect(acc, r) : r;
            any = true;
        }
        return acc;
    }

    private static Rect UnionRect(Rect a, Rect b)
    {
        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Confirms a group disband (removing this member leaves only one). Returns true to
    /// proceed.</summary>
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

    /// <summary>Snaps a raw card-level insertion index out of any group's interior (so a drag never
    /// splits a group), except the dragged member's own group - within which reordering is allowed.
    /// Snaps to whichever boundary of the intruded group is nearer.</summary>
    private int SnapInsertion(int raw, int source)
    {
        var count = ViewModel.Devices.Count;
        if (raw <= 0 || raw >= count) return raw;

        var sourceGroup = ViewModel.Devices[source].GroupId;
        var prev = ViewModel.Devices[raw - 1].GroupId;
        var next = ViewModel.Devices[raw].GroupId;
        if (prev is not null
            && string.Equals(prev, next, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(prev, sourceGroup, StringComparison.OrdinalIgnoreCase))
        {
            var (gStart, gEnd) = GroupRunBounds(prev);
            return (raw - gStart) <= (gEnd - raw) ? gStart : gEnd;
        }
        return raw;
    }

    /// <summary>First member index and one-past-last member index of a group's contiguous run in the
    /// visible list.</summary>
    private (int Start, int End) GroupRunBounds(string groupId)
    {
        var start = -1;
        var end = -1;
        for (var i = 0; i < ViewModel.Devices.Count; i++)
        {
            if (string.Equals(ViewModel.Devices[i].GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            {
                if (start < 0) start = i;
                end = i + 1;
            }
        }
        return (start, end);
    }

    /// <summary>Decides whether the pointer is over a card's centre with intent to group onto it.
    /// Only an ungrouped source can group (Stage 2); the target may be a lone card (create) or a
    /// grouped card (join). Returns false over a card's edge / the gap (= reorder intent).</summary>
    private bool TryResolveGroupTarget(Point point, int sourceIndex, out DeviceCard target)
    {
        target = null!;
        if (Layout is null) return false;

        var sourceCard = sourceIndex >= 0 && sourceIndex < ViewModel.Devices.Count ? ViewModel.Devices[sourceIndex] : null;
        if (sourceCard is null || sourceCard.GroupId is not null) return false;

        var idx = Layout.GetCardIndexAt(point);
        if (idx < 0 || idx == sourceIndex || idx >= ViewModel.Devices.Count) return false;

        var candidate = ViewModel.Devices[idx];
        if (candidate.GroupId is { } gid)
        {
            // Over a group: MOST of the footprint joins (you can't sort a card into a group's
            // interior). Only a thin edge on each side inserts before / after the group (reorder,
            // group bumps as a unit).
            var bounds = GroupIdentityBounds(gid);
            if (bounds.Width <= 0) return false;
            var edge = Math.Min(40.0, bounds.Width * 0.2);
            if (point.X < bounds.Left + edge || point.X > bounds.Right - edge) return false;
            target = candidate;
            return true;
        }

        // Over a lone card: the centre creates a new group; the edges reorder.
        if (!IsCentreZone(point, Layout.GetCardRect(idx))) return false;
        target = candidate;
        return true;
    }

    /// <summary>Inner 40% of the card counts as "centre" (30% inset each side); the surrounding
    /// frame reads as reorder so the two gestures don't fight at the boundary.</summary>
    private static bool IsCentreZone(Point p, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        var insetX = r.Width * 0.3;
        var insetY = r.Height * 0.3;
        return p.X >= r.Left + insetX && p.X <= r.Right - insetX
            && p.Y >= r.Top + insetY && p.Y <= r.Bottom - insetY;
    }

    private void SetGroupDropTarget(DeviceCard card)
    {
        if (ReferenceEquals(_groupDropTargetCard, card)) return;
        ClearGroupDropTarget();
        _groupDropTargetCard = card;
        card.IsGroupDropTarget = true;
    }

    private void ClearGroupDropTarget()
    {
        if (_groupDropTargetCard is null) return;
        _groupDropTargetCard.IsGroupDropTarget = false;
        _groupDropTargetCard = null;
    }

    /// <summary>Highlights the whole group <paramref name="groupId"/> as the join drop target (its
    /// row outlines turn accent); clears any previous one.</summary>
    private void SetGroupJoinTarget(string groupId)
    {
        if (string.Equals(_joinTargetGroupId, groupId, StringComparison.OrdinalIgnoreCase)) return;
        _joinTargetGroupId = groupId;
        RebuildOutlines();
    }

    private void ClearGroupJoinTarget()
    {
        if (_joinTargetGroupId is null) return;
        _joinTargetGroupId = null;
        RebuildOutlines();
    }

    /// <summary>Clears both group drop highlights (the per-card create highlight and the whole-group
    /// join highlight). Used by every non-group-intent path.</summary>
    private void ClearGroupHighlights()
    {
        ClearGroupDropTarget();
        ClearGroupJoinTarget();
    }

    /// <summary>Compacts a raw "insert before data index" position into the source-excluded space
    /// the gap and <see cref="HomeViewModel.ReorderDeviceToCompactIndex"/> both use.</summary>
    private int ToCompactIndex(int raw, int source)
    {
        var compact = raw > source ? raw - 1 : raw;
        return Math.Clamp(compact, 0, Math.Max(0, ViewModel.Devices.Count - 1));
    }


    /// <summary>Attaches (or removes) a Composition implicit Offset animation on each realized card
    /// so any layout re-arrange slides smoothly. Kept on only during a reorder drag so page loads
    /// and scrolls don't animate.</summary>
    private void EnableReorderAnimations(bool enable)
    {
        _reorderActive = enable;
        for (var i = 0; i < ViewModel.Devices.Count; i++)
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

    private void OnDevicesElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_reorderActive)
        {
            ApplyReorderAnimation(args.Element, true);
        }
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

    private void OnDeviceCardDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        // A card reorder/group drag (our own) is handled at the container; bubble immediately without
        // touching the DataView, so the (blocking) payload read only happens for app-chip drags.
        if (_draggedCard is not null) return;

        // Bail early when the drag isn't ours. Other drags (file drops onto the window, etc.)
        // shouldn't get our acceptance.
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Read the payload once, then branch on prefix: card-reorder vs app-chip move.
        var text = TryReadText(e.DataView);
        if (text is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Card-reorder payloads bubble up to the container (OnDevicesDragOver), which positions the
        // gap from stable layout geometry. Leave the event unhandled so it gets there.
        if (text.StartsWith(DragPayloadCardPrefix, StringComparison.Ordinal))
        {
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

        // A card reorder/group drop is committed at the container (OnDevicesDrop); bubble immediately
        // without the blocking payload read. Only app-chip drops are handled per card.
        if (_draggedCard is not null) return;

        var text = TryReadText(e.DataView);
        if (text is null) return;
        if (text.StartsWith(DragPayloadCardPrefix, StringComparison.Ordinal)) return;

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
        foreach (var card in ViewModel.Devices)
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
#pragma warning restore CA1822
}
