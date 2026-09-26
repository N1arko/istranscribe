---
status: active
---

# PROP-000: Base Project Rules {#root}

## Простыми словами {#plain-language}

Этот документ фиксирует фундамент проекта как нового spec-driven репозитория: на каком стеке строится приложение, где живёт канон, как устроены артефакты реализации и какие repo-level правила обязательны по умолчанию.

## Goal {#goal}

Задать фундаментальные решения проекта, без которых нельзя стабильно вести ни spec-space, ни реализацию.

## Scope {#scope}

В документ входят:
- стек, язык и runtime baseline;
- локальный storage/deploy contour MVP;
- observability и test conventions;
- базовая структура `specs/`, `src/`, `tests/`, `tools/`;
- роль `AGENTS.md`, `CLAUDE.md` и `BOOT`;
- базовые naming и referencing conventions.

## Rules {#rules}

- Стек release v2: `C# / .NET 10`, Avalonia, SQLite и JSON; Windows secrets защищаются DPAPI, а platform-specific adapters вынесены из общего runtime.
- Release v2 поставляется как локальное desktop-приложение без обязательного server-side контура. Windows x64 — текущая release-платформа; macOS arm64 готовится отдельным контуром.
- Исходный код и документация isTranscribe публикуются под MIT; полный текст находится в корневом `LICENSE`. Сторонние компоненты и модели сохраняют собственные лицензии. Проектовый канон должен оставаться пригодным для чтения внешними участниками без приватного контекста.
- Каноническое локальное хранение включает recordings, transcript artifacts, app metadata, temp artefacts и logs.
- Observability первой волны локальная: файловые логи, recovery diagnostics и user-openable log folder.
- `src/` хранит код приложения и модулей runtime; `tests/` хранит исполняемые проверки и интеграционные сценарии; `tools/` хранит вспомогательные утилиты вроде `spec-lint`.
- Тесты проверяют канон из спек; код не считается источником истины, если он спорит со спекой.
- `AGENTS.md` и `CLAUDE.md` содержат одинаковый текст.
- Агент всегда начинает с `specs/protocols/BOOT.md`.
- `SPEC-MAP.md` — каталог канона и lifecycle спецификаций.
- `BOARD.md` — оперативная правда по `WI-NNN`.
- `WAL.md` — checkpoint незавершённой работы между сессиями.
- `ROADMAP.md` содержит только крупные волны и устойчивые зависимости.
- `TECHDEBT.md` хранит только осознанные технические компромиссы.
- `PROP`, `FEAT` и `INFRA` содержат lifecycle и адресуются через `spec://...#anchor`.
- Новый или существенно изменённый spec-owned код помечается `@spec` с полным `spec://...#anchor`.

## Acceptance {#acceptance}

Проект соответствует этому `PROP`, если:
- есть рабочий spec-space;
- фундаментальные решения по стеку, хранению, наблюдаемости и тестам зафиксированы явно;
- агент стабильно входит через `BOOT`;
- отслеживаемые work items ведутся через WI-файл и `BOARD`, а `WAL` содержит только checkpoints;
- структура `src/`, `tests/` и `tools/` не остаётся неоговорённой;
- canonical system description можно расширять без потери структуры.

## Document Notes {#document-notes}

- 2026-09-26: Зафиксирована MIT-лицензия исходного кода при открытой публикации проекта.
- 2026-07-31: Мигрирован на WI, SPEC-MAP, lifecycle спек и checkpoint-only WAL.
- 2026-04-02: Expanded from generic scaffold text into real project baseline for the new repo.
