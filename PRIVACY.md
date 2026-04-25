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

Earmark does not declare the `internetClient` capability and makes no outbound network requests.

## Third-Party Services

None.

## Contact

If you have questions about this privacy policy, please open an issue at [github.com/hoobio/earmark](https://github.com/hoobio/earmark/issues).
