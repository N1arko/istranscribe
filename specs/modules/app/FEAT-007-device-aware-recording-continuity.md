---
status: active
---

# FEAT-007: Device-Aware Recording Continuity {#root}

## Простыми словами {#plain-language}

Эта фича фиксирует, как приложение переживает смену output/microphone устройств во время встречи: когда бесшовно переключается, когда завершает текущую сессию и создаёт новую, а когда запрашивает решение пользователя.

## Goal {#goal}

Описать implementation-ready continuity модель для device changes без потери уже записанного аудио и без неявных переходов состояний.

## Depends on {#depends-on}

- `spec://common/PROP-003-audio-capture-and-device-observation#rules`
- `spec://common/PROP-004-meeting-session-and-data-model#rules`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#device-manager.behavior`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#diagnostics.events`

## Related {#related}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#behavior`
- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.merge`

## Scope {#scope}

### In scope {#scope.in}

- output/mic selection modes: fixed vs follow-default;
- canonical policies for device change during active recording;
- switching behavior для `auto`, `ask`, `manual` active sessions;
- transient-loss handling и recovery windows;
- diagnostic logging contract для device decisions.

### Out of scope {#scope.out}

- whitelist and app-discovery policy;
- tray command UX;
- transcription queue/retry behavior.

---

## Behavior {#behavior}

Каноническое поведение FEAT-007 определяется разделами:

- модель выбора устройств: `#selection-model`
- policy set и матрица решений: `#policy-set`, `#change-matrix`
- transient loss и continuity outcomes: `#transient-loss`, `#continuity`
- режимные границы и диагностика: `#mode-interaction`, `#diagnostics`

---

## Device Selection Model {#selection-model}

### User-facing modes {#selection-model.modes}

Для output и microphone применима общая модель:

- `follow_default = true`: приложение следует system default device.
- `follow_default = false`: приложение пытается использовать pinned device id.

`automatic mode` из onboarding (FEAT-001) означает предпочтение `follow_default` и не фиксирует навсегда устройства, выбранные на шаге wizard.

### Device inventory updates {#selection-model.inventory}

- Device list обновляется динамически без restart.
- Возврат ранее известного device id должен восстанавливать его идентичность в модели.

---

## Policy Set {#policy-set}

Канонические policy values:

- `seamless_switch`
- `end_and_start_new`
- `ask`

Политика задаётся отдельно для output и mic, но при одновременной потере обоих применяется более строгий результат:

- если хотя бы один policy = `end_and_start_new`, итог `end_and_start_new`;
- иначе если хотя бы один policy = `ask`, итог `ask`;
- иначе `seamless_switch`.

---

## Device Change Matrix {#change-matrix}

### Event types {#change-matrix.events}

- `DEVICE_DISCONNECTED`
- `DEFAULT_DEVICE_CHANGED`
- `BETTER_MATCH_CONNECTED` (новое устройство появилось, но старое ещё доступно)

### Decision matrix {#change-matrix.decisions}

1. If policy = `seamless_switch`:
	- попытаться переключиться на наиболее подходящее доступное устройство той же роли;
	- если `follow_default = true`, first candidate всегда current system default;
	- если `follow_default = false`, first candidate pinned device id, затем fallback to default.

2. If policy = `end_and_start_new`:
	- завершить текущую сессию с явной device-boundary причиной;
	- при сохранении meeting eligibility стартовать новую session автоматически по текущему mode.

3. If policy = `ask`:
	- показать runtime prompt с вариантами `Switch now`, `Finish and start new`, `Keep current if possible`;
	- timeout prompt = `Switch now` для `follow_default = true`, иначе `Finish and start new`.

### Prompt timeout profile {#change-matrix.prompt-timeouts}

- Prompt visibility timeout: `8s` from first render.
- If user does not answer in time:
  - with `follow_default = true` -> auto action `Switch now`.
  - with `follow_default = false` -> auto action `Finish and start new`.
- If `Keep current if possible` selected but current device is already unavailable, fallback becomes `Switch now` when any compatible device exists, otherwise `Finish and start new`.
- Prompt is cancelled immediately (without second prompt) if recording already ended by user action during timeout window.

---

## Transient Loss Handling {#transient-loss}

### Grace window {#transient-loss.window}

- Кратковременная потеря устройства (до `5s`) трактуется как transient glitch.
- В grace window запись не должна аварийно завершаться; система продолжает попытки rebind.

### After grace window {#transient-loss.after-window}

Если за `5s` rebind не удался, применяется policy из `#policy-set`.

---

## Session Continuity Semantics {#continuity}

### Seamless switch outcome {#continuity.seamless}

- Текущая `MeetingSession` сохраняет identity (`session_id` не меняется).
- В session metadata фиксируется новый device id и event о переключении.

### End-and-start-new outcome {#continuity.end-start}

- Текущая сессия завершается как normal boundary (не merge-resume boundary).
- Новая сессия стартует только если meeting eligibility всё ещё выполняется.
- Эти две сессии не merge-ятся в FEAT-002 merge window.

### Ask outcome {#continuity.ask}

- Пользовательский выбор применяется немедленно.
- До выбора пользователя запись сохраняет последнее рабочее состояние в пределах grace window.

---

## Interaction With Modes {#mode-interaction}

### Auto and Ask recordings {#mode-interaction.auto-ask}

- Для auto/ask сессий continuity decisions не должны создавать параллельную вторую запись.
- Любой restart по device policy проходит через явную границу старой сессии.

### Manual Force Record {#mode-interaction.manual}

- Для manual сессий применяются те же policy values.
- При `end_and_start_new` пользовательская запись не должна silently discard-иться; граница должна быть видима в статусе и логах.

### Cancellation on stop {#prompt-cancel-on-stop}

Открытый prompt выбора устройства отменяется, когда пользователь останавливает или discard-ит текущую запись. После отмены prompt не может изменить состояние завершённой сессии.

---

## Diagnostics Contract {#diagnostics}

Каждое device decision обязано логировать минимум:

- `session_id`
- `event_type`
- `device_role` (`output|mic`)
- `old_device_id`
- `new_device_id` (nullable)
- `policy_applied`
- `decision_result` (`switched|ended_and_restarted|prompted|no_available_device`)

Ошибки переключения не должны оставаться немыми: required log level минимум `Warning`.

---

## Acceptance {#acceptance}

FEAT-007 считается завершённой, если одновременно выполнено:

1. Описана единая модель fixed/follow-default выбора устройств.
2. Зафиксированы policy values и deterministic правила комбинирования output+mic решений.
3. Есть explicit matrix для ключевых device events.
4. Определён transient-loss grace window и поведение после него.
5. `end_and_start_new` явно разрывает merge eligibility с предыдущей сессией.
6. Любое device-switch decision диагностируемо через обязательный logging contract.
7. Спека согласована с onboarding automatic mode из FEAT-001.
8. Ask-policy prompt имеет фиксированный timeout и deterministic fallback actions.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-06: Clarified onboarding-facing automatic device mode so first-run UX does not imply permanent device pinning.
- 2026-04-13: Rewritten to implementation-ready canon with device-change matrix, transient-loss handling and continuity outcomes.
- 2026-04-13: Added explicit ask-policy prompt timeout and fallback matrix.
