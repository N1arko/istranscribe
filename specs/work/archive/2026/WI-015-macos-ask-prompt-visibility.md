# WI-015: Показывать Ask на активном пространстве macOS

- Kind: `fix`
- Canon action: `none`

## Outcome

Опубликованный runtime-запрос Ask остаётся видимым пользователю на текущем macOS
Space и поверх полноэкранной встречи, даже когда системные уведомления ещё не разрешены.

## Specs

- Governing: `spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt`
- Affected: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services`
- Constraint: `spec://modules/app/FEAT-011-meeting-detection-v2#user-policy`

## Scope

- In: native presentation Ask window on macOS, active Space/full-screen visibility,
  notification-independent app surface and regression coverage.
- Out: detection scoring, Ask timeout, automatic finish, notification permission onboarding
  and Windows prompt presentation.

## Acceptance

- [x] Live reproduction `2026-08-11 08:04` закреплена: runtime опубликовал Ask,
  пользователь не увидел surface, запрос истёк и запись началась вручную.
- [x] Ask window на macOS получает floating/all-Spaces/full-screen-auxiliary presentation
  и выходит вперёд без захвата клавиатурного фокуса.
- [x] Отсутствующее разрешение системных уведомлений не скрывает app-owned Ask surface.
- [x] Windows/shared поведение Ask остаётся прежним.
- [x] Desktop и macOS regression tests, format и Release build проходят.

## Result

macOS presenter переводит нативное окно Ask в floating level, добавляет
`CanJoinAllSpaces | FullScreenAuxiliary` и выводит его вперёд без активации приложения.
Desktop composition передаёт presenter только из macOS entrypoint; Windows остаётся на
общем Avalonia-поведении. При отсутствующем notification permission app-owned surface
остаётся основным путём решения.

Проверки: Desktop `132/132`, macOS `65/65`, целевые Ask/macOS system integration
`6/6 + 11/11`, format verify и macOS Release build проходят. App/DMG candidate
`artifacts/release/wi-015-ask-prompt-visibility` собран и проверен; нативный symbol
присутствует, codesign verify проходит. Live Zoom после перезапуска опубликовал Ask,
CGWindow подтвердил on-screen floating окно `392 × 178`, пользователь нажал «Записать»,
и runtime создал активную `ask / Zoom` session.
