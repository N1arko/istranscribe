# WI-014: Сохранить запись Zoom при смене экранного контекста

- Kind: `fix`
- Canon action: `none`

## Outcome

Подтверждённая запись одной Zoom-встречи непрерывно продолжается при включении
и остановке демонстрации экрана, полноэкранном переходе и переключении macOS Space.
Новый Ask появляется только для следующей встречи после подтверждённого завершения
текущей.

## Specs

- Governing: `spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal`
- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity`
- Affected: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection`
- Constraint: `spec://modules/app/FEAT-011-meeting-detection-v2#privacy`

## Dependencies

- Blocks: `WI-012`.

## Scope

- In: active-session continuity при временной потере meeting-window evidence,
  suppression текущего candidate, Zoom presentation/Space evidence на macOS,
  automatic finish после фактического окончания встречи, privacy-safe diagnostics,
  deterministic regression tests и live Zoom recheck.
- Out: новые scoring weights, пользовательские настройки, UI/UX, Zoom API/SDK,
  остальные profile-specific selectors и полный release acceptance.

## Acceptance

- [x] `Record → screen share дольше stop delay → stop share` сохраняет одну
  активную recording session без automatic finish.
- [x] Возврат meeting-window evidence для того же candidate не создаёт повторный Ask.
- [x] Подтверждённое окончание встречи по-прежнему запускает один automatic finish
  после configured loss delay.
- [x] macOS Zoom meeting-host evidence сохраняет стабильный desktop candidate,
  когда presentation mode или переключение Space временно убирает обычное layer-0
  meeting window.
- [ ] Core lifecycle, application runtime, macOS provider и затронутые Windows
  regression tests проходят.
- [x] Диагностика не сохраняет raw window titles, audio payload или новые
  пользовательские данные.
- [ ] Live Zoom recheck подтверждает непрерывную запись во время демонстрации,
  отсутствие повторного Ask и корректный MP3 после выхода из встречи.

## Result

Заполняется при завершении WI.
