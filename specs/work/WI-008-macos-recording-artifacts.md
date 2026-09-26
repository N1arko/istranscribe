# WI-008: Реализовать macOS recording и MP3 artifacts

- Kind: `implement`
- Canon action: `none`

## Outcome

Ручная или уже подтверждённая Ask-запись на Mac создаёт один проверенный
output+microphone MP3 и восстанавливается после штатных сбоев.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording`
- Governing: `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization`
- Governing: `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.format`
- Governing: `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization`

## Dependencies

- Depends on: `WI-007`.

## Scope

- In: macOS recording coordinator, manual/confirmed-Ask start, pause/resume,
  finish/discard, timestamped source legs, mixing, exact MP3 preset, verification,
  atomic promotion, device continuity, crash recovery, cleanup and legacy history actions.
- Out: candidate detection, Ask eligibility, menu bar/widget platform behavior,
  release bundle/DMG and transcription.

## Acceptance

- [ ] Manual recording produces one decodable `.mp3` at `48 kHz / stereo / 128 kbps`
  with proven output and microphone content.
- [ ] Pause/resume and multi-leg device/process changes preserve active duration and alignment.
- [ ] Output-only and microphone-only degraded sessions remain valid and explained.
- [ ] Failure injection and process termination preserve a readable artifact or
  actionable recovery sources at every finalization stage.
- [ ] New and legacy recordings support list/open/reveal/remove semantics.
- [ ] Two-hour smoke, macOS tests and complete affected Windows recording regressions pass.

## Result

Заполняется при завершении WI.
