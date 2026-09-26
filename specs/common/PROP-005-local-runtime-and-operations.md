---
status: active
---

# PROP-005: Local Runtime And Operations {#root}

## Простыми словами {#plain-language}

Этот документ описывает, как приложение живёт на машине пользователя: где хранятся настройки и артефакты, как защищаются секреты будущих provider-интеграций, что логируется и как выполняется recovery после crash.

## Goal {#goal}

Зафиксировать local runtime, storage, diagnostics and recovery canon.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#root`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#root`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#root`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`
- `spec://modules/app/FEAT-014-transcription-extension-seam#root`

## Scope {#scope}

### In scope {#scope.in}
- local storage layout;
- settings and secret handling;
- logging and diagnostics;
- recovery behavior and performance expectations.

### Out of scope {#scope.out}
- UI implementation of the diagnostics screen;
- external deployment or server observability;
- code-level optimization details.

## Rules {#rules}

- Sensitive settings, future provider keys and local recordings are treated as protected local data.
- Секрет не хранится в открытом виде; Windows-native DPAPI или `.NET ProtectedData` остаётся канонической защитой Windows key material.
- Release v2 не требует transcription API key и не запускает transcription runtime. Будущий provider показывает и позволяет удалить свой ключ только после явного opt-in пользователя.
- Local storage is split into config, database, recordings, temp and logs. Recommended layout:
  - `AppData/config/settings.json`
  - `AppData/config/secrets.bin`
  - `AppData/data/app.db`
  - `AppData/recordings/YYYY/MM/<session-dir>/...`
  - `AppData/temp/`
  - `AppData/logs/`
- User may customize recordings path, but the app remains owner of its internal config/temp/log/data layout.
- Logs are local files and must not contain the API key in open form.
- User-facing shell provides a command or button to open the logs folder.
- Minimum log levels: `Info`, `Warning`, `Error`, `Debug`.
- Logging covers at least: app start/stop, recording start/stop, state transitions, audio-capture errors, device-access errors, device switches and manual user actions. Provider errors появляются только в активированной future transcription feature.
- On next launch the app must detect unfinished temp sessions, offer recovery or mark them as failed recovery, and must not silently delete them.
- Recorded files must survive finalization, future provider failures and device disappearance.
- Performance bar for MVP: low idle CPU, no memory leaks during long background sessions, and no audible glitches introduced by the app during recording.

## Acceptance {#acceptance}

Канон корректен, если:
- local storage, secrets, logs and recovery are fully specified without needing the original TZ;
- operational behavior after provider/device failures is deterministic;
- performance expectations are explicit enough to govern implementation and testing.

## Document Notes {#document-notes}

- 2026-07-31: Обновлены dependencies и rules под release-v2 без активной транскрибации.
- 2026-04-02: Extracted and consolidated from the original meeting-capture TZ.
- 2026-04-02: Renumbered from `PROP-006` to `PROP-005` after PROP consolidation.
