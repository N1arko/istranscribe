# BOARD-PROTOCOL

## Назначение {#purpose}

`BOARD.md` — компактный оперативный индекс work items. `SPEC-MAP.md` каталогизирует канон, файлы WI содержат scope и acceptance, `WAL.md` хранит checkpoint, Git хранит полную историю реализации.

## Формат {#format}

Одна строка соответствует одному `WI-NNN` и ссылается на его файл. Колонка `Specs` хранит компактные IDs, а полные `spec://...#anchor` находятся в WI.

| Колонка | Обязательные поля |
|---|---|
| Backlog | Work, Title, Specs, Owner, Priority |
| In Progress | Work, Title, Specs, Owner, Started, Blocker |
| Blocked | Work, Title, Owner, Reason, Waiting for |
| Done | Work, Title, Owner, Date |

Legacy-строки с `FEAT-*` и `INFRA-*` допустимы только как сохранённая история до завершения migration. Новая строка всегда ведёт на WI.

## Правила {#rules}

- `BOARD.md` — единственный источник status, owner и priority.
- Агент меняет строку своего `@handle`; `specs/.me` требуется перед такой операцией.
- Немедленно начатый WI помещается сразу в `In Progress`.
- `Blocked` применяется при внешней зависимости, доступе, решении человека или другом WI. Обычная сложность остаётся `In Progress`.
- `Done` требует достигнутого outcome, пройденного acceptance, заполненного Result, согласованных кода и канона, закрытого checkpoint WAL и отражённого техдолга.
- Файл готового WI переносится в `work/archive/YYYY/`. В `Done` остаются последние десять WI; более ранняя история доступна в архиве и Git.

## Синхронизация {#sync}

Проверь:
- каждая строка WI ведёт на существующий файл;
- один WI находится только в одной колонке;
- `Specs` доски соответствует файлу WI;
- checkpoint существует только для `In Progress` или `Blocked`;
- completed WI имеет Result и архивный путь.
