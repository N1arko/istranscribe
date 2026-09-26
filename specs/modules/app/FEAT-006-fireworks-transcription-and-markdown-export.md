---
status: superseded
---

# FEAT-006: Fireworks Transcription And Markdown Export {#root}

## Простыми словами {#plain-language}

Эта фича определяет путь от сохранённой записи до готового транскрипта: как сессия ставится в очередь, как формируется запрос к Fireworks, как материализуются `markdown + json`, и как работает retry при ошибках.

## Goal {#goal}

Описать implementation-ready transcription канон для MVP без неопределённостей в queue/retry/artifact semantics.

## Superseded by {#superseded-by}

- Release v2 deactivates this runtime through `spec://modules/app/FEAT-014-transcription-extension-seam#root`.

## Depends on {#depends-on}

- `spec://common/PROP-004-meeting-session-and-data-model#pipeline`
- `spec://common/PROP-004-meeting-session-and-data-model#statuses.transcription`
- `spec://common/PROP-004-meeting-session-and-data-model#markdown-format`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#fault-taxonomy.retry`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine`

## Related {#related}

- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#behavior`

## Scope {#scope}

### In scope {#scope.in}

- enqueue завершённой `MeetingSession` в transcription pipeline;
- Fireworks request contract (model/language/diarization/response format);
- материализация `transcript.md` и `verbose_json`;
- automatic retry и manual retry semantics;
- status transitions и observable failure modes.

### Out of scope {#scope.out}

- альтернативные провайдеры транскрибации;
- LLM summary/semantic enrichment;
- transcript editor.

---

## Behavior {#behavior}

Каноническое поведение FEAT-006 определяется разделами:

- очередь и enqueue trigger: `#queue-management`
- provider request contract: `#request-contract`
- materialization artifacts: `#artifact-materialization`
- retry lifecycle и ошибки: `#retry-lifecycle`, `#errors`

---

## Queue Management {#queue-management}

### Enqueue trigger {#queue-management.trigger}

Сессия попадает в transcription queue только после успешной finalization recording artifacts:

- `recording_status = saved`
- минимум один валидный audio artifact path доступен для чтения

После этого `transcription_status` становится `queued`.

### Queue ownership {#queue-management.ownership}

- FEAT-006 владеет product-уровнем enqueue criteria и artifact expectations.
- INFRA-004 владеет job execution/scheduling/retry orchestration.

### Concurrency and ordering {#queue-management.ordering}

- MVP queue ordering: FIFO by `queued_at`.
- Максимум одна активная transcription job (`uploading|processing`) на приложение.
- Параллельная транскрибация нескольких сессий в MVP не обязательна.

---

## Fireworks Request Contract {#request-contract}

### Required request fields {#request-contract.required-fields}

Каждый provider request обязан включать:

- audio file (основной input artifact)
- model
- optional language
- diarization flag
- response format

### Model and language mapping {#request-contract.model-language}

- `model` берётся из настроек Transcription section.
- `language = auto` отправляется как отсутствие явной language фиксации.
- Явный язык (`ru`, `en`, etc.) передаётся как provider-supported language value.

### Diarization contract {#request-contract.diarization}

- Если diarization включена, запрос обязан просить формат, достаточный для speaker-labeled transcript (`verbose_json` или эквивалентный rich format).
- Если diarization выключена, итоговый markdown всё равно формируется, но speaker labels могут отсутствовать.

### Input artifact selection {#request-contract.input-artifact}

Канонический порядок выбора audio input:

1. `audio_mix_path` (если существует)
2. `audio_output_path` (если mix отсутствует)
3. `audio_mic_path` (если output отсутствует)

Если ни один input artifact невалиден, транскрибация не стартует, статус `failed`, `error_code = TX_INPUT_MISSING`.

### HTTP transport contract {#request-contract.http}

- Method: `POST`
- Endpoint: `/v1/audio/transcriptions`
- Content-Type: `multipart/form-data`
- Authentication: `Authorization: Bearer <api_key>`

Required multipart fields:

- `file` (binary audio input)
- `model` (string)

Optional multipart fields:

- `language` (string)
- `response_format` (`json` or `verbose_json`)
- `diarization` (bool/flag per provider contract)
- `timestamp_granularities[]` (when verbose timestamps are requested)

If diarization enabled, canonical request profile for MVP:

- `response_format = verbose_json`
- `timestamp_granularities[]` includes at least `word`

### Response contract {#request-contract.response}

Минимально ожидаемые поля успешного provider response:

- `text` (full transcript text)
- `language` (detected or fixed)
- `duration` (seconds)

When `response_format = verbose_json`, expected additional structures:

- segment or word-level timestamps
- speaker/diarization metadata where provider supports it

`transcript_json_path` хранит provider payload in normalized JSON document. Поля, не нужные продукту, допускается сохранять как есть без попытки semantic post-processing.

---

## Artifact Materialization {#artifact-materialization}

### Output paths {#artifact-materialization.paths}

- Markdown и JSON сохраняются в configured transcripts path с привязкой к session id.
- `transcript_md_path` и `transcript_json_path` записываются в `MeetingSession` атомарно с `transcription_status = completed`.

### Markdown structure {#artifact-materialization.markdown-structure}

MVP markdown обязан включать:

1. Заголовок встречи.
2. Metadata block: дата, start/end/duration, source app, mode, devices, model.
3. Секцию transcript body.
4. Если есть diarization segments: speaker-labeled blocks с timestamp marker.

### JSON preservation {#artifact-materialization.json}

- При diarization enabled полный provider `verbose_json` обязателен к сохранению.
- При diarization disabled допускается сохранение provider JSON summary, если full verbose не возвращается.

---

## Retry Lifecycle {#retry-lifecycle}

### Default retry policy {#retry-lifecycle.defaults}

- Auto retry включён по умолчанию.
- Default attempts: `3`.
- Default schedule: `1m / 5m / 15m`.

### Retryable vs non-retryable failures {#retry-lifecycle.classification}

- transient/recoverable errors -> `failed` then `retry_scheduled`.
- invalid API key (`configuration`) -> `failed` без automatic retry.
- missing input artifact (`terminal`) -> `failed` без automatic retry.

### Manual retry semantics {#retry-lifecycle.manual}

- Manual retry из FEAT-004 всегда требует явного user action.
- При manual retry session переходит в `queued`, even если auto-retry ранее исчерпан.
- Manual retry не должен удалять existing artifacts до успешного нового completion.

---

## User-visible Error Semantics {#errors}

### Invalid API key {#errors.invalid-key}

- Local recordings остаются сохранёнными.
- Transcription status становится `failed` с машинным `error_code` и человекочитаемым message.
- Пользователь видит, что требуется обновить API key в Settings.

### Provider/network failures {#errors.provider}

- Ошибка не уничтожает `audio_*` artifacts.
- До исчерпания retry attempts статус может переходить через `retry_scheduled`.

---

## Acceptance {#acceptance}

FEAT-006 считается завершённой, если одновременно выполнено:

1. Enqueue trigger формализован и не запускается до сохранения recording artifacts.
2. Fireworks request contract фиксирует model/language/diarization/format behavior.
3. Определён canonical выбор input artifact для transcription.
4. Markdown и JSON materialization имеют явный output contract.
5. Retry lifecycle согласован с INFRA-004 и различает retryable/non-retryable errors.
6. Invalid API key не ломает локальное хранение записей и даёт явный `failed` state.
7. Manual retry из Home повторно ставит сессию в очередь без внешних скриптов.
8. Fireworks transport contract фиксирует метод, endpoint, auth и required multipart fields.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-13: Rewritten to implementation-ready canon with queue contract, request/artifact contracts and full retry lifecycle semantics.
- 2026-04-13: Added explicit HTTP-level Fireworks request/response contract for implementation and tests.
- 2026-07-11: Linked the release v2 Fireworks deactivation and provider-neutral future seam.
