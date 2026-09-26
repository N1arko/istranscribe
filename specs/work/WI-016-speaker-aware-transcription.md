# WI-016: Реализовать общий speaker-aware transcription pipeline

- Kind: `implement`
- Canon action: `new-spec`

## Outcome

Любая новая транскрипция использует один из трёх режимов, сохраняет известного
пользователя как `self`, разделяет голоса собеседников и создаёт единый
speaker-aware JSON/Markdown без выбора моделей в normal settings.

## Specs

- Governing: `spec://modules/app/FEAT-017-speaker-aware-transcription#root`
- Affected: `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#source-retention`
- Affected: `spec://modules/app/FEAT-013-minimal-desktop-experience#settings`
- Affected: `spec://modules/app/FEAT-010.A-release-v2-localization#resources`
- Constraint: `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`

## Scope

- In: mode state/migration, shared source-aware request, source-track handoff,
  app-managed diarization runtime/assets, cross-chunk speaker identity, word
  assignment, speaker-turn materialization, RU/EN UI resources, recovery and
  Windows/macOS contract tests.
- Out: Groq/OpenRouter HTTP adaptation owned by `WI-005`, full
  `large-v3-turbo` ASR adaptation owned by `WI-006`, transcript editor,
  summaries and live captions.

## Dependencies

- `WI-005` supplies online engines with required word timestamps and compatible
  routing.
- `WI-006` supplies the fixed local ASR engine and verified model lifecycle.
- Shared contracts and deterministic fixtures may be implemented before live
  engine acceptance; FEAT-017 product acceptance requires all three WI.

## Acceptance

- [x] Persisted/UI mode is exactly `off | local | online`; existing settings
  migrate deterministically and no prior opt-in causes a new online upload.
- [x] Normal settings contain the three localized choices and omit ASR model,
  diarization model, diarization toggle and speaker-count controls.
- [x] Finalization hands microphone/output source tracks to enabled jobs and
  cleans them only after recoverable terminal processing.
- [x] Microphone words map to `self`; output words map to stable remote ordinals
  across chunks and restart.
- [ ] Pinned app-managed diarization assets install without user registry
  credentials and have hashes/licenses/payload evidence.
- [x] `speaker-transcript/v1` JSON and localized Markdown materialize atomically
  with deterministic speaker turns and overlap handling.
- [x] Speaker-aware completion fails closed on missing diarization or word
  timestamps; primary audio and previous transcript remain readable.
- [x] RU, EN and one additional supported-language fixture prove automatic
  language detection independent from UI language.
- [ ] Shared automated gates pass on current platform; platform-specific suites
  pass on Windows x64 and macOS arm64 before completion.

## Result

Implementation checkpoint; platform and manual acceptance remain open.

- Implemented shared modes, safe effective-model migration, separate source
  handoff with continuity offsets, pinned local speaker worker/assets,
  cross-chunk identity replay, mandatory word assignment, localized turns,
  per-source language/request/usage provenance and atomic publication.
- Source cleanup is guarded by a SQLite write transaction and canonical owned
  directory checks. A pending/new job prevents cleanup; an interrupted cleanup
  can retry. The real local queue test also verifies released source pointers
  and the continued existence of primary audio.
- macOS solution: **636/636** automated tests. Separate opt-in real-model queue:
  **1/1**, using the worker inside the generated `.app`, full turbo, public
  two-speaker English output plus synthetic Portuguese microphone. It produces
  `self`, `remote:1`, `remote:2`, both detected languages and two completed local
  attempts. Three-language native fixture RU/EN/PT passes independently.
- Verified source-only replay avoids repeated successful ASR requests. Three
  cross-chunk voices and overlap/tie rules are deterministic synthetic tests;
  this is not real multi-hour meeting quality evidence.
- macOS DMG and source/native/license inventories verified. New runtime has
  14 dependency license materials plus the source/model notice; on-demand
  speaker weights total 32,527,278 bytes. Windows C# app cross-build on macOS
  passes with zero warnings/errors; Windows native execution was not run.
- Evidence: `artifacts/acceptance/FEAT-017/macos-arm64/verification-summary.md`.
- Online follow-up: Groq/OpenRouter each pass explicit EN/PT and EN/RU real
  source-aware queue runs on macOS; combined suite now 649/649. The corresponding
  Windows provider changes are included in the source baseline. Evidence is
  linked from the macOS online verification report.
- September 25 macOS solution gate passed 650/650 ordinary tests
  and spec verification passed 41 specs / 16 work items. A separate real 32-second
  two-chunk diagnostic retained `self`, `remote:1` and `remote:2` in both chunks,
  with three uncertain word groups explicitly labelled `speaker_unresolved`.
  The first opt-in test had assumed zero unresolved words; its assertion now
  follows the governing spec, but the revised real opt-in test was not rerun
  after temporary model/runtime assets were cleared.
- Windows follow-up: native speaker/Whisper CPU/Vulkan outputs were preserved
  and verified. Development MSIX packaging and dependency/license verification
  passed using a short staging path. The speaker worker found two speakers in
  the fixture, and the real durable local queue passed. The ordinary Windows
  and shared suites passed 893 checks in the final runs; the historical installer
  acceptance kit was excluded because its old package fixtures were absent.
  One window-transition case passed on rerun after an initial timing failure.
- Remaining: online platform UI/retry/cancel acceptance, real multi-chunk/overlap/three-
  speaker quality and user visual review. These gates remain mandatory.
