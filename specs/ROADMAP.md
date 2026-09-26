# ROADMAP

`ROADMAP.md` хранит только крупные волны и устойчивые зависимости. Канон каталогизируется в [SPEC-MAP.md](./SPEC-MAP.md), а текущий статус работы — в [BOARD.md](./BOARD.md).

## Windows release evidence

`WI-002` завершает live acceptance `INFRA-008`. `WI-003` проводит Store private flight по `INFRA-005.B`; эти работы имеют разные внешние блокеры.

## Cross-platform parity

`INFRA-009 → WI-004 → WI-007 → WI-008 → WI-009 → WI-010 → WI-011 → WI-012 / INFRA-010`.

Результат: Apple Silicon app из DMG с parity текущих detection, recording, UI/widget, persistence и recovery flows.

## Optional transcription engines

`WI-012 → WI-005 / FEAT-015`

`WI-012 → WI-006 / FEAT-016`

`FEAT-015` и `FEAT-016` остаются независимыми sibling engines над extension seam `FEAT-014`.
