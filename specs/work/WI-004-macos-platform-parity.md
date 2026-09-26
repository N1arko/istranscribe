# WI-004: Подготовить macOS native foundation и permissions

- Kind: `implement`
- Canon action: `none`

## Outcome

macOS composition root работает внутри стабильной app identity, использует
канонические app-data paths и Keychain и публикует реальные состояния системных
permissions.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#target`
- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions`
- Affected: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services`
- Constraint: `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts`

## Scope

- In: stable bundle identity, minimal development app host, thin native bridge,
  app paths, Keychain vault, microphone/system-audio/Accessibility/screen-capture
  permission state and System Settings actions.
- Out: audio sample capture, recording, meeting detection, menu bar, login item,
  public app bundle/DMG и transcription engines.

## Acceptance

- [ ] Development `.app` собирается и запускается на macOS arm64 со стабильным
  bundle id и обязательными usage descriptions.
- [ ] App-data и recordings paths соответствуют INFRA-010 и сохраняют общий
  persistence contract.
- [ ] Keychain CRUD проходит без plaintext persistence и secret logging.
- [ ] Permission tests покрывают not_requested, granted, denied, revoked и
  needs-restart, а live smoke проверяет независимые microphone/system-audio flows.
- [ ] Общие macOS boundary tests и Windows regression для изменённых contracts проходят.

## Result

Заполняется при завершении WI.
