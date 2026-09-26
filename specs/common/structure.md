# Project Structure {#root}

## Spec-space {#spec-space}

- `SPEC-MAP.md` — человекочитаемый каталог канона и lifecycle.
- `BOARD.md` — компактный индекс WI.
- `work/` — scope, acceptance и Result каждого WI; `work/archive/YYYY/` хранит завершённые проходы.
- `WAL.md` — checkpoints незавершённых WI.
- `TECHDEBT.md` — долгоживущие инженерные компромиссы.
- `history/` — immutable snapshots прежнего операционного слоя, исключённые из active canon.
- `protocols/` — маршрутизация и правила синхронизации.

## Modules and namespaces {#modules}

| Module | Spec namespace | Spec folder | Responsibility |
|---|---|---|---|
| `app` | `spec://modules/app/` | `specs/modules/app/` | Пользовательские сценарии, detection policy, recording experience, localization и transcription capabilities. |
| `platform` | `spec://modules/platform/` | `specs/modules/platform/` | Platform adapters, persistence, packaging, distribution, acceptance и recovery contours. |

## Code ownership {#code-map}

| Spec namespace | Code and artifacts | Responsibility |
|---|---|---|
| `spec://common/*` | `src/IsTranscribe.Core/**`, `src/IsTranscribe.Application/**`, `tests/IsTranscribe.Core.Tests/**`, `tests/IsTranscribe.Application.Tests/**` | Shared product contracts, runtime policies and application orchestration. |
| `spec://modules/app/*` | `src/IsTranscribe.Desktop/**`, `src/IsTranscribe.App/**`, `tests/IsTranscribe.Desktop.Tests/**`, `tests/IsTranscribe.App.Tests/**` | Desktop experience and retained legacy UI behavior. |
| `spec://modules/platform/*` | `src/IsTranscribe.Persistence/**`, `src/IsTranscribe.Platform.*/**`, `src/IsTranscribe.App.Windows/**`, `src/IsTranscribe.App.MacOS/**`, `packaging/**`, `eng/**`, `tools/**`, platform/persistence tests | Platform composition, storage, capture, packaging and release evidence. |

`src/IsTranscribe.Host/**` and WPF `src/IsTranscribe.App/**` retain legacy code where an active spec explicitly requires compatibility. New release-v2 ownership belongs to the application, desktop and platform projects above.

`spec://modules/app/FEAT-017-speaker-aware-transcription#root` owns shared
`Application/Transcription/Speaker*`, `TranscriptionSourceHandoff`, speaker
model/protocol contracts in `Transcription.Local`, `Worker/Runtime/SpeakerDiarizationCommand`,
`native/speaker/**` and `eng/transcription/Build-SpeakerNative.ps1`. Platform
recording coordinators retain source handoff responsibility under FEAT-012/017.

## Update rules {#updates}

Update this file only when modules, namespaces, code ownership or the spec-space topology change. Ordinary new specs, WI status changes and WAL checkpoints do not change this map.
