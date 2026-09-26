# WI-005: Адаптировать online transcription к speaker-aware pipeline

- Kind: `change`
- Canon action: `direct-edit`

## Outcome

Пользователь может включить online mode с Groq или OpenRouter; оба adapter
возвращают отдельные source results с word timestamps и проходят общий
speaker-aware pipeline без выбора remote model в normal settings.

## Specs

- Governing: `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`
- Governing: `spec://modules/app/FEAT-017-speaker-aware-transcription#modes.online`
- Constraint: `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`

## Scope

- In: cloud engines, consent, separate microphone/output requests, word
  timestamps, compatible OpenRouter routing, queue/chunking, usage provenance и
  cross-platform online setup.
- Out: local ASR implementation and shared diarization/turn materialization
  owned by `WI-006` and `WI-016`.

## Acceptance

- [x] Groq uses fixed `whisper-large-v3-turbo`, `verbose_json` and word/segment
  timestamps without a model picker.
- [x] OpenRouter selects only a route that supports `verbose_json` word
  timestamps; incompatible route fails with `timestamp_capability_missing`.
- [x] Microphone/output chunks are submitted as separate logical sources with
  preserved timeline offsets and silent ranges omitted deterministically.
- [ ] Online mode consent, keys, retry/cancel, usage and zero-network-before-
  opt-in contracts pass.
- [ ] FEAT-015 and FEAT-017 online integration acceptance passes on Windows and
  macOS after `WI-016` shared pipeline is available.

## Result

Implementation checkpoint; platform and manual acceptance remain open.

- Groq uses the fixed turbo profile and requests word + segment timestamps.
  Both adapters reject text-only success with `timestamp_capability_missing`.
- OpenRouter discovery now exposes only `openai/whisper-large-v3-turbo`; the
  adapter checks its current endpoint catalog before upload and accepts only
  reviewed `groq` / `deepinfra` endpoints. Every possible endpoint must also
  appear in the current ZDR catalog when required. Empty/unreviewed or
  privacy-incompatible endpoint sets fail before audio upload.
  STT ignores `provider.only/order/ignore`, so these are not used as routing
  guarantees. Final response word-timing validation is mandatory.
- Shared local diarization runs on separate microphone/output inputs and omits
  speech-free sources. Successful ASR checkpoints survive retry/restart and
  preserve per-source language, request id and usage.
- macOS combined gate: 649/649 automated tests. Four explicit live runs pass:
  Groq and OpenRouter each process EN output with PT and RU microphone inputs.
  Production adapters, durable queue and packaged local speaker worker produce
  `self`, `remote:1`, `remote:2`, ISO languages and atomic speaker artifacts;
  primary audio remains and processing source pointers are released.
- Live checks found provider language names and upstream 429 on the initial
  OpenRouter whisper-1 route. ISO normalization and the reviewed turbo route
  resolve these cases; frozen legacy model/privacy identity is preserved.
- Evidence: `artifacts/acceptance/FEAT-017/macos-arm64/online-verification.md`.
- The September 25 macOS solution gate passed 650/650 ordinary tests;
  real Groq/OpenRouter runs above remain separate evidence. Windows provider
  changes and build fixes have since been reconciled into the source baseline.
  Final online UI and provider-account acceptance remain open.
- Remaining: live retry/cancel/billing, Windows parity and visual provider/key
  setup. Published ZDR endpoint policy and pre-upload enforcement are checked;
  server-side data deletion and Groq account controls are not independently
  proven. No acceptance criterion is relaxed.
