---
status: active
---

# INFRA-009: Cross-Platform Repository Boundaries {#root}

## Простыми словами {#plain-language}

Windows и macOS развиваются в одном репозитории и используют одну продуктовую
логику и одну Avalonia-оболочку. Системные различия находятся в небольших
platform-проектах, а отдельные Windows- и macOS-приложения только собирают
нужные реализации вместе.

## Goal {#goal}

Довести начатое в `INFRA-007` разделение до строгой dual-platform архитектуры:
общий application runtime и UI не знают о конкретной ОС, Windows release
сохраняет текущее поведение и появляется готовая граница для macOS adapter,
host и build contour.

## Depends on {#depends-on}

- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#target-structure`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- `spec://modules/app/FEAT-011-meeting-detection-v2#signal-model`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#root`
- `spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract`

## See also {#see-also}

- `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#root`
- `spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#root`
- `spec://modules/app/FEAT-016-local-whisper-transcription#root`

## Scope {#scope}

### In scope {#scope.in}

- общий `IsTranscribe.Application` для runtime orchestration и пользовательских
  сценариев;
- Avalonia UI как общая библиотека без ссылки на concrete Windows adapter;
- отдельный Windows entrypoint/composition root;
- обязательные buildable macOS adapter и entrypoint boundaries без заявления
  о готовой macOS-функциональности;
- перенос Win32, NAudio/WASAPI, Media Foundation, DPAPI и Windows shell code в
  `IsTranscribe.Platform.Windows`;
- platform-neutral persistence и app-path contracts;
- интерфейсы для platform capabilities, permissions, secrets, single-instance,
  autostart, shell и placement;
- одинаковый dependency direction для Windows и будущего macOS;
- shared contract-test kit, OS-specific solution filters и provider-neutral
  dual-OS verification commands;
- lossless migration существующих Windows settings, database и artifacts.

### Out of scope {#scope.out}

- рабочий macOS audio capture и meeting observation;
- macOS permission onboarding и системная интеграция;
- готовый `.app` или `.dmg`;
- активация транскрибации;
- редизайн существующего интерфейса;
- изменение пользовательского поведения Windows release.

## Repository Baseline {#repository-baseline}

- Перед первым code change workspace получает локальный Git baseline. Если
  `.git` отсутствует, исполнитель создаёт локальный repository и baseline
  commit текущего source/spec состояния.
- Создание remote repository, push, hosting и branch protection требуют
  отдельного пользовательского решения и не входят в `INFRA-009`.
- `.gitignore` исключает `bin/`, `obj/`, generated release payloads, local
  recordings, diagnostics, `*.nettrace`, IDE state и machine-local secrets.
- Репозиторий хранит один `global.json`, общий `Directory.Build.props`, один
  release solution и platform-specific solution filters/commands.
- Текстовые source/config files имеют детерминированную line-ending policy;
  Windows command files сохраняют совместимый CRLF.
- Generated binaries, user recordings и local acceptance evidence не
  становятся source dependencies.

## Canonical Repository Structure {#structure}

Release-репозиторий имеет следующие ownership boundaries:

```text
src/
├── IsTranscribe.Core/
├── IsTranscribe.Application/
├── IsTranscribe.Persistence/
├── IsTranscribe.Desktop/
├── IsTranscribe.Platform.Windows/
├── IsTranscribe.Platform.MacOS/
├── IsTranscribe.App.Windows/
└── IsTranscribe.App.MacOS/

tests/
├── IsTranscribe.Core.Tests/
├── IsTranscribe.Application.Tests/
├── IsTranscribe.Persistence.Tests/
├── IsTranscribe.Desktop.Tests/
├── IsTranscribe.Platform.ContractTests/
├── IsTranscribe.Platform.Windows.Tests/
└── IsTranscribe.Platform.MacOS.Tests/

packaging/
├── windows/
└── macos/
```

Все восемь `src` boundaries и перечисленные test boundaries обязательны в
release solution этой волны.

- `IsTranscribe.Desktop` становится общей class library. Project name и
  namespaces сохраняются, а assembly получает имя
  `IsTranscribe.Desktop.UI`, чтобы не конфликтовать со стабильным Windows
  executable identity.
- `IsTranscribe.App.Windows` становится единственным Windows release
  executable и composition root.
- `IsTranscribe.Platform.MacOS` и `IsTranscribe.App.MacOS` являются buildable
  non-release boundaries. До `INFRA-010` macOS adapter возвращает честные
  `unsupported` capability states и не публикуется пользователю.
- Текущее содержимое `tools/installer` переносится в `packaging/windows` с
  одновременным обновлением внутренних путей и verification scripts.
  Compatibility forwarding scripts допустимы только для документированных
  engineering commands и не входят в release payload.
- `packaging/macos` в этой волне содержит только ownership/readiness contract;
  `.app` и `.dmg` появляются в `INFRA-010`.

## Dependency Rules {#dependencies}

- `Core` не ссылается на UI, persistence, network SDK или platform project.
- `Application` ссылается на `Core` и владеет product orchestration.
- `Persistence` ссылается на `Core` и получает пути/secret protection через
  абстракции.
- `Desktop` ссылается на `Core` и `Application`; views и view models не
  создают concrete platform services.
- Каждый `Platform.*` ссылается только на `Core` и `Application`, реализует
  их contracts и содержит native interop своей ОС.
- `App.Windows` собирает `Desktop + Application + Persistence +
  Platform.Windows`.
- `App.MacOS` собирает `Desktop + Application + Persistence +
  Platform.MacOS`.
- `Platform.Windows` и `Platform.MacOS` не ссылаются друг на друга.
- Release graph не содержит legacy WPF entrypoint.

Автоматический dependency test запрещает в общих проектах:

- `System.Windows`, WPF-UI и Windows Forms;
- NAudio/WASAPI и Windows SDK projections;
- DPAPI и Windows Registry;
- Win32/COM handles и P/Invoke к Windows libraries;
- AppKit/CoreAudio/ScreenCaptureKit concrete types;
- `OperatingSystem.IsWindows()` как способ выбирать product behavior.

Проверка ОС допустима в diagnostics и platform composition roots.

## Entrypoints And Windows Identity {#entrypoints}

### Stable entrypoints and identity {#entrypoints-and-windows-identity}

`IsTranscribe.App.Windows` владеет:

- `Program` и Avalonia desktop lifetime;
- Windows manifest, icon и runtime-pack selection;
- single-instance bootstrap и platform service registration;
- созданием shared `ApplicationRuntime`;
- передачей готового runtime/platform context в общий `App.axaml`.

Project физически находится в
`src/IsTranscribe.App.Windows/IsTranscribe.App.Windows.csproj`, но публикует
стабильное Windows entry assembly:

- assembly/executable: `IsTranscribe.Desktop` /
  `IsTranscribe.Desktop.exe`;
- MSIX package name: `isTranscribe.Desktop`;
- MSIX application id: `App`;
- startup task id: `isTranscribeStartup`.

Сохраняются текущие app-data paths, settings/database locations, Run-key
semantics и upgrade identity. Windows installer scripts переключают только
source project path; artifact tests продолжают проверять существующие
executable/package identifiers.

`IsTranscribe.App.MacOS` владеет только macOS `Program`, desktop lifetime и
composition bootstrap. В `INFRA-009` он:

- собирается на macOS arm64 runner;
- разрешает полный dependency graph через safe unsupported adapters;
- не создаёт `.app`/`.dmg`, не запрашивает permissions и не сообщает о
  доступности recording/detection capabilities.

## Shared Application Runtime {#application-runtime}

Текущее поведение `WindowsReleaseV2Runtime` переносится в нейтральный runtime,
который владеет:

- service state и settings application;
- meeting candidate lifecycle и Ask decisions;
- manual/Ask recording commands;
- pause/resume/finish и automatic-finish coordination;
- processing, recovery и recent-recording projection;
- rename/remove/open intent contracts;
- future transcription job orchestration boundary.

`IApplicationRuntime`, runtime snapshots, user commands и concrete
`ApplicationRuntime` принадлежат `IsTranscribe.Application`. Domain entities,
normalized evidence и provider/repository contracts остаются в
`IsTranscribe.Core`.

Runtime получает нормализованные audio, process, window, speech и permission
snapshots через интерфейсы. Platform adapters не принимают продуктовые решения
о показе Ask, начале записи или завершении meeting session.

После cutover `WindowsReleaseV2Runtime` удаляется из release graph. Windows
adapter не содержит facade или второй orchestration runtime с тем же
поведением.

## Platform Contract Surface {#platform-contracts}

Минимальный contract surface включает:

- audio environment observation и capture session;
- process/application/window evidence;
- microphone and speech-activity sampling;
- permission status, request и переход в системные настройки;
- app-data и user-document path policy;
- encrypted secret vault;
- single-instance activation;
- launch-at-login;
- open file/folder и system notification;
- active-work-area и floating-window placement;
- platform descriptor и capability snapshot.

Каждая capability имеет состояние `available | permission_required |
unsupported | failed` с безопасным пользовательским пояснением. Общий UI
реагирует на эти состояния и не скрывает отсутствие реализации.

## Desktop Boundary {#desktop}

- Avalonia XAML, view models, Calm Instrument tokens, localization, Ask prompt,
  recording widget и settings живут в одном `IsTranscribe.Desktop`.
- `App.axaml.cs` получает runtime и platform services от composition root.
- Общий UI не содержит P/Invoke и concrete `Windows*`/`Mac*` construction.
- Foreground work-area lookup и widget placement получают данные через
  platform contract; Win32 lookup перемещается в `Platform.Windows`.
- Platform conventions допускают небольшие различия меню, close behavior,
  permission prompts и system typography при сохранении одинаковых сценариев.
- Новый общий feature считается доступным обеим ОС, пока его spec явно не
  ограничивает platform scope.

## Persistence And Migration {#persistence}

### Shared persistence and migration {#persistence-and-migration}

- SQLite schema, migrations, session entities и settings models остаются
  общими.
- Root app-data path выбирает platform adapter; repository принимает готовую
  path policy.
- Secret envelope/provider identifiers остаются нейтральными.
- Windows использует DPAPI implementation, macOS получит Keychain
  implementation в `INFRA-010`.
- Текущие Windows paths, database, recordings и display titles сохраняются без
  перемещения или потери.
- `IsTranscribe.Host` удаляется из `isTranscribe.sln` и всего release dependency
  graph. Product orchestration переезжает в `Application`, Windows-native code
  — в `Platform.Windows`, SQLite/storage code — в `Persistence`.
- Если legacy WPF migration solution всё ещё нужен, `IsTranscribe.Host` может
  остаться только в `isTranscribe.Legacy.sln`, иметь
  `IsReleaseArtifact=false` и не быть ProjectReference ни одного release
  project.

## Incremental Cutover {#cutover}

Работа выполняется проверяемыми slices:

1. Зафиксировать current Windows characterization: solution graph, Release
   tests, publish artifact identity и чтение существующего app-data fixture.
2. Создать `Application` и перенести runtime contracts/orchestration за
   существующим UI contract без изменения Windows behavior.
3. Перенести оставшиеся Windows-native services из `Host` и `Desktop` в
   `Platform.Windows`.
4. Превратить `Desktop` в shared UI library и подключить новый `App.Windows`
   composition root со стабильным executable/MSIX identity.
5. Удалить `Host` из release graph, добавить обязательные macOS boundaries и
   shared contract tests.
6. Перенести Windows packaging ownership, выполнить Windows upgrade regression
   и macOS shared-build evidence.

После каждого slice release solution собирается, затронутые tests проходят, а
Windows entrypoint остаётся запускаемым. Одновременный big-bang rename,
runtime rewrite и persistence migration не используется.

## Build And Test Contour {#build-test}

### Build and test contour {#build-and-test-contour}

- `isTranscribe.sln` является полным Windows/release solution.
- Checked-in macOS solution filter исключает Windows-native projects и включает
  `Core`, `Application`, `Persistence`, `Desktop`, `Platform.MacOS`,
  `App.MacOS` и их shared/contract tests.
- Shared unit tests выполняются на Windows x64 и macOS 14+ arm64.
- Platform contract tests запускают одинаковый набор behavioral fixtures
  против каждого adapter.
- Detection replay fixtures дают одинаковые confidence decisions при
  одинаковых normalized evidence frames.
- Desktop tests используют fake platform services.
- Windows build/publish/install scripts продолжают работать после split.
- macOS verification до `INFRA-010` выполняет restore/build/test solution
  filter и composition-resolution smoke, не публикует release artifact.
- Эти команды документируются provider-neutral. Проверка на локальном Mac,
  remote Mac runner или CI даёт одинаковое acceptance evidence.
- Подключение конкретного CI provider переносит те же команды без изменения
  test contract. Отдельные долгоживущие OS-ветки не используются.

## Verification {#verification}

- Dependency graph и namespace audit подтверждают правила `#dependencies`.
- Windows regression suite проходит без изменения пользовательских сценариев.
- Existing Windows installation запускает новую composition root и читает
  прежние settings/database/artifacts.
- Shared runtime tests покрывают Ask, manual recording, pause, automatic finish,
  processing и history без concrete platform assembly.
- Windows и macOS evidence выполняют зафиксированные в `#build-test` команды.
- Windows artifact verification подтверждает сохранение executable, MSIX и
  upgrade identity после смены project path.
- Release solution/project-reference audit подтверждает отсутствие
  `IsTranscribe.Host`.
- Release payload не содержит второй скрытый runtime с расходящимся поведением.

## Acceptance {#acceptance}

INFRA-009 завершена, когда:

1. Product orchestration принадлежит `IsTranscribe.Application`, а не Windows
   adapter.
2. Общий Avalonia UI не ссылается на `IsTranscribe.Platform.Windows`.
3. Windows native code находится внутри Windows adapter и composition root.
4. Persistence получает platform paths и secret protection через contracts.
5. Обязательные Windows и macOS entrypoint boundaries следуют одному dependency
   graph; macOS boundary честно сообщает unsupported capabilities.
6. Общие behavioral и dependency tests выполняются на обеих ОС.
7. Текущий Windows release сохраняет settings, recordings и все release-v2
   сценарии.
8. Windows executable, MSIX package/application/startup-task identity и upgrade
   path сохраняются после перехода на `App.Windows`.
9. `IsTranscribe.Host` отсутствует в release solution, project references и
   release payload.
10. Repository baseline, ignore policy, Windows packaging ownership и macOS
    verification contour находятся в source control и воспроизводимы.

## Document Notes {#document-notes}

- 2026-07-30: Создана следующая инфраструктурная волна после `INFRA-007` для
  одного репозитория, общей продуктовой логики и двух тонких platform hosts.
- 2026-07-30: Перед реализацией зафиксированы обязательные macOS boundaries,
  incremental cutover, удаление legacy Host из release graph, стабильная
  Windows executable/MSIX identity и provider-neutral dual-OS verification.
