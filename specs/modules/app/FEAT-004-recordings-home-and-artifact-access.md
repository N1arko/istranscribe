---
status: active
---

# FEAT-004: Recordings Home And Artifact Access {#root}

## Простыми словами {#plain-language}

Эта фича описывает главный экран записей: пользователь видит последние встречи как `MeetingSession`, понимает их состояние, открывает локальные артефакты и вручную перезапускает транскрибацию там, где это допустимо.

## Goal {#goal}

Определить implementation-ready канон Home surface для сессий от `recording` до `transcription completed/failed`, включая правила отображения и user actions.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://common/PROP-004-meeting-session-and-data-model#entities.meeting-session`
- `spec://common/PROP-004-meeting-session-and-data-model#statuses`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine`

## Related {#related}

- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#current-recording`
- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#behavior`

## Scope {#scope}

### In scope {#scope.in}

- список сессий с deterministic ordering;
- отображение recording/transcription statuses и важных session fields;
- row-level actions: `Open Folder`, `Open Markdown`, `Retry Transcription`;
- empty/loading/error states для Home surface.

### Out of scope {#scope.out}

- редактирование транскрипта;
- full-text/semantic search;
- экспорт в внешние системы;
- multi-device sync.

---

## Behavior {#behavior}

Каноническое поведение FEAT-004 определяется разделами:

- screen model и сортировка: `#screen-model`
- row contract и статусы: `#row-contract`
- доступные действия пользователя: `#actions`
- состояния экрана: `#screen-states`
- интеграция с transcription/retry jobs: `#integration`

---

## Screen Model {#screen-model}

### Data source {#screen-model.data-source}

Home читает данные из persisted `MeetingSession` и не сканирует файловую систему как первичный источник.

### Default ordering {#screen-model.ordering}

- Default sort: `started_at DESC`.
- Secondary sort при совпадении: `id DESC`.
- MVP не требует фильтров и поиска; пользовательский сценарий покрывается последними сессиями.

### Initial page size {#screen-model.page-size}

- Home показывает последние `50` сессий.
- Кнопка `Load more` добавляет следующую страницу `+50`.
- Отсутствие следующих страниц явно отображается (disabled state).

---

## Row Contract {#row-contract}

### Required visible fields {#row-contract.fields}

Каждая строка обязана показывать:

- source app (`source_app` или fallback `Unknown app`)
- start time (`started_at`, локальный часовой пояс)
- end time (`ended_at` или `In progress`)
- duration (если `ended_at` есть; иначе live counter)
- recording status badge
- transcription status badge

### Derived formatting rules {#row-contract.formatting}

- Duration format: `H:MM:SS` при длительности >= 1h, иначе `MM:SS`.
- Start/end date format: locale-aware short date + time.
- `recording_status = failed` отображается как error badge с кратким reason tooltip из `error_code`.
- `transcription_status = retry_scheduled` отображается с подписью `Retry scheduled`.

### Status mapping {#row-contract.status-mapping}

- Recording states `prebuffering` и `awaiting_confirmation` не показываются в Home как normal rows.
- `recording`, `paused`, `stopping`, `saved`, `discarded`, `failed` показываются как полноценные записи.
- `discarded` виден только если сессия уже была materialized как persisted session; ephemeral rejected Ask candidates не должны появляться.

---

## Actions {#actions}

### Open Folder {#actions.open-folder}

- Открывает папку текущей сессии (session artifact directory), а не root `Recordings` folder.
- Если session directory отсутствует, action disabled и показывает reason `Folder not found`.

### Open Markdown {#actions.open-markdown}

- Доступно только когда `transcript_md_path` существует и файл доступен.
- Если transcript ещё не готов (`not_started|queued|uploading|processing|retry_scheduled`), кнопка disabled.
- Для `completed` при missing-file case кнопка disabled, статус остаётся `completed`, но строка показывает warning marker `artifact missing`.

### Retry Transcription {#actions.retry}

`Retry Transcription` доступна только для состояний:

- `transcription_status = failed`
- `transcription_status = completed` и пользователь явно хочет перегенерировать transcript (optional action через secondary menu)

Основной MVP path обязателен только для `failed`.

При нажатии retry:

1. Проверяется наличие хотя бы одного валидного audio artifact path.
2. Если валидного audio input нет, retry отклоняется с user-visible error.
3. Если input валиден, сессия переводится в `queued`.
4. Счётчик auto-retry attempts сбрасывается для новой ручной попытки.
5. Home обновляет row status без перезапуска приложения.

`Retry` не должен требовать ручного запуска внешних скриптов.

---

## Screen States {#screen-states}

### Loading {#screen-states.loading}

- При первом открытии Home отображает loading skeleton/list placeholder.
- Пока данные грузятся, row actions недоступны.

### Empty {#screen-states.empty}

- Если нет ни одной persisted session, Home показывает explicit empty state с текстом о том, что записи появятся после первой встречи.
- Empty state может содержать CTA `Open Settings`.

### Read error {#screen-states.error}

- Если чтение persistence не удалось, Home показывает recoverable error state и кнопку `Retry load`.
- Ошибка логируется как diagnostics event, но screen не падает.

---

## Integration Rules {#integration}

### With FEAT-003 {#integration.current-recording}

- Если есть активная текущая запись, Home может показывать pinned active row, синхронизированную с current recording surface.
- Stop/Discard из FEAT-003 должны отражаться в Home сразу после смены статуса.

### With FEAT-006 and INFRA-004 {#integration.transcription}

- Home не управляет provider API напрямую: он меняет только session state (`failed -> queued`) и делегирует обработку jobs из INFRA-004.
- Home отображает промежуточные статусы `queued`, `uploading`, `processing`, `retry_scheduled` как read-only operational states.

---

## Acceptance {#acceptance}

FEAT-004 считается завершённой, если одновременно выполнено:

1. Home показывает `MeetingSession` rows с обязательными полями и deterministic sorting.
2. Реализованы states: loading, empty, read error.
3. `Open Folder` и `Open Markdown` подчиняются explicit availability rules.
4. `Retry Transcription` переводит eligible failed session в `queued` без внешних скриптов.
5. Home корректно отображает operational transcription statuses из INFRA-004.
6. Rejected Ask candidates не засоряют Home ложными rows.
7. Поведение экрана не требует чтения сырых файлов как source of truth.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-13: Rewritten to implementation-ready canon with explicit row contract, screen states, retry action semantics and integration boundaries.
