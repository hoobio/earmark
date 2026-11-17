# Routing engine and rule schema

## How routing works (read before changing audio code)

The "per-app default endpoint" feature is undocumented Windows. Two interfaces:

1. **Per-app routing** (`ApplicationDevice` actions): `IAudioPolicyConfigFactory` (WinRT, IID `ab3d4648-e242-459f-b02f-541c70306324` on Win11 22000+, `2a59116d-...` on older Win10). Activated via `RoGetActivationFactory` against `Windows.Media.Internal.AudioPolicyConfig`. Declared `IUnknown`-based with three reserved `IInspectable` vtable slots (modern .NET drops `InterfaceIsIInspectable` marshalling). HSTRING params passed as `IntPtr`, built/freed via `combase!WindowsCreateString`/`WindowsDeleteString`. See `Interop/IAudioPolicyConfigFactory.cs`, `HString.cs`.
2. **System default device** (`DefaultDevice` actions): `IPolicyConfigVista::SetDefaultEndpoint` (IID `568b9108-...`) on `CPolicyConfigClient` (CLSID `294935CE-...`). Plain LPWStr device IDs, classic COM. See `IPolicyConfigVista.cs`.

The COM factory is **created once** and cached in `AudioPolicyService` (re-activating per call freezes the UI thread). Per-app `Set` is preceded by `Get` and skipped if the persisted value matches, avoiding an audio glitch.

`RoutingApplier` (`Earmark.Audio/Routing`) is the engine; `EndpointWriter` performs the device, volume and mute writes.

- **One pass**: each pass takes a snapshot (rules, render and capture endpoints, sessions) and evaluates every rule's conditions once into a `Met` map (`RuleMatcher.EvaluateConditions`). Matcher, resolver and the Wave Link work all read that map; none re-evaluate conditions.
- **Serialised with rerun-pending**: passes run under one gate. A request that arrives while a pass runs sets `_rerunPending` and the running pass loops again; `skipIfBusy` callers (timer, default-device events) drop out instead of queuing.
- **Triggers**: rules changed (`force: true`, clears the dedupe set), session added, endpoint topology change (750 ms debounce), volume or mute drift (500 ms debounce), default-device change, and a safety-net timer every 5 minutes (`PeriodicInterval`).
- **Dedupe**: `_appliedSessionKeys` (per `pid|rule|endpoint|flow`) stops repeat per-app writes. Default-device dedupe is not cached: the Get-before-Set check in `ApplyDefaultRole` short-circuits when the OS already matches.
- **Activation edges**: `ComputeActivationEdges` compares each rule's `Met` against the previous pass (`_lastConditionsMet`); one-shot actions enact on an edge, a new session, or a forced pass.
- **Sessions**: `IAudioSessionService.GetSessions()` is a cached render snapshot. Capture-device sessions are a separate snapshot (`GetCaptureSessions()`) used for display and are not walked by the per-process routing loop.

`AppPattern` is tested against **both** process name and full exe path; either matches. Path comes from `QueryFullProcessImageName` (`PROCESS_QUERY_LIMITED_INFORMATION`), which works on anti-cheat games. **Do not reintroduce `Process.MainModule.FileName`** (blocks on protected processes).

## Rule schema

A `RoutingRule` has a name, enabled bit, list of AND-ed **conditions**, and two action lists: **`Actions`** (main branch) and **`ElseActions`** (otherwise). `ConditionsMet` selects the live branch via `ActiveActions(met)` (shared by matcher, resolver, applier, evaluator). No conditions = always met, so only the main branch fires.

**Conditions** - one `Kind` plus a `Negate` polarity flag:

| `ConditionKind` | `Negate=false` / `true` | Fields |
|---|---|---|
| `Device` | present / missing | `DevicePattern`, `Flow` (Any/Render/Capture) |
| `DefaultDevice` | is / is not system default | `DevicePattern`, `Flow` |
| `Application` | running / not running | `AppPattern` |

**Actions** - one `Kind` plus mode fields:

| `ActionKind` | Mode field | Required | Behaviour |
|---|---|---|---|
| `ApplicationDevice` | `Flow` (Output=Render/Input=Capture) | `AppPattern`, `DevicePattern` | Pin matching processes' per-app endpoint |
| `DefaultDevice` | `Flow`; `SetsDefault` (Console+Multimedia)/`SetsCommunications` | `DevicePattern`, ≥1 role | Set system default endpoint |
| `WaveLinkMix` | `Membership` (Include/Exclude/Exclusive) | `MixPattern`, `DevicePattern` | Add/remove device to a Wave Link mix; Exclusive strips non-matching outputs |
| `DeviceVolume` | - | `DevicePattern`, `Volume` 0-1 | Pin a device's volume |
| `DeviceMute` | `Muted` | `DevicePattern` | Set mute state |
| `RenameDevice` | - | `DevicePattern`, `NewName` | Parked: needs elevated registry write, hidden from picker |

Every action carries **`Pinned`** (default true). A **pinned** action is continuously reconciled (external drift is reverted). A **one-shot** (`Pinned=false`) fires only on its rule's *activation edge* (conditions flip, edit, or startup), then is left alone. An apply pass enacts when `Pinned || edge`. Reconcile passes carry no edges (pinned only). `DeviceRuleResolver` returns `Pinned`, so a one-shot never locks a slider.

Rules apply in **list order** - top wins, no priority field. On the Rules page, conditions and actions are drag-reorderable (between branches and onto other rules); a drag-drop commits immediately, unlike field edits which buffer until Save.

## Reactivity

**Event-driven first, polling as fallback.** When the OS exposes a change notification (`IMMNotificationClient`, `AudioEndpointVolume.OnVolumeNotification`, `IAudioSessionEvents`), subscribe and reconcile on the event. Polling is only a safety net (drift recovery, restart correctness).
