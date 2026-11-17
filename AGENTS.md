# Earmark - per-app audio routing

Avalonia / .NET 10 desktop app for Windows that routes individual applications to specific audio endpoints using regex rules. Like Volume Mixer's per-app default device, but pattern-driven. Repo: [hoobio/earmark](https://github.com/hoobio/earmark).

## Feature docs

Feature detail is in `docs/design/`, not in this file. Before work in an area, read its doc. A new or changed feature fact goes in that doc in the same change; this file holds only the index, build, delivery and rules. Some doc lines run to 1,200 characters: search the docs with Grep (`-o` and a bounded pattern such as `.{0,300}term.{0,300}`), never a whole-file Read.

| Before touching | Read |
|---|---|
| `Earmark.Audio`: `RoutingApplier`, `AudioPolicyService`, per-app and default-device routing, `RoutingRule` schema, conditions, actions, `Pinned`, rule order, event-driven reactivity | `docs/design/routing.md` |
| `Earmark.Desktop` shell, navigation, DI areas, keyed services, threading, dialogs, Rules page, tray, Quick Controls, update pill, `UiWatchdog`, render chain, foreground, EcoQoS | `docs/design/ui-architecture.md` |
| `src/Earmark.Desktop/Styles/`, tokens, `SettingsGroup`, `Slider.Volume`, ComboBox, scroll bars, `Motion`, backdrop, `Controls/` | `docs/design/ui-styles.md` |
| COM interop, CsWin32, compiled bindings, CA rules, fonts, templates, resources, animations, hover fades, dev launch flags | `docs/design/gotchas.md` |
| Devices page loopback and listening app chips | `docs/features/loopback-chip-suppression.md` |
| A decision with lasting consequences | `docs/adr/` |

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
