# SPEC MAP

`SPEC-MAP.md` — человекочитаемый каталог канона isTranscribe. Он показывает ответственность и lifecycle спецификаций; статус реализации живёт в `BOARD.md`.

## Active canon

### Common

| Spec | Responsibility | Lifecycle |
|---|---|---|
| [PROP-000](common/PROP-000.md) | Workflow, качество и трассировка | active |
| [PROP-005](common/PROP-005-local-runtime-and-operations.md) | Local runtime, storage, diagnostics и recovery | active |
| [PROP-006](common/PROP-006-release-v2-product-canon.md) | Release-v2 product canon | active |

### App

| Spec | Responsibility | Lifecycle |
|---|---|---|
| [FEAT-003](modules/app/FEAT-003-manual-recording-controls-and-tray.md) | Manual controls, tray и hotkeys | active |
| [FEAT-004](modules/app/FEAT-004-recordings-home-and-artifact-access.md) | Доступ к сохранённым recordings и legacy artifacts | active |
| [FEAT-005](modules/app/FEAT-005-application-rules-and-discovery.md) | App rules и discovery contracts | active |
| [FEAT-007](modules/app/FEAT-007-device-aware-recording-continuity.md) | Device continuity policy | active |
| [FEAT-010.A](modules/app/FEAT-010.A-release-v2-localization.md) | RU/EN release-v2 localization | active |
| [FEAT-011](modules/app/FEAT-011-meeting-detection-v2.md) | Meeting detection policy | active |
| [FEAT-011.A](modules/app/FEAT-011.A-consent-aware-windows-validation.md) | Consent-aware validation and Zen support | active |
| [FEAT-012](modules/app/FEAT-012-recording-artifact-pipeline-v2.md) | Recording artifact pipeline | active |
| [FEAT-012.A](modules/app/FEAT-012.A-royalty-cleared-mp3-artifact.md) | MP3 release artifact contract | active |
| [FEAT-013](modules/app/FEAT-013-minimal-desktop-experience.md) | Minimal desktop experience | active |
| [FEAT-013.A](modules/app/FEAT-013.A-floating-recording-widget.md) | Floating recording widget | active |
| [FEAT-014](modules/app/FEAT-014-transcription-extension-seam.md) | Disabled transcription runtime and extension seam | active |
| [FEAT-015](modules/app/FEAT-015-cloud-transcription-groq-openrouter.md) | Opt-in cloud transcription | active |
| [FEAT-016](modules/app/FEAT-016-local-whisper-transcription.md) | Local Whisper transcription | active |
| [FEAT-017](modules/app/FEAT-017-speaker-aware-transcription.md) | Three transcription modes, source-aware diarization and speaker artifacts | active |

### Platform

| Spec | Responsibility | Lifecycle |
|---|---|---|
| [INFRA-002](modules/platform/INFRA-002-local-persistence-and-secret-storage.md) | Persistence, local paths and secret storage | active |
| [INFRA-003](modules/platform/INFRA-003-windows-audio-capture-foundation.md) | Windows audio capture primitives | active |
| [INFRA-004](modules/platform/INFRA-004-diagnostics-jobs-and-recovery.md) | Diagnostics, jobs and recovery | active |
| [INFRA-005](modules/platform/INFRA-005-windows-installer-and-shell-integration.md) | Legacy Windows installation baseline | active |
| [INFRA-005.A](modules/platform/INFRA-005.A-production-windows-x64-installer.md) | MSIX payload and local acceptance | active |
| [INFRA-005.B](modules/platform/INFRA-005.B-store-signed-windows-distribution.md) | Store distribution and signing | active |
| [INFRA-005.C](modules/platform/INFRA-005.C-deterministic-third-party-notices.md) | Third-party notices and supply-chain evidence | active |
| [INFRA-007](modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell.md) | Core/Avalonia architecture foundation | active |
| [INFRA-008](modules/platform/INFRA-008-release-hardening-and-windows-acceptance.md) | Windows release hardening and acceptance | active |
| [INFRA-009](modules/platform/INFRA-009-cross-platform-repository-boundaries.md) | Dual-platform repository boundary | active |
| [INFRA-010](modules/platform/INFRA-010-macos-platform-parity-and-dmg.md) | macOS parity and DMG | active |

## Draft

Нет draft-спек: все будущие работы в BOARD уже имеют implementation-ready governing canon.

## Superseded

| Spec | Replaced by | Lifecycle |
|---|---|---|
| [PROP-001](common/PROP-001-product-canon.md) | PROP-006 | superseded |
| [PROP-002](common/PROP-002-app-shell-and-settings.md) | FEAT-013 and PROP-006 | superseded |
| [PROP-003](common/PROP-003-audio-capture-and-device-observation.md) | FEAT-011, FEAT-012 and INFRA-003 | superseded |
| [PROP-004](common/PROP-004-meeting-session-and-data-model.md) | FEAT-012 and FEAT-014 | superseded |
| [FEAT-001](modules/app/FEAT-001-first-run-setup-and-settings.md) | FEAT-013 | superseded |
| [FEAT-002](modules/app/FEAT-002-automatic-detection-and-session-lifecycle.md) | FEAT-011 | superseded |
| [FEAT-006](modules/app/FEAT-006-fireworks-transcription-and-markdown-export.md) | FEAT-014 | superseded |
| [FEAT-008](modules/app/FEAT-008-modern-ui-design-system.md) | FEAT-013 | superseded |
| [FEAT-009](modules/app/FEAT-009-settings-ux-simplification.md) | FEAT-013 | superseded |
| [FEAT-010](modules/app/FEAT-010-russian-localization.md) | FEAT-010.A | superseded |
| [INFRA-001](modules/platform/INFRA-001-windows-desktop-host-baseline.md) | INFRA-007 | superseded |

## Retired

| Spec | Reason | Lifecycle |
|---|---|---|
| [INFRA-006](modules/platform/INFRA-006-release-v2-canon-and-work-breakdown.md) | Завершённый переход к release-v2; актуальные waves и dependencies живут ниже. | retired |

## Waves

- Windows release evidence: `WI-002` / `INFRA-008`, с внешними gates `WI-003` / `INFRA-005.B`.
- Cross-platform parity: `INFRA-009 → WI-004 → WI-007 → WI-008 → WI-009 → WI-010 → WI-011 → WI-013 → WI-012 / INFRA-010`.
- Optional transcription engines: `WI-012 → WI-016 / FEAT-017`, with online
  adaptation in `WI-005 / FEAT-015` and local full `large-v3-turbo` adaptation
  in `WI-006 / FEAT-016`.

## Dependencies

- `FEAT-015` и `FEAT-016` используют extension seam `FEAT-014`, artifact contract `FEAT-012.A` и dual-platform boundary `INFRA-009/010`.
- `FEAT-017` owns transcription mode, source retention, local diarization,
  speaker identity and final artifacts; `FEAT-015` and `FEAT-016` supply its
  online and local word-timestamp ASR paths.
- `INFRA-008` принимает release-v2 product, detection, recording, UX, localization и installer acceptance.
- `INFRA-005.B` использует payload/acceptance из `INFRA-005.A` и notice evidence из `INFRA-005.C`.
