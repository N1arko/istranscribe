---
status: superseded
---

# FEAT-001: First-Run Setup And Settings {#root}

## Простыми словами {#plain-language}

При первом запуске приложение проводит пользователя через короткий wizard: язык, storage, devices, API key, recording mode и applications. После завершения wizard пользователь в любой момент может открыть полный экран Settings и изменить любую настройку MVP без ручной правки файлов.

## Goal {#goal}

Описать канонический first-run onboarding и полный settings surface для MVP, с точностью до конкретных полей, типов, defaults и правил применения.

## Superseded by {#superseded-by}

- Release v2 first run and settings are governed by `spec://modules/app/FEAT-013-minimal-desktop-experience#first-run` and `spec://modules/app/FEAT-013-minimal-desktop-experience#settings`.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules` — каталог обязательных settings sections и полей
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions` — storage layout, settings.json, secrets.bin

## Related {#related}

- `spec://common/PROP-001-product-canon#rules` — продуктовые границы MVP
- `spec://common/PROP-003-audio-capture-and-device-observation#rules` — detection signals, prebuffer defaults, device policies
- `spec://common/PROP-004-meeting-session-and-data-model#rules` — session model, stop delay, merge window, retry defaults
- `spec://common/PROP-005-local-runtime-and-operations#rules` — secret handling, storage layout, logging
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#entrypoints.first-run` — host routing: незавершённый onboarding блокирует normal startup
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.states` — degraded mode: process loopback недоступен
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#decisions` — capture adapters, prebuffer, watchers
- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#behavior` — Auto/Ask detection flows, зависят от recording mode
- `spec://modules/app/FEAT-005-application-rules-and-discovery#behavior` — whitelist и auto-discovery, настраиваются из Settings
- `spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#behavior` — transcription provider, model, diarization
- `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior` — device selection и switching policies

## Scope {#scope}

### In scope {#scope.in}

- First-run wizard: шаги, навигация, валидация, условие завершения.
- Полный settings catalog: каждое поле с типом, default, допустимыми значениями и валидацией.
- Поведение settings surface: save semantics, применение изменений, dirty state.
- API key UX: ввод, маскирование, валидация, удаление.
- Degraded mode awareness: что wizard и settings показывают, когда process loopback недоступен.

### Out of scope {#scope.out}

- Реализация audio capture adapters (INFRA-003).
- Session list и current recording surface (FEAT-004, FEAT-003).
- Full retry pipeline internals (INFRA-004).
- SQLite schema и migrations (INFRA-002).
- Visual design, layout и component library.

---

## Behavior {#behavior}

Этот historical MVP behavior сохранён для legacy code ownership; release-v2 first run и settings принадлежат FEAT-013.

## First-run wizard {#wizard}

### Предусловия {#wizard.preconditions}

- Host bootstrap завершён, tray icon поднят (`spec://modules/platform/INFRA-001-windows-desktop-host-baseline#bootstrap.sequence`).
- Persistence layer инициализирован; `settings.json` либо отсутствует, либо содержит `"onboarding_completed": false`.
- Пока `onboarding_completed` не станет `true`, host обязан открывать wizard вместо normal idle mode.

### Навигация {#wizard.navigation}

- Wizard отображается как модальное окно, из которого нельзя перейти в основной shell.
- Навигация: **Back** / **Next** между шагами; **Finish** на последнем шаге.
- Back не теряет ранее введённые значения в рамках текущей wizard-сессии.
- Next доступен только если текущий шаг прошёл валидацию.
- Закрытие wizard-окна (крестик или Alt+F4) не завершает процесс, а сворачивает в tray. При повторном открытии из tray wizard возобновляется с того же шага с сохранёнными данными.
- Wizard может быть прерван через tray → Quit; в этом случае onboarding остаётся незавершённым.

### Шаги wizard {#wizard.steps}

#### Шаг 1: App language {#wizard.steps.language}

Пользователь выбирает:

| Поле | Default | Валидация |
|------|---------|-----------|
| UI language | `ru` | Выбрано одно значение из списка поддерживаемых языков |

- Первый запуск всегда начинается с выбора языка интерфейса.
- На стартовой волне поддерживаются `ru` и `en`, но UI и persistence model обязаны предполагать расширяемый список языков.

#### Шаг 2: Storage paths {#wizard.steps.storage}

Пользователь выбирает:

| Поле | Default | Валидация |
|------|---------|-----------|
| Recordings folder | `<Documents>/isTranscribe/Recordings` | Путь существует или может быть создан; доступен на запись |
| Transcripts folder | `<Documents>/isTranscribe/Transcripts` | Путь существует или может быть создан; доступен на запись |

- Если путь не существует, wizard предлагает создать его при нажатии Next.
- Каждое поле имеет кнопку **Browse…** для выбора через системный folder picker.

#### Шаг 3: Devices {#wizard.steps.devices}

Пользователь выбирает:

| Поле | Default | Валидация |
|------|---------|-----------|
| Determine devices automatically | `true` | — |
| Output device | System default | Хотя бы одно output device доступно, или включён automatic mode |
| Microphone | System default | Хотя бы один microphone доступен, или включён automatic mode |

- Списки устройств заполняются из текущих active devices системы.
- Когда **Determine devices automatically** включён, приложение не фиксируется на одной паре устройств: оно следует за актуальными устройствами, через которые meeting app или system default сейчас выводят звук и принимают микрофон, с учётом continuity rules из `spec://modules/app/FEAT-007-device-aware-recording-continuity#behavior`.
- В automatic mode explicit selectors остаются видимыми как информационный preview текущих output/mic, но не задают pinning.
- Когда **Determine devices automatically** выключен, пользователь может явно закрепить output и microphone.
- Если в системе нет ни одного output device или microphone, шаг показывает предупреждение, но не блокирует Next: пользователь сможет записывать с тем, что доступно.
- В degraded mode (`spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.states`) показывается информационный banner: «Process-specific capture недоступен на этой системе. Доступны device loopback и microphone.»

#### Шаг 4: Fireworks API Key {#wizard.steps.api-key}

Пользователь вводит:

| Поле | Default | Валидация |
|------|---------|-----------|
| Fireworks API key | пусто | Непустая строка |

- Поле ввода — password-style (замаскировано), с toggle-кнопкой **Show/Hide**.
- Wizard показывает optional кнопку **Test key**. Нажатие выполняет online probe к Fireworks API и возвращает один из результатов: success, auth failed, network/endpoint unavailable.
- Результат **Test key** informational-only: successful test не обязателен для Next/Finish, а failed test не блокирует wizard, если ключ непустой.
- При сохранении ключ передаётся в DPAPI-protected storage (`spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`). В `settings.json` ключ никогда не появляется в открытом виде.
- Под полем — ссылка-подсказка «Где получить API key?» со статичным URL документации Fireworks.

#### Шаг 5: Recording mode {#wizard.steps.recording-mode}

Пользователь выбирает:

| Поле | Default | Допустимые значения |
|------|---------|---------------------|
| Recording mode | `Ask` | `Off`, `Ask`, `Auto` |

- Каждый вариант сопровождается кратким описанием поведения (1–2 предложения).
- В degraded mode вариант `Auto` помечен пометкой, что process-specific auto-capture недоступен, но device loopback auto-detection работает.

#### Шаг 6: Applications {#wizard.steps.applications}

Пользователь настраивает product behavior для meeting apps, а не вручную перебирает текущие audio processes как основной сценарий first-run:

- Checkbox **Suggest new meeting apps automatically** по умолчанию включён. В wizard он задаёт безопасную начальную политику `ask_to_add`; выключение переводит policy в `off`. Более агрессивный `auto_add` остаётся доступен только в Settings.
- Основное содержимое шага — список известных locally installed meeting apps из встроенного catalog-а desktop clients. На стартовой волне catalog обязан включать как минимум `Zoom`; допустимо включать дополнительные well-known apps.
- Предзаполненные installed-app suggestions видимы пользователю как recommended toggles и удаляемы так же, как любые другие whitelist entries.
- Running processes с audio activity могут быть доступны только как secondary/manual affordance, а не как главный onboarding flow.
- Кнопка **Add manually…** позволяет ввести имя `.exe` вручную.
- Шаг не блокирует Finish при пустом whitelist: если выбран режим `Off` или `Force Record`, whitelist не обязателен.
- Если whitelist пуст и mode = `Auto` или `Ask`, wizard показывает предупреждение: «Без приложений в списке автоматическое обнаружение не будет работать. Вы сможете добавить приложения позже в Settings.» Предупреждение не блокирует Finish.
- Copy шага обязана объяснять, что whitelist влияет не только на текущие running processes, но и на будущие detection prompts/runtime matching.
- Wizard не показывает отказ от неизвестного app как одноразовое действие без последствий: copy должна объяснять, что future suggestions можно будет выключить или later reset в Settings.

### Завершение wizard {#wizard.completion}

- При нажатии **Finish** wizard атомарно:
  1. Создаёт/обновляет `settings.json` с выбранными значениями и defaults для всех полей, не показанных в wizard.
  2. Сохраняет API key в protected storage.
  3. Создаёт записи `AppRule` в БД для выбранных whitelist-процессов.
  4. Устанавливает `"onboarding_completed": true`.
- После commit wizard закрывается; host переходит в normal idle mode (tray-first или open window в зависимости от launch context).
- Если запись settings/secrets/DB fails, wizard показывает ошибку и не закрывается; пользователь может повторить или изменить настройки.

---

## Settings catalog {#settings}

### Общие правила settings surface {#settings.rules}

- Settings открываются из main window (вкладка или навигация).
- Все поля группированы в разделы согласно `spec://common/PROP-002-app-shell-and-settings#rules`.
- Каждый раздел — отдельная секция или tab.
- Изменения применяются по модели **Save per section**: внизу каждого раздела кнопка **Save**. При наличии несохранённых изменений кнопка активна и раздел помечен dirty-индикатором.
- При попытке покинуть раздел с несохранёнными изменениями — confirmation dialog: «Сохранить изменения?» / **Save** / **Discard** / **Cancel**.
- Невалидные поля блокируют Save и показывают inline validation message.
- Настройки персистятся в `settings.json` (`spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`); секреты — в `secrets.bin` через DPAPI.

### Применение изменений {#settings.apply}

Изменения настроек классифицируются по моменту вступления в силу:

| Категория | Когда применяется | Примеры |
|-----------|-------------------|---------|
| **Immediate** | Сразу после Save | Recording mode, notifications, language, API key, diarization, retry toggle |
| **Next-session** | С начала следующей recording session | Prebuffer duration, silence threshold, start/stop delay, merge window, default sources, device selection, switching policies |
| **Next-launch** | После перезапуска приложения | Autostart with Windows |

- Если изменение категории **next-session** сохраняется во время активной записи, текущая сессия продолжается со старыми значениями. UI показывает subtle note: «Изменение вступит в силу со следующей записи.»
- Изменение Recording mode на `Off` во время активной записи не останавливает текущую сессию; оно влияет только на автоматическое обнаружение новых сессий.

### General {#settings.general}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Autostart with Windows | `bool` | `true` | — | — |
| Minimize to tray on close | `bool` | `true` | — | — |
| Notifications | `bool` | `true` | — | — |
| UI language | `enum` | `ru` | `ru`, `en` | — |
| Hotkeys | `object` | см. [Hotkeys](#settings.general.hotkeys) | — | Нет конфликтов между hotkeys |

#### Hotkeys {#settings.general.hotkeys}

| Действие | Default | Заметки |
|----------|---------|---------|
| Start/Stop Force Record | нет | Опционально |
| Privacy Pause on/off | нет | Опционально |
| Discard current recording | нет | Опционально |
| Open main window | нет | Опционально |

- Каждый hotkey задаётся через поле-recorder: пользователь нажимает комбинацию клавиш.
- Кнопка **Clear** сбрасывает hotkey в «не назначен».
- При конфликте (две одинаковые комбинации) — inline error, Save заблокирован.
- Hotkeys регистрируются как global Windows hotkeys. Если регистрация не удаётся (комбинация занята другим приложением), показывается ошибка при Save, hotkey не применяется.

### Recording {#settings.recording}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Recording mode | `enum` | `Ask` | `Off`, `Ask`, `Auto` | — |
| Prebuffer duration | `enum` | `15s` | `5s`, `10s`, `15s`, `30s` | — |
| Silence threshold (dBFS) | `number` | `-40` | `-60` … `-20`, шаг `1` | В допустимом диапазоне |
| Start delay | `number` (сек.) | `2` | `1` … `10`, шаг `1` | В допустимом диапазоне |
| Stop delay | `number` (сек.) | `20` | `5` … `120`, шаг `5` | В допустимом диапазоне |
| Merge window | `number` (сек.) | `60` | `0` … `300`, шаг `10` | В допустимом диапазоне |
| Privacy pause policy | `enum` | `pause` | `pause`, `finish` | — |
| Default sources (Auto) | `flags` | `process_output + mic` | `process_output`, `device_loopback`, `mic` (min 1) | Хотя бы 1 source |
| Default sources (Force Record) | `flags` | `device_loopback + mic` | `device_loopback`, `mic` (min 1) | Хотя бы 1 source |

- `process_output` как source для Auto недоступен в degraded mode; если пользователь пытается включить его при degraded — показывается inline warning и source не добавляется.
- Start delay (`spec://common/PROP-003-audio-capture-and-device-observation#rules`): минимальная длительность сигнала до auto-start.
- Stop delay (`spec://common/PROP-004-meeting-session-and-data-model#rules`): задержка перед автоматической остановкой после тишины.
- Merge window (`spec://common/PROP-004-meeting-session-and-data-model#rules`): окно, в котором возвращение активности не создаёт новую сессию, а возобновляет предыдущую.

### Devices {#settings.devices}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Output device | `enum` | System default | Active output devices + "System default" | — |
| Microphone | `enum` | System default | Active microphones + "System default" | — |
| Follow system default (output) | `bool` | `true` | — | — |
| Follow system default (mic) | `bool` | `true` | — | — |
| Auto-discover output devices | `bool` | `true` | — | — |
| Auto-discover microphone | `bool` | `true` | — | — |
| Output change policy | `enum` | `seamless_switch` | `seamless_switch`, `end_and_start_new`, `ask` | — |
| Mic change policy | `enum` | `seamless_switch` | `seamless_switch`, `end_and_start_new`, `ask` | — |
| Active recording device policy | `enum` | `seamless_switch` | `seamless_switch`, `end_and_start_new`, `ask` | — |

- Списки устройств обновляются в реальном времени при открытии раздела Devices.
- Если выбранное фиксированное устройство недоступно, рядом с ним отображается warning badge; Save разрешён (устройство может вернуться).
- Когда "Follow system default" включён, конкретный device selector отключён (greyed out), но показывает текущее system default устройство как информацию.

### Applications {#settings.applications}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Whitelist | `list<AppRule>` | пуст | — | — |
| Auto-discovery policy | `enum` | `ask_to_add` | `auto_add`, `ask_to_add`, `off` | — |
| Ignored app suggestions | `list<string>` | пуст | — | — |
| Exclusions | `list<string>` | predefined system processes | — | — |

- **Whitelist** отображается как sortable list с полями: display name, process name, enabled toggle.
- **Add from running processes**: кнопка открывает picker со списком running processes (без system/excluded). Пользователь выбирает один или несколько.
- **Add manually**: кнопка открывает dialog для ввода `.exe` имени и display name.
- **Remove**: удаляет правило из whitelist.
- **Ordering**: drag-and-drop или кнопки up/down; порядок определяет приоритет при нескольких совпадениях.
- **Ignored app suggestions** показывают user-level denylist для приложений, которые пользователь явно отклонил в runtime prompt "добавить в whitelist?". Из этого списка пользователь может удалить app, чтобы разрешить future suggestions снова.
- Exclusions показаны как read-only list predefined system processes + пользовательские дополнения. Кнопка **Add exclusion** и **Remove** для пользовательских.
- Exclusions и ignored suggestions не смешиваются: exclusions отражают technical/system filtering, ignored suggestions отражают user rejection of future app prompts.

### Storage {#settings.storage}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Recordings folder | `path` | `<Documents>/isTranscribe/Recordings` | Доступный на запись путь | Путь существует или может быть создан |
| Transcripts folder | `path` | `<Documents>/isTranscribe/Transcripts` | Доступный на запись путь | Путь существует или может быть создан |
| Failed/temp folder | `path` | `<AppData>/temp` | Доступный на запись путь | Путь существует или может быть создан |
| Filename template | `string` | `YYYY-MM-DD HH-mm — {SourceApp} — {SessionId}` | Шаблон с плейсхолдерами | Содержит `{SessionId}` |
| Keep raw audio after successful transcription | `bool` | `true` | — | — |
| Temp retention period | `enum` | `7d` | `1d`, `3d`, `7d`, `14d`, `30d`, `never` | — |

- Browse-кнопки для каждого path-поля.
- Изменение recordings/transcripts folder не перемещает существующие файлы; новые сессии пишутся в новый путь. UI информирует: «Существующие записи останутся в прежней папке.»
- Failed/temp folder не доступен для кастомизации пользователем в wizard, но доступен в Settings для продвинутых пользователей.
- Filename template preview: под полем показывается пример сгенерированного имени файла на основе текущего шаблона.

### Transcription {#settings.transcription}

| Поле | Тип | Default | Допустимые значения | Валидация |
|------|-----|---------|---------------------|-----------|
| Fireworks API key | `secret` | — | Непустая строка | Непусто |
| Model | `string` | `whisper-v3-turbo` | Список поддерживаемых моделей | Из допустимого списка |
| Diarization | `bool` | `true` | — | — |
| Min speakers | `number` | `2` | `1` … `20` | ≤ Max speakers |
| Max speakers | `number` | `6` | `1` … `20` | ≥ Min speakers |
| Language | `enum` | `auto` | `auto`, `ru`, `en` + список ISO 639-1 кодов, поддерживаемых Fireworks | — |
| Auto retry | `bool` | `true` | — | — |
| Retry count | `number` | `3` | `1` … `10` | ≥ 1 если Auto retry включён |

- API key UX:
  - Если ключ уже сохранён, поле показывает маску: `fw_••••••••••••abcd` (префикс + последние 4 символа).
  - Кнопка **Change** очищает поле и переводит его в edit mode для ввода нового ключа.
  - Кнопка **Remove** удаляет ключ из protected storage после confirmation dialog.
  - При пустом ключе Save раздела Transcription блокируется; inline message: «API key обязателен для транскрибации.»
- Min/Max speakers: Min speakers disabled если Diarization = `false`. Max speakers disabled если Diarization = `false`.
- Retry count disabled если Auto retry = `false`.

---

## Degraded mode behavior {#degraded}

Когда host capability state = `degraded` (`spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.states`):

- **Wizard step 4 (Recording mode)**: `Auto` доступен, но с пометкой, что process-specific auto-capture не работает.
- **Settings → Recording → Default sources (Auto)**: `process_output` недоступен для выбора; если ранее был включён, показывается warning и source автоматически заменяется на `device_loopback`.
- **Settings → Devices**: устройства показываются нормально; ограничение затрагивает только capture adapter, а не device enumeration.
- Informational banner в верхней части Settings: «Эта система не поддерживает process-specific capture. Доступны device loopback и microphone.»
- Функциональность wizard и settings в остальном не ограничена.

---

## Acceptance {#acceptance}

FEAT-001 реализован, если одновременно выполнены все условия:

### Wizard {#acceptance.wizard}
1. При `onboarding_completed = false` host открывает wizard вместо normal idle mode.
2. Wizard проводит пользователя через 6 шагов в каноническом порядке: Language → Storage → Devices → API Key → Recording Mode → Applications.
3. Back/Next навигация работает без потери введённых данных.
4. Next заблокирован при невалидных данных текущего шага.
5. Закрытие окна wizard сворачивает в tray; повторное открытие восстанавливает состояние.
6. После Finish: `settings.json` создан со всеми defaults, API key в protected storage, `onboarding_completed = true`, whitelist-правила в БД.
7. После Finish host переходит в normal idle mode.
8. Wizard language step сохраняет выбранный UI language в `settings.json`, не требуя отдельного посещения Settings.
9. Wizard devices step явно поддерживает automatic mode и не создаёт впечатление, что пользователь навсегда pin-ит одну пару устройств.
10. Wizard API key step содержит optional кнопку **Test key**, которая не блокирует Next/Finish.
11. Wizard applications step содержит checkbox для prompt-based auto-suggestions и показывает installed known meeting apps как primary onboarding content; running-process picker остаётся secondary/manual.

### Settings surface {#acceptance.settings}
12. Settings UI содержит все 6 разделов: General, Recording, Devices, Applications, Storage, Transcription.
13. Каждое поле из [Settings catalog](#settings) отображается с корректным типом, default и валидацией.
14. Save per section: кнопка Save активна только при наличии изменений; невалидные поля блокируют Save.
15. Confirmation при покидании раздела с dirty state.
16. Next-session настройки не влияют на текущую активную запись; UI показывает соответствующую пометку.
17. Settings → Applications различает whitelist, ignored app suggestions и technical exclusions как отдельные пользовательские состояния.

### API key {#acceptance.api-key}
18. API key маскируется при отображении; доступны Change и Remove.
19. API key хранится только через DPAPI; никогда не появляется в `settings.json` или логах.
20. Пустой API key блокирует Save раздела Transcription.

### Degraded mode {#acceptance.degraded}
21. В degraded mode wizard и settings показывают информационный banner о недоступности process capture.
22. `process_output` как source для Auto не выбираем в degraded mode.

### Integrity {#acceptance.integrity}
23. Пользователь может полностью сконфигурировать MVP без внешнего редактирования файлов.
24. Изменение настроек не делает существующие записи или артефакты недоступными.
25. Settings UI не обходит platform rules для secrets и persistence.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-03: Spec rewritten to implementation-ready canon: wizard flow with 5 steps, full settings catalog with types/defaults/validation, save/apply semantics, API key UX, degraded mode behavior, 20 testable acceptance criteria.
- 2026-04-06: Wizard expanded to 6 steps with language first, device automatic mode messaging, optional Fireworks key test action, and applications-step prompt/suggestion behavior aligned with FEAT-005 and FEAT-007.
- 2026-04-06: Applications step reframed around known apps and suggestion behavior rather than active-process picking; settings catalog now distinguishes whitelist, ignored app suggestions and technical exclusions.
- 2026-07-11: Linked the one-surface release v2 onboarding and minimal settings replacement.
