# WI-010: Реализовать macOS desktop system integration

- Kind: `implement`
- Canon action: `none`

## Outcome

Установленное приложение ведёт себя как tray-first Mac app: один экземпляр,
menu bar, launch at login, системные уведомления, Finder actions и корректный
виджет на нескольких мониторах.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services`
- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions`
- Constraint: `spec://modules/app/FEAT-013-minimal-desktop-experience#tray`
- Constraint: `spec://modules/app/FEAT-013.A-floating-recording-widget#placement`
- Constraint: `spec://modules/app/FEAT-010.A-release-v2-localization#surfaces`

## Dependencies

- Depends on: `WI-004`, `WI-008`, `WI-009`.

## Scope

- In: per-user single instance and activation, `SMAppService` login item,
  menu-bar states/commands, native notifications, Finder open/reveal, NSScreen
  working areas, topmost widget placement and permission onboarding/actions.
- Out: audio/detection algorithms, new navigation, App Store integration,
  release DMG creation and transcription.

## Acceptance

- [ ] Concurrent launches create one runtime/menu-bar item and activate the primary instance.
- [ ] Login-item enable/disable/approval states are observable and login start remains tray-first.
- [ ] Menu commands and native notifications route through the existing shared runtime.
- [ ] Finder open/reveal and missing-path failures behave deterministically.
- [ ] Widget placement, restore and topmost behavior pass deterministic adapter tests.
- [ ] Permission UI covers every canonical state without startup prompt loops.
- [ ] RU/EN, themes, accessibility, Desktop and Windows regressions pass.

## Result

Заполняется при завершении WI.
