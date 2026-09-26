# BOARD

> Компактный оперативный индекс work items. Каталог спецификаций находится в [SPEC-MAP.md](./SPEC-MAP.md).

## Backlog

| Work | Title | Specs | Owner | Priority |
|---|---|---|---|---|
| [WI-004](work/WI-004-macos-platform-parity.md) | macOS native foundation and permissions | INFRA-010, INFRA-009 | @nikita | P1 |
| [WI-008](work/WI-008-macos-recording-artifacts.md) | macOS recording and MP3 artifacts | INFRA-010, FEAT-012, FEAT-012.A | @nikita | P1 |
| [WI-009](work/WI-009-macos-meeting-detection.md) | macOS meeting detection parity | INFRA-010, FEAT-011 | @nikita | P1 |
| [WI-010](work/WI-010-macos-desktop-integration.md) | macOS desktop system integration | INFRA-010, FEAT-013, FEAT-013.A, FEAT-010.A | @nikita | P1 |
| [WI-011](work/WI-011-macos-app-dmg.md) | macOS app bundle and unsigned DMG | INFRA-010, INFRA-005.C | @nikita | P1 |
| [WI-012](work/WI-012-macos-release-acceptance.md) | macOS parity release acceptance | INFRA-010, INFRA-008 | @nikita | P1 |

## In Progress

| Work | Title | Specs | Owner | Started | Blocker |
|---|---|---|---|---|---|
| [WI-017](work/WI-017-public-github-repository.md) | Public GitHub open-source repository | PROP-000, PROP-006 | @nikita | 2026-09-25 | — |
| [WI-007](work/WI-007-macos-audio-capture.md) | macOS audio observation and capture | INFRA-010, INFRA-009, FEAT-011 | @nikita | 2026-07-31 | WI-004 required for bundled permission/live acceptance; native and managed audio work can proceed |

## Blocked

| Work | Title | Owner | Reason | Waiting for |
|---|---|---|---|---|
| [WI-016](work/WI-016-speaker-aware-transcription.md) | Shared speaker-aware transcription pipeline | @nikita | Windows native/MSIX preparation is complete; real multi-chunk quality and visual acceptance remain open. | Representative meeting and manual UI acceptance |
| [WI-005](work/WI-005-cloud-transcription.md) | Online Groq/OpenRouter speaker-aware adaptation | @nikita | Live retry/cancel, provider setup UI and final platform acceptance remain open. | Provider account and manual UI acceptance |
| [WI-006](work/WI-006-local-whisper-transcription.md) | Full large-v3-turbo local adaptation | @nikita | Full-model CPU, clean-platform resources and Windows OS-level offline acceptance remain open. | Resource/offline matrix and manual UI acceptance |
| [WI-014](work/WI-014-zoom-screen-share-continuity.md) | Zoom screen-share recording continuity | @nikita | Implementation и code regressions готовы; acceptance требует живого Zoom transition. | User-run Zoom screen-share and macOS Space recheck |
| [WI-002](work/WI-002-windows-release-evidence.md) | Windows release evidence | @nikita | Automatic finish ожидает live-наблюдения; остальные release gates зависят от живой среды. | Real Zoom recheck, attended UAC and remaining live acceptance |
| [WI-003](work/WI-003-store-distribution-flight.md) | Store distribution flight | @nikita | Store-flight и local lifecycle требуют внешних данных и действий. | Partner Center identity, hosted privacy/support URLs, age rating, private flight and attended UAC |

## Done

| Work | Title | Owner | Date |
|---|---|---|---|
| [WI-015](work/archive/2026/WI-015-macos-ask-prompt-visibility.md) | macOS Ask prompt visibility | @nikita | 2026-08-11 |
| [WI-013](work/archive/2026/WI-013-candidate-gated-audio-observation.md) | Candidate-gated audio observation | @nikita | 2026-08-10 |
| [WI-001](work/archive/2026/WI-001-spec-workflow-migration.md) | Migrate spec-driven workflow | @nikita | 2026-07-31 |

Legacy BOARD history до WI сохранена в [snapshot](history/2026-07-31-pre-wi-operations.md) и Git.
