# Earmark - per-app audio routing

Avalonia / .NET 10 desktop app for Windows that routes individual applications to specific audio endpoints using regex rules. Like Volume Mixer's per-app default device, but pattern-driven. Repo: [hoobio/earmark](https://github.com/hoobio/earmark).

## Layout

```
Earmark.slnx                  # solution
global.json                   # pins SDK to 10.0.x
Directory.Build.props         # central MSBuild settings + version stamping
Directory.Packages.props      # central package versions (CPM)
version.txt                   # app version source of truth (release-please owns it)
installer/Earmark.wxs         # WiX MSI
packaging/windows/            # MSIX manifest + visual assets
scripts/                      # Build-DesktopMsix, Publish-EarmarkDesktop, New-MsixBundle, Store helpers
.github/workflows/            # build.yaml (build, package, release, Store) + pr-title-check.yaml

src/
  Earmark.Core/               # net10.0: models, rule matcher, persistence, settings model, update check
  Earmark.Audio/              # Core Audio interop, NAudio, Wave Link client, and the Routing engine (RoutingApplier, EndpointWriter)
  Earmark.Desktop/            # the single Avalonia head: shell, pages, view-models, tray, Quick Controls, watchdog
tests/
  Earmark.Core.Tests/         # xUnit
  Earmark.Desktop.Tests/      # xUnit + Avalonia.Headless: UI guards over sample audio services
```

`Earmark.Desktop` builds `Earmark.exe` (`AssemblyName` `Earmark`, root namespace `Earmark.Desktop`). It is self-contained and unpackaged for the MSI and portable zip; the same publish output is wrapped as MSIX for the Store. There is no second head and no UI abstraction layer.

## Build, run, iterate (THE pattern)

**Rebuild and re-launch after EVERY code change** to anything that ends up in a binary (`.cs`, `.axaml`, `.csproj`). A clean build is not verification; the running app is. Report what the relaunched binary did (log lines, behaviour) before declaring success. Doc-only edits (`.md`) are exempt.

The running app holds file handles on its own DLLs, so incremental builds fail with `MSB3027`. **Always kill before building.** One command: kill, build, launch only on a green build, read the new log:

```bash
set -o pipefail
taskkill //F //IM Earmark.exe >/dev/null 2>&1; sleep 1
dotnet build src/Earmark.Desktop/Earmark.Desktop.csproj -c Debug -p:Platform=x64 2>&1 | tail -10 \
  && { EXE="src/Earmark.Desktop/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.exe"; \
       nohup "$EXE" --no-activate --monitor=rightmost >/dev/null 2>&1 & disown; sleep 4; \
       ls -t /c/Users/AlexHoogeveen-Hill/AppData/Local/Earmark/Dev/logs/*.log | head -1 | xargs head -20; }
```

- `set -o pipefail` keeps the build's exit code through `| tail`, so launch fires only on a green build.
- Always pass `-p:Platform=x64` (or `ARM64`). No `AnyCPU` config exists for the head.
- Launch flags: `--no-activate` starts without taking focus, `--monitor=rightmost` places the window on the rightmost screen, `--open=<Page>` opens `Devices`, `Rules`, `Sessions` or `Settings` (`--open=QuickControls` shows the overlay without focus after 3 s, on the rightmost screen with `--monitor=rightmost`), `--tray` starts hidden in the tray. Agents always pass `--no-activate --monitor=rightmost`.
- Launch via `nohup ... & disown` so the app outlives the shell.
- Logs: `%LocalAppData%\Earmark\<channel>\logs\earmark-{yyyyMMdd-HHmmss}.log`, one per launch. `<channel>` is `Dev` for local builds, `Prerelease` for pre-release builds, and absent for Release (`%LocalAppData%\Earmark\logs`). Read the newest with `Get-ChildItem "$env:LocalAppData\Earmark\Dev\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
- Editing only `Earmark.Core` or `Earmark.Audio` can build individually without killing; a final `Earmark.Desktop` build still needs the kill.

## Working alongside other agents

This repo is sometimes edited by multiple agents at once, so a build can go red on changes you never touched. Rebuild first (their tree may already be fixed), and if a failure clearly is not yours, wait and re-poll rather than patching their work in progress.

Each agent builds with its own output tree so builds never lock each other's files: add `--artifacts-path <dir>` to `dotnet build` and `dotnet test`. The exe is then `<dir>\bin\Earmark.Desktop\debug_win-x64\Earmark.exe`. Only one Earmark runs at a time (single instance), so launches are serialised by the caller.

Only commit and push once green. When committing mixed work in one file, stage the whole file.

## Where state lives

- Rules: `%APPDATA%\Hoobi\Earmark[\Dev|\Prerelease]\rules.json`
- Settings: `%APPDATA%\Hoobi\Earmark[\Dev|\Prerelease]\settings.json`
- Logs: `%LocalAppData%\Earmark[\Dev|\Prerelease]\logs\earmark-{yyyyMMdd-HHmmss}.log` (one per launch, flushes per call)
- Run at login: Release builds only; on Dev and Prerelease the Settings toggle is disabled and reads off. An unpackaged build (MSI, portable) writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Earmark`, rewritten on startup to the current `Environment.ProcessPath` (the 1.1.0 exe was `Earmark.App.exe`). A packaged build (Store) uses the manifest `StartupTask` `EarmarkStartup` (`StartupSettingsApplier`), because an MSIX app's HKCU writes go to its private hive (`%LocalAppData%\Packages\<PFN>\SystemAppData\Helium`) and never reach the real Run key. A packaged build cannot remove a Run value that an MSI or portable build left.

Rules and settings live under roaming `AppData` for backup; logs live in `LocalAppData` to avoid sync churn. Unknown fields in old `settings.json` files are ignored on load.

Both stores write atomically (`AtomicFile`: write `.tmp`, then move over the target, 5 attempts with backoff to survive OneDrive and antivirus holding the file). A `rules.json` or `settings.json` that fails to parse is copied aside (`*.corrupt-<timestamp>.json`), saves are blocked (`IRulesService.IsSaveBlocked`, `SettingsService.IsSaveBlocked`), and the shell offers a dialog to allow saves from empty rules or default settings, or keep them blocked. While either is blocked, default seeding and the Run-value write are skipped; routing still starts.

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

## UI architecture

- **Shell**: `Program.Main` runs `UiWatchdog.WaitForPreviousInstance`, then `SingleInstance.TryAcquire`, then `StartWithClassicDesktopLifetime`. `App.OnFrameworkInitializationCompleted` builds the generic host (`Hosting/ServiceRegistration.AddEarmark`), loads settings and rules off the UI thread, starts the routing applier in the background, then shows `MainWindow` (or hides it to the tray). `ShutdownMode` is `OnExplicitShutdown`.
- **Navigation**: `MainWindow` holds a left nav of four singleton pages from DI: `DevicesPage` (`Devices`), `RulesPage`, `SessionsPage`, `SettingsPage`. `--open=<Page>` and the tray menu navigate by name.
- **DI areas**: each area registers itself in `Hosting/<Area>Registration.cs` (`AddDevicesArea`, `AddRulesArea`, `AddSessionsArea`, `AddSettingsArea`, `AddQuickControlsArea`). Pages take their view-model as a constructor parameter and set `DataContext` before `InitializeComponent`.
- **Keyed cross-area services** (Microsoft.Extensions.DependencyInjection keyed singletons; consumers use `GetKeyedService` and handle null):
  - `"QuickControlsBlocks"` (`Func<IReadOnlyList<Control>>`, Devices): overlay cards for the pinned blocks, consumed by Quick Controls.
  - `"QuickControlsSettings"` (`Control`, Quick Controls): the Quick Controls settings section, hosted by Settings.
  - `"OpenRule"` (`Action<Guid>`, Rules): navigates to Rules and expands the rule.
  - `"OpenSettingsSection"` (`Action<string>`, Settings): navigates to Settings and scrolls to a section key.
- **Threading**: engine events fire off the UI thread. Marshal with `Ui.Post(Action)` (`Services/Ui.cs`).
- **Dialogs and toasts**: `AppDialogs.ShowAsync` / `ShowMessageAsync` (FluentAvalonia content dialogs), `MainWindow.ShowToast`.
- **Rules page**: inline editor, no dialog, auto-saves 500 ms after the last keystroke. `RuleRow` is the per-rule view-model with its own debounced save; on a rules-list change `RulesViewModel` syncs rows in place when order is unchanged and rebuilds `Items` only on add, delete or reorder.
- **Tray**: Avalonia `TrayIcon`, created by `MainWindow.CreateTrayIcon`. The window hides to the tray on close and minimise.
- **Quick Controls**: one warm window (`QuickControlsWindow`), created once and kept cloaked (DWM `DWMWA_CLOAK`) while hidden, so the global hotkey shows it without a first-paint delay. It has its own backdrop and chrome.
- **Update pill**: title-bar button, visible when `UpdateService` reports an update. An unpackaged build's `UpdateService` reads the GitHub releases list (not `releases/latest`) and is channel-aware. A packaged Release build checks the Microsoft Store through `StoreUpdates` (`Services/StoreUpdates.cs`, ported from Steward). It runs `AppInstallManager.SearchForUpdatesAsync` for the product first, because `StoreContext` sees an update only after the Store has scanned, and adds `SearchForAllUpdatesAsync` on "Check now" only, because that call queues every Store app's update. It then calls `StoreContext.GetAppAndOptionalStorePackageUpdatesAsync`, with the window handle passed through `InitializeWithWindow`. When `CanSilentlyDownloadStorePackageUpdates` is true, the update installs silently. Otherwise the pill and the About button call `RequestDownloadAndInstallStorePackageUpdatesAsync`, and a failed or cancelled request opens `ms-windows-store://downloadsandupdates`. `RegisterApplicationRestart` (`--tray` or `--no-activate`) runs before the scan and before an install, so Windows relaunches Earmark after the update. A packaged Prerelease build has no update check. `Dev` skips the auto-check; the manual "Check now" still works.

## Styles and controls

- Style files in `src/Earmark.Desktop/Styles/`: `Resources.axaml` (tokens), `Glyphs.axaml` (Fluent System Icons codepoints and font family), `Controls.axaml` (global control styles), `Dialogs.axaml`, `SettingsCard.axaml`, and one file per area (`Devices`, `Rules`, `Sessions`, `Settings`, `QuickControls`). `App.axaml` includes them. Area styles go in the area file; do not edit the global files for one page.
- **Tokens**: the hoobi-design palette (Ayu Mirage dark) from Steward's styles, a 4 px spacing grid, radius 4 for controls and 8 for cards, fills rather than borders, Hanken Grotesk text, Fluent System Icons glyphs. The light/dark Theme setting no longer exists.
- **`SettingsGroup`**: `StackPanel.SettingsGroup` stacks `SettingsCard` rows (and `ContentPresenter` rows) into one rounded group; first and last children get the group corner radii.
- **`Slider.Volume`**: the volume slider style used by device cards and Quick Controls.
- **ComboBox**: one global style in `Controls.axaml` (pill hover, selected pill); do not restyle per page.
- **Scroll bar hit area** (`Styles/ScrollBarHitArea.axaml`, ported from Steward, included after `Controls.axaml`): a `ScrollViewer`'s scroll bars take pointer input over a 22px band from the edge but draw the same pixels as FluentAvalonia 3.1.0's 14px bar. FluentAvalonia scales the collapsed `Thumb` to 0.35 of its width with a `RenderTransform`, which also shrank its hit area, so a quick press on the thin bar paged instead of dragging. The collapse transform is now on the inner `ThumbVisual`, and the `Thumb` fills the band. `ScrollBarHitAreaTests` checks the pixels at 100%, 125% and 150% and the drag. The band takes clicks from content within 22px of a scrollable edge, so Devices and Rules keep their right gutter inside the `ScrollViewer`, and the scroll bar sits at the window edge.
- **`Motion`** (`Services/Motion.cs`): the shared 150 ms duration and easing. It returns zero duration when the system client-area animation setting is off, so every animation honours it.
- **Backdrop**: the `Backdrop` setting picks Mica (default), Acrylic or Solid. Avalonia has no system Mica here, so Mica and Acrylic are a mock-Mica effect (`Styles/Acrylic.cs`, `Views/DitheredRadialGradient.cs`: a dithered gradient glow over the window); Solid is an opaque fill.
- **Custom controls** in `Controls/`: `DeviceCard`, `DeviceGroupView`, `CardWrapPanel` (wraps cards, each row sized to its tallest card), `PeakMeter`. Reach for a FluentAvalonia or Avalonia control first, then an existing control in `Controls/`, then a new one driven by styled properties.

## Steward lessons applied

Earmark follows the Steward companion's hard-won Avalonia practice:

- **UI watchdog** (`UiWatchdog`): a background thread posts a beat to the UI thread every 5 seconds and probes the render thread through the compositor; after six misses (UI or render thread) it relaunches the exe with `--restart-after=<pid>` (the new process waits for the old one to exit). No restart within 5 minutes of the last one. Skipped when a debugger is attached.
- **Render chain**: `AngleEgl`, then `Software`; composition `WinUIComposition`, then `DirectComposition`, then `RedirectionSurface`. Set in `Program.BuildAvaloniaApp`.
- **Single instance across channels**: a named mutex is taken before Avalonia starts; a second launch signals the first through a named event to show its window. `AbandonedMutexException` counts as acquired (a killed process leaves it abandoned).
- **Tray disposal**: the `TrayIcon` is disposed before shutdown so no ghost icon stays in the notification area. `TrayMenuFocus` focuses Avalonia's tray popup so Escape and the arrow keys reach the menu.
- **Foreground**: `ForegroundGate` and `ForceForeground` decide when the window may take focus; `--no-activate` and tray starts hold the gate.
- **Background cost**: while hidden the process opts into EcoQoS (execution-speed throttle only, never idle priority) and trims its working set (`Native.cs`).
- **Logging**: `AvaloniaLogSink` forwards Avalonia's own log (including binding errors, logged as `Avalonia Binding`) to the file log. Three unhandled-exception hooks (UI dispatcher, AppDomain, unobserved tasks) log Critical.
- **First show**: the window is cloaked until its first frame, then uncloaked, so there is no white flash. `WM_ENDSESSION` quits the app.

## Reactivity

**Event-driven first, polling as fallback.** When the OS exposes a change notification (`IMMNotificationClient`, `AudioEndpointVolume.OnVolumeNotification`, `IAudioSessionEvents`), subscribe and reconcile on the event. Polling is only a safety net (drift recovery, restart correctness).

## Common gotchas

- **Do not use `[InterfaceType(InterfaceIsIInspectable)]`** - modern .NET will not marshal it. Use `IUnknown` with reserved vtable slots.
- **Do not use `[MarshalAs(UnmanagedType.HString)]`** - same reason. Use `IntPtr` + the `HString` helper.
- **Do not reintroduce `Process.MainModule.FileName`** - blocks on anti-cheat games. Use `ProcessPath.TryGet`.
- **Compiled bindings are on** (`AvaloniaUseCompiledBindingsByDefault`). Every view and template with a binding needs `x:DataType`, or the build fails.
- **CA rules are errors in CI.** `Directory.Build.props` sets `TreatWarningsAsErrors` and `AnalysisLevel` `latest-recommended`. Build with 0 warnings; silenced rules are listed in `.editorconfig` (`CA1848`, `CA1873`, `CA1816`, ...). Do not add suppressions to get a build through.
- **Variable fonts do not render per weight.** Ship static per-weight TTFs (`HankenGrotesk-Regular/SemiBold/Bold`) in `Assets/Fonts`, with their licences beside them.
- **Cross-thread UI**: engine callbacks are off the UI thread; touching a control or an `ObservableCollection` bound to one from them throws or corrupts. Use `Ui.Post`.
- **`Microsoft.Win32.Registry`** is in the Windows TFM; adding the package triggers `NU1510`.
- **CsWin32**: `Earmark.Audio` generates interop from `NativeMethods.txt`; add a name there instead of hand-writing a `DllImport`.
- **ContextMenu**: FluentAvaloniaUI 3.1.0 ships no `ContextMenu` theme; `Styles/Controls.axaml` supplies one.
- **Template precedence**: in Avalonia 12 a value set in a control template outranks a style setter without a pseudo-class. Restyle templated parts through the theme's resource keys or a selector with a pseudo-class.
- **Resource name clashes**: FluentAvalonia defines resources with the same names as Earmark's tokens (for example `ContentDialogPadding`). Read Earmark's own tokens with `StaticResource`.
- **DataContext**: do not set an inner `DataContext` from a `StyledProperty` changed callback. The callback can fire during `DataContext` propagation and the inner compiled bindings keep a null source (the blank `DeviceGroupView`). Rely on inheritance.
- **Animations**: animate the control's `RenderTransform` property, not the `TranslateTransform` object. Avalonia 12 throws `InvalidCastException` on the latter.
- **Hover fades and white flashes**: `BrushTransition` interpolates straight, not premultiplied, alpha. A fade between a clear brush and an opaque brush of a different RGB passes through a lighter colour, which shows as a white flash on hover. A clear rest brush carries the RGB of the brush it fades to (`#00242936` against `#FF242936`, `#00FFFFFF` against `#0FFFFFFF`). An opaque rest against a translucent hover uses an opaque hover (`Button.RuleChip` uses `DefaultButtonStyle`, not `SubtleButtonStyle`). A user-coloured background (device tile, colour swatch) has no fade. `HoverTransitionTests.No_hover_or_press_fade_passes_through_a_lighter_colour` (`tests/Earmark.Desktop.Tests`) walks the shell, every page, the Devices menus, Quick Controls and a control gallery, applies `:pointerover` and `:pressed`, and fails on any such pair. Add new control types and button themes to its gallery.
- **Dev launch flags**: `Earmark.exe --open=<Page> --no-activate --monitor=rightmost` opens the named page on the rightmost monitor without taking focus.
- **Parallel agents**: each agent builds with its own `--artifacts-path` so builds do not lock each other's files.
- **Render checks**: `PrintWindow` capture of the app window is acceptable for render checks.

## Version, About, and the update check

- App version is release-please-managed `version.txt` -> `<Version>` (`Directory.Build.props`), read at runtime through `Earmark.Core.AppInfo`. Do not hardcode a version string.
- `Directory.Build.props` stamps `AssemblyMetadata("BuildChannel")` and the git commit onto **Earmark.Desktop only**. Channel: `Dev` locally, `Release` under CI, `Prerelease` when CI passes `-p:EarmarkBuildChannel=Prerelease`. `AppInfo.DisplayVersion` produces the `(Dev)`/`(Pre-release)` marker, and `AppInfo.ChannelFolder` names the data and log subfolder.
- The update check in shipped 1.1.0 clients reads the GitHub releases list and picks the release asset; release asset names must stay compatible.

## Packaging and CI

All packages are built by `.github/workflows/build.yaml`, per architecture (x64 and ARM64):

- **MSIX** (`scripts/Build-DesktopMsix.ps1`): publishes `Earmark.Desktop` self-contained, stamps `packaging/windows/Package.appxmanifest` (identity `Hoobi.Earmark`, `Hoobi.Earmark.Prerelease` for pre-releases; Publisher is the reserved Store CN and must not change), builds `resources.pri` with `makepri`, packs with `makeappx` (found in the pinned `Microsoft.Windows.SDK.BuildTools` package or the installed Windows SDK). The package version is `major.minor.<run number>.0`. `scripts/New-MsixBundle.ps1` combines the x64 and ARM64 packages into the `.msixupload` for the Store.
- **Portable zip**: `scripts/Publish-EarmarkDesktop.ps1 -SingleFile` produces one `Earmark.exe`; CI fails if any loose DLL stays beside it. The zip carries the exe plus the licences (Earmark, Hanken Grotesk OFL, Fluent System Icons, Cascadia).
- **MSI**: WiX 5 (`installer/Earmark.wxs`) wraps the loose publish output. Per-user install. UpgradeCodes are fixed per channel and architecture and must not change. The install sequence runs `taskkill` for both `Earmark.App.exe` (the 1.1.0 exe) and `Earmark.exe` so an upgrade replaces files held by a tray-parked app.
- **Signing**: MSI and exe are signed when `SIGNING_CERTIFICATE` and `SIGNING_CERT_THUMBPRINT` are set; otherwise the build warns and ships unsigned.
- **WACK** runs on each MSIX; `.github/wack-exclusions.json` lists the accepted false positives.
- **Store submit**: `store-submit` (release) and `store-flight` (pre-release flight) run only when the `AZURE_CLIENT_ID` variable is set (with `AZURE_TENANT_ID`); credentials come from Key Vault.
- **Release**: release-please opens the release PR, the `release` job attaches MSIX, msixupload, MSI, zips, SBOM and WACK reports, and publishes. A release-please PR also builds a pre-release.
- **Tests**: `lint-and-test` builds the solution with `TreatWarningsAsErrors` and runs every test project.

## Conventional commits & release-please

All commits and PR titles MUST follow [Conventional Commits](https://www.conventionalcommits.org/); release-please reads `main` history to compute versions and the changelog. This repo is **not** an ADO project, so **no `AB#NNNNN` suffix** (unlike Nintex repos).

Format: `<type>[optional scope]: <description>`, first line under 72 chars, imperative mood, no trailing period.

| Type | Bump |    | Type | Bump |
|---|---|---|---|---|
| `feat` | Minor | | `docs` `style` `refactor` `test` `chore` `build` `ci` | None |
| `fix` `perf` | Patch | | `revert` | Depends |

Breaking change (MAJOR): `!` after type (`feat!: ...`) or a `BREAKING CHANGE:` footer.

**No emoji / no gitmoji** - they break the conventional-commit regex (`pr-title-check.yaml`), so such commits produce no changelog entry or bump. This overrides the gitmoji preference in personal global instructions for this repo.

### Multi-change PRs (squash merge)

The PR description body becomes the squash commit message release-please parses. To produce multiple changelog entries, append extra bare conventional-commit footers at the **bottom** of the description, **each separated by a blank line**:

```
feat: add per-app input routing

Optional body text.

---

fix(audio): release COM factory on shutdown

test: cover new RuleEvaluator shadow logic

BREAKING-CHANGE: rename ApplicationOutput field "Priority" to "Order"
```

- **Blank line between every footer is required** - the parser groups consecutive non-blank lines into one paragraph and keeps only the first conventional-commit line (this bit PR #32). Soft-wrapping within a footer is fine.
- Footers go **after** any free-form body text.
- Every type produces a changelog line; only `feat`/`fix`/`perf`/`revert`/breaking trigger a bump.

### CI plumbing

- `pr-title-check.yaml` validates the PR title (no `AB#` required); required for release-please.
- `release-please.yaml` logic lives in the `release-please` job of `build.yaml`; it needs secret `RELEASE_PLEASE_APP_PRIVATE_KEY` and variable `RELEASE_PLEASE_APP_ID`.
- Branch model: feature work on a branch, PR to `main`, release-please drafts the release PR. Do not push directly to `main`.

## Verifying behaviour after a change

1. Rebuild + launch via the pattern above.
2. Read the latest log. Key lines: `ApplyAll: N sessions, M rules` (per cycle), `Applied rule X to <Process> ...` (rule applied), `Skip Set ... already pinned to target` (Get-before-Set short-circuit), `Skip re-apply for ... already pinned` (dedupe hit), `Applied default-device rule -> ...`, `Avalonia Binding` (a binding error).
3. **If no Skip Set / Skip re-apply lines appear after the first cycle**, dedupe is broken - the periodic pass will glitch audio.
4. **For UI changes**, kill before rebuilding and ask Hoobi to verify by hand; screenshots and UI Automation do not work on the multi-monitor, fullscreen-game setup. Never synthesise drags against real rules.
