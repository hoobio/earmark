# Device grouping: the container-model rewrite

Handoff for another agent. This describes replacing the current **flat** grouping model on the
Devices page with a **container model** where a group is a real, atomic unit and devices are its
children. The goal is to make "a drag splits a group" structurally impossible, removing the class of
edge-case bugs the flat model keeps producing.

Read [AGENTS.md](../AGENTS.md) first for build/run/verify (kill `Earmark.App.exe`, build x64, relaunch,
read the latest log). Grouping is entirely on the **Devices page** (`HomePage`).

## Why rewrite

The current implementation (see "Current state" below) treats the device grid as a **flat ordered
list of cards**. A "group" is just a run of adjacent cards that share a `GroupId`, held together by a
contiguity rule (`HomeViewModel.ApplyGroupContiguity`) plus geometry-based snapping during drags.

That works for most operations but is fragile at the seams, because:

- The drop target during a drag is computed from **frozen no-gap card geometry** (`WrapByRowLayout._identityRects`). While a multi-card group is mid-drag, the other cards have visually shifted (the make-space gap), so the geometry can point *inside* another group's run.
- "Don't split a group" is therefore enforced by a patchwork: `SnapInsertion` / `SnapOutOfGroups` (page) nudge the insertion to a group boundary, and `ApplyGroupContiguity` re-asserts adjacency on commit. Each new interaction (single card vs group, within-row vs wrapped) needs its own guard.

A container model removes the whole problem: the top level is a list of **blocks** (a lone card or a
group), and reorder operates on blocks. A group is one slot - you can only drop *before* or *after* it,
never *into* it. Joining/leaving a group is an explicit, separate gesture, not an emergent side effect
of where a reorder happened to land.

## Current state (what exists today, the baseline you're replacing)

### Model + persistence
- `src/Earmark.App/Settings/AppSettings.cs`
  - `List<DeviceGroup> DeviceGroups`. `DeviceGroup { string Id; string Title; bool DedicatedRow; List<string> MemberIds; }` (`MemberIds` are endpoint ids in left-to-right member order).
  - `List<string> DeviceOrder` is still the single source of truth for absolute card order (including hidden cards). `DeviceGroups` is a **parallel overlay** keyed by endpoint id; contiguity reconciles the two.
- Persistence mirrors `DeviceOrder`: mutate `_settings.Current`, call `QueueSettingsSave()` (200ms debounce). JSON is source-generated (`SettingsJsonContext`); nested `DeviceGroup` serialises automatically.

### View-models (`src/Earmark.App/ViewModels/`)
- `DeviceCard.cs`: `string? GroupId`, `bool IsGroupMember`, `bool IsGroupDropTarget` (transient accent outline when two lone cards will pair), `bool IsBeingDragged` (lifts the card out of flow - `CardOpacity` 0).
- `DeviceGroupInfo.cs`: per-group chrome VM - `Id`, two-way `Title`, `DedicatedRow`, reused across rebuilds so an in-progress title edit keeps focus; raises a change callback so `HomeViewModel` persists.
- `GroupOverlay.cs`: positions one group's **editable title** (`X`, `Y`, `Width`, `IsEditing`, `ShowLabel`).
- `GroupOutlineSegment.cs`: one **per-row** dotted-outline rectangle (`X/Y/Width/Height`, `ShowOutline`, `IsJoinTarget`, `ShowNormalOutline`). A wrapped group has one per row so the outline hugs each row instead of a bounding box.
- `HomeViewModel.cs` group surface:
  - Build: `BuildCards` calls `BuildGroupIdMap` (endpoint id -> group id, only for groups with >=2 present members) and `ApplyGroupContiguity<T>` (pulls each group's members adjacent, anchored at the earliest-appearing member). Cards get their `GroupId` stamped here.
  - `_groupInfos` (`Dictionary<string, DeviceGroupInfo>`) + `IReadOnlyDictionary GroupInfos` + `event Action GroupInfosChanged`; `ReconcileGroupInfos` keeps it in sync after rebuilds.
  - Mutations: `CreateGroup(sourceId, targetId)`, `AddToGroup(sourceId, groupId)`, `ApplyGroupChange` (shared tail: re-contiguate + re-derive `DeviceOrder` + save + `SyncVisibleDevices` + `ReconcileGroupInfos`), `RemoveFromGroup`, `ReorderWithinGroup`, `RemoveMemberFromGroupModel` (disbands if <2), `UngroupAll`, `UngroupDevice` (drops the card behind the group), `ReorderGroup(groupId, visibleInsertIndex)`, `GroupMemberCount`.
  - Hiding a grouped card removes it from its group: `OnCardVisibilityToggled` calls `RemoveMemberFromGroupModel`.

### Layout (`src/Earmark.App/Controls/WrapByRowLayout.cs`)
A `VirtualizingLayout` (uniform-width columns, per-row height). Grouping additions:
- `IGroupLayoutInfo` (injected by the page): `string? GroupIdForIndex(int)`, `bool IsDedicatedRow(string)`.
- `const double TitleBandHeight = 28` reserved above the first row a group starts in.
- Reorder state generalised to a **block**: `int[] _draggedIndices` + `int _gapIndex`; `SetReorderState(int, int)` / `SetReorderState(IReadOnlyList<int>, int)`; `BuildDisplayOrder` lifts the block and re-inserts it contiguously at the gap. This is what makes the live make-space gap work for both a single card and a whole group.
- `IncludeDraggedInGroupRect` (whether the dragged card still counts toward its group outline - true only for within-group member reorder).
- `GroupSegments` (`Dictionary<string, IReadOnlyList<Rect>>`): per group, one union rect per row it spans. Drives the overlay.
- Hit-testing for drags: `GetInsertionIndex(Point)`, `GetCardIndexAt(Point)`, `GetCardRect(int)` - all against the frozen `_identityRects`.
- `event Action Arranged` (page repositions overlays synchronously each arrange) and `RefreshLayout()` (forces re-measure after a membership change that didn't reorder).

### Page (`src/Earmark.App/Views/HomePage.xaml` + `.xaml.cs`)
- A transparent `Grid` wraps the `ItemsRepeater` (`DevicesRepeater`) and handles **container-level** card-reorder + group drags (`OnDevicesDragOver`/`OnDevicesDrop`); per-card handlers only deal with app-chip drops and bubble card-payload events up.
- Two overlay `ItemsControl`s above the repeater in the same scroll-content space: `GroupOutlines` (per-row outline segments) and `GroupTitleHost`/`GroupOverlays` (editable titles). Both positioned by a `TranslateTransform` driven by `x:Bind` (a `Canvas.Left` style-binding silently fails on generated containers - **do not** reintroduce it).
- Drag intent: over a lone card's **centre** = create group; over a group, most of the footprint = **join** (thin ~40px edges = reorder before/after); a card's **edge / the gap** = reorder. Member dragged **past the group's bounds** = ungroup (`GroupIdentityBounds` test) with a `ContentDialog` disband-confirm when it would drop below 2.
- The **title is the group's drag handle**: `CanDrag` on the title Grid starts a whole-group block drag (`OnGroupTitleDragStarting` lifts all members); double-tap / context "Rename" enters edit; context "Ungroup all" / member-card "Ungroup device".
- Group-on-group split guard (the pragmatic fix): `ComputeGroupInsertIndex` + `SnapOutOfGroups` (page) keep the block's drop out of other groups' runs; `ReorderGroup` re-asserts contiguity.
- Composition implicit `Offset` animations attached to realised cards during a drag (the cards slide to make space). Outlines shown only while dragging.

## Target: the container model

### Data model
Introduce a real ordered tree, one level deep:

- `HomeViewModel.Blocks : ObservableCollection<object>` where each item is either:
  - a `DeviceCard` (ungrouped), or
  - a `DeviceGroupCard` (new VM): `Id`, two-way `Title`, `bool DedicatedRow`, `ObservableCollection<DeviceCard> Members`.
- Persist as today (`AppSettings.DeviceGroups` + a top-level order). The block order replaces the flat
  `DeviceOrder` semantics: persist the ordered list of "block ids" (endpoint id for a lone card, group
  id for a group) plus each group's member order. Keep a migration that reads the existing
  `DeviceOrder` + `DeviceGroups` into the block list on first run.

`ApplyGroupContiguity`, `BuildGroupIdMap`, `_groupInfos`/`DeviceGroupInfo`, and the per-card `GroupId`
stamping all **go away** - membership is now structural (a card is in a group iff it's in that group's
`Members`).

### Layout
Replace `WrapByRowLayout` with a **span-aware** wrap layout (or heavily extend it). Each block has a
column span:
- A lone card spans 1 column.
- A non-dedicated group spans `min(memberCount, columnCount)` columns and reserves the title band on top; members wrap within the group's span if they exceed it.
- A `DedicatedRow` group spans the **full row** (forces a row break before and after; neighbours bump to the row above/below - this is also the "dedicated row" feature, so the rewrite subsumes it).

Bind the repeater to `Blocks` with a template selector: lone-card template (the existing card) vs a
**group-container** template (transparent border + editable title + an inner panel that lays the member
cards out). The group container draws its own dotted outline (only during a drag) and title - **no
separate overlay layer needed**, which removes the whole `GroupOverlay`/`GroupOutlineSegment`/
`Arranged`/`TranslateTransform` overlay machinery and its sync bugs.

### Drag + drop
- **Reorder** operates on top-level blocks. The make-space gap opens between blocks. A group is one
  block, so it can never be split, and a lone card can never land inside a group. Keep the
  make-space gap concept and the Composition `Offset` slide - they carry over directly, just at block
  granularity instead of card granularity.
- **Create group**: drop a lone card onto another lone card's centre -> new `DeviceGroupCard` with both. Drop a lone card onto a group container -> add to that group's `Members`. This is now a genuine reparent (card moves from the outer `Blocks` list into a group's `Members`), not a flag flip.
- **Ungroup / leave**: drag a member card out of the group container's bounds -> move it from `Members` back to the outer `Blocks` list at the drop position. Disband (replace the group block with its lone remaining card) when `Members` drops below 2, with the existing `ContentDialog` confirm.
- **Move a whole group**: drag the group container (or its title) to reorder the group block among the other blocks.
- **Reorder within a group**: drag a member inside the container to reorder `Members`.

The hard part is the **cross-container drag** (a card moving between the outer list and a group's inner
panel). Options: a single shared `DataPackage` payload + the drop target decides which collection the
card lands in based on whether the pointer is inside a group container's bounds; or hit-test the
group container under the pointer in the page's drop handler and move the card between observable
collections directly (in-process drag, so you can resolve the live `DeviceCard` from the payload).

### What to keep / reuse from today
- The make-space gap + Composition `Offset` implicit animation (carry over to block granularity).
- The opaque rounded drag-bitmap (`RenderCardOpaqueAsync` in `HomePage.xaml.cs`).
- The disband `ContentDialog` flow.
- The "outline only while dragging", centre-vs-edge intent zones, and "join lights the whole group" affordances - they map onto the container directly (the container *is* the outline now).
- `DeviceGroupInfo`'s editable-title behaviour (double-click / Rename to edit, label is the drag handle) - moves onto the group-container template.
- App-chip drag/drop (`OnAppChipDragStarting` etc.) is independent of grouping; leave it.

### Migration plan (stages, each builds + runs)
0. Add `DeviceGroupCard` VM + `Blocks` collection; build `Blocks` from the existing settings; bind the repeater to `Blocks` with a template selector. Lone-card template = existing card. Group template = a simple bordered container with the members inside (no drag yet). Verify groups render as containers.
1. Span-aware layout: group container spans N columns + title band; dedicated-row groups span the full row. Verify single-row and wrapped groups, and dedicated-row reservation.
2. Block reorder (make-space at block granularity) + move-whole-group. Verify a group can't be split and lone cards reorder around it.
3. Create / join / leave / disband as real reparents between `Blocks` and `Members`, incl. the cross-container member drag and the disband confirm.
4. Within-group member reorder + per-group context menus + title editing.
5. Persistence (block order + member order), migration from the old `DeviceOrder`/`DeviceGroups`, and pruning of unplugged endpoints.

### Gotchas
- `ItemsRepeater` ignores `Move` notifications under a custom `VirtualizingLayout` (it doesn't re-arrange a moved realised element). The flat code works around this with Remove+Insert in `SyncVisibleDevices` - the container version must do the same for both the outer `Blocks` and inner `Members` collections.
- Hidden devices: a group may contain hidden members. Decide whether a hidden member still counts toward the >=2 threshold (the flat model counts all members, so revealing a hidden one never surprise-disbands - keep that).
- Reusing `DeviceCard` instances across rebuilds matters (peak meters / slider bindings churn otherwise) - the flat code preserves instances; the container version must too.
- Theme-dependent brushes must stay `{ThemeResource}` (see AGENTS.md). Group transparency at rest + dotted outline only while dragging is a hard requirement.
- The peak-meter/apps work on the card template is owned by other changes; don't regress it when introducing the template selector.

### Verification
Per AGENTS.md, after each stage: kill, build (`dotnet build src/Earmark.App/Earmark.App.csproj -c Debug
-p:Platform=x64 --no-restore`), relaunch, read the newest log under `%LocalAppData%\Earmark\logs`. Then
exercise in the running app: create/join/leave/disband, reorder a lone card around a group, **drag one
group through another (must never split)**, move a group, reorder within a group, dedicated-row toggle,
rename, and confirm `DeviceGroups` + order persist across relaunch.
