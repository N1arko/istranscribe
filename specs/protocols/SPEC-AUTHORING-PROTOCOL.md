# SPEC-AUTHORING-PROTOCOL

## Назначение {#purpose}

Протокол применяется для новой спецификации или глубокой переработки существующей. Спека описывает долговечный канон; WI хранит план конкретной реализации и её acceptance.

## Исследование и решение {#research}

Перед черновиком прочитай `common/main.md`, `SPEC-MAP.md`, `common/structure.md`, релевантные PROP и соседние спеки, существующий WI, код и тесты при описании реализованного поведения. Останови поиск, когда понятны ownership, действующий канон и затронутые контракты.

До черновика выбери `direct-edit`, `new-spec` или `supersede`; тип, свободный ID, module/namespace, lifecycle, governing specs, scope и REVIEW-вопросы.

## Регистрация и разбиение {#registration}

Новая или изменившая lifecycle спека получает одну строку в `SPEC-MAP.md`. `structure.md` меняется только при изменении module, namespace или code ownership. WI создаётся для отслеживаемой реализации или отдельной authoring-задачи.

`draft` используется при незавершённом каноне. `active` означает, что scope, сценарии и контракты достаточно определены для governing implementation.

## Требования {#requirements}

`PROP` содержит цель, границы, инварианты или термины, связи, checklist и changelog. `FEAT` и `INFRA` содержат управляющие спеки, scope, сценарии либо operational decisions, данные и состояние при необходимости, контракты, ошибки, traceability, acceptance, связи и changelog.

Документ не хранит owner, priority, session status или прогресс WI. При готовности проверь type, lifecycle, ownership, `spec://`-ссылки, регистрацию, changelog и нужный набор WI.
