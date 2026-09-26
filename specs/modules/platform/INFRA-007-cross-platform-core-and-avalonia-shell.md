---
status: active
---

# INFRA-007: Cross-Platform Core And Avalonia Shell {#root}

## Простыми словами {#plain-language}

Приложение переезжает с WPF-структуры на современную Avalonia-оболочку, а рабочая логика отделяется от Windows UI. В этой волне по-прежнему выпускается Windows x64, при этом новый core можно будет подключить к macOS adapter без повторного переписывания продукта.

## Goal {#goal}

Создать `.NET 10 LTS` architecture baseline с platform-neutral core, Windows adapters и Avalonia desktop shell, сохранив существующие локальные данные и рабочие capture primitives.

## Depends on {#depends-on}

- `spec://common/PROP-006-release-v2-product-canon#platform.runtime`
- `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#root`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#root`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#root`

## See also {#see-also}

- `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#root`
- `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#root`

## Supersedes {#supersedes}

- WPF ownership decisions from `spec://modules/platform/INFRA-001-windows-desktop-host-baseline#runtime`
- WPF-UI framework decisions from `spec://modules/app/FEAT-008-modern-ui-design-system#behavior.framework`

## Scope {#scope}

### In scope {#scope.in}

- solution upgrade to `.NET 10 LTS`;
- current stable Avalonia desktop application project;
- `IsTranscribe.Core` for platform-neutral domain and application contracts;
- `IsTranscribe.Platform.Windows` for WASAPI/NAudio, processes, UI Automation, tray, hotkeys and Windows shell primitives;
- composition root with dependency injection or an equivalent explicit registration model;
- MVVM baseline and navigation/state host for the future compact shell;
- settings/database compatibility and startup migration;
- preservation of current recordings, transcripts and recovery data;
- Windows x64 build and test contour.

### Out of scope {#scope.out}

- finished visual experience;
- detection v2 scoring;
- new audio post-processing behavior;
- macOS capture implementation;
- installer packaging;
- active transcription.

## Target Structure {#target-structure}

Canonical project ownership:

- `IsTranscribe.Core` — meeting entities, lifecycle contracts, settings contracts, detection abstractions, recording orchestration abstractions and transcription seam;
- `IsTranscribe.Persistence` or an equivalent platform-neutral host library — SQLite repositories, migrations and artifact path policy;
- `IsTranscribe.Platform.Windows` — Windows capability assessment, process/audio/device observation, capture adapters, UI Automation probes, DPAPI, autostart, tray and hotkeys;
- `IsTranscribe.Desktop` — Avalonia application, view models, views, theme resources and composition root;
- matching test projects by responsibility.

Platform-neutral projects must not reference `System.Windows`, WPF-UI, Windows Forms controls, NAudio implementation types or concrete Win32 handles in public contracts.

## Migration Rules {#migration}

- Migration is incremental behind buildable boundaries; no parallel WPF and Avalonia product behavior may diverge silently.
- Existing SQLite schema and local path layout remain readable.
- Existing `settings.json` is migrated atomically to the release-v2 schema.
- Old recording mode `auto` maps to `ask`; `off` maps to service paused.
- Fireworks secrets may remain in protected legacy storage for rollback/read compatibility, while the new UI/runtime never requires or uses them.
- Existing transcript paths remain browsable.
- Single-instance, tray-first lifetime and crash recovery semantics remain supported on Windows.

## Desktop Baseline {#desktop-baseline}

- The Avalonia app starts without a console window.
- The initial shell may be visually minimal during this infrastructure item, but it must expose observable runtime state needed by later FEAT-013.
- Close-to-tray and explicit Quit are distinct actions.
- Activation of an existing process focuses or opens the existing window.
- Theme resources use semantic tokens and support light/dark/system modes.
- View models own presentation state; platform services and repositories are not called directly from view code-behind.

## Windows Adapter Contract {#windows-adapters}

- Existing proven WASAPI/process-loopback primitives may be moved or wrapped instead of rewritten without evidence.
- Platform adapters expose normalized snapshots/contracts defined in Core.
- Win32/COM object lifetime stays inside the Windows project.
- Capability degradation is explicit and observable.
- New platform-specific implementations receive `@spec` markers at ownership points.

## Verification {#verification}

- `dotnet build isTranscribe.sln -c Release` succeeds on Windows.
- All migrated unit/integration tests run under the new target framework.
- A Windows x64 smoke proves launch, single-instance activation, tray presence, open/close-to-tray and explicit quit.
- A migration smoke starts with a copy of v1 settings/database and proves recordings remain listed.
- Dependency inspection proves Core has no WPF/WinForms/NAudio implementation dependency.

## Acceptance {#acceptance}

INFRA-007 is complete when:

1. The solution targets .NET 10 and contains the canonical project boundaries.
2. Avalonia is the only release-v2 UI shell.
3. Core compiles without Windows UI framework references.
4. Windows capture/device/process services are available through Core-owned contracts.
5. Existing user settings and data migrate without loss.
6. Windows x64 desktop smoke and the complete automated test suite pass.
7. WPF release entrypoint is removed from the final build contour or retained only as an explicitly non-release migration fallback.

## Document Notes {#document-notes}

- 2026-07-30: Linked the post-v2 boundary completion and macOS adapter waves.
- 2026-07-11: Initial release-v2 architecture spec authored for Windows x64 first and future macOS adapters.
- 2026-07-11: Реализация завершена: solution переведён на .NET 10; добавлены Core, Persistence, Windows adapter и Avalonia Desktop boundaries; v1 settings/database migration, recovery, single-instance, tray-first lifecycle и Windows x64 smoke подтверждены; WPF вынесен в отдельный нерелизный fallback solution.
