---
status: active
---

# FEAT-010.A: Release V2 Localization {#root}

## Простыми словами {#plain-language}

Release v2 полностью работает на русском и английском. Русский используется по умолчанию, выбранный язык сохраняется, а переключение сразу обновляет открытые окна, tray, вопросы о записи и уведомления. Тексты остаются короткими, спокойными и помещаются в компактную Avalonia-оболочку.

## Goal {#goal}

Закрепить и завершить RU/EN-локализацию Avalonia release-v2 surface: ресурсный контракт, мгновенное переключение, сохранение выбора, форматирование, локализацию динамических состояний и доказательную bilingual QA.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#experience-canon`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#scope.in`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#settings`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#tray`

## Related {#related}

- `spec://modules/app/FEAT-011-meeting-detection-v2#root`
- `spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#modes`
- `spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#root`

## Supersedes {#supersedes}

- release-v2 architecture, behavior, decisions and acceptance from `spec://modules/app/FEAT-010-russian-localization#root`;
- WPF `.resx`, generated accessor, `x:Static`, per-window `ApplyLocalization`, six-step wizard language choice, system-locale formatting and informal «ты» decisions from the base wave.

The base FEAT-010 document remains the historical WPF/v1 slice. This change-spec governs the Avalonia release-v2 product.

## Scope {#scope}

### In scope {#scope.in}

- RU/EN resources for `IsTranscribe.Desktop`;
- Main, setup, settings, Ask, confirmation, help and diagnostics windows;
- passive notifications, tray menu, tray tooltip, status, error and accessibility copy;
- persisted `ru | en` setting with Russian default;
- immediate language switching for every already open release surface;
- known meeting-profile and migrated source-label localization;
- selected-language date, number, duration and plural formatting;
- static integrity gates and bilingual visual evidence;
- transcription modes, provider setup, model download/progress, speaker labels,
  transcript states and actionable errors from `FEAT-017`;

### Out of scope {#scope.out}

- non-release WPF `IsTranscribe.App` resources;
- installer copy owned by `INFRA-005.A`;
- logs, internal exceptions and raw diagnostic technical values;
- user-entered names, filesystem paths and artifact filenames;
- additional languages, RTL and macOS-specific polish;
- a separate language step in the compact first-run surface.

## Resource Contract {#resources}

- Canonical catalogs are `Localization/Resources/Strings.en.axaml` and `Strings.ru.axaml`.
- English is the base dictionary; the selected non-English dictionary overrides it. A damaged or incomplete runtime catalog therefore falls back to English and never exposes a raw key.
- Release gates require both shipped catalogs to contain the same non-empty key set, so a normal build never relies on mixed-language fallback.
- Keys use `String.<DomainOrSurface>.<SemanticRole>` with `.Format` and plural variants such as `.One | .Few | .Many` where required.
- XAML uses `DynamicResource`. Runtime-generated text uses `ILocalizationService.Get` or `Format`.
- Placeholder indexes and format clauses are identical between languages.
- Brand names, product names, glyph-only controls and user/system data may remain invariant.

## Runtime Switching {#switching}

- `ru` maps to `ru-RU`; `en` maps to `en-US`; missing or unsupported persisted values canonicalize to `ru`.
- Choosing a language in Settings updates the interface immediately and enters the existing auto-save flow.
- Runtime snapshots older than a pending local language edit do not revert the preview. A later persisted snapshot confirms the choice.
- Dynamic XAML resources update through the active dictionaries. Computed view-model text, dialogs, notifications and tray state subscribe to `LanguageChanged` and rebuild from stable resource keys or raw domain values.
- Transient status and error meaning survives a language switch. Copy may change language while the underlying status/error category stays unchanged.
- A successfully loaded migrated `en` setting applies before the first normal window becomes visible. If persistence cannot provide any settings during fatal startup, the safe fallback surface uses Russian.

## Surface Rules {#surfaces}

- Release user-facing copy is resource-backed across Main, setup, settings, Ask, confirmation, help, diagnostics, notifications and tray.
- Notification format arguments that represent known meeting sources are localized at render time, so an open notification changes fully with the selected language.
- Diagnostics localizes labels, UI language names, statuses and errors. Paths, version, architecture and operating-system values remain literal technical data.
- Known Zoom, Teams, Google Meet, Яндекс Телемост, Контур.Толк and generic-browser identities use friendly localized labels.
- Unknown, custom and user-owned source labels are preserved verbatim.
- Legacy and speaker-aware transcript access uses localized artifact actions.
- Speaker semantic roles localize at render/materialization time: `self` becomes
  `Я | Me`, `remote:<ordinal>` becomes `Собеседник N | Speaker N`, and
  `unknown:<ordinal>` becomes `Участник N | Participant N`.
- The three transcription modes and Groq/OpenRouter setup copy use resource keys
  and update immediately with the open Settings surface.

## Language And Tone {#copy}

- Russian copy is calm, plain and respectfully neutral. Action labels prefer infinitives such as `Открыть`, `Записать`, `Пропустить` and `Удалить`; direct guidance may use `вы` where it improves clarity.
- English copy is concise desktop UI language with the same meaning and action hierarchy.
- Terms stay aligned across state, settings, transcript, tray and notification
  surfaces: `запись`, `встреча`, `микрофон`, `системный звук`, `собеседник`,
  `транскрибация`, `папка записей`, `уведомления`, `диагностика`.
- Fireworks, ASR/diarization model choice, speaker count and separate automatic
  transcription toggle remain absent from release resources. API-key and retry
  copy exists only in the online transcription setup/recovery surfaces.

## Formatting {#formatting}

- Visible dates, numbers and plural forms follow the selected UI culture.
- Time zone and calendar values still come from the operating system.
- Persistence values, paths, logs, protocol identifiers and diagnostic codes remain culture-invariant.
- Russian Ask countdown follows `one | few | many`; English follows singular/plural behavior.

## Verification {#verification}

- Catalog tests prove key parity, code/XAML reference resolution and placeholder parity.
- Hardcoded-copy scans cover all release `.axaml` and user-facing C# presentation paths with a narrow invariant allowlist.
- Runtime tests cover default/canonical language, immediate Settings switching, stale-snapshot resistance, dialog/notification/error refresh and meeting-source localization.
- Visual Review renders the canonical RU/EN matrix at `100%`, `150%` and `200%` in light and dark themes, plus high-contrast. The bounded edge-case matrix renders in RU/EN at `100%` in light, dark and high-contrast because DPI changes physical scale while Avalonia preserves the same logical layout constraints.
- Windows acceptance in this item verifies a live RU → EN → RU switch in the open primary and Settings windows. Tray copy and refresh wiring are contract-tested here; the final live tray/Ask/notification matrix belongs to `INFRA-008`.

## Acceptance {#acceptance}

FEAT-010.A is complete when:

1. RU and EN catalogs are non-empty, key-identical and contain every release XAML/C# resource reference.
2. Placeholder contracts match and all formatted/plural paths resolve without raw keys or formatting errors.
3. All release user-facing copy is resource-backed within the declared invariant/data exceptions.
4. New and invalid settings use Russian; persisted or migrated English applies before the first normal surface.
5. Settings switches RU ↔ EN immediately, persists the value and resists stale runtime snapshot rollback.
6. Main, setup, settings, Ask, open confirmations, notifications, diagnostics and tray update without restart while preserving current semantic state.
7. Known meeting sources relocalize and unknown/custom labels remain unchanged.
8. The RU/EN canonical matrix passes layout, `100–200%` scaling, light/dark/high-contrast and artifact validation; the bounded edge-case matrix passes layout and artifact validation at `100%` in all three themes.
9. Live Windows RU → EN → RU acceptance passes for the primary and Settings windows; tray refresh wiring passes its resource/composition gate and is handed to the final live matrix in `INFRA-008`.
10. Release and legacy builds, formatting and complete automated tests are clean.

## Document Notes {#document-notes}

- 2026-08-30: Added bilingual FEAT-017 mode, provider, progress, speaker-label
  and transcript-state ownership without exposing model selection.
- 2026-07-12: Change-spec authored after the Avalonia FEAT-013 rebuild. It replaces the WPF `.resx` implementation decisions, adopts the existing paired ResourceDictionary architecture and adds runtime-state plus bilingual evidence gates.
- 2026-07-12: Final live tray/Ask/notification observation assigned to INFRA-008; FEAT-010.A retains resource/composition gates and the live primary/Settings switch.
