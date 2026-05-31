# Decision record: persistent device identity (device key) + remembered devices

**Status:** Accepted (2026-05-31). Implemented in `feat/devices-persistence-and-block-assembly`.

Records how Earmark persists devices across disconnect and driver reinstall, and why the persisted
stores were re-keyed from the audio endpoint id to a stable **device key**.

## Context

The Devices page persists three things, all keyed by the **audio endpoint id** (the
`{0.0.0.00000000}.{guid}` string): `AppSettings.DeviceOrder`, `DeviceGroup.MemberIds`, and the
`AppSettings.Devices` per-device config map (`Hidden` / `Pinned` / `VolumeControlsHidden` / glyph /
accent). Rules match by friendly-name regex, so rules already survive an id change; order, groups,
and per-device config did **not**.

The endpoint id is not stable across a driver reinstall / OS update. Two problems followed:

1. A reinstall silently dropped a device's order slot, group membership, and customisation.
2. `BuildCards` only enumerated **live** endpoints, so a disconnect removed the card entirely. The
   `ItemsRepeater` then recreated every element on the next change, so the always-on block-slide
   animation had nothing to animate from (surviving cards "appeared" rather than slid) and every
   connect/disconnect flashed.

## Decision

### 1. Stable identity: the device key

Introduce `Earmark.Core.Models.DeviceIdentity`. The **device key** is:

- `container|flow` when the endpoint exposes `DEVPKEY_Device_ContainerId` (the common case: one
  render + one capture endpoint per physical device). The container id is stable across a reinstall,
  and for Bluetooth devices it is derived from the MAC address, so the key is stable across reboots
  and re-pairs too.
- `container|flow|<discriminator>` when several endpoints share one container+flow (multi-jack
  devices). The discriminator is the normalised friendly name, falling back to the endpoint-id tail
  if names also collide, so two co-resident endpoints never collapse to one key.
- `name:<normalised friendly name>|flow` when the endpoint exposes **no** container id (some virtual
  / loopback endpoints). The caller logs this less-reliable fallback.

`flow` is part of the key, so a device exposing both render and capture is two rows.

### 2. Re-key the stores to the device key (Approach A, not an alias layer)

The persistence spec offered two options: **(A)** re-key `DeviceOrder` / `DeviceGroups` / `Devices`
to the device key, or **(B)** keep them endpoint-id-keyed behind a `DeviceKey -> currentEndpointId`
resolver.

**We chose (A).** (B) leaves a permanent indirection on every store lookup and keeps the volatile id
as the on-disk identity - tech debt that the whole feature exists to remove. (A) makes the device key
the single on-disk identity end to end: order, groups, config, and the known-devices table all speak
device key, the block-assembly code keys on it directly, and reinstall-survival falls out for free
(same key -> same slot, no rewrite). The cost - touching the whole block/group/order surface to swap
`endpoint.Id` for `card.DeviceKey` - is paid once and leaves no residue.

A device key always contains `|`, which neither an endpoint id nor a group GUID contains, so the
re-key is unambiguous and group ids / already-migrated keys are never disturbed.

### 3. Migration: one-time, with a backup, completing convergently

- Gated by `AppSettings.SettingsSchemaVersion`. The first run after upgrade (version 0) snapshots
  the pre-migration `settings.json` to `settings.backup.v0.json`, then re-keys every store entry
  whose id resolves against the live enumeration, and bumps the version to
  `DeviceKeySchemaVersion`. The backup means an unresolved-id mishap is recoverable.
- A device absent at migration time can't be resolved yet, so its store entries stay as a raw id.
  `DeviceKeyStore.ReKey` therefore runs on **every** enumeration (cheap, idempotent): when that
  device reappears, its now-known id is rewritten to its key. The migration thus completes
  convergently as devices are seen, and the stores end up holding only device keys - no resolver
  indirection remains on the read path.

### 4. Remembered devices + instance reuse

- `AppSettings.KnownDevices` is the known-devices table (key, last endpoint id, name, flow, container
  id, last-seen). Seeded as devices are enumerated; capped and aged out by the Devices view-model;
  "Forget device" removes a row.
- The card set is the **union** of live endpoints and persisted-absent known devices (the
  disconnected ones, rendered dimmed with controls disabled).
- `RebuildAsync` reconciles `_allCards` **by device key**: a surviving device's existing `DeviceCard`
  instance is reused and refreshed in place (`DeviceCard.RefreshFrom`), so the `ItemsRepeater` keeps
  its element and the block slide animates connect/disconnect instead of rebuilding. `BuildCards`
  runs on a background thread and returns plain snapshots; instance reuse / refresh happens on the UI
  thread.

## Consequences

- Order, groups, and per-device config now survive a driver reinstall and a disconnect.
- Connect/disconnect of a known device no longer rebuilds the card list: the slide animates and the
  flash is gone. A genuinely new device (first sight) still appends a card (accepted; the harder
  instance-reuse-on-add problem is out of scope).
- One-time migration risk is bounded by the pre-migration backup and the convergent re-key.
- `HiddenAppsOnDevice` stays endpoint-id-keyed: it is a per-app, connected-only concern (a
  disconnected device shows no app chips), so it does not need the stable key.

## Bluetooth (Part 3)

The card's Bluetooth connect/disconnect button (KS `KSPROPERTY_ONESHOT_RECONNECT` /
`_DISCONNECT`) maps an endpoint to its BT device via the same container id. Endpoint-level BT
detection from the form factor is a partial signal; the button shows only for endpoints confirmed
Bluetooth via the container match. Tracked as a follow-up to Parts 1+2.
