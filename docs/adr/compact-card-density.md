# Decision record: per-view card display options (compact density + Quick Controls overrides)

**Status:** Accepted (2026-06-07). Implemented in `feat/compact-cards` (`PeakMeterOptions`, `CardPresentation`, `DeviceCardView`, `HomeViewModel`, `AppSettings`).

Records how a device card renders at a different density and feature set in each window without duplicating the card, why compact mode is one input among the per-view options, and the slider-centring gotcha the compact row hit.

## Context

Device cards (`DeviceCardView`) render in two windows from the **same** view-models: the Devices page (`HomePage`) and the Quick Controls overlay (`QuickControlsWindow`). There is one `DeviceCard` per device and one `NowPlayingStrip` per playing app; both windows bind those same instances. This single-instance design is why volume, mute, the 20 Hz peak meters, and now-playing state are automatically identical in both windows (same object, nothing to sync).

Two needs pulled against that:

1. **Compact density** had to tighten many independent measurements at once (card padding, section spacing, icon tile, volume + meter height, now-playing strip layout, rule chips) and update live on toggle.
2. **Quick Controls had to diverge from the Devices page**: render compact, drop the rules section, drop the header badges, drop the section dividers, keep now-playing - while the main window kept the user's roomy Devices layout. Driving the one shared options object compacts the main window too whenever it's visible behind the overlay (a visible glitch the user rejected).

## Decision

**Card display is resolved per view through a lightweight `CardPresentation(card, options)` projection. The card and strip instances stay shared (live state identical); only a `PeakMeterOptions` instance diverges per window.**

- `DeviceCardView` takes an `Options` dependency property. Unset (the main window) falls back to `card.MeterOptions`, the global options object - so the Devices page is byte-for-byte unchanged. Quick Controls sets `Options="{x:Bind QuickMeterOptions}"`.
- From `Card` + `Options` the view builds a `CardPresentation`. The XAML binds it for two things:
  - **display geometry** (`Presentation.Options.X`: padding, spacing, icon tile, volume/meter heights, the now-playing strip layout, rule-chip metrics, and the `CompactCards` flag), and
  - **combine-with-data visibility** (`Presentation.ShowRulesSection`, `ShowNowPlaying`, the `ShowNormalBadges`/`ShowCompactBadges` pair, the section dividers) which fold one option (`ShowRules` / `ShowNowPlaying` / `ShowDeviceBadges` / `ShowCardDividers` / `CompactCards`) with card data.
- The **data-template gap** is closed by `CardPresentation.NowPlayingStrips`: it mirrors `DeviceCard.NowPlayingStrips` as `NowPlayingStripView(Strip, Options)` records (in place, via `CollectionChanged`). Each strip's data template binds `Strip.X` for live state and `Options.X` for geometry, so the strip renders at the host window's density even though the template can't reach the host control's DP. The expanded rule chips use the same trick (`RuleSummary.Options`).

This is a refinement of the per-view option the first draft of this ADR rejected (then "approach 2"). What made it viable is that `CardPresentation` holds **no live state** - only a reference to the shared card/strip plus the view's options, recomputing pure styling flags on `PropertyChanged`. So it never becomes "approach 3" (separate card/strip instances + a mirroring layer): live state stays single-instance and identical across windows; only styling diverges.

### Compact density

`CompactCards` on `PeakMeterOptions` drives every compact geometry getter (`CardContentPadding`, `CardSectionSpacing`, `IconTileSize`, `VolumeRowHeight`, `MeterTotalHeight`, `VolumeSliderMargin`, `NowPlayingStripPadding`, the transport sizes, `RuleChipPadding`, the `ShowNormalBadges`/`ShowCompactBadges` pair, etc.). Every getter returns the **roomy** value when `CompactCards` is false, so the non-compact layout never shifts. `OnCompactCardsChanged` re-raises them all, so a toggle re-flows live with no card rebuild.

- The Devices page is fed by `AppSettings.CompactCards` (default false), toggled from Settings and the Devices backdrop menu, mirrored onto the global options by `HomeViewModel.SyncMeterOptions`.
- Quick Controls is fed by its own `QuickControlsCompact` (default true), independent of the page.

### Quick Controls overrides

Quick Controls is its own configurable view. `HomeViewModel.SyncQuickMeterOptions` builds `QuickMeterOptions` by mirroring the global options, then applying five overrides that win for the overlay only (the main window binds the global object, QC binds its own, so the page is never touched):

| Setting | Default | Effect in QC |
|---|---|---|
| `QuickControlsCompact` | true | Compact density |
| `QuickControlsShowNowPlaying` | true | Keep the now-playing strip |
| `QuickControlsShowRules` | false | Drop the rules section (overlay is for control, not rule editing) |
| `QuickControlsShowDeviceBadges` | false | Drop the flow / Default / Communications pills |
| `QuickControlsShowDividers` | false | Drop the hairline section dividers |

Defaults suit a dense, glanceable overlay. A Quick Controls card's only context menu is a single **Settings** item: it restores the main window, navigates to Settings, and reveals (expands + scrolls to) the Quick Controls section. Configuration lives in the app, not the overlay.

## Tradeoffs

| Concern | This design | Alternative |
|---|---|---|
| Per-window divergence | `CardPresentation` projection over shared instances | Separate card/strip instances + a live-state mirroring layer |
| Live state across windows | Single instance, identical, nothing to sync | Mirroring layer (volume/mute/meters/now-playing/seek/connection) |
| Live update on toggle | `OnPropertyChanged` re-raise, no rebuild | Card rebuild on toggle (loses transient UI state) |
| Data-templated scopes (strip, rule chips) | `NowPlayingStripView` / `RuleSummary.Options` carry the view's options | Unreachable from a host-view DP (half-compact card) |
| Number of compact knobs | One `CompactCards` flag per options object | Per-card flags (more state, more sync) |

## Consequences

- A new display-dependent measurement is added as a getter on `PeakMeterOptions` and re-raised in `OnCompactCardsChanged` (or the relevant `On…Changed`). Keep the roomy branch returning the pre-compact value so normal layout never shifts.
- A new combine-with-data flag is added to `CardPresentation` (option folded with card data) and re-raised in `RaiseAll`, then bound as `Presentation.X`.
- A new per-window QC override is a setting on `AppSettings` (mirrored in `SyncQuickMeterOptions`) plus a card under the Quick Controls settings expander. It must read from `QuickMeterOptions`, never the global, so the main window is untouched.
- Live state stays single-instance. If a future need can't be expressed as a styling option (it needs genuinely different live data per window), that is the trigger to give QC its own projected card instances with a sync layer - and this ADR is where that cost is documented.

### Slider height gotcha (volume + now-playing seek)

The WinUI `Slider` template forces a ~32 px min body (`SliderHorizontalHeight`) with the thumb sitting below the control's geometric centre. Two non-fixes we tried and rejected:

- Overriding the `SliderHorizontalHeight` resource in `Slider.Resources` does **nothing** (the template doesn't pick up the instance override).
- A `MaxHeight` clamp shrinks the control but does **not** re-centre the thumb, so it clips.

What works: keep the slider its natural size and place it (centre-aligned) inside a **fixed-height, non-clipping host**; the slim row is the host's height, the slider overflows it invisibly (transparent track), and a small DIP-based negative top margin lifts the low-sitting thumb back onto the host centre. The compact volume row and the now-playing seek host both use this.
