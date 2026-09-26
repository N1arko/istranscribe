# WI-001: Мигрировать isTranscribe на актуальный spec-driven workflow

- Kind: `migration`
- Canon action: `direct-edit`

## Outcome

Канон, операционный слой и инструкции проекта используют SPEC-MAP, WI-NNN, compact BOARD и checkpoint-only WAL с сохранённой legacy-историей.

## Specs

- Governing: `spec://common/PROP-000#rules`
- Affected: `spec://common/structure#spec-space`

## Scope

- In: protocols, instructions, lifecycle specs, SPEC-MAP, work items, BOARD, WAL, TECHDEBT format и history snapshot.
- Out: продуктовый код, release evidence, изменение пользовательского поведения и массовая замена legacy `@spec`.

## Acceptance

- [x] Все рабочие спеки имеют lifecycle и запись в SPEC-MAP.
- [x] Каждая новая строка BOARD ведёт на WI.
- [x] WAL содержит только актуальный checkpoint и живой cross-work контекст.
- [x] Legacy BOARD/WAL история доступна через snapshot и Git.
- [x] Инструкции агента используют lazy routing.
- [x] Проверка ссылок и структуры проходит.

## Result

Миграция завершена 2026-07-31. Добавлены SPEC-MAP, WI lifecycle, compact BOARD, checkpoint-only WAL, актуальные protocols, human/agent instructions и статическая проверка `eng/verify-spec-workflow.ps1`. Все 40 spec-файлов имеют lifecycle; проверка 202 уникальных `spec://` targets/anchors в коде, тестах и active canon проходит. Legacy operations сохранены в `specs/history/2026-07-31-pre-wi-operations.md` и Git.
