---
status: superseded
---

# FEAT-010: Russian Localization {#root}

## Простыми словами {#plain-language}

Приложение получает инфраструктуру локализации на базе .resx-ресурсов и полный перевод интерфейса на русский язык. При переключении языка (ru ↔ en) все строки меняются мгновенно, без перезапуска. Русский — язык по умолчанию.

## Goal {#goal}

Зафиксировать канонические решения по локализации: архитектура ресурсов, правила именования ключей, процесс извлечения строк, требования к русскому переводу и runtime-переключение языка.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#settings`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings`
- `spec://modules/app/FEAT-008-modern-ui-design-system#behavior.code-rules`

## Related {#related}

- `spec://modules/app/FEAT-009-settings-ux-simplification#behavior.labels`

## Superseded by {#superseded-by}

- `spec://modules/app/FEAT-010.A-release-v2-localization#root` for the Avalonia release-v2 architecture, behavior and acceptance.

## Scope {#scope}

### In scope {#scope.in}

- .resx инфраструктура: default (English) + Russian;
- извлечение всех user-facing строк (~330) из XAML и code-behind;
- полный русский перевод;
- runtime-переключение языка без перезапуска;
- правила именования ключей и организации ресурсов;
- tray context menu и notification strings.

### Out of scope {#scope.out}

- локализация логов, внутренних ошибок и DevTools текстов;
- right-to-left (RTL) layout;
- дополнительные языки помимо en и ru;
- автоматические переводы (все строки переводятся вручную);
- локализация формата даты/времени/чисел (используется system locale).

---

## Status Quo {#status-quo}

### Current state {#status-quo.current}

- **~200** уникальных hardcoded English строк в XAML (атрибуты `Text=`, `Content=`, `Header=`, `ToolTip=`, `Title=`).
- **~130** уникальных hardcoded English строк в code-behind (`.cs`): combo items, MessageBox тексты, validation messages, status labels, tray menu items.
- **0** `.resx` файлов.
- **0** инфраструктуры локализации: нет `x:Uid`, нет `DynamicResource` для строк, нет `IStringLocalizer`.
- `GeneralSettings.Language` существует (default: `"ru"`), сохраняется в `settings.json`, но **не влияет на UI** — нет кода, который читает setting и переключает строки.
- Wizard step 1 предлагает выбор языка (ru/en), но выбор только сохраняется в настройки без эффекта.

---

## Behavior {#behavior}

### Resource architecture {#behavior.architecture}

#### File structure {#behavior.architecture.files}

```
src/IsTranscribe.App/
  Strings/
    Strings.resx              — default (English)
    Strings.ru.resx           — Russian
```

Один файл на язык. Не разбивается на несколько .resx по окнам — при ~330 строках единственный файл остаётся управляемым.

#### Key naming convention {#behavior.architecture.keys}

Формат: `{Surface}_{Section}_{Element}`

Примеры:

| Key | English value |
|-----|--------------|
| `Shell_Home_Title` | Home |
| `Shell_Settings_Recording_Header` | Recording |
| `Shell_Settings_Recording_Mode_Label` | Recording mode |
| `Shell_Settings_Recording_SilenceThreshold_Desc` | Audio level below which silence is detected |
| `Wizard_Step1_Title` | App language |
| `Wizard_Step1_Description` | Choose your preferred interface language |
| `Tray_Menu_ForceRecord` | Start Force Record |
| `Tray_Menu_Quit` | Quit |
| `Dialog_QuitConfirm_Title` | Confirm quit |
| `Dialog_QuitConfirm_Message` | Recording is in progress. Are you sure you want to quit? |
| `Validation_Prebuffer_Invalid` | Prebuffer must be 5, 10, 15 or 30 seconds. |
| `Status_Recording_Active` | Recording: active |
| `Recording_Preset_Meetings` | Meetings |
| `Recording_Preset_QuickNotes` | Quick notes |
| `Recording_Preset_Custom` | Custom |

Правила:
- PascalCase для каждого сегмента.
- `_Desc` суффикс для description-текстов под полями.
- `_Label` суффикс для labels рядом с контролами.
- `_Title` суффикс для заголовков окон и секций.
- `_Message` суффикс для тел диалоговых окон.
- Перечисления (combo items): `{Surface}_{Field}_{Value}`, например `Settings_Recording_Mode_Off`, `Settings_Recording_Mode_Auto`.

#### Generated accessor {#behavior.architecture.accessor}

Visual Studio генерирует `Strings` class из `Strings.resx` автоматически (designer file). Доступ:

```csharp
// Code-behind
string label = Strings.Shell_Settings_Recording_Mode_Label;
```

XAML binding через `x:Static`:

```xml
<TextBlock Text="{x:Static strings:Strings.Shell_Home_Title}" />
```

Namespace mapping в каждом XAML файле:

```xml
xmlns:strings="clr-namespace:IsTranscribe.App.Strings"
```

### XAML string extraction {#behavior.xaml}

Все hardcoded text attributes в XAML заменяются на `{x:Static strings:Strings.KeyName}`:

Before:
```xml
<TextBlock Text="Recording mode" />
<Button Content="Save" />
<CheckBox Content="Autostart with Windows" />
```

After:
```xml
<TextBlock Text="{x:Static strings:Strings.Settings_Recording_Mode_Label}" />
<Button Content="{x:Static strings:Strings.Common_Save}" />
<CheckBox Content="{x:Static strings:Strings.Settings_General_Autostart}" />
```

### Code-behind string extraction {#behavior.codebehind}

Все user-facing hardcoded строки в `.cs` заменяются на `Strings.KeyName`:

Before:
```csharp
MessageBox.Show("Recording is in progress. Are you sure you want to quit?", "Confirm quit", ...);
```

After:
```csharp
MessageBox.Show(Strings.Dialog_QuitConfirm_Message, Strings.Dialog_QuitConfirm_Title, ...);
```

Строки, комбинирующие runtime-данные, используют `string.Format`:

```csharp
// Strings.resx: Status_Recording_Duration = "Recording: {0}"
string status = string.Format(Strings.Status_Recording_Duration, elapsed);
```

### Runtime language switching {#behavior.switching}

#### Mechanism {#behavior.switching.mechanism}

При изменении `GeneralSettings.Language`:

1. `Thread.CurrentThread.CurrentUICulture` устанавливается на выбранный `CultureInfo`.
2. `Strings.Culture` устанавливается на ту же culture.
3. Все окна обновляют свои bindings.

#### XAML refresh {#behavior.switching.refresh}

`x:Static` bindings не обновляются автоматически при смене culture. Для runtime-переключения без перезапуска используется подход:

- Вводится `LocalizationManager` class с событием `LanguageChanged`.
- Каждое окно подписывается на `LanguageChanged` и перезагружает свои локализованные значения.
- Это реализуется через helper-метод `ApplyLocalization()` в каждом window code-behind, который вызывается:
  1. При загрузке окна (`Loaded` event).
  2. При получении `LanguageChanged` event.

Альтернативный вариант (допустим при реализации): использование `Binding` + `IValueConverter` вместо `x:Static` для автоматического обновления.

#### Apply timing {#behavior.switching.timing}

Категория apply: **Immediate** (определено в FEAT-001). Переключение языка не требует перезапуска приложения.

### Default language {#behavior.default}

Default language: **Russian** (`"ru"`).

Обоснование: целевая аудитория — русскоязычные пользователи; это соответствует текущему default в `GeneralSettings.Language`.

При отсутствии `Strings.ru.resx` ресурса для ключа — fallback на default `Strings.resx` (English). Это стандартное поведение .NET resource resolution.

### Translation guidelines {#behavior.translation}

Требования к русскому переводу:

1. **Естественный русский язык**, не калька с английского.
2. **Ты-обращение** (не «вы»): «Запусти запись», не «Запустите запись». Это согласуется с informal tone приложения.
3. **Единый терминологический словарь** (определён ниже).
4. **Длина строк**: русский текст в среднем на 20-30% длиннее английского; UI layout должен это вмещать.
5. **Placeholder-ы** (`{0}`, `{1}`) сохраняют порядок.

### Terminology glossary {#behavior.terminology}

| English | Russian | Notes |
|---------|---------|-------|
| Recording | Запись | |
| Force Record | Принудительная запись | |
| Privacy Pause | Пауза конфиденциальности | |
| Transcription | Транскрипция | |
| Diarization | Разделение по говорящим | |
| Whitelist | Список отслеживания | Не «белый список» |
| Exclusion | Исключение | |
| Prebuffer | Предзапись | |
| Silence threshold | Порог тишины | |
| Merge window | Окно объединения | |
| Output device | Устройство вывода | |
| Microphone | Микрофон | |
| Hotkey | Горячая клавиша | |
| Tray | Трей | Устоявшийся термин |
| Settings | Настройки | |
| API key | API-ключ | |
| Start delay | Задержка начала | |
| Stop delay | Задержка остановки | |

### String categories {#behavior.categories}

| Category | Est. count | Source |
|----------|-----------|--------|
| XAML labels and headers | ~80 | `Text=`, `Header=`, `Content=` |
| XAML descriptions and hints | ~40 | Longer `Text=` in descriptions |
| XAML button content | ~20 | `Content=` on buttons |
| Combo box items | ~40 | OptionItem strings in code-behind |
| MessageBox / dialogs | ~20 | `MessageBox.Show()` calls |
| Status / state texts | ~30 | Dynamic labels set from code-behind |
| Tray menu items | ~12 | `TrayIconController` |
| Validation messages | ~15 | Settings handlers |
| Wizard-specific | ~25 | Wizard window step texts |
| Notification texts | ~10 | Toast/balloon notifications |
| Format strings with placeholders | ~15 | `string.Format` templates |
| Window titles | ~7 | `Title=` on windows |
| **Total** | **~330** | |

---

## Canonical Decisions {#decisions}

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Resource format | .resx | Standard .NET localization; designer-generated accessor; VS tooling support |
| File granularity | Single Strings.resx + Strings.ru.resx | ~330 strings manageable in one file; no premature splitting |
| XAML binding | `x:Static` + runtime refresh | Compile-time safety; explicit refresh on language change |
| Default language | Russian (ru) | Target audience; matches existing GeneralSettings default |
| Fallback | English | .NET standard resource fallback chain |
| Addressing style | Ты-обращение | Informal product tone |
| Runtime switching | Immediate, no restart | FEAT-001 requirement |

---

## Acceptance {#acceptance}

FEAT-010 считается завершённой, если одновременно выполнено:

1. Файлы `Strings/Strings.resx` (English) и `Strings/Strings.ru.resx` (Russian) существуют с полным набором строк.
2. В XAML файлах **0** hardcoded English text strings — все заменены на `{x:Static strings:Strings.*}`.
3. В code-behind **0** hardcoded user-facing English строк — все заменены на `Strings.*`.
4. Переключение языка в Settings → General → Language мгновенно меняет все строки без перезапуска.
5. Default language: `"ru"`. При первом запуске (и в wizard) интерфейс на русском.
6. Русский перевод соответствует терминологическому словарю из `#behavior.terminology`.
7. Русский перевод не обрезается и не переполняет layout (учтён 20-30% рост длины).
8. При выборе `"en"` весь интерфейс на английском.
9. Tray context menu и notification texts локализованы.
10. `dotnet build isTranscribe.sln` и `dotnet test isTranscribe.sln` проходят без ошибок.
11. Ключи в .resx соответствуют naming convention из `#behavior.architecture.keys`.

---

## Document Notes {#document-notes}

- 2026-07-12: Avalonia release-v2 localization moved to FEAT-010.A; this document remains the historical WPF/v1 slice.
- 2026-04-13: Spec authored from user feedback on missing Russian localization; codebase analysis shows ~330 hardcoded English strings, zero .resx infrastructure, and a Language setting that exists but has no runtime effect.
