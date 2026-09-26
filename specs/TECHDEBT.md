# TECHDEBT

Реестр осознанно оставленных технических компромиссов.

## Open

## Resolved

### TD-002: Runtime global hotkeys
- Resolved: 2026-07-31
- Evidence: `FEAT-003`; `GlobalHotkeyRuntimeManager` регистрирует сохранённые комбинации на старте и после изменения настроек, маршрутизируя действия в tray/manual control surface.

### TD-004: Third-party notice payload is incomplete
- area: `platform`, Windows package supply chain and distribution compliance
- related: `spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#root`
- current state: закрыто в `INFRA-005.C`: Store и development MSIX содержат deterministic `THIRD-PARTY-NOTICES.txt`, exact 41/31 inventory, Windows SDK/Inter overrides и закреплённые physical package/payload hashes.
- risk: закрыт для текущего Windows x64 release graph; изменение dependency graph блокирует build и требует policy review.
- trigger: n/a
- mitigation: выполнено через offline exact-version policy, independent Store/signed verification, deterministic generation и fail-closed mutation tests.

### TD-003: Commercial AAC licensing review before public distribution
- area: `platform`, Windows audio codec and release distribution
- related: `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#root`
- current state: закрыто в `FEAT-012.A`: новые recordings используют system Media Foundation MP3/`.mp3`; Store fixture `rc.9002` и package verifier подтверждают отсутствие active AAC encoder path и bundled codec binary.
- risk: закрыт для shipping AAC contour; нестандартная модель распространения или отдельная юрисдикция по-прежнему требует индивидуальной оценки.
- trigger: n/a
- mitigation: выполнено через exact MP3 release manifest, PATH-empty golden, двухчасовой encode и AAC-symbol/package rejection.

### TD-001: Bootstrap-only settings subset before INFRA-002
- area: `platform`, desktop host bootstrap persistence
- related: `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#bootstrap.invariants`
- current state: закрыто в `INFRA-002`: host пишет канонический `config/settings.json`, использует legacy bridge для старого bootstrap shape и больше не живёт на bootstrap-only subset.
- risk: закрыт; отдельный migration bridge для subset больше не требуется.
- trigger: n/a
- mitigation: выполнено в `INFRA-002`.
