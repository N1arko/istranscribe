---
status: active
---

# INFRA-004: Diagnostics Jobs And Recovery {#root}

## Простыми словами {#plain-language}

Эта спека фиксирует, как приложение остаётся предсказуемым в долгой фоновой работе: какие события логируются, какие фоновые джобы запускаются, как классифицируются ошибки, как выполняется автоматический retry, и что происходит с незавершёнными сессиями после crash/kill.

## Goal {#goal}

Сделать operational поведение desktop app детерминированным при ошибках provider/capture/device и после нештатного завершения процесса.

## Depends on {#depends-on}

- `spec://common/PROP-004-meeting-session-and-data-model#statuses`
- `spec://common/PROP-004-meeting-session-and-data-model#pipeline`
- `spec://common/PROP-005-local-runtime-and-operations#rules`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`

## Related {#related}

- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#behavior`
- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#retry-lifecycle`

## Scope {#scope}

### In scope {#scope.in}

- формат локальных operational logs и обязательный event catalog;
- fault taxonomy для capture/provider/recovery/classification;
- background jobs: transcription worker, retry scheduler, startup recovery worker, cleanup worker;
- canonical transitions для `recording_status` и `transcription_status` при recovery/retry;
- bounded cleanup policies для temp/stale artifacts.

### Out of scope {#scope.out}

- визуальный дизайн diagnostics screen;
- audio capture implementation details (owned by INFRA-003);
- Markdown rendering template (owned by FEAT-006).

---

## Decisions {#decisions}

- Diagnostics, retry scheduling, startup recovery и cleanup формализованы как отдельные runtime jobs.
- Retry eligibility определяется fault taxonomy (`transient|recoverable|configuration|terminal`) и не зависит от UI.
- Recovery обязан нормализовать dangling recording/transcription states на старте.
- Cleanup удаляет только temp/orphaned artifacts и не трогает referenced final recordings.
- Все ключевые operational переходы должны быть диагностируемы через обязательный event catalog.

---

## Operational Model {#operational-model}

### Runtime jobs {#operational-model.jobs}

Платформа обязана поддерживать четыре независимых фоновых jobs:

1. `TranscriptionDispatchJob` - берёт `queued` сессии и переводит их в provider pipeline.
2. `RetrySchedulerJob` - переводит eligible `failed` сессии в `retry_scheduled` и обратно в `queued` по расписанию.
3. `StartupRecoveryJob` - на старте разбирает незавершённые временные сессии и закрывает dangling states.
4. `CleanupJob` - удаляет устаревшие temp artifacts и orphaned transient files по retention policy.

Ограничения:

- Один job type исполняется в одном экземпляре на процесс.
- `TranscriptionDispatchJob` и `RetrySchedulerJob` могут работать параллельно, но не должны одновременно обрабатывать одну и ту же `MeetingSession`.
- Job orchestration не должен зависеть от того, открыт ли main window: tray-only runtime обязан выполнять те же фоновые jobs.

### Concurrency model {#operational-model.concurrency}

- `TranscriptionDispatchJob` работает с `max_concurrency = 1` для MVP.
- `RetrySchedulerJob`, `StartupRecoveryJob` и `CleanupJob` работают serially.
- Claiming session для job выполняется optimistic-lock/compare-and-set обновлением статуса; если claim неуспешен, job пропускает запись.

### Scheduling model {#operational-model.schedule}

- `TranscriptionDispatchJob`: wake-up каждые `10s`, плюс event-trigger при появлении новых `queued` sessions.
- `RetrySchedulerJob`: wake-up каждые `30s`.
- `StartupRecoveryJob`: однократно после successful bootstrap persistence.
- `CleanupJob`: при старте и далее раз в `6h`.

---

## Diagnostics And Logging {#diagnostics}

### Log sinks and format {#diagnostics.format}

- Логи пишутся в локальную папку `AppData/logs`.
- Формат записи: newline-delimited JSON (одна запись на строку).
- Каждая запись содержит минимум:
	- `timestamp_utc`
	- `level` (`Debug | Info | Warning | Error`)
	- `event_code`
	- `session_id` (nullable)
	- `job_name` (nullable)
	- `message`
	- `metadata` (object, redacted)

### Rotation and retention {#diagnostics.rotation}

- Active log file rotates at `10 MB`.
- Maximum retained rotated files: `20`.
- Maximum local diagnostics footprint budget: `200 MB`; oldest rotated files are removed first.
- Default retention window: `14d` for log files even if size limits are not reached.
- `Error`-level records are not exempt from rotation rules; durability is provided by bounded retention, not infinite growth.

### Secret-safe logging {#diagnostics.redaction}

- API key, bearer tokens и raw authorization headers никогда не логируются.
- Пути файлов можно логировать только в пределах app-owned directories.
- Provider payload может логироваться только в виде безопасного summary (размер файла, model, language, diarization flag, HTTP status).

### Mandatory event catalog {#diagnostics.events}

Минимальные обязательные события:

- lifecycle: `APP_START`, `APP_STOP`, `HOST_DEGRADED_CAPABILITY`
- recording: `RECORDING_STARTED`, `RECORDING_PAUSED`, `RECORDING_RESUMED`, `RECORDING_STOPPED`, `RECORDING_FAILED`
- transcription: `TX_QUEUED`, `TX_UPLOADING`, `TX_PROCESSING`, `TX_COMPLETED`, `TX_FAILED`, `TX_RETRY_SCHEDULED`, `TX_RETRY_EXHAUSTED`
- recovery: `RECOVERY_SCAN_STARTED`, `RECOVERY_ITEM_HANDLED`, `RECOVERY_ITEM_FAILED`, `RECOVERY_SCAN_COMPLETED`
- cleanup: `CLEANUP_STARTED`, `CLEANUP_DELETED`, `CLEANUP_SKIPPED`, `CLEANUP_COMPLETED`
- device/capture errors: `CAPTURE_DEVICE_LOST`, `CAPTURE_INIT_FAILED`, `CAPTURE_WRITE_FAILED`

---

## Fault Taxonomy {#fault-taxonomy}

### Categories {#fault-taxonomy.categories}

Ошибки делятся на четыре operational категории:

- `transient`: сеть недоступна, timeout, временный provider 5xx.
- `recoverable`: локальные проблемы, которые можно исправить без изменения user intent (временный lock файла, короткая недоступность устройства).
- `configuration`: invalid API key, unsupported model, invalid path/config.
- `terminal`: повреждённый input artifact, отсутствует обязательный audio file, невалидный session state.

### Retry eligibility rules {#fault-taxonomy.retry}

- `transient` и `recoverable` ошибки допускают auto-retry.
- `configuration` не запускает auto-retry до явного изменения соответствующей настройки пользователем.
- `terminal` не запускает auto-retry; сессия остаётся `failed`.
- Любая retry-eligible ошибка обязана выставлять machine-readable `error_code` в `MeetingSession`.

---

## Status Transition Contracts {#transitions}

### Recording status recovery {#transitions.recording}

На старте `StartupRecoveryJob` применяет следующие правила:

- `recording` или `stopping` из прошлой сессии процесса -> `failed` с `error_code = RECOVERY_UNCLEAN_SHUTDOWN`.
- `paused` из прошлой сессии процесса -> `failed` с тем же error code.
- `prebuffering` или `awaiting_confirmation` -> `discarded` (как неподтверждённый ephemeral candidate).

При этом уже сохранённые финальные audio artifacts не удаляются.

### Transcription status recovery {#transitions.transcription}

На старте `StartupRecoveryJob` нормализует dangling transcription states:

- `uploading` или `processing` без active worker claim -> `queued`.
- `retry_scheduled` с просроченным `next_retry_at` -> `queued`.
- `queued` остаётся `queued`.
- `completed` и `failed` не изменяются.

### Retry lifecycle state machine {#transitions.retry-state-machine}

Канонический flow:

1. `queued` -> `uploading` (worker claim)
2. `uploading` -> `processing` (provider accepted)
3. `processing` -> `completed` (artifacts materialized)
4. `uploading|processing` -> `failed` (error)
5. `failed` -> `retry_scheduled` (если error retry-eligible и попытки не исчерпаны)
6. `retry_scheduled` -> `queued` (когда `next_retry_at <= now`)
7. `failed` остаётся terminal, если retry исчерпан или error non-retryable

---

## Startup Recovery {#startup-recovery}

### Recovery scan algorithm {#startup-recovery.algorithm}

`StartupRecoveryJob` выполняет шаги в фиксированном порядке:

1. Загружает сессии в potentially inconsistent states.
2. Применяет transition rules из `#transitions.recording` и `#transitions.transcription`.
3. Валидирует ссылки на файлы артефактов.
4. Помечает missing-required-artifact cases как `failed` с `error_code = RECOVERY_ARTIFACT_MISSING`.
5. Пишет recovery summary event в лог.

### Recovery idempotency {#startup-recovery.idempotency}

- Повторный запуск recovery на уже восстановленной базе не должен менять статусы повторно.
- Recovery changes должны быть atomic per session: либо все поля сессии обновлены, либо ни одно.

---

## Cleanup Policies {#cleanup}

### Targets {#cleanup.targets}

`CleanupJob` обрабатывает только:

- временные файлы в `AppData/temp`;
- orphaned transient artifacts без валидной ссылки из `MeetingSession`;
- stale retry scratch files.

Нельзя удалять:

- финальные recordings/transcripts, привязанные к любой session;
- `failed` session artifacts, если они still referenced.

### Retention defaults {#cleanup.retention}

- Default retention для temp artifacts: `7d`.
- Минимальный retention: `1d`.
- `never` допускается для troubleshooting mode и должен отключать only time-based deletion, но не orphan cleanup.

### Cleanup safety {#cleanup.safety}

- Перед удалением файла cleanup обязан проверить, что файл не захвачен активной записью/job.
- Ошибки удаления логируются как `Warning` и не прерывают весь проход cleanup.

---

## Performance And Reliability Bar {#quality-bar}

- Idle CPU в tray-only режиме должен оставаться low and stable.
- Jobs не должны создавать unbounded memory growth.
- Retry/recovery activity не должна приводить к потере уже сохранённых аудиоартефактов.
- Любая terminal ошибка должна быть диагностируема по комбинации session status + log events.

## Acceptance {#acceptance}

INFRA-004 считается завершённой, если одновременно выполнено:

1. Определён фиксированный набор runtime jobs и их schedule.
2. Зафиксирован fault taxonomy и правило retry eligibility.
3. Канонические переходы `recording_status` и `transcription_status` при recovery описаны явно.
4. Startup recovery алгоритм и idempotency contract зафиксированы.
5. Cleanup policy ограничена безопасными целями и retention defaults.
6. Обязательный event catalog покрывает lifecycle, transcription, recovery и cleanup.
7. Спека не противоречит local-first ограничению: все операции локальные, без server-side orchestration.
8. Для логов зафиксированы числовые rotation/retention limits, исключающие неограниченный рост.

## Document Notes {#document-notes}

- 2026-04-02: Initial diagnostics/recovery baseline authored from the MVP TZ.
- 2026-04-13: Rewritten to implementation-ready canon with explicit job model, fault taxonomy, status transitions, startup recovery algorithm and cleanup contracts.
- 2026-04-13: Added explicit log rotation and retention limits for predictable storage usage.
