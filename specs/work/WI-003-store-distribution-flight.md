# WI-003: Провести Store distribution flight

- Kind: `implement`
- Canon action: `none`

## Outcome

Store MSIX прошёл private-flight контур с реальными Partner Center данными и lifecycle evidence.

## Specs

- Governing: `spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#root`
- Constraint: `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#acceptance`
- Constraint: `spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#acceptance`

## Scope

- In: Partner Center identity, reviewed privacy/support URLs, age rating, private flight и attended UAC.
- Out: изменение package payload и публичная публикация до acceptance private flight.

## Acceptance

- [ ] Private flight установлен и проверен на реальном Windows окружении.
- [ ] Store metadata и trusted signing подтверждены.
- [ ] Lifecycle evidence добавлено в release index.

## Result

Заполняется при завершении WI.
