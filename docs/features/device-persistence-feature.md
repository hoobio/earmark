# Feature spec: device persistence (remember disconnected devices)

Implementation prompt for an agent. Not yet built. Read [AGENTS.md](../AGENTS.md) first
(layout, the kill -> build -> relaunch loop, conventional commits, no emoji/gitmoji, no
AI-attribution footer). This is a Devices-page feature in `Earmark.App` plus a small surface in
`Earmark.Audio`.

## Why

When a device connects or disconnects, `EndpointsChanged` fires a **full card rebuild**:
[`HomeViewModel.RebuildAsync`](../src/Earmark.App/ViewModels/HomeViewModel.cs) discards every
`DeviceCard` (`_allCards.Clear()` then `BuildCards(...)` makes fresh instances) and `SyncBlocks`
replaces the whole `Blocks` collection. The `ItemsRepeater` therefore **recreates every element**,
so the page's always-on block slide animation has nothing to animate from: a surviving device's
card is a brand-new element that just appears at its new slot instead of sliding. (Drag-reorder
slides only because it reuses the same instances.) There's also a brief visual flash on every
device change for the same reason.

Persisting devices fixes this structurally: a disconnected device keeps its card (rendered
disabled / dimmed) instead of being removed, so connect/disconnect becomes a **state toggle on a
surviving element**, not an add/remove. No rebuild churn, so the existing slide animation works and
the flash goes away. Only a genuinely new device (first time ever seen) still rebuilds, which is
rare and acceptable.

## Goal

1. **Persist known devices.** A device the app has seen stays in the Devices list after it
   disconnects, shown disabled (reduced opacity, controls non-interactive, a "disconnected"
   affordance). It reactivates in place when it reconnects.
2. **Stable identity + dedup.** Persist by a device identity that survives a driver reinstall (which
   changes the audio endpoint id). Re-seeing the same physical device must reuse its existing card /
   order slot / group membership / config, not spawn a duplicate.
3. **Bluetooth connect/disconnect button.** For a Bluetooth device, a control in the card's
   top-right to connect/disconnect it - the same action as the Quick Settings flyout. This is
   **feasible** via the documented KS one-shot reconnect/disconnect property (see Part 3); it's just
   more interop-heavy than Parts 1+2.

Ship Part 1+2 first (they fix the slide), then Part 3 as a follow-up.

## Part 1 + 2: persistence and identity

### The central decision: device identity

Everything the app persists today is keyed by the **audio endpoint id** (the
`{0.0.0.00000000}.{guid}` string): `AppSettings.DeviceOrder`, `DeviceGroup.MemberIds`, the
`AppSettings.Devices` config map (`Hidden` / `Pinned` / `VolumeControlsHidden`). Rules match by
**friendly-name regex** (`DevicePattern`), not by id, so rules already survive an id change; order,
groups, and per-device config do **not**.

The endpoint id is **not stable** across a driver reinstall / OS update. The stable identity is the
**container id**:

- `DEVPKEY_Device_ContainerId` (`{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}, 2`), readable from the
  endpoint's property store (`IMMDevice::OpenPropertyStore` -> the container-id PROPERTYKEY; NAudio
  exposes `MMDevice.Properties`).
- For **Bluetooth** devices the container id is generated from the device MAC address, so it is
  stable across reinstalls and reboots (per
  [Container IDs for Bluetooth Devices](https://learn.microsoft.com/windows-hardware/drivers/install/container-ids-for-bluetooth-devices)).
- Container id is **not globally unique per endpoint**: one physical device (one container) can
  expose several endpoints (e.g. render + capture, or multiple jacks). So the persistence key is
  `containerId + flow + a within-container discriminator` (the endpoint's friendly-name tail, or the
  KS pin category / `PKEY_AudioEndpoint_Association`), not the container id alone.

**Recommended identity model:** introduce a `DeviceKey` (string) = `containerId|flow|discriminator`,
computed when an endpoint is first seen. Persist a known-devices table keyed by `DeviceKey`, each
row carrying: the `DeviceKey`, the last-seen endpoint id, friendly name, flow, and last-seen
timestamp. On every enumeration, resolve each live endpoint to its `DeviceKey` and:

- match an existing row -> reuse it (and refresh its last-seen endpoint id), OR
- no match -> new device (create a row).

Dedup fallback when container id is unavailable (some virtual/legacy endpoints return none): fall
back to matching on normalised friendly name + flow. Log when this fallback is used.

**Migration / compatibility:** the existing id-keyed stores (`DeviceOrder`, `DeviceGroups`,
`Devices`) must not break. Two viable approaches, pick one and document it in an ADR under
`docs/adr/`:

- **(A) Re-key to `DeviceKey`.** Migrate the three stores from endpoint id to `DeviceKey` on load
  (one-time, with the live enumeration providing the id->key map; unresolved ids are dropped or kept
  as raw-id rows). Cleanest long-term; order/groups/config then survive reinstalls too.
- **(B) Id-alias layer.** Keep the stores keyed by endpoint id, add a `DeviceKey -> currentEndpointId`
  resolver, and rewrite a device's persisted id to the new one when its `DeviceKey` reappears with a
  changed id. Less invasive, but every store lookup goes through the resolver.

Recommendation: **(A)**, because the whole point is stable identity and it removes the alias
indirection. Gate it behind a settings-schema version bump with a one-time migration.

### Card list = live + persisted-absent

Today `BuildCards` enumerates only **live, active** endpoints. Change the card set to the **union**
of:

- live endpoints (as now), and
- persisted known devices whose `DeviceKey` is not currently live (the disconnected ones).

A disconnected card is built from the persisted row (friendly name, flow, last endpoint id for
display), marked `IsConnected = false`. Sort/order/group logic is unchanged - a disconnected device
keeps its slot in `DeviceOrder` / its group, so it sits where the user left it.

Pruning: never auto-drop on disconnect (that's the feature). Provide a user action to forget a
device (context menu "Forget device", visible on disconnected cards), and consider a cap / age-out
(e.g. forget devices not seen in N days, or beyond a count cap) with the dropped ones logged. Make
the cap a constant, not a magic number sprinkled around.

### The instance-reuse requirement (this is what makes it animate)

Persisting devices only removes the slide problem **if the surviving `DeviceCard` instances are
reused across a rebuild** - otherwise the `ItemsRepeater` still recreates elements. Today
`RebuildAsync` always news-up cards. Change the rebuild to **reconcile `_allCards` by `DeviceKey`**
on the UI thread (in the `_dispatcher.Enqueue` block of `RebuildAsync`): for each rebuilt entry,
reuse the existing card instance for the same `DeviceKey` and refresh its state in place; create a
new instance only for a `DeviceKey` not already present; drop instances whose device is neither live
nor persisted.

`BuildCards` runs on a background thread (`Task.Run`) and must keep producing plain data (it must
not touch existing `DeviceCard` instances - their `ObservableProperty` setters fire on the UI
thread). So have `BuildCards` return a list of value snapshots (endpoint + resolved `DeviceKey` +
volume + mute + rule-summary + config + connected flag), and do the reuse/refresh on the UI thread.

`DeviceCard` needs an in-place refresh method (e.g. `RefreshFrom(snapshot)`) that updates and raises
change notifications for everything currently set only in its constructor:

- `Endpoint` (a new `AudioEndpoint` instance each enumeration; `IsDefault` /
  `IsDefaultCommunications` can change when defaults shift). Updating it must re-raise every
  Endpoint-derived binding (`IsDefault`, `IsDefaultCommunications`, `DefaultPillText`,
  `DeviceIdSubtext`, etc.). `DeviceNameOnly` / glyph are derived from the friendly name and are
  stable for the same device, so they can stay.
- volume / mute (there is already `SetVolumeAndMute`), the rule-lock state
  (`IsVolumeLockedByRule`, `IsMuteLockedByRule`, `RuleMutedTarget/Source`, `RuleVolumeSource`),
  `Rules` + `AdditionalRules`, the config flags, and the new `IsConnected`.

This refresh is the **highest-risk part** (it touches rule/default correctness on every device
change). Cover it carefully and verify against the log lines in
[AGENTS.md](../AGENTS.md) "Verifying behaviour after a change".

### Disconnected-card UX

- Reduced opacity on the whole card (reuse the existing card-opacity binding pattern;
  `DeviceCard.CardOpacity` already exists - add a disconnected tier).
- Volume slider, mute toggle, and app-chip drop disabled / non-interactive while disconnected.
- A clear "disconnected" affordance (a pill or a muted-grey state badge). Keep it Fluent 2, match
  Settings; do not invent a one-off colour.
- The context menu still offers "Forget device" (and, for BT, connect - see Part 3).
- Rules chip stays visible (the rule still targets the device; it just can't apply while absent).
- **Do not** add an implicit opacity hide animation on the card container - see the AGENTS.md /
  HomePage note about recycled containers coming back stuck at opacity 0. Drive the dim via a bound
  property only.

### Reactivity

Prefer the event path over polling (AGENTS.md "Reactivity preferences"). Device arrival/removal and
state changes already arrive via `IMMNotificationClient` -> `EndpointsChanged`. Persisted-device
reconciliation should hang off that event, not a timer. The existing debounce in `RebuildAsync`
stays.

## Part 3: Bluetooth connect/disconnect button

### Feasibility (research, May 2026): yes, the same path Windows uses

Windows' own Connect/Disconnect (the Quick Settings flyout, and the Control Panel Sound dialog)
drives the audio driver through a **kernel-streaming property**, and it's reachable from user space:

- Property set [`KSPROPSETID_BtAudio`](https://learn.microsoft.com/windows-hardware/drivers/audio/kspropsetid-btaudio)
  with [`KSPROPERTY_ONESHOT_RECONNECT`](https://learn.microsoft.com/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect)
  (connect) and [`KSPROPERTY_ONESHOT_DISCONNECT`](https://learn.microsoft.com/windows-hardware/drivers/audio/ksproperty-oneshot-disconnect)
  (disconnect), defined in `ksmedia.h` (the [`KSPROPERTY_BTAUDIO`](https://learn.microsoft.com/windows-hardware/drivers/ddi/ksmedia/ne-ksmedia-ksproperty_btaudio)
  enum), Windows 7+. The docs state plainly: "When an audio driver supports these properties, the
  Sound dialog box in the Control Panel exposes Connect and Disconnect commands" - this is exactly
  the mechanism behind the buttons in the user's screenshot.
- Sent as a KS property request (descriptor type `KSPROPERTY`, target = **Filter**, no value;
  invoked as the GET form - it's a one-shot trigger, not a readback) to the BT endpoint's KS filter,
  via `IKsControl::KsProperty` or `DeviceIoControl(IOCTL_KS_PROPERTY, ...)` on the filter handle.
- **Reference implementation:** [ToothTray](https://github.com/m2jean/ToothTray) (C++) does exactly
  this - opens the BT audio endpoint's `IKsControl` and sends `KSPROPERTY_ONESHOT_RECONNECT` /
  `KSPROPERTY_ONESHOT_DISCONNECT`. Use it as the model.
  [PolarGoose/BluetoothDevicePairing](https://github.com/PolarGoose/BluetoothDevicePairing) is a
  second working example.

Caveats (design for them; none are blockers):
- The request is **fire-and-attempt**: STATUS_SUCCESS means the driver *tried*, not that the link is
  up. Reflect real state from connection status (`KSPROPERTY_JACK_DESCRIPTION`'s `IsConnected`, or
  just let the `IMMNotificationClient` device-state events settle the card's `IsConnected`), not from
  the call's return.
- A headset exposes several endpoints (A2DP render, HFP render/capture). The one-shot acts at the
  filter level, so to connect/disconnect the *device* send it to the relevant endpoint(s) for that
  container, not just one.
- `BluetoothSetServiceState` (toggling the A2DP `110b` / HFP `111e` service GUIDs) is an alternative
  path, but it's reported as slow and unreliable for *disconnect* - prefer the KS one-shot.
- This is Win32/KS interop: keep it in `Earmark.Audio` behind a small service interface (e.g.
  `IBluetoothAudioControl` with `IsBluetooth(endpoint)`, `Connect(...)`, `Disconnect(...)`), mirroring
  `IProcessControlService` / `IPolicyConfigVista`. The view-model and `DeviceCard` stay
  Windows-API-free.

This is more interop-heavy than Parts 1+2, so land it as a follow-up commit - but it is **feasible**,
not a "might be impossible" spike. (An earlier draft of this doc wrongly said there was no API; the
KS one-shot is the API.)

### Mapping an audio endpoint to its Bluetooth device

The endpoint -> BT-device link (to know which endpoints belong to one headset, and to show the
button only on BT cards) goes through the **container id**:

- Read the endpoint's container id (same `DEVPKEY_Device_ContainerId` used for identity above).
- Enumerate Bluetooth devices via `Windows.Devices.Enumeration.DeviceInformation.FindAllAsync` with
  the Bluetooth selector, read each `System.Devices.Aep.ContainerId` /
  `System.Devices.AepContainer.ContainerId`, and match the audio endpoint's container id to the BT
  device's container.
- Detect "this endpoint is Bluetooth" via the AEP class-of-device audio service
  (`System.Devices.Aep.Bluetooth.Cod.Services.Audio`) or the endpoint's enumerator/bus type. Only
  show the button for BT endpoints.

Capability: the app already runs unpackaged self-contained; confirm whether the
`bluetooth` capability / a manifest entry is needed for the enumeration calls on this build.

## Codebase pointers

- [`HomeViewModel`](../src/Earmark.App/ViewModels/HomeViewModel.cs): `RebuildAsync` (rebuild +
  `_dispatcher.Enqueue` reuse point), `BuildCards` (make it return snapshots), `_allCards`,
  `SyncBlocks` (in-place block reconcile - already preserves instances, so the reuse must happen at
  the `_allCards` level feeding it), `OnAnythingChanged` (the `EndpointsChanged` hookup).
- [`DeviceCard`](../src/Earmark.App/ViewModels/DeviceCard.cs): add `IsConnected` + `RefreshFrom`,
  add the disconnected opacity tier to `CardOpacity`, gate `IsVolumeEditable` / drop-target /
  mute on `IsConnected`. Note `IBlockLayoutInfo` (`StretchToRowHeight`) so a disconnected card still
  lays out normally.
- [`AppSettings`](../src/Earmark.App/Settings/AppSettings.cs): add the known-devices table; decide
  the migration for `DeviceOrder` / `DeviceGroups` / `Devices` per the identity section. Bump the
  settings schema and add a one-time migration.
- `Earmark.Audio` endpoint service + the `AudioEndpoint` model (where endpoints are enumerated via
  NAudio's `MMDeviceEnumerator`): surface `ContainerId` (and a BT flag) on `AudioEndpoint`, read
  from `MMDevice.Properties`. Keep Windows interop in `Earmark.Audio`, per the architecture
  boundary.
- [`HomePage.xaml` / `.cs`](../src/Earmark.App/Views/HomePage.xaml): the always-on block slide
  (`OnBlockElementPrepared`) is what will now animate connect/disconnect once instances survive -
  no page-side animation work needed beyond verifying it.

## Acceptance criteria

Done:
- A device that disconnects stays in the Devices list, dimmed, with controls disabled, in its
  existing order/group slot; it reactivates in place on reconnect.
- Connect/disconnect of an already-known device does **not** rebuild the card list: the surviving
  cards keep their elements and the reflow slides (no flash, no "appear").
- Re-seeing a device after a driver reinstall (new endpoint id, same container id) reuses the same
  card, order slot, group membership, and per-device config - no duplicate.
- Rules still apply correctly after connect/disconnect and after a default-device change (the
  in-place `RefreshFrom` keeps rule summaries / default pills accurate). Verify via the
  `Applied rule ...` / `Skip Set ...` / `Skip re-apply ...` log lines.
- "Forget device" removes a persisted disconnected device.

Follow-up (feasible, more interop-heavy - own commit):
- Bluetooth connect/disconnect button via the KS one-shot reconnect/disconnect property (Part 3),
  shown only on BT cards, with the card's connected state driven by status events (not the call's
  return).

## Risks / open questions

- **`RefreshFrom` correctness** is the main risk: a missed binding leaves a card showing stale
  rules / default pills / volume after a device change. Enumerate every constructor-set property.
- **Endpoints with no container id** (some virtual/loopback devices): the friendly-name fallback can
  mis-merge two same-named devices. Log fallbacks; consider including the endpoint-id tail in the
  discriminator.
- **Settings migration** (id -> `DeviceKey`) must be reversible-safe: don't lose a user's order /
  groups / hidden state if the migration can't resolve an id. Keep unresolved rows rather than
  dropping silently, or snapshot the pre-migration settings.
- **New-device addition still rebuilds** (accepted). If even that should animate later, it's the
  separate, harder instance-reuse-on-add problem.
- **Bluetooth button** is KS-filter interop (`IKsControl` / `IOCTL_KS_PROPERTY` with
  `KSPROPSETID_BtAudio`). The one-shot is fire-and-attempt, so the card's connected state must come
  from status events, not the call's return. Reference: ToothTray. Confirm whether a `bluetooth`
  capability / manifest entry is needed for the device enumeration on the self-contained build.
