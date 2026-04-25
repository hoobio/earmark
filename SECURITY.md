# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| Latest release | Yes |
| Previous minor | Best-effort |
| Older | No |

Only the latest release receives security fixes. Users are encouraged to stay up to date via [GitHub Releases](https://github.com/hoobio/earmark/releases/latest).

## Reporting a Vulnerability

**Please do not open a public issue for security vulnerabilities.**

Instead, report vulnerabilities privately using [GitHub Security Advisories](https://github.com/hoobio/earmark/security/advisories/new).

Include:

- A description of the vulnerability
- Steps to reproduce or proof of concept
- The potential impact
- Any suggested fix (optional)

You should receive an initial response within 72 hours. The advisory will remain private until a fix is released.

## Security Model

Earmark talks to undocumented Windows audio APIs from a regular user-mode desktop app. Here's how it's scoped:

### Privilege

- Earmark runs entirely as the current Windows user, with no elevation, no service install, no driver, and no scheduled task.
- The "Launch on startup" setting writes a `Run` key under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, which is per-user and does not require elevation.

### Audio APIs

- Per-app routing uses `IAudioPolicyConfigFactory` (WinRT, undocumented) on `Windows.Media.Internal.AudioPolicyConfig`. The same mechanism Windows itself uses for the per-app device picker in Volume Mixer. Calls are scoped to processes the user already owns.
- System-default device changes use `IPolicyConfigVista::SetDefaultEndpoint` on `CPolicyConfigClient`. Equivalent to what the Settings app does when you pick a default device.
- Earmark does not call any kernel APIs and does not patch any system component.

### Process Inspection

- Process paths are resolved via `QueryFullProcessImageName` with `PROCESS_QUERY_LIMITED_INFORMATION`, the same minimal-rights API used by Task Manager. This works for protected processes (e.g. anti-cheat-protected games) without any privileged access.
- Earmark does not inject into other processes, hook their APIs, or modify their memory.

### Input Handling

- Rule patterns are compiled as `Regex` with a 250 ms timeout per match, so a malicious or accidental catastrophic-backtracking pattern cannot freeze the routing thread.
- Pattern compile failures are caught and the offending rule is treated as inert (no match) rather than throwing into the apply loop.

### Storage

- Rules and settings are written as plain JSON under `%UserProfile%\Documents\Hoobi\Earmark\`. They contain pattern strings only, no secrets.
- Logs are written to `%LocalAppData%\Earmark\logs\`. They include process names, executable paths, and audio endpoint IDs, but no user content.
- No data leaves the machine. There is no telemetry endpoint.

### Network

- Earmark makes no outbound network requests. The app manifest does not declare the `internetClient` capability.

### Supply Chain

- Dependencies are managed via [Dependabot](.github/dependabot.yml).
- Releases are produced from [release-please](.github/workflows/release-please.yaml), with version bumps gated on Conventional Commits in the merged PR titles.

## Disclosure Policy

When a vulnerability is confirmed:

1. A fix is developed in a private fork or branch
2. A new release is published with the fix
3. The security advisory is published on GitHub with credit to the reporter
4. The CHANGELOG notes the security fix
