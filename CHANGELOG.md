# Changelog

## [0.2.0](https://github.com/hoobio/earmark/compare/v0.1.9...v0.2.0) (2026-06-01)


### ⚠ BREAKING CHANGES

* **rules:** pinned/one-shot actions, drag-and-drop, consolidated schema ([#49](https://github.com/hoobio/earmark/issues/49))
* **devices:** rules.json schema replaced - actions and conditions now use a Kind plus orthogonal mode fields (direction / membership / muted) and a Pinned flag; the old type-per-row enum no longer deserialises.

### Features

* add "always show pinned apps" setting for rule-pinned chips ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* add appearance backdrop setting (Mica, Acrylic, Solid) ([#39](https://github.com/hoobio/earmark/issues/39)) ([7f2769a](https://github.com/hoobio/earmark/commit/7f2769a4d1286d2d999c7e80710145b7dfea0511))
* add Devices-page reset to default and new-install seeding ([baffea1](https://github.com/hoobio/earmark/commit/baffea1346924afcbd953e1ce1a0cecbdb484761))
* card-height and card-divider options for the Devices page ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* close and terminate an app from its chip menu, across all the ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* device customisation panel (glyph + accent colour) ([#46](https://github.com/hoobio/earmark/issues/46)) ([34956eb](https://github.com/hoobio/earmark/commit/34956ebfd318fe20fd08d8e2105c762c094245a8))
* Devices app chips, close/terminate, undo, and card options ([#44](https://github.com/hoobio/earmark/issues/44)) ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* Devices page options menu (show title bar / show hidden / lock ([288d13a](https://github.com/hoobio/earmark/commit/288d13a7cc043fd0f3c4d7eaffd6818d1c8d0040))
* **devices:** add a Bluetooth connect/disconnect button on Bluetooth ([c6feb57](https://github.com/hoobio/earmark/commit/c6feb5797b44d5ac89ed8d617e556a1524a32b0e))
* **devices:** add persisted device-card display toggles ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* hide an app's chip on a single device from its chip context menu ([34956eb](https://github.com/hoobio/earmark/commit/34956ebfd318fe20fd08d8e2105c762c094245a8))
* hide app chips via a context menu, manage from settings ([#41](https://github.com/hoobio/earmark/issues/41)) ([8e62187](https://github.com/hoobio/earmark/commit/8e62187c223682f26182f093af0d316b3ceb30e1))
* per-device accents, customise dialog, and Devices page options ([#47](https://github.com/hoobio/earmark/issues/47)) ([288d13a](https://github.com/hoobio/earmark/commit/288d13a7cc043fd0f3c4d7eaffd6818d1c8d0040))
* per-device app-chip indicators with drag-to-reroute on the Devices ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* per-device deterministic accent colours, reworked customise ([288d13a](https://github.com/hoobio/earmark/commit/288d13a7cc043fd0f3c4d7eaffd6818d1c8d0040))
* persist disconnected devices and add the Show disconnected filter ([#48](https://github.com/hoobio/earmark/issues/48)) ([c6feb57](https://github.com/hoobio/earmark/commit/c6feb5797b44d5ac89ed8d617e556a1524a32b0e))
* persist the navigation pane expand/collapse state across ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* **rules:** flag matched-but-disconnected devices in the match chip ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* **rules:** per-field match modes (regex / wildcard / exact) with a ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* **rules:** pinned/one-shot actions, drag-and-drop, consolidated schema ([#49](https://github.com/hoobio/earmark/issues/49)) ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* **rules:** warn when an action is superseded by a higher-priority rule ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* undo app-chip hide, reorder, and group changes with Ctrl+Z ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))


### Bug Fixes

* allow click-to-mute on the device icon when volume controls are ([34956eb](https://github.com/hoobio/earmark/commit/34956ebfd318fe20fd08d8e2105c762c094245a8))
* always animate the Devices card reflow (apps row, device ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* expanding a card's rules no longer shrinks a shorter sibling card ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* glide app chips on re-sort by ranking, not collection Move ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* keep the device accent tile coloured while a mute rule is active ([288d13a](https://github.com/hoobio/earmark/commit/288d13a7cc043fd0f3c4d7eaffd6818d1c8d0040))
* lay out Insights.Resource.dll for self-contained app notifications ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))
* move a device directly from one group into another in one drag ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))


### Documentation

* note the backdrop setting in the design-language section ([7f2769a](https://github.com/hoobio/earmark/commit/7f2769a4d1286d2d999c7e80710145b7dfea0511))
* rewrite the rule schema reference for the consolidated model ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* spec loopback / listening chip suppression heuristic ([#45](https://github.com/hoobio/earmark/issues/45)) ([16b4aad](https://github.com/hoobio/earmark/commit/16b4aadcd598be4e04a12263abf8e3917f6daf45))


### Code Refactoring

* **devices:** split oversized DeviceCard and HomeViewModel into ([c6feb57](https://github.com/hoobio/earmark/commit/c6feb5797b44d5ac89ed8d617e556a1524a32b0e))


### Tests

* cover rule matcher, device-rule resolver, and shadow analyzer ([1ea0d9e](https://github.com/hoobio/earmark/commit/1ea0d9e0afcfee16dd84dba97dd08aa615044580))
* **devices:** cover device-key identity, list filter, and store re-key ([c6feb57](https://github.com/hoobio/earmark/commit/c6feb5797b44d5ac89ed8d617e556a1524a32b0e))


### Build System

* update dependencies to latest stable ([dd387a6](https://github.com/hoobio/earmark/commit/dd387a6c2715e1eedc9305f69f591d951ae40bbb))


### Continuous Integration

* use the release-please PR body for pre-release notes ([288d13a](https://github.com/hoobio/earmark/commit/288d13a7cc043fd0f3c4d7eaffd6818d1c8d0040))

## [0.1.9](https://github.com/hoobio/earmark/compare/v0.1.8...v0.1.9) (2026-05-30)


### Features

* add device grouping (container model) to the Devices page ([#38](https://github.com/hoobio/earmark/issues/38)) ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))
* in-app update check with version and About in Settings ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))
* peak meter colour modes, single-colour fill, and a Settings colour ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))
* per-app indicator linger, audio-order, and show/hide toggles ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))
* re-apply routing rules when audio devices connect or disconnect ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))


### Bug Fixes

* include Wave Link mix rules in device rule summaries ([f607013](https://github.com/hoobio/earmark/commit/f607013f6b614e817b1b1e08e28a726f37693891))


### Build System

* set Microsoft Store package identity for Earmark ([d9ad050](https://github.com/hoobio/earmark/commit/d9ad050944342d739582000890e0c56fdfd2f528))

## [0.1.8](https://github.com/hoobio/earmark/compare/v0.1.7...v0.1.8) (2026-05-29)


### Features

* devices page peak meters, drag-reorder, and silent-app chips ([#35](https://github.com/hoobio/earmark/issues/35)) ([bc68d1f](https://github.com/hoobio/earmark/commit/bc68d1f935e59b9e660f6b615b9d34feb782c49d))
* explicit-save rule editor with an else branch and duplicate-rule ([bc68d1f](https://github.com/hoobio/earmark/commit/bc68d1f935e59b9e660f6b615b9d34feb782c49d))


### Bug Fixes

* keep rule-locked volume sliders usable inside a card and unclipped ([bc68d1f](https://github.com/hoobio/earmark/commit/bc68d1f935e59b9e660f6b615b9d34feb782c49d))


### Code Refactoring

* centralise device volume/mute resolution in DeviceRuleResolver ([bc68d1f](https://github.com/hoobio/earmark/commit/bc68d1f935e59b9e660f6b615b9d34feb782c49d))

## [0.1.7](https://github.com/hoobio/earmark/compare/v0.1.6...v0.1.7) (2026-05-29)


### Features

* add a setting to reconcile Wave Link device names ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))
* add Devices page with live cards, peak meter and Ctrl+Z undo ([#32](https://github.com/hoobio/earmark/issues/32)) ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **audio:** reconcile external mute changes via AudioEndpointVolume ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **home:** Ctrl+Z undo for hide / show, volume drags and mute toggles ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **home:** match device-card icons to Wave Link mixes ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))
* **home:** persist window size across launches ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **rules:** add ApplicationRunning and ApplicationNotRunning conditions ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **ui:** replace per-page subtext lines with info-icon tooltips ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **ui:** right-click menus to hide a device and enable or delete a rule ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))
* Wave Link card theming, app theme and per-device apps row ([#34](https://github.com/hoobio/earmark/issues/34)) ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))
* **wavelink:** route Wave Link input mute and volume over the WebSocket ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))


### Bug Fixes

* **audio:** keep CoreAudio COM off the UI thread to stop hangs ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))
* **audio:** release COM wrappers and adopt singleton view-model lifetime ([#28](https://github.com/hoobio/earmark/issues/28)) ([694819e](https://github.com/hoobio/earmark/commit/694819ea032cfe4e1be2f778e893cd1c315a284f))
* **audio:** release MMDevice / AudioSessionControl wrappers and ([694819e](https://github.com/hoobio/earmark/commit/694819ea032cfe4e1be2f778e893cd1c315a284f))
* **rules:** mark volume / mute rules targeting capture devices as Active ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))


### Performance Improvements

* **audio:** reuse cached MMDevice for peak-level reads to avoid ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))
* **audio:** reuse the cached MMDevice and skip no-op mute notifications ([0f7d12c](https://github.com/hoobio/earmark/commit/0f7d12c53b19d3993f249344b18ce53dc5e862ed))


### Documentation

* **release-please:** require blank lines between footer entries ([#33](https://github.com/hoobio/earmark/issues/33)) ([4d21efc](https://github.com/hoobio/earmark/commit/4d21efca2e2af1ab221d690ded77362fa200f124))


### Miscellaneous Chores

* rename CLAUDE.md to AGENTS.md and document event-driven ([f7951e8](https://github.com/hoobio/earmark/commit/f7951e8b2f5db119a3674826e84c70edd785f8b0))


### Build System

* **deps:** Bump hoobio/pipeline-tools from 1.5.0 to 2.2.1 ([#31](https://github.com/hoobio/earmark/issues/31)) ([afe3e45](https://github.com/hoobio/earmark/commit/afe3e450b0a3de3b141dbf31a1f469e16920865f))

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
