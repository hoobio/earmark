# Decision record: singleton DI pages swapped via `Content=` instead of `Frame.Navigate`

**Status:** Accepted (2026-06-06). Implemented in `perf/startup-improvements` (`NavigationService`).

Records why the shell navigates by assigning `Frame.Content` to long-lived, DI-resolved page singletons instead of calling `Frame.Navigate(typeof(Page))`, and what we gave up to do it.

## Context

The shell is a `NavigationView` hosting a single `ContentFrame` (`MainWindow.xaml`). Four top-level pages (`HomePage`, `RulesPage`, `SessionsPage`, `SettingsPage`) are registered as **singletons** in DI (`HostBuilderExtensions.cs:90-93`) and constructor-inject their view models. `NavigationService.Navigate` resolves the page from the container and sets `_frame.Content = page` (`INavigationService.cs`).

This is deliberately *not* the idiomatic WinUI pattern. The textbook approach is `Frame.Navigate(typeof(HomePage))`: the Frame owns a navigation stack, instantiates each page via its parameterless constructor on every visit, and animates the swap with the `NavigationThemeTransition` declared in `Frame.ContentTransitions`.

Two things make that default a poor fit here:

1. **Constructor injection.** `Frame.Navigate` instantiates pages itself with `Activator`, so it can only call a parameterless constructor. Our pages take their view models (and the VMs take audio services) through DI. Routing every page through `Frame.Navigate` would mean either a service-locator anti-pattern in each page's parameterless ctor or a custom navigation hook that resolves from the container anyway, at which point we are most of the way to our own navigator.

2. **Pages are expensive to build, and the cost is COM-shaped.** `HomePage` binds an `ItemsRepeater` of device cards; each card hosts `ChannelPeakMeter`s, a `CrossfadeImage`, and now-playing strips driven by live audio data. Building a page means re-enumerating endpoints and sessions and re-establishing meter polling against the Core Audio COM services (`AudioEndpointService`, `AudioSessionService`, `AudioSessionMeterService`, all marshalled WASAPI/`IMMDeviceEnumerator` interop). The marshalling, the per-card visual tree, and the first layout pass dominate page-open cost. With `Frame.Navigate` that whole cost is paid **on every tab switch**, because the Frame discards the old page and constructs a fresh one. It also throws away transient UI state (scroll offset, expanded cards, in-flight meter animations) each time.

Singletons turn the first build into a one-time cost. After a page is built once, re-navigating to it re-attaches the **same** fully-realised element tree, with its COM subscriptions and visual state intact, in roughly the cost of a property set.

## Decision

Keep pages as DI singletons and navigate by `Content=` assignment. Accept that this means owning the parts of `Frame` we bypass.

### What `NavigationService` reimplements

`Frame.Navigate` gives you a navigation stack, `CanGoBack`/`CanGoForward`, and the page-transition animation for free. Bypassing it means we build those ourselves:

- **Back/forward history.** Two `Stack<Type>` collections (`_backStack` / `_forwardStack`) with standard browser semantics: a forward `Navigate` pushes the current page and clears the forward stack; `GoBack`/`GoForward` move entries between the two. We store `Type`, not page instances, because the instance is always recoverable from the container. A `HistoryChanged` event lets the title-bar back button track `CanGoBack`.
- **Page transitions.** `NavigationThemeTransition` only fires on a real `Frame.Navigate`, so it never ran for us (the `Frame.ContentTransitions` block we initially copied in was dead, and the `NavigationTransitionInfo` we passed into `Navigate` was silently ignored). Element `Transitions` (e.g. `EntranceThemeTransition`) don't fill the gap either: they only play on an element's **first** realisation into the tree, so a re-shown cached singleton wouldn't animate (only never-seen pages would). `SwapTo` instead drives an explicit fade + slide-up `Storyboard` (`PlayEntrance`) on every swap, which animates whether the page is freshly built or a cached re-show.
- **De-duping.** `Navigate` no-ops when the target page is already current, so re-clicking the active `NavigationViewItem` does nothing.

## Tradeoffs

| Concern | `Frame.Navigate` (default) | Singleton + `Content=` (chosen) |
|---|---|---|
| Page construction cost | Paid on **every** visit | Paid **once**, then reused |
| COM interop churn | Re-enumerate endpoints/sessions + re-subscribe meters per visit | Subscriptions stay live for the app's lifetime |
| UI state across visits | Lost (scroll, expansion, animations reset) | Preserved |
| DI / constructor injection | Awkward (parameterless ctor only) | Natural (container resolves the page) |
| Back/forward stack | Built in | We maintain it (`_backStack`/`_forwardStack`) |
| Transitions | `NavigationThemeTransition` for free | We run a `Storyboard` per swap (`PlayEntrance`) |
| Memory | One page alive at a time | All four pages + their trees resident for the session |
| Per-page lifecycle hooks | `OnNavigatedTo`/`OnNavigatedFrom` fire | Don't fire; page must react to its VM, not nav events |

The deliberate cost we accept: **all four pages stay resident** for the session, and pages don't get `OnNavigatedTo`/`OnNavigatedFrom` callbacks. The resident-memory cost is bounded (four pages) and cheap next to the COM/layout savings. The missing lifecycle hooks are a non-issue because our pages are driven by their view models and the live audio services, not by navigation events: there is no per-visit "load" step to hang off `OnNavigatedTo`.

## Consequences

- New top-level pages must be registered as singletons in `HostBuilderExtensions` and added to the `tag -> Type` maps in `MainWindow.xaml.cs`.
- Anything that previously would have lived in `OnNavigatedTo` (refresh-on-show) must instead be reactive: subscribe to the view model / audio service so the page updates whether or not it is the visible page.
- A page that must *not* hold resources while hidden (none today) would be the trigger to revisit this: at that point we would add an explicit show/hide signal rather than reverting to `Frame.Navigate`.
- `INavigationService.Navigate` no longer accepts a `NavigationTransitionInfo` (it was dead); transition tuning happens in `NavigationService.PlayEntrance`.
