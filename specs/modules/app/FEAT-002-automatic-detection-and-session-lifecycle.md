---
status: superseded
---

# FEAT-002: Automatic Detection And Session Lifecycle {#root}

## Простыми словами {#plain-language}

Эта фича задаёт главный автоматический сценарий продукта: как приложение замечает вероятную встречу, когда показывает confirmation prompt, в какой момент создаёт и продолжает `MeetingSession`, когда автоматически заканчивает её, как работает `Privacy Pause`, и как не дробить одну встречу на случайные куски.

## Goal {#goal}

Описать implementation-ready канон automatic detection, `Ask` confirmation, `Auto` start, automatic stop and merge semantics для MVP без дублирующихся state machines и без implementation-time guesswork.

## Superseded by {#superseded-by}

- Release v2 automatic lifecycle is governed by `spec://modules/app/FEAT-011-meeting-detection-v2#root` and uses Ask-only behavior.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules` - shell surfaces, ask UX, settings catalog и timing settings
- `spec://common/PROP-003-audio-capture-and-device-observation#rules` - detection signals, source defaults, prebuffer and auto-discovery baseline
- `spec://common/PROP-004-meeting-session-and-data-model#entities.meeting-session` - canonical session fields
- `spec://common/PROP-004-meeting-session-and-data-model#rules` - one-session model, stop delay, merge window and privacy semantics
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.rules` - degraded capability gating
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.snapshots` - observation snapshots for process/session detection
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#source-model.request` - capture request contract
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#prebuffer.promotion` - Ask prebuffer promotion/discard behavior
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#capability.degraded` - degraded fallback from process capture to device flows

## Related {#related}

- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#command-model.states`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.pause`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#privacy-and-quit.privacy`
- `spec://modules/app/FEAT-005-application-rules-and-discovery#behavior`
- `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior`

## Scope {#scope}

### In scope {#scope.in}

- automatic meeting-candidate detection for `Off`, `Ask`, `Auto` and `Privacy Pause`;
- runtime coordinator model for pending confirmation, active auto/ask sessions and merge-pending sessions;
- source-plan resolution for automatic recording;
- known-app Ask confirmation and unknown-app combined runtime prompt;
- session bootstrap, automatic stop, merge window, suppression and cooldown rules;
- boundaries with manual controls, app-discovery policy and device continuity.

### Out of scope {#scope.out}

- manual tray commands, global hotkeys and current-recording control semantics;
- editing whitelist and exclusions UI;
- recordings home/list UX;
- Fireworks request building, queueing and Markdown rendering internals.

---

## Behavior {#behavior}

Этот historical MVP behavior сохранён для legacy code ownership; release-v2 detection принадлежит FEAT-011.

## Coordinator Model {#coordinator-model}

### Ownership {#coordinator-model.ownership}

`FEAT-002` owns one product-level runtime coordinator for automatic capture lifecycle. Этот coordinator:

- consumes observation snapshots from `INFRA-003`;
- evaluates current recording mode, app rules, discovery policy and privacy state;
- owns at most one pending confirmation candidate;
- owns at most one active auto/ask `MeetingSession`;
- decides automatic start, automatic stop and merge-window resume;
- hands active-session controls to the same current-session control path that `FEAT-003` uses for `Pause`, `Resume`, `Stop`, `Discard` and `Privacy Pause`.

Реализация не должна собирать отдельную ad hoc state machine только для auto flows и ещё одну независимую state machine только для current-recording controls. После старта session у продукта остаётся один current-session contour.

### Runtime objects {#coordinator-model.runtime-objects}

Coordinator различает три runtime объекта:

1. `MeetingCandidate` - ephemeral detection object до старта записи. Он хранит root process identity, source app label, resolved capture plan, prompt state, countdown и suppression state. `MeetingCandidate` не создаёт user-facing artifacts и не должен оставлять запись в Home.
2. `ActiveSession` - уже начатая auto/ask `MeetingSession`, которой можно управлять через `FEAT-003` controls.
3. `MergePendingSession` - auto/ask session, для которой live capture уже остановлен по автоматической причине, но окончательная finalization ещё отложена на merge window.

В каждый момент времени допускается максимум один объект из набора `MeetingCandidate | ActiveSession | MergePendingSession`. Параллельные automatic recordings в MVP запрещены.

### Startup and idle gating {#coordinator-model.gating}

- Пока onboarding не завершён, automatic detection, Ask prompts и unknown-app runtime prompts не запускаются.
- `Off` полностью отключает automatic start и Ask/unknown-app prompts. Manual `Force Record` остаётся соседней функцией `FEAT-003`.
- Пока активна любая manual, ask или auto session, новый независимый automatic candidate не создаётся. Runtime может логировать чужую активность, но не начинает вторую session и не показывает второй prompt.
- `Privacy Pause` блокирует появление новых automatic candidates независимо от текущего recording mode.

---

## Detection And Eligibility {#detection}

### Canonical detection signal {#detection.signal}

Automatic detection использует только канонический signal layer проекта:

- observed root process exists;
- matching audio session is associated with that process or its process tree;
- audio session state is effectively `Active`;
- signal level stays above configured silence threshold for configured start delay.

Browser scenarios матчатся по whitelisted root process name и его process tree, а не по буквальному совпадению child PID с whitelist entry.

### Known-app eligibility {#detection.known-app}

Known-app candidate может перейти в `Ask` или `Auto` только если одновременно выполняются условия:

- есть enabled whitelist rule для `process_name`;
- process/session observation удовлетворяет `#detection.signal`;
- mode != `off`;
- `Privacy Pause` выключен;
- resolved automatic capture plan даёт хотя бы один startable source.

Default timing values:

- start delay: `2 секунды`;
- ask timeout: `8 секунд`;
- stop delay after loss of eligibility: `20 секунд`;
- merge window: `60 секунд`.

### Unknown-app eligibility {#detection.unknown-app}

Unknown-app runtime candidate допускается только если одновременно верно:

- meeting candidate не совпал с enabled whitelist rule;
- process не находится в technical exclusions и не находится в ignored-apps denylist;
- auto-discovery policy != `off`;
- current recording mode = `ask` или `auto`;
- detection conditions из `#detection.signal` выполнены;
- resolved automatic capture plan даёт хотя бы один startable source.

`FEAT-005` владеет тем, какие процессы считаются discovery-candidates и как пользователь редактирует whitelist/ignored state. `FEAT-002` владеет тем, что происходит с уже найденным candidate в рамках текущей встречи.

### Automatic source resolution {#detection.sources}

Automatic capture plan строится из `Recording.DefaultSourcesAuto` и device rules из `FEAT-007`.

Правила:

- В одной automatic session может быть не больше одного output source.
- Если в настройках включены и `process_output`, и `device_loopback`, coordinator never requests both together. Канонический приоритет такой:
  - `process_output` используется первым, если платформа и текущий candidate его поддерживают;
  - `device_loopback` используется как fallback output source, если process loopback недоступен в текущем capability contour или не может быть честно разрешён для этого candidate.
- `mic` может добавляться поверх output source, если microphone source разрешён и startable.
- Если пользователь запросил несколько automatic sources, а стартуем только subset, session всё равно может начаться с доступным subset, если доступен хотя бы один source. UI и logs обязаны отразить частичную деградацию.
- Если не стартует ни один source, candidate не создаёт prompt и не создаёт session.

### Candidate suppression and reset {#detection.suppression}

Чтобы один и тот же meeting candidate не превращался в prompt loop:

- `Ask -> No` или ask timeout suppress-ят этот candidate до тех пор, пока он не перестанет удовлетворять detection conditions и не выйдет из текущей meeting window.
- `Start once` suppress-ит повторные prompts для текущей встречи, но не пишет app в denylist.
- `Ignore this app` suppress-ит текущую встречу и добавляет process name в ignored-app denylist для будущих встреч.
- Candidate считается завершившимся, когда root process исчез или matching session перестала быть eligible дольше configured stop delay. Только после этого тот же process может считаться новым meeting candidate.

---

## Confirmation Surfaces {#confirmation}

### Ask confirmation for known apps {#confirmation.ask}

Для known app в режиме `ask` product показывает одно confirmation surface:

- заметный, но ненавязчивый toast/dialog поверх других окон;
- headline уровня `Слышу встречу — записывать?`;
- source app label;
- countdown до timeout;
- минимум две action-кнопки: `Start recording` и `No`.

Пока prompt открыт:

- prebuffer ведётся только в памяти;
- `Current Recording` surface не считается active;
- hotkey/tray semantics из `FEAT-003` не подменяют этот prompt скрытым manual start.

Если пользователь выбирает `Start recording`, prebuffer flush-ится в начало capture и candidate превращается в active `MeetingSession`. Если пользователь выбирает `No` или таймаут истекает, prebuffer discard-ится, session не создаётся и candidate переходит в suppression state.

### Combined prompt for unknown apps {#confirmation.unknown-app}

Если meeting activity пришла от unknown app и policy = `ask_to_add`, product обязан показать один combined runtime prompt вместо двух последовательных prompt-ов.

Канонические действия:

- `Add and start` - app добавляется в whitelist и текущая встреча сразу стартует на запись.
- `Start once` - текущая встреча стартует на запись, но app остаётся unknown.
- `Not now` - текущая встреча не стартует, но app не попадает ни в whitelist, ни в denylist.
- `Ignore this app` - текущая встреча не стартует, а app попадает в ignored-app denylist.

Дополнительные rules:

- В режиме `auto` combined prompt временно заменяет fully-automatic start для этого unknown candidate. Запись не начинается, пока пользователь не выберет `Add and start` или `Start once`.
- В режиме `ask` combined prompt также завершает и whitelist-решение, и решение по текущей встрече. После `Add and start` или `Start once` второй ask-prompt для того же candidate показывать нельзя.
- Timeout combined prompt по умолчанию трактуется как `Not now`.
- `Ignore this app` является durable user decision до тех пор, пока пользователь не удалит app из ignored list в Settings.

### Auto-add policy interaction {#confirmation.auto-add}

Если unknown app попал под policy = `auto_add`:

- правило whitelist создаётся автоматически;
- после этого candidate ведёт себя как known app;
- в режиме `auto` recording starts immediately;
- в режиме `ask` показывается обычный known-app Ask prompt.

Если policy = `off`, unknown app не стартует automatic recording и не показывает runtime prompt.

### Prompt cancellation {#confirmation.cancellation}

Pending prompt обязан отменяться немедленно, если:

- `Privacy Pause` включился до ответа пользователя;
- recording mode переключён в `off`;
- onboarding state снова стал blocking;
- detection candidate потерял eligibility до подтверждения.

Во всех этих случаях prebuffer discard-ится и prompt не resurrect-ится автоматически. Если условия позже снова стали valid, detection начинает новый candidate с нового start-delay окна.

---

## Session Bootstrap {#session-bootstrap}

### When a session is created {#session-bootstrap.when}

`MeetingSession` создаётся только в момент, когда продукт уже обязался писать встречу:

- для `auto` - сразу после успешного automatic start;
- для `ask` - после explicit user confirmation или combined unknown-app action, которая запускает текущую встречу.

Rejected or timed-out Ask candidates не должны оставлять normal persisted session rows и не должны засорять recordings home.

### Bootstrap fields {#session-bootstrap.fields}

При старте auto/ask session coordinator обязан заполнить минимум:

- `mode = auto` или `mode = ask` по фактическому recording mode, а не по типу prompt-а;
- `status = recording`;
- `started_at`;
- `source_app` - user-readable app label текущего candidate;
- `source_process_id` - current root PID, если session привязана к process candidate;
- `output_device_id` и/или `microphone_device_id` по фактически разрешённым sources;
- `source_type = process` для process-output-only;
- `source_type = device` для device-loopback-only;
- `source_type = mic` для mic-only;
- `source_type = mixed` для multi-source session.

Дополнительно session обязана наследовать transcription-relevant settings, которые уже действуют для новых recordings: model, diarization flag, language.

### Capture start semantics {#session-bootstrap.capture}

- `Ask` starts recorder in buffered contour and promotes prebuffer only after confirmation.
- `Auto` starts recorder without confirmation gate.
- Once session started, она становится обычной current session и полностью подчиняется active-session controls из `FEAT-003`.

Если session resumed within merge window, создаётся не новая `MeetingSession`, а продолжение прежней session. Новый capture leg допускается только внутри того же session ownership contour.

---

## Active Session Lifecycle {#lifecycle}

### Active-session ownership {#lifecycle.ownership}

После старта auto/ask session:

- `FEAT-002` продолжает владеть automatic stop, merge-window resume и detection linkage с source app;
- `FEAT-003` владеет user-driven controls поверх этой уже активной session.

Нельзя разводить auto session и manual current-recording control как две независимые active записи.

### Pause and privacy interaction {#lifecycle.pause-and-privacy}

`Pause`, `Resume`, `Stop`, `Discard` и `Privacy Pause` after session start obey `FEAT-003`.

Для `FEAT-002` канонически важно следующее:

- manual pause и privacy-pause-with-policy=`pause` блокируют automatic stop countdown;
- пока session paused хотя бы одним pause blocker-ом, signal loss не должен silently завершать session как будто capture всё ещё actively recording;
- `Privacy Pause` with policy = `finish` создаёт explicit finish boundary, а не merge-eligible automatic stop.

### Automatic loss of eligibility {#lifecycle.loss-of-eligibility}

Для active auto/ask session coordinator continuously checks whether the original meeting source is still eligible:

- root process still exists or has just transiently restarted;
- matching audio session is still present;
- signal remains above threshold often enough to keep the meeting alive.

Если eligibility теряется, coordinator запускает automatic stop delay countdown. Пока countdown не истёк:

- session остаётся текущей active session;
- current-recording controls продолжают работать;
- возвращение eligibility отменяет countdown и session продолжается без split.

### Automatic stop and merge window {#lifecycle.merge}

Если automatic stop delay истёк и loss of eligibility не восстановился:

- live capture для этой session останавливается;
- если `merge_window_seconds > 0` и stop cause был automatic, session входит в `MergePendingSession` вместо немедленной finalization;
- `MergePendingSession` остаётся текущей session в paused-like contour с automatic blocker `merge_window`;
- user-driven `Resume` не может снять blocker `merge_window`; его снимает только возврат eligibility того же meeting source;
- `Stop` и `Discard` остаются доступны во время merge-pending state;
- during merge-pending state session ещё не считается окончательно saved for transcription pipeline;
- если тот же logical meeting source вернулся в течение merge window, coordinator возобновляет ту же `MeetingSession`, а не создаёт новую.

Canonical merge key:

- same normalized source app/process identity;
- same product mode (`ask` or `auto`);
- same source family resolved for the meeting, unless `FEAT-007` explicitly forces a new-session boundary because device continuity policy says `end_and_start_new`.

`merge_window_seconds = 0` отключает эту anti-fragmentation wave: automatic stop finalizes session immediately after stop delay.

### What breaks merge eligibility {#lifecycle.merge-boundaries}

Merge window never applies if current session ended because of an explicit user boundary:

- user pressed `Stop`;
- user confirmed `Discard`;
- `Privacy Pause` policy = `finish`;
- host quit path executed semantic equivalent of explicit `Stop`;
- device continuity policy from `FEAT-007` explicitly demanded `end_and_start_new`.

После такого boundary возвращение той же app activity создаёт новую session, а не resurrect-ит предыдущую.

### Finalization timing {#lifecycle.finalization}

Session becomes final and eligible for post-recording pipeline only when one of the following is true:

- explicit user boundary finished it;
- automatic stop happened and merge window expired without resume;
- capture fault made continuation impossible and no merge-eligible resume remained.

Only after that point user-facing artifacts are promoted to final storage contour and transcription may be enqueued.

---

## Boundaries With Neighbor Specs {#boundaries}

### FEAT-003 boundary {#boundaries.feat-003}

`FEAT-003` owns:

- tray commands;
- hotkeys;
- `Current Recording` screen;
- `Pause` / `Resume` / `Stop` / `Discard` semantics for an already active session.

`FEAT-002` owns:

- pending Ask / unknown-app confirmation state;
- automatic start;
- automatic stop;
- merge-window anti-fragmentation;
- prompt cancellation and candidate suppression.

`awaiting_confirmation` must remain a distinct FEAT-002 surface and must not masquerade as an active `Current Recording` screen.

### FEAT-005 boundary {#boundaries.feat-005}

`FEAT-005` owns:

- whitelist editing;
- installed-app suggestions;
- technical exclusions;
- persistence of ignored-app denylist and app rules.

`FEAT-002` owns only the runtime decision for the current unknown meeting candidate: prompt shape, one-shot actions and how those actions affect the current meeting start.

### FEAT-007 boundary {#boundaries.feat-007}

`FEAT-007` owns device selection and continuity policy when actual devices change. `FEAT-002` assumes device plan resolution is already available and only decides whether the meeting should start, continue, merge or end.

If device continuity policy says `end_and_start_new`, that explicit device policy beats merge-window reuse.

### INFRA-003 boundary {#boundaries.infra-003}

`INFRA-003` owns:

- process/session/device observation;
- prebuffer storage and promotion mechanics;
- recorder engine start/stop/pause/resume primitives.

`FEAT-002` consumes those primitives and decides when they should be used. Foundation must not silently decide Ask timeout, unknown-app prompt shape or merge-window rules by itself.

## Acceptance {#acceptance}

FEAT-002 is implementation-ready only if all of the following are true:

### Coordinator integrity {#acceptance.coordinator}
1. Auto/Ask lifecycle is described as one coherent runtime coordinator, not as disconnected watchers, prompts and current-recording hacks.
2. The spec guarantees at most one pending candidate or active session at a time.
3. Boundaries with `FEAT-003`, `FEAT-005`, `FEAT-007` and `INFRA-003` are explicit enough to avoid ownership ambiguity.

### Detection and prompts {#acceptance.detection}
4. Known-app and unknown-app eligibility rules are explicit and consistent with whitelist, ignored-apps, capability gating and privacy mode.
5. Ask prompt, combined unknown-app prompt and auto-add interaction do not degrade into double-prompt behavior for the same meeting candidate.
6. Candidate suppression/reset rules prevent infinite prompt loops on the same meeting.
7. Automatic source resolution explains what happens when `process_output`, `device_loopback` and `mic` selections overlap or partially fail.

### Session lifecycle {#acceptance.lifecycle}
8. The moment when a `MeetingSession` is created is defined without ambiguity.
9. Automatic stop, prompt cancellation, pause/privacy interaction and finalization timing do not contradict each other.
10. Merge window behavior is explicit enough to prevent accidental meeting fragmentation and explicit enough to know when merge must not happen.
11. Explicit user boundaries (`Stop`, `Discard`, privacy-finish, quit-stop) are clearly separated from merge-eligible automatic stops.

### Defaults and scope discipline {#acceptance.defaults}
12. Start delay, ask timeout, stop delay and merge window defaults are canonically fixed.
13. Rejected Ask candidates do not silently create normal recordings or user-facing artifacts.
14. FEAT-002 does not silently swallow manual controls, whitelist editing, device continuity policy or transcription formatting that belong to neighboring specs.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-06: Added combined unknown-app runtime prompt so whitelist suggestion and recording decision are resolved in one surface rather than two serial dialogs.
- 2026-04-07: Spec rewritten into implementation-ready canon: unified automatic/session coordinator, source-plan resolution, known-app Ask and unknown-app runtime prompts, session bootstrap rules, automatic stop/merge semantics, candidate suppression and explicit boundaries with FEAT-003, FEAT-005 and FEAT-007.
- 2026-07-11: Linked the multi-signal Ask-only release v2 replacement.
