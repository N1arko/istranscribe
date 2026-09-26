---
status: active
---

# FEAT-014: Transcription Extension Seam {#root}

## Простыми словами {#plain-language}

Release v2 не транскрибирует встречи. Старый Fireworks-код перестаёт запускаться и исчезает из интерфейса, а в core остаётся небольшой нейтральный контракт, к которому позже можно подключить локальную модель или другой внешний сервис.

## Goal {#goal}

Безопасно вывести Fireworks из active runtime и определить provider-neutral transcription seam без реализации, загрузки моделей и фоновых jobs в release v2.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#transcription-seam`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#target-structure`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary`
- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#root`

## See also {#see-also}

- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`
- `spec://modules/app/FEAT-016-local-whisper-transcription#root`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#root`

## Supersedes {#supersedes}

- active provider and queue behavior from `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#behavior`

## Scope {#scope}

### In scope {#scope.in}

- disable Fireworks worker startup and network dispatch;
- stop enqueueing new sessions for transcription;
- remove Fireworks/API/model/diarization/retry UI from release surfaces;
- preserve existing transcript files and metadata;
- define provider-neutral engine, request, progress, result and capability contracts;
- migration semantics for legacy queued/failed jobs;
- tests proving no transcription network activity in release v2.

### Out of scope {#scope.out}

- local model selection or download;
- external provider implementation;
- transcript materialization;
- diarization and speaker-aware materialization, governed by `FEAT-017`;
- transcription settings UI;
- job scheduler implementation for a future engine.

## Runtime Deactivation {#deactivation}

- `TranscriptionBackgroundWorker` is not created or started in the release-v2 composition root.
- New saved recordings keep transcription state `not_started` or a migrated neutral equivalent; absence of an engine is normal.
- Existing `queued`, `uploading`, `processing` and `retry_scheduled` legacy rows are migrated to a non-running legacy state without deleting audio or transcript files.
- Release v2 makes no request to `api.fireworks.ai`.
- Legacy API key may be removed from protected storage through migration or retained unread for rollback; it is never displayed or required.

## Extension Contract {#extension-contract}

Core defines an inactive contract equivalent to:

- engine id and display name;
- execution kind `local | remote`;
- privacy disclosure and network requirement;
- supported operating systems/architectures;
- model and language capabilities;
- optional diarization/timestamp capabilities;
- minimum memory/GPU/disk requirements where applicable;
- cancellable request with primary audio artifact path;
- optional source-aware inputs with source role, path/hash and timeline metadata;
- progress events and structured result/error categories.

Contracts must not contain Fireworks-specific HTTP fields, endpoint paths or API response types.

## Legacy Artifacts {#legacy-artifacts}

- Existing Markdown/JSON transcript paths remain on `MeetingSession` or a compatible artifact relation.
- Recent recordings may expose legacy transcript files through a secondary artifact action.
- Opening an old transcript never activates a provider or retry job.
- Legacy failure messages may be retained for history and are not shown as current release health failures.

## Future Activation Boundary {#future-activation}

A future transcription item must separately specify:

- selected engine(s);
- explicit user opt-in;
- model acquisition/update and storage;
- privacy/network disclosure;
- resource/capability gating;
- queue/retry semantics;
- transcript format and UI.

Speaker-aware activation, source-track handoff and three-mode settings are
specified by `spec://modules/app/FEAT-017-speaker-aware-transcription#root`.

Adding the seam in FEAT-014 does not authorize any of these behaviors.

## Verification {#verification}

- Composition-root tests prove no transcription worker/provider is resolved or started.
- Network test/fake handler proves completing a recording performs zero provider calls.
- Repository migration tests cover every legacy active transcription status.
- Contract tests prove Core has no Fireworks namespace/type dependency.
- UI search/render tests prove API key, model, diarization and retry controls are absent.
- Existing transcript open actions remain functional.

## Acceptance {#acceptance}

FEAT-014 is complete when:

1. New recordings never enter an active transcription queue.
2. Fireworks is absent from release runtime composition and user-facing settings.
3. Legacy audio/transcript artifacts remain accessible.
4. All legacy active job states migrate deterministically.
5. Provider-neutral inactive contracts exist in Core with tests.
6. End-to-end recording completion performs no transcription network call.

## Document Notes {#document-notes}

- 2026-08-30: Linked the speaker-aware activation canon and clarified that the
  neutral seam may carry source-aware inputs while diarization remains outside
  FEAT-014 ownership.
- 2026-07-30: Linked the future opt-in Groq/OpenRouter and local Whisper
  activation items.
- 2026-07-11: Initial seam spec authored after the product decision to postpone transcription and consider a future local model.
