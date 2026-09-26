# WI-013: Включать audio observation только для meeting-кандидата

- Kind: `change`
- Canon action: `direct-edit`

## Outcome

В состоянии Listening без признаков встречи isTranscribe не держит system-audio/process
tap или микрофон открытыми; sample-bearing observation включается только для вероятного
meeting-кандидата, Ask или записи и своевременно выключается после их завершения.

## Specs

- Governing: `spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle`
- Affected: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection`
- Constraint: `spec://modules/app/FEAT-011-meeting-detection-v2#privacy`
- Constraint: `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts`

## Dependencies

- Depends on: `WI-007`, `WI-009`.
- Blocks: `WI-012`.

## Scope

- In: shared eligibility/demand policy, candidate-gated Windows/macOS render and
  microphone VAD, Ask/recording lifecycle, bounded teardown, explicit capability
  probe, diagnostics and regression tests.
- Out: meeting scoring weights, metadata-only audio-session observation, permission
  onboarding, recording artifact format and new user settings.

## Acceptance

- [x] Listening без eligible meeting-кандидата не создаёт system-audio/process tap
  или microphone capture на Windows и macOS.
- [x] Dedicated meeting app с active render metadata либо browser с meeting-specific
  window/Accessibility evidence включает observation до Ask без файла или network activity.
- [x] Pending Ask и подтверждённая запись удерживают требуемые candidate sources;
  ручная запись использует только recording-owned sources.
- [x] Skip, candidate loss, service pause и завершение записи закрывают observation
  не позднее чем через `5 секунд`, если другой eligible candidate отсутствует.
- [x] Явная setup/capability-проверка может кратко открыть микрофон и всегда закрывает его.
- [x] Browser media playback без meeting-specific evidence не включает render tap или микрофон.
- [x] Shared, Windows и macOS lifecycle/regression tests проходят.

## Result

Добавлены shared eligibility/demand policy и demand-driven lifecycle для macOS Core
Audio process taps, Windows render loopback и microphone VAD. Ask и automatic recording
удерживают candidate sources; Skip, timeout, candidate loss, service stop и session end
освобождают их. Explicit microphone probe использует краткоживущий capture.

Проверки: solution build — `0` warnings/errors; macOS contour — `309/309`; Windows
speech-provider filter — `17/17`; release app/DMG собраны и проверены. В живом macOS
smoke с активным Zoom capture закрылся после Ask timeout: процесс isTranscribe остался
с `running_input=false`, `running_output=false`, без Core Audio I/O thread и без новых
microphone retry после teardown.
