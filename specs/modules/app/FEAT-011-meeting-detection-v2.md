---
status: active
---

# FEAT-011: Meeting Detection V2 {#root}

## Простыми словами {#plain-language}

Приложение должно отличать встречу от ролика, музыки и системного звука достаточно хорошо, чтобы prompt не раздражал пользователя. Detection v2 собирает несколько локальных признаков, оценивает уверенность и всегда спрашивает разрешение перед записью.

## Goal {#goal}

Реализовать explainable multi-signal meeting detection для Windows и macOS с
Ask-only policy, app profiles, browser-specific evidence, privacy-safe observation
и измеримым false-positive quality gate.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#detection-principles`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.snapshots`
- `spec://modules/app/FEAT-005-application-rules-and-discovery#root`

## Supersedes {#supersedes}

- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection`
- automatic `Auto` start behavior from `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap`

## See also {#see-also.validation}

- `spec://modules/app/FEAT-011.A-consent-aware-windows-validation#root` governs
  live-verification and release-completion policy. Signal, scoring, privacy and
  lifecycle behavior remain governed here.

## Scope {#scope}

### In scope {#scope.in}

- normalized meeting signal model;
- signal providers and time-window aggregation;
- local speech activity estimation for render and microphone paths;
- app/window/UI Automation evidence;
- confidence score and explainable decision breakdown;
- Ask prompt eligibility and candidate suppression;
- app profile registry;
- initial profiles for Zoom, Microsoft Teams, Google Meet, Яндекс Телемост and Контур.Толк;
- generic unknown-app fallback;
- shadow decision mode and local quality log;
- deterministic tests and live Windows validation matrix.
- cross-platform audio observation lifecycle policy.

### Out of scope {#scope.out}

- recording without explicit confirmation;
- cloud classification or speech upload;
- speech-to-text for detection;
- browser extension;
- calendar integration;
- platform-native audio capture primitives, governed by platform specs.

## Signal Model {#signal-model}

Each observation frame may contain:

- normalized app profile id and process/root tree identity;
- render audio session state and rolling energy;
- render speech probability over a bounded window;
- microphone endpoint activity and microphone speech probability;
- meeting-specific window title or UI Automation evidence;
- app foreground/background state as weak context;
- process/window lifetime and recent restart continuity;
- current device identities;
- previous user policy and recent decision for this logical candidate.

Signal providers emit facts. They do not decide whether a meeting exists and do not start recording.

## Audio Observation Lifecycle {#audio-observation-lifecycle}

Detection использует двухступенчатое наблюдение:

1. idle observation собирает process, profile, window/Accessibility и metadata
   активных audio sessions без открытия sample-bearing system-audio/process tap
   или microphone capture;
2. render и microphone observation включаются, когда metadata и UI evidence
   создают eligible meeting-кандидата.

Eligible candidate существует, когда:

- known dedicated meeting app имеет active attributed render; или
- browser root имеет meeting-specific window/Accessibility evidence.

Browser media playback и identity процесса без этих признаков не открывают process tap
или microphone capture. Pending Ask удерживает observation sources конкретного candidate.
Подтверждённая автоматическая запись удерживает candidate sources, требуемые для
session continuity и automatic finish. Ручная запись использует только recording-owned
sources; detection observation не создаёт независимый idle capture.
После Skip, потери последнего eligible candidate, service pause или завершения записи
observation sources закрываются не позднее чем через `5 секунд`, если другой eligible
candidate или recording session отсутствует. Короткий capability probe допустим только
при явной setup/retry-проверке пользователя и всегда закрывается после результата.

Microphone speech остаётся дополнительным confidence evidence. Первичное создание
candidate не зависит от постоянно открытых audio capture. Windows и macOS применяют
одинаковую eligibility policy и platform-specific capture lifecycle. Metadata-only
session observation не считается audio capture и не показывает privacy indicator.

## Privacy Boundary {#privacy}

- VAD input remains in a bounded in-memory ring buffer.
- Before confirmation no audio file is created and no sample bytes are written to diagnostics.
- Diagnostics store signal names, normalized numeric summaries, score, decision reason and app profile id.
- Window evidence is reduced to matched rule identifiers; raw sensitive titles are excluded from normal logs.
- No network call is allowed in the detection path.

## App Profiles {#app-profiles}

### Profile contract {#app-profiles.contract}

An app profile can declare:

- process/executable matchers;
- process-tree behavior;
- known window/UI Automation evidence rules;
- browser host/title/UIA matchers where a service runs in a browser;
- evidence weights and hard exclusions;
- capture source preference;
- friendly display name and icon key.

`ProcessTreeBehavior` applies consistently to window, render-session and attributed
speech evidence. A `RootOnly` profile cannot inherit child-process audio/VAD facts.

Profiles are data-first and versioned. Adding a normal profile must not require changes to the central scoring state machine.

### Initial coverage {#app-profiles.initial}

- Zoom desktop;
- Microsoft Teams desktop and supported browser meeting evidence;
- Google Meet in Chromium- and Firefox-family browsers, including Zen Browser;
- Яндекс Телемост desktop and browser;
- Контур.Толк desktop and browser;
- generic dedicated meeting app added by the user;
- generic browser candidate with stricter evidence threshold.

Exact executable and UI Automation selectors are verified against installed current clients during implementation and recorded in profile fixtures.

## Confidence Model {#confidence}

### Decision bands {#confidence.bands}

- `0–39 ignored` — candidate remains internal;
- `40–69 suspected` — observation continues without user interruption;
- `70–100 ask` — prompt becomes eligible after temporal stability rules.

Weights and thresholds are configuration constants owned by the scorer, covered by fixtures and absent from normal user settings.

### Evidence rules {#confidence.evidence}

- Meeting-specific call controls/window evidence is strong positive evidence.
- Sustained render speech is stronger than raw render energy.
- Microphone speech or active capture adds positive evidence.
- Alternating render/microphone speech adds conversational evidence.
- Dedicated known meeting app identity adds context, but identity alone cannot trigger a prompt.
- Browser audio without meeting-specific evidence remains below the Ask band.
- Short sounds, notification processes, media players and technical exclusions apply negative evidence or hard exclusion.
- A muted/listen-only call can reach Ask through strong meeting UI evidence plus sustained remote speech.

### Temporal behavior {#confidence.temporal}

- Ask eligibility requires the score to remain in the Ask band for at least `3 seconds`.
- A single frame cannot show a prompt.
- Score uses hysteresis so short drops do not repeatedly create/destroy a candidate.
- `Skip` suppresses the same logical candidate until it loses meeting eligibility for at least `20 seconds` or the meeting UI/process boundary ends.
- A closed/timed-out prompt resolves to `skip`.
- At most one prompt or active session exists at a time.

## User Policy {#user-policy}

- Global automatic policy is `ask` when the service is listening.
- Per-profile policy values are `ask | ignore`.
- `Записать` applies only to the current candidate.
- `Пропустить` suppresses the current candidate window.
- `Игнорировать это приложение` persists `ignore` and is reversible in settings.
- Unknown dedicated apps can be promoted to a user profile after positive confirmation.
- Release v2 does not expose `always auto-record`.

## Shadow Mode And Diagnostics {#shadow-mode-and-diagnostics}

- Shadow mode runs the full scorer and records decisions without showing prompts or starting recordings.
- A bounded local decision log supports replay fixtures and manual quality review.
- Shadow mode never leaves user-facing meeting sessions.
- The shipping Windows app can enter shadow mode through the operational
  `--detection-shadow` switch or `ISTRANSCRIBE_DETECTION_MODE=shadow` environment value.
- Release rollout may compare v1 and v2 detection decisions, while only v2 owns prompt behavior after cutover.

## Verification Matrix {#verification}

Positive scenarios include each initial app profile, muted participant, listen-only meeting, multiple speakers, app restart and output device switch.

Negative scenarios include YouTube, streaming music, local media, browser notification, system sound, short voice message, game audio, idle meeting-app window and background updater processes.

Initial quality gates:

- supported live meeting produces an Ask prompt within `15 seconds` of stable meaningful meeting activity;
- sound shorter than `3 seconds` never produces a prompt;
- curated negative replay suite produces zero Ask decisions;
- reference `8-hour` mixed background workload produces at most one false prompt;
- repeated Skip for one meeting produces one prompt maximum;
- no pre-confirmation audio file exists after any negative/timeout scenario.

Live evidence is stored as `feat-011-live-v1` JSON under
`artifacts/acceptance/FEAT-011/windows-x64/<run-id>/` together with a privacy-gated host
log, dependency manifest, schema and SHA-256 manifest. A positive `passed` outcome
requires an observed x64 target, client version/hash bound to the prompt root PID,
matching desktop/browser surface, runtime-published Ask within the fixed `15 seconds`,
zero recording-capture starts/files/session rows, a matching explainable decision-log record, no
decoded raw-window-title match, no payload-like audio data and no managed HTTP event.
A privacy-failing source log is replaced by a synthetic artifact entry and removed with
the isolated runtime root.

`live-matrix.json` covers the eight required profile/surface scenarios, accepts only
fresh evidence from one exact dependency build and remains `incomplete` for missing,
stale, invalid, failed, blocked or not-run rows. Missing operator consent and clients
are recorded as `not_run` or `blocked`; neither status can satisfy acceptance.

## Acceptance {#acceptance}

FEAT-011 is complete when:

1. Signal providers, scorer, policy and lifecycle are separate tested responsibilities.
2. All initial profiles have deterministic fixtures; client-specific evidence level and
   live-observation policy are governed by `spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels`.
3. Browser playback alone cannot reach the Ask band.
4. Ask-only behavior and suppression rules match this spec.
5. Privacy boundary tests prove no disk/network audio before confirmation.
6. Replay verification meets the quality gates; live verification follows
   `spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy`.
7. Decision diagnostics explain which evidence produced each score.
8. Idle listening keeps system-audio/process taps and microphone capture closed;
   candidate, Ask, recording and teardown transitions follow
   `#audio-observation-lifecycle` on Windows and macOS.

## Document Notes {#document-notes}

- 2026-08-10: Candidate-gated lifecycle расширен на sample-bearing render/process taps:
  обычный browser audio и idle listening не должны показывать system-audio indicator.
- 2026-08-02: Зафиксирован candidate-gated microphone lifecycle для обеих платформ:
  idle listening не должен удерживать системный microphone capture.
- 2026-07-12: Installed Zen 1.21.6b is now the explicit browser in current-build Teams/Meet/Телемост/Контур preflights. A separate `compatibility-index.json` reports seven `environment_observed` surfaces and one `contract_verified` Контур.Толк desktop surface; all eight production-path contract checks pass.
- 2026-07-12: Validation/completion policy moved to `FEAT-011.A` after the user declined staged Zoom and Контур.Толк calls and named Zen Browser as the primary browser. Product support remains in scope; client-specific live status is now reported separately from deterministic compatibility.
- 2026-07-12: Acceptance evidence was hardened around the fixed 15-second gate, candidate surface/root-PID binding, conditional passed-schema, decoded privacy inspection, redacted failed logs, full dependency hashes and an eight-row same-build/freshness aggregator. Current matrix is intentionally `incomplete`: seven rows are `not_run: operator_consent_missing`; Контур.Толк desktop is `blocked: client_process_not_found`.
- 2026-07-12: Added a durable Windows x64 acceptance runner and `feat-011-live-v1` schema. Current machine evidence records Яндекс Телемост desktop and Google Meet browser as `not_run: operator_consent_missing`, and Контур.Толк desktop as `blocked: client_process_not_found`. The original requirement for a fresh all-client live pass was later replaced by `FEAT-011.A`.
- 2026-07-11: Windows live matrix зафиксировала idle-негативы для Zoom, Teams, Телемоста и Google Meet landing; positive Ask прошёл на реальных Zoom (`6 s`) и Teams (`7 s`) со входом в пустую встречу и выключенными camera/mic во время detection gate. В profiles добавлены наблюдённые русские selectors `Zoom Конференция`, `Включить звук`, `Завершение`, `Собрание с организатором`, `Включить микрофон`, `Выйти`. Positive Телемост/Meet и live Контур.Толк остаются открытой частью acceptance matrix.
- 2026-07-11: Initial detection v2 spec authored after audit showed v1 equated a meeting with active above-threshold process audio.
