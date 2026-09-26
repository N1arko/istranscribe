---
status: active
---

# INFRA-002: Local Persistence And Secret Storage {#root}

## Простыми словами {#plain-language}

Эта спека фиксирует канон локального хранения данных для MVP: как устроена файловая структура приложения, что хранит SQLite, как устроен `settings.json`, как защищается Fireworks API key через DPAPI, и какие политики управляют жизненным циклом артефактов.

## Goal {#goal}

Задать безопасный, восстановимый и готовый к реализации local storage contour, на который могут опираться UI, capture, transcription и recovery.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`
- `spec://common/PROP-004-meeting-session-and-data-model#entities`
- `spec://common/PROP-004-meeting-session-and-data-model#markdown-format`
- `spec://common/PROP-005-local-runtime-and-operations#rules`

## See also {#see-also}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#behavior`
- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#behavior`
- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#behavior`
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#bootstrap`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#decisions`

## Scope {#scope}

### In scope {#scope.in}

- каноническая файловая структура приложения и правила ownership;
- SQLite baseline: таблицы `MeetingSession` и `AppRule`, типы колонок, индексы;
- JSON settings profile: структура, defaults, атомарность записи;
- DPAPI-based secret storage: контракт записи, чтения и ротации API key;
- политики хранения артефактов: temp retention, keep-raw-after-success;
- filename template и session directory convention;
- контракт инициализации persistence в bootstrap sequence;
- migration strategy для SQLite schema.

### Out of scope {#scope.out}

- UI screens настроек (→ `FEAT-001`);
- Fireworks HTTP клиент и transcription pipeline (→ `FEAT-006`);
- логика выбора аудиоисточника (→ `INFRA-003`);
- retry scheduler и recovery jobs (→ `INFRA-004`);
- log file rotation и diagnostic formatting (→ `INFRA-004`).

## Decisions {#decisions}

- `MeetingSession` metadata и справочник `AppRule` хранятся в SQLite.
- Пользовательские настройки хранятся в `settings.json` с полной JSON-схемой; API key в этот файл не входит.
- Fireworks API key хранится через `.NET ProtectedData` (DPAPI `CurrentUser` scope).
- Пользовательские пути для recordings и transcripts могут быть кастомными; внутренние `config/`, `data/`, `temp/`, `logs/` остаются под управлением приложения.
- Шаблон имени файлов и расположение артефактов стабильны и пригодны для повторного открытия из UI.
- Удаление temp и failed артефактов управляется настраиваемой политикой хранения.
- Persistence layer не владеет бизнес-логикой записи и транскрибации; он предоставляет storage primitives.

## File layout {#file-layout}

### App-owned directories {#file-layout.app-owned}

Корень app-owned storage: `%LOCALAPPDATA%/isTranscribe/`.

```
%LOCALAPPDATA%/isTranscribe/
├── config/
│   ├── settings.json          # пользовательские настройки
│   └── secrets.bin            # DPAPI-protected secrets
├── data/
│   └── app.db                 # SQLite database
├── temp/                      # незавершённые сессии, промежуточные файлы
└── logs/                      # application logs
```

- Приложение создаёт эту структуру при первом запуске, если она отсутствует.
- `config/`, `data/`, `logs/` не подлежат пользовательской relocate; приложение является единственным owner.
- `temp/` является default location для failed/temp артефактов; пользователь может переназначить через `storage.failed_temp_folder` в Settings, но app-owned `temp/` directory всегда существует.
- Пользователь может открыть `logs/` из shell UI, но не может переназначить путь.

### User-customizable directories {#file-layout.user-dirs}

Defaults:

```
%USERPROFILE%/Documents/isTranscribe/
├── Recordings/
│   └── YYYY/MM/<session-dir>/
│       ├── output.wav
│       ├── mic.wav
│       └── mix.wav             # опциональный, если записываются оба источника
└── Transcripts/
    └── <transcript>.md
    └── <transcript>.json       # полный Fireworks response при diarization
```

- Пользователь может изменить `recordings_folder` и `transcripts_folder` в настройках.
- По умолчанию recordings и transcripts лежат в разных папках; транскрипты идентифицируются по `{SessionId}` в filename template.
- Если пользователь явно выставит `transcripts_folder` равным `recordings_folder`, транскрипты будут лежать в session directory рядом с аудио.
- Audio артефакты всегда лежат в session directory внутри `recordings_folder`; transcript placement определяется `transcripts_folder`.

### Session directory convention {#file-layout.session-dir}

- Каждая `MeetingSession` получает отдельную директорию внутри `recordings_folder/YYYY/MM/`.
- Имя session directory: `<session-id>` (GUID без дефисов, lowercase).
- Внутри session directory имена файлов фиксированы: `output.wav`, `mic.wav`, `mix.wav`.
- Транскрипт использует filename template из настроек.

### Filename template {#file-layout.filename-template}

- Default template: `YYYY-MM-DD HH-mm — {SourceApp} — {SessionId}`.
- Обязательный placeholder: `{SessionId}` — гарантирует уникальность.
- Доступные placeholders: `{SessionId}`, `{SourceApp}`, `{Date}` (`YYYY-MM-DD`), `{Time}` (`HH-mm`), `{Mode}`.
- Template применяется к `.md` и `.json` транскриптам.
- Если template не содержит `{SessionId}`, settings validation должна отклонить его.

## SQLite database {#sqlite}

### General rules {#sqlite.rules}

- Database file: `data/app.db`.
- WAL mode включён при первом создании; все последующие подключения используют WAL.
- Приложение использует один SQLite connection с сериализацией доступа на уровне приложения; concurrent multi-process access не поддерживается (гарантируется single-instance из `INFRA-001`).
- При первом запуске приложение создаёт schema через embedded migration; на последующих запусках применяет pending migrations.
- Миграции нумеруются последовательно (`001`, `002`, ...) и хранятся как embedded resources в assembly.

### MeetingSession table {#sqlite.meeting-session}

```sql
CREATE TABLE meeting_session (
    id                    TEXT    PRIMARY KEY,  -- GUID
    created_at            TEXT    NOT NULL,     -- ISO 8601 UTC
    started_at            TEXT,                 -- ISO 8601 UTC
    ended_at              TEXT,                 -- ISO 8601 UTC
    status                TEXT    NOT NULL,     -- recording status enum
    mode                  TEXT    NOT NULL,     -- auto | ask | manual
    source_type           TEXT    NOT NULL,     -- process | device | mic | mixed
    source_app            TEXT,                 -- display name of source application
    source_process_id     INTEGER,             -- Windows PID at time of capture
    output_device_id      TEXT,                 -- MMDevice ID
    microphone_device_id  TEXT,                 -- MMDevice ID
    audio_output_path     TEXT,                 -- relative to session dir
    audio_mic_path        TEXT,                 -- relative to session dir
    audio_mix_path        TEXT,                 -- relative to session dir
    transcript_md_path    TEXT,                 -- relative to transcripts_folder
    transcript_json_path  TEXT,                 -- relative to transcripts_folder
    duration_seconds      REAL,
    transcription_status  TEXT    NOT NULL DEFAULT 'not_started',
    transcription_model   TEXT,
    diarization_enabled   INTEGER NOT NULL DEFAULT 1,  -- boolean
    language              TEXT,
    error_code            TEXT,
    error_message         TEXT,
    user_discarded        INTEGER NOT NULL DEFAULT 0   -- boolean
);

CREATE INDEX idx_meeting_session_status ON meeting_session(status);
CREATE INDEX idx_meeting_session_created_at ON meeting_session(created_at);
CREATE INDEX idx_meeting_session_transcription_status ON meeting_session(transcription_status);
```

- `status` допустимые значения: `prebuffering`, `awaiting_confirmation`, `recording`, `paused`, `stopping`, `saved`, `discarded`, `failed`.
- `transcription_status` допустимые значения: `not_started`, `queued`, `uploading`, `processing`, `completed`, `failed`, `retry_scheduled`.
- Enum validation выполняется на уровне приложения, не через CHECK constraints, чтобы не блокировать migrations при добавлении новых значений.
- Пути к аудиофайлам хранятся относительно session directory. Пути к транскриптам — относительно `transcripts_folder`. Абсолютные пути собираются в runtime из соответствующего base folder + relative path.

### AppRule table {#sqlite.app-rule}

```sql
CREATE TABLE app_rule (
    id                    TEXT    PRIMARY KEY,  -- GUID
    display_name          TEXT    NOT NULL,
    process_name          TEXT    NOT NULL,     -- executable name, e.g. "Zoom.exe"
    enabled               INTEGER NOT NULL DEFAULT 1,
    capture_mode_default  TEXT,                 -- process | device | NULL (use global)
    notes                 TEXT
);

CREATE UNIQUE INDEX idx_app_rule_process_name ON app_rule(process_name);
```

- `process_name` уникален; одно и то же приложение не может быть в whitelist дважды.

### Migration strategy {#sqlite.migrations}

- Миграции — forward-only; rollback не поддерживается в MVP.
- Каждая миграция имеет порядковый номер и описание.
- Текущая applied version хранится в `PRAGMA user_version`.
- При запуске приложение читает `user_version`, сравнивает с embedded migrations и применяет недостающие в порядке номера в одной транзакции.
- Если миграция падает, транзакция откатывается; приложение показывает blocking error и не стартует.

## Settings JSON {#settings}

### Structure {#settings.structure}

`config/settings.json` содержит полный settings profile. Структура повторяет шесть секций из `PROP-002`:

```json
{
  "version": 1,
  "onboarding_completed": false,
  "general": {
    "autostart": true,
    "minimize_to_tray_on_close": true,
    "notifications": true,
    "language": "ru",
    "hotkeys": {
      "force_record_toggle": null,
      "privacy_pause_toggle": null,
      "discard_current": null,
      "open_main_window": null
    }
  },
  "recording": {
    "mode": "ask",
    "prebuffer_seconds": 15,
    "silence_threshold_dbfs": -40,
    "start_delay_seconds": 2,
    "stop_delay_seconds": 20,
    "merge_window_seconds": 60,
    "privacy_pause_policy": "pause",
    "default_sources_auto": ["process_output", "mic"],
    "default_sources_force": ["device_loopback", "mic"]
  },
  "devices": {
    "output_device_id": null,
    "microphone_device_id": null,
    "follow_system_default_output": true,
    "follow_system_default_mic": true,
    "auto_discover_output": true,
    "auto_discover_mic": true,
    "output_change_policy": "seamless_switch",
    "mic_change_policy": "seamless_switch",
    "active_recording_device_policy": "seamless_switch"
  },
  "applications": {
    "auto_discovery_policy": "ask_to_add",
    "exclusions": []
  },
  "storage": {
    "recordings_folder": null,
    "transcripts_folder": null,
    "failed_temp_folder": null,
    "filename_template": "YYYY-MM-DD HH-mm — {SourceApp} — {SessionId}",
    "keep_raw_after_success": true,
    "temp_retention_period": "7d"
  },
  "transcription": {
    "model": "whisper-v3-turbo",
    "diarization": true,
    "min_speakers": 2,
    "max_speakers": 6,
    "language": "auto",
    "auto_retry": true,
    "retry_count": 3
  }
}
```

- `version` — schema version для forward compatibility; при чтении неизвестной version приложение использует defaults для отсутствующих полей.
- `recordings_folder: null` означает default path (`%USERPROFILE%/Documents/isTranscribe/Recordings`).
- `transcripts_folder: null` означает default path (`%USERPROFILE%/Documents/isTranscribe/Transcripts`).
- `failed_temp_folder: null` означает default path (`%LOCALAPPDATA%/isTranscribe/temp`). Пользователь может переназначить через Settings; при `null` используется app-owned `temp/`.
- `applications.whitelist` хранится в SQLite (`AppRule`), не дублируется в JSON.
- API key **не** входит в `settings.json`.

### Read/write contract {#settings.rw}

- Чтение: при bootstrap persistence layer читает `settings.json`, мержит с defaults для отсутствующих полей, возвращает typed settings object.
- Если файл отсутствует или повреждён, приложение создаёт файл с полными defaults и `onboarding_completed: false`.
- Запись: атомарная через write-to-temp + rename. Последовательность: serialize → write `settings.json.tmp` → `File.Replace` / rename → delete temp.
- Partial update: settings layer принимает section-level updates; при сохранении перезаписывается весь файл (не patch).
- Concurrent writes не возникают благодаря single-instance guarantee из `INFRA-001`.

### Validation rules {#settings.validation}

- `filename_template` обязан содержать `{SessionId}`.
- `recordings_folder`, `transcripts_folder` и `failed_temp_folder`, если заданы, должны указывать на writeable path; validation выполняется при сохранении.
- `temp_retention_period` допускает значения: `1d`, `3d`, `7d`, `14d`, `30d`, `never`.
- `prebuffer_seconds` допускает: `5`, `10`, `15`, `30`.
- `silence_threshold_dbfs` допускает диапазон `[-60, -20]`.
- `start_delay_seconds` допускает диапазон `[1, 10]`.
- `stop_delay_seconds` допускает диапазон `[5, 120]`.
- `merge_window_seconds` допускает диапазон `[0, 300]`.
- `min_speakers` и `max_speakers` допускают диапазон `[1, 20]`; `min_speakers ≤ max_speakers`.
- `retry_count` допускает диапазон `[1, 10]`.

## Secret storage {#secrets}

### Contract {#secrets.contract}

- Единственный секрет в MVP: Fireworks API key.
- Хранилище: файл `config/secrets.bin`.
- Формат файла: DPAPI-encrypted blob, полученный через `System.Security.Cryptography.ProtectedData.Protect` с `DataProtectionScope.CurrentUser`.
- Plaintext перед шифрованием: UTF-8 encoded JSON object `{"fireworks_api_key": "<value>"}`.
- JSON envelope выбран сознательно, чтобы в будущем можно было добавить дополнительные секреты без смены формата файла.

### Operations {#secrets.operations}

- **Read**: прочитать `secrets.bin` → `ProtectedData.Unprotect` → десериализовать JSON → вернуть typed secret object. Если файл отсутствует или Unprotect падает, вернуть empty secrets (API key = null).
- **Write**: сериализовать JSON → `ProtectedData.Protect` → write-to-temp + rename (аналогично `settings.json`).
- **Delete**: пользователь может удалить API key из UI; при этом записывается `secrets.bin` с `{"fireworks_api_key": null}`.
- **Rotation**: перезапись ключа = Write с новым значением. Старый blob полностью перезаписывается.

### Security invariants {#secrets.invariants}

- API key никогда не появляется в `settings.json`.
- API key никогда не появляется в log-файлах.
- В UI API key маскируется; полное значение доступно только при явном Show toggle.
- `secrets.bin` привязан к текущему Windows user profile через DPAPI `CurrentUser` scope; другой пользователь на той же машине не сможет расшифровать файл.
- Если пользователь сбрасывает Windows profile или переносит `secrets.bin` на другую машину, Unprotect вернёт ошибку; приложение обрабатывает это как отсутствие ключа и предлагает ввести заново.

## Storage policies {#storage-policies}

### Temp retention {#storage-policies.temp}

- `temp/` содержит незавершённые записи и промежуточные файлы.
- Политика хранения задаётся `storage.temp_retention_period` в настройках.
- Cleanup job проверяет `temp/` при каждом запуске приложения и удаляет файлы старше retention period, кроме файлов, привязанных к сессиям в статусе `failed` с непустым `error_code` (они удаляются только при `never` → при ручном действии пользователя или при достижении retention period).
- При `never` автоматическое удаление не выполняется.

### Keep raw after success {#storage-policies.keep-raw}

- `storage.keep_raw_after_success` управляет, сохраняются ли `.wav` артефакты после успешной транскрибации.
- Если `true` (default): audio files остаются в session directory.
- Если `false`: после `transcription_status = completed` audio files удаляются; `.md` и `.json` остаются.
- Удаление происходит асинхронно после подтверждения `completed` статуса, не в критическом пути транскрибации.

### Recovery-safe invariants {#storage-policies.recovery}

- Audio артефакты не удаляются, пока сессия не достигла terminal status (`saved` + `transcription_status = completed` и `keep_raw = false`, либо `discarded` с `user_discarded = true`).
- Незавершённые temp-сессии обнаруживаются при startup; recovery coordinator (→ `INFRA-004`) предлагает пользователю восстановить или пометить как failed.
- Приложение никогда не удаляет temp-файлы молча, если они привязаны к сессии, не достигшей terminal status.

## Persistence initialization {#initialization}

### Bootstrap position {#initialization.bootstrap}

Persistence инициализируется на шаге 7 bootstrap sequence из `INFRA-001`:

1. Ensure app-owned directory structure exists; create missing directories.
2. Open or create `data/app.db`; apply pending SQLite migrations.
3. Read `config/settings.json`; merge with defaults.
4. Read `config/secrets.bin`; handle absent/corrupt gracefully.
5. Publish settings и secret availability для downstream services.

- Если migration falls, это blocking error; приложение не стартует.
- Если `settings.json` повреждён, приложение пересоздаёт с defaults и `onboarding_completed: false`; пользователь проходит wizard заново.
- Если `secrets.bin` повреждён или нерасшифровываем, API key = null; пользователь вводит ключ заново.

### First-run initialization {#initialization.first-run}

При первом запуске (или после сброса):

1. Directories создаются.
2. `app.db` создаётся с начальной migration.
3. `settings.json` создаётся с defaults и `onboarding_completed: false`.
4. `secrets.bin` не создаётся (будет создан при вводе API key в wizard).
5. Host направляет пользователя в first-run wizard (→ `FEAT-001`).

### Wizard completion {#initialization.wizard}

Wizard completion (→ `FEAT-001`) выполняет атомарно:

1. Записывает `settings.json` со всеми значениями из wizard + defaults для незатронутых полей.
2. Записывает `secrets.bin` с API key.
3. Создаёт `AppRule` записи в SQLite для выбранных приложений в whitelist.
4. Устанавливает `onboarding_completed: true` в `settings.json`.

Атомарность: если любой шаг падает, wizard не считается завершённым; `onboarding_completed` остаётся `false`.

## Acceptance {#acceptance}

Спека достаточна для реализации, если одновременно выполняются все условия:

- Файловая структура app-owned и user-customizable directories описана явно, без двусмысленности о том, кто владеет каждым путём.
- SQLite schema для `MeetingSession` и `AppRule` содержит все canonical fields из `PROP-004` с типами и индексами.
- `settings.json` structure покрывает все шесть секций из `PROP-002` с defaults и validation rules.
- Secret storage contract описывает полный цикл read/write/delete/rotation через DPAPI без хранения ключа в plaintext.
- Storage policies для temp retention и keep-raw-after-success описаны достаточно для реализации cleanup logic.
- Filename template и session directory convention описаны достаточно для deterministic path resolution из UI и background jobs.
- Bootstrap initialization описан как конкретный sequence с handling повреждённых или отсутствующих файлов.
- Migration strategy определена: forward-only, embedded, sequential, transactional.
- Scope boundaries с `FEAT-001`, `FEAT-006`, `INFRA-001` и `INFRA-004` явные и не пересекаются.

## Document Notes {#document-notes}

- 2026-04-02: Initial persistence and secret storage baseline authored from the MVP TZ.
- 2026-04-03: Спека переписана в implementation-ready канон: добавлены SQLite schema, settings JSON structure с defaults и validation, DPAPI secret contract, storage policies, file layout convention, bootstrap initialization sequence и migration strategy.
