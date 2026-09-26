---
status: active
---

# INFRA-005.A: Production Windows X64 Installer {#root}

## Простыми словами {#plain-language}

Release v2 устанавливается двойным кликом как обычное Windows-приложение, появляется в Start Menu и Installed Apps, запускается без консоли и обновляется без потери записей. Старый ZIP с PowerShell-скриптами остаётся development artifact и не выдаётся пользователю как релиз.

## Goal {#goal}

Довести existing INFRA-005 contour до production-ready signed Windows x64 package для финального Avalonia payload.

## Depends on {#depends-on}

- `spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#root`
- `spec://common/PROP-006-release-v2-product-canon#platform.windows-release`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#root`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#tray`

## Changes {#changes}

This change-spec replaces the v1 development bundle as the public distribution entrypoint and fixes the initial release architecture to `win-x64`.

## See also {#see-also.store-distribution}

`spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#root` governs public distribution and production signing. This document governs the MSIX payload, local development acceptance, persistence and packaged-capability requirements.

## Scope {#scope}

### In scope {#scope.in}

- self-contained Windows x64 publish of the final Avalonia app;
- Windows-native double-click install surface;
- Start Menu, Installed Apps, icon and version metadata;
- per-user install without normal-path elevation;
- launch-after-install;
- autostart integration controlled by the app setting;
- single-instance, tray, autostart and process-loopback verification in packaged context;
- in-place update and downgrade rejection policy;
- clean uninstall with preserved user data by default;
- production signing hook and release certificate documentation;
- machine-readable build manifest and checksums.

### Out of scope {#scope.out}

- Windows ARM64/x86;
- Microsoft Store submission;
- enterprise deployment;
- background delta updater service;
- macOS packaging.

## Package Decision {#package-decision}

- Primary release artifact is a signed `MSIX` package for `win-x64` with Windows App Installer double-click UX.
- Package identity, publisher and upgrade family remain stable between releases.
- The Avalonia process runs as a full-trust desktop application within the package contour.
- Before committing the final packaging pipeline, an early package smoke must prove tray, autostart, custom recordings folder, DPAPI/secret migration, WASAPI capture and process loopback.
- Global hotkeys are not exposed by the current FEAT-013 Avalonia release shell. The legacy WPF hotkey runtime is not a packaged-capability prerequisite and is not reintroduced by installer work.
- If a documented MSIX platform restriction prevents a release-critical capability, the same work item switches the public entrypoint to a signed WiX bootstrapper/MSI while preserving all observable requirements. The feasibility smoke is an explicit decision gate, not an open-ended dual packaging strategy.

## Install And Launch {#install}

- User installs by opening one release artifact and confirming the Windows install surface.
- No terminal, PowerShell policy change, archive extraction or SDK/runtime installation is required.
- Install is per-user and registers product name, publisher, semantic version, icon and uninstall action.
- Start Menu launch and repeated executable launch focus/open the existing tray instance.
- Normal launch shows no console flash.
- First launch opens the release-v2 first-run surface only when migration/onboarding requires it.

### MSIX compatibility decisions {#install.msix-compatibility}

- The package targets Windows Desktop `10.0.19041.0` or newer and declares full-trust execution.
- `desktop6:FileSystemWriteVirtualization` and `desktop6:RegistryWriteVirtualization` are disabled with the `unvirtualizedResources` restricted capability so the canonical `%LocalAppData%\isTranscribe` data and HKCU interoperability remain visible and survive package removal.
- Packaged autostart uses a disabled-by-default `windows.startupTask` extension with TaskId `isTranscribeStartup` and `--autostart` parameters. The runtime uses `Windows.ApplicationModel.StartupTask` through the stable `Microsoft.Windows.SDK.NET.Ref 10.0.26100.84` projection when package identity is present and retains the Run-key adapter only for unpackaged development launches. Direct `Microsoft.Windows.SDK.Contracts` metadata is excluded because .NET 5+ rejects direct WinMD references with `NETSDK1130`.
- On packaged initialization, the persisted desired state, last observed package state and current `StartupTask` state are reconciled. Windows user/policy constraints remain authoritative; an unconstrained external re-enable after a previously observed disabled state becomes the newer user choice. The desired, effective and last packaged states are stored separately so unrelated settings remain editable while Windows controls startup.
- The app-owned legacy `HKCU Run\isTranscribe` registration is removed after the packaged target is confirmed. A transient or cancelled enable retains it only when the command has the exact `--autostart` shape, targets an app executable and that executable still exists; malformed, missing or unrelated values are cleared and never counted as effective autostart. Before onboarding, the package performs cleanup-only migration and does not enable autostart without the user's setup choice.
- Cancellation is reconciled against the post-operation Windows state before it is rethrown, preventing duplicate or contradictory registrations after a platform mutation. Settings-persistence rollback restores the prior effective launch-at-login state even when that state was temporarily supplied by a validated legacy fallback.
- Release evidence identifies the application-data roots through the Windows known folders resolved by the runtime (`LocalApplicationData` and `MyDocuments`) plus relative paths. It does not assume that Documents lives under `%USERPROFILE%`; the recordings root remains owned by `settings.recordingsFolder`, with the known-folder path used only as its default.

## Update And Uninstall {#update-uninstall}

- A newer signed version upgrades in place.
- Running-instance handoff/close is deterministic before binary replacement.
- Settings, database, logs, recordings, recovery artifacts and legacy transcripts remain outside package replacement boundaries.
- Uninstall removes binaries, shortcuts/package registration and autostart integration.
- User-created data remains by default; any full data removal is an explicit separate action.
- Reinstall discovers existing data and applies supported migrations.

## Signing And Supply Chain {#signing}

### Supply chain {#supply-chain}

- Public packages are signed by a trusted code-signing identity matching package publisher metadata.
- CI/release tooling accepts signing credentials through secure environment/secret integration and never stores private material in the repository.
- Development builds may use a local test certificate and are clearly marked non-public.
- Release output includes SHA-256 checksums, version manifest and dependency/license inventory.
- Build uses locked package versions and a reproducible documented command.

## Verification {#verification}

Clean Windows x64 VM scenarios:

1. fresh install by double click;
2. first launch and onboarding;
3. tray/single-instance/autostart;
4. manual and Ask recording with process output + microphone;
5. app close/reboot/relaunch;
6. in-place upgrade with preserved settings and artifacts;
7. uninstall with preserved data;
8. reinstall and data recovery;
9. default-user launch without administrator rights;
10. signature and checksum verification.

## Acceptance {#acceptance}

INFRA-005.A is complete when:

1. Public Windows x64 output is one signed native install artifact.
2. PowerShell/ZIP tooling is absent from the user release path.
3. All packaged capability smoke scenarios pass.
4. Install/update/uninstall preserve user data as specified.
5. Start Menu, Installed Apps, tray, autostart and single-instance behavior work on clean Windows.
6. Release metadata, signing hook, checksums and dependency inventory are produced automatically.

## Document Notes {#document-notes}

- 2026-07-12: Rebuilt development packages after moving single-instance registration before Avalonia initialization: `2.0.0` SHA-256 `b2dfae75aad66819987a31b592abfbcb3f443240688584d78fcb4496a608516f`, `2.0.1` SHA-256 `3fed94104e76d9d0376a7ca73220fc37e063ec8fec71ca78a746c48207309cdf`. Regenerated 26-file portable kit archive SHA-256 `8df07894bcf58f75fb33b7a2c136c73a8cdb1c5226f4296b3f1c4bf7680f`, manifest SHA-256 `6833ed00142f77048536afb5dd177a379f15ca4a44be06049edf0f5550a91bb9`; stock PowerShell 5.1 verification and 36 installer contract tests pass. Public distribution moved to `INFRA-005.B`; final local lifecycle waits for an interactive UAC window.
- 2026-07-12: Added a standalone development-only clean-machine acceptance kit with exact 26-file allowlists, external archive digest, byte-verified ZIP promotion, stock PowerShell 5.1 verification, standard-user and manual-trust gates, local read leases, staged evidence validation and isolated Sandbox generation. Final archive `c95c3914b06d0c978f12c30768c5b3cc3cf3e1602caa25c4e67d1fd304b81b26` passes source/ZIP/ledger parity, tamper negatives and independent correctness/security review; live package lifecycle remains gated by a supported clean Windows host.
- 2026-07-12: Hardened the lifecycle harness around official downgrade HRESULT `0x80073D06`, PowerShell 5.1 optional process-id handling, exact owned-process cleanup/recovery and evidence assertions; hardened kit staging and partial-copy cleanup against junction and interrupted-write residue.
- 2026-07-12: Added a host-path-specific Windows Sandbox lifecycle fixture with read-only package/tool inputs, one isolated evidence staging mapping, PowerShell 5.1 two-pass preflight/lifecycle execution, package/harness hash pins, reparse/overlap guards and no automated trust or security changes. The generator reports this Windows Home host as launch-unsupported; live acceptance still requires a supported clean host/VM.
- 2026-07-12: Live Windows App Installer preflight confirmed the native double-click surface, product name, `2.0.1.65535` version, package icon and capability disclosure; Install remained disabled at the expected untrusted development-certificate boundary and no package, data or security state changed.
- 2026-07-12: Added a checked-in stable identity/rollover policy, exact locked runtime and packaging graphs, strict signature/block-map/dependency verification, atomic release/evidence promotion and a disposable-profile lifecycle harness. Final development packages `2.0.0`/`2.0.1` pass artifact verification; live install remains gated by certificate trust and clean-machine acceptance.
- 2026-07-12: Split desired, effective and last-observed packaged autostart state; added constraint-aware Windows reconciliation, validated legacy fallback, cancellation recovery and settings-save rollback coverage.
- 2026-07-12: Added one-time Run-key to packaged StartupTask migration and known-folder-based persistence metadata for redirected/OneDrive Documents profiles.
- 2026-07-12: Fixed the .NET 10 StartupTask projection on stable `Microsoft.Windows.SDK.NET.Ref` and retained Run-key registration only as the unpackaged development fallback.
- 2026-07-12: Removed the stale packaged global-hotkey gate because global hotkeys exist only in the superseded WPF shell and are absent from the accepted FEAT-013 Avalonia release surface.
- 2026-07-11: Change-spec authored to convert the existing development installer contour into the final Windows x64 release package.
