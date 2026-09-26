# WI-006: Перевести local transcription на full large-v3-turbo

- Kind: `change`
- Canon action: `direct-edit`

## Outcome

Пользователь включает local mode, приложение загружает fixed full
`large-v3-turbo` и возвращает multilingual source results с word timestamps для
общего speaker-aware pipeline на Windows и macOS.

## Specs

- Governing: `spec://modules/app/FEAT-016-local-whisper-transcription#root`
- Governing: `spec://modules/app/FEAT-017-speaker-aware-transcription#modes.local`
- Constraint: `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`
- Affected: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution`

## Scope

- In: whisper.cpp runtime, canonical model manifest/download/migration, worker,
  automatic language detection, source-aware word timestamps, resource policy,
  queue and local-mode setup.
- Out: remote provider implementation and shared diarization/turn
  materialization owned by `WI-005` and `WI-016`.

## Acceptance

- [x] Public catalog/model picker is removed and full non-quantized
  `ggml-large-v3-turbo.bin` is the only canonical ASR model.
- [x] Existing small/base/medium preferences migrate without silently selecting
  a smaller runtime fallback or deleting cached files.
- [x] Local engine returns per-source detected language and required word/
  segment timestamps.
- [ ] Model download, verification, storage cleanup, resource errors, worker
  isolation and network-denied reuse pass for the canonical model.
- [ ] FEAT-016 and FEAT-017 local integration acceptance passes on Windows and
  macOS after `WI-016` shared pipeline is available.

## Result

Implementation checkpoint; platform and manual acceptance remain open.

- Catalog v2 contains only full non-quantized `ggml-large-v3-turbo.bin`,
  1,624,555,275 bytes, SHA-256
  `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`.
  New jobs use it regardless of legacy small/base/medium selection. Previously
  frozen jobs keep their original identity; cached files are not deleted.
- whisper.cpp now emits word-sized timestamp items with auto language. Native
  RU/EN/PT and isolated RU/EN worker tests pass using full turbo on Metal.
  The durable source-aware queue passes with real English + Portuguese audio,
  `self` and two remote voices. Empty/control-only Whisper items are filtered
  before strict application word validation; a regression fixture covers this.
- 636/636 macOS solution tests and format pass. Windows C# cross-build passes
  on macOS (0 warnings/errors), without a Windows runtime claim.
- The macOS release script now composes worker, Whisper, speaker runtime and
  licenses and produces a verified signed `.app`/DMG. It pins the same official
  .NET 10.0.11 runtime as local-runtime staging; the old Homebrew .NET/Brotli
  inventory was replaced by the verified redistributed graph. Source composition
  hashes and final signed bundle hashes are recorded as separate stages.
- Evidence: `artifacts/acceptance/FEAT-017/macos-arm64/verification-summary.md`.
- Follow-up: updated macOS app/DMG verified; full local queue passes with both
  ASR and diarization workers under an OS `deny network*` sandbox (13s, 1/1).
  A control TCP connection is denied by the OS and succeeds outside the policy;
  parent model-download handler observes zero calls. Host networking remains
  enabled. Evidence: macOS `online-verification.md` and
  `reports/speaker-real-queue-os-network-denied.trx`.
- Windows preparation: Release build and development MSIX verification passed
  after reconciling the native/build fixes. Full turbo passed Vulkan RU/EN,
  packaged-worker and durable speaker-aware queue checks. CPU passed with an
  existing base model; a full-turbo CPU run was stopped after 28 minutes without
  a final result. Existing models and user data were preserved.
- Remaining: full-model CPU acceptance; full-model installation and
  resource/cancellation matrix on clean platforms; Windows OS-level offline
  acceptance and representative long-meeting quality/visual review. The initial
  dependency-level denied HTTP client in the local queue test is not an OS
  network isolation test; the separate follow-up above adds worker OS isolation.
  No app installation or live capture was performed.
