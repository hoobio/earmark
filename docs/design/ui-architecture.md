# UI architecture and Steward lessons

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
