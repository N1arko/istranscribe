---
status: active
---

# INFRA-005: Windows Installer And Shell Integration {#root}

## Простыми словами {#plain-language}

Эта спека фиксирует, что isTranscribe должен поставляться и запускаться как обычное Windows-приложение, а не как dev-only `dotnet run` артефакт. Пользователь должен иметь возможность установить приложение через нормальный installer, запустить его из Start Menu, удалить через Windows Apps & Features и обновить поверх существующей установки без потери своих локальных данных.

## Goal {#goal}

Зафиксировать install/distribution contour для MVP Windows desktop app: install root, shell integration, upgrade/uninstall semantics и release artifact expectations.

## See also {#see-also.release-v2}

- Release v2 production packaging is governed by `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#root`.
- Public release trust and distribution are governed by `spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#root`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`
- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://common/PROP-005-local-runtime-and-operations#rules`
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#runtime`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`

## See also {#see-also}

- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#entrypoints.normal`
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#single-instance`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#initialization.bootstrap`
- `spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard`

## Scope {#scope}

### In scope {#scope.in}

- канонический installable artifact для end-user Windows distribution;
- install root и runtime prerequisites;
- Start Menu / Apps & Features / shell shortcut integration;
- first launch after install и normal launch after reboot;
- in-place upgrade semantics;
- uninstall semantics относительно binaries и user data;
- release signing expectations.

### Out of scope {#scope.out}

- Store publishing strategy;
- background auto-update agent или delta patching;
- enterprise deployment policies (`Intune`, `GPO`, SCCM);
- macOS/Linux packaging;
- visual branding, marketing website и release-note copy.

## Decisions {#decisions}

- MVP end-user distribution обязан поставляться как Windows installer package, а не как инструкция “установи .NET SDK и запусти `dotnet run`”.
- Канонический runtime payload для installer: self-contained `win-x64` publish, чтобы у пользователя не было внешней зависимости на заранее установленный .NET runtime.
- MVP поддерживает одну end-user архитектуру distribution: `x64`. `arm64` и `x86` остаются вне scope текущей волны.
- Installer обязан выполнять per-user install без требований administrator privileges в нормальном happy path.
- Install root обязан быть отделён от app-owned data root; upgrade и uninstall не должны случайно удалять user recordings, transcripts, settings и database.
- Public release artifacts должны быть code-signed. Локальные dev builds и unsigned prerelease артефакты допустимы только вне end-user release contour.
- MVP update model является user-driven: новая версия ставится поверх существующей установки через тот же installer contour. Background auto-updater в текущую волну не входит.

## Installer Model {#installer-model}

### Distribution artifact {#installer-model.artifact}

- Release artifact обязан быть одним user-facing installer entrypoint, который пользователь может скачать и запустить двойным кликом из Windows Explorer.
- Installer не должен требовать от пользователя ручного выбора `dotnet publish`, распаковки raw build output или копирования файлов по папкам.
- Installer должен доставлять self-contained app payload для `win-x64`, включая все runtime dependencies, нужные для запуска `IsTranscribe.App`.
- Installer technology не является продуктовым каноном сама по себе; каноном является observable behavior install flow. Допустимы `MSI`, bootstrapper-based installer или другой Windows-native contour, если он удовлетворяет этой спецификации.

### Install location {#installer-model.location}

- Канонический install root: user-scoped directory внутри `%LocalAppData%\Programs\isTranscribe` или эквивалентный per-user programs root.
- Binaries, app icon resources и uninstall metadata живут в install root.
- App-owned runtime data продолжает жить в канонических путях из `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`; installer не должен переносить config/database/logs в install root.
- Приложение не должно требовать write access в install root для normal runtime operations.

### Install prerequisites {#installer-model.prerequisites}

- Happy-path install не должен требовать отдельно установленного .NET runtime.
- Если машина не удовлетворяет platform prerequisites из `spec://common/PROP-001-product-canon#rules`, installer или first launch обязаны явно сообщить о несовместимости, а не завершаться silent failure.
- VC++/runtime dependencies, если они нужны выбранному packaging contour, должны приходить вместе с installer chain или быть проверены до завершения install flow.

## Shell Integration {#shell-integration}

### Start Menu and launch surfaces {#shell-integration.start-menu}

- После успешной установки пользователь обязан видеть `isTranscribe` в Start Menu.
- Start Menu entry обязан запускать primary app entrypoint, а не auxiliary tool, console host или installer repair surface.
- Desktop shortcut в MVP опционален и по умолчанию не обязателен. Если installer предлагает его, это optional checkbox, а не жёсткое требование.
- Launcher metadata обязана использовать понятное product name `isTranscribe` и корректную иконку приложения.

### Apps & Features presence {#shell-integration.apps-features}

- После установки приложение обязано появляться в Windows Apps & Features / Installed Apps.
- Запись должна содержать как минимум: product name, version, publisher и uninstall action.
- Uninstall flow должен быть доступен через стандартный Windows surface, а не только через внутреннюю папку установки.

### Launch semantics {#shell-integration.launch}

- Запуск из Start Menu, desktop shortcut или installer “Launch isTranscribe” должен приводить к тому же `open-or-focus-app` behavior, который задан в `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#single-instance`.
- Повторный запуск установленного приложения не должен порождать вторую живую tray instance.
- Установленное приложение не должно показывать console window, dev bootstrap console или transient terminal flashes как часть нормального user launch.

## First Launch And Startup Behavior {#startup-behavior}

### Post-install first launch {#startup-behavior.post-install}

- Installer может предложить checkbox `Launch isTranscribe` на последнем шаге; default допустим `on`.
- Если приложение запускается впервые после install и onboarding не завершён, host обязан открыть first-run wizard согласно `spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard`.
- Первый запуск не должен требовать от пользователя открытия install directory или ручного запуска `.exe` из файловой системы.

### Reboot and autostart {#startup-behavior.autostart}

- Параметр `Autostart with Windows` из `spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general` применяется только после того, как пользователь включил его в настройках или подтвердил его в wizard/defaults.
- Installer сам по себе не должен принудительно включать autostart без продуктовой настройки.
- Если autostart включён, система shell integration обязана обеспечивать запуск установленной app entrypoint после user logon без необходимости dev environment.
- Если autostart выключён, после reboot приложение не должно подниматься silently.

## Upgrade Model {#upgrade-model}

### In-place upgrade {#upgrade-model.in-place}

- Установка более новой версии поверх старой обязана быть поддерживаемым happy path.
- Upgrade не должен сбрасывать `settings.json`, `secrets.bin`, SQLite database, recordings и transcripts.
- Installer обязан либо корректно закрывать running instance, либо запросить пользователя закрыть приложение перед заменой binaries.
- После upgrade Start Menu entry и uninstall registration должны продолжать указывать на новую версию без duplicate app entries.

### Versioning expectations {#upgrade-model.versioning}

- Installer metadata и Apps & Features запись обязаны публиковать user-visible version.
- Release version должна быть достаточно стабильной, чтобы Windows surfaces различали upgrade и fresh install.
- Side-by-side установка нескольких end-user версий одного продукта в MVP не поддерживается.

## Uninstall Model {#uninstall-model}

### Binary removal {#uninstall-model.binaries}

- Uninstall обязан удалить install root, launcher entries и uninstall registration.
- После uninstall приложение не должно оставлять рабочий executable в Start Menu или broken shortcut.

### User data policy {#uninstall-model.data}

- По умолчанию uninstall не должен удалять user-created recordings, transcripts, settings database и logs автоматически.
- Default uninstall behavior обязан считать user data ценными локальными артефактами, которые могут понадобиться после удаления binaries.
- Если выбранный installer contour поддерживает optional `Remove application data`, эта опция должна быть явно отдельной и не включаться по умолчанию.
- Uninstall flow или release documentation обязаны объяснять, где лежат оставшиеся user data, если они сохраняются.

## Release Trust And Signing {#signing}

### Public release artifacts {#signing.public}

- Public MVP release artifact обязан быть подписан code-signing certificate, чтобы Windows SmartScreen и shell surfaces не выглядели как arbitrary unsigned binary.
- Publisher identity в installer и Apps & Features должна быть согласованной между версиями.
- Unsigned release artifact не считается каноническим public distribution contour, даже если технически запускается.

### Development artifacts {#signing.dev}

- Local dev builds, CI artifacts и ручной `dotnet run` остаются допустимыми для development/testing workflows.
- Development contour не заменяет end-user install contour и не должен использоваться как аргумент, что спецификация packaging не нужна.

## Acceptance {#acceptance}

Эта спека достаточно полна для реализации, если одновременно выполняются все условия:

- end-user install contour описан как installer-based, а не как raw SDK workflow;
- install root, data root и их границы не смешиваются;
- Start Menu, Apps & Features и normal launch semantics зафиксированы без двусмысленности;
- upgrade path определён как in-place и не теряет user data;
- uninstall поведение по binaries и data описано явно;
- release signing expectations сформулированы как обязательный public contour;
- scope не подменяет собой store strategy, auto-update или enterprise deployment.

## Document Notes {#document-notes}

- 2026-04-13: Initial spec authored to close the gap between runtime host canon and actual end-user Windows app installation/distribution semantics.
- 2026-07-11: Linked the production Windows x64 change-spec for the final Avalonia payload.
