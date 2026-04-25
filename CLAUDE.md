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

Rules and settings are intentionally kept in `Documents` so OneDrive backs them up across machines. Logs stay in `LocalAppData` because they're noisy churn that shouldn't sync. There's a one-shot legacy importer in both `JsonRuleStore` and `SettingsService` that copies `%LocalAppData%\Earmark\{rules,settings}.json` to the new location on first load if the new file doesn't yet exist - safe to delete the importer once everyone's migrated.

## How routing actually works (what to know before changing the audio code)

The "per-app default endpoint" feature is undocumented Windows. Two distinct interfaces are involved:

1. **Per-app routing** (Application* rule types) uses `IAudioPolicyConfigFactory` (WinRT, IID `ab3d4648-e242-459f-b02f-541c70306324` on Win11 22000+, `2a59116d-...` on older Win10). Activated via `RoGetActivationFactory` against the runtime class name `Windows.Media.Internal.AudioPolicyConfig`. Modern .NET no longer supports `[InterfaceType(InterfaceIsIInspectable)]` marshalling, so the interfaces are declared as `IUnknown`-based with three reserved `IInspectable` methods at the start of the vtable. HSTRING parameters are passed as `IntPtr` and built/freed via `combase!WindowsCreateString` / `WindowsDeleteString`. See `src/Earmark.Audio/Interop/IAudioPolicyConfigFactory.cs` and `HString.cs`.

2. **System default device** (Default* rule types) uses the older `IPolicyConfigVista::SetDefaultEndpoint` (IID `568b9108-...`) on the `CPolicyConfigClient` COM class (CLSID `294935CE-...`). Plain LPWStr device IDs, classic COM. See `IPolicyConfigVista.cs`.

The COM factory is **created once** and cached in `AudioPolicyService` - re-activating per call is slow enough to make the periodic timer freeze the UI thread. Per-app `Set` is preceded by `Get` and skipped if the persisted value already matches the target, which avoids the brief audio glitch from a redundant `SetPersistedDefaultAudioEndpoint`.

The `RoutingApplier` keeps two dedupe caches: `_appliedSessionKeys` (per pid|rule|endpoint|flow) and `_appliedDefaults` (per flow). On a rule change, `OnRulesChanged` clears both and re-runs. On a default-device-changed notification, `OnDefaultsChanged` clears only `_appliedDefaults` and re-evaluates - this catches Windows reverting our pin or the user changing it manually. The 10-second timer is a safety net using `skipIfBusy: true`.

## Rule schema

Four types, single `DevicePattern` field per rule:

| Type | Required fields | Behaviour |
|---|---|---|
| `ApplicationOutput` | `AppPattern`, `DevicePattern` | Pin per-app render endpoint for matching processes |
| `ApplicationInput` | `AppPattern`, `DevicePattern` | Pin per-app capture endpoint |
| `DefaultOutput` | `DevicePattern` | Set system default render endpoint (Console + Multimedia + Communications) |
| `DefaultInput` | `DevicePattern` | Set system default capture endpoint (all three roles) |

`AppPattern` is tested against **both** the process name and the full executable path; either match counts. Path is resolved via `QueryFullProcessImageName` (`PROCESS_QUERY_LIMITED_INFORMATION`), which works for almost all processes including anti-cheat-protected games. `Process.MainModule.FileName` does **not** work for those - don't reintroduce it.

Rules apply in **list order** - top of the list wins. `RoutingRule.Priority` is a legacy field kept for JSON compat but not used.

JSON migration: legacy `Application` rules become `ApplicationOutput` (and a paired `ApplicationInput` if the input pattern was set); legacy `DefaultDevice` rules split into one rule per filled slot. The migrator runs at load; the first save rewrites the file in the new schema. See `JsonRuleStore.MigrateRule`.

## UI architecture

- `Program.Main` (custom, `DISABLE_XAML_GENERATED_MAIN`) handles single-instance via `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("Earmark.SingleInstance")`. A second launch redirects activation to the running process, which calls `App.RestoreFromBackground` -> `IWindowChromeManager.RestoreWindow`.
- `App.OnLaunched` builds the generic host, loads settings + rules, starts the routing applier, attaches `WindowChromeManager` to `MainWindow`, then activates (or hides to tray if "Launch to tray" is on).
- DI uses `Microsoft.Extensions.Hosting`. Pages and view-models are registered in `HostBuilderExtensions.ConfigureEarmark`.
- Pages: `RulesPage` (inline editor - **no dialog**, click a rule to expand and edit, auto-saves 500ms after last keystroke), `SessionsPage`, `SettingsPage`. The Devices page was removed - everything routing-relevant is on Rules.
- `RuleRow` is the per-rule view-model. It owns its own debounced `SaveAsync`. When the underlying rules list changes, `RulesViewModel.OnRulesChanged` calls `SyncFromRule` on each existing row in place (preserving expanded state) when the order is unchanged; only on add/delete/reorder does it rebuild `Items`.
- Tray: `H.NotifyIcon.WinUI`. `WindowChromeManager` subclasses the window with `SetWindowSubclass` to intercept `WM_SYSCOMMAND/SC_MINIMIZE` for "minimize to tray", and handles `Closed` for "close to tray".

## Common gotchas

- **Don't use `[InterfaceType(InterfaceIsIInspectable)]`** in COM interop. Modern .NET doesn't marshal it. Use `IUnknown` with reserved vtable slots.
- **Don't use `[MarshalAs(UnmanagedType.HString)]`**. Same reason. Use `IntPtr` + the `HString` helper.
- **Don't reintroduce `Process.MainModule.FileName`**. It blocks on protected processes (games with anti-cheat). The replacement is `ProcessPath.TryGet` in `Earmark.Audio.Interop`.
- **Two-way x:Bind on `ComboBox.SelectedItem` against a value-type property** (e.g. an enum) NREs during item-template recycling because the generated code casts null to the value type. Use `Mode=OneWay` plus a `SelectionChanged` event handler that updates the source.
- **`UnhandledType.UnhandledExceptionEventArgs` is ambiguous** between `Microsoft.UI.Xaml` and `System` namespaces. Always fully-qualify: `Microsoft.UI.Xaml.UnhandledExceptionEventArgs`.
- **`Grid.Padding`** wants a `Thickness`. Don't define an `x:Double` resource named `PagePadding` and bind it to `Padding` - it'll throw at XAML parse time. Use `<Thickness x:Key="PagePadding">28,12,28,20</Thickness>`.
- **Editor disposable analyzer rules**: `CA1001`, `CA1816`, `CA1848`, `CA1873` etc are silenced in `.editorconfig`. Don't add them back unless you also fix the call sites.
- **`Microsoft.Win32.Registry`**: don't add as a separate package. Already provided by the windows TFM (`net10.0-windows10.0.26100.0`); adding the package triggers `NU1510`.

## CI / release flow

- PR titles validated by `.github/workflows/pr-title-check.yaml` (conventional-commits regex). The workflow is required for release-please to work cleanly.
- `release-please.yaml` runs on push to `main`. It needs **two** repo settings:
  - Secret `RELEASE_PLEASE_APP_PRIVATE_KEY` (private key for the GitHub App that owns the release)
  - Variable `RELEASE_PLEASE_APP_ID` (the GitHub App's numeric App ID)
- `version.txt` is the single source of truth for `<Version>` (read by `Directory.Build.props` via `System.IO.File.ReadAllText`). release-please bumps it.
- Branch model: feature work on `dev`, PR to `main`, release-please drafts a release PR. Don't push directly to `main`.

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
