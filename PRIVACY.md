# Privacy Policy

**Earmark**

*Last updated: April 26, 2026*

## Data Collection

Earmark does **not** collect, store, or transmit any personal data or telemetry. There is no analytics endpoint, crash reporter, or "phone home" of any kind.

## How It Works

Earmark runs entirely as a local desktop application:

- It reads the list of audio sessions and audio endpoints from Windows Core Audio APIs.
- It resolves running process names and paths via Windows process APIs.
- It applies routing rules by calling Windows audio policy APIs to pin specific apps or roles to specific devices.

All of this happens on your machine. None of it leaves your machine.

## Local Files

| What | Where |
|------|-------|
| Rules | `%UserProfile%\Documents\Hoobi\Earmark\rules.json` |
| Settings | `%UserProfile%\Documents\Hoobi\Earmark\settings.json` |
| Logs | `%LocalAppData%\Earmark\logs\earmark-{yyyyMMdd-HHmmss}.log` |

The `Documents` location is intentional: OneDrive can sync rules across machines if you have it enabled. If you'd rather your rules stay on a single machine, exclude `Documents\Hoobi\Earmark\` from OneDrive sync.

Logs include process names, executable paths, and audio endpoint identifiers (for example "Speakers (Realtek)"). They do not include any user content like browser tabs, audio waveforms, or document contents.

## Network Access

The Microsoft Store (MSIX) build declares no `internetClient` capability and makes no outbound network requests. It receives updates through the Store.

The standalone (MSI) build checks GitHub Releases for a newer version: once in the background on launch, and again whenever you click "Check for updates" in Settings. This is a single request to `api.github.com` that sends no personal data (only the standard user-agent string) and is used solely to compare version numbers. You can turn off the automatic check under Settings > About > "Check automatically on launch"; the manual button still works on demand.

Beyond that, Earmark makes no outbound requests and has no analytics, crash reporting, or telemetry of any kind.

## Third-Party Services

None.

## Contact

If you have questions about this privacy policy, please open an issue at [github.com/hoobio/earmark](https://github.com/hoobio/earmark/issues).
