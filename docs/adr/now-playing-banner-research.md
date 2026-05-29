# "Now Playing" banner: API research and rendering notes

Research only. No implementation decided. Captures what Windows exposes for a now-playing
banner, what it doesn't, and the rendering strategy under consideration.

Target: .NET 10 / Windows App SDK on Windows 10.0.26100, so the full WinRT
`Windows.Media.Control` surface is available.

## What Windows exposes (SMTC)

"Now Playing" comes from the System Media Transport Controls (SMTC) via `Windows.Media.Control`:

```
GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
  -> GetCurrentSession()  // single focused session, can be null
  -> GetSessions()        // IReadOnlyList of ALL sessions
       -> session.TryGetMediaPropertiesAsync()  // metadata + thumbnail
       -> session.GetPlaybackInfo().PlaybackStatus  // Playing/Paused/Stopped
       -> session.SourceAppUserModelId  // AUMID of the source app
```

Events to stay live: `SessionsChanged` (list membership), `CurrentSessionChanged`,
and per-session `MediaPropertiesChanged` / `PlaybackInfoChanged`.

Capability: `globalMediaControl`.

### Metadata fields available (per session)

`Title`, `Artist`, `AlbumTitle`, `AlbumArtist`, `TrackNumber`, `AlbumTrackCount`,
`Subtitle`, `Genres`, `PlaybackType`, plus the single `Thumbnail`.

## The image

There is exactly **one** image per session: `GlobalSystemMediaTransportControlsSessionMediaProperties.Thumbnail`,
an `IRandomAccessStreamReference`. Open it with `OpenReadAsync()` and decode the bytes (JPEG/PNG/BMP).

- No array of sizes, no resolution negotiation, no "banner" variant. One stream.
- Size and aspect are entirely set by the **source app** (it pushes via
  `SystemMediaTransportControlsDisplayUpdater.Thumbnail`; Windows enforces no size).

### Resolution reality (unpredictable, per-app)

- Spotify desktop: ~300x300, sometimes 640x640.
- Browsers (YouTube, SoundCloud, etc. via the JS MediaSession API): whatever the page lists
  in `navigator.mediaSession.metadata.artwork`. Commonly 512x512 or 256x256, sometimes ~96x96.
- Local files / WMP / Groove: embedded album art, can be large or absent.
- Many apps push a small (~100x100) thumbnail or none at all.

### Aspect is NOT guaranteed square

Usually square album art, but video/podcast sources can hand you 16:9 or other ratios.
This is why decoded dimensions are useful as a signal, not just for sizing (see rendering).

## Multiple things playing

`GetSessions()` returns a list, one entry per app that has registered media (Spotify + a browser
tab + a game can all be present at once). Each entry is independent: own thumbnail, own metadata,
own `PlaybackStatus`.

- To find what's actually producing sound: enumerate `GetSessions()`, filter to
  `PlaybackStatus == Playing`.
- One app can register several sessions (Chrome registers one per playing tab).
- `GetCurrentSession()` is just the one the OS thinks the user wants to control (media-key target).

## Identifying the output device

**SMTC cannot tell you the device.** Nothing in `Windows.Media.Control` exposes an audio endpoint,
and there is no public PID on the session either, only `SourceAppUserModelId` (an AUMID).

### Bridging to a device

Audio sessions are enumerated **per render endpoint** via Core Audio (which Earmark already uses):

```
IMMDeviceEnumerator -> per IMMDevice (endpoint)
  -> IAudioSessionManager2.GetSessionEnumerator()
    -> IAudioSessionControl2.GetProcessId()   // PID rendering to THIS endpoint
```

So Core Audio gives "PID X is rendering to device Y." The join to a media session is matching the
SMTC `SourceAppUserModelId` to that PID's process (resolve AUMID -> process, or process-name match).

It's a heuristic, and the messy cases are:

- **Aliasing:** SMTC keys on AUMID, Core Audio on PID. Bridging them is not guaranteed 1:1.
- **Browsers:** AUMID is "the browser," audio runs in child/renderer processes, and per-tab media
  sessions collapse onto the same process audio. You can't reliably say which tab went to which device.
- **Multi-endpoint:** one process can render to multiple endpoints at once.

### Better path for Earmark

Earmark sets the per-app endpoint itself in `src/Earmark.Audio/Services/AudioPolicyService.cs`
(`SetDefaultEndpointForApp`, keyed on the PID embedded in the session identifier). For any app it has
routed, its **own rule state is the authoritative "which device"**, more reliable than re-deriving from
Core Audio. Match the SMTC session's process to existing per-app routing first; use Core Audio
enumeration only as the fallback for un-pinned apps.

## Rendering strategy (preferred: adaptive on dimensions)

You get true source dimensions before rendering: decode with `BitmapDecoder.CreateAsync(...)` and read
`PixelWidth` / `PixelHeight` (and `OrientedPixelWidth`/`OrientedPixelHeight`) from the header. So the
render path can branch on actual size and aspect at runtime.

Preferred approach: use the decoded **dimensions** to decide whether the image can fill the banner
"close to natively," and only fall back to blur/frost when it's too small to show clearly even with an
upscale filter. Dimensions also reveal whether the source is already banner-shaped (wide) vs square
album art vs a tiny thumbnail.

### Decide on upscale factor, not raw resolution

The banner is wide; the source aspect varies. For a cover-fill, the limiting factor is how much you must
scale up after cropping to the banner aspect:

```
upscale = bannerPhysicalWidth / sourceWidth   // for a wide banner cover-filled by a square-ish source
```

A 640px square filling an 800px banner is ~1.25x (crisp); the same 640px into a 1920px-wide banner is
~3x (mush). A natively wide (16:9) source may already fill with little or no upscale.

### Use physical pixels, not logical

A 200pt-tall banner at 200% display scale is 400px of real estate. Multiply target dimensions by the
current `RasterizationScale` (or the Win2D/CanvasDevice DPI) before computing `upscale`, or the decision
mis-fires on high-DPI screens.

### Rough tiers

- `upscale <= ~1.3`: cover-fill sharp, no blur. Use a quality resampler (Fant / high-quality linear),
  not nearest.
- `~1.3 - 2.5`: ambiguous band. Sharp still passable with mild upscale; worth A/B-ing.
- `> ~2.5`: blur/frost (blur hides upscaling, so it's the honest fallback).

Also branch on aspect: a source already close to the banner ratio fills cleanly with minimal crop; a
square crops top/bottom hard; a tiny source goes straight to blur.

### Caveats (resolution is a proxy, not a guarantee)

- A heavily JPEG-compressed 600px thumbnail can look worse than its pixel count implies.
- Some apps pad small art onto a larger letterboxed canvas, so `PixelWidth` overstates real detail.
- Both are rare; ignore for a first cut.

### Alternative: layered hybrid (lower branching)

Blurred source as full-bleed background (blur forgives any upscale, so source size never matters there)
plus the crisp source shown small and sharp on top at or below native size (never upscaled). This is the
Spotify/Apple Music look. It removes the sharp-vs-blur branch: background is always blur, foreground
always crisp, and the only fallback is a generic gradient when there's no thumbnail at all. Downside vs
the adaptive approach: it can't take advantage of a genuinely high-res or already-wide image to fill the
whole banner natively.

## Decision still open

- Adaptive fill-vs-blur on dimensions (preferred) vs layered hybrid vs a mix (e.g. layered, but let a
  high-res/wide source promote to a sharp full-bleed fill).
- Exact upscale thresholds and resampler choice, to be tuned against real thumbnails.
- No-thumbnail fallback (gradient from a dominant colour? generic art?).

## Sources

- Thumbnail property: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmediaproperties.thumbnail
- GetSessions: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager.getsessions
- GetCurrentSession: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager.getcurrentsession
- SourceAppUserModelId: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.sourceappusermodelid
- SessionManager class: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager
