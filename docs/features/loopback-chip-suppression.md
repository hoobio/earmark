# Feature spec: suppress loopback / listening app chips

Implementation prompt for an agent. Not yet built. Read [AGENTS.md](../../AGENTS.md) first. This is a
Devices-page metering/placement change in `Earmark.App` plus a small surface in `Earmark.Audio`.

## Why

App chips are placed **peak-driven**: in [`HomeViewModel.TickAppMeters`](../../src/Earmark.App/ViewModels/HomeViewModel.cs)
(Phase 2) any app whose per-session peak on an endpoint is `>= AppChip.AudibleAmplitudeThreshold`
gets a chip, and the chip's underbar is driven by that per-session peak (`MaxPeakForApp` ->
`IAudioSessionMeterService.GetPeak(pid, endpoint)`).

Some apps open a **listening** session on a render endpoint and *read* its output without producing
audio. The session is `Active` and its per-session meter reports the **channel's** audio (the mix on
that endpoint), so Earmark places a **phantom chip that mirrors whatever is actually playing there**.

The mechanism is a **loopback read of one specific endpoint/channel**, not "sampling the system".
The cause varies and doesn't matter for detection:
- **WebRTC echo cancellation** - the app loopback-reads its *output* device as the AEC reference
  (Discord, browsers, voice apps in a call).
- **Audio routing / sharing** - the app captures a channel to send its audio into a call (e.g. "share
  game audio"), or a monitor mix.

Both look identical: an `Active` session, on one endpoint, whose meter tracks that endpoint's mix
rather than contributing to it. (Confirmed live: a Discord renderer read the Elgato "Game" channel
byte-for-byte while the channel's device-level peak stayed equal to the *single* game stream - i.e.
Discord was reading, not adding - and a real producer on the same channel metered its own, separate
level. Audio on a *different* channel was not seen, because the read is per-channel.)

Observed and verified live (standalone NAudio read, i.e. raw Core Audio, not Earmark code):

```
Game (Elgato Virtual Audio) render sessions:
  Discord     (51936) Active 0.36  meterCh=2 vol=1.00   <- mirrors the game, NOT producing
  FactoryGame (36700) Active 0.36  meterCh=2 vol=1.00   <- the real producer (Satisfactory)
  Audacity    (45480) Active 0.01  meterCh=2 vol=1.00   <- a different real producer, own level
Microphone Mix capture:
  Discord     (51936) Active                             <- same pid is capturing the mic (AEC tell)
```

Audacity reads its own 0.01 while Discord reads the game's 0.36, so per-session metering *does*
isolate on this virtual device. Discord specifically mirrors the channel because its renderer is
loopback-reading the Game output as the echo-cancellation reference. It is not producing audio.

## Why it can't be done deterministically

Windows' audio session API (`IAudioSessionControl` / `IAudioSessionControl2`) exposes **no
render-vs-loopback/listening flag**. Verified by dumping every property Windows offers per session:
process id, state, session identifier, session instance identifier, display name, simple-volume,
meter-channel count, and the system-sounds flag are **byte-identical in shape** between the listening
session and a real playback session. There is no supported property to filter on, so detection must
be a **heuristic**.

(There is also no way to enumerate "loopback clients" distinctly: a WASAPI loopback-capture client
appears in the *render* endpoint's session list exactly like a render client.)

## The heuristic

Flag a session `S` for process `P` on render endpoint `E` as a listening/loopback chip-suppression
candidate when **both** hold:

1. **P holds an active capture (microphone) session** somewhere - i.e. it's in a call. A voice app
   doing AEC or routing a channel into a call always has the mic open; a plain media player doesn't.
2. **S reads the mix rather than contributing to it**: `S`'s per-session peak equals the
   **device-level** peak of `E` (within an epsilon) sustained over a short rolling window, **and**
   there is **another active producer session on `E` whose peak `S` mirrors** (something else is
   making the audio `S` is reading).

Why this is reasonably specific:
- A voice app that plays its **own** audio to `E` reads its own (independent, usually lower) level,
  not the channel mix, so it fails (2). In the capture above, Audacity (a real producer) read 0.01,
  not the 0.36 mix.
- The loopback reads the **full mix**, so its session peak tracks the device-level peak.
- Requiring condition (1) on top of (2) excludes a lone loud producer that merely equals the device
  peak: that producer would have to *also* be mic-capturing *and* be mirroring a second producer.

## Implementation

`Earmark.Audio` - [`AudioSessionMeterService`](../../src/Earmark.Audio/Services/AudioSessionMeterService.cs):
- It already samples per-`(pid, endpoint)` peaks at 20Hz into `_peakSnapshot` and enumerates
  **render** endpoints in `Rebuild`. Keep every COM read on this background sampler; the UI/VM only
  reads published values.
- Add the **device-level peak** per endpoint to the sampler (`MMDevice.AudioMeterInformation.MasterPeakValue`)
  - one cheap read per device.
- Add **capture awareness**: extend `Rebuild` to also enumerate active **capture** endpoints' sessions
  and publish the set of pids that currently hold an active capture session (today `Rebuild`
  enumerates `DataFlow.Render` only). Coalesce through the existing rebuild worker.
- Compute a **monitor flag** per `(pid, endpoint)`: over the last K samples, the session peak equals
  the device peak within epsilon **and** equals at least one other active session's peak on `E`, and
  the pid is in the capture set.
- Expose it, e.g. `bool IsLikelyMonitor(uint pid, string endpoint)` on `IAudioSessionMeterService`, or
  carry a flag alongside the peak in the snapshot.

`Earmark.App` - [`HomeViewModel`](../../src/Earmark.App/ViewModels/HomeViewModel.cs):
- In `TickAppMeters` Phase 2 (placement) and in `MaxPeakForApp`, treat a monitor-flagged
  `(pid, endpoint)` as **not audible**: do not place a chip and do not drive a meter from it. Respect
  the existing identity grouping (`pidsByAppKey`) - a *different* process of the same app that
  genuinely produces audio on `E` must still chip.
- This only gates the peak-driven placement; it must not prune a rule-pinned chip.

Setting - [`PeakMeterOptions`](../../src/Earmark.App/ViewModels/PeakMeterOptions.cs) /
[`AppSettings`](../../src/Earmark.App/Settings/AppSettings.cs): a flag such as
**"Hide echo-cancellation / monitor chips"**, surfaced in Settings > App indicators.

## Decisions

- **Default on or opt-in?** Recommend **opt-in (default off)** until the heuristic is validated across
  more setups - a false positive hides a real chip, which is worse than showing a phantom one. Flip to
  default-on only if testing shows it's clean.
- **Window K + epsilon:** tune. Start ~K=10 samples (0.5s at 20Hz) and epsilon ~0.02. Long enough to
  avoid flicker, short enough to drop the chip quickly when a call ends.
- **Scope:** render endpoints only. Virtual devices (Wave Link, VB-Cable) are where this shows up, but
  the heuristic is device-agnostic - no special-casing by device name.

## Acceptance criteria

- With Discord echo-cancellation active and its output routed through a channel carrying a game, the
  phantom Discord chip on that channel does not appear (or clears within ~1s), while the real
  producer's chip and a separate music app's chip (e.g. Audacity at its own level) are unaffected.
- Ending the Discord call / disabling echo cancellation already clears it today; with this on, it
  never showed.
- Turning the setting off restores current behaviour exactly.
- A real producer that also has a mic open (e.g. a game with in-engine voice chat) is **not**
  suppressed (guarded by the "mirrors another producer" condition).

## Risks / open questions

- The "mirrors another producer" check is short-window correlation; mistune and you get either chip
  flicker or false positives. Prefer a slightly longer window over a tighter epsilon.
- Capture enumeration adds COM work to `Rebuild`; it's already coalesced onto a background worker, so
  keep it there.
- Worst case: two apps genuinely playing **identical** audio while one has a mic open. Accept it, or
  require a longer correlation window before suppressing.
- This is a heuristic, not a guarantee. The deterministic fix lives in the source app's config (echo
  cancellation / output-device choice), which the user controls - so this feature is a convenience,
  not a correctness fix.
