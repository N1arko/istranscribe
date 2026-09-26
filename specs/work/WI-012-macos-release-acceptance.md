# WI-012: Закрыть macOS parity release acceptance

- Kind: `implement`
- Canon action: `none`

## Outcome

Один immutable DMG candidate проходит канонический macOS end-to-end flow,
lifecycle, recovery, resource, compatibility и cross-platform regression gates.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#acceptance`
- Constraint: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#verification`
- Constraint: `spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#performance`

## Dependencies

- Depends on: `WI-008`, `WI-009`, `WI-010`, `WI-011`, `WI-013`.
- Unlocks: `WI-005`, `WI-006`.

## Scope

- In: exact candidate identity, verified DMG installation, representative desktop
  and Zen/Safari meeting E2E, manual recording/widget/history/settings/Finder,
  compatibility index, update/uninstall/reinstall, crash recovery, permission/device
  failures, idle/long-run resources, accessibility and Windows/shared regression evidence.
- Out: Developer ID/notarization, live installation of every supported third-party
  client, transcription engines and product behavior outside INFRA-010.

## Acceptance

- [ ] One exact DMG candidate passes packaging verification, installation and
  contextual permission onboarding on the acceptance Mac.
- [ ] Desktop and browser representative flows complete detect → Ask → accept →
  output+microphone MP3 → automatic finish → open/reveal.
- [ ] Manual controls, pause-aware widget, menu bar, login item, notifications,
  history and settings pass installed-app smoke.
- [ ] All declared services have deterministic compatibility fixtures and truthful live status.
- [ ] Update/reinstall preservation, process-kill recovery and permission/device failure matrix pass.
- [ ] Eight-hour idle and two-hour recording/resource gates pass.
- [ ] Evidence is schema-valid, privacy-gated and bound to the candidate hashes.
- [ ] Complete macOS/shared and affected Windows regression suites pass.
- [ ] Every `INFRA-010#acceptance` clause is satisfied.

## Result

Заполняется при завершении WI.
