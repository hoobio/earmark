# Common gotchas

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
