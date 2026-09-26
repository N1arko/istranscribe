---
status: active
---

# INFRA-008: Release Hardening And Windows Acceptance {#root}

## Простыми словами {#plain-language}

Последняя волна доказывает, что готов весь продуктовый путь на чистой Windows x64: приложение устанавливается, не мешает работе, корректно распознаёт встречи, сохраняет звук, восстанавливается после сбоев и удаляется без потери пользовательских файлов.

## Goal {#goal}

Провести requirement-by-requirement release audit, закрыть найденные дефекты и собрать доказуемый Windows x64 release candidate по `PROP-006`.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#acceptance`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#acceptance`
- `spec://modules/app/FEAT-011-meeting-detection-v2#acceptance`
- `spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance`
- `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#acceptance`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#acceptance`
- `spec://modules/app/FEAT-014-transcription-extension-seam#acceptance`
- `spec://modules/app/FEAT-010.A-release-v2-localization#acceptance`
- `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#acceptance`

## Scope {#scope}

### In scope {#scope.in}

- complete automated test suite and clean-build validation;
- consent-aware representative live meeting plus deterministic service compatibility matrix;
- false-positive background workload;
- recording quality/listening verification;
- crash, disk, device and update failure injection;
- performance/resource budgets;
- visual/accessibility QA;
- signed installer lifecycle on clean Windows x64;
- security/privacy audit;
- release notes, known limitations and release evidence index;
- fixes required to satisfy governing acceptance.

### Out of scope {#scope.out}

- new product features;
- transcription engine;
- Windows ARM64;
- macOS implementation;
- store submission.

## Reference Environments {#environments}

- Windows 11 x64 current stable, primary release environment;
- Windows 10 22H2 x64 compatibility environment with explicit capability degradation where process loopback is unavailable;
- one clean local user without administrator privileges;
- speakers + built-in microphone;
- USB headset;
- Bluetooth headset with device/profile switching;
- at least one multi-monitor/high-DPI setup.

## End-To-End Scenarios {#e2e}

Required scenarios:

1. fresh install → one-surface setup → listening;
2. naturally occurring, user-authorized supported meeting → Ask → accept → record → processing → open ready file; Zen Browser is preferred when it is the user's active environment;
3. deterministic current-build compatibility evidence for Zoom, Teams, Google Meet, Яндекс Телемост and Контур.Толк across their declared desktop/browser surfaces;
4. unknown dedicated meeting app positive confirmation/profile creation;
5. Skip and persistent Ignore behavior;
6. manual `Record now`;
7. output-only, microphone-only and output+microphone;
8. pause/resume/finish;
9. default device change and disconnect during recording;
10. crash during recording and each post-processing stage;
11. recordings folder unavailable/disk full;
12. reboot/autostart/single-instance;
13. in-place update and uninstall/reinstall with preserved data;
14. legacy v1 settings/database/artifact migration;
15. proof of zero Fireworks/transcription network calls.

## Performance Budgets {#performance}

On the documented reference Windows 11 x64 machine:

- median idle CPU while listening: `≤ 1%` after warm-up;
- steady idle working set: `≤ 200 MB` with no monotonic growth during the 8-hour soak;
- primary window cold start to interactive: `≤ 2 seconds` after process launch;
- UI input remains responsive during recording finalization;
- installed payload target: `≤ 150 MB`, with any exception documented and approved before release;
- prompt appears within the FEAT-011 timing gate.

Reference hardware and measurement method are recorded with results.

## Reliability Gates {#reliability}

- 8-hour listening soak has no crash, deadlock or unbounded handle/memory growth.
- A sequence of at least 10 meetings in one process lifetime produces 10 consistent session outcomes.
- Failure injection never deletes every readable copy of captured audio.
- Unexpected shutdown recovery reaches a stable user-visible state on next launch.
- Background loops stop cleanly on Quit and leave no orphan process.
- Logs use bounded retention and contain no API keys, raw pre-confirmation audio or sensitive window title content.

## Experience Gates {#experience}

- Every FEAT-013 canonical state has approved light/dark renders.
- Keyboard-only flow covers setup, prompt, recording and opening the result.
- `100–200%` scaling has no clipped primary action or inaccessible content.
- Error messages state what happened, whether audio is safe and the next action.
- No v1 Fireworks, Auto-mode, six-tab settings or raw capability dashboard surfaces remain reachable in the release build.
- Live RU → EN → RU switching refreshes the native tray, an open Ask prompt and a visible passive notification while preserving each surface's current semantic state.

## Evidence Index {#evidence}

The release candidate stores or links:

- exact source revision/version;
- build and test command outputs;
- test result files;
- package manifest, signature and checksums;
- install/update/uninstall smoke log;
- detection replay metrics and live matrix;
- per-scenario validation level from `FEAT-011.A`: `contract_verified | environment_observed | live_verified`;
- soak/performance measurements;
- artifact listening/fixture verification;
- visual QA renders;
- known limitations and supported-platform statement.

## Acceptance {#acceptance}

INFRA-008 is complete when:

1. Every `PROP-006#acceptance` item maps to direct passing evidence.
2. All dependency-spec acceptance sections are proven in current release artifacts.
3. Automated tests and clean Windows x64 end-to-end scenarios pass.
4. Detection, reliability, performance, privacy and visual gates pass.
5. Any release-blocking finding discovered during audit is fixed and reverified.
6. The final signed installer and checksum are the same artifacts used for acceptance.
7. `BOARD`, `WAL`, release version and evidence index are synchronized.

Staged Zoom and Контур.Толк calls are not release prerequisites. Any client-specific
scenario without a real observation keeps its lower evidence level in the release index.

## Document Notes {#document-notes}

- 2026-07-12: Added the FEAT-010.A dependency and final live localization observation for the native tray, Ask prompt and passive notification.
- 2026-07-12: Added the FEAT-012.A dependency so clean-machine artifact playback verifies the royalty-cleared MP3 cutover.
- 2026-07-12: Detection acceptance now follows `FEAT-011.A`: one representative user-authorized natural call is sufficient for product E2E, while every declared service retains deterministic current-build compatibility evidence and an explicit evidence level.
- 2026-07-11: Initial release hardening spec authored as the final gate for Windows x64 release v2.
