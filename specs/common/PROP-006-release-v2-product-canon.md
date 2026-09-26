---
status: active
---

# PROP-006: Release V2 Product Canon {#root}

## Простыми словами {#plain-language}

Release v2 превращает isTranscribe в лёгкое локальное desktop-приложение для Windows x64. Приложение работает в фоне, замечает вероятную встречу, спрашивает разрешение на запись и сохраняет один готовый аудиофайл в выбранную папку. Транскрибация временно выключена; архитектура допускает будущий локальный или внешний движок.

## Goal {#goal}

Задать продуктовый и release-канон следующей волны: минимальный пользовательский путь, надёжное Ask-only detection, готовый аудиоартефакт, современная desktop-оболочка, Windows x64 distribution и platform seams для будущего macOS.

## Supersedes {#supersedes}

Для release v2 этот документ заменяет конфликтующие решения из:

- `spec://common/PROP-001-product-canon#rules`;
- `spec://common/PROP-002-app-shell-and-settings#rules`;
- `spec://common/PROP-003-audio-capture-and-device-observation#rules`;
- `spec://common/PROP-004-meeting-session-and-data-model#pipeline`.

Старые документы сохраняются как канон реализованной MVP/v1-волны. При расхождении в release v2 применяется `PROP-006` и связанные с ним work-item спеки.

## Depends on {#depends-on}

- `spec://common/PROP-005-local-runtime-and-operations#rules`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#root`

## See also {#see-also}

- `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#root`
- `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#root`
- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`
- `spec://modules/app/FEAT-016-local-whisper-transcription#root`
- `spec://modules/app/FEAT-017-speaker-aware-transcription#root`

## Scope {#scope}

### In scope {#scope.in}

- personal-first и local-first desktop product;
- release target `Windows x64`;
- global service state `enabled | paused`;
- automatic lifecycle только в режиме `Ask`;
- ручной старт записи через `Record now`;
- detection для Zoom, Microsoft Teams, Google Meet, Яндекс Телемоста, Контур.Толка и расширяемых app profiles;
- запись системного/app output и microphone;
- один готовый сжатый meeting artifact в пользовательской папке;
- компактная современная desktop-оболочка;
- нормальная install/update/uninstall модель Windows;
- platform-neutral core и контракты для будущего macOS;
- provider-neutral seam для будущей транскрибации.

### Out of scope {#scope.out}

- автоматический старт записи без подтверждения пользователя;
- активная транскрибация, Fireworks API и загрузка локальной speech model;
- summaries, transcript editor и semantic search;
- accounts, backend, billing, sync и team collaboration;
- готовый macOS audio adapter и macOS release artifact;
- Windows ARM64/x86 distribution.

## Product Model {#product-model}

### Primary loop {#product-model.primary-loop}

Канонический пользовательский путь:

1. Пользователь устанавливает и запускает isTranscribe как обычное Windows-приложение.
2. После короткого first run приложение остаётся доступным в tray и наблюдает только локальные meeting signals.
3. При достаточной уверенности приложение показывает компактный prompt `Похоже, началась встреча. Записать?`.
4. `Записать` начинает или подтверждает запись с доступным prebuffer.
5. `Пропустить` завершает candidate без файлов и блокирует повторный prompt для той же logical meeting window.
6. После завершения встречи приложение формирует один готовый сжатый файл и сохраняет его в configured recordings folder.
7. Пользователь может открыть файл или папку из списка последних встреч.

### User-visible states {#product-model.states}

Основные состояния продукта:

- `listening` — сервис включён и ждёт meeting signals;
- `suspected` — candidate набирает confidence, prompt ещё не показан;
- `awaiting_confirmation` — пользователь решает, записывать ли встречу;
- `recording` — аудио сохраняется;
- `processing` — дорожки закрываются, смешиваются и сжимаются;
- `ready` — итоговый meeting artifact доступен;
- `attention_required` — аудио сохранено в recovery/fallback виде, требуется действие пользователя;
- `paused` — automatic observation и prompts выключены пользователем.

Технические capability, device и queue состояния не должны становиться основной навигацией приложения.

### Recording policy {#product-model.recording-policy}

- Release v2 использует `Ask` как единственную automatic policy.
- `Record now` доступен независимо от detection и запускает manual meeting session.
- Timeout confirmation имеет безопасный результат `skip`.
- Отказ не создаёт normal session row и не сохраняет prebuffer на диск.
- Для конкретного app profile пользователь может выбрать `ask` или `ignore`.
- Старые значения `auto` мигрируют в `ask`; старые `off` мигрируют в `paused`.

## Detection Principles {#detection-principles}

- Detection является multi-signal confidence model, а не проверкой одного peak threshold.
- Signal layer может использовать process identity/tree, render session state, microphone usage, локальный speech activity, window/UI Automation evidence, temporal stability и user decision history.
- Ни один короткий звук, notification или единичный audio peak не достаточен для prompt.
- Generic browser playback считается слабым сигналом; browser meeting требует дополнительных meeting-specific признаков.
- Speech analysis для detection работает на коротком in-memory buffer. До согласия пользователя audio bytes не материализуются как файл.
- Решение должно быть explainable через локальный diagnostics event с сигналами и итоговым score без сохранения распознанной речи.

Детальный runtime contract задаёт `spec://modules/app/FEAT-011-meeting-detection-v2#root`.

## Recording Artifact {#recording-artifact}

- Каждая завершённая встреча имеет один primary user-facing audio artifact.
- Primary artifact включает слышимый output и microphone, если соответствующие sources были доступны.
- Source tracks могут существовать во временном/recovery contour и удаляются после успешной атомарной finalization согласно storage policy.
- Compression не зависит от executable в system `PATH`.
- Ошибка compression сохраняет читаемый fallback audio и переводит session в `attention_required`; потеря записанного материала недопустима.
- Release v2 не enqueue-ит transcription job и не выполняет сетевой upload аудио.

Детальный contract задаёт `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`.

## Experience Canon {#experience-canon}

- Приложение использует одну компактную primary window и tray surface.
- Current recording является состоянием primary window, отдельная постоянная страница не требуется.
- First run укладывается в одну короткую setup surface с разумными defaults.
- Основные настройки: service state, monitored apps, microphone, recordings folder и autostart.
- Advanced diagnostics доступны отдельно и не конкурируют с основным пользовательским путём.
- Settings применяются сразу или сохраняются автоматически после локальной валидации.
- Визуальная система опирается на актуальные desktop patterns и platform conventions текущего поколения; legacy admin-dashboard, nested-tab и dense-control layouts не входят в release surface.

Детальный contract задаёт `spec://modules/app/FEAT-013-minimal-desktop-experience#root`.

## Platform And Distribution {#platform}

### Runtime architecture {#platform.runtime}

- Release v2 строится на `.NET 10 LTS`.
- UI shell строится на current stable Avalonia generation.
- Domain, detection policy, session lifecycle, persistence contracts и transcription seam не зависят от WPF или Windows UI types.
- Windows-specific capture, process observation, UI Automation, tray, hotkeys и shell integration реализуются platform adapter-ами.
- macOS остаётся следующим platform adapter поверх общего core; текущая волна обязана не создавать новую WPF-only границу.

### Windows release {#platform.windows-release}

- Первая release architecture: `win-x64`.
- Публичный artifact устанавливается двойным кликом через Windows-native signed installer/package.
- Приложение появляется в Start Menu и Installed Apps, поддерживает per-user update и clean uninstall.
- User recordings и settings переживают in-place update и default uninstall.
- Runtime является self-contained и не требует установленного SDK/runtime.
- ZIP, PowerShell script и `dotnet run` не являются public release entrypoint.

## Transcription Future Seam {#transcription-seam}

- Fireworks runtime, API key UX и automatic dispatch выключаются в release v2.
- Existing transcript files остаются доступными как пользовательские артефакты.
- Core определяет provider-neutral `ITranscriptionEngine` и capability descriptor без активной реализации.
- Future engine может быть local или remote и обязан явно сообщать privacy, model, resource и language capabilities.
- Отсутствие engine является нормальным состоянием и не создаёт failed jobs.

Детальный contract задаёт `spec://modules/app/FEAT-014-transcription-extension-seam#root`.

Post-v2 optional activation использует cloud engine из `FEAT-015`, local engine
из `FEAT-016` и общий speaker-aware mode/artifact contract из `FEAT-017`. Эти
спеки расширяют продукт после базовой release-v2 acceptance и не возвращают
Fireworks runtime.

## Release Quality Bar {#quality-bar}

- idle observation рассчитано на `8+ часов` без unbounded memory growth;
- crash/device loss не удаляют уже записанные bytes;
- prompt и recording lifecycle устойчивы к app restart, краткой тишине и device switch;
- основной UI usable при keyboard-only navigation и system scaling `100–200%`;
- light/dark themes имеют читаемый contrast;
- public Windows x64 artifact проходит clean install, launch, update и uninstall smoke;
- release checklist проверяет end-to-end путь от detection до открытия готового файла.

## Acceptance {#acceptance}

Release v2 соответствует этому канону, когда одновременно доказано:

1. Пользователь устанавливает подписываемый Windows x64 artifact без development tools.
2. First run приводит к готовому состоянию `listening` без API key и шестишаговой настройки.
3. Поддерживаемые meeting apps создают Ask prompt по multi-signal detection.
4. Passive media и короткие system sounds проходят установленный false-positive quality gate.
5. Positive confirmation приводит к записи output + microphone, когда оба source доступны.
6. Завершение создаёт один открываемый сжатый meeting file в configured folder.
7. Ошибка post-processing сохраняет recovery audio.
8. Приложение не вызывает Fireworks и не enqueue-ит transcription work.
9. Primary window остаётся компактной и покрывает listening, prompt-adjacent, recording, processing и history flows.
10. Solution имеет platform-neutral core и Windows-specific adapters без WPF ownership в новых границах.
11. Все release-v2 items в `BOARD` завершены и подтверждены соответствующими acceptance evidence.

## Document Notes {#document-notes}

- 2026-08-30: Linked the post-v2 speaker-aware three-mode transcription canon;
  the original Windows release-v2 acceptance remains the recording baseline.
- 2026-07-30: Linked the post-v2 repository, macOS parity, opt-in
  Groq/OpenRouter transcription and local Whisper transcription waves without
  changing the Windows x64 release v2 acceptance.
- 2026-07-11: Initial release v2 canon authored from the user-approved rebuild direction: Windows x64 first, Ask-only automatic behavior, no active transcription, modern lightweight UI and future macOS architecture.
