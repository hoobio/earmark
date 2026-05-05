# Changelog

## [0.1.7](https://github.com/hoobio/earmark/compare/v0.1.6...v0.1.7) (2026-05-05)


### Bug Fixes

* **audio:** release COM wrappers and adopt singleton view-model lifetime ([#28](https://github.com/hoobio/earmark/issues/28)) ([694819e](https://github.com/hoobio/earmark/commit/694819ea032cfe4e1be2f778e893cd1c315a284f))
* **audio:** release MMDevice / AudioSessionControl wrappers and ([694819e](https://github.com/hoobio/earmark/commit/694819ea032cfe4e1be2f778e893cd1c315a284f))

## [0.1.6](https://github.com/hoobio/earmark/compare/v0.1.5...v0.1.6) (2026-04-30)


### Features

* add Wave Link integration plus volume/mute rule actions ([#26](https://github.com/hoobio/earmark/issues/26)) ([f526d7e](https://github.com/hoobio/earmark/commit/f526d7e910053e3799f68e1a84c3394f1d34a012))


### Bug Fixes

* **rules:** atomic copy-on-write store to prevent crash when deleting a ([f526d7e](https://github.com/hoobio/earmark/commit/f526d7e910053e3799f68e1a84c3394f1d34a012))

## [0.1.5](https://github.com/hoobio/earmark/compare/v0.1.4...v0.1.5) (2026-04-30)


### Features

* **ci:** adopt Backstage hierarchy in DT SBOM upload ([#24](https://github.com/hoobio/earmark/issues/24)) ([b244c47](https://github.com/hoobio/earmark/commit/b244c47afb1ac576a3da013d90a17348b057d943))

## [0.1.4](https://github.com/hoobio/earmark/compare/v0.1.3...v0.1.4) (2026-04-27)


### Bug Fixes

* **installer:** wire MSI icon and add branded installer chrome ([#17](https://github.com/hoobio/earmark/issues/17)) ([7881d1c](https://github.com/hoobio/earmark/commit/7881d1cbc8094617821b26ef735d2f00a0e5fbf1))


### Performance Improvements

* **installer:** shrink MSI size ([#19](https://github.com/hoobio/earmark/issues/19)) ([5666084](https://github.com/hoobio/earmark/commit/566608467e259ea1f80bc454a997578f0ec565d0))

## [0.1.3](https://github.com/hoobio/earmark/compare/v0.1.2...v0.1.3) (2026-04-27)


### Bug Fixes

* **shutdown:** prevent process hang and slow startup ([#15](https://github.com/hoobio/earmark/issues/15)) ([73565c2](https://github.com/hoobio/earmark/commit/73565c2a0f3f21248d55dd62309174b6bdae134d))


### Performance Improvements

* **startup:** defer audio init and rules load off the UI thread ([73565c2](https://github.com/hoobio/earmark/commit/73565c2a0f3f21248d55dd62309174b6bdae134d))

## [0.1.2](https://github.com/hoobio/earmark/compare/v0.1.1...v0.1.2) (2026-04-26)


### Bug Fixes

* **installer:** copy AppIcon.ico to publish output for the MSI ([48ee6c1](https://github.com/hoobio/earmark/commit/48ee6c186aefa9e0b0ca60b67b03e8fb86e0e118))
* **ui:** tray context menu and installer icon publish ([#13](https://github.com/hoobio/earmark/issues/13)) ([48ee6c1](https://github.com/hoobio/earmark/commit/48ee6c186aefa9e0b0ca60b67b03e8fb86e0e118))


### Continuous Integration

* pin release-please to v1.3.1 ([48ee6c1](https://github.com/hoobio/earmark/commit/48ee6c186aefa9e0b0ca60b67b03e8fb86e0e118))

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
