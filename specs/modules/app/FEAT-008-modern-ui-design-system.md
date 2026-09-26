---
status: superseded
---

# FEAT-008: Modern UI Design System {#root}

## Простыми словами {#plain-language}

Приложение переходит на единую дизайн-систему WPF-UI (Fluent 2): все окна, контролы и цвета определяются централизованно через ResourceDictionary, вместо раскиданных по XAML hardcoded hex-значений. Появляется поддержка светлой и тёмной темы, а визуальный язык приложения приводится к нативному Windows 11 виду.

## Goal {#goal}

Зафиксировать канонические решения по дизайн-системе приложения: выбор UI-фреймворка, структура тем и ресурсов, миграция всех поверхностей, контракт на светлую/тёмную тему и правила, по которым новый UI-код должен писаться.

## Superseded by {#superseded-by}

- Release v2 moves to Avalonia through `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#root` and is visually governed by `spec://modules/app/FEAT-013-minimal-desktop-experience#root`.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings`

## Related {#related}

- `spec://common/PROP-001-product-canon#rules`
- `spec://modules/app/FEAT-009-settings-ux-simplification#root`
- `spec://modules/app/FEAT-010-russian-localization#root`

## Scope {#scope}

### In scope {#scope.in}

- выбор и интеграция WPF-UI (Fluent 2) как единственного UI-фреймворка;
- выделение всех цветов, типографики и отступов в ResourceDictionary;
- миграция всех окон приложения на WPF-UI контролы и layout;
- поддержка светлой и тёмной темы с runtime-переключением;
- каноническая палитра, typography scale и spacing scale;
- правила для нового UI-кода: запрет inline hex-цветов, обязательные ссылки на ресурсы.

### Out of scope {#scope.out}

- реструктуризация вкладок настроек (FEAT-009);
- локализация строк (FEAT-010);
- переработка функционального поведения любых поверхностей;
- custom анимации и transitions (допускаются только дефолтные из WPF-UI);
- responsive layout для размеров окон меньше текущего minimum.

---

## Status Quo {#status-quo}

Текущее состояние UI, зафиксированное на момент написания спеки.

### Surfaces {#status-quo.surfaces}

| Window | Role | Approx controls |
|--------|------|----------------|
| MainShellWindow | Primary shell: Home / Current Recording / Settings (6 sub-tabs) | ~90 |
| FirstRunWizardWindow | Onboarding wizard (6 steps) | ~40 |
| BlockedStartupWindow | Error/blocked state banner | ~5 |
| RecoveryWindow | Recovery flow | ~5 |
| ManualApplicationRuleWindow | Dialog: add app rule | ~5 |
| RunningProcessPickerWindow | Dialog: pick running process | ~10 |
| SingleValuePromptWindow | Generic single-value prompt | ~5 |

### Current styling approach {#status-quo.styling}

- **0** ResourceDictionary files.
- **18** unique hex color values используются inline, **69** вхождений суммарно.
- **App.xaml** пуст — ни глобальных стилей, ни тем.
- Все стили заданы через прямые свойства в XAML (`Background="#F3F4F6"`, `Foreground="#1F2937"` и т.д.).
- Нет тёмной темы, нет runtime-переключения.

---

## Behavior {#behavior}

### UI framework {#behavior.framework}

Единственный UI-фреймворк: **WPF-UI** (NuGet `WPF-UI`, https://github.com/lepoco/wpfui).

Каноническое обоснование:
- Fluent 2 design language, нативный для Windows 11;
- предоставляет `FluentWindow`, `NavigationView`, `CardControl`, `TextBox`, `Button` и прочие контролы с Fluent-стилями из коробки;
- light/dark theme switching через `ApplicationThemeManager`;
- активно поддерживается, совместим с .NET 8 и WPF.

Не добавляются другие UI-библиотеки (Material Design, MahApps и т.д.).

### Resource architecture {#behavior.resources}

#### Color tokens {#behavior.resources.colors}

Все цвета определяются как именованные ресурсы в `src/IsTranscribe.App/Themes/AppColors.xaml`:

| Token | Light | Dark | Usage |
|-------|-------|------|-------|
| `AppBackgroundBrush` | `#F3F4F6` | `#1A1A1A` | Window/page background |
| `AppSurfaceBrush` | `#FFFFFF` | `#2D2D2D` | Cards, panels |
| `AppBorderBrush` | `#E5E7EB` | `#404040` | Dividers, card borders |
| `AppTextPrimaryBrush` | `#1F2937` | `#F3F4F6` | Primary text |
| `AppTextSecondaryBrush` | `#4B5563` | `#A1A1AA` | Descriptions |
| `AppTextTertiaryBrush` | `#6B7280` | `#71717A` | Hints, placeholders |
| `AppAccentBrush` | `#3730A3` | `#818CF8` | Active/selected states |
| `AppAccentBackgroundBrush` | `#EEF2FF` | `#312E81` | Info panels |
| `AppWarningBrush` | `#B45309` | `#F59E0B` | Warnings |
| `AppWarningBackgroundBrush` | `#FEF3C7` | `#422006` | Warning panels |
| `AppErrorBrush` | `#B91C1C` | `#EF4444` | Validation errors |
| `AppErrorBackgroundBrush` | `#FEE2E2` | `#450A0A` | Error panels |
| `AppSuccessBrush` | `#15803D` | `#4ADE80` | Success indicators |

Конкретные hex-значения для dark theme выбираются при реализации и обязаны обеспечивать WCAG AA contrast ratio (≥ 4.5:1 для text, ≥ 3:1 для large text/UI).

Inline hex-значения в XAML запрещены. Каждый цвет должен ссылаться на токен через `{DynamicResource AppTextPrimaryBrush}`.

#### Typography scale {#behavior.resources.typography}

Именованные стили определяются в `Themes/AppTypography.xaml`:

| Style | FontSize | FontWeight | Usage |
|-------|----------|------------|-------|
| `AppTitleStyle` | 28 | SemiBold | Window titles |
| `AppHeadingStyle` | 20 | SemiBold | Section headers |
| `AppSubheadingStyle` | 16 | Medium | Sub-section headers |
| `AppBodyStyle` | 14 | Regular | Body text (default) |
| `AppCaptionStyle` | 12 | Regular | Captions, hints |

Базовый font family: `Segoe UI Variable` (если доступен), fallback `Segoe UI`.

#### Spacing scale {#behavior.resources.spacing}

Канонический spacing scale для margins и paddings:

| Token | Value |
|-------|-------|
| `SpaceXs` | 4 |
| `SpaceSm` | 8 |
| `SpaceMd` | 12 |
| `SpaceLg` | 16 |
| `SpaceXl` | 20 |
| `Space2xl` | 24 |
| `Space3xl` | 32 |

Inline numeric margins допустимы только когда точное значение не совпадает ни с одним токеном и используется однократно.

### Resource files structure {#behavior.resources.files}

```
src/IsTranscribe.App/
  Themes/
    AppColors.xaml          — color tokens (light + dark aware)
    AppTypography.xaml       — text styles
    AppSpacing.xaml          — spacing constants
```

`App.xaml` регистрирует все три словаря:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ui:ThemesDictionary Theme="Light" />
            <ui:ControlsDictionary />
            <ResourceDictionary Source="Themes/AppColors.xaml" />
            <ResourceDictionary Source="Themes/AppTypography.xaml" />
            <ResourceDictionary Source="Themes/AppSpacing.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

### Window migration {#behavior.windows}

#### MainShellWindow {#behavior.windows.main-shell}

- Класс мигрирует на `ui:FluentWindow`.
- Root layout: `ui:NavigationView` с тремя items вместо `TabControl`:
  - Home
  - Current Recording
  - Settings
- `NavigationView` предоставляет sidebar navigation с иконками.
- Каждый navigation item загружает свой content area через content control (не UserControl/Page — остаётся inline content switching, как сейчас).
- Sidebar collapse при малой ширине окна: WPF-UI `NavigationView` делает это из коробки.

#### FirstRunWizardWindow {#behavior.windows.wizard}

- Мигрирует на `ui:FluentWindow`.
- Sidebar (step list) сохраняется как отдельная panel с WPF-UI стилями.
- Content area использует WPF-UI контролы.
- Кнопки Back / Next / Finish / Quit используют `ui:Button` с Appearance вариантами.

#### Dialog windows {#behavior.windows.dialogs}

- `ManualApplicationRuleWindow`, `RunningProcessPickerWindow`, `SingleValuePromptWindow` — мигрируют на `ui:FluentWindow`.
- `BlockedStartupWindow`, `RecoveryWindow` — мигрируют на `ui:FluentWindow` с информационным layout.

### Theme switching {#behavior.theme}

#### Setting {#behavior.theme.setting}

Новое поле в `GeneralSettings`:

```
AppTheme: "system" | "light" | "dark"
```

Default: `"system"`.

В режиме `"system"` приложение следует за системной темой Windows через `SystemThemeWatcher`.

#### Runtime behavior {#behavior.theme.runtime}

- Переключение темы происходит через `ApplicationThemeManager.Apply()`.
- `AppColors.xaml` содержит `DynamicResource` brush-ы, которые WPF-UI runtime автоматически переключает при смене темы.
- Переключение не требует перезапуска приложения.
- При переключении на `"system"` приложение подписывается на `SystemThemeWatcher` и реагирует на смену системной темы.

#### Apply timing {#behavior.theme.apply}

Theme change: **Immediate** (как и `ui_language` в FEAT-001).

### Code rules {#behavior.code-rules}

Обязательные правила для любого нового или изменяемого UI-кода:

1. **Нет inline hex-цветов.** Любой цвет — через `{DynamicResource TokenName}`.
2. **Нет inline FontSize/FontWeight.** TextBlock/Label — через именованный `Style`.
3. **Margins/Paddings** — через spacing tokens или через прямые числа, если одноразовые и в пределах scale.
4. **Новые контролы** — только из WPF-UI namespace или standard WPF.
5. **Images/icons** — через WPF-UI `SymbolIcon` / `SymbolRegular` enum, не через custom bitmap assets.

---

## Canonical Decisions {#decisions}

| Decision | Choice | Rationale |
|----------|--------|-----------|
| UI framework | WPF-UI (Fluent 2) | Нативный Windows 11 look, active maintenance, .NET 8 compatible |
| Color definition | ResourceDictionary с DynamicResource | Поддержка runtime theme switching |
| Theme default | `"system"` | Уважает пользовательскую системную тему |
| Font family | Segoe UI Variable / Segoe UI | Windows system font; нет внешних шрифтов |
| Navigation model | `NavigationView` вместо `TabControl` | Современная sidebar navigation с collapse |
| Icon set | WPF-UI `SymbolRegular` | Fluent 2 icon set, встроен в библиотеку |

---

## Migration Plan {#migration}

Порядок миграции поверхностей:

1. **App.xaml** — подключить WPF-UI themes и resource dictionaries.
2. **AppColors.xaml, AppTypography.xaml, AppSpacing.xaml** — создать и заполнить токены.
3. **MainShellWindow** — мигрировать на FluentWindow + NavigationView + DynamicResource.
4. **FirstRunWizardWindow** — мигрировать на FluentWindow + WPF-UI контролы.
5. **Dialog windows** (4 шт.) — мигрировать на FluentWindow.
6. **Code-behind** — убрать все hardcoded цвета из `.xaml.cs` (если есть).
7. **Theme setting** — добавить `AppTheme` в GeneralSettings, wiring в UI.

Каждый шаг должен компилироваться и не ломать существующие тесты.

---

## Acceptance {#acceptance}

FEAT-008 считается завершённой, если одновременно выполнено:

1. NuGet `WPF-UI` добавлен в `IsTranscribe.App.csproj` и builds clean.
2. `App.xaml` регистрирует WPF-UI theme dictionaries и custom resource dictionaries.
3. Существуют файлы `Themes/AppColors.xaml`, `Themes/AppTypography.xaml`, `Themes/AppSpacing.xaml` с полным набором токенов из `#behavior.resources`.
4. `MainShellWindow` использует `FluentWindow` + `NavigationView` с тремя navigation items (Home, Current Recording, Settings).
5. Все 7 XAML-окон мигрированы на `FluentWindow` и WPF-UI контролы.
6. В XAML файлах **0** inline hex-цветов — все цвета через `DynamicResource`.
7. Светлая и тёмная тема переключаются через `AppTheme` setting (`"system"`, `"light"`, `"dark"`) без перезапуска.
8. В режиме `"system"` приложение следует за системной темой Windows.
9. WCAG AA contrast ratio для text (≥ 4.5:1) соблюдён в обеих темах.
10. `dotnet build isTranscribe.sln` и `dotnet test isTranscribe.sln` проходят без ошибок.
11. Все existing функциональные surfaces сохраняют свою функциональность (навигация, settings save, recording controls, wizard flow).

---

## Document Notes {#document-notes}

- 2026-04-13: Spec authored from user feedback on weak app design; based on codebase analysis showing 18 unique hardcoded hex colors across 69 inline usages with zero ResourceDictionary infrastructure.
- 2026-07-11: Linked the Avalonia and minimal experience release v2 replacement.
