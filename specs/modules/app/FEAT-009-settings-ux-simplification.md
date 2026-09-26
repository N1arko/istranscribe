---
status: superseded
---

# FEAT-009: Settings UX Simplification {#root}

## Простыми словами {#plain-language}

Настройки приложения остаются в шести разделах, но каждый раздел делится на «основные» и «расширенные» параметры. По умолчанию пользователь видит только основные — понятные, с человекочитаемыми пресетами вместо технических значений. Расширенные параметры раскрываются по клику для опытных пользователей. Результат: новый пользователь не пугается 70+ контролов, но ни один параметр не удалён.

## Goal {#goal}

Зафиксировать канонические решения по упрощению UX настроек: какие параметры являются основными, а какие расширенными; как устроены пресеты; как работает advanced-секция; какие label-ы и подсказки должны стать понятнее.

## Superseded by {#superseded-by}

- This planned v1 refinement is replaced by `spec://modules/app/FEAT-013-minimal-desktop-experience#settings` for release v2.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#settings`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings`
- `spec://modules/app/FEAT-008-modern-ui-design-system#behavior.resources`

## Related {#related}

- `spec://common/PROP-001-product-canon#rules`
- `spec://modules/app/FEAT-010-russian-localization#root`

## Scope {#scope}

### In scope {#scope.in}

- разделение полей каждого settings-раздела на «основные» (essential) и «расширенные» (advanced);
- collapsible Advanced-секция в каждом разделе, свёрнутая по умолчанию;
- введение пресетов (presets) для Recording раздела;
- улучшение label-ов и описаний для неочевидных параметров;
- единая кнопка Save на весь раздел (уже есть) с dirty-indicator.

### Out of scope {#scope.out}

- удаление или скрытие полей без возможности восстановления;
- изменение количества разделов: PROP-002 определяет шесть и они сохраняются;
- миграция на WPF-UI контролы (FEAT-008);
- локализация текстов (FEAT-010);
- добавление новых функциональных settings, не определённых в PROP-002.

---

## Status Quo {#status-quo}

### Current issues {#status-quo.issues}

1. **Cognitive overload.** 6 вкладок, ~70 контролов — все параметры видны сразу, включая технические (dBFS threshold, merge window, temp retention).
2. **Technical labels.** `Silence threshold (dBFS)`, `Prebuffer duration`, `Output change policy` — непонятны без документации.
3. **No grouping within tabs.** Recording tab: 12 controls в плоском списке — mixing timing, sources, и policy.
4. **No presets.** Пользователь обязан понимать каждый параметр, чтобы настроить recording behavior.
5. **Hotkeys in General.** 4 hotkey-поля — малополезны для большинства пользователей, занимают 50% visible area вкладки General.

---

## Behavior {#behavior}

### Design principle {#behavior.principle}

**Progressive disclosure**: основные параметры видны и понятны сразу; расширенные параметры доступны, но спрятаны за явным действием.

Все 6 разделов PROP-002 сохраняются. Ни один параметр не удаляется. Каждый параметр из PROP-002 остаётся доступным.

### Advanced section pattern {#behavior.advanced}

Каждый settings-раздел (tab) может содержать:

```
┌─────────────────────────────────────┐
│  Essential fields                    │
│  ─────────────────────────────────── │
│  ▶ Advanced settings (N parameters)  │  ← collapsed by default
│  ─────────────────────────────────── │
│  [Save]                              │
└─────────────────────────────────────┘
```

- Клик на `▶ Advanced settings` раскрывает секцию (toggle).
- Состояние (свёрнуто/развёрнуто) **не сохраняется** между перезапусками — всегда collapsed.
- Если advanced-часть пуста (все параметры essential), секция не отображается.
- Count `(N parameters)` показывает количество скрытых параметров.

### Tab-by-tab layout {#behavior.tabs}

#### General {#behavior.tabs.general}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Autostart | Launch at Windows startup | CheckBox | |
| Minimize to tray | Minimize to tray instead of closing | CheckBox | |
| Notifications | Show notifications | CheckBox | |
| Language | Interface language | ComboBox | |
| Theme | App theme | ComboBox | System / Light / Dark (FEAT-008) |

**Advanced (4 parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Force record hotkey | Force Record hotkey | Hotkey input | |
| Privacy pause hotkey | Privacy Pause hotkey | Hotkey input | |
| Discard hotkey | Discard Recording hotkey | Hotkey input | |
| Open window hotkey | Open Main Window hotkey | Hotkey input | |

Обоснование: hotkeys — power-user функция; большинству пользователей достаточно tray-меню и кнопок в UI.

#### Recording {#behavior.tabs.recording}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Recording mode | Recording mode | ComboBox | Off / Ask me / Automatic |
| Preset | Recording preset | ComboBox | See `#behavior.presets` |
| Privacy pause | Privacy pause policy | ComboBox | |

**Advanced (9 parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Prebuffer | Prebuffer duration | ComboBox | + описание: «How many seconds of audio to keep before recording starts» |
| Silence threshold | Silence threshold (dBFS) | TextBox | + описание: «Audio level below which silence is detected» |
| Start delay | Start delay (seconds) | TextBox | + описание: «Wait before starting a new recording» |
| Stop delay | Stop delay (seconds) | TextBox | + описание: «Wait for silence before stopping» |
| Merge window | Merge window (seconds) | TextBox | + описание: «Merge recordings that restart within this window» |
| Auto: process_output | Auto: capture app audio | CheckBox | Renamed from technical name |
| Auto: device_loopback | Auto: capture system audio | CheckBox | Renamed from technical name |
| Auto: mic | Auto: capture microphone | CheckBox | |
| Force: device_loopback | Force Record: capture system audio | CheckBox | |
| Force: mic | Force Record: capture microphone | CheckBox | |

Обоснование: при выборе пресета advanced-поля получают предустановленные значения, но остаются доступны для тонкой настройки.

#### Devices {#behavior.tabs.devices}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Output device | Output device | ComboBox | |
| Follow default output | Follow system default (output) | CheckBox | |
| Microphone | Microphone | ComboBox | |
| Follow default mic | Follow system default (mic) | CheckBox | |

**Advanced (5 parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Auto-discover output | Auto-discover output devices | CheckBox | |
| Auto-discover mic | Auto-discover microphones | CheckBox | |
| Output change policy | When output device changes | ComboBox | Label made human-readable |
| Mic change policy | When microphone changes | ComboBox | Label made human-readable |
| Active recording policy | When device changes during recording | ComboBox | Label made human-readable |

#### Applications {#behavior.tabs.applications}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Auto-discovery | New app discovered | ComboBox | Label: not "Auto-discovery policy" |
| Whitelist | Monitored applications | List | Primary user interaction |
| Add from running | Add from running apps… | Button | |

**Advanced (4+ parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Add manually | Add application manually… | Button | |
| Exclusions | Excluded applications | List | |
| Add exclusion | Add exclusion… | Button | |
| Ignored suggestions | Ignored app suggestions | List | + Allow again button |

Обоснование: exclusions и ignored suggestions — edge-case функции, редко нужны.

#### Storage {#behavior.tabs.storage}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Recordings folder | Recordings folder | Path picker | |
| Transcripts folder | Transcripts folder | Path picker | |
| Compress audio | Compress audio (Opus) after recording | CheckBox | |

**Advanced (4 parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Failed/temp folder | Temporary files folder | Path picker | |
| Filename template | Filename template | TextBox | + hint with available variables |
| Keep raw | Keep raw audio after transcription | CheckBox | |
| Temp retention | Clean up temporary files after | ComboBox | |

#### Transcription {#behavior.tabs.transcription}

**Essential:**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| API key | Fireworks API key | PasswordBox | S show/hide, change/remove |
| Model | Transcription model | ComboBox | |
| Language | Transcription language | ComboBox | |

**Advanced (4 parameters):**

| Field | Label | Type | Notes |
|-------|-------|------|-------|
| Diarization | Distinguish speakers | CheckBox | Label: not "Enable diarization" |
| Min speakers | Minimum speakers | TextBox | Visible only when diarization enabled |
| Max speakers | Maximum speakers | TextBox | Visible only when diarization enabled |
| Auto retry | Retry failed transcriptions | CheckBox | |
| Retry count | Retry attempts | TextBox | Visible only when auto retry enabled |

### Recording presets {#behavior.presets}

Пресеты для Recording tab — это **named combinations** значений advanced-полей.

| Preset | Prebuffer | Silence | Start delay | Stop delay | Merge | Sources (Auto) |
|--------|-----------|---------|-------------|------------|-------|----------------|
| **Meetings** (default) | 5s | -40 dBFS | 2s | 8s | 30s | process_output, device_loopback |
| **Meetings + Mic** | 5s | -40 dBFS | 2s | 8s | 30s | process_output, device_loopback, mic |
| **Quick notes** | 0s | -35 dBFS | 0s | 3s | 10s | mic |
| **Custom** | — | — | — | — | — | — |

Behavior:
- При выборе пресета (кроме Custom) advanced-поля перезаписываются значениями из таблицы.
- Если пользователь вручную меняет advanced-поле, пресет автоматически переключается на `Custom`.
- Выбранный пресет не сохраняется отдельно: при загрузке settings определяется по совпадению текущих значений с пресетом; если не совпадает — показывается `Custom`.
- Пресеты — фиксированный набор, не расширяемый пользователем.

### Improved labels and descriptions {#behavior.labels}

Каждое advanced-поле получает однострочное описание (description), отображаемое под полем мелким текстом (`AppCaptionStyle` из FEAT-008).

Примеры переименований:

| Old label | New label | Description |
|-----------|-----------|-------------|
| Silence threshold (dBFS) | Silence threshold | Audio level below which silence is detected (-60 quietest, 0 loudest) |
| Prebuffer duration | Prebuffer | Seconds of audio kept in memory before recording starts |
| Merge window (sec.) | Merge window | Automatically merge recordings that restart within this many seconds |
| Output change policy | When output device changes | What happens if the audio device changes while idle |
| Enable diarization | Distinguish speakers | Identify different speakers in the transcript |
| process_output (Auto) | Capture app audio (Auto) | Record audio from the target application directly |
| device_loopback (Auto) | Capture system audio (Auto) | Record all audio playing through the output device |

### Dirty indicator {#behavior.dirty}

Dirty indicator уже реализован в текущем UI (из FEAT-001). Поведение сохраняется без изменений: при изменении любого поля (essential или advanced) кнопка Save активируется, при сохранении — деактивируется.

---

## Canonical Decisions {#decisions}

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Number of tabs | 6 (unchanged) | PROP-002 mandate |
| Simplification method | Progressive disclosure via collapsible Advanced sections | Preserves all fields while reducing cognitive load |
| Default state of Advanced | Collapsed | New users see only essential fields |
| Persist collapsed state | No (always collapsed on restart) | Consistent UX, prevents forgotten hidden settings |
| Recording presets | Fixed set of 3 named + Custom | Covers 90% use cases without understanding every field |
| Preset storage | Derived from field values, not stored separately | No migration needed, no sync issues |
| Label improvements | In-place rename + description text | Backwards compatible, no data model change |

---

## Acceptance {#acceptance}

FEAT-009 считается завершённой, если одновременно выполнено:

1. Каждый из 6 settings-разделов содержит Essential и Advanced секции согласно `#behavior.tabs`.
2. Advanced секция по умолчанию свёрнута; раскрывается по клику; показывает количество скрытых параметров.
3. Все параметры из PROP-002 остаются доступны — ни один не удалён.
4. Recording tab содержит комбобокс «Recording preset» с пресетами из `#behavior.presets`.
5. Выбор пресета (кроме Custom) перезаписывает advanced-поля; ручное изменение advanced-поля переключает пресет на Custom.
6. Переименованные label-ы из `#behavior.labels` применены.
7. Каждое advanced-поле имеет однострочное описание, отображаемое под полем.
8. Dirty indicator работает для всех полей (essential + advanced).
9. Speaker min/max видимы только при включённом diarization; retry count — при включённом auto retry.
10. `dotnet build isTranscribe.sln` и `dotnet test isTranscribe.sln` проходят без ошибок.
11. Все existing settings save/load/dirty flows функционируют корректно.

---

## Document Notes {#document-notes}

- 2026-04-13: Spec authored from user feedback on complex settings UI; codebase analysis shows ~70 controls across 6 flat tabs with no grouping or progressive disclosure.
- 2026-07-11: Marked superseded by the release v2 minimal settings model.
