# WI-007: Реализовать macOS audio observation и capture

- Kind: `implement`
- Canon action: `none`

## Outcome

macOS audio adapter наблюдает активность приложений и микрофона и после
разрешения отдаёт синхронизированные output и microphone PCM streams.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection`
- Constraint: `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts`
- Constraint: `spec://modules/app/FEAT-011-meeting-detection-v2#privacy`

## Dependencies

- Depends on: `WI-004`.

## Scope

- In: process/bundle/PID observation, Core Audio process taps, system-output
  fallback, microphone capture, device inventory, timestamps, bounded buffers,
  source lifecycle and permission/device/process failure states.
- Out: session persistence, MP3 finalization, meeting scoring, window evidence,
  desktop system services and DMG.

## Acceptance

- [ ] `IAudioPlatform` публикует coherent process, output, microphone и device snapshots.
- [ ] Process-scoped output tap и microphone capture одновременно отдают readable
  timestamped PCM на Apple Silicon.
- [ ] До подтверждения observation остаётся bounded, disk-free и zero-network.
- [ ] Process exit, device change, permission revocation и callback teardown проходят
  deterministic lifecycle tests.
- [ ] Audio golden подтверждает оба источника и общую временную шкалу.
- [ ] macOS/shared tests и затронутые Windows regressions проходят.

## Result

Заполняется при завершении WI.
