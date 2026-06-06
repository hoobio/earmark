# Earmark - per-app audio routing

WinUI 3 / .NET 10 desktop app for Windows that routes individual applications to specific audio endpoints using regex rules. Like Volume Mixer's per-app default device, but pattern-driven. Repo: [hoobio/earmark](https://github.com/hoobio/earmark).

## Layout

```
Earmark.slnx                  # solution
global.json                   # pins SDK to 10.0.x
Directory.Build.props         # central MSBuild settings + version stamping
Directory.Packages.props      # central package versions (CPM)
version.txt                   # app version source of truth (release-please owns it)
.github/workflows/            # release-please.yaml + pr-title-check.yaml

src/
  Earmark.Core/               # models, rule matcher, persistence (no Windows deps in interfaces)
  Earmark.Audio/              # NAudio + Core Audio + IAudioPolicyConfigFactory + IPolicyConfigVista interop
  Earmark.App/                # WinUI 3 unpackaged app: pages, view-models, hosting, tray, settings
tests/
  Earmark.Core.Tests/         # xUnit (scaffolding only)
```

`Earmark.App` is unpackaged self-contained (`<WindowsPackageType>None</WindowsPackageType>`, `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`). Build output: `src/Earmark.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.App.exe`.

## Build, run, iterate (THE pattern)

**Rebuild and re-launch after EVERY code change** to anything that ends up in a binary (`.cs`, `.xaml`, `.csproj`). A clean build is not verification; the running app is. Report what the relaunched binary did (log lines, behaviour) before declaring success. Doc-only edits (`.md`, comments) are exempt.

The running app holds file handles on its own DLLs, so incremental builds fail with `MSB3027`. **Always kill before building.** The whole loop is one command: kill -> build -> (only if green) relaunch -> tail the new log:

```bash
set -o pipefail
taskkill //F //IM Earmark.App.exe >/dev/null 2>&1; sleep 1
dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64 --no-restore 2>&1 | tail -10 \
  && { EXE="src/Earmark.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.App.exe"; \
       nohup "$EXE" >/dev/null 2>&1 & disown; sleep 4; \
       ls -t /c/Users/AlexHoogeveen-Hill/AppData/Local/Earmark/logs/*.log | head -1 | xargs head -20; }
```

- `set -o pipefail` keeps the build's exit code through `| tail`, so launch fires only on a green build.
- Always pass `-p:Platform=x64` (or `ARM64`). No `AnyCPU` config exists.
- Launch via `nohup ... & disown` so the app outlives the shell.
- Drop the trailing `xargs head -20` if you only need to confirm relaunch. Read latest log via PowerShell: `Get-ChildItem "$env:LocalAppData\Earmark\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
- Editing only `Earmark.Core`/`Earmark.Audio` can build individually without killing, but a final `Earmark.App` build still needs the kill.

## Working alongside other agents

This repo is sometimes edited by multiple agents at once, so a build can go red on changes you never touched. Rebuild first (their tree may already be fixed), and if a failure clearly isn't yours, wait and re-poll rather than patching their WIP. Only commit/push once green. When committing mixed work in one file, stage the whole file.

## Where state lives

- Rules: `%UserProfile%\Documents\Hoobi\Earmark\rules.json`
- Settings: `%UserProfile%\Documents\Hoobi\Earmark\settings.json`
- Logs: `%LocalAppData%\Earmark\logs\earmark-{yyyyMMdd-HHmmss}.log` (one per launch, flushes per call)

Rules/settings live in `Documents` for OneDrive backup; logs in `LocalAppData` to avoid syncing churn. Both stores write atomically with a 5-attempt retry to survive OneDrive holding the file.

## How routing works (read before changing audio code)

The "per-app default endpoint" feature is undocumented Windows. Two interfaces:

1. **Per-app routing** (`ApplicationDevice` actions): `IAudioPolicyConfigFactory` (WinRT, IID `ab3d4648-e242-459f-b02f-541c70306324` on Win11 22000+, `2a59116d-...` on older Win10). Activated via `RoGetActivationFactory` against `Windows.Media.Internal.AudioPolicyConfig`. Declared `IUnknown`-based with three reserved `IInspectable` vtable slots (modern .NET drops `InterfaceIsIInspectable` marshalling). HSTRING params passed as `IntPtr`, built/freed via `combase!WindowsCreateString`/`WindowsDeleteString`. See `Interop/IAudioPolicyConfigFactory.cs`, `HString.cs`.
2. **System default device** (`DefaultDevice` actions): `IPolicyConfigVista::SetDefaultEndpoint` (IID `568b9108-...`) on `CPolicyConfigClient` (CLSID `294935CE-...`). Plain LPWStr device IDs, classic COM. See `IPolicyConfigVista.cs`.

The COM factory is **created once** and cached in `AudioPolicyService` (re-activating per call freezes the UI thread). Per-app `Set` is preceded by `Get` and skipped if the persisted value matches, avoiding an audio glitch.

`RoutingApplier` keeps a dedupe set `_appliedSessionKeys` (per `pid|rule|endpoint|flow`). `OnRulesChanged` re-applies with `force: true` (clears the set). Default-device dedupe is not cached: the Get-before-Set check in `ApplyDefaultRole` short-circuits if the OS already matches, so `OnDefaultsChanged` just calls `ApplyAllInternalAsync(force: false, skipIfBusy: true)`. The 10s timer is a safety net (also `skipIfBusy: true`).

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

Every action carries **`Pinned`** (default true). A **pinned** action is continuously reconciled (external drift is reverted). A **one-shot** (`Pinned=false`) fires only on its rule's *activation edge* (conditions flip, edit, or startup), then is left alone. `RoutingApplier.ComputeActivationEdges` compares current `ConditionsMet` against the previous cycle; an apply pass enacts when `Pinned || edge`. Reconcile passes carry no edges (pinned only). `DeviceRuleResolver` returns `Pinned`, so a one-shot never locks a slider.

`AppPattern` is tested against **both** process name and full exe path; either matches. Path comes from `QueryFullProcessImageName` (`PROCESS_QUERY_LIMITED_INFORMATION`), which works on anti-cheat games. **Don't reintroduce `Process.MainModule.FileName`** (blocks on protected processes).

Rules apply in **list order** - top wins, no priority field. On the Rules page, conditions and actions are drag-reorderable (between branches and onto other rules); a drag-drop commits immediately, unlike field edits which buffer until Save.

## UI architecture

- `Program.Main` (custom, `DISABLE_XAML_GENERATED_MAIN`) handles single-instance via `AppInstance.FindOrRegisterForKey("Earmark.SingleInstance")`. A second launch redirects activation, calling `App.RestoreFromBackground` -> `IWindowChromeManager.RestoreWindow`.
- `App.OnLaunched` builds the generic host, loads settings + rules, starts the applier, attaches `WindowChromeManager`, then activates (or hides to tray).
- DI: `Microsoft.Extensions.Hosting`. Pages/VMs registered in `HostBuilderExtensions.ConfigureEarmark`.
- Pages in `src/Earmark.App/Views/`: `HomePage` (the "Devices" nav item, `Tag="Home"`), `RulesPage` (inline editor, no dialog, auto-saves 500ms after last keystroke), `SessionsPage`, `SettingsPage`.
- `RuleRow` is the per-rule VM with its own debounced `SaveAsync`. On rules-list change, `RulesViewModel.OnRulesChanged` calls `SyncFromRule` in place when order is unchanged; only add/delete/reorder rebuilds `Items`.
- Tray: `H.NotifyIcon.WinUI`. `WindowChromeManager` subclasses the window (`SetWindowSubclass`) to intercept `WM_SYSCOMMAND/SC_MINIMIZE` (minimize to tray) and `Closed` (close to tray).

## Components and custom controls

Reach for UI building blocks in order: (1) WinUI 3 native control, (2) existing control in `src/Earmark.App/Controls/`, (3) new custom component. New ones go in `Controls/` (namespace `Earmark.App.Controls`), driven by dependency properties, shared not duplicated.

- `ExpanderPill` - rounded pill with left content + full-height chevron toggling `IsExpanded`. Used by the Devices first-rule chip and Rules row header. Knobs: `PillBackground`, `ChevronCornerRadius`, `PlainChevronWhenExpanded`, `ToggleOnBodyTap`. Toggle via `Tapped` (not nested `Button.Click`, which swallows the first tap in a `ListView`). Rules dims disabled rows by fading card *content*, not surface.
- `WrapByRowLayout` - virtualising wrap layout sizing each row to its tallest card.

## Design language (Fluent 2)

Follows [Fluent 2](https://fluent2.microsoft.design); benchmark against Windows Settings.

- **Spacing:** 4px grid via `Spacing*` `x:Double` resources in `App.xaml` (`SpacingXXSmall`=2 ... `SpacingXXLarge`=32). No ad-hoc numbers.
- **Corner radii:** `{ThemeResource ControlCornerRadius}` (4) for inset controls/chips, `{StaticResource CardCornerRadius}` (8) for cards. No one-off radii.
- **Theme:** `AppSettings.Theme` drives `RootGrid.RequestedTheme`, caption colours, backdrop tint. Theme-dependent brushes MUST be `{ThemeResource}` (code-resolved brushes snapshot one theme). Absolute brand colours (Wave Link accents, white mix tile) are deliberately theme-independent.
- **Backdrop:** `AppSettings.Backdrop` (Mica default / Acrylic / Solid) picks material. `MainWindow.ApplyBackdrop` (and `QuickControlsWindow`) attach a `MicaController` (`Base`, matching Windows Settings - it bleeds the wallpaper through; `BaseAlt` reads much darker) or `DesktopAcrylicController` so tint follows the Theme setting. Solid shows the opaque `SolidBackdrop` border. Mica fills the whole window uniformly like Settings: `NavigationViewContentBackground` is overridden to `Transparent` in `App.xaml` so the content region doesn't sit on a lighter layer than the pane.
- **Two windows, one look:** the main window and the Quick Controls flyout share `DeviceCardView` + `SectionCardStyle`, but have **separate** backdrop controllers and chrome. Any chrome/visual change (backdrop kind, card surface, dividers, scrim) MUST be applied to **both** `MainWindow` and `QuickControlsWindow` so they stay consistent.
- **Cards & dividers:** card surface is theme-aware via `SectionCardBackgroundBrush` / `SectionCardBorderBrush` (App.xaml theme dictionaries): light gets the standard `CardBackgroundFillColorDefault` + `CardStrokeColorDefault` hairline, dark keeps the subtle borderless `LayerFillColorDefault` (a hard fill + stroke reads too light/boxy over dark Mica). Intra-card dividers use `CardStrokeColorDefaultBrush` (`DividerStrokeColorDefault` is lighter in dark, not more defined).
- **Content width:** Devices/Rules/Sessions stretch full-width; Settings uses a ~720 column. Don't cap the list/grid pages.

## Reactivity

**Event-driven first, polling as fallback.** When the OS exposes a change notification (`IMMNotificationClient`, `AudioEndpointVolume.OnVolumeNotification`, `IAudioSessionEvents`), subscribe and reconcile on the event. Polling is only a safety net (drift recovery, restart correctness).

## Common gotchas

- **Don't use `[InterfaceType(InterfaceIsIInspectable)]`** - modern .NET won't marshal it. Use `IUnknown` with reserved vtable slots.
- **Don't use `[MarshalAs(UnmanagedType.HString)]`** - same reason. Use `IntPtr` + the `HString` helper.
- **Don't reintroduce `Process.MainModule.FileName`** - blocks on anti-cheat games. Use `ProcessPath.TryGet`.
- **Two-way x:Bind on `ComboBox.SelectedItem` against a value-type property** NREs during template recycling. Use `Mode=OneWay` + a `SelectionChanged` handler.
- **`UnhandledExceptionEventArgs` is ambiguous** - always fully-qualify `Microsoft.UI.Xaml.UnhandledExceptionEventArgs`.
- **`Padding`/`Margin` want a `Thickness`, not a `double`.** `Spacing*` are `x:Double`; binding one to a `Thickness` throws at page load (not caught at build). Use a `Thickness` resource (`PagePadding`, `SectionPadding`) or literal.
- **Editor disposable analyzer rules** (`CA1001`, `CA1816`, `CA1848`, `CA1873`) are silenced in `.editorconfig`. Don't re-add without fixing call sites.
- **`Microsoft.Win32.Registry`** - already in the windows TFM; adding the package triggers `NU1510`.
- **App-notification layout workaround** ([WindowsAppSDK#6071](https://github.com/microsoft/WindowsAppSDK/issues/6071)): unpackaged self-contained 2.x omits `Microsoft.WindowsAppRuntime.Insights.Resource.dll`, so `AppNotificationManager.Register` throws `0x8007007E` even though `IsSupported()` is true. The `_EarmarkLayoutInsightsResource` target in `Earmark.App.csproj` extracts that DLL from the runtime MSIX. **Recheck #6071 on every `Microsoft.WindowsAppSDK` bump**: delete the DLL from a self-contained build, relaunch; if `Register()` succeeds, drop the target and the explicit Runtime reference.

## Version, About, and the update check

- App version is release-please-managed `version.txt` -> `<Version>` (`Directory.Build.props`), read at runtime by `Services/AppInfo`. Don't hardcode a version string.
- `Directory.Build.props` stamps `AssemblyMetadata("BuildChannel")` + git commit onto **Earmark.App only**. Channel: `Dev` locally, `Release` under CI, `Prerelease` when CI passes `-p:EarmarkBuildChannel=Prerelease`. `AppInfo.DisplayVersion` produces the `(Dev)`/`(Pre-release)` marker.
- `IUpdateService` reads the releases list (not `releases/latest`) and is **channel-aware** and **gated to unpackaged builds** (`AppInfo.IsPackaged` hides the UX for MSIX/Store). `Dev` skips auto-check and never touches the `CheckForUpdates` setting; the manual "Check now" still works.
- The title-bar pill is a **sibling of `AppTitleBarDragRegion`, not a child** (`SetTitleBar` swallows pointer input). Right margin set from `AppWindow.TitleBar.RightInset`.

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
- `release-please.yaml` runs on push to `main`; needs secret `RELEASE_PLEASE_APP_PRIVATE_KEY` and variable `RELEASE_PLEASE_APP_ID`.
- Branch model: feature work on a branch, PR to `main`, release-please drafts the release PR. Don't push directly to `main`.

## Verifying behaviour after a change

1. Rebuild + launch via the pattern above.
2. Read the latest log. Key lines: `ApplyAll: N sessions, M rules` (per cycle), `Applied rule X to <Process> ...` (rule applied), `Skip Set ... already pinned to target` (Get-before-Set short-circuit), `Skip re-apply for ... already pinned` (dedupe hit), `Applied default-device rule -> ...`.
3. **If no Skip Set / Skip re-apply lines appear after the first cycle**, dedupe is broken - the timer will glitch audio every 10s.
4. **For UI changes**, kill before rebuilding. No hot-reload shortcuts.
