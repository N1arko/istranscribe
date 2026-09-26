---
status: active
---

# INFRA-003: Windows Audio Capture Foundation {#root}

## Простыми словами {#plain-language}

Эта спека фиксирует низкоуровневый Windows foundation для записи звука: какие capture adapters и watcher-ы существуют, какие события и артефакты они публикуют, как работает `prebuffer`, и как система обязана деградировать или сигнализировать об ошибках без silent breakage.

## Goal {#goal}

Задать implementation-ready platform contour для audio capture и device/process observation, на который смогут честно опереться `FEAT-002`, `FEAT-003`, `FEAT-005`, `FEAT-007` и будущий session manager.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`
- `spec://common/PROP-003-audio-capture-and-device-observation#rules`
- `spec://common/PROP-004-meeting-session-and-data-model#pipeline`
- `spec://common/PROP-004-meeting-session-and-data-model#rules`
- `spec://common/PROP-005-local-runtime-and-operations#rules`
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.rules`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#file-layout.session-dir`

## See also {#see-also}

- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#behavior`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#behavior`
- `spec://modules/app/FEAT-005-application-rules-and-discovery#behavior`
- `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#decisions`

## Scope {#scope}

### In scope {#scope.in}

- Windows-only audio substrate on top of `WASAPI / Core Audio` interop;
- adapters for `process output`, `device loopback` and `microphone` capture;
- process, audio-session and device watcher-ы;
- signal metering contract for detection and auto-discovery;
- in-memory `prebuffer` ring buffers and their promotion to recording;
- temp recording primitives, raw-track ownership and optional mixed artifact;
- capability matrix, degraded states and runtime failure surfaces;
- platform service boundaries between watchers, recorder engine and upper session logic.

### Out of scope {#scope.out}

- product rules for `Off / Ask / Auto / Privacy Pause / Force Record` as user-facing behavior;
- UI for whitelist, tray, ask confirmation or settings;
- SQLite schema, final transcript paths and secret storage;
- retry scheduler, recovery UX and log rotation;
- Fireworks request building and transcript materialization.

## Decisions {#decisions}

- Audio foundation для MVP использует один Windows contour: `WASAPI / Core Audio` interop с `process loopback`, `device loopback` и `microphone` adapters.
- Foundation разделяет наблюдение (`watchers`) и выполнение записи (`recorder engine`); watcher-ы не создают сессии и не принимают продуктовые решения.
- Raw output track, raw microphone track и optional mixed artifact считаются разными артефактами; raw tracks являются каноническим источником истины.
- `Process loopback` доступен только на Windows 11 или Windows 10 build `20348+`; при его отсутствии foundation обязан войти в явный degraded contour, а не silently fallback-нуться в device capture.
- `Prebuffer` хранится только в памяти и существует только для явно разрешённых источников, пока верхний слой не подтвердит запись.
- Любая потеря устройства, unsupported capability или capture fault поднимается как структурированное runtime event, а не остаётся только в логах.
- Browser scenarios опираются на whitelisted root process identity и его process tree; foundation не требует, чтобы audio session принадлежала ровно root PID.
- Foundation выполняет запись в temp-contour и передаёт наверх детерминированный набор temp artifacts; продуктовый session lifecycle и final save/promotion остаются вне ответственности этой спеки.

## Platform layering {#layering}

### Foundation modules {#layering.modules}

Канонический `INFRA-003` contour состоит из пяти платформенных зон:

1. `Process Watcher`
2. `Audio Session Watcher`
3. `Device Manager`
4. `Recorder Engine`
5. `Prebuffer Store`

Эти зоны могут быть реализованы в одном assembly или нескольких service classes, но их ответственности не должны смешиваться ad hoc.

### Ownership boundaries {#layering.boundaries}

- `Process Watcher` владеет наблюдением за root processes и process-tree resolution, но не выбирает, когда запись должна стартовать.
- `Audio Session Watcher` владеет Windows audio-session inventory, состояниями `Active / Inactive / Expired` и signal metering, но не решает, meeting это или нет.
- `Device Manager` владеет списком endpoint devices, default-device notifications и stable device identity.
- `Recorder Engine` выполняет конкретные capture requests и пишет temp audio artifacts, но не владеет whitelist rules, ask timeout или merge window.
- `Prebuffer Store` владеет in-memory ring buffers и их flush/discard semantics.
- Upper layers вне этой спеки владеют `MeetingSession`, recording statuses, user confirmation, device-switch policy choice и final artifact promotion.

## Source model {#source-model}

### Canonical source kinds {#source-model.kinds}

Foundation обязана понимать три первичных source kinds:

- `process_output`
- `device_loopback`
- `mic`

`mixed` не является самостоятельным Windows capture adapter. Это производный artifact, который может быть собран из raw tracks после остановки или в финализации, если upper layer запросил mixed output.

### Capture request contract {#source-model.request}

Каждый активный recorder запускается по immutable capture request, который обязан содержать:

- `session_id` или иной stable runtime identifier будущей `MeetingSession`;
- `mode` с одним из значений `auto | ask | manual`;
- набор requested source kinds;
- root process identity для `process_output`, если этот source включён;
- concrete endpoint id для `device_loopback` и `mic`, если source зафиксирован настройками;
- resolved temp session directory;
- `prebuffer_seconds`, если для данного flow prebuffer разрешён;
- флаг, нужен ли `mixed` artifact после capture.

Upper layer обязан передавать уже разрешённый capture plan. Recorder engine не должен сам решать, писать ли process output вместо device loopback или включать ли microphone.

### Device identity rules {#source-model.device-identity}

- Канонический device identity для output и microphone tracks: `MMDevice ID`.
- Human-readable device names нужны для UI и логов, но foundation опирается на стабильный `MMDevice ID`.
- Если устройство временно исчезает и возвращается с тем же `MMDevice ID`, foundation рассматривает это как возврат того же logical device.
- Если ОС выдаёт новый `MMDevice ID`, это считается новым device instance, даже если display name совпадает.

## Observation pipeline {#observation}

### Process Watcher {#observation.process-watcher}

`Process Watcher` обязан:

- перечислять и отслеживать running processes, совпадающие с whitelist rules;
- определять root process и его актуальное дерево потомков;
- публиковать start/exit/restart changes для root processes;
- публиковать discovery candidates для non-whitelisted playback processes, если это требуется `FEAT-005`;
- игнорировать predefined exclusions, service/system processes и уже whitelisted processes в auto-discovery contour.

Правила:

- Browser scenarios матчатся по whitelisted browser root process; child processes считаются частью того же meeting source.
- Root process restart трактуется как новое runtime observation, даже если executable name не изменился.
- Effective process observation latency не должна превышать `1 секунду` в steady state.

### Audio Session Watcher {#observation.audio-session-watcher}

`Audio Session Watcher` обязан:

- получать audio sessions для render endpoints;
- соотносить audio sessions с whitelisted root process или discovery candidate process tree;
- отслеживать состояния `Active`, `Inactive`, `Expired`;
- публиковать signal-level snapshots, пригодные для сравнения с `recording.silence_threshold_dbfs`;
- уметь отличать отсутствие подходящей audio session от её временной неактивности.

Правила:

- Detection и auto-discovery используют только audio sessions, которые можно соотнести с наблюдаемым process context; анонимный system mix сам по себе не считается process candidate.
- Если root process существует, но подходящая audio session ещё не появилась, foundation публикует это как `process-present / session-missing`, а не как error.
- Signal metering нормализуется в `dBFS`; upper layers не работают с device-specific raw meter units.
- Effective meter refresh cadence не должна быть хуже `250 мс`.

### Detection snapshots {#observation.snapshots}

Канонический output observation layer для upper modules - это snapshot/event stream, в котором для каждого наблюдаемого source context доступны минимум:

- `observed_at_utc`
- `root_process_name`
- `root_process_id`
- `audio_session_state`
- `signal_level_dbfs`
- `render_device_id`, если применимо
- `is_whitelisted`
- `is_process_tree_match`

`FEAT-002` и `FEAT-005` опираются именно на этот signal layer, а не на низкоуровневые COM callbacks напрямую.

### Device Manager {#observation.device-manager}

#### Behavior {#device-manager.behavior}

`Device Manager` обязан:

- перечислять active render endpoints и capture endpoints;
- отслеживать default output device и default microphone changes;
- отслеживать появление, исчезновение и state changes endpoint devices;
- публиковать актуальный device inventory без перезапуска приложения;
- отдавать stable snapshots для settings UI, recording coordinator и diagnostics.

Правила:

- Device inventory обязан различать минимум: `render output devices` и `capture microphone devices`.
- Device notifications должны обрабатываться event-driven через Windows device notifications; после resume from sleep foundation обязана выполнить полную resync inventory.
- If `auto_discover_output` or `auto_discover_mic` is disabled, Device Manager всё равно отслеживает inventory; флаг влияет на product behavior, а не на саму платформенную способность видеть изменения.

## Recorder engine {#recorder-engine}

### Adapter set {#recorder-engine.adapters}

Recorder engine обязан предоставлять три adapter family:

1. `Process loopback adapter`
2. `Device loopback adapter`
3. `Microphone capture adapter`

Каждый adapter:

- стартует и останавливается независимо;
- пишет свой raw artifact в temp session directory;
- публикует structured runtime state (`starting`, `running`, `stopping`, `completed`, `faulted`);
- не скрывает device-loss или access faults.

### Process loopback adapter {#recorder-engine.process-loopback}

Правила:

- Основан на Windows `process loopback` capture и обязан захватывать root process вместе с его дочерними процессами.
- Недоступен на Windows builds ниже `20348`; в таком случае adapter обязан возвращать `unsupported` до начала записи.
- Не должен silently fallback-нуться в `device_loopback`, потому что это меняет capture semantics.
- Если target process завершается во время записи, adapter завершает capture cleanly и поднимает runtime event о stop cause.

### Device loopback adapter {#recorder-engine.device-loopback}

Правила:

- Захватывает весь render mix выбранного output endpoint.
- Требует concrete render endpoint id, разрешённый текущими device settings или policy resolution.
- Если endpoint исчезает или становится unusable, adapter обязан выдать `device_lost` fault или clean stop с указанием причины; продолжение записи решает upper layer.

### Microphone adapter {#recorder-engine.microphone}

Правила:

- Захватывает выбранный capture endpoint как отдельный raw track.
- Не должен зависеть от наличия output capture; mic-only recording является канонически поддерживаемым режимом.
- Если microphone becomes unavailable, foundation обязана выдать явный runtime event; output capture при этом не должен теряться автоматически, если сам остаётся здоровым.

### Temp artifact ownership {#recorder-engine.temp-artifacts}

- Recorder engine пишет raw files в temp session directory, который разрешается persistence layer.
- Канонические имена raw artifacts в рамках session temp directory: `output.wav`, `mic.wav`; `mix.wav` создаётся только если upper layer запросил mixed artifact.
- Пока session не перешла в finalized state, эти файлы считаются temp-owned artifacts, даже если имена уже совпадают с финальными.
- Foundation обязана закрыть все file handles до передачи control в finalize/promotion path.

### Mixed artifact policy {#recorder-engine.mixed-artifact}

- Raw tracks являются обязательными первичными артефактами при multi-source capture.
- `mix.wav` является optional derived artifact и не заменяет raw tracks.
- Mixed artifact не должен быть prerequisite для сохранения сессии; при failure mix build raw tracks всё равно считаются валидным результатом записи.
- Для одноисточниковой записи derived mix не создаётся.

## Prebuffer store {#prebuffer}

### General rules {#prebuffer.rules}

- `Prebuffer` реализуется как отдельный ring buffer на каждый разрешённый source.
- Буфер существует только в оперативной памяти.
- Буфер ведётся только для sources, разрешённых текущим `Ask` capture plan.
- Default duration: `15 секунд`; supported values: `5`, `10`, `15`, `30` секунд.

### Promotion semantics {#prebuffer.promotion}

- Пока пользователь не подтвердил `Ask`, foundation не создаёт disk artifacts для prebuffer audio.
- При подтверждении foundation flush-ит доступные buffered frames в начало temp raw track соответствующего source и продолжает запись без разрыва.
- Если buffered audio меньше requested duration, flush-ится только реально накопленный хвост.
- При отказе или ask-timeout buffer полностью discard-ится и затирается из оперативной памяти; disk side effects не допускаются.

### Scope boundary {#prebuffer.boundary}

- `INFRA-003` владеет тем, как buffer хранится и переносится в raw track.
- `FEAT-002` владеет тем, когда Ask стартует, сколько длится timeout и какой ответ считается default.

## Capability and degraded model {#capability}

### Capability matrix {#capability.matrix}

Foundation обязана публиковать platform capability snapshot с минимум следующими флагами:

- `process_loopback_supported`
- `device_loopback_supported`
- `microphone_capture_supported`
- `audio_session_observation_supported`
- `device_notifications_supported`

Для MVP canonical states такие:

- `full`: все пять capability flags доступны;
- `degraded`: отсутствует `process_loopback_supported`, остальные foundation services продолжают работать;
- `blocked`: отсутствует базовая Windows host capability или foundational audio services не могут быть инициализированы вообще.

### Degraded semantics {#capability.degraded}

- При `degraded` state foundation обязана сохранить:
  - `device_loopback`
  - `microphone capture`
  - `device inventory and notifications`
  - manual flows, которые могут быть собраны верхним уровнем на этих primitives
- Foundation не должна сама запрещать ручную запись по устройствам только потому, что process loopback недоступен.
- Upper layer обязан получать explicit unsupported reason, если запрашивает `process_output` capture на machine без `process_loopback_supported`.

## Runtime failure model {#failure-model}

### Failure classes {#failure-model.classes}

Каждый capture/runtime fault должен быть отнесён минимум к одному из классов:

- `unsupported`
- `access_denied`
- `device_lost`
- `process_exited`
- `session_observation_lost`
- `io_failure`
- `unexpected_runtime_failure`

### Failure handling rules {#failure-model.rules}

- Fault всегда публикуется как structured event с временем, affected source и machine-readable code.
- Fault никогда не должен silently discard-ить уже записанный raw audio.
- Если ошибка затрагивает только один source из multi-source recording, foundation обязана сохранить состояние остальных source adapters и передать наверх факт частичной деградации.
- Если foundation больше не может продолжать ни один active source, это поднимается как terminal recorder failure.
- Диагностическое логирование и recovery jobs остаются соседней ответственностью `INFRA-004`, но `INFRA-003` обязан отдать ей достаточно структурированной информации.

## Cross-module boundaries {#boundaries}

### What INFRA-003 owns {#boundaries.owned}

Эта спека владеет:

- Windows audio interop contour;
- watcher-ами процессов, audio sessions и устройств;
- capture adapters и их temp artifact contract;
- in-memory prebuffer;
- capability matrix и runtime error surfaces capture layer.

### What INFRA-003 does not own {#boundaries.not-owned}

Эта спека не владеет:

- rule evaluation для `Off / Ask / Auto / Privacy Pause`: `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#behavior`
- manual commands и tray UX: `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#behavior`
- whitelist editing и auto-add user policy: `spec://modules/app/FEAT-005-application-rules-and-discovery#behavior`
- выбор пользовательской continuity policy при смене устройств: `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior`
- storage schema и final artifact placement: `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- diagnostics persistence, cleanup jobs и crash recovery: `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#decisions`

## Acceptance {#acceptance}

Спека достаточна для реализации, если одновременно выполняются все условия:

- Foundation layering между watcher-ами, recorder engine и upper session logic описан явно и не оставляет ownership ambiguity.
- Source kinds, capture request contract и device identity определены настолько, чтобы adapters можно было реализовать без гадания о входах и выходах.
- Process, audio-session и device observation описаны как platform signals с конкретным минимумом полей и latency expectations.
- `Prebuffer` semantics полностью определены: memory-only, per-source, confirm flush, decline discard.
- Raw tracks и optional mixed artifact разведены как отдельные результаты, а temp artifact contract согласован с `INFRA-002`.
- Capability matrix и degraded semantics не допускают silent fallback from process capture to device capture.
- Runtime failure classes и propagation rules достаточны, чтобы `FEAT-002`, `FEAT-007` и `INFRA-004` могли строить session behavior и diagnostics поверх capture layer.
- Scope boundaries с `FEAT-002`, `FEAT-003`, `FEAT-005`, `FEAT-007`, `INFRA-001`, `INFRA-002` и `INFRA-004` явные и не пересекаются.

## Document Notes {#document-notes}

- 2026-04-02: Initial audio capture foundation spec authored from the MVP TZ.
- 2026-04-05: Спека переписана в implementation-ready канон: добавлены platform layering, source/request model, process/audio-session/device watchers, recorder-engine contract, prebuffer promotion rules, capability matrix, runtime failure model и явные cross-module boundaries.
