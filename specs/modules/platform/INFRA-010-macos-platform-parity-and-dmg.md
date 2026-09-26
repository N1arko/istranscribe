---
status: active
---

# INFRA-010: macOS Platform Parity And DMG {#root}

## Простыми словами {#plain-language}

isTranscribe становится обычным приложением для Mac: оно устанавливается из
DMG, живёт в menu bar, распознаёт встречи, спрашивает о записи, сохраняет звук и
показывает тот же компактный интерфейс и виджет, что Windows-версия.

## Goal {#goal}

Реализовать macOS platform adapter и public Apple Silicon application с
функциональным паритетом Windows release-v2, сохранив общий runtime, UI,
persistence schema и будущие extension points.

## Depends on {#depends-on}

- `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#acceptance`
- `spec://modules/app/FEAT-011-meeting-detection-v2#acceptance`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#acceptance`
- `spec://modules/app/FEAT-013.A-floating-recording-widget#acceptance`
- `spec://modules/app/FEAT-010.A-release-v2-localization#acceptance`

## See also {#see-also}

- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity`
- `spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.macos`

## Target Environment {#target}

- public runtime: `osx-arm64`;
- minimum operating system: macOS `14.2`;
- primary hardware: Apple Silicon MacBook;
- self-contained `.app` inside a drag-to-Applications `.dmg`;
- ru/en, light/dark/system themes;
- Intel/x64 and Mac App Store distribution остаются отдельной будущей волной.

Минимальная версия `14.2` выбрана для process-scoped Core Audio taps, которые
позволяют приблизить output observation/capture к Windows process-loopback
contract.

## Scope {#scope}

### In scope {#scope.in}

- macOS implementations всех contracts из `INFRA-009#platform-contracts`;
- system/app output и microphone capture;
- process, application, browser, window и local speech evidence;
- Ask-only meeting detection и automatic finish;
- manual recording, pause/resume/finish и floating widget;
- app-data paths, SQLite, Keychain secrets и crash recovery;
- menu bar/tray, notifications, single-instance и launch-at-login;
- contextual onboarding системных permissions;
- `.app` bundle, Info.plist, entitlements и unsigned DMG packaging;
- macOS unit, contract, UI и live acceptance;
- compatibility matrix для Zoom, Teams, Google Meet, Яндекс Телемоста,
  Контур.Толка, Zen и generic browser meetings.

### Out of scope {#scope.out}

- Intel/x64 or universal binary;
- Mac App Store submission;
- обязательная Developer ID подпись и notarization;
- покупка Apple Developer Program membership;
- iCloud sync;
- native Swift/AppKit rewrite общего UI;
- активная транскрибация;
- новый продуктовый функционал, отсутствующий в Windows release.

## Functional Parity Contract {#parity}

На Windows и macOS одинаковы:

- service states и Ask policy;
- known meeting-service catalog и browser fallback;
- confidence scoring и candidate lifecycle;
- ручной и подтверждённый automatic start;
- active-duration timer, pause/resume и finish;
- automatic finish после устойчивой потери meeting eligibility;
- processing/recovery semantics;
- recent recordings, rename, open, reveal и remove;
- setup/settings information architecture;
- localization, themes и accessibility intent.

Нативные permission dialogs, menu-bar conventions, window chrome, hotkeys и
default storage roots следуют правилам macOS.

## Audio And Meeting Signals {#audio-detection}

- Core Audio process observation нормализует bundle id, PID, active output и
  input activity в Core-owned snapshots.
- Metadata active-output процесса наблюдается без sample-bearing capture.
  Process output observation после eligible candidate использует Core Audio process taps.
  ScreenCaptureKit может давать app/window inventory и system-audio fallback там,
  где это требуется adapter-у.
- Microphone capture использует системный Core Audio/AVFoundation path и следует
  demand-driven lifecycle из `spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle`.
- Idle listening без eligible meeting-кандидата не удерживает process/system-audio tap
  или microphone capture и не показывает постоянные privacy indicators.
- Zoom/Teams desktop bundle ids и browser-family process trees сопоставляются с
  общими `MeetingAppProfile`.
- Chrome, Edge, Firefox, Safari и Zen являются browser hosts. В UI они не
  становятся отдельными meeting services.
- Accessibility/window evidence остаётся privacy-reduced: runtime получает
  факты и stable candidate ids без сохранения полного текста окна.
- Общий scorer и temporal lifecycle принимают решение о prompt и finish.

System audio, microphone и Accessibility permissions проверяются независимо.
Отсутствующая permission публикует `permission_required`; приложение сохраняет
ручной доступ к настройкам и показывает прямое действие для открытия нужной
панели System Settings.

## Recording And Artifacts {#recording}

- Общий recording coordinator сохраняет output + microphone timeline и
  pause/recovery semantics из `FEAT-012`.
- Новый macOS primary artifact остаётся `.mp3` с тем же speech-first preset
  `48 kHz / stereo / 128 kbps`.
- macOS encoder находится за общим `IAudioArtifactEncoder`. Если системный
  encoder не подтверждает exact preset, release использует небольшую
  dynamically linked MP3 encoder library с совместимой лицензией, notices и
  отдельным native payload audit; standalone `ffmpeg` executable не
  поставляется.
- Readability и duration проверяются до atomic promotion.
- Legacy `.m4a`, `.ogg`, `.wav` и Windows-created `.mp3` остаются
  list/open/delete compatible.
- Device change, permission revocation и adapter failure сохраняют хотя бы одну
  читаемую recovery copy.

## macOS System Services {#system-services}

- Secrets: Keychain item per provider/secret id.
- Autostart: `SMAppService` main-app login item с observable approval state.
- Single instance: per-user platform coordinator с activation текущего
  процесса.
- Shell: open audio и reveal in Finder.
- Tray: Avalonia/native menu-bar item с listening, recording, processing и
  attention states.
- Notifications: system notification только для Ask, ready и actionable
  failure.
- Widget placement: NSScreen/Avalonia working-area adapter, multi-monitor,
  scaling, persisted drag position и topmost behavior.
- App data: `~/Library/Application Support/isTranscribe`; пользовательские
  записи по умолчанию остаются в `~/Documents/isTranscribe/Recordings`.

## Permission Experience {#permissions}

First run объясняет и запрашивает permissions в контексте:

1. microphone — для голоса пользователя;
2. system audio recording — для голосов собеседников;
3. Accessibility или screen capture — только для meeting-specific evidence,
   которое действительно использует adapter;
4. notifications и launch-at-login — опционально.

Каждый шаг показывает `granted | denied | needs_restart | not_requested`,
разрешает повторную проверку и открывает точную системную панель. Отказ не
создаёт бесконечный prompt loop. Приложение явно показывает доступный degraded
recording contour.

## DMG Distribution {#distribution}

- Build создаёт self-contained `isTranscribe.app` с deterministic bundle id,
  icon, version, Info.plist, usage descriptions и required entitlements.
- DMG содержит приложение и ссылку на `/Applications`.
- Пользователь устанавливает приложение обычным drag-to-Applications flow.
- Development/private DMG собирается без Apple Developer membership,
  Developer ID и notarization.
- Для unsigned build рядом с DMG публикуется короткая first-launch инструкция
  для Gatekeeper `Open Anyway`, если текущая macOS её потребует.
- Pipeline умеет позднее принять signing identity и notarization credentials
  без изменения source layout или пользовательского формата DMG.
- Release manifest содержит checksums, target RID, minimum OS, permissions,
  native libraries и third-party notices.

## Verification {#verification}

- `osx-arm64` build и app-bundle validation выполняются на macOS runner.
- Common, Desktop и macOS adapter tests проходят.
- Permission tests покрывают granted, denied, revoked и needs-restart states.
- Audio golden подтверждает слышимые output и microphone signals.
- Contract replays подтверждают те же Ask/finish decisions, что Windows, для
  одинаковых normalized evidence.
- Live smoke покрывает Zen или Safari browser meeting и одну desktop meeting:
  detect → Ask → accept → record → automatic finish → open MP3.
- Manual recording, pause-aware timer и Finder actions проходят live smoke.
- DMG layout, self-contained payload и app-relative dependencies подтверждаются
  packaging verification; update replacement и uninstall/reinstall сохраняют
  пользовательские recordings.
- Idle resource, crash recovery и accessibility gates фиксируются отдельно от
  Windows evidence.

## Acceptance {#acceptance}

INFRA-010 завершена, когда:

1. Один общий runtime и UI обслуживают Windows и macOS composition roots.
2. macOS распознаёт representative desktop/browser meeting и показывает Ask.
3. Согласие создаёт output + microphone MP3 и automatic finish завершает его.
4. Manual controls, widget, history, settings и recovery достигают
   функционального паритета.
5. Permission states объяснимы и ведут к корректным System Settings.
6. Keychain, login item, menu bar, notifications и Finder integration работают.
7. `osx-arm64` DMG содержит self-contained приложение без repository-local
   dependencies и обязательной платной подписи.
8. macOS contract, live, packaging и regression evidence проходят.

## Document Notes {#document-notes}

- 2026-07-30: Первая macOS-волна зафиксирована для Apple Silicon и macOS 14.2+
  с DMG distribution без обязательной Developer ID подписки.
- 2026-08-01: Из release acceptance исключены отдельные live-проверки на
  mixed-scale/multi-monitor конфигурации и запуск на чистом Mac-профиле;
  deterministic widget и packaging verification сохранены.
- 2026-08-02: Для macOS microphone observation зафиксирован candidate-gated lifecycle;
  постоянный capture в idle listening исключается из целевого поведения.
- 2026-08-10: Тот же lifecycle распространён на Core Audio process taps, чтобы browser
  playback без meeting evidence не включал системный индикатор записи аудио.

## External References {#external-references}

- [Apple: ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit)
- [Apple: Capturing system audio with Core Audio taps](https://developer.apple.com/documentation/CoreAudio/capturing-system-audio-with-core-audio-taps)
- [Apple: SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)
- [Apple: Keychain services](https://developer.apple.com/documentation/security/keychain-services)
