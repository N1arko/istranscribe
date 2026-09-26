---
status: active
---

# FEAT-016: Local Whisper Transcription {#root}

## Простыми словами {#plain-language}

Пользователь один раз загружает full `large-v3-turbo` и транскрибирует встречи
прямо на своём компьютере. Аудиозаписи не отправляются во внешний сервис,
обработка переживает перезапуск приложения и использует доступное ускорение Mac
или Windows без постоянной нагрузки в фоне.

## Goal {#goal}

Добавить к provider-neutral seam из `FEAT-014` локальный `whisper.cpp` engine с
управляемой загрузкой моделей, изолированным worker process, bounded resource
policy, resumable jobs и одинаковым пользовательским сценарием на Windows x64
и macOS arm64.

## Depends on {#depends-on}

- `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#modes.local`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization`
- `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime`
- `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#acceptance`
- `spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#acceptance`

## Related {#related}

- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`

## Scope {#scope}

### In scope {#scope.in}

- local engine на базе pinned `whisper.cpp`;
- Windows x64 CPU baseline и optional Vulkan acceleration;
- macOS arm64 Metal acceleration и CPU fallback;
- отдельный short-lived transcription worker process;
- versioned manifest единственной canonical multilingual model;
- explicit download, resume, checksum verification, update и removal canonical model;
- hardware/resource capability probe;
- ручная транскрибация готовой записи;
- opt-in automatic local transcription новых recordings;
- persisted chunk checkpoints, pause/preemption, cancel и restart resume;
- required word/segment timestamps for FEAT-017 speaker-aware materialization;
- local mode UX из `FEAT-017` без model picker;
- deterministic native/model licensing and payload inventory;
- zero-audio-network verification после установки модели.

### Out of scope {#scope.out}

- включение модели в основной installer/DMG;
- обязательная загрузка модели при first run;
- CUDA, ROCm, OpenVINO или Core ML companion model в первой волне;
- arbitrary user-imported model files;
- quantized/community model variants;
- live transcription во время записи;
- diarization runtime and speaker-turn assembly, owned by `FEAT-017`;
- translation в другой язык;
- summaries, action items, semantic search и transcript editor;
- обучение или fine-tuning на пользовательских записях;
- cloud fallback без явного выбора пользователя.

## Engine And Runtime {#engine}

### Canonical implementation {#engine.implementation}

- Initial runtime baseline: tagged `whisper.cpp v1.9.1`, pinned exact commit и
  source archive hash в release manifest.
- Integration использует C API и app-owned thin interop layer.
- Native payload содержит только library/runtime files, нужные выбранному RID.
- `whisper-cli`, local HTTP server, Python, package manager и executable из
  system `PATH` не участвуют в product runtime.
- `whisper.cpp` и OpenAI Whisper license notices входят в deterministic
  third-party inventory.
- Runtime update является обычным versioned application update и проходит
  native ABI, model compatibility и golden transcript gates.

### Project ownership {#engine.projects}

```text
IsTranscribe.Core/
└── provider-neutral transcription contracts

IsTranscribe.Application/
└── job lifecycle, selection, progress and artifact orchestration

IsTranscribe.Transcription.Local/
├── whisper C API interop
├── model catalog/download manager
├── chunk/checkpoint protocol
└── normalized result mapping

IsTranscribe.Transcription.Worker/
└── isolated local inference process

runtimes/
├── win-x64/native/
└── osx-arm64/native/
```

`Desktop` видит engine capabilities и commands через Application contracts и
не загружает native library напрямую.

## Canonical Model {#models}

- Единственная public ASR model: full, non-quantized multilingual
  `large-v3-turbo` для `whisper.cpp`.
- Canonical artifact: `ggml-large-v3-turbo.bin`, upstream size approximately
  `1.5 GiB`; exact bytes и SHA-256 фиксируются embedded manifest.
- Model поддерживает automatic multilingual transcription. Product не
  ограничивает audio языками интерфейса.
- Base/small/medium и quantized variants не являются runtime fallback и не
  показываются пользователю.
- Exact URL, size, SHA-256, upstream model id, format version, minimum runtime
  version и expected memory находятся в versioned embedded manifest.
- Models хранятся вне installation directory:
  `%LOCALAPPDATA%/isTranscribe/models` на Windows и
  `~/Library/Application Support/isTranscribe/models` на macOS.
- Application update сохраняет model cache.
- Storage surface показывает installed size и действие `Удалить локальную
  модель`; normal transcription settings не содержит model controls.
- Model replacement не удаляет последнюю рабочую copy до checksum и load
  verification новой.

Смена canonical model является изменением product canon и требует benchmark,
multilingual transcript review, manifest update и migration plan.

## Model Acquisition {#model-download}

- Модель загружается после explicit выбора `local` и подтверждения показанного
  download/resource requirement.
- Download использует HTTPS, `.partial` file, resumable range requests и
  bounded retry.
- Перед download проверяются write access и свободное место для partial file,
  atomic promotion и safety margin.
- SHA-256 проверяется до первого load; mismatch удаляет только invalid partial
  и показывает безопасный retry.
- Cancel сохраняет resumable partial, если пользователь не выбрал удаление.
- Повторные запуски не обращаются к сети, если installed model и manifest hash
  валидны.
- Model source, license, size и checksum доступны в diagnostics/release
  manifest.
- Download traffic не содержит audio, transcript, meeting metadata или
  пользовательские identifiers сверх обычных transport metadata.

## Hardware And Acceleration {#acceleration}

### macOS arm64 {#acceleration.macos}

- Primary backend: Metal on Apple Silicon.
- CPU fallback обязателен при Metal initialization/device failure.
- Core ML/ANE companion artifacts не загружаются в первой волне.
- Runtime capability descriptor сообщает выбранный backend, model memory и
  expected compute class.

### Windows x64 {#acceleration.windows}

- Portable CPU build является обязательным baseline.
- Vulkan backend может включаться после runtime capability probe и per-device
  smoke; driver/runtime failure безопасно возвращает job на CPU.
- CUDA/ROCm/OpenVINO runtimes не входят в payload первой волны.
- Windows build не требует Visual Studio, Python или CMake на машине
  пользователя.

### Resource policy {#acceleration.resources}

- До запуска проверяются installed RAM, available memory, disk и backend
  health.
- Недостаток памяти не запускает заведомо неустойчивую job и показывает
  requirement canonical model.
- Worker использует bounded thread count и background/low process priority.
- Одновременно работает максимум одна local inference job.
- Новая recording session имеет приоритет: local worker завершает текущий
  checkpoint, освобождает model context и переносит job обратно в queue.
- Automatic jobs откладываются во время OS low-power/energy-saver state.
- Manual action может продолжить обработку после понятного предупреждения о
  батарее и возможном нагреве.
- После завершения job или короткого idle timeout worker выгружается, model
  memory возвращается системе.

## Worker Isolation {#worker}

- Main UI process запускает worker только для local job.
- Transport использует private child-process pipes с versioned framed
  protocol; listening TCP/HTTP port не создаётся.
- Worker получает job id, input path, model path/hash, language, backend и
  chunk boundaries.
- Secrets worker-у не передаются.
- Worker публикует readiness, progress, segment results, resource telemetry и
  stable failure categories.
- Crash, out-of-memory или native failure не завершает main application.
- После worker crash незавершённый chunk остаётся pending; completed
  checkpoints сохраняются.
- Повторный native crash на том же backend предлагает CPU safe mode и не
  образует бесконечный restart loop.
- Shutdown и cancel завершают worker с bounded grace period, затем безопасно
  останавливают child process.

## Audio Preparation And Inference {#inference}

- New source-aware input использует отдельные microphone/output tracks из
  `FEAT-017`; legacy input может использовать primary readable meeting artifact.
- Platform sample decoder читает source/legacy audio и потоково преобразует его в
  `16 kHz`, mono, normalized PCM для C API.
- Standalone `ffmpeg` и полный temporary WAV не требуются.
- Long recording делится на deterministic chunks максимум по `5 минут` с
  коротким overlap и persisted time boundaries.
- Engine использует multilingual transcription task, `temperature = 0` и
  automatic language detection.
- Word and segment timestamps обязательны для каждого source result.
- Chunk result сохраняется атомарно до перехода к следующему chunk.
- Merge сдвигает timestamps в meeting timeline и удаляет только bounded
  overlap duplicates.
- Input artifact hash, model hash, runtime version и inference parameters
  входят в job identity; изменение любого значения создаёт новый explicit run.

## Product Behavior And UI {#experience}

- Normal settings показывает mode `Включить локальную транскрибацию` из
  `FEAT-017`, privacy/resource summary и setup progress. Model preset отсутствует.
- Backend, threads, hashes и timings находятся в diagnostics.
- `Транскрибировать` у ready recording ставит manual local job.
- Active local mode автоматически ставит новые ready recordings в queue после
  verified model and diarization assets install.
- Progress показывает подготовку, процент meeting duration и примерное
  оставшееся время после первого устойчивого измерения.
- Пользователь может отменить job, удалить canonical model и повторить
  обработку после её verified reinstall.
- Existing transcript остаётся доступным до успешной atomic replacement.
- Во время active recording UI объясняет, что локальная транскрибация
  продолжится после встречи.

## Sibling Engine Contract {#sibling-engines}

- `FEAT-015` и `FEAT-016` являются sibling implementations одного
  `ITranscriptionEngine`.
- Общие job entities, normalized result, Markdown writer и recordings UI
  создаются один раз в `Application/Persistence/Desktop`.
- Первый реализуемый sibling вводит shared implementation; второй добавляет
  engine adapter и additive migrations.
- Session хранит exact engine id `local.whisper | remote.groq |
  remote.openrouter`, model id/hash и execution kind.
- Смена transcription mode влияет только на новые jobs.
- Active job продолжает работу с зафиксированным engine/model.
- Automatic fallback между local и remote execution не выполняется.

## Persistence And Artifacts {#artifacts}

- Local jobs используют provider-neutral statuses, queue ordering и atomic
  artifact lifecycle.
- Persisted checkpoint содержит session/job id, input hash, model/runtime hash,
  chunk index/time range, backend, attempts и partial result path.
- Retry применяется к worker crash, transient decode/backend failure и
  interrupted shutdown.
- Corrupt/missing model, insufficient memory/disk и repeated native crash
  переходят в `attention_required`.
- Final `<session-id>.transcript.md` и `.transcript.json` совместимы с
  `FEAT-015` normalized artifact contract.
- JSON дополнительно хранит local runtime version, model SHA-256, backend,
  processing duration и segment timestamps.
- Audio и предыдущий transcript не удаляются при failure/cancel.

## Privacy And Network Invariants {#privacy}

- Local inference не вызывает network endpoints.
- После verified model install полный E2E recording-to-transcript проходит с
  network disabled.
- Audio samples, chunks и transcript text остаются внутри app-owned/user-owned
  local storage и worker pipes.
- Model download является единственным разрешённым network path этого engine.
- Diagnostics и logs не содержат transcript text или raw audio.
- Remote sibling engine запускается только после отдельного выбора/opt-in из
  `FEAT-015`.
- Runtime не содержит telemetry SDK и не отправляет performance benchmarks.

## Verification {#verification}

- Native build inventory фиксирует exact source commit, compiler flags,
  architectures, linked libraries, licenses и SHA-256.
- ABI tests загружают каждый RID library и проверяют required C symbols.
- Model-download tests покрывают resume, cancel, disk full, bad hash,
  replacement и offline reuse.
- Golden RU/EN и минимум один additional-language fixture проверяют automatic
  language detection, word/segment timestamps и bounded transcript quality full
  `large-v3-turbo`.
- Windows CPU и macOS Metal E2E проходят на одном consented fixture.
- Windows Vulkan smoke подтверждает acceleration и CPU fallback.
- Two-hour fixture доказывает bounded chunk memory, progress, checkpoints и
  restart resume.
- Worker crash/OOM/backend failure не завершает main app и не повреждает audio.
- Starting a recording preempts worker и после finalization корректно
  возобновляет local job.
- Low-power and manual override states покрыты platform contract tests.
- Network-denied E2E после model install создаёт готовые Markdown/JSON.
- Package audit подтверждает отсутствие ASR model weights, CLI/server, Python,
  standalone ffmpeg, CUDA/ROCm/OpenVINO и лишних architectures.
- Windows x64 и macOS arm64 UI/functional parity matrix проходит.

## Acceptance {#acceptance}

FEAT-016 завершена, когда:

1. Пользователь может включить local mode, загрузить, проверить и удалить full
   `large-v3-turbo` без выбора ASR model.
2. Ready recording транскрибируется локально вручную и опционально
   автоматически без передачи audio во внешний сервис.
3. Windows CPU и macOS Metal являются рабочими baseline paths с безопасными
   fallbacks.
4. Worker isolation сохраняет main app при native crash/OOM и продолжает job с
   persisted checkpoint.
5. Recording lifecycle имеет приоритет над local inference и не теряет
   transcription progress.
6. Model, memory, disk, low-power и backend problems дают понятное состояние и
   действие пользователя.
7. Source-scoped word/segment results передаются в FEAT-017 и материализуются
   как совместимые speaker-aware artifacts.
8. Installer/DMG не содержат model weights, а native runtime имеет
   deterministic license/payload evidence.
9. После model install полный Windows/macOS E2E проходит при отключённой сети.

## Document Notes {#document-notes}

- 2026-08-30: Public catalog replaced with fixed full non-quantized
  `large-v3-turbo`; model picker and smaller fallback were removed, automatic
  language detection and word timestamps became mandatory for FEAT-017.
- 2026-07-30: Создан sibling work item для полностью локальной
  Windows/macOS-транскрибации. Выбран `whisper.cpp`, optional model download,
  `small` default, isolated worker, macOS Metal и portable Windows CPU baseline.

## External References {#external-references}

- [ggml-org: whisper.cpp](https://github.com/ggml-org/whisper.cpp)
- [ggml-org: whisper.cpp releases](https://github.com/ggml-org/whisper.cpp/releases)
- [ggml-org: whisper.cpp license](https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE)
- [OpenAI: Whisper](https://github.com/openai/whisper)
- [OpenAI: Whisper model card](https://github.com/openai/whisper/blob/main/model-card.md)
- [OpenAI: Whisper license](https://github.com/openai/whisper/blob/main/LICENSE)
