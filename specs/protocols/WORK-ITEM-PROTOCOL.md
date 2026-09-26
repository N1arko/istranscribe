# WORK-ITEM-PROTOCOL

## Назначение {#purpose}

Work item — ограниченная единица работы с отдельным наблюдаемым outcome и собственной проверкой готовности. Спецификация фиксирует долгоживущий канон; WI фиксирует конкретный проход реализации, исследования, изменения или миграции.

## Когда нужен {#when}

Создавай WI при самостоятельном scope или acceptance, нескольких шагах, риске, блокере, зависимости, отдельном owner/priority, продолжении между сессиями, координации либо прямом запросе человека. Одношаговая правка в текущей сессии может обойтись без WI, BOARD и WAL.

## Идентификатор и хранение {#naming}

Используй отдельный namespace `WI-NNN`. Активный файл: `specs/work/WI-NNN-short-slug.md`. После завершения перенеси его в `specs/work/archive/YYYY/`. Номер не переиспользуется.

## Формат {#format}

```md
# WI-024: Краткое название

- Kind: `fix`
- Canon action: `none`

## Outcome

Один проверяемый результат.

## Specs

- Governing: `spec://modules/app/FEAT-001-example#root`

## Scope

- In: конкретные части текущего прохода.
- Out: соседние области.

## Acceptance

- [ ] Проверка результата.

## Result

Заполняется при завершении: итог, проверки, commit или release evidence.
```

Допустимые `Kind`: `implement`, `fix`, `change`, `migration`, `research`.

Допустимые `Canon action`: `none`, `direct-edit`, `new-spec`, `supersede`.

## Ссылки и размер {#slicing}

`Governing` задаёт обязательное поведение. `Affected` меняется вместе с результатом. `Constraint` задаёт обязательное ограничение. Для implement/fix/change/migration нужен Governing.

Один WI имеет один outcome и одно решение о завершении. Разделяй WI, если части независимо выпускаются, проверяются, блокируются или относятся к разным последовательным волнам.

## Изменения и завершение {#done}

- Расхождение с ясной активной спекой: `Kind: fix`, `Canon action: none`, reproduction и regression test в acceptance.
- Изменение внутри прежней ответственности: `Kind: change`, `Canon action: direct-edit`.
- Замена или разделение ответственности: `Canon action: supersede` и обновление lifecycle.
- При Done outcome достигнут, acceptance пройден, Result заполнен, WAL удалён, BOARD обновлён, WI перенесён в архив.
