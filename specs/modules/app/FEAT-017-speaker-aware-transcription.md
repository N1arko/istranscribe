---
status: active
---

# FEAT-017: Speaker-Aware Transcription {#root}

## Простыми словами {#plain-language}

Пользователь выбирает один из трёх понятных режимов: без транскрибации,
локальная транскрибация или онлайн-транскрибация. Готовый текст показывает,
где говорит пользователь и где говорят отдельные собеседники. Выбор speech- и
diarization-моделей остаётся внутренней ответственностью приложения.

## Goal {#goal}

Добавить единый speaker-aware pipeline поверх provider-neutral seam из
`FEAT-014`: сохранить известную роль microphone как `self`, разделить system
output на устойчивых в пределах встречи remote speakers, объединить реплики по
word timestamps и материализовать одинаковый transcript contract для local и
online execution на Windows x64 и macOS arm64.

## Depends on {#depends-on}

- `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#source-retention`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#settings`
- `spec://modules/app/FEAT-010.A-release-v2-localization#resources`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine`

## See also {#see-also}

- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`
- `spec://modules/app/FEAT-016-local-whisper-transcription#root`
- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#behavior`

## Scope {#scope}

### In scope {#scope.in}

- один persisted transcription mode `off | local | online`;
- автоматическая транскрибация новых recordings в выбранном enabled mode;
- ручной запуск, retry и atomic replacement готового transcript;
- source-aware requests для microphone и system output;
- deterministic `self` identity для microphone source;
- локальная diarization system-output source для local и online modes;
- automatic language detection без product-level language allowlist;
- word-level timestamp alignment и speaker-turn merge;
- versioned speaker-aware JSON и readable Markdown;
- app-managed acquisition diarization assets без пользовательского аккаунта;
- migration существующих engine/model/automatic settings;
- Windows/macOS parity, privacy и recovery contracts.

### Out of scope {#scope.out}

- live captions или streaming transcript во время встречи;
- распознавание личности, имени или аккаунта собеседника;
- сохранение speaker identity между разными recordings;
- ручное переименование и редактирование speaker turns;
- speaker-count, diarization-model или ASR-model controls в normal settings;
- translation, summary, action items, semantic search и LLM enrichment;
- server account, sync, team sharing или human review service.

## Product Modes And Settings {#modes}

### Canonical modes {#modes.values}

Normal settings показывают одну single-choice control с тремя локализованными
вариантами:

| Persisted value | Русская подпись | Meaning |
|---|---|---|
| `off` | `Без транскрибации` | новые записи не создают transcription jobs |
| `local` | `Включить локальную транскрибацию` | ASR и diarization выполняются на устройстве |
| `online` | `Включить онлайн-транскрибацию` | ASR выполняется выбранным remote provider, diarization — на устройстве |

- Default и safe fallback: `off`.
- Выбор `local` или `online` включает automatic transcription новых ready
  recordings; отдельного automatic-transcription toggle нет.
- Смена mode влияет на новые jobs. Уже запущенная job сохраняет execution plan,
  source hashes, engine и model ids до terminal state.
- `Транскрибировать` у существующей ready recording использует текущий enabled
  mode. В `off` действие сначала ведёт к выбору mode и ничего не отправляет в
  сеть.
- Model, diarization, min/max speakers, backend, threads и timestamp profile не
  показываются как пользовательские настройки.

### Local setup {#modes.local}

- При первом выборе `local` приложение показывает размер загрузки, ожидаемое
  использование памяти и действие подтверждения.
- Приложение загружает и проверяет canonical ASR model из `FEAT-016` и
  app-managed diarization assets.
- Установка приложения не содержит ASR weights. Model cache переживает
  application update и может быть очищен отдельным storage action.
- Пока обязательные assets не verified, mode хранится как desired `local`, а
  runtime показывает setup progress и не создаёт заведомо невыполнимую job.

### Online setup {#modes.online}

- Внутри `online` пользователь выбирает `Groq` или `OpenRouter`, сохраняет ключ
  и принимает provider-specific disclosure.
- Provider/model routing обязан возвращать word timestamps. Несовместимый route
  не запускается и получает `attention_required: timestamp_capability_missing`.
- UI не показывает remote model picker. Закрепление модели и проверка provider
  route принадлежат `FEAT-015`.
- До explicit online opt-in приложение не отправляет audio или meeting metadata
  remote provider.

## Source And Speaker Model {#speakers}

### Source roles {#speakers.sources}

- `microphone` является известным source пользователя и получает semantic role
  `self`.
- `system_output` содержит голоса remote participants и получает semantic role
  `remote` после diarization.
- Source tracks сохраняют общий monotonic meeting timeline, включая pause и
  discontinuity metadata.
- Transcription request фиксирует доступные source descriptors, paths, hashes,
  timeline origin и channel availability. Mixed primary artifact остаётся
  пользовательской записью и не заменяет source-aware input новой job.
- Microphone-only meeting создаёт только `self` turns. Output-only meeting
  создаёт только numbered remote turns.

### Speaker identities {#speakers.identity}

- Canonical semantic ids: `self` и `remote:<ordinal>`.
- Remote ordinal назначается по времени первого уверенного появления: `1`,
  `2`, `3` и далее.
- Ordinal стабилен при chunking, restart/resume и повторной materialization одной
  job.
- `self` отображается как `Я` в русском и `Me` в английском; remote speaker —
  как `Собеседник N` / `Speaker N`. JSON хранит semantic role и ordinal, поэтому
  UI может локализовать label без изменения identity.
- Голосовая похожесть используется только внутри одной recording. Pipeline не
  утверждает имя, личность или равенство speaker между разными встречами.

### Legacy and degraded inputs {#speakers.legacy}

- Для записи, где сохранился только mixed primary artifact, pipeline не
  присваивает роль `self` по догадке.
- Доступный mixed artifact может быть диаризован в `unknown:<ordinal>` с
  пользовательскими labels `Участник N` / `Participant N` и provenance
  `source_identity_unavailable`.
- Отсутствие microphone не считается ошибкой. Отсутствие любого readable audio
  переводит job в `attention_required: input_missing`.
- Новый source-aware recording не завершается как speaker-aware transcript,
  если remote source присутствует, а diarization не дала валидный result.

## Diarization Runtime {#diarization}

- Diarization является app-owned local post-processing stage и используется в
  `local` и `online` modes.
- Runtime использует pinned speaker-segmentation and embedding pipeline с
  versioned engine/model ids, exact hashes, licenses и platform payload
  inventory.
- Assets загружаются приложением из app-controlled manifest. Пользователь не
  регистрируется в Hugging Face или другом model registry и не вводит model
  token.
- Public installer/DMG может содержать небольшой native runtime; model weights
  загружаются on demand и хранятся в app-owned model cache.
- Speaker count определяется автоматически. Internal safety bounds защищают
  runtime от noise fragmentation и не становятся normal UI controls.
- Diarization chunks используют overlap и cross-chunk speaker embeddings, чтобы
  один голос не получал новый ordinal на каждой границе.
- Runtime не зависит от detected language и обрабатывает речь как audio speaker
  characteristics.

### Pinned implementation {#diarization.implementation}

- Isolated C# worker uses `sherpa-onnx/1.12.14`; native source commit:
  `26aa2fa93210376a89de3a65a1a4dd320c37f5e9`, source archive SHA-256:
  `7c2daea812195ebef5f0799e68a907319e732b152176b0efa4e1dd7660df6572`.
- The native build enables CPU speaker diarization and disables TTS, Python,
  PortAudio, WebSocket and GPU; Eigen uses `EIGEN_MPL2_ONLY`. The all-features
  native NuGet payload is excluded. `Build-SpeakerNative.ps1` records exact
  native files and full dependency licenses; composition rejects missing or
  mismatched inventories. macOS baseline is 14.2 with app-relative load paths.
- Segmentation: public Pyannote segmentation-3.0 ONNX export, MIT/CNRS,
  5,992,913 bytes, SHA-256
  `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079`.
- Embeddings: WeSpeaker English VoxCeleb ResNet34 ONNX, Apache-2.0,
  26,534,365 bytes, SHA-256
  `5ef208a9da1453335308a6b6f4e6dfbd7e183a38b604de0a57664f45d257fe94`.
  The training corpus name does not restrict ASR language; multilingual speaker
  quality remains part of the real-audio acceptance matrix.
- Versioned `speaker-clustering/v1` parameters: automatic cluster count,
  clustering threshold 0.5, 256-dimensional normalized voice vectors,
  cross-chunk cosine match >0.65, one-to-one cluster mapping inside each chunk,
  at most 64 meeting speakers. The embedding uses up to 30 seconds of clean
  speech with overlapping voices excluded. These parameters require separate
  real multi-chunk quality evidence before product completion.
- Assignment uses maximum word/voice time overlap, nearest voice within 350 ms,
  and explicit unresolved identity on a tie. Turn boundaries use voice changes,
  pauses over 1.25 seconds, 30 seconds or 420 characters. Empty Whisper control
  items do not become words. Nonempty text without valid word times fails closed.
- The on-demand model payload totals 32,527,278 bytes; acquisition verifies size
  and SHA-256 before atomic promotion. Inference performs no model downloads.
- Source-ASR checkpoints precede assembly and retain provider request identity,
  language and usage per source/chunk. Cross-chunk speaker reconciliation can
  replay from immutable checkpoints without repeating successful ASR calls.

## Recognition And Language Contract {#recognition}

- ASR получает microphone и system-output audio как отдельные logical streams с
  общей timeline. Remote APIs вызываются отдельными requests/chunks, поскольку
  multitrack input не гарантирует обработку всех дорожек.
- Silent ranges не отправляются remote provider после deterministic VAD, при
  этом original timeline offsets сохраняются.
- Каждый engine возвращает detected language и word items `text, start, end`
  для каждого source. Segment-only или text-only result недостаточен для нового
  speaker-aware completion.
- Language по умолчанию определяется автоматически. Persisted/UI language не
  подставляется как язык audio.
- Product contract не содержит hardcoded `ru | en` allowlist. Engine-supported
  multilingual language range и detected ISO code сохраняются как capability и
  provenance.
- Пунктуация и casing принадлежат ASR result. Speaker merge не переписывает
  распознанные слова.

## Turn Assembly {#assembly}

- Microphone words напрямую получают speaker id `self`.
- System-output words назначаются remote diarization intervals по максимальному
  temporal overlap; ближайший интервал используется только внутри bounded
  tolerance.
- Неуверенное слово получает explicit `speaker_unresolved` provenance и не
  меняет identity соседнего speaker автоматически.
- Consecutive words одного speaker объединяются в turn до speaker change,
  meaningful pause или configured maximum readable length.
- Реальное перекрытие речи сохраняется отдельными overlapping turns. Ordering
  стабилен по `start`, затем по source role и speaker id.
- Merge deterministic для одинаковых ASR words, diarization intervals и
  versioned parameters.

## Persistence And Artifacts {#artifacts}

Normalized JSON schema version `speaker-transcript/v1` хранит:

- session/job ids, mode, engine/model/runtime versions и input hashes;
- requested/detected languages и per-source provenance;
- diarization engine/model hashes и parameters version;
- speakers с semantic role, ordinal и source kind;
- turns с `speaker_id`, `start`, `end`, `text`, source kind и confidence flags;
- optional words с timestamps и speaker assignment;
- remote usage/cost/request ids без secrets;
- warnings `source_identity_unavailable | speaker_unresolved`.

Markdown показывает title/date/duration и последовательность реплик:

```md
**Я · 00:12**
Давайте начнём.

**Собеседник 1 · 00:15**
Хорошо.
```

- Markdown создаётся на выбранном UI language; JSON остаётся
  language-neutral по identity.
- JSON и Markdown пишутся во temporary paths и продвигаются атомарно.
- `completed` означает наличие readable artifacts и speaker assignment для
  каждого resolved turn.
- Legacy transcript artifacts сохраняются и открываются без миграции содержимого.

## Source Retention And Cleanup {#retention}

- Finalizer перед удалением microphone/output source tracks создаёт durable
  transcription input handoff с paths, hashes и timeline metadata, если mode
  для новой recording равен `local` или `online`.
- Source tracks остаются app-owned processing artifacts до `completed`,
  `cancelled` или terminal cleanup состояния speaker-aware job.
- Crash/restart восстанавливает handoff и job без повторной записи audio.
- Смена mode после enqueue не удаляет inputs active job.
- Cleanup удаляет только подтверждённые processing copies и никогда не удаляет
  primary recording или последний готовый transcript.
- В `off` source tracks следуют обычной recovery-safe cleanup policy FEAT-012.

## Failures And Recovery {#failures}

- `diarization_assets_missing` предлагает продолжить или повторить загрузку.
- `diarization_failed` сохраняет checkpoints и предлагает retry/diagnostics.
- `timestamp_capability_missing` требует совместимый online route или смену
  mode.
- `model_resource_insufficient` объясняет disk/memory requirement canonical
  local model без предложения меньшей скрытой модели.
- `input_missing` и `input_changed` не повторяются автоматически.
- Transient local worker/provider failures используют общий bounded retry.
- Pipeline не публикует undiarized transcript как успешный speaker-aware
  result. Partial ASR data остаётся внутренним recovery artifact.

## Privacy And Diagnostics {#privacy}

- `local` mode не отправляет audio, transcript или meeting metadata в сеть;
  разрешены только загрузки versioned model assets до verified install.
- `online` mode отправляет consented source chunks выбранному provider;
  diarization audio и speaker embeddings остаются локальными.
- Logs, database diagnostics и crash reports не содержат raw audio, transcript
  text, voice embeddings, API keys или base64 payload.
- Diagnostics показывают mode, source availability, engine/model versions,
  language, speaker count, timing, stable warning/error categories и hashes в
  redacted form.

## Migration {#migration}

- Legacy transcription disabled canonicalizes to `off`.
- Enabled `local.whisper` canonicalizes to `local`.
- Enabled `remote.groq` или `remote.openrouter` canonicalizes to `online` и
  сохраняет provider/key references.
- Legacy separate automatic toggle схлопывается в mode: disabled automatic
  сохраняет выбранный engine как setup preference, а active mode остаётся
  `off` до нового explicit выбора пользователя.
- Legacy selected local model не управляет новым mode. Existing model files не
  удаляются автоматически и могут быть предложены storage cleanup.
- New Groq jobs use `whisper-large-v3-turbo`; new OpenRouter jobs use the reviewed
  `openai/whisper-large-v3-turbo` profile. Legacy stored model selections do not override these
  effective defaults. Existing durable jobs retain their frozen model identity.
- Migration никогда не включает online upload без ранее сохранённого opt-in.

## Verification {#verification}

- Settings tests проверяют три mode values, отсутствие model/diarization/
  speaker-count controls и deterministic migration.
- Source fixtures покрывают microphone+output, microphone-only, output-only,
  overlapping speech, pause/discontinuity и legacy mixed artifact.
- Speaker fixtures покрывают одного, двух и трёх remote speakers, cross-chunk
  identity и restart resume.
- Multilingual fixtures включают русский, английский и минимум один другой
  supported language; persisted UI language не влияет на ASR detection.
- Local E2E использует full `large-v3-turbo`, word timestamps и local
  diarization при отключённой сети после установки assets.
- Groq/OpenRouter contract tests требуют word timestamps, отдельные source
  requests и отказ несовместимого route.
- Artifact tests проверяют schema, deterministic merge, localized labels,
  overlapping turns и atomic replacement.
- Recovery tests проверяют удержание source tracks до terminal state и cleanup
  без удаления primary audio.
- Windows x64 и macOS arm64 проходят одинаковую functional matrix; visual
  acceptance выполняется отдельно пользователем после implementation-ready
  automated gates.

## Acceptance {#acceptance}

FEAT-017 завершена, когда:

1. Settings содержит только три canonical transcription modes и provider setup
   внутри online mode; model и diarization controls отсутствуют.
2. Local mode использует fixed full `large-v3-turbo`, automatic language
   detection, word timestamps и app-managed local diarization.
3. Online Groq и OpenRouter paths возвращают word timestamps и проходят тот же
   local diarization/turn assembly contract.
4. Microphone reliably получает semantic role `self`, remote speakers получают
   стабильные ordinals в пределах recording, а mixed legacy input не выдаётся
   за известного `self`.
5. JSON и Markdown содержат chronological speaker turns и материализуются
   атомарно без повреждения audio или предыдущего transcript.
6. Source tracks переживают restart и сохраняются до terminal processing
   cleanup.
7. Ни один enabled path не завершает job успешным undiarized transcript при
   наличии remote source.
8. Language detection и pipeline работают без hardcoded Russian language
   choice и проходят multilingual fixtures.
9. Пользователю не нужен model-registry account/token; installer size не
   включает full ASR weights.
10. Windows x64 и macOS arm64 проходят automated parity gates и готовы к
    пользовательскому transcript-quality review.

## Document Notes {#document-notes}

- 2026-08-30: Initial active canon authored from the approved three-mode UX,
  full local `large-v3-turbo`, source-known `self`, local remote-speaker
  diarization and speaker-aware Groq/OpenRouter requirement.
- 2026-09-07: Pinned the portable ONNX speaker implementation, native build
  profile, model integrity, clustering/turn parameters and per-source recovery
  provenance. Product acceptance still requires real Windows and online runs.

## External References {#external-references}

- [Groq: Speech to Text](https://console.groq.com/docs/speech-to-text)
- [OpenRouter: Speech-to-Text](https://openrouter.ai/docs/guides/overview/multimodal/stt)
- [ggml-org: whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp)
