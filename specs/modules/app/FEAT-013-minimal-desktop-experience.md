---
status: active
---

# FEAT-013: Minimal Desktop Experience {#root}

## Простыми словами {#plain-language}

isTranscribe должен ощущаться как современный лёгкий companion: обычно он тихо живёт в tray, а открытое окно сразу отвечает на три вопроса — включён ли сервис, идёт ли запись и где последняя встреча. Технические детали доступны для диагностики и не перегружают основной путь.

## Goal {#goal}

Реализовать высококачественную Avalonia-оболочку release v2: компактную primary window, одну setup surface, ненавязчивый Ask prompt, state-driven recording controls и минимальные настройки.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#experience-canon`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline`
- `spec://modules/app/FEAT-011-meeting-detection-v2#user-policy`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization`
- `spec://modules/app/FEAT-004-recordings-home-and-artifact-access#root`

## See also {#see-also}

- `spec://modules/app/FEAT-010.A-release-v2-localization#root`
- `spec://modules/app/FEAT-013.A-floating-recording-widget#root`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#root`

## Supersedes {#supersedes}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard`
- `spec://modules/app/FEAT-008-modern-ui-design-system#behavior.windows.main-shell`
- `spec://modules/app/FEAT-009-settings-ux-simplification#behavior`

## Scope {#scope}

### In scope {#scope.in}

- visual direction exploration and selection;
- semantic design tokens and current desktop visual language;
- compact primary window;
- state-driven listening/recording/processing/history UI;
- Ask prompt;
- one-surface onboarding;
- minimal settings and advanced diagnostics boundary;
- tray menu and notifications;
- ru/en localization for all release-v2 strings;
- accessibility, scaling, keyboard and theme QA;
- rendered visual review artifacts.

### Out of scope {#scope.out}

- transcript editor and speaker-renaming UI;
- analytics dashboard;
- audio waveform editor;
- cloud/account surfaces;
- macOS-specific final polish;
- marketing site.

## Design Direction Gate {#design-gate}

- Selected direction: **Calm Instrument**, with the quieter typographic rhythm and whitespace discipline explored in **Quiet Paper**.
- The review artifact is `artifacts/design/FEAT-013/FEAT-013 Design Directions.html`; it contains three comparable directions, eight canonical states and light/dark theme previews.
- Before production view implementation, create at least three meaningfully different high-fidelity directions for the same canonical states.
- References and component patterns must come from current desktop systems and products released or materially refreshed within the previous five years.
- Selection records typography, palette, materials, iconography, radius, spacing, density and motion rules.
- The chosen system has a distinct calm companion character and avoids a generic admin/dashboard appearance.
- Visual approval is based on rendered screens at normal and dark themes, not XAML inspection alone.

Release surfaces exclude these legacy shapes:

- permanent 220px navigation sidebar for three destinations;
- nested tab controls for settings;
- six-step wizard;
- a wall of status badges, capability labels and filesystem paths;
- duplicated recording actions across multiple pages;
- raw process names, dBFS values and device policy enums in the primary experience;
- hardcoded white topmost dialog disconnected from the app theme.

## Primary Window {#primary-window}

### Geometry {#primary-window.geometry}

- Default content footprint targets approximately `460 × 620` logical pixels.
- Minimum usable footprint is `420 × 520` with vertical adaptation.
- Window remembers a valid position and never restores fully off-screen.
- Content supports `100–200%` scaling without clipping primary actions.

### Information hierarchy {#primary-window.hierarchy}

The primary window contains:

1. product identity and service toggle/state;
2. one contextual primary action;
3. current activity area that morphs by runtime state;
4. recent recordings list;
5. compact access to settings/help/quit.

There is no separate permanent `Current Recording` destination. When a session starts, the activity area displays timer, source label, pause/resume and finish. The timer measures active recorded time and stays frozen while recording is paused. Destructive discard is placed in a secondary confirmed action.

### State presentation {#primary-window.states}

- `listening` — calm ready state, `Записать сейчас` primary action;
- `suspected` — subtle transient observation, no alarming progress UI;
- `awaiting_confirmation` — prompt owns the decision while the main window remains coherent;
- `recording` — clear red/accent recording affordance, timer and `Завершить`;
- `processing` — bounded progress/status with permission to close to tray;
- `ready` — recent item exposes `Открыть запись` and `Открыть папку`;
- `attention_required` — plain-language recovery action;
- `paused` — explicit service-off state with `Включить`.

## Ask Prompt {#ask-prompt}

- Prompt is a small theme-aware non-modal window near the active work area/taskbar edge.
- It does not steal keyboard focus while the user is typing unless platform accessibility behavior requires focus.
- Primary copy: `Похоже, началась встреча` plus friendly app label.
- Primary actions: `Записать` and `Пропустить`.
- Secondary menu: `Игнорировать это приложение` and diagnostics reason when advanced mode is enabled.
- Countdown is visually quiet; timeout resolves to Skip.
- The prompt supports keyboard actions and screen-reader names.
- Only one prompt is visible.

## First Run {#first-run}

- First run is one setup surface, not a stepper.
- Defaults: service listening, Ask policy, system output, system default microphone, app-owned recordings folder, autostart offered clearly.
- Supported meeting services appear as a preselected concise list that can be edited later. Browser-based services stay available regardless of which supported browser is currently installed or running; desktop availability may be highlighted without hiding a service.
- Browser hosts such as Chrome, Edge, Firefox and Zen do not appear as separate meeting-service choices. Migrated rules for supported browser executables are removed from the release-v2 catalog and detection runtime.
- Completion requires one primary action and does not require an API key.
- Permission/capability problems are explained inline with a direct retry or settings action.

## Settings {#settings}

Normal settings expose:

- service launch/autostart;
- monitored and ignored apps;
- microphone choice and follow-system-default;
- recordings folder;
- theme and language;
- notifications;
- transcription mode `Без транскрибации | Включить локальную транскрибацию |
  Включить онлайн-транскрибацию` according to `FEAT-017`.

Rules:

- settings use grouped sections or a compact sheet with direct navigation, not nested tabs;
- the generic fallback is labelled `Другие сервисы в браузере` and explains that it covers services absent from the named list across supported browsers;
- changes auto-save after validation;
- technical timing, score, device continuity and codec values are internal defaults;
- ASR model, diarization model/toggle and speaker-count values are internal and
  absent from normal settings;
- advanced diagnostics is a separate surface with copy/export/open-logs actions;
- legacy settings remain migrated, while obsolete controls are absent from release UI.

## Recent Recordings {#recent-recordings}

- Recent items show friendly app/title, local date/time, duration and a simple readiness state.
- Ready items can receive a user-owned display title from an icon-only rename action; renaming keeps the audio filename and path stable.
- Primary item action opens the audio file.
- Secondary actions open containing folder or remove the history entry/file through an explicit confirmation policy.
- Existing and new transcript files are reachable from the recording artifact
  menu; transcript reading does not introduce a separate primary navigation
  destination.
- Empty, loading, processing, failed/recovery and populated states have designed layouts.

## Visual System {#visual-system}

- Semantic color tokens cover background, surface, text, accent, recording, success, warning and danger roles.
- Typography uses platform-appropriate system fonts with a compact readable scale.
- Icons use one current coherent icon family.
- Motion communicates state changes with short transitions and respects reduced-motion preferences.
- Light, dark and system themes are supported from the same semantic tokens.
- Focus states, contrast and hit areas meet desktop accessibility expectations.

## Tray And Notifications {#tray}

Tray menu contains service state, `Записать сейчас`, contextual stop/pause action, `Открыть isTranscribe` and `Выйти`.

Tray tooltip/icon communicates listening, recording, processing, attention and paused states. Notifications are reserved for Ask prompt, recording ready and actionable failure.

## Verification {#verification}

- Render every canonical state at `100%`, `150%` and `200%` scaling in light/dark themes.
- Verify keyboard traversal, screen-reader labels, reduced motion and high-contrast behavior.
- Run a usability script from install through first saved recording without exposing advanced settings.
- Confirm no release-v2 string is hardcoded outside localization resources.
- Confirm obsolete Fireworks, model, diarization, speaker-count and separate
  automatic-transcription controls are absent while the three canonical modes
  remain visible.
- Perform visual regression/screenshot review before acceptance.

## Acceptance {#acceptance}

FEAT-013 is complete when:

1. A selected design direction and token system are implemented consistently.
2. One compact primary window covers all runtime states without duplicated navigation.
3. First run completes on one surface without API configuration.
4. Ask prompt is theme-aware, non-modal, accessible and safe on timeout.
5. Normal settings contain only the release-v2 catalog and auto-save correctly.
6. Recent recording actions work for new and legacy artifacts.
7. All render, scaling, theme, keyboard and localization verification passes.
8. User-approved visual review confirms the product meets the modern lightweight direction.

## Document Notes {#document-notes}

- 2026-08-30: Added the three-mode FEAT-017 transcription setting and retained
  model/diarization internals outside normal UI.

- 2026-07-13: Browser hosts were separated from meeting-service choices: migrated Zen/browser process rules are removed from UI and detection runtime, while one clearly explained generic browser-service fallback remains.
- 2026-07-13: Manual-recording feedback clarified a pause-aware active timer, restrained finish-button interaction, persistent display-title rename and successful Windows shell dispatch without requiring a new process handle.
- 2026-07-13: Live review clarified overflow alignment, restrained primary hover, duplicate-free ready/privacy hierarchy, third-person product voice and explanatory application-list copy within the existing visual-system and settings canon.
- 2026-07-12: First-run application catalog clarified from an installed-only subset to the full concise supported catalog. This keeps Google Meet, Яндекс Телемост, Контур.Толк and other browser meetings available in Chrome, Zen and other supported browsers.
- 2026-07-12: Design gate completed. Selected Calm Instrument as the production foundation and adopted Quiet Paper's typographic rhythm and whitespace; browser review covered the Ask state, light/dark persistence, narrow layout and console health.
- 2026-07-11: Initial minimal desktop experience spec authored to replace the large WPF sidebar, separate recording page, six-tab settings and six-step wizard.
