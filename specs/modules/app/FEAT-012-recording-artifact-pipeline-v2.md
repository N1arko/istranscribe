---
status: active
---

# FEAT-012: Recording Artifact Pipeline V2 {#root}

## Простыми словами {#plain-language}

После записи пользователь должен получить один понятный аудиофайл со встречей, где слышны собеседники и его микрофон. Временные дорожки нужны для надёжности и восстановления, а их смешивание и сжатие выполняются самим приложением без установленного отдельно `ffmpeg`.

## Goal {#goal}

Сделать loss-resistant pipeline `capture → finalize → mix → compress → promote`, который формирует один Windows-friendly meeting artifact и никогда не теряет исходный звук при ошибке обработки.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#recording-artifact`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#root`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#root`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#root`

## Supersedes {#supersedes}

- v1 finalization/input selection behavior owned by `spec://common/PROP-004-meeting-session-and-data-model#pipeline`
- PATH-dependent post-recording compression behavior introduced after `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#recorder-engine.temp-artifacts`

## See also {#see-also.codec}

- Windows codec/container decisions for new recordings are governed by `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#root`.

## Scope {#scope}

### In scope {#scope.in}

- temporary output and microphone tracks;
- deterministic timeline alignment across capture legs;
- mixed primary artifact;
- Windows-native or app-bundled compression implementation;
- atomic artifact promotion and database update;
- fallback/recovery artifact behavior;
- source-track retention policy;
- progress/status surfaced to the desktop app;
- compatibility with manual and Ask-started sessions.

### Out of scope {#scope.out}

- transcription;
- speaker separation;
- audio editor;
- cloud upload;
- macOS encoder implementation;
- lossy re-encoding of old recordings.

## Artifact Contract {#artifact-contract}

### Primary artifact {#artifact-contract.primary}

- Each successfully finalized session exposes exactly one primary user-facing audio file.
- Windows release target format is `.m4a` with AAC-LC through a Windows-native or redistributed runtime controlled by the application.
- Windows v2 uses a fixed speech-first preset: AAC-LC, 48 kHz, 16-bit stereo PCM input and 128 kbps output; it is not exposed as a normal user setting.
- The primary artifact contains output and microphone when both were captured.
- Output-only and microphone-only sessions remain valid and produce the same primary container format.
- The file opens through the normal Windows shell on a clean supported machine.

### Source artifacts {#artifact-contract.sources}

- Capture writes source tracks to an app-owned temporary session directory.
- Source tracks remain until primary artifact fsync/close and repository promotion both succeed.
- Default successful flow deletes temporary source tracks after an atomic promotion checkpoint.
- Diagnostics/recovery mode may retain source tracks with a clear status and cleanup policy.
- User-facing folders are not filled with duplicate raw tracks by default.

### Transcription handoff {#source-retention}

- When the persisted transcription mode for a newly finalized recording is
  `local` or `online`, finalization creates a durable handoff for the separate
  output and microphone tracks before ordinary source cleanup.
- The handoff stores source role, path, hash and timeline/discontinuity metadata
  required by `spec://modules/app/FEAT-017-speaker-aware-transcription#retention`.
- Source processing copies remain app-owned and recoverable until the
  speaker-aware job reaches `completed`, `cancelled` or terminal cleanup.
- Primary artifact promotion and the user-visible `ready` state do not wait for
  transcription completion.
- Mode `off` keeps the ordinary post-promotion cleanup behavior.
- Cleanup never removes the primary artifact, the last completed transcript or
  source files still referenced by a recoverable active job.

## Mixing {#mixing}

- Output and microphone are converted to a shared sample rate/channel layout before mixing.
- Capture legs preserve timeline order across pause/resume and device continuity boundaries.
- Mixing applies headroom and a limiter or equivalent clipping protection.
- Missing intervals are represented as silence; legs are not concatenated in a way that shifts one source relative to another.
- Mixed duration matches recorded session duration within a documented codec/container tolerance.
- A failed source does not invalidate a healthy remaining source.

## Compression {#compression}

- Production compression must not depend on `ffmpeg.exe` or another executable being discoverable through system `PATH`.
- Preferred Windows contour uses platform Media Foundation or an app-bundled, versioned codec component.
- The implemented Windows contour uses the operating-system Media Foundation AAC transform through the application-owned NAudio boundary and an explicit MPEG-4 sink; no codec executable is resolved through `PATH`.
- Codec availability is assessed at startup/build packaging and observable in diagnostics.
- Compression runs outside the UI thread and reports bounded progress/state.
- Cancellation during app shutdown preserves recoverable inputs and resumes or recovers on next launch.

## Finalization Transaction {#finalization}

Canonical stages:

1. `stopping` — stop live adapters and close source writers;
2. `processing` — align/mix sources and encode a temporary primary file;
3. `verifying` — prove the output exists, is non-empty and has readable duration;
4. `promoting` — move/replace into final user folder and persist paths/status;
5. `ready` — expose primary artifact and clean eligible temp files;
6. `attention_required` — preserve readable fallback/source files and a clear error when processing cannot finish.

Repository state and file promotion use a recoverable checkpoint. A crash between stages must never result in both missing final file and deleted sources.

## Naming And Storage {#storage}

- Default directory remains organized by meeting date/session id through `ArtifactPathResolver` ownership.
- User-visible filename uses local date/time and sanitized source app label; session id is available for collision resistance without dominating the label.
- Existing custom recordings folder is honored.
- Changes to the folder affect new sessions and do not move existing files implicitly.
- Primary path becomes the canonical action target for `Open recording` and `Open folder`.

## Migration {#migration}

- Existing WAV/OGG/source-track sessions remain readable and listed.
- Existing database columns are migrated additively when a dedicated primary artifact path or processing status is required.
- Old sessions are not automatically re-encoded.
- New sessions never enqueue v1 transcription automatically.

## Verification {#verification}

- Unit tests cover alignment, mix headroom, clipping protection, missing source, multi-leg pause/resume and path promotion.
- Golden audio fixtures prove both output and microphone content are present in the primary file.
- Failure injection covers encoder unavailable, disk full, target folder missing, process termination during each stage and device loss.
- A clean Windows x64 environment without `ffmpeg` produces a playable primary artifact.
- Long recording smoke covers at least two hours and verifies bounded memory use during post-processing.
- Source-retention tests cover `off`, enabled handoff, restart recovery,
  cancellation and terminal cleanup without changing the primary ready file.

## Acceptance {#acceptance}

FEAT-012 is complete when:

1. Output + microphone produces one playable primary file containing both test signals.
2. Compression works on a clean release machine without PATH dependencies.
3. UI-thread responsiveness is maintained during finalization.
4. Crash/failure injection never loses all readable audio.
5. Repository and filesystem states recover deterministically after restart.
6. User folders contain the primary file and omit successful-flow temp duplicates.
7. Manual and Ask sessions share the same finalization path.
8. Enabled speaker-aware transcription receives recoverable separate source
   tracks, while `off` recordings retain the ordinary cleanup behavior.

## Document Notes {#document-notes}

- 2026-08-30: Added durable microphone/output handoff for speaker-aware
  transcription while preserving independent primary-artifact readiness.
- 2026-07-12: Automated artifact acceptance completed with 272 release tests, PATH-empty AAC encode, decoded output+microphone golden audio, continuity/restart/shutdown recovery, pause timeline preservation and the two-hour bounded-memory smoke. Clean-machine execution, real subprocess termination at every stage and measured UI-thread responsiveness remain explicit release gates in `spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#root`.
- 2026-07-12: Windows implementation fixed at Media Foundation AAC-LC 128 kbps with explicit `.m4a.partial` MPEG-4 staging; path-empty encode, output+mic golden decode, restart recovery and two-hour bounded-memory smoke passed.
- 2026-07-12: Windows AAC/`.m4a` decision superseded for new recordings by `FEAT-012.A`; this document remains the canon for mixing, transaction, recovery and loss-resistance behavior.
- 2026-07-11: Initial artifact pipeline v2 spec authored after audit found mix creation disabled and compression dependent on a system `ffmpeg` command.
