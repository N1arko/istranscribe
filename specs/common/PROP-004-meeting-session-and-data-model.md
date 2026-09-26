---
status: superseded
---

# PROP-004: Meeting Session And Data Model {#root}

## Простыми словами {#plain-language}

Этот документ фиксирует главную доменную модель приложения: что считается meeting session, какие у неё поля и статусы, как session проходит путь от detection до saved recording, а также как для неё формируются transcription artifacts, Markdown и JSON outputs.

## Goal {#goal}

Зафиксировать каноническую data model, lifecycle и output contract, общие для UI, storage, capture и transcription.

## Superseded by {#superseded-by}

- Release v2 recording artifact behavior is governed by `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`.
- Release v2 transcription deactivation and future contract are governed by `spec://modules/app/FEAT-014-transcription-extension-seam#root`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`
- `spec://common/PROP-003-audio-capture-and-device-observation#rules`

## Scope {#scope}

### In scope {#scope.in}
- `MeetingSession` and `AppRule` entities;
- recording and transcription statuses;
- recording pipeline stages;
- canonical defaults for start/stop/merge semantics;
- Fireworks transcription contract for MVP;
- Markdown/JSON output format and file naming.

### Out of scope {#scope.out}
- concrete DB migrations;
- UI layout details;
- additional providers besides Fireworks.

## Entities {#entities}

### MeetingSession {#entities.meeting-session}

Canonical fields:
- `id`
- `created_at`
- `started_at`
- `ended_at`
- `status`
- `mode` with values `auto | ask | manual`
- `source_type` with values `process | device | mic | mixed`
- `source_app`
- `source_process_id`
- `output_device_id`
- `microphone_device_id`
- `audio_output_path`
- `audio_mic_path`
- `audio_mix_path`
- `transcript_md_path`
- `transcript_json_path`
- `duration_seconds`
- `transcription_status`
- `transcription_model`
- `diarization_enabled`
- `language`
- `error_code`
- `error_message`
- `user_discarded`

### AppRule {#entities.app-rule}

Canonical fields:
- `id`
- `display_name`
- `process_name`
- `enabled`
- `capture_mode_default`
- `notes`

## Statuses {#statuses}

### Recording statuses {#statuses.recording}

Allowed values:
- `prebuffering`
- `awaiting_confirmation`
- `recording`
- `paused`
- `stopping`
- `saved`
- `discarded`
- `failed`

### Transcription statuses {#statuses.transcription}

Allowed values:
- `not_started`
- `queued`
- `uploading`
- `processing`
- `completed`
- `failed`
- `retry_scheduled`

## Pipeline {#pipeline}

Canonical recording/transcription pipeline:
1. `Detection` watches processes, audio sessions, devices and signal level.
2. `Session bootstrap` creates `MeetingSession`, starts required capture pipelines and writes to temp files.
3. `Recording` updates session status and metadata while audio is being captured.
4. `Finalize` closes streams, promotes temp files to final artifacts and enqueues transcription.
5. `Transcription` builds provider input, sends to Fireworks and materializes JSON/Markdown outputs.

## Rules {#rules}

- Каждая user-driven or automatically detected запись оформляется как одна `MeetingSession`.
- `Off` disables automatic recording, but manual recording, session browsing and rerunning transcription remain allowed.
- `Ask` uses prebuffer and confirmation before promoting a session to saved recording.
- `Auto` starts recording only when detection conditions are satisfied.
- `Privacy Pause` blocks automatic start and applies configured pause/finish behavior to the current recording.
- `Force Record` ignores whitelist detection rules and records selected manual sources.
- Default stop delay after inactivity: `20 секунд`.
- Default merge window that prevents fragmenting one meeting into multiple sessions: `60 секунд`.
- Если активность возвращается раньше stop delay, запись продолжается в рамках той же session.
- Error in transcription never implies loss of already recorded audio artifacts.
- MVP uses `Fireworks Audio Transcriptions API` as the only transcription provider.
- After a recording is finalized, the app runs pre-recorded transcription against Fireworks.
- Provider request must support: audio file, model, optional language, diarization settings and response format.
- If diarization is enabled, the request must ask for `response_format=verbose_json` and `timestamp_granularities=word`, or their equivalent provider parameters, sufficient to reconstruct speaker-labeled Markdown.
- Minimum stored provider outputs: human-readable transcript and full `verbose_json` when diarization is enabled.
- If diarization is enabled, the app stores speaker segmentation and reflects speakers in Markdown where provider data allows.
- Default automatic retry count: `3`.
- Default retry schedule: `1 минута`, `5 минут`, `15 минут`.
- If Fireworks is unavailable or returns an error, transcription status becomes `failed`; if automatic retries are enabled, the session may also enter `retry_scheduled`.
- Invalid API key does not block local recording persistence; it only fails transcription and surfaces a clear user-visible error.

## Markdown format {#markdown-format}

- Every completed transcript is stored as a separate `.md` file.
- Default filename template: `YYYY-MM-DD HH-mm — SourceApp — SessionId.md`.
- Recommended Markdown structure:
  - title line with meeting name;
  - metadata block with date, start time, end time, duration, source, recording mode, output device, microphone and diarization state;
  - `## Транскрипт` section;
  - speaker-labeled blocks in the form `[Speaker N | HH:MM:SS]`.
- Additional colocated artifacts may include:
  - `*.json` for full Fireworks response;
  - `*.wav` / `*.m4a` for audio artifacts;
  - `*.log` when a session ended with an error or needs diagnostics.

## Acceptance {#acceptance}

Канон корректен, если:
- data fields and enums from the original TZ are preserved in canonical specs;
- any UI surface or background worker can rely on one shared session model;
- recording lifecycle and transcription lifecycle are linked explicitly but not conflated;
- provider-level requirements and output-file semantics are preserved in canonical specs;
- Markdown/JSON artifacts are deterministic and discoverable from UI/storage.

## Document Notes {#document-notes}

- 2026-04-02: Extracted and consolidated from the original meeting-capture TZ.
- 2026-04-02: Merged transcription provider contract and artifact format into the main meeting-session canon.
- 2026-07-11: Linked release v2 artifact and transcription seam specs.
