# WI-009: Реализовать macOS meeting detection parity

- Kind: `implement`
- Canon action: `none`

## Outcome

Representative desktop и browser meeting на Mac проходят общий candidate lifecycle:
устойчивые сигналы создают Ask, Skip подавляет повтор, потеря eligibility запускает
automatic finish.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection`
- Governing: `spec://modules/app/FEAT-011-meeting-detection-v2#confidence`
- Constraint: `spec://modules/app/FEAT-011-meeting-detection-v2#privacy`

## Dependencies

- Depends on: `WI-007`, `WI-008`.

## Scope

- In: application/bundle/process-family evidence, privacy-reduced window and
  Accessibility evidence, local speech activity, macOS profile fixtures, browser
  normalization, Ask/Skip lifecycle and automatic-finish parity.
- Out: redesign of scorer/thresholds, browser extension, calendar/network signals,
  complete release compatibility matrix and DMG lifecycle.

## Acceptance

- [ ] macOS providers emit normalized process, window, output and microphone speech facts.
- [ ] Windows/macOS replays produce identical score, Ask, suppression and finish decisions.
- [ ] Zoom/Teams desktop and declared browser-service profiles have positive,
  listen-only and idle-negative fixtures.
- [ ] Browser playback and application identity alone remain below Ask eligibility.
- [ ] Pre-confirmation privacy tests prove zero audio files, raw-title logging and network calls.
- [ ] One installed desktop client and Zen or Safari provider smoke reach expected Ask signals.

## Result

Заполняется при завершении WI.
