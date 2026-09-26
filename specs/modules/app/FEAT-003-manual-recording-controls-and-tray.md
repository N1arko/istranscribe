---
status: active
---

# FEAT-003: Manual Recording Controls And Tray {#root}

## Простыми словами {#plain-language}

Эта фича задаёт весь ручной control surface MVP: какие команды доступны из tray и global hotkeys, как стартует `Force Record`, что именно показывает экран `Current Recording`, и как работают `Pause`, `Stop`, `Discard`, `Privacy Pause` и `Quit`, когда запись уже идёт.

## Goal {#goal}

Описать implementation-ready канон ручного управления записью для MVP без двусмысленности между tray, hotkeys, current-recording surface и session lifecycle.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules` - канонический набор shell surfaces, tray commands и hotkeys
- `spec://common/PROP-003-audio-capture-and-device-observation#rules` - default sources для `Force Record`
- `spec://common/PROP-004-meeting-session-and-data-model#entities.meeting-session` - session fields и статусы
- `spec://common/PROP-004-meeting-session-and-data-model#rules` - `Force Record`, `Privacy Pause`, `Off` и lifecycle rules
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#tray-window-lifecycle` - tray-first host, close-to-tray и coordinated quit
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#source-model.request` - capture request contract
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#capability.degraded` - manual recording remains available in degraded mode

## Related {#related}

- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general.hotkeys`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices`
- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#behavior`
- `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior`

## Scope {#scope}

### In scope {#scope.in}

- availability и поведение tray commands;
- runtime semantics global hotkeys после их успешной регистрации;
- `Force Record` bootstrap, source resolution и manual session lifecycle;
- user-driven actions поверх текущей активной сессии: `Pause`, `Resume`, `Stop`, `Discard`;
- current-recording surface, включая empty state и active-state contract;
- user-visible rules для `Privacy Pause`, когда запись уже активна;
- coordinated quit behavior, когда ручные controls затрагивают активную сессию.

### Out of scope {#scope.out}

- автоматическое обнаружение встречи, ask-confirmation и anti-fragmentation logic;
- каталог настроек и UI их редактирования;
- device-switch continuity policy;
- recordings home/list UX;
- Fireworks transcription pipeline и post-recording exports.

---

## Behavior {#behavior}

Этот раздел объединяет каноническое поведение manual controls, tray и hotkeys.

## Command Model {#command-model}

### User-visible surfaces {#command-model.surfaces}

MVP manual controls существуют на трёх поверхностях:

1. `Tray menu`
2. `Global hotkeys`
3. `Current Recording` surface внутри main shell

Все три поверхности обязаны отправлять команды в один runtime coordinator manual/session controls. Реализация не должна разводить отдельные ad hoc state machines для tray, hotkeys и UI.

### Runtime states {#command-model.states}

Для ручных controls канонически важны следующие runtime states:

- `onboarding_blocked` - onboarding ещё не завершён; shell commands видимы, но recording commands не стартуют capture;
- `idle` - активной session нет;
- `awaiting_confirmation` - ask-candidate существует, но запись ещё не стала active current recording;
- `recording` - активная session пишет хотя бы один source;
- `paused` - active session существует, но capture временно остановлен pause-blocker-ами;
- `stopping` - active session уже не принимает новые commands, кроме `Open app` и `Quit`, и ждёт finalize/discard completion.

`Current Recording` surface считается active только для `recording`, `paused` и `stopping`. Состояние `awaiting_confirmation` остаётся ответственностью `FEAT-002`, а не подменяется current-recording экраном.

### Command dispatch rules {#command-model.dispatch}

- Все commands должны быть idempotent per state: повторный вызов той же команды в `stopping` или уже достигнутом target-state не должен создавать вторую session, второй finalize path или вторую discard attempt.
- Команда, которая в текущем состоянии недопустима, не должна silently mutate runtime. Пользователь должен получить понятный result через shell state и, если notifications включены, через toast.
- В MVP одновременно допускается только одна active `MeetingSession`. `Start Force Record` никогда не создаёт вторую параллельную запись поверх уже существующей active session.
- `Open app` и hotkey `Open main window` при `recording`, `paused` или `stopping` открывают shell сразу на вкладке `Current Recording`; в `idle` открывается обычный shell surface.

---

## Force Record {#force-record}

### Purpose and ownership {#force-record.purpose}

- `Force Record` создаёт `MeetingSession` с `mode = manual`.
- `Force Record` не зависит от whitelist, process detection, auto-discovery policy и ask-confirmation.
- `Force Record` использует capture plan из `Recording default sources (Force Record)` и актуального device resolution contour, а не process-specific sources.
- `process_output` не является допустимым source для `Force Record` в MVP.

### Start preconditions {#force-record.preconditions}

Команда `Start Force Record` разрешена только если одновременно выполняются все условия:

- onboarding завершён;
- runtime state = `idle`;
- `Privacy Pause` выключен;
- settings дают хотя бы один включённый source для `Force Record`;
- device/capability resolution даёт хотя бы один реально стартуемый source.

Если хотя бы одно условие не выполнено, start не создаёт session и сообщает понятную причину, например: onboarding incomplete, privacy pause active, no sources configured, no devices available.

### Source resolution {#force-record.sources}

- Канонический input для manual capture plan - `Recording.DefaultSourcesForce` из `FEAT-001`.
- Для `device_loopback` и `mic` применяются device rules из `FEAT-007`: follow system default, pinned device selection и continuity policy resolution.
- Если пользователь запросил два source-а, но в момент старта доступен только один, `Force Record` всё равно может стартовать с доступным subset, если хотя бы один source валиден.
- Если ни один source стартовать нельзя, команда fail-ится до создания active session.
- Если multi-source manual session стартовала только частично, UI и logs обязаны явно показать, какой source отсутствует или деградировал.

### Session bootstrap {#force-record.bootstrap}

При успешном `Start Force Record` система обязана:

1. Создать новую `MeetingSession`.
2. Заполнить минимум:
   - `mode = manual`
   - `status = recording`
   - `started_at`
   - `output_device_id` и/или `microphone_device_id` по фактически разрешённым источникам
   - `source_type = device` для output-only
   - `source_type = mic` для mic-only
   - `source_type = mixed` для output + mic
3. Запустить capture через platform capture request без prebuffer и без ask state.
4. Обновить tray/current-recording shell state так, чтобы запись была управляемой даже при закрытом main window.

`Force Record` timer стартует с `0` и показывает длительность уже записанного audio, а не длительность скрытого background uptime.

---

## Tray Menu {#tray}

### Baseline {#tray.baseline}

Tray menu остаётся обязательной operational surface, пока primary host жив. Минимальные команды:

- `Start Force Record`
- `Stop current recording`
- `Privacy Pause on/off`
- `Open app`
- `Quit`

Канонические label-ы допускают minor wording differences, но семантика этих пяти команд не может быть silently removed.

### Availability rules {#tray.availability}

- `Start Force Record` enabled only in `idle`, при завершённом onboarding, выключенном `Privacy Pause` и наличии startable sources.
- `Stop current recording` enabled only in `recording` или `paused`.
- `Privacy Pause on/off` enabled всегда, кроме blocking host failure.
- `Open app` enabled всегда.
- `Quit` enabled всегда, но при active session переходит в coordinated confirmation flow.

Tray icon/text обязаны отражать минимум:

- `idle`
- `recording`
- `paused`
- `privacy pause active`

Пользователь не должен открывать main window только для того, чтобы понять, идёт ли запись или включён ли privacy mode.

---

## Hotkeys {#hotkeys}

### Supported actions {#hotkeys.actions}

MVP поддерживает минимум следующие configurable global hotkeys:

- `Start/Stop Force Record`
- `Privacy Pause on/off`
- `Discard current recording`
- `Open main window`

FEAT-001 владеет вводом, сохранением и Windows registration validation. FEAT-003 владеет runtime semantics после того, как hotkey уже успешно зарегистрирован.

### Runtime semantics {#hotkeys.runtime}

- `Start/Stop Force Record`:
  - в `idle` ведёт себя как `Start Force Record`;
  - в `recording` или `paused` ведёт себя как `Stop current recording`;
  - в `awaiting_confirmation` и `stopping` не создаёт вторую ветку lifecycle и не заменяет ask flow скрытым manual start.
- `Privacy Pause on/off` зеркалит tray toggle и меняет один общий privacy state приложения.
- `Discard current recording` действует только в `recording` или `paused`; в остальных состояниях это no-op с user-visible feedback.
- `Open main window` восстанавливает или фокусирует shell; при active session открывает вкладку `Current Recording`.

Hotkeys не должны silently fail: если runtime state не допускает команду, пользователь должен получить feedback, а не просто отсутствие эффекта.

---

## Current Recording Surface {#current-recording}

### Empty state {#current-recording.empty}

Когда active session нет, вкладка `Current Recording` не исчезает из shell, а показывает empty state:

- записи сейчас нет;
- `Privacy Pause` on/off status;
- если onboarding завершён и `Privacy Pause` выключен, доступна CTA `Start Force Record`;
- если manual start сейчас недоступен, empty state объясняет причину.

### Active state {#current-recording.active}

При `recording`, `paused` или `stopping` surface обязан показывать минимум:

- session status;
- mode (`manual`, `ask`, `auto`) текущей session;
- source summary;
- timer;
- microphone indicator;
- output indicator;
- `Privacy Pause` state;
- actions `Pause`/`Resume`, `Stop`, `Discard`.

Source summary обязан быть user-readable и опираться на фактический runtime plan, например:

- source mode (`Output`, `Microphone`, `Output + Microphone`);
- selected device names;
- source app, если текущая session пришла из auto/ask flow.

### Indicator semantics {#current-recording.indicators}

- `microphone indicator` показывает минимум: not part of session / active / unavailable.
- `output indicator` показывает минимум: not part of session / active / unavailable.
- Если session идёт в degraded subset из-за отсутствующего source, surface обязан показывать это явно, а не только в логах.
- В `stopping` surface остаётся видимой до завершения finalize/discard outcome; actions кроме `Open app` и `Quit` больше недоступны.

---

## Pause, Stop And Discard {#actions}

### Pause model {#actions.pause}

- `Pause` - это ручной control поверх текущей active session независимо от того, была она запущена вручную, через `Ask` или через `Auto`.
- `Pause` не завершает session и не создаёт новую.
- Manual pause добавляет runtime blocker `manual_pause`.
- `Privacy Pause` with policy `pause` добавляет runtime blocker `privacy_pause`.
- Session считается `paused`, пока существует хотя бы один pause blocker.
- Session resumes only when все pause blockers сняты.

Это означает:

- если пользователь вручную paused session, а потом включил и выключил `Privacy Pause`, session остаётся paused, пока пользователь явно не нажмёт `Resume`;
- если единственной причиной pause был `Privacy Pause`, выключение `Privacy Pause` автоматически возвращает session в `recording`.

Пока session paused:

- timer не увеличивает recorded duration;
- automatic stop timers и signal-based auto transitions не должны silently завершать session как будто запись всё ещё actively capturing;
- `Discard` и `Stop` остаются доступны.

### Stop semantics {#actions.stop}

- `Stop` - недеструктивная остановка текущей session.
- При `Stop` session переходит в `stopping`, capture завершается, temp artifacts финализируются, и при успехе session получает `status = saved`.
- `Stop` не помечает session как user-discarded и не отключает post-recording pipeline. Дальнейшая транскрибация идёт по обычным session rules.
- `Stop` одинаково применим к manual и auto/ask sessions, если пользователь вмешался через current-recording controls.

### Discard semantics {#actions.discard}

- `Discard` всегда является destructive action и требует явного confirmation, независимо от того, вызвана ли команда с экрана или через hotkey.
- После подтверждения `Discard` немедленно прекращает текущий capture path и не enqueue-ит transcription.
- Session получает минимум:
  - `status = discarded`
  - `user_discarded = true`
  - `ended_at`
- Session-level audio artifacts этой записи удаляются из user-facing artifact contour best-effort.
- Если cleanup файлов завершился не полностью, user-visible outcome всё равно остаётся `discarded`; diagnostics/recovery contour получает structured failure, но discarded session не resurrect-ится обратно в normal recording flow.

`Discard` никогда не превращается в скрытый `Stop`. Пользовательский смысл этой команды - отбросить текущую запись, а не просто закончить её раньше.

---

## Privacy Pause And Quit {#privacy-and-quit}

### Privacy Pause interaction {#privacy-and-quit.privacy}

- `Privacy Pause` остаётся глобальным toggle, видимым из tray и current-recording surface.
- Когда `Privacy Pause` включается при `idle`, automatic recording не стартует, а manual `Start Force Record` становится недоступным, пока пользователь явно не выключит privacy mode.
- Когда `Privacy Pause` включается во время active session:
  - если configured policy = `pause`, текущая session входит в pause model из `#actions.pause`;
  - если configured policy = `finish`, текущая session проходит тот же lifecycle, что и explicit `Stop`.
- Текущий UI обязан явно показывать, что pause вызвана privacy mode, а не runtime failure.

### Quit semantics {#privacy-and-quit.quit}

- `Quit` с `idle` state завершает host обычным coordinated shutdown.
- `Quit` с active session требует explicit confirmation.
- Confirmed quit никогда не трактуется как `Discard`; он сначала выполняет semantic equivalent `Stop current recording`, затем завершает host.
- Если пользователь отменил quit confirmation, запись и host продолжают жить без side effects.

---

## Degraded Mode {#degraded}

- В degraded host/audio state manual recording остаётся доступной.
- `Force Record` в degraded mode опирается на `device_loopback` и `mic`; недоступность process loopback сама по себе не отключает tray, current-recording surface или hotkeys.
- Если force-source plan зависит от конкретного устройства, которого сейчас нет, применяются обычные startability rules из `#force-record.sources`.
- UI обязан объяснять degraded/manual fallback явно, а не делать вид, что `Force Record` работает как process-specific capture.

## Acceptance {#acceptance}

FEAT-003 достаточно полна для реализации, если одновременно выполняются все условия:

### Control surfaces {#acceptance.surfaces}
1. Tray, hotkeys и current-recording screen описаны как одна согласованная command model, а не как три независимых поведения.
2. Канонические tray commands и minimum hotkeys зафиксированы явно.
3. Недопустимые commands не приводят к silent no-op без user-visible feedback.

### Force Record {#acceptance.force-record}
4. `Force Record` starts only from `idle`, uses manual source resolution and never creates a parallel second recording.
5. `Force Record` lifecycle определён до полей `MeetingSession`, source resolution и стартовых preconditions.
6. Degraded/manual recording remains available without process loopback.

### Active session control {#acceptance.active-session}
7. `Pause`, `Resume`, `Stop` и `Discard` определены без двусмысленности для текущей active session.
8. `Pause` и `Privacy Pause` не спорят друг с другом: additive pause-blocker model и resume rules заданы явно.
9. `Stop` и `Discard` различаются канонически: `Stop` сохраняет session, `Discard` помечает её discarded и не запускает transcription.
10. `Quit` во время active recording согласован с session semantics и не превращается в неявный discard.

### Current Recording UX {#acceptance.current-recording}
11. `Current Recording` surface имеет определённые empty и active states.
12. Surface показывает status, source, timer, mic/output indicators и privacy state в минимально достаточной детализации для управления записью.
13. Active recording можно полностью довести до `Pause`/`Stop`/`Discard` даже если main window до этого был закрыт и пользователь работает через tray/hotkeys.

### Boundaries {#acceptance.boundaries}
14. FEAT-003 не переопределяет auto-detection, settings catalog, device continuity policy или transcription pipeline.
15. Границы с `FEAT-001`, `FEAT-002`, `FEAT-007`, `INFRA-001` и `INFRA-003` явные и не требуют implementation-time guesswork.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-07: Spec rewritten into implementation-ready canon: unified command model for tray/hotkeys/current-recording surface, Force Record bootstrap and source resolution, pause/privacy interaction, discard semantics, current-recording states, quit behavior and degraded-mode manual fallback.
