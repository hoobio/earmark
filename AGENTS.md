# Earmark - per-app audio routing

WinUI 3 / .NET 10 desktop app for Windows that routes individual applications to specific audio endpoints using regex rules. Like Volume Mixer's per-app default device, but driven by patterns rather than per-app dropdowns. Repo: [hoobio/earmark](https://github.com/hoobio/earmark).

## Working directory layout

```
Earmark.slnx                  # solution
global.json                   # pins SDK to 10.0.x
Directory.Build.props         # central MSBuild settings
Directory.Packages.props      # central package versions (CPM)
version.txt                   # source of truth for the app version (release-please owns this)
release-please-config.json    # release-please config
.github/workflows/            # release-please.yaml + pr-title-check.yaml

src/
  Earmark.Core/               # models, rule matcher, persistence (no Windows deps in interfaces)
  Earmark.Audio/              # NAudio + Core Audio + IAudioPolicyConfigFactory + IPolicyConfigVista interop
  Earmark.App/                # WinUI 3 packaged-as-unpackaged app: pages, view-models, hosting, tray, settings
tests/
  Earmark.Core.Tests/         # xUnit project (currently scaffolding only)
```

`Earmark.App` is unpackaged self-contained (`<WindowsPackageType>None</WindowsPackageType>`, `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`). Build outputs land at `src/Earmark.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.App.exe`.

## Build, run, and iterate (THE pattern)

**Rebuild and re-launch after EVERY code change.** Not "after the last change in a batch", not "if it looked like a UI change" - every change to anything that ends up in a binary (`.cs`, `.xaml`, `.csproj`, etc.). A clean `dotnet build` is not verification; the running app is. If you finish a task with only a build, the task is not finished. Report what the relaunched binary actually did (log lines, observed behaviour) before declaring success. Doc-only edits (`.md`, comments-only) are exempt.

The app holds open file handles on its own DLLs while running, which makes incremental builds fail with `MSB3027`. **Always kill before building.** Standard inner loop:

```bash
taskkill //F //IM Earmark.App.exe 2>&1 | head -2
sleep 1
dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64 --no-restore 2>&1 | tail -10
EXE="src/Earmark.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.App.exe"
nohup "$EXE" >/dev/null 2>&1 & disown 2>/dev/null
sleep 4
LOGFILE=$(ls -t /c/Users/AlexHoogeveen-Hill/AppData/Local/Earmark/logs/*.log 2>/dev/null | head -1)
head -15 "$LOGFILE"
```

- `taskkill` is harmless when nothing is running (you'll see "not found"); the `head -2` keeps output bounded.
- Always pass `-p:Platform=x64` (also valid: `ARM64`). The csproj declares `<Platforms>x64;ARM64</Platforms>` and has no `AnyCPU` configuration.
- `--no-restore` keeps the loop fast once dependencies are already pulled.
- Launch via `nohup ... & disown` so the app is parented to the system, not the shell - the shell can return without killing the app.
- Read the latest log file with PowerShell when the file path needs Windows-style resolution: `Get-ChildItem "$env:LocalAppData\Earmark\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.

If you only edited `Earmark.Core` or `Earmark.Audio` you can build those individually for fast iteration without killing the app, but a final `Earmark.App` build still requires the kill.

## Where state lives

- Rules: `%UserProfile%\Documents\Hoobi\Earmark\rules.json`
- Settings: `%UserProfile%\Documents\Hoobi\Earmark\settings.json`
- Logs: `%LocalAppData%\Earmark\logs\earmark-{yyyyMMdd-HHmmss}.log` (one per launch; file logger flushes per call)

Rules and settings live in `Documents` so OneDrive backs them up across machines. Logs stay in `LocalAppData` because they're noisy churn that shouldn't sync. Both stores write atomically with a 5-attempt retry loop to survive the OneDrive sync agent briefly holding the file.

## How routing actually works (what to know before changing the audio code)

The "per-app default endpoint" feature is undocumented Windows. Two distinct interfaces are involved:

1. **Per-app routing** (Application* rule types) uses `IAudioPolicyConfigFactory` (WinRT, IID `ab3d4648-e242-459f-b02f-541c70306324` on Win11 22000+, `2a59116d-...` on older Win10). Activated via `RoGetActivationFactory` against the runtime class name `Windows.Media.Internal.AudioPolicyConfig`. Modern .NET no longer supports `[InterfaceType(InterfaceIsIInspectable)]` marshalling, so the interfaces are declared as `IUnknown`-based with three reserved `IInspectable` methods at the start of the vtable. HSTRING parameters are passed as `IntPtr` and built/freed via `combase!WindowsCreateString` / `WindowsDeleteString`. See `src/Earmark.Audio/Interop/IAudioPolicyConfigFactory.cs` and `HString.cs`.

2. **System default device** (Default* rule types) uses the older `IPolicyConfigVista::SetDefaultEndpoint` (IID `568b9108-...`) on the `CPolicyConfigClient` COM class (CLSID `294935CE-...`). Plain LPWStr device IDs, classic COM. See `IPolicyConfigVista.cs`.

The COM factory is **created once** and cached in `AudioPolicyService` - re-activating per call is slow enough to make the periodic timer freeze the UI thread. Per-app `Set` is preceded by `Get` and skipped if the persisted value already matches the target, which avoids the brief audio glitch from a redundant `SetPersistedDefaultAudioEndpoint`.

`RoutingApplier` keeps a single dedupe set `_appliedSessionKeys` (per `pid|rule|endpoint|flow`). On a rule change, `OnRulesChanged` re-applies with `force: true`, which clears the set. Default-device dedupe is *not* cached: the Get-before-Set check inside `ApplyDefaultRole` short-circuits if the OS already has our target. `OnDefaultsChanged` therefore just calls `ApplyAllInternalAsync(force: false, skipIfBusy: true)` and lets that check do the work. The 10-second timer is a safety net (also `skipIfBusy: true`).

## Rule schema

Four types, single `DevicePattern` field per rule:

| Type | Required fields | Behaviour |
|---|---|---|
| `ApplicationOutput` | `AppPattern`, `DevicePattern` | Pin per-app render endpoint for matching processes |
| `ApplicationInput` | `AppPattern`, `DevicePattern` | Pin per-app capture endpoint |
| `DefaultOutput` | `DevicePattern`, at least one of `SetsDefault` / `SetsCommunications` | Set system default render endpoint. `SetsDefault` covers Console + Multimedia roles; `SetsCommunications` covers the Communications role. Both default to true. |
| `DefaultInput` | `DevicePattern`, at least one of `SetsDefault` / `SetsCommunications` | Same as `DefaultOutput`, capture flow. |

`AppPattern` is tested against **both** the process name and the full executable path; either match counts. Path is resolved via `QueryFullProcessImageName` (`PROCESS_QUERY_LIMITED_INFORMATION`), which works for almost all processes including anti-cheat-protected games. `Process.MainModule.FileName` does **not** work for those - don't reintroduce it.

Rules apply in **list order** - top of the list wins. There is no priority field; reorder the list to change precedence.

## UI architecture

- `Program.Main` (custom, `DISABLE_XAML_GENERATED_MAIN`) handles single-instance via `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Earmark.SingleInstance")`. A second launch redirects activation to the running process, which calls `App.RestoreFromBackground` -> `IWindowChromeManager.RestoreWindow`.
- `App.OnLaunched` builds the generic host, loads settings + rules, starts the routing applier, attaches `WindowChromeManager` to `MainWindow`, then activates (or hides to tray if "Launch to tray" is on).
- DI uses `Microsoft.Extensions.Hosting`. Pages and view-models are registered in `HostBuilderExtensions.ConfigureEarmark`.
- Pages live in `src/Earmark.App/Views/` (namespace `Earmark.App.Views`): `HomePage` (the "Devices" nav item, `Tag="Home"` - per-device cards with volume/meter and a rule-summary chip), `RulesPage` (inline editor - **no dialog**, click a rule to expand and edit, auto-saves 500ms after last keystroke), `SessionsPage`, `SettingsPage`.
- `RuleRow` is the per-rule view-model. It owns its own debounced `SaveAsync`. When the underlying rules list changes, `RulesViewModel.OnRulesChanged` calls `SyncFromRule` on each existing row in place (preserving expanded state) when the order is unchanged; only on add/delete/reorder does it rebuild `Items`.
- Tray: `H.NotifyIcon.WinUI`. `WindowChromeManager` subclasses the window with `SetWindowSubclass` to intercept `WM_SYSCOMMAND/SC_MINIMIZE` for "minimize to tray", and handles `Closed` for "close to tray".

## Components and custom controls

Reach for UI building blocks in this order, and don't skip a tier without reason:

1. **WinUI 3 native control first.** Use the built-in control if it does the job. Note its limits before rejecting it (e.g. `Expander` can't stretch its header content to fill width - that's why `ExpanderPill` exists).
2. **Existing custom component second.** If a control in `src/Earmark.App/Controls/` already covers it, reuse it. Don't reinvent or inline a one-off copy.
3. **New custom component last.** Only when neither of the above fits.

When you do build one, make it a **reusable control**, not page-local markup: put it in `src/Earmark.App/Controls/` (namespace `Earmark.App.Controls`, referenced from XAML via `xmlns:controls="using:Earmark.App.Controls"`) and drive it through dependency properties so every page configures it the same way. When two pages need the same affordance, share one component - don't duplicate the XAML and let the copies drift.

Current shared controls:
- `ExpanderPill` - rounded pill with left content + a full-height chevron (grey box) on the right that toggles `IsExpanded`. Used by the Devices first-rule chip and the Rules row header so both expanders match. Key knobs: `PillBackground` (grey chip on Devices; `Transparent` on Rules so the rule's own card is the surface), `ChevronCornerRadius` (rounds the chevron's right corners to the container radius - 4 for the chip, 8 for the card - so the chevron sits flush to the edge), `PlainChevronWhenExpanded` (Rules: drop the grey box to a plain glyph while expanded so the open editor isn't cluttered; Devices leaves it on since its chip never grows). Toggle is driven by `Tapped` (not a nested `Button.Click`, which swallows the first tap inside a `ListView` item). `ToggleOnBodyTap=false` leaves the body free for its own click (Devices navigates; Rules toggles on the whole pill). Rules dims disabled rows by fading the card *content*, not the card surface - fading the surface lets the `ListViewItem`'s own chrome show through as a nested box.
- `WrapByRowLayout` - virtualising wrap layout that sizes each row to its own tallest card, so one expanded card grows only its row.

## Design language (Fluent 2)

The UI follows current [Fluent 2](https://fluent2.microsoft.design) standards. Benchmark against first-party Windows apps (Settings): it should read as native, not bespoke.

- **Spacing:** 4px grid via the `Spacing*` `x:Double` resources in `App.xaml` (`SpacingXXSmall`=2 ... `SpacingXXLarge`=32). Prefer these over ad-hoc numbers and keep values on the grid (nudge stray 10/14 to 8/12/16).
- **Corner radii:** `{ThemeResource ControlCornerRadius}` (4) for inset controls/chips, `{StaticResource CardCornerRadius}` (8) for cards/tiles/containers. No one-off radii (the old stray `10`s were removed).
- **Theme:** `AppSettings.Theme` (System/Light/Dark) drives `RootGrid.RequestedTheme` in `MainWindow`, the caption-button colours, and the Mica tint. Mica is applied via `MicaController` (not `<Window.SystemBackdrop>`) **so its tint follows the Theme setting, not just the OS**. Theme-dependent brushes MUST be `{ThemeResource}`: a brush resolved in code (`Application.Current.Resources[key]`) snapshots one theme and renders the wrong variant after a switch. Absolute brand colours (Wave Link channel accents, the white mix tile) are deliberately theme-independent.
- **Content width:** content pages (Devices/Rules/Sessions) stretch full-width; Settings uses a ~720 column. A `ContentMaxWidth` token exists if a page ever needs capping, but don't cap the list/grid pages: they're meant to stretch.

## Reactivity preferences

- **Event-driven first, polling as fallback.** When the OS exposes a change notification (e.g. `IMMNotificationClient`, `AudioEndpointVolume.OnVolumeNotification`, `IAudioSessionEvents`), prefer subscribing to that and reconciling on the event. Polling is acceptable as a safety net (drift recovery, restart correctness), but the primary path for "external state changed -> reconcile our state" must not depend on a tick interval. New code that adds reactivity should plumb the event path first and only fall back to polling when the OS has no usable notification.

## Common gotchas

- **Don't use `[InterfaceType(InterfaceIsIInspectable)]`** in COM interop. Modern .NET doesn't marshal it. Use `IUnknown` with reserved vtable slots.
- **Don't use `[MarshalAs(UnmanagedType.HString)]`**. Same reason. Use `IntPtr` + the `HString` helper.
- **Don't reintroduce `Process.MainModule.FileName`**. It blocks on protected processes (games with anti-cheat). The replacement is `ProcessPath.TryGet` in `Earmark.Audio.Interop`.
- **Two-way x:Bind on `ComboBox.SelectedItem` against a value-type property** (e.g. an enum) NREs during item-template recycling because the generated code casts null to the value type. Use `Mode=OneWay` plus a `SelectionChanged` event handler that updates the source.
- **`UnhandledType.UnhandledExceptionEventArgs` is ambiguous** between `Microsoft.UI.Xaml` and `System` namespaces. Always fully-qualify: `Microsoft.UI.Xaml.UnhandledExceptionEventArgs`.
- **`Padding`/`Margin` want a `Thickness`, not a `double`.** The `Spacing*` resources are `x:Double` (sized for `StackPanel.Spacing` and `Grid.ColumnSpacing`/`RowSpacing`, which are doubles). Binding one to a `Thickness` property throws at page load (`Failed to assign to property ... Border.Padding`) - it is NOT caught at build time. For padding/margins use a `Thickness` resource (`PagePadding`, `SectionPadding`) or a literal (`Padding="16"`). Same trap with an `x:Double PagePadding` bound to `Grid.Padding`.
- **Editor disposable analyzer rules**: `CA1001`, `CA1816`, `CA1848`, `CA1873` etc are silenced in `.editorconfig`. Don't add them back unless you also fix the call sites.
- **`Microsoft.Win32.Registry`**: don't add as a separate package. Already provided by the windows TFM (`net10.0-windows10.0.26100.0`); adding the package triggers `NU1510`.

## Conventional commits & release-please hygiene

All commits and PR titles MUST follow the [Conventional Commits](https://www.conventionalcommits.org/) specification, because release-please reads the history off `main` to compute the next version and generate the changelog. This repo is **not** associated with an Azure DevOps project, so commits and PRs DO NOT require an `AB#NNNNN` work-item suffix (in contrast to Nintex repos).

### Required format

```
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

### Commit types

| Type | Purpose | Version bump |
|------|---------|-------------|
| `feat` | New feature | Minor |
| `fix` | Bug fix | Patch |
| `perf` | Performance improvement | Patch |
| `revert` | Revert a previous commit | Depends |
| `docs` | Documentation only | None |
| `style` | Formatting, no logic change | None |
| `refactor` | Code change with no feature/fix | None |
| `test` | Add/update tests | None |
| `chore` | Maintenance, dependency bumps, tooling | None |
| `build` | Build system / external deps | None |
| `ci` | CI/CD pipeline changes | None |

### Breaking changes (MAJOR bump)

Either form works:

- Add `!` after the type: `feat!: rename rule schema fields`
- Or include a `BREAKING CHANGE:` footer in the body

### Scope (optional)

Use a parenthesised scope to narrow the area: `feat(rules): add device-present condition`, `fix(audio): release factory on shutdown`.

### Examples

Good:

```
feat: add device-present and device-missing conditions
fix(routing): clear dedupe cache on rule change
chore: bump CommunityToolkit.WinUI to 8.2.250402
docs: document rule shadowing in CONTRIBUTING.md
ci: enforce conventional-commit PR titles
```

Bad (don't use):

```
🐛 fix bug
Fixed the lock issue
WIP
Update
```

### Notes

- First line under 72 characters
- Imperative mood ("add", not "adds" or "added")
- No trailing period
- **No emoji / no gitmoji.** They break the conventional-commit regex that release-please and `pr-title-check.yaml` use, so commits with a leading emoji do not produce changelog entries or version bumps. This overrides the gitmoji preference in the personal global instructions for this repo specifically.

### Multi-change PRs (PR description format)

This repo uses **squash merges**, so the PR description body becomes the commit message that release-please parses. To produce multiple changelog entries from a single PR, append additional conventional-commit footers at the **bottom** of the PR description body:

```
feat: add per-app input routing

Optional body text explaining the PR.

fix(audio): release COM factory on shutdown
BREAKING-CHANGE: rename ApplicationOutput field "Priority" to "Order"
test: cover the new RuleEvaluator shadow logic
```

- Each footer entry must follow the same `type(scope): description` format
- `BREAKING-CHANGE:` (or `BREAKING CHANGE:`) triggers a MAJOR bump
- Additional entries must appear **after** any free-form body text, not between paragraphs
- Only `feat`, `fix`, `perf`, and `revert` produce changelog entries; `ci`, `test`, `docs`, `chore`, `build`, `style`, `refactor` do not
- Each entry produces its own changelog line

### Repo CI plumbing

- [pr-title-check.yaml](.github/workflows/pr-title-check.yaml) validates the PR title against the conventional-commits regex. This workflow is required for release-please to work cleanly. The regex deliberately does NOT require an `AB#NNNNN` suffix.
- [release-please.yaml](.github/workflows/release-please.yaml) runs on push to `main` and needs two repo settings:
  - Secret `RELEASE_PLEASE_APP_PRIVATE_KEY` (private key for the GitHub App that owns the release)
  - Variable `RELEASE_PLEASE_APP_ID` (the GitHub App's numeric App ID)
- [version.txt](version.txt) is the single source of truth for `<Version>` (read by [Directory.Build.props](Directory.Build.props) via `System.IO.File.ReadAllText`). release-please bumps it.
- Branch model: feature work on `dev`, PR to `main`, release-please drafts a release PR off `main`. Don't push directly to `main`.

## Verifying behaviour after a change

1. Rebuild via the pattern above, launch the app.
2. Read the latest log. The interesting lines:
   - `ApplyAll: N sessions, M rules` (one per cycle)
   - `Applied rule X to <Process> (pid <P>, <Flow>) -> <Endpoint>` (a rule applied)
   - `Skip Set ... already pinned to target` (Get-before-Set short-circuited a redundant write)
   - `Skip re-apply for ... already pinned` (dedupe cache hit)
   - `Applied default-device rule -> Render = <Endpoint>` (a Default* rule fired)
3. **If you don't see Skip Set / Skip re-apply lines after the first cycle**, the dedupe is broken - the periodic timer will glitch audio every 10s.
4. **For UI changes**, the running app must be killed before rebuilding. Don't try to be clever with hot-reload or partial copies.
