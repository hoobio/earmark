# Contributing to Earmark

Thanks for your interest in contributing! This guide will help you get started.

## Getting Started

### Prerequisites

- **Windows 10 (19041+)** or **Windows 11** (Win11 22000+ for the modern audio policy interface)
- **[.NET 10 SDK](https://dot.net)** (the repo pins the SDK via `global.json`)
- **[Visual Studio 2022](https://visualstudio.microsoft.com/)** or **[VS Code](https://code.visualstudio.com/)** with the C# Dev Kit

### Building

```powershell
git clone https://github.com/hoobio/earmark.git
cd earmark
dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64
```

The csproj declares `<Platforms>x64;ARM64</Platforms>` and has no `AnyCPU` configuration, so you must always pass `-p:Platform=x64` (or `ARM64`).

### Running Tests

```powershell
dotnet test -p:Platform=x64
```

### The Inner Loop

The app holds open file handles on its own DLLs while running, so incremental builds fail with `MSB3027` if a previous instance is still alive. The standard inner loop is:

```bash
taskkill //F //IM Earmark.App.exe 2>&1 | head -2
sleep 1
dotnet build src/Earmark.App/Earmark.App.csproj -c Debug -p:Platform=x64 --no-restore
EXE="src/Earmark.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Earmark.App.exe"
nohup "$EXE" >/dev/null 2>&1 & disown
```

Logs are at `%LocalAppData%\Earmark\logs\earmark-{yyyyMMdd-HHmmss}.log`, one file per launch. Tail the latest to verify behavior:

```powershell
Get-ChildItem "$env:LocalAppData\Earmark\logs\*.log" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    Get-Content -Tail 50 -Wait
```

If you only changed `Earmark.Core` or `Earmark.Audio`, you can build those projects in isolation without killing the app, but the final `Earmark.App` build still needs the kill.

## How to Contribute

### Reporting Bugs

Open a [Bug Report](https://github.com/hoobio/earmark/issues/new?template=bug_report.yml) issue. Include:

- Steps to reproduce
- Expected vs actual behavior
- Your Windows version and Earmark version
- A snippet of the latest log file (most useful) or screenshots

### Suggesting Features

Open a [Feature Request](https://github.com/hoobio/earmark/issues/new?template=feature_request.yml) issue. Describe the problem you're trying to solve and the solution you'd like.

### Submitting Code

1. **Fork** the repository and create a branch from `dev` (not `main`)
2. **Make your changes**, keep them focused and minimal
3. **Add or update tests** where it makes sense (the test project lives at `tests/Earmark.Core.Tests`)
4. **Update documentation** if your change affects behavior or adds a setting
5. **Open a pull request** against `dev`

The branch model: feature work lands on `dev`, `dev` is PR'd to `main` periodically, and release-please drafts a release PR from `main`. Don't push directly to `main`.

## Conventions

### Commit Messages

All commits must follow [Conventional Commits](https://www.conventionalcommits.org/). The repo uses [release-please](https://github.com/googleapis/release-please) for automated releases, which reads conventional-commit history off `main`.

```
<type>[optional scope]: <description>
```

| Type | Purpose | Version Bump |
|------|---------|-------------|
| `feat` | New feature | Minor |
| `fix` | Bug fix | Patch |
| `perf` | Performance improvement | Patch |
| `docs` | Documentation only | None |
| `test` | Add/update tests | None |
| `refactor` | Code change (no feature/fix) | None |
| `chore` | Maintenance, deps, tooling | None |
| `ci` | CI/CD changes | None |
| `build` | Build system changes | None |
| `style` | Formatting, no logic change | None |
| `revert` | Revert a previous commit | Depends |

- Use imperative mood: "add feature", not "added feature"
- Keep the first line under 72 characters
- Add `!` after the type for breaking changes: `feat!: rename rule schema`
- Gitmoji prefixes (e.g. `✨`) are accepted but optional

### PR Titles

PR titles **must** match Conventional Commits format. CI enforces this via the `pr-title-check.yaml` workflow. The PR title becomes the squash-merge commit message that release-please reads.

### Code Quality

- **Self-documenting code**: avoid unnecessary comments. Names should do the work.
- **No over-engineering**: don't add features, abstractions, or config beyond what's needed.
- **Fix linting before committing**: the editor analyzers are configured in `.editorconfig`. Don't suppress them in new code without a good reason.
- **Two reasons not to over-comment**: well-named identifiers describe what the code does, and PR descriptions describe why it changed. A comment is only worth writing when something would surprise a future reader (a hidden constraint, a workaround, an invariant).

### Architecture Boundaries

- **Earmark.Core** has no Windows-specific dependencies in its public interfaces. Models, the rule matcher, and the rule evaluator live here. New domain logic generally belongs here.
- **Earmark.Audio** wraps Windows audio APIs (NAudio + Core Audio + the COM interop for `IAudioPolicyConfigFactory` and `IPolicyConfigVista`). Touch this layer when adding routing capabilities.
- **Earmark.App** is the WinUI 3 host: pages, view-models, settings, tray, single-instance handling. UI changes go here.

If you find yourself reaching from `Earmark.Core` into a Windows API, that's a sign the abstraction needs another seam.

## Development Tips

### COM Interop Gotchas

The audio policy interfaces are undocumented and modern .NET doesn't marshal `IInspectable`-based WinRT interfaces declaratively. The interop layer in [src/Earmark.Audio/Interop/](src/Earmark.Audio/Interop/) declares interfaces as `IUnknown`-based with three reserved `IInspectable` slots at the start of the vtable. HSTRING parameters are passed as `IntPtr` and built via `combase!WindowsCreateString` / `WindowsDeleteString`.

If you change anything in this folder:

- **Don't use `[InterfaceType(InterfaceIsIInspectable)]`**. Modern .NET doesn't support it. Use `IUnknown` with reserved vtable slots.
- **Don't use `[MarshalAs(UnmanagedType.HString)]`**. Same reason. Use `IntPtr` + the `HString` helper.
- **Don't reintroduce `Process.MainModule.FileName`**. It blocks on protected processes (anti-cheat games). The replacement is `ProcessPath.TryGet` in `Earmark.Audio.Interop`.

### XAML Gotchas

- **Two-way `x:Bind` on `ComboBox.SelectedItem` against a value-type property** (e.g. an enum) NREs during item-template recycling. Use `Mode=OneWay` plus a `SelectionChanged` handler that updates the source.
- **`Microsoft.UI.Xaml.UnhandledExceptionEventArgs` is ambiguous** between `Microsoft.UI.Xaml` and `System` namespaces. Always fully-qualify.
- **`Grid.Padding` wants a `Thickness`**. Don't define an `x:Double` resource and bind it to `Padding`.

### Verifying Behavior

After a change, rebuild and check the log. The interesting lines:

- `ApplyAll: N sessions, M rules` (one per cycle)
- `Applied rule X to <Process> (pid <P>, <Flow>) -> <Endpoint>` (a rule applied)
- `Skip Set ... already pinned to target` (Get-before-Set short-circuited a redundant write)
- `Skip re-apply for ... already pinned` (dedupe cache hit)
- `Applied default-device rule -> Render = <Endpoint>` (a Default* rule fired)

If you don't see `Skip Set` / `Skip re-apply` lines after the first cycle, the dedupe is broken and the periodic timer will glitch audio every 10 seconds.

For UI changes, the running app must be killed before rebuilding. There's no useful hot-reload path for unpackaged WinUI 3 apps in this configuration.

## Questions?

Open a [Discussion](https://github.com/hoobio/earmark/issues) or check the existing issues for context.
