---
status: active
---

# FEAT-015: Cloud Transcription With Groq Or OpenRouter {#root}

## Простыми словами {#plain-language}

Пользователь может подключить собственный ключ Groq или OpenRouter и получить
текст для сохранённой встречи. Передача аудио включается только после понятного
согласия, длинные записи обрабатываются безопасными фрагментами, а готовый
транскрипт хранится рядом с локальными данными приложения.

## Goal {#goal}

Активировать provider-neutral seam из `FEAT-014` через два взаимозаменяемых
remote engine — Groq и OpenRouter — с явным opt-in, resumable queue, bounded
chunk upload, безопасным хранением ключей и одинаковым пользовательским
сценарием на Windows и macOS.

## Depends on {#depends-on}

- `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#modes.online`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization`
- `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime`
- `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#acceptance`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine`

## Related {#related}

- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#root`
- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#behavior`
- `spec://modules/app/FEAT-016-local-whisper-transcription#root`

## Scope {#scope}

### In scope {#scope.in}

- remote transcription provider choice `groq | openrouter`;
- отдельный API key для каждого provider в platform secret vault;
- ручная транскрибация любой readable saved recording;
- opt-in automatic transcription новых recordings;
- Groq Audio Transcriptions API;
- OpenRouter Speech-to-Text API;
- provider/model discovery и capability validation;
- mandatory word timestamps and source-aware remote requests;
- bounded audio chunking, persisted checkpoints и restart resume;
- FIFO queue с одной active job;
- automatic retry и actionable errors;
- remote ASR checkpoints and normalized word results for FEAT-017 materialization;
- progress/cancel/retry/open actions в минимальном UI;
- privacy/cost disclosure и zero-network behavior до opt-in;
- Windows/macOS functional parity.

### Out of scope {#scope.out}

- Fireworks provider revival;
- локальная speech model;
- live/streaming transcription во время встречи;
- local diarization runtime, speaker assignment and final turn materialization,
  owned by `FEAT-017`;
- summary, action items, semantic search или LLM enrichment;
- transcript editor;
- server-side account, sync или team sharing;
- automatic fallback на другой платный provider без решения пользователя;
- удаление исходного audio после успешной транскрибации.

## Product Behavior {#behavior}

### Setup and consent {#behavior.consent}

- Transcription выключена по умолчанию.
- В online mode пользователь выбирает `Groq` или `OpenRouter`, вводит API key и
  видит короткое объяснение: аудиофрагменты встречи будут отправлены выбранному
  внешнему сервису, возможна оплата по его тарифу.
- API key сохраняется локально после явного действия пользователя.
- Выбор `online` включает automatic transcription новых recordings согласно
  `FEAT-017`; отдельного automatic toggle нет.
- Смена provider не переносит и не показывает ключ другого provider.
- До сохранения ключа и включения/ручного запуска приложение не вызывает
  provider endpoints и не загружает audio.

### Recording actions {#behavior.actions}

- У каждой ready recording появляется действие `Транскрибировать`, если
  выбранный engine доступен.
- Manual action ставит только выбранную session в queue.
- При active `online` mode новая session ставится в queue только
  после успешной atomic finalization primary audio artifact.
- Повторное действие для `completed` предлагает заменить существующий
  transcript новой версией; старые artifacts сохраняются до успешной atomic
  promotion.
- Recording rename меняет display title будущего Markdown, не перемещает audio
  и transcript files.

### User-visible states {#behavior.states}

- `not_started` — транскрипции ещё нет;
- `queued` — ожидает обработки;
- `preparing` — приложение готовит безопасные chunks;
- `uploading` — текущий chunk отправляется;
- `processing` — provider обрабатывает chunk/result;
- `completed` — Markdown и JSON готовы;
- `retry_scheduled` — временная ошибка и известное время следующей попытки;
- `attention_required` — требуется ключ, баланс, модель или действие
  пользователя;
- `cancelled` — дальнейшая обработка остановлена, audio сохранено.

Основной recordings list показывает короткое состояние и progress. Provider
details, chunk count и response ids доступны в diagnostics.

## Provider Contract {#providers}

Оба adapter реализуют `ITranscriptionEngine` из Core и возвращают единый
результат:

- transcript text;
- provider id и resolved model id;
- requested/detected language, когда доступно;
- chunk time range;
- required word timestamps and optional additional segment timestamps;
- optional provider-reported duration, usage и cost;
- privacy/network capability descriptor;
- stable error category и provider request id.

HTTP DTO, headers и raw provider error types остаются внутри concrete adapter.

Для speaker-aware completion adapter обязан вернуть word timestamps по каждой
отдельной logical source track. Text-only и segment-only response сохраняется
только как internal recovery data и переводит job в
`attention_required: timestamp_capability_missing`.

### Groq {#providers.groq}

- Base URL: `https://api.groq.com/openai/v1`.
- Endpoint: `POST /audio/transcriptions`.
- Authentication: `Authorization: Bearer <GROQ_API_KEY>`.
- Request: `multipart/form-data` с binary `file`, `model`, optional
  `language`, `response_format`, `temperature` и timestamp granularities.
- Default model: `whisper-large-v3-turbo`.
- Model не выбирается в normal settings.
- Default response profile: `verbose_json`, `temperature = 0`, word and segment
  timestamps.
- Поддерживаемые direct inputs включают `mp3`, `m4a`, `wav`, `flac`, `ogg` и
  другие форматы, заявленные текущей Groq documentation.
- Adapter учитывает account-specific RPM/RPD/ASH/ASD limits и `retry-after`.

Single-request payload не полагается на dev-tier лимит. Client chunk budget
остаётся ниже документированного free-tier ограничения `25 MB`.

### OpenRouter {#providers.openrouter}

- Base URL: `https://openrouter.ai/api/v1`.
- Endpoint: `POST /audio/transcriptions`.
- Authentication: `Authorization: Bearer <OPENROUTER_API_KEY>`.
- Request: JSON с `model`, `input_audio.data` как raw base64,
  `input_audio.format`, optional `language`, `temperature` и provider options.
- Response: `verbose_json` transcript с required word timestamps и optional
  `usage` через OpenAI-compatible provider route.
- Доступные модели запрашиваются через Models API с
  `output_modalities=transcription`; routing layer дополнительно требует
  OpenAI-compatible `verbose_json` и `timestamp_granularities[]=word`.
- App-owned manifest/discovery выбирает совместимый STT route. Model id
  сохраняется в job provenance и не показывается как normal setting.
- Reviewed OpenRouter model: `openai/whisper-large-v3-turbo`, reviewed endpoints
  `groq` and `deepinfra`. Both support word-timed output; normalized response
  validation remains mandatory. STT API ignores `provider.only/order/ignore`;
  these fields are not routing guarantees. Before upload the app checks every
  current endpoint and stops if an unreviewed provider appears.
- Existing frozen `openai/whisper-1` jobs retain the reviewed `openai` route;
  new jobs use turbo. A frozen ZDR requirement is never silently relaxed.
- Audio input не передаётся через URL или data URI.
- OpenRouter upstream timeout учитывается chunker-ом; timeout уменьшает chunk
  budget перед retry.
- `provider.zdr = true` используется, когда текущий STT endpoint/model
  подтверждает ZDR routing. Если ZDR route недоступен, приложение запрашивает
  явное решение пользователя и не ослабляет privacy policy автоматически.
- With ZDR required, every possible endpoint must also appear with the exact
  model id and provider tag in the current `/endpoints/zdr` catalog before
  audio upload. Missing/incomplete policy fails with `zdr_route_unavailable`.
  This verifies the provider's published policy; a client-side HTTP success
  cannot independently prove deletion inside the provider's infrastructure.

## Chunking And Merge {#chunking}

- Primary audio никогда не переписывается ради provider upload.
- Job строит deterministic chunk manifest по hash/size/duration исходного
  artifact.
- Initial chunk ограничен одновременно `10 минутами` и `20 MiB` raw audio.
- OpenRouter adapter учитывает base64 expansion до создания request body.
- Между соседними chunks используется короткое перекрытие до `2 секунд`.
- Merge использует required word timestamps; text-only response не считается
  совместимым speaker-aware result.
- `413`, provider payload limit или повторный timeout делит конкретный chunk
  пополам до minimum duration `60 секунд`.
- Completed chunks имеют persisted checkpoint и не отправляются снова после
  restart.
- Изменение input file hash инвалидирует manifest и требует нового explicit
  run.
- Temporary chunks и partial results находятся в app-owned temp area. После
  completion/cancel/terminal cleanup они удаляются согласно recovery-safe
  retention.

## Queue And Retry {#queue-retry}

- Queue ordering: FIFO by `queued_at`.
- Одновременно выполняется максимум одна transcription job и один upload.
- Job переживает application restart и продолжает с первого незавершённого
  chunk.
- Default transient retry schedule: `1m / 5m / 15m` с jitter.
- `Retry-After` имеет приоритет над локальным schedule.
- `429`, Groq `498`, transport timeout и `5xx` являются retryable.
- `413` запускает chunk split без расходования общего retry budget.
- `401/403` переводят job в `attention_required: invalid_key`.
- OpenRouter `402` переводит job в `attention_required: insufficient_credit`.
- Invalid model/configuration и отсутствующий input требуют действия
  пользователя и не повторяются автоматически.
- Manual retry доступен после исправления ключа, баланса, модели или сети.
- Cancel прекращает новые uploads. Уже принятый provider request может
  завершиться удалённо; его результат не материализуется после подтверждённой
  local cancellation.

Endpoint-ы не обещают application idempotency key. Локальная идемпотентность
обеспечивается `job id + input hash + provider + model + chunk index`,
checkpoint-ами и atomic materialization.

## Persistence And Artifacts {#artifacts}

Persistence хранит:

- job id, session id, provider, model и input artifact hash;
- status, progress, attempts, next retry и stable error code;
- chunk index, time boundaries, hash и completion checkpoint;
- provider request ids без secrets;
- final Markdown/JSON paths и provider-reported usage.

Final artifacts:

- `<session-id>.transcript.md` — читаемый transcript;
- `<session-id>.transcript.json` — normalized result, chunks, timestamps,
  provider/model/language/usage metadata.

Final speaker-aware Markdown/JSON принадлежат `FEAT-017`. Remote adapter
передаёт туда source-scoped words, detected language, provider/model/usage и
request provenance. Provider raw payload может храниться только внутри
normalized JSON под versioned adapter section; ключи, authorization headers и
unbounded diagnostics туда не попадают.

Оба файла пишутся во временные paths, проверяются и продвигаются атомарно.
Database получает `completed` и final paths в той же commit boundary. Ошибка
никогда не удаляет или не повреждает primary audio.

## Security, Privacy And Cost {#privacy}

- Windows secrets используют DPAPI-backed vault, macOS secrets используют
  Keychain.
- API keys не попадают в settings JSON, database, crash reports, logs или UI
  после сохранения.
- Logs не содержат audio bytes, transcript text, base64 payload или полный
  meeting title.
- Перед первым remote use UI показывает provider, network upload, link на
  актуальную data policy и возможную стоимость.
- Groq ZDR управляется в Groq account Data Controls; приложение показывает
  ссылку и не заявляет ZDR без подтверждённого account capability.
- OpenRouter routing учитывает endpoint/provider retention policy и выбранный
  ZDR режим.
- Provider-reported usage/cost показывается после job, когда API возвращает эти
  данные. Приложение не выдаёт локальную оценку за окончательный счёт.
- Provider model/pricing/limits считаются изменяемыми external capabilities и
  обновляются через discovery/response metadata, без тихого перехода на другую
  платную модель.

## Platform Parity {#platform-parity}

- Queue, chunking, retry, persistence, materialization и UI принадлежат общим
  Core/Application/Desktop projects.
- Platform code отвечает только за secret vault, app paths и native file
  handling.
- Один provider adapter используется в Windows и macOS builds.
- Одинаковая session и fixture дают одинаковый chunk manifest и normalized
  transcript независимо от ОС.
- Feature acceptance требует Windows x64 и macOS arm64 evidence.

## Verification {#verification}

- Provider contract tests используют recorded/synthetic HTTP fixtures без
  реальных ключей.
- Groq tests проверяют multipart fields, auth redaction, verbose JSON,
  timestamps, `413`, `429`, `498`, `5xx` и `retry-after`.
- OpenRouter tests проверяют base64 body, compatible route/model discovery,
  verbose JSON, word timestamps, usage, `402`, `413`, `429`, timeout split и
  ZDR policy.
- Chunk tests покрывают short file, двухчасовую запись, size boundary,
  overlap merge, restart resume, input hash change и cancellation.
- Persistence tests подтверждают atomic status/artifact transition и отсутствие
  duplicate completed chunks.
- Security tests ищут ключи, base64 и transcript content в logs/database.
- Zero-network E2E проходит при выключенной transcription и при отсутствии
  ручной команды.
- Opt-in E2E с отдельными test keys проходит для Groq и OpenRouter на short
  consented fixture.
- Windows/macOS UI tests покрывают online mode setup, manual run,
  progress, retry, cancel и open transcript.
- Live long-recording smoke подтверждает chunking, restart resume и readable
  final Markdown.

## Acceptance {#acceptance}

FEAT-015 завершена, когда:

1. Пользователь может включить online mode, выбрать Groq или OpenRouter и
   безопасно сохранить ключ без model picker.
2. Remote audio upload невозможен до explicit opt-in или manual action.
3. Saved recording транскрибируется вручную, а новые recordings автоматически
   обрабатываются при active online mode.
4. Длинные встречи обрабатываются bounded chunks и продолжаются после restart.
5. Groq и OpenRouter adapters соблюдают текущие documented request/response,
   auth, limit и error contracts.
6. Оба adapter возвращают source-scoped word timestamps для speaker-aware
   materialization; несовместимый route завершается actionable состоянием.
7. Retry, cancel, invalid key, insufficient credit, model change, timeout и
   payload limit дают корректное состояние и действие пользователя.
8. Keys, audio и transcript content отсутствуют в diagnostics/logs.
9. Final FEAT-017 Markdown/JSON материализуются атомарно, primary audio остаётся
   сохранённым при любой ошибке.
10. Windows x64 и macOS arm64 используют один общий feature implementation и
   проходят parity evidence.

## Document Notes {#document-notes}

- 2026-09-07: Live acceptance identified upstream 429 on the initial
  `openai/whisper-1` route and its absence from the ZDR endpoint catalog.
  Reviewed turbo with Groq/DeepInfra returned real word timestamps; added
  all-endpoint ZDR preflight and normalized provider language names to ISO
  codes without a Russian-specific allowlist.
- 2026-08-30: Cloud engines moved under the three-mode FEAT-017 UX. Groq is
  fixed to `whisper-large-v3-turbo`; OpenRouter routing must support
  `verbose_json` word timestamps; model choice and successful undiarized output
  were removed from the user contract.
- 2026-07-30: Linked the local Whisper sibling engine; shared queue, artifact
  contract and UI are implemented once for both transcription paths.
- 2026-07-30: Future seam из `FEAT-014` активирован как отдельная opt-in волна
  для Groq direct key или OpenRouter key. Diarization, summaries и local model
  сохранены за пределами первого transcription scope.

## External References {#external-references}

- [Groq: Speech to Text](https://console.groq.com/docs/speech-to-text)
- [Groq: API Reference](https://console.groq.com/docs/api-reference)
- [Groq: Rate Limits](https://console.groq.com/docs/rate-limits)
- [Groq: API Errors](https://console.groq.com/docs/errors)
- [Groq: Your Data](https://console.groq.com/docs/your-data)
- [OpenRouter: Speech-to-Text](https://openrouter.ai/docs/guides/overview/multimodal/stt)
- [OpenRouter: Create transcription](https://openrouter.ai/docs/api/api-reference/transcriptions/create-audio-transcriptions)
- [OpenRouter: Errors and Debugging](https://openrouter.ai/docs/api/reference/errors-and-debugging)
- [OpenRouter: Zero Data Retention](https://openrouter.ai/docs/guides/features/zdr)
- [OpenRouter: Data Collection](https://openrouter.ai/docs/guides/privacy/data-collection)
