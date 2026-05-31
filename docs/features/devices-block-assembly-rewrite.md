# Feature spec: Devices block-assembly rewrite + "show disconnected" toggle

Implementation prompt for an agent. Not yet built. Read [AGENTS.md](../AGENTS.md) first. Pairs with
[device-persistence-feature.md](device-persistence-feature.md): persistence supplies the
disconnected cards and the instance reuse; this spec is the **assembly pipeline** that turns the
card set into the rendered `Blocks`, reworked so filters (show hidden, show disconnected) and the
live/persisted union compose cleanly. Land persistence's identity + reuse work alongside or just
before this.

## Why

Filtering for the Devices list is split across two layers today:

- [`DeviceCard`](../src/Earmark.App/ViewModels/DeviceCard.cs) holds a `_showHidden` flag and exposes
  `IsListed = IsGroupMember || _showHidden || !IsEffectivelyHidden` plus a `CardOpacity` that bakes in
  the "shown via show-hidden" dim tier.
- [`HomeViewModel.SyncBlocks`](../src/Earmark.App/ViewModels/HomeViewModel.cs) reads `card.IsListed`
  while assembling `Blocks`, and `OnShowHiddenDevicesChanged` pushes the toggle into every card's
  `_showHidden` then resyncs.

Putting the toggle state on every card (and re-deriving `IsListed` / `CardOpacity` from it) means
adding a second toggle ("show disconnected") would push **another** flag onto every card and grow
both `IsListed` and the opacity logic. Stacking persistence (live + persisted-absent union) on top
makes the implicit pipeline (`BuildCards` -> `_allCards` -> `ComputeOrderedBlockIds` -> `SyncBlocks`
-> `Blocks` + `_visibleCards`) hard to reason about. Rework it into one explicit pipeline with the
filter as a single composable predicate owned by the view-model, not the card.

## Goal

1. **One assembly pipeline** that goes: card source -> filter -> order/group -> in-place reconcile
   into `Blocks` + `_visibleCards`, with each stage isolated and the filter centralised.
2. **"Show disconnected" toggle** in the Devices header, parallel to "Show hidden": off by default,
   it reveals persisted-but-disconnected device cards (dimmed) and hides them again when off.
3. **Preserve the in-place reconcile** so card/group instances survive (this is what keeps the slide
   animation and avoids the flash - see the persistence doc).

## Current pipeline (what to refactor)

- `BuildCards` (background thread): enumerates live active endpoints, returns `DeviceCard`s in
  default-sort order.
- `_allCards`: flat list, replaced wholesale each rebuild (the persistence doc changes this to a
  reuse-by-`DeviceKey` reconcile and to a live + persisted-absent union).
- `ComputeOrderedBlockIds`: from `_allCards` + `DeviceGroups` + `DeviceOrder`, produces the ordered
  block-id list (lone cards + groups; a group needs >= 2 present members to be live). Includes hidden
  cards' slots so toggling show-hidden doesn't move things.
- `SyncBlocks`: builds/reuses group cards, computes `desired` blocks (filtering lone cards by
  `card.IsListed`), two-phase in-place reconcile (`RemoveMissing` then `PositionInPlace`) of `Blocks`
  and each group's `Members`, then rebuilds `_visibleCards`.
- Filtering predicates live on the card: `IsEffectivelyHidden` (user-hidden, or no-rules-and-not-
  default-and-not-pinned), `IsListed`, `CardOpacity`.

## Proposed pipeline

Keep `BuildCards` (background) producing data and `SyncBlocks` doing the in-place reconcile - those
are sound. Change what feeds and filters them:

1. **Source set** (`_allCards`): live cards plus persisted-absent cards (from the persistence doc),
   reconciled by `DeviceKey` so instances survive. Each card exposes only **intrinsic** facts:
   `IsConnected`, `IsEffectivelyHidden`, `IsGroupMember`, `IsDefault`, `HasRules`. The card no longer
   holds `_showHidden` (or any toggle state).

2. **Filter** (centralised in the view-model / a small `DeviceListFilter` helper, not on the card):

   ```
   listed(card) =
       card.IsGroupMember                       // membership always pins it visible
       || ( (ShowHidden       || !card.IsEffectivelyHidden)
         && (ShowDisconnected || card.IsConnected) )
   ```

   Both toggles are view-model state (`ShowHiddenDevices`, `ShowDisconnectedDevices`). A group member
   stays listed regardless (matches today). A disconnected member of a group: decide whether the
   group keeps showing it (recommended yes - a group is a user-curated unit) or drops it; document
   the choice.

3. **Order / group**: `ComputeOrderedBlockIds` largely as-is, but the "present" set must include
   persisted-absent devices so a disconnected device keeps its order slot and group membership.
   (Today "present" = live endpoint ids; extend to known `DeviceKey`s.)

4. **Reconcile**: `SyncBlocks` consumes the filtered, ordered set and reconciles in place (unchanged
   mechanism). It reads the centralised `listed(...)` result, not `card.IsListed`.

Net: the card stops knowing about toggles; the VM owns the filter; adding a third filter later is one
clause, not a new per-card flag.

### What moves off the card

- Delete `DeviceCard._showHidden` and the `showHidden` constructor/refresh params; `IsListed` either
  goes away (VM computes `listed`) or becomes a pure function of intrinsic state the VM passes in.
- `CardOpacity`'s "dimmed because shown-via-toggle" tier moves to a VM-driven signal. The card still
  owns `IsBeingDragged` (drag source = invisible). Define the opacity tiers explicitly (see below).
- `OnShowHiddenDevicesChanged` no longer loops cards to push a flag; it just re-runs the filter +
  reconcile (a resync). Same for the new `OnShowDisconnectedDevicesChanged`.

## The "show disconnected" toggle

- **UI**: a second `ToggleSwitch` in the Devices header next to "Show hidden"
  ([HomePage.xaml](../src/Earmark.App/Views/HomePage.xaml), the header `Grid` around the existing
  toggle). Two plain toggles is fine; if it gets crowded, a single "Filter" split-button / flyout
  with both options is the fallback - keep it Fluent 2, benchmark against Settings.
- **View-model**: `[ObservableProperty] public partial bool ShowDisconnectedDevices`, mirroring
  `ShowHiddenDevices`. Its change handler resyncs (filter + reconcile), like
  `OnShowHiddenDevicesChanged`.
- **Persistence of the toggle itself**: `ShowHiddenDevices` is deliberately **session-only**
  (defaults off each launch - see the note in `HomeViewModel`). Decide whether `ShowDisconnected`
  matches (session-only, recommended for consistency) or persists to `AppSettings`. Pick one and note
  it.
- **Default**: off. Disconnected devices are remembered but not shown until the user opts in, so the
  list isn't cluttered by every headset ever paired.

## Opacity / disabled tiers (define explicitly)

A card can be in several display states; spell out the precedence so the tiers don't fight (today
they're tangled in one ternary):

1. drag source -> opacity 0 (its slot is the drop gap). Highest precedence.
2. disconnected (shown via the toggle) -> dimmed + controls disabled (persistence doc owns the
   disabled behaviour; this owns the dim).
3. hidden-but-shown (via show-hidden) -> the existing ~0.5 dim.
4. normal -> opacity 1.

Drive each from a bound VM/card signal; **no implicit opacity animation on the card container**
(recycled-container stuck-at-0 bug, per AGENTS.md / the HomePage notes).

## Interaction with the animation

The slide works as long as the **surviving** cards keep their elements (instance reuse from the
persistence doc), independent of the toggle:

- Toggle **off**: a device disconnecting is filtered out of `Blocks` (removed), the other cards'
  instances survive, so they slide up to close the gap; reconnect re-adds it (it appears, others
  slide down).
- Toggle **on**: a disconnecting device keeps its slot and just dims in place (no reflow); reconnect
  un-dims. No slide needed because nothing moved.

Either way there's no full rebuild, so no flash. Verify both.

## Codebase pointers

- [`HomeViewModel`](../src/Earmark.App/ViewModels/HomeViewModel.cs): `SyncBlocks`,
  `ComputeOrderedBlockIds`, `ShowHiddenDevices` + `OnShowHiddenDevicesChanged` (model the new toggle
  on these), `_allCards`, `_visibleCards`, `HasItems` / `ShowEmptyState` / `ShowLoadingState` (the
  empty-state must account for "everything filtered out by the toggles" vs "genuinely no devices").
- [`DeviceCard`](../src/Earmark.App/ViewModels/DeviceCard.cs): `IsEffectivelyHidden`, `IsListed`,
  `CardOpacity`, `_showHidden` (remove), `IsConnected` (added by the persistence doc).
- [`HomePage.xaml`](../src/Earmark.App/Views/HomePage.xaml): the header toggle area; the empty-state
  card copy (it should hint "turn on Show hidden / Show disconnected" when the list is non-empty but
  fully filtered).
- [`AppSettings`](../src/Earmark.App/Settings/AppSettings.cs): only if `ShowDisconnected` is chosen
  to persist.

## Acceptance criteria

Done:
- The filter is a single view-model predicate; `DeviceCard` no longer carries toggle state.
- A "Show disconnected" toggle sits beside "Show hidden"; off by default; toggling it reveals/hides
  persisted disconnected cards (dimmed) and resyncs in place (no full rebuild).
- "Show hidden" behaviour is unchanged (same cards shown/dimmed as before for connected devices).
- Order and group membership are preserved for disconnected devices (they keep their slot).
- Empty-state vs filtered-empty are distinguished in the UI copy.
- Opacity tiers follow the defined precedence; no implicit opacity animation on card containers.

## Risks / open questions

- **Empty-state semantics**: with both toggles off and only hidden/disconnected devices present, the
  list is empty - the empty-state copy must point at the toggles rather than implying "no devices".
- **Group + disconnected**: confirm a group keeps a disconnected member visible (recommended) and
  that a group with only one *connected* member still renders sensibly.
- **`ComputeOrderedBlockIds` "present" set**: must include persisted `DeviceKey`s, or disconnected
  devices lose their order slot. Coordinate with the persistence doc's identity model.
- **Toggle persistence**: session-only vs saved - pick deliberately; session-only matches the
  existing show-hidden choice.
