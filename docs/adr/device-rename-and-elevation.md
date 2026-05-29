# Decision record: "Rename device" rule + the elevation UI (shelved)

**Status:** Shelved / won't build (2026-05-29). Code left dormant, not removed.

This records a feature we designed and then abandoned: a **"Rename device" rule action**, and the **elevation UI** that would have supported it. It exists so nobody re-proposes the same thing without knowing why it can't work.

## What was requested

A new rule action, **Rename device**: given a device matching a `DevicePattern`, set its Windows FriendlyName (the label shown in Settings > Sound and the Volume Mixer) to a literal new name. Built initially, then we discovered it needs privileges Earmark doesn't have.

## The elevation UI we proposed (and did not build)

Because renaming writes a machine-scope property, the plan was to make elevation a first-class, **generic** capability (not rename-specific, since other future actions might need it too):

1. **A generic "requires elevation" tag on actions.** `RuleAction.RequiresElevation` (and a rule-level roll-up), so any action type can opt in and reuse the same UX. Rename was the only one that needed it.
2. **A "Run elevated" setting.** Toggle that relaunches Earmark elevated, and - because Earmark auto-starts - registers an elevated **scheduled task ("Run with highest privileges")** so it starts elevated on login *without* a UAC prompt each time.
3. **Invalid / caution rule UX.** A rule containing an elevation-requiring action shows a yellow caution state ("requires running elevated") while the app isn't elevated, instead of silently failing. (Not the normal red "invalid" - the rule is valid, it just can't apply yet.)
4. **A prompt on rule creation.** When the user adds an elevation-requiring action, a dialog explains the requirement and offers to enable the "Run elevated" setting / elevate now, with a cancel/disable option.

We got as far as the generic `RequiresElevation` flag and an elevation gate before testing whether the underlying write could work at all. It can't - so the whole UI above became moot.

## Why we couldn't do it

Renaming an audio endpoint's FriendlyName is **not achievable from a third-party app on Windows 11**, at any privilege level. Tested live on Win11 26200:

| Method | Non-elevated | Elevated |
|---|---|---|
| `IMMDevice::OpenPropertyStore(READWRITE)` + `IPropertyStore::SetValue(PKEY_Device_FriendlyName)` | `E_ACCESSDENIED` (0x80070005) | `E_ACCESSDENIED` |
| `IPolicyConfig(Vista)::SetPropertyValue(deviceId, bFxStore=false, key, propvar)` - brokered to the Windows Audio Service | `E_ACCESSDENIED` | `E_ACCESSDENIED` |
| Direct registry write to `HKLM\…\MMDevices\Audio\Render\{guid}\Properties\{a45c254e-…},14` | open-for-write **denied** | works, but see below |

Key facts:

- **Microsoft documents endpoint properties as read-only to clients** ([Device Properties, Core Audio](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)): `IPropertyStore::SetValue` on these returns `E_ACCESSDENIED`. Elevation doesn't change that - it's a software policy, not a registry ACL.
- **The brokered path also refuses it.** `IPolicyConfig::SetPropertyValue` RPCs to the Windows Audio Service (the same `CPolicyConfigClient` path that lets our *default-device* rules work without elevation). Adding the `bFxStore` parameter got the call past `RPC_X_BAD_STUB_DATA` all the way to the service - but the service returns `E_ACCESSDENIED` for a property write, even when the caller is elevated. It authorises `SetDefaultEndpoint`/`SetEndpointVisibility` for clients, not a FriendlyName write. This is why Windows **Settings** can rename (its write is performed by / trusted by the service) while we cannot.
- **The dedicated tools don't offer rename either**, which corroborates the absence of a usable API: [NirSoft SoundVolumeView](https://www.nirsoft.net/utils/sound_volume_view.html) has set-default / volume / mute but no rename; [AudioDeviceManager / AudioDeviceCmdlets](https://github.com/ElizabethGreene/AudioDeviceManager) has set-default / enable / disable / SysFX but no rename.
- **The only programmatic route is a direct HKLM registry write** (the MMDevices `…,14` REG_SZ value, or the device-node `DeviceDesc` under `Enum`, which is TrustedInstaller-owned). Even done elevated it has three disqualifiers:
  1. requires admin (and the `Enum` variant requires taking ownership of the key);
  2. **doesn't reflect live** - the audio service caches names, so it needs restarting `AudioEndpointBuilder` + `AudioSrv` (a brief **system-wide audio dropout**) or a reboot / device re-enable;
  3. **reverts** on driver updates or new device instances.

## Decision

Don't build it. The elevation UI was designed to make rename usable, but:

- **Elevation doesn't even unblock it** - the API returns `E_ACCESSDENIED` whether elevated or not. So the "Run elevated" setting would buy nothing for rename.
- The registry fallback would require Earmark to **restart the system audio service** to take effect, which is unacceptable for an app whose whole job is to manage audio without disruption.
- It **can't ship to the Microsoft Store** regardless: MSIX packages run `asInvoker` (no elevation) and can't write HKLM, and Store certification rejects elevation-requiring apps. Building an elevation feature would foreclose Store distribution for a feature that still wouldn't work.

The same blocked write also makes the **Wave Link "Reconcile device names"** setting non-functional (it renames the Windows endpoint to match the Wave Link label via the same path), so that setting was hidden too.

## What's left in the codebase (dormant)

Kept so a future revival is a small re-enable, not a rebuild:

- `ActionType.RenameDevice` + `RuleAction.NewName` + its `IsValid` case (so existing `rules.json` with a rename action still loads). **Hidden from the action picker** (`ActionRow.TypeOptions`).
- `DeviceFriendlyNameWriter` rewritten to use `IPolicyConfigVista::SetPropertyValue` with the **verified-correct signature** (reaches the service, past `RPC_X_BAD_STUB_DATA`). It still returns `E_ACCESSDENIED`, but it's the closest-to-working artifact if the OS ever opens this up.
- The Wave Link name reconciler (`WaveLinkNameReconciler`) and its setting field stay wired but the setting is hidden and the applier no longer calls it.

The generic `RequiresElevation` tag and the elevation gate were removed (no consumer once rename was shelved); re-add them if reviving.

## What would change the decision

Re-test the `IPolicyConfig::SetPropertyValue` path on future Windows builds (a temporary startup probe renaming a device and checking the HRESULT is the fastest check). If it ever returns `S_OK`:

1. Confirm whether it needs elevation. If non-elevated works, no elevation UI is needed at all - just un-hide the action and re-enable the reconciler.
2. If it works only when elevated, *then* build the elevation UI above (sections 1-4), and feature-flag the rename action off in any MSIX/Store build via the `RequiresElevation` tag.

## Sources

- [Device Properties (Core Audio) - Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties) - endpoint properties read-only to clients.
- [Friendly Names for Audio Endpoint Devices - Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/friendly-names-for-audio-endpoint-devices) - how driver/registry friendly names work.
- [How Windows Sets the Default Audio Device - CodeMachine](https://codemachine.com/articles/how_windows_sets_default_audio_device.html) - `CPolicyConfigClient` → AudioSrv RPC broker.
- [NirSoft SoundVolumeView](https://www.nirsoft.net/utils/sound_volume_view.html), [AudioDeviceManager](https://github.com/ElizabethGreene/AudioDeviceManager) - neither offers endpoint rename.
- [MSIX / WindowsAppSDK #896](https://github.com/microsoft/WindowsAppSDK/issues/896), [Advanced Installer: allowElevation](https://www.advancedinstaller.com/allow-elevation-msix-packages.html) - Store/MSIX can't elevate.
