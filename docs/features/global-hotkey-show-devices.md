# Feature spec: Quick Controls (global-hotkey device overlay)

Handoff spec for an agent picking this up cold. Read [AGENTS.md](../../AGENTS.md) first for the
build/run loop and conventions. This is mostly an `Earmark.App` change (a new window, a new
view-model projection, a global-hotkey service, two new persisted flags, a settings capture control),
plus small additions to `AppSettings` and `DeviceDefaultsService`.

## Goal

A global hotkey (default `Ctrl+Win+V`, user-rebindable) toggles a floating **Quick Controls** overlay:
a single vertical column of device cards, on top of every other window, that lets the user control
audio devices without bringing Earmark to the foreground. It works while Earmark is hidden in the tray.

The overlay shows only the device cards (and groups) the user has **pinned to Quick Controls** - a new
per-device / per-group state, distinct from the existing internal visibility-pin. The cards have a
solid fill and rounded corners exactly as on the Devices page, but **there is no surface behind them**:
the desktop (or whatever window is behind the overlay) shows through the gaps between cards.

Positioning: anchored to the bottom-right of the work area, where the Windows notification / Action
Center flyout appears. This is a standalone topmost window, **not** an integration with the OS
notification shade.

## Decisions (locked with the product owner)

- **Name:** "Quick Controls". The pin action is "Pin to Quick Controls".
- **Hotkey:** user-configurable in Settings, default `Ctrl+Win+V`. Registration failure (combo already
  taken) surfaces a non-blocking warning; the feature stays inert until rebound.
- **Interaction in the overlay:** volume sliders + mute toggles work; app-indicator chips render
  (read-only, with their peak underbar). No drag-reorder, grouping, customise dialog, or rule
  navigation - those stay in the main window.
- **What appears:** only quick-pinned blocks. A device that belongs to a group **cannot be pinned
  individually** - only the whole group can be pinned (groups are atomic blocks everywhere else, and
  this keeps it consistent).
- **Pin requires a shown card; hide and pin are mutually exclusive.** A page-hidden device can't be
  pinned. Hiding a pinned device removes its pin, after a confirm popup ("This device is pinned to
  Quick Controls. Hiding it will remove it from Quick Controls."). Pinning also keeps a card shown on
  the page (it overrides auto-hide-for-no-rules), so a pinned card never silently drops out of the
  overlay.
- **Pin affordances:** a "Pin to Quick Controls" context-menu item on lone device cards and on group
  headers, plus a pin icon at the top-right of a lone card that appears on hover / keyboard focus
  (auto-hiding, like the page's floating "..." button) and stays shown (subtle) while pinned.
- **Scrollable:** if the pinned set is taller than the work area, the overlay scrolls with the
  mouse wheel.
- **Default seed:** on a fresh install (and once for existing users on upgrade), auto-pin the default
  output/input devices - the "Default Devices" group if it formed, otherwise the individual default
  render + capture devices - so the overlay isn't empty on first use.

## Current architecture (what exists today)

### Devices page card/block machinery
- [`HomeViewModel`](../../src/Earmark.App/ViewModels/HomeViewModel.cs) is a **singleton** (registered in
  [HostBuilderExtensions.cs:59](../../src/Earmark.App/Hosting/HostBuilderExtensions.cs#L59)). It owns
  `ObservableCollection<object> Blocks` (each item a lone [`DeviceCard`](../../src/Earmark.App/ViewModels/DeviceCard.cs)
  or a [`DeviceGroupCard`](../../src/Earmark.App/ViewModels/DeviceGroupCard.cs)) and a private
  `_visibleCards` flat list. `SyncBlocks()` rebuilds both from `_allCards` applying the page's
  hidden/disconnected filters.
- Cards are built on a background thread as immutable
  [`DeviceCardSnapshot`](../../src/Earmark.App/ViewModels/DeviceCardSnapshot.cs) records, then reconciled
  onto reused `DeviceCard` instances on the UI thread (`ReconcileAllCards`, `RefreshFrom`). **Threading a
  new per-device flag means adding it to the snapshot record and the reconcile, not just the card.**
- Peak meters + app chips update on a single 20Hz `DispatcherTimer` (`OnPeakTick` ->
  [HomeViewModel.cs:314](../../src/Earmark.App/ViewModels/HomeViewModel.cs#L314)) that iterates
  **`_visibleCards` only**. The page pauses/resumes this poll on `HomePage` `Loaded`/`Unloaded`
  ([HomePage.xaml.cs:52](../../src/Earmark.App/Views/HomePage.xaml.cs#L52)).
- The `DeviceCardTemplate` and `DeviceGroupCardTemplate` live in
  [HomePage.xaml](../../src/Earmark.App/Views/HomePage.xaml) `Page.Resources`. The card already has a
  `ContextFlyout` MenuFlyout (hide/show, Customise, Forget, ungroup/rename/delete group) bound to
  `DeviceCard` commands - the pattern to follow for the new "Pin to Quick Controls" item. There was
  previously an always-on eye button at the card's top-right; it was replaced by the right-click menu,
  so a top-right hover affordance is established prior art.

### Per-device / per-group persistence
- Per-device flags live in [`DeviceConfig`](../../src/Earmark.App/Settings/AppSettings.cs#L241), keyed by
  the stable device key in `AppSettings.Devices`. Nullable bools so unset flags are omitted from JSON;
  `IsDefault` prunes all-default entries on save. **Note the existing `DeviceConfig.Pinned`** means
  "pinned visible on the page (override auto-hide-no-rules)" - it is NOT the overlay pin. Do not reuse it.
- `UpdateDeviceConfig(card)` ([HomeViewModel.cs:1960](../../src/Earmark.App/ViewModels/HomeViewModel.cs#L1960))
  serialises a card's flags back into the map and prunes when default; `QueueSettingsSave()` debounces
  the write. Customisation changes flow through `OnCardCustomisationChanged` -> `UpdateDeviceConfig`.
- Groups persist as [`DeviceGroup`](../../src/Earmark.App/Settings/AppSettings.cs#L277) records
  (`Id`, `Title`, `MemberIds`) in `AppSettings.DeviceGroups`. The `DeviceGroupCard` VM is reused by id;
  `SyncFrom(title)` refreshes it without firing the change callback.
- `AppSettings.SettingsSchemaVersion` drives one-time migrations (currently
  `DeviceKeySchemaVersion = 1`, the endpoint-id -> device-key re-key). Bump it for the seed below.

### Window, tray, single-instance
- [`Program.Main`](../../src/Earmark.App/Program.cs) is the custom entry (single-instance via
  `AppInstance.FindOrRegisterForKey`). A second launch redirects to `App.RestoreFromBackground`.
- [`WindowChromeManager`](../../src/Earmark.App/Services/WindowChromeManager.cs) (singleton) **already
  subclasses the main window HWND** (`SetWindowSubclass`, handling `WM_SYSCOMMAND`/`WM_GETMINMAXINFO`)
  and owns the tray icon (`H.NotifyIcon`). It exposes `RestoreWindow` / `HideToTray` / `RequestExit`.
  When hidden to tray the window is `AppWindow.Hide()`d, **not closed - the HWND and the dispatcher
  stay alive**, so a hotkey registered against it keeps firing while in the tray.
- No global-hotkey infrastructure exists yet (`RegisterHotKey` / `WM_HOTKEY`: not found anywhere).

### Settings UI
- [`AppSettings`](../../src/Earmark.App/Settings/AppSettings.cs) is the plain settings POCO; toggles are
  surfaced in [SettingsPage.xaml](../../src/Earmark.App/Views/SettingsPage.xaml) via
  [`SettingsViewModel`](../../src/Earmark.App/ViewModels/SettingsViewModel.cs). There is **no
  shortcut-capture control** in WinUI 3 / the Windows App SDK / the Community Toolkit; one must be built
  (see the gotcha below).

## Part A - Pinning

### A1. Persistence
- Add `bool? PinnedToQuickControls` to [`DeviceConfig`](../../src/Earmark.App/Settings/AppSettings.cs#L241).
  Include it in `IsDefault` (so the entry still prunes when it's the only-and-false flag) and in
  `UpdateDeviceConfig`'s serialisation.
- Add `bool PinnedToQuickControls` to [`DeviceGroup`](../../src/Earmark.App/Settings/AppSettings.cs#L277).
- Thread the device flag through [`DeviceCardSnapshot`](../../src/Earmark.App/ViewModels/DeviceCardSnapshot.cs)
  (new ctor param) -> a `DeviceCard.IsQuickPinned` observable -> `RefreshFrom`. The group flag flows
  through the group reconcile (where `DeviceGroupCard` instances are created / `SyncFrom`'d) onto a
  `DeviceGroupCard.IsQuickPinned` observable.

### A2. Toggle commands
- `DeviceCard`: a `ToggleQuickPinCommand` that flips `IsQuickPinned`, calls `UpdateDeviceConfig(card)`
  + `QueueSettingsSave()`, then notifies the overlay projection (see B2). **Guard:** offer this only on
  a card that is shown (`!IsEffectivelyHidden`) and is not a group member - hide/disable the menu item
  and the hover icon otherwise. To pin a no-rule device that auto-hides, the user force-shows it first
  (the existing hide toggle does that).
- `DeviceGroupCard`: a `ToggleQuickPinCommand` that flips the group's `PinnedToQuickControls`, writes
  it into the matching `AppSettings.DeviceGroups` record, saves, and notifies the projection. Wire it
  through the same `_onChanged` callback path the title edit already uses, or a dedicated callback.

### A3. UI affordances (in [HomePage.xaml](../../src/Earmark.App/Views/HomePage.xaml))
- **Context menu:** add a "Pin to Quick Controls" / "Unpin from Quick Controls" `MenuFlyoutItem`
  (toggle label + filled/outline pin glyph) to the card `ContextFlyout` (lone cards only) and to the
  group header `ContextFlyout`. Mirror it in the app-chip flyout's owner-card block if you want chips
  to offer it too (optional; keep parity minimal).
- **Hover/focus pin icon:** a small pin button at the lone card's top-right. Auto-hide with the same
  opacity pattern as `OverflowButton` ([HomePage.xaml.cs:73](../../src/Earmark.App/Views/HomePage.xaml.cs#L73)):
  opacity 1 while the pointer is over the card or it/the button has focus, else 0 - **but** when the
  card is pinned, keep a subtle persistent pin glyph shown so the pinned state is legible at rest, and
  let hover/focus surface the full clickable toggle. Per-card hover is cleanest via the card `Border`'s
  `PointerEntered`/`PointerExited` (the cards live in an `ItemsRepeater`, so a page-level pointer flag
  won't disambiguate which card). For groups, put the pin toggle in the title band next to the title.
- Suggested glyphs (verify in the in-app glyph browser, `DeviceGlyphMapper.AllFluentGlyphs`):
  Pin `E718`, Unpin `E77A`, Pinned/filled `E840` (Segoe Fluent Icons codepoints).

### A4. Visibility interaction (pin and hide are mutually exclusive)
- **Quick-pin is a force-show.** In
  [`DeviceCard.IsEffectivelyHidden`](../../src/Earmark.App/ViewModels/DeviceCard.cs#L441) return shown
  when `IsQuickPinned`, alongside the existing `IsPinnedByUser` override. A quick-pinned card is
  therefore always in `Blocks` / `_visibleCards` (this is what removes the polling gotcha in B2). Add
  `OnIsQuickPinnedChanged` -> `OnPropertyChanged(nameof(IsEffectivelyHidden))` so the page re-filters.
- **Hiding a pinned card confirms, then unpins.** The hide is
  [`DeviceCard.ToggleUserVisibility`](../../src/Earmark.App/ViewModels/DeviceCard.cs#L742), invoked from
  the card `ContextFlyout` and the app-chip flyout's owner-card block. Route the hide item through a
  HomePage code-behind Click handler (like `OnForgetDeviceClicked`,
  [HomePage.xaml.cs:259](../../src/Earmark.App/Views/HomePage.xaml.cs#L259)): if the card is about to be
  hidden (`!IsEffectivelyHidden`) **and** `IsQuickPinned`, show a confirm `ContentDialog` (reuse the
  `ConfirmDisbandAsync` pattern, [HomePage.xaml.cs:1086](../../src/Earmark.App/Views/HomePage.xaml.cs#L1086)).
  On confirm, clear the quick-pin (persist) then toggle visibility; on cancel, do nothing. Cover both
  the card menu's hide item and the chip menu's `OwnerCard` hide item.
- Copy: "This device is pinned to Quick Controls. Hiding it will remove it from Quick Controls."
  Buttons "Hide and unpin" / "Cancel".

## Part B - the Quick Controls overlay

### B1. The window
- New `QuickControlsWindow : Window` (+ a slim page/root). Register as a singleton in
  [HostBuilderExtensions.cs](../../src/Earmark.App/Hosting/HostBuilderExtensions.cs); create lazily on
  first activation and keep it hidden between uses (cheaper than recreate).
- Chrome via `AppWindow` + `OverlappedPresenter`:
  `SetBorderAndTitleBar(false, false)`, `IsAlwaysOnTop = true`, `IsResizable = false`,
  `IsMaximizable = false`, `IsMinimizable = false`, `IsShownInSwitchers = false` (keep it out of
  alt-tab and the taskbar). No `SystemBackdrop`.
- **Transparency is the #1 technical risk - spike it before building the rest.** The hard requirement
  is per-pixel see-through gaps between opaque cards, not one translucent panel. WinUI 3 does not make
  a window transparent by default (a transparent root typically composites against black). Approaches,
  cheapest first:
  1. Transparent window via the supported path: `SystemBackdrop = null`, root `Background="Transparent"`,
     and verify on the target Windows 11 build (see the **WinUI 3 Gallery 2.9** "transparent window"
     sample, shipped ~May 2026). Empirically confirm the desktop shows through the gaps.
  2. If (1) doesn't yield true transparency, fall back to a layered window (`WS_EX_LAYERED`) + DWM
     P/Invoke (`DwmExtendFrameIntoClientArea` / per-pixel alpha), or pull in WinUIEx's transparent-window
     helper. Note `SwapChainPanel` does not support transparency (not used here, but relevant if effects
     are added).
  Do **not** ship an acrylic/Mica panel behind the cards - that violates the "see the desktop between
  the gaps" requirement.
- **Layout:** single column. Width ~360-400 DIP (one card column; the page's min item width is 320 +
  card padding). Vertical `StackPanel`/`ItemsRepeater` with `SpacingMedium` between blocks, cards
  carrying their own `SectionCardStyle` fill + `CardCornerRadius`. A pinned group renders as its title
  + members stacked vertically in the one column.
- **Scroll:** wrap the column in a `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`) so the mouse
  wheel scrolls when the pinned set exceeds the work-area height. Size the window height to the content,
  capped to the work-area height; only then does the scroll engage.
- **Positioning:** anchor bottom-right of `DisplayArea.WorkArea` (use the monitor under the cursor or
  the foreground window, falling back to primary), inset a small margin from the edges, sitting above
  the tray. `AppWindow.Move` after sizing. Optional slide+fade in (reuse the toast animation idea in
  [MainWindow.xaml.cs:128](../../src/Earmark.App/MainWindow.xaml.cs#L128)).
- **Dismiss / toggle:** the hotkey toggles visibility. Also dismiss on window deactivation (focus lost)
  and on `Esc`. Show on the monitor with the cursor. Don't aggressively steal focus from games, but the
  window must accept pointer + wheel input for the sliders and scrolling.

### B2. Data source - reuse the singleton HomeViewModel
- The overlay binds the **same** `DeviceCard` / `DeviceGroupCard` instances as the page (VMs are not
  `UIElement`s, so two ItemsRepeaters can template the same VM concurrently - each realised visual is
  independent). This keeps volume/mute/peak state coherent across the page and the overlay with no
  duplication.
- Add a derived projection on `HomeViewModel`, e.g. `ObservableCollection<object> QuickControlBlocks`,
  containing exactly: quick-pinned groups (`DeviceGroupCard.IsQuickPinned`) and quick-pinned lone cards
  (`DeviceCard.IsQuickPinned` and not a group member). Rebuild it in `SyncBlocks` and whenever a pin
  toggles. The overlay binds this; it uses a slim single-column template (no drag/group/customise/rule
  handlers - just the tile, name/pills, volume row, and the read-only apps row).
- **Gotcha - keep the meter timer alive for the overlay.** Because a quick-pinned card is always shown
  (A4), it's already in `_visibleCards`, so the existing 20Hz `OnPeakTick` covers it - no need to
  extend the poll set. But that timer is paused on `HomePage.Unloaded`, so when the overlay is open
  while HomePage isn't loaded (e.g. launched to tray), meters/chips would freeze. Keep the timer
  running while the overlay is visible: pause only when HomePage is unloaded **and** the overlay is
  hidden. Also ensure the singleton `HomeViewModel` is resolved when the overlay first opens (a
  tray-only launch may never have navigated to HomePage); `SyncBlocks` runs on every rebuild
  regardless of the page, so `_visibleCards` is already populated once the VM exists.

### B3. Global hotkey
- New `IGlobalHotkeyService` (singleton). Recommended: own a dedicated **message-only window**
  (`HWND_MESSAGE`) created on the UI thread, so the hotkey is independent of the main window's
  visibility and lifecycle. (Registering against the main window HWND also works because it survives
  hide-to-tray, but a dedicated window is cleaner separation.)
- `RegisterHotKey(hwnd, id, fsModifiers, vk)` with `fsModifiers` from the parsed combo
  (`MOD_CONTROL | MOD_WIN | MOD_NOREPEAT`, etc.) and `vk` the virtual-key. Handle `WM_HOTKEY` in the
  window proc -> raise an event the overlay subscribes to (toggle show/hide). `UnregisterHotKey` on
  dispose and before re-registering after a rebind.
- **Registration failure** (`RegisterHotKey` returns false, combo taken): log it, surface a
  non-blocking toast via [`IInAppNotificationService`](../../src/Earmark.App/Services/IInAppNotificationService.cs)
  ("Couldn't register Ctrl+Win+V - it may be in use by another app"), and leave the feature inert until
  the user picks a free combo. Reflect the failed state in Settings.
- Start the service in `App.OnLaunched` after the host is built (alongside the other startup services).

## Part C - Settings + shortcut capture

### C1. Settings model
- Add to [`AppSettings`](../../src/Earmark.App/Settings/AppSettings.cs):
  `bool QuickControlsEnabled { get; set; } = true;` and
  `string QuickControlsHotkey { get; set; } = "Ctrl+Win+V";` (store the human string; parse to
  modifiers+vk at registration). Re-register on change.

### C2. Shortcut-capture control (no native control exists)
- Confirmed: **WinUI 3 / Windows App SDK / Windows Community Toolkit ship no shortcut-recorder
  control.** The PowerToys "Activation shortcut" dialog is their bespoke `ShortcutControl` (MIT,
  in the PowerToys repo) - use it as the reference model, not a dependency.
- Build a small recorder dialog: a "Change shortcut" button (in a `SettingsCard` for "Quick Controls
  shortcut") opens a `ContentDialog` that records keys, renders keycaps, and validates the combo,
  with Save / Reset / Cancel.
- **Critical:** capturing a **Windows-key** combo cannot be done with normal XAML `KeyDown` - the OS
  swallows the Win key before it reaches the app. The recorder must install a **low-level keyboard hook
  (`SetWindowsHookEx(WH_KEYBOARD_LL)`)** while the dialog is open (uninstall on close), track the
  modifier set + the final non-modifier key, and `return 1` from the hook to suppress the keys while
  recording so they don't leak to other apps. This is exactly why PowerToys uses a hook to show
  `Win + Shift + S` etc.
- Validation: require at least one modifier, and prefer requiring `Ctrl`/`Alt`/`Win` (a bare `Shift+<key>`
  global hotkey is hostile). On Save, persist the string, re-register via `IGlobalHotkeyService`, and if
  registration fails show the warning and keep the dialog open / revert.

## Part D - default seed

Goal: a first-time user (and existing users on upgrade) get a non-empty overlay, but a user who later
**unpins everything on purpose must not be re-seeded**. Gate on a schema marker, not on "the set is
empty".

- **Fresh install:** in
  [`DeviceDefaultsService.ApplyDefaultDeviceLayout`](../../src/Earmark.App/Services/DeviceDefaultsService.cs#L92),
  after the "Default Devices" group is built (`AddGroup` / `PinAll`), also set that group's
  `PinnedToQuickControls = true`. If the group didn't form (fewer than two default devices present),
  set `PinnedToQuickControls = true` on the default render + default capture `DeviceConfig` entries
  instead. Seeding only runs on a blank slate (`HasExistingConfig()` gate), so this can't clobber a
  configured user.
- **Existing users (one-time):** add `QuickControlsSeedSchemaVersion = 2` and a migration gated on
  `SettingsSchemaVersion < 2`: if nothing is quick-pinned yet, pin the block holding the current default
  render + capture devices (the group if both are in one, else the individual cards), then bump the
  version. Run it where live default-device data is available (the rebuild pass /
  `MaybeMigrateDeviceKeys` neighbourhood). The version bump is what guarantees it fires once and never
  re-seeds after the user clears the set.

## Gotchas (read before coding)

1. **`DeviceConfig.Pinned` is not the overlay pin.** It means "pinned visible on the page". The new flag
   is `PinnedToQuickControls`. Keep them separate in code and UI copy.
2. **Group members can't be pinned alone.** Hide/disable the per-card pin affordance when `IsGroupMember`;
   only the group header pins. The projection must never list a member card as a lone pinned block.
3. **Snapshot threading.** A new device flag has to ride `DeviceCardSnapshot` -> `RefreshFrom`, not just
   live on the card, or it won't survive the connect/disconnect rebuild reconcile.
4. **Meter timer lifecycle.** Quick-pinned cards are always shown (A4), so always polled - but the 20Hz
   timer is paused with HomePage. Keep it running while the overlay is open, and resolve the VM on the
   overlay's first open (a tray-only launch may never have navigated to HomePage) (B2).
5. **Win-key capture needs a low-level hook** (C2). Plain `KeyDown` can't see it.
6. **Transparency** (B1) is the riskiest piece - spike it first; don't let it block the rest of the work.
7. **Tray lifecycle.** The hotkey must work while the main window is hidden to tray. A message-only
   window (or the persisting main HWND) satisfies this; don't tie registration to window visibility.
8. **No em-dashes, conventional commits, no AB# suffix** (this repo isn't ADO-linked). See AGENTS.md.
   Likely commits: `feat: add Quick Controls pin state and overlay`, `feat: add global hotkey service`,
   `feat: seed default Quick Controls pins`.

## Acceptance / how to verify (per AGENTS.md build-run loop)

- Right-click a lone card -> "Pin to Quick Controls"; the pin icon shows at top-right and persists at
  rest; the flag survives a relaunch (check `settings.json` `Devices.<key>.PinnedToQuickControls`).
- Pin a group from its header; members show in the overlay under the group title.
- A group member offers no individual pin (menu item + hover icon absent).
- A page-hidden device offers no pin option; force-show it and the pin option appears.
- Hide a pinned device: a confirm appears; confirming removes it from the overlay and unpins, cancelling
  leaves it pinned and shown.
- Press `Ctrl+Win+V` (with the main window both visible and hidden-to-tray): the overlay appears
  bottom-right, on top of other windows, with the desktop/background visible between the cards. Press
  again / `Esc` / click away: it dismisses.
- Pin more cards than fit the screen height: the overlay scrolls with the mouse wheel, capped to the
  work area.
- Move a slider / toggle mute in the overlay: the device responds and the page reflects it (shared VMs).
- App chips appear on overlay cards with live peak bars when audio plays on a pinned device.
- Rebind the shortcut in Settings (including a Win-key combo); the old combo stops working, the new one
  works. A taken combo shows the warning and doesn't silently no-op.
- Fresh profile (rename `settings.json`): the overlay is pre-seeded with the default devices/group.
  Unpin everything, relaunch: it stays empty (no re-seed).
