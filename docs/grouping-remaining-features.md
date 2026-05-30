# Device grouping: remaining features

Handoff for another agent. Two grouping features on the Devices page are designed but not built:
**dedicated row** and **group <-> ungrouped spacing**. Both live in the layout
(`src/Earmark.App/Controls/WrapByRowLayout.cs`) and its group plumbing.

Read [AGENTS.md](../AGENTS.md) for build/run/verify, and
[grouping-container-model.md](./grouping-container-model.md) for the overall grouping architecture and
current-state map. **If the container-model rewrite is done, "dedicated row" is subsumed by it** (a
dedicated group is just a full-row-spanning block) - implement these against whichever model is current.

Both features need `WrapByRowLayout.ComputeSlotRects` to move from its current **uniform
`columnCount`-per-row walk** (`rowStart += columnCount`) to a **running-fill model** (advance a column
cursor, with forced row breaks and variable inter-item gaps). That's the shared, slightly risky core;
do it once and both features fall out. Keep the hit-testing (`GetInsertionIndex`, `GetCardIndexAt`,
`GetCardRect`) honest - it already reads explicit `_identityRects`, so it adapts to non-uniform x as
long as those rects are correct.

## 1. Dedicated row

Let a group reserve its own grid row(s); other items bump to the row above/below by order.

**Already in place:**
- `AppSettings.DeviceGroup.DedicatedRow` (persisted) and `DeviceGroupInfo.DedicatedRow` (two-way, persists via `HomeViewModel.OnGroupInfoChanged` -> `QueueSettingsSave` + `GroupInfosChanged`, which triggers `WrapByRowLayout.RefreshLayout()`).
- `IGroupLayoutInfo.IsDedicatedRow(string groupId)` - the page's adapter already answers it from `DeviceGroupInfo.DedicatedRow`.

**To build:**

1. **Context-menu toggle.** Add a `ToggleMenuFlyoutItem Text="Dedicated row"` to the group title's
   `MenuFlyout` in `HomePage.xaml` (the `GroupTitleHost` item template, alongside "Rename" / "Ungroup
   all"), two-way bound to the group's `DeviceGroupInfo.DedicatedRow`. The item's `DataContext` is the
   `GroupOverlay`; bind `IsChecked="{x:Bind Group.DedicatedRow, Mode=TwoWay}"`. Toggling persists +
   relayouts via the existing change callback.

2. **Layout reservation** in `ComputeSlotRects`. When walking blocks/cards in order, before placing the
   first member of a `DedicatedRow` group:
   - If the current row already has cards, flush it (force a row break) so the group starts a fresh row.
   - Lay the group's members on their own row(s): they take consecutive columns at the normal
     `columnWidth`; if `memberCount < columnCount` the trailing columns stay empty (do **not** widen
     members - they must line up with lone-card width); if `memberCount > columnCount` they wrap onto
     additional rows that remain exclusive to the group.
   - After the group's last member, force a row break so the next item starts a new row.

   Net effect: a dedicated group sits on its own row band; items before it end their row early (bump
   up) and items after start fresh (bump down) - exactly "reserves the entire row, others bump above/
   below by order".

3. The title band (`TitleBandHeight`) already reserves space above the group's first row; that logic is
   unchanged - a dedicated group still gets its title band on top of its exclusive rows.

**Gotchas:**
- `RefreshLayout()` (re-measure) must run when `DedicatedRow` flips - it already does via
  `GroupInfosChanged`. Verify the band + segments recompute and neighbours bump.
- `GroupSegments` (per-row outline rects) must still be correct for a dedicated group that wraps onto
  multiple exclusive rows - it already groups member rects by row, so it should just work once the
  members are arranged on their own rows.

## 2. Group <-> ungrouped spacing

Add visible horizontal separation (the user asked for roughly "double space") between a group and an
adjacent ungrouped card (or another group), so a group reads as a distinct cluster and there's a clear
drop zone beside it.

**To build:** in the running-fill `ComputeSlotRects`, add extra horizontal gap at group boundaries -
i.e. before a card that **starts** a group (when it isn't the first item in its row) and after a card
that **ends** a group (before the next item). Suggested gap = `ColumnSpacing` extra (so ~2x the normal
gap at the boundary). Use `IGroupLayoutInfo.GroupIdForIndex` to detect boundaries (group id differs
from the previous/next slot).

**The tradeoff to decide:**
- The current grid keeps columns **aligned across rows** (every card at `col * (columnWidth +
  ColumnSpacing)`). Inserting extra gap shifts subsequent cards, breaking that alignment in rows that
  contain a group boundary.
- Two acceptable resolutions:
  - **Running-fill + wrap (recommended):** advance a real x cursor, add the boundary gap, and wrap to
    the next row when the next card wouldn't fit in the remaining width. Rows with a group boundary
    won't be perfectly column-aligned with plain rows, but the separation is real and the layout never
    overflows. Hit-testing already uses explicit rects, so it adapts.
  - **Reserve a blank column:** treat a boundary as consuming one empty grid column. Preserves
    alignment but wastes a full column of width (likely more than "double space" - probably too much).

**Bonus drop-zone payoff:** once there's real space beside a group, that gap becomes a stable
"insert before/after the group" drop target (the pointer is over empty space, so `GetCardIndexAt`
returns -1 and the drag reads as a reorder, not a join). This directly improves the "insert in front of
a first-in-row group" ergonomics that needed the group-thirds workaround in the flat model.

## Verification (both)

Per AGENTS.md: kill, build (`dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64
--no-restore`), relaunch, read the newest log under `%LocalAppData%\Earmark\logs`. Then in the running
app:
- **Dedicated row:** toggle it on a group; confirm the group takes its own row(s), neighbours bump
  above/below, the title band still shows, and the state persists across relaunch.
- **Spacing:** confirm a visible gap appears between a group and adjacent cards/groups, the row doesn't
  overflow or clip, drag/reorder still hit-tests correctly, and dropping in the new gap reorders
  (doesn't join).
