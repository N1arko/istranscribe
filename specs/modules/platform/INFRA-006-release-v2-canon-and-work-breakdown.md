---
status: retired
---

# INFRA-006: Release V2 Canon And Work Breakdown {#root}

## Простыми словами {#plain-language}

Этот item переводит проект со старого MVP-плана на release v2. Он фиксирует новые продуктовые решения, сохраняет старые спеки как историю, заводит все этапы в `BOARD` и оставляет одновременно активной только одну следующую задачу.

## Goal {#goal}

Подготовить непротиворечивый spec-driven execution contour для последовательной реализации Windows x64 release v2.

## Retired {#retired}

Этот документ сохраняет историческое решение о переходе к release v2. Текущий каталог канона находится в `SPEC-MAP.md`, очередность WI — в `BOARD.md`, а устойчивые волны — в `ROADMAP.md`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#root`
- `spec://common/PROP-002-app-shell-and-settings#root`
- `spec://common/PROP-003-audio-capture-and-device-observation#root`
- `spec://common/PROP-004-meeting-session-and-data-model#root`
- `spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#root`

## Produces {#produces}

- `spec://common/PROP-006-release-v2-product-canon#root`;
- implementation-ready release-v2 `FEAT` / `INFRA` specs;
- ordered work-item backlog in `specs/BOARD.md`;
- release-v2 waves and dependencies in `specs/ROADMAP.md`;
- supersession links from old MVP specs;
- synchronized active work state in `specs/WAL.md`.

## Scope {#scope}

### In scope {#scope.in}

- product canon for Windows x64 first release;
- explicit retirement of Fireworks runtime and Auto mode from release v2;
- work breakdown for architecture, detection, audio pipeline, experience, future transcription seam, installer and hardening;
- dependency order and ownership;
- preservation of unfinished old installer work as a later change-wave.

### Out of scope {#scope.out}

- production code changes;
- UI implementation;
- capture/detection implementation;
- installer build changes;
- release execution.

## Work Breakdown Rules {#work-breakdown}

- Each board row maps to one addressable canonical spec URI with a `#root` anchor.
- Only one `@nikita` work item is `In Progress` during the rebuild unless the user explicitly changes the execution policy.
- Completed MVP items remain in `Done`; changed behavior is represented by new items or change-specs.
- FEAT-009 is moved to a `Superseded` service column because its six-tab preservation strategy conflicts with release v2.
- INFRA-005 returns to Backlog and is governed for the new wave by `INFRA-005.A` after the final desktop payload exists.
- FEAT-010 remains in Backlog and is applied to the new shell after FEAT-013.

## Acceptance {#acceptance}

INFRA-006 is complete when:

1. `PROP-006` contains scope, behavior, architecture, distribution and release acceptance.
2. Every required implementation stage exists once in `BOARD` with owner and spec.
3. All new specs contain plain-language, scope, decisions/behavior, acceptance and document notes.
4. Old conflicting specs point to the release-v2 replacements.
5. `ROADMAP` orders work from architecture through release audit.
6. `WAL` and `BOARD` agree on the single active item.
7. No production source file was changed as part of spec authoring.

## Document Notes {#document-notes}

- 2026-07-11: Initial spec authored for the release v2 reset and board-first execution policy.
