# isTranscribe

## Что это за проект

`isTranscribe` — личное local-first desktop-приложение, которое помогает не забывать записывать онлайн-встречи.

Release v2 делает один основной сценарий:

1. приложение работает в фоне;
2. локально замечает признаки вероятной встречи;
3. спрашивает пользователя, записывать ли её;
4. записывает output и microphone после подтверждения;
5. формирует один сжатый аудиофайл;
6. сохраняет его в выбранную локальную папку.

Продукт остаётся personal-first: без аккаунта, backend, облачной синхронизации и командной работы.

## Актуальный release-контур

Первая целевая поставка release v2:

- Windows x64;
- обычная установка двойным кликом;
- Start Menu, tray, autostart по выбору пользователя и Installed Apps;
- Ask-only automatic lifecycle;
- ручная команда `Записать сейчас`;
- Zoom, Microsoft Teams, Google Meet, Яндекс Телемост, Контур.Толк и расширяемые app profiles;
- один primary audio artifact на встречу;
- современная компактная Avalonia-оболочка;
- отсутствие активной транскрибации.

macOS является следующей платформой. Release v2 заранее отделяет общий Core от Windows capture/shell adapters, но готовый macOS build не входит в текущую Windows x64 волну.

## Основной пользовательский flow

1. Пользователь устанавливает приложение и проходит одну короткую setup surface.
2. Приложение переходит в состояние `listening` и живёт в tray.
3. Multi-signal detection оценивает process/audio/speech/window evidence.
4. При достаточной уверенности появляется prompt `Похоже, началась встреча. Записать?`.
5. `Записать` подтверждает запись; `Пропустить` закрывает candidate без файла.
6. После завершения приложение смешивает output + microphone, сжимает результат и атомарно сохраняет primary file.
7. Последнюю запись можно открыть из компактного главного окна или напрямую из папки.

## Транскрибация

Fireworks больше не входит в active release runtime.

Базовая release-v2 поставка:

- не требует API key;
- не отправляет аудио во внешний сервис;
- не запускает transcription worker;
- сохраняет доступ к старым transcript artifacts;
- содержит provider-neutral extension seam для локальной или внешней модели.

Post-v2 transcription contour активируется отдельными спеками:

- `FEAT-015` — Groq/OpenRouter speech-to-text;
- `FEAT-016` — fixed full local `large-v3-turbo`;
- `FEAT-017` — три пользовательских режима, source-aware diarization и
  speaker-aware artifacts.

Normal settings не показывает выбор ASR/diarization model. Язык audio
определяется автоматически, microphone получает semantic role `self`, а голоса
system output разделяются локально в local и online modes.

## Устройство системы

Target architecture:

- `IsTranscribe.Core` — domain model, policies, lifecycle и platform-neutral contracts;
- platform-neutral persistence layer — SQLite, migrations и artifact policies;
- `IsTranscribe.Platform.Windows` — WASAPI/NAudio, process observation, UI Automation, DPAPI, tray, hotkeys и Windows shell;
- `IsTranscribe.Desktop` — Avalonia views, view models, themes и composition root;
- future `IsTranscribe.Platform.Mac` — отдельный CoreAudio/shell adapter.

Новый код release v2 не добавляет WPF ownership в Core или product contracts.

## Техническая база

- язык/runtime: C# / .NET 10 LTS;
- desktop UI: current stable Avalonia;
- release architecture: Windows x64;
- metadata: SQLite;
- settings: versioned local JSON;
- protected Windows secrets/migration: DPAPI;
- audio: WASAPI/Core Audio через Windows platform adapter;
- post-processing: application-controlled mix and compression without system-PATH dependency;
- distribution: signed Windows-native package/installer.

## История MVP/v1

`PROP-001`–`PROP-005`, `FEAT-001`–`FEAT-008` и `INFRA-001`–`INFRA-005` описывают реализованный MVP/v1 и сохраняются как исторический канон.

При конфликте release v2 использует:

- [PROP-006-release-v2-product-canon.md](./PROP-006-release-v2-product-canon.md);
- новые release-v2 `FEAT` / `INFRA` спеки из `specs/modules`;
- explicit `Superseded by` / `Supersedes` links в связанных документах.

## Куда смотреть дальше

- [PROP-006-release-v2-product-canon.md](./PROP-006-release-v2-product-canon.md) — актуальные продуктовые границы и release acceptance;
- [PROP-005-local-runtime-and-operations.md](./PROP-005-local-runtime-and-operations.md) — сохраняемый local runtime/recovery канон;
- [structure.md](./structure.md) — карта spec-space;
- [../BOARD.md](../BOARD.md) — все work items и текущий статус;
- [../WAL.md](../WAL.md) — один активный work item и handoff-контекст;
- [../ROADMAP.md](../ROADMAP.md) — последовательность release v2 волн.

## Release v2 work items

1. `INFRA-006` — canon and work breakdown;
2. `INFRA-007` — cross-platform core and Avalonia shell;
3. `FEAT-011` — meeting detection v2;
4. `FEAT-012` — recording artifact pipeline v2;
5. `FEAT-013` — minimal desktop experience;
6. `FEAT-014` — transcription extension seam;
7. `FEAT-010` — localization of the new shell;
8. `INFRA-005.A` — production Windows x64 installer;
9. `INFRA-008` — release hardening and acceptance.

Post-v2 transcription work:

- `WI-005` — Groq/OpenRouter adapters and online acceptance;
- `WI-006` — fixed local `large-v3-turbo` engine;
- `WI-016` — shared modes, source retention, diarization and speaker-aware
  materialization.
