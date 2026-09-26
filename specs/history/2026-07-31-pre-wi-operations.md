# Pre-WI operational snapshot — 2026-07-31

Этот файл сохраняет состояние до migration на `WI-NNN`. Полный исходный текст доступен в Git на commit `82272f9` и ранее.

## BOARD

- Backlog: `INFRA-010`, `FEAT-015`, `FEAT-016`.
- Blocked: `INFRA-008` — live Zoom/UAC/release gates; `INFRA-005` — Partner Center, hosted URLs, age rating, private flight и UAC.
- Superseded: `FEAT-009` заменён `FEAT-013`.
- Done: 25 legacy FEAT/INFRA items, включая `INFRA-009` (2026-07-31), `FEAT-013.A` (2026-07-30) и release-v2 foundation (2026-07-11–2026-07-12).

## WAL

- Active session: `INFRA-008`, blocked после реализации 20-second automatic finish; следующий шаг — реальная Zoom-перепроверка.
- Completed: 26 legacy completion records.
- Cross-module: `INFRA-009 → INFRA-010 → FEAT-015/016`; release-v2 dependency contour; installer/Store prerequisites.
- Decision pending: внешние Partner Center и attended-UAC условия `INFRA-005`.

## Migration mapping

- `INFRA-008` продолжен как `WI-002`.
- Открытый Store scope `INFRA-005` продолжен как `WI-003` с governing `INFRA-005.B`.
- Будущие `INFRA-010`, `FEAT-015`, `FEAT-016` продолжены как `WI-004`, `WI-005`, `WI-006`.

Этот snapshot не является active canon и не участвует в spec-lint.
