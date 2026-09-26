---
status: active
---

# FEAT-005: Application Rules And Discovery {#root}

## Простыми словами {#plain-language}

Эта фича задаёт канон управления meeting apps: как пользователь редактирует whitelist, как появляются предложения по новым приложениям, как работает denylist после отказа, и как всё это влияет на automatic detection.

## Goal {#goal}

Описать implementation-ready модель правил приложений и discovery-политик так, чтобы FEAT-001/FEAT-002 использовали единый и предсказуемый источник truth.

## Depends on {#depends-on}

- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://common/PROP-003-audio-capture-and-device-observation#rules`
- `spec://common/PROP-004-meeting-session-and-data-model#entities.app-rule`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`

## Related {#related}

- `spec://modules/app/FEAT-001-first-run-setup-and-settings#behavior`
- `spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#behavior`

## Scope {#scope}

### In scope {#scope.in}

- settings surface для whitelist/discovery/ignored states;
- lifecycle AppRule: add, edit, disable, reorder, remove;
- runtime discovery policy for unknown meeting candidates;
- ignored-apps denylist semantics после user rejection;
- preseed catalog contract для installed known meeting apps.

### Out of scope {#scope.out}

- manual `Force Record` lifecycle;
- recording controls in tray;
- device-switch continuity;
- transcription rendering.

---

## Behavior {#behavior}

Каноническое поведение FEAT-005 определяется разделами:

- состояния приложений: `#app-states`
- settings contract: `#settings-contract`
- discovery policy matrix: `#discovery-policy`
- lifecycle правил: `#app-rule-lifecycle`
- preseed catalog и границы интеграции: `#preseed-catalog`, `#integration-boundaries`

---

## Canonical App States {#app-states}

Продукт обязан различать три user-facing состояния процесса:

- `whitelisted` - есть enabled `AppRule`.
- `ignored` - process name в denylist `ignored_apps`.
- `unknown` - не в whitelist и не в ignored.

Technical exclusions (system/service processes) - отдельный технический слой и не должны отображаться как user decisions.

---

## Settings Contract {#settings-contract}

### Applications section fields {#settings-contract.fields}

В разделе Settings -> Applications обязаны быть:

- `whitelist` (ordered list)
- `auto_discovery_policy` (`auto_add | ask_to_add | off`)
- `ignored_apps` (manageable denylist)
- `technical_exclusions` (read-only preseed + optional user additions)

### Whitelist row model {#settings-contract.whitelist-row}

Минимальные поля строки whitelist:

- `display_name`
- `process_name` (normalized, lowercase, `.exe` suffix)
- `enabled`
- `priority_index`

### Normalization and uniqueness {#settings-contract.normalization}

- `process_name` хранится в canonical normalized виде (например, `zoom.exe`).
- Добавление duplicate normalized process запрещено.
- Если пользователь пытается добавить duplicate, UI предлагает открыть существующее правило вместо создания нового.

### Ordering semantics {#settings-contract.ordering}

- Порядок whitelist определяет приоритет matching при ambiguous scenarios.
- Reorder не меняет identity правила и не должен сбрасывать `enabled`.

---

## Discovery Policy {#discovery-policy}

### Policy matrix {#discovery-policy.matrix}

- `off`: unknown candidate не создаёт prompts и не меняет whitelist.
- `ask_to_add`: unknown candidate вызывает combined runtime prompt из FEAT-002.
- `auto_add`: unknown candidate автоматически добавляется в whitelist и дальше обрабатывается как known app.

Wizard mapping из FEAT-001:

- checkbox enabled -> `ask_to_add`
- checkbox disabled -> `off`
- `auto_add` доступен только в full Settings.

### Discovery eligibility {#discovery-policy.eligibility}

Unknown process может попасть в discovery только если:

- не matched by enabled whitelist;
- не входит в technical exclusions;
- не находится в `ignored_apps`;
- создаёт playback audio activity above threshold duration;
- соответствует общим detection условиям из FEAT-002.

### Prompt loop protection {#discovery-policy.prompt-loop}

- `Not now` suppress-ит только текущий candidate window.
- `Ignore this app` добавляет process в durable `ignored_apps`.
- `ignored_apps` не auto-expire в MVP; управление только пользователем.

---

## AppRule Lifecycle {#app-rule-lifecycle}

### Creation paths {#app-rule-lifecycle.creation}

Поддерживаются три пути создания правила:

1. Preseed suggestion (installed known app catalog).
2. Running-process picker.
3. Manual `.exe` input.

Все пути обязаны создавать одинаковую canonical AppRule model.

### Update paths {#app-rule-lifecycle.update}

Пользователь может:

- изменить `display_name`;
- включить/выключить правило;
- изменить priority order;
- удалить правило.

Удаление правила:

- немедленно исключает процесс из future known-app matching;
- не удаляет исторические meeting sessions.

### Auto-added rules visibility {#app-rule-lifecycle.auto-added}

- Правила, созданные по `auto_add`, должны быть видимы в whitelist как обычные user-editable entries.
- Продукт может пометить их как `auto-added`, но это не ограничивает редактирование.

---

## Preseed Catalog {#preseed-catalog}

### Catalog source {#preseed-catalog.source}

- Приложение содержит встроенный catalog известных meeting apps.
- MVP минимум включает `zoom.exe`; допускаются дополнительные well-known apps.

### Catalog behavior {#preseed-catalog.behavior}

- При first-run можно предложить installed apps как toggles, не создавая forced rules без действия пользователя.
- Если app из catalog позже удалена из системы, существующее правило не resurrect-ится автоматически после user removal.

---

## Integration Boundaries {#integration-boundaries}

### With FEAT-002 {#integration-boundaries.feat-002}

- FEAT-005 владеет policy and state (whitelist/ignored/unknown).
- FEAT-002 владеет runtime prompt execution и meeting-candidate lifecycle.

### With FEAT-001 {#integration-boundaries.feat-001}

- FEAT-001 wizard задаёт безопасное начальное значение discovery policy.
- FEAT-005 определяет полный advanced settings contract после onboarding.

---

## Acceptance {#acceptance}

FEAT-005 считается завершённой, если одновременно выполнено:

1. Есть явная модель `whitelisted | ignored | unknown` и она используется в runtime.
2. Settings -> Applications поддерживает whitelist management, policy switch и ignored-app management.
3. Duplicate process rules предотвращаются canonical normalization.
4. `auto_add`, `ask_to_add`, `off` имеют недвусмысленную матрицу поведения.
5. Rejected app suggestions не приводят к бесконечному prompt loop.
6. Auto-added rules видимы и редактируемы пользователем.
7. Preseed catalog не подменяет user intent и не resurrect-ит удалённые правила silently.

## Document Notes {#document-notes}

- 2026-04-02: Initial FEAT backlog spec authored from the MVP TZ.
- 2026-04-06: Clarified onboarding-facing prompt checkbox and preseeded installed meeting-app suggestions such as Zoom.
- 2026-04-06: Added ignored-app denylist semantics for rejected prompts and clarified that running-process pickers are secondary to known-app onboarding and runtime suggestions.
- 2026-04-13: Rewritten to implementation-ready canon with explicit app-state model, policy matrix, AppRule lifecycle and settings contract.
