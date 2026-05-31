# Device customisation panel

Let the user override a device card's **glyph** and **accent colour** from a right-click menu on the Devices page. Both are per-device overrides keyed by endpoint id, persisted in `settings.json`, and win over the auto-derived visuals (name-based glyph, Wave Link accent).

## Goal

On the Devices page (`HomePage`), the icon and accent tile of each card are auto-derived: the glyph from the device name via `DeviceGlyphMapper`, the accent colour from a Wave Link channel's artwork (or none). The user can't change either. This feature adds a "Customise..." entry to the existing card context menu that opens a small picker for:

1. **Glyph** - choose from the curated audio-device glyph set (the same icons `DeviceGlyphMapper` already uses), or "Auto" to fall back to the derived glyph.
2. **Colour** - choose an accent tile colour from a Fluent swatch palette (the peak-meter swatches plus more defaults), a custom colour via `ColorPicker`, or "Auto" to fall back to the Wave Link accent / default tile.

A user override always wins over the auto-derived value. "Auto" clears the override.

## Current state (what exists today)

- **Card context menu**: `Border.ContextFlyout` > `MenuFlyout` in [HomePage.xaml:64-117](../../src/Earmark.App/Views/HomePage.xaml#L64-L117). Existing items: hide/show device, show/hide volume controls, and group actions. Commands live on the `DeviceCard` view-model; group actions are `Click` handlers in [HomePage.xaml.cs](../../src/Earmark.App/Views/HomePage.xaml.cs) with `Tag="{x:Bind}"`.
- **Per-device VM**: [`DeviceCard`](../../src/Earmark.App/ViewModels/DeviceCard.cs). Exposes `Endpoint` (carries the stable `Endpoint.Id`), `Glyph` (string, [line 554](../../src/Earmark.App/ViewModels/DeviceCard.cs#L554)), `WaveLinkTileBrush` / `ShowAccentTile` / `ShowDefaultTile` ([lines 615-626](../../src/Earmark.App/ViewModels/DeviceCard.cs#L615-L626)).
- **Glyph precedence** ([line 554](../../src/Earmark.App/ViewModels/DeviceCard.cs#L554)): `_waveLinkGlyphOverride` (mix icon) > `_themedGlyph` (name-derived, resolved at construction) > render/mute fallback. There is **no user override slot yet** - this feature inserts one above all of these.
- **Accent tile**: `WaveLinkTileBrush` is `_waveLinkAccent` wrapped in a `SolidColorBrush`. `_waveLinkAccent` is set only by `SetWaveLinkVisual` ([line 593](../../src/Earmark.App/ViewModels/DeviceCard.cs#L593)). Three overlaid `FontIcon`s + tile borders are bound to `ShowAccentTile`/`ShowDefaultTile` so theme-dependent fills stay `{ThemeResource}`. An absolute user colour behaves like a Wave Link accent (theme-independent).
- **Curated glyph set**: [`DeviceGlyphMapper`](../../src/Earmark.App/ViewModels/DeviceGlyphMapper.cs) holds 10 Segoe Fluent glyphs as `private const string` (Game ``, Chat ``, Music ``, Monitor ``, Globe ``, Streaming ``, Headphones ``, Earbuds ``, Speakers ``, Microphone ``). Plus the two render/capture fallbacks (`` speaker, `` mic). These need to be exposed as a public, named, ordered list for the picker.
- **Colour swatches**: the peak-meter solid-colour picker in [SettingsPage.xaml:152-191](../../src/Earmark.App/Views/SettingsPage.xaml#L152-L191) (a `Button` > `Flyout` with an `ItemsControl` of swatch borders + a `ColorPicker` + reset). Swatch list is `FluentSwatches` in [SettingsPage.xaml.cs:14-25](../../src/Earmark.App/Views/SettingsPage.xaml.cs#L14-L25): 7 colours (teal `#00B7C3`, blue `#0078D4`, purple `#8764B8`, magenta `#E3008C`, red `#E81123`, orange `#F7630C`, gold `#FFB900`) plus the green default. Stored as `#AARRGGBB` hex via `PeakMeterOptions.ToHex` / `ColourFromHex`.
- **Per-device persistence**: [`DeviceConfig`](../../src/Earmark.App/Settings/AppSettings.cs#L173) in `AppSettings.Devices` (`Dictionary<string, DeviceConfig>`, keyed by `endpoint.Id`, case-insensitive). Nullable flags, pruned on save via `IsDefault`. `HomeViewModel.BuildCards` reads `cfg` per endpoint and passes flags into the `DeviceCard` constructor.

## Design decisions

- **Reuse, don't duplicate.** The colour picker is the established peak-meter pattern (`Button` > `Flyout` > swatch `ItemsControl` + `ColorPicker`). Extract the swatch markup + `ColorPicker` into a reusable `Controls/ColourPickerFlyout` (or a shared `UserControl`) so the Settings peak-meter picker and the device picker share one implementation and one swatch list. Per [AGENTS.md "Components and custom controls"](../../AGENTS.md), a one-off inline copy is not acceptable when two places need the same affordance. If extraction is too large for one pass, at minimum share the swatch colour list (move `FluentSwatches` to a single public source both consumers reference) and note the markup-sharing follow-up.
- **Picker surface**: a **`Flyout`** anchored to the menu, not a `ContentDialog`. Matches the peak-meter precedent, stays contextual to the card, and is non-modal. The right-click `MenuFlyout` can't host a rich sub-panel cleanly, so add a "Customise..." `MenuFlyoutItem` whose `Click` opens a separate `Flyout` anchored to the card (or open it as a sub-section). Confirm the anchoring approach during implementation - if a `MenuFlyoutSubItem` can host the swatch grid acceptably, prefer that; otherwise a `Click` handler that shows a `Flyout` on the card `Border`.
- **Glyph picker UI**: a wrap of selectable glyph buttons (each a `FontIcon` of one curated glyph) plus an "Auto" choice. Single-select; selection writes the chosen glyph string (or null for Auto).
- **Override precedence**: user override > snapped auto accent > default tile. Add a `_userGlyphOverride` checked **first** in the `Glyph` getter, and a `_userAccent` checked **first** in the accent-tile path. `ShowAccentTile`/`ShowDefaultTile` must treat a user accent the same as a Wave Link accent (absolute colour, theme-independent, suppressed while muted/rule-locked).
- **Snap the auto accent to the nearest Fluent palette colour (the default path).** When the user has *not* set a colour, do not render the raw Wave Link / icon-derived accent at face value. Snap it to the nearest colour in the Fluent palette (nearest by RGB Euclidean distance) and tint the tile with that. This is the resting state, not just a picker suggestion: a Wave Link device shows a Fluent-aligned accent out of the box. The snap happens where the auto accent is resolved (in `SetWaveLinkVisual` or at the point the tile brush is built) - store/use the snapped colour, not the artwork colour. When the picker opens, the swatch matching the snapped colour reads as the current selection.
- **Swatch palette = the full Windows personalisation accent palette**, the same (or visually equivalent) grid Windows shows under Settings > Personalisation > Colours > Accent colour (the ~48 Windows accent swatches), not the seven peak-meter colours. Define this palette once as the shared swatch source. The peak-meter picker can keep its smaller curated set or adopt the full palette - decide during the shared-control extraction, but the device picker uses the full palette.
- **Colour wheel hidden by default.** The picker shows the swatch grid only. A "Custom" affordance (button/toggle) reveals the `ColorPicker` (ring/spectrum + hex) for an off-palette colour. Default state is swatches-only; the wheel appears on demand.

## Persistence

Extend [`DeviceConfig`](../../src/Earmark.App/Settings/AppSettings.cs#L173):

```csharp
/// <summary>User-chosen glyph override (a single Segoe Fluent codepoint string). Null = derive
/// the glyph automatically from the device name / Wave Link channel.</summary>
public string? Glyph { get; set; }

/// <summary>User-chosen accent tile colour as "#AARRGGBB". Null = use the Wave Link accent (or
/// the default tile when there is none).</summary>
public string? AccentColour { get; set; }
```

Update `IsDefault` to also require `Glyph is null && AccentColour is null`, so an all-default entry still prunes. Reuse `PeakMeterOptions.ToHex` / `ColourFromHex` for the colour string round-trip (or move those helpers somewhere shared if `PeakMeterOptions` isn't a sensible home for a device colour).

`HomeViewModel.BuildCards` already does `deviceConfigs.TryGetValue(endpoint.Id, out var cfg)`; pass `cfg?.Glyph` and the parsed `cfg?.AccentColour` into the `DeviceCard` constructor (or a setter) alongside the existing flags.

## Work

1. **Expose the curated glyph set.** Promote the `DeviceGlyphMapper` glyph constants into a public, ordered, named list (e.g. `IReadOnlyList<(string Label, string Glyph)>`) the picker can bind to, including the speaker + mic fallbacks. Keep `DeviceGlyphMapper.TryResolve` working off the same source so the two don't drift.
2. **Extend `DeviceConfig`** with `Glyph` + `AccentColour` and update `IsDefault` (above).
3. **Add user-override slots to `DeviceCard`.** A `_userGlyphOverride` checked first in `Glyph`, and a `_userAccent` checked first in the accent-tile resolution. Add a method mirroring `SetWaveLinkVisual` (e.g. `SetUserCustomisation(string? glyph, Color? accent)`) that sets the fields, raises `OnPropertyChanged` for every affected visual property (`Glyph`, `WaveLinkTileBrush`, `ShowAccentTile`, `ShowDefaultTile`, the glyph-contrast brushes), and persists via the settings service. Decide ownership: either the card raises a "customisation changed" event the `HomeViewModel` persists, or the card writes through an injected settings service - follow whatever pattern the existing `ToggleUserVisibilityCommand` / hide-flag persistence uses (mirror it, don't invent a new path).
4. **Wire `BuildCards`** to seed the new override fields from `cfg`.
5. **Build the shared colour picker.** A reusable control in `src/Earmark.App/Controls/` driven by a `Color` dependency property. Default view = the **full Windows accent swatch grid** only. A "Custom" button/toggle reveals a `ColorPicker` (ring + hex) for off-palette colours; hidden until invoked. Define the full Fluent accent palette once as the shared swatch source. Point the new device picker at it; fold the Settings peak-meter picker onto the same control where it fits (it may keep its smaller curated swatch set, but must not duplicate picker markup).
   - **Snapping helper.** Add a `NearestSwatch(Color)` (RGB Euclidean distance) over the palette. Used both for the default auto-accent snap (step 8) and to mark the active swatch when the picker opens.
6. **Build the glyph picker.** A selectable wrap of the curated glyphs + an "Auto" option, single-select, bound to the card's current override (null => Auto selected).
7. **Add the menu entry + picker surface.** A "Customise..." `MenuFlyoutItem` (icon e.g. `` Personalize / ``) in the card `ContextFlyout` above the group section, opening the glyph + colour picker `Flyout`. On apply, call the card's `SetUserCustomisation`; "Auto" on either axis clears that axis's override.
8. **Snap the auto accent to the palette (default rendering).** Where the auto accent is resolved (in/around `SetWaveLinkVisual`), run the raw Wave Link / icon-derived colour through `NearestSwatch` and tint the tile with the snapped colour, not the artwork colour. This is the resting state for any device with no user colour set. When the picker opens, the swatch equal to the snapped colour reads as selected.
9. **Reset.** "Auto" entries on both axes must round-trip to a pruned `DeviceConfig`. A device with neither override set produces no `Devices` entry (or an entry only carrying other flags).

## Acceptance criteria

**Done when:**
- Right-clicking a device card shows a "Customise..." entry that opens a glyph + colour picker.
- Picking a glyph from the curated set changes the card icon immediately and survives relaunch.
- Picking a colour (swatch or custom) tints the card's accent tile immediately (theme-independent, suppressed while muted / rule-locked, matching Wave Link accent behaviour) and survives relaunch.
- A user override visibly wins over the Wave Link accent / themed glyph for that device.
- "Auto" on either axis clears that override and the card reverts to its derived visual.
- A device with both axes on "Auto" leaves no `glyph`/`accentColour` in its persisted `DeviceConfig` (entry pruned if no other flags).
- The colour picker shows the full Windows accent swatch palette; the wheel/`ColorPicker` is hidden until "Custom" is chosen.
- A Wave Link device with no user colour shows an accent **snapped to the nearest Fluent palette colour**, not the raw artwork colour; the matching swatch reads as selected when the picker opens.
- The device picker and the Settings peak-meter picker do not duplicate picker markup.

**Out of scope:**
- Renaming devices (the existing menu has group rename only; per-device rename is a separate feature).
- Custom uploaded glyph images / arbitrary icon fonts - only the curated Segoe Fluent set.

## Notes / gotchas (from AGENTS.md)

- Rebuild + relaunch after every change; verify against the running app, not a green build.
- `Padding`/`Margin` need a `Thickness`, never an `x:Double` `Spacing*` resource (throws at page load, not build time).
- Two-way `x:Bind` on a value-type `SelectedItem` NREs during recycling - use `Mode=OneWay` + a `SelectionChanged`/`Click` handler.
- Theme-dependent brushes must be `{ThemeResource}`; absolute accent colours (the user/Wave Link tile) are deliberately theme-independent.
- New shared UI goes in `src/Earmark.App/Controls/` as a reusable control with dependency properties, not page-local markup.
