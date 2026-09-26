# WAL-PROTOCOL

## Назначение {#purpose}

`WAL.md` — короткая память незавершённой работы между сессиями. Scope и acceptance живут в WI, status и owner — в BOARD, история реализации — в Git.

## Когда нужен {#when}

Создавай или обновляй checkpoint, если сессия заканчивается до завершения WI, нужен handoff, исполнитель переключается на другой WI, предстоит значимый деструктивный шаг либо требуется сохранить следующий шаг или блокер.

WAL не нужен для Backlog, старта WI, задачи в одной сессии, мелких действий и Done.

## Формат {#format}

```md
### WI-011: Название (@handle)
- Work: [WI-011](work/WI-011-short-slug.md)
- Updated: YYYY-MM-DD
- Checkpoint: достигнутое состояние.
- Next: ближайший конкретный шаг.
- Blocker: —
```

Один WI имеет одну активную секцию. WAL содержит `Active Checkpoints`, `Cross-work`, `Decisions Pending`. Секция Completed в новом процессе не используется.

## Правила {#rules}

- Агент редактирует checkpoint своего `@handle`; `.me` обязателен для записи.
- Summary не повторяет scope, acceptance, список спек и историю коммитов.
- Cross-work содержит только живые зависимости нескольких WI.
- Decisions Pending содержит только ожидающие решения человека; принятый результат переносится в owning spec или WI.
- При Done перенеси проверки в Result, удали checkpoint, обнови BOARD и архивируй WI.
