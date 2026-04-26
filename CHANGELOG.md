# Changelog

## [0.1.1](https://github.com/hoobio/earmark/compare/v0.1.0...v0.1.1) (2026-04-26)


### Features

* add verbose logging toggle to settings ([8705683](https://github.com/hoobio/earmark/commit/87056836f11fd9a8e7bc67301ea80ee386af6707))
* condition-based rule engine, Fluent-polished UI, branding, perf, and docs ([#1](https://github.com/hoobio/earmark/issues/1)) ([5b5b3a6](https://github.com/hoobio/earmark/commit/5b5b3a6161dd3ae878f915fd72ebb328f1ffe35e))
* WiX 5 MSI installer alongside MSIX ([#11](https://github.com/hoobio/earmark/issues/11)) ([cf64b32](https://github.com/hoobio/earmark/commit/cf64b32c4365523fa1cd552a798aaefb3e78e0b3))


### Bug Fixes

* **audio:** cache endpoint and session enumeration to avoid repeat COM ([5b5b3a6](https://github.com/hoobio/earmark/commit/5b5b3a6161dd3ae878f915fd72ebb328f1ffe35e))
* **ci:** bump CycloneDX tool to 6.1.1 for .slnx support ([#7](https://github.com/hoobio/earmark/issues/7)) ([b48ef3c](https://github.com/hoobio/earmark/commit/b48ef3c0f300eed94d8b37cd00ccb2b3cf0feb31))
* **ci:** emit CycloneDX SBOM at spec 1.6 ([#8](https://github.com/hoobio/earmark/issues/8)) ([3387c9a](https://github.com/hoobio/earmark/commit/3387c9af79df190e9aa5fbfae5c088c5a37e5a09))
* **ui:** close-to-tray and tray-icon thread bug ([#5](https://github.com/hoobio/earmark/issues/5)) ([23b0400](https://github.com/hoobio/earmark/commit/23b04008bdd81c0adc908af00fd32a90fd119993))


### Performance Improvements

* event-driven routing applier and session lifecycle events ([#9](https://github.com/hoobio/earmark/issues/9)) ([8705683](https://github.com/hoobio/earmark/commit/87056836f11fd9a8e7bc67301ea80ee386af6707))


### Build System

* **deps:** Bump actions/github-script from 8 to 9 ([#2](https://github.com/hoobio/earmark/issues/2)) ([fb780bf](https://github.com/hoobio/earmark/commit/fb780bfa47243b2dd85056a416bbe6ce3907ef2f))
* **deps:** Bump googleapis/release-please-action from 4 to 5 ([#3](https://github.com/hoobio/earmark/issues/3)) ([b3865cd](https://github.com/hoobio/earmark/commit/b3865cda0f4db928be87ab81aed2278245a1b21b))


### Continuous Integration

* add CycloneDX SBOM generation and Dependency-Track upload ([#6](https://github.com/hoobio/earmark/issues/6)) ([d16860b](https://github.com/hoobio/earmark/commit/d16860b3c7010f6a1844359e83e9a81555c73f61))
* rename app-id input to client-id for create-github-app-token v3 ([f65a397](https://github.com/hoobio/earmark/commit/f65a3976aca25cd9496fc729dcddfc9929b332ee))
* switch to pipeline-tools and combine SBOM + release into one workflow ([#10](https://github.com/hoobio/earmark/issues/10)) ([91ec101](https://github.com/hoobio/earmark/commit/91ec10163cf911a5b648bbed974ec095a1f8b3dc))
