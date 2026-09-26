# WI-011: Собрать macOS app bundle и unsigned DMG

- Kind: `implement`
- Canon action: `none`

## Outcome

Один reproducible release command создаёт self-contained Apple Silicon
`isTranscribe.app`, unsigned drag-to-Applications DMG, manifest, checksums и notices.

## Specs

- Governing: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution`
- Constraint: `spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#target`
- Constraint: `spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#decisions.payload`

## Dependencies

- Depends on: `WI-008`, `WI-009`, `WI-010`.

## Scope

- In: self-contained `osx-arm64` publish, production `.app` layout, Info.plist,
  icon, usage descriptions, entitlements, app-relative native payload, unsigned
  DMG with Applications link, Gatekeeper instructions, manifest, checksums,
  notices and optional future signing/notarization inputs.
- Out: Developer ID acquisition, mandatory notarization, Mac App Store,
  universal binary, hosted distribution and auto-update.

## Acceptance

- [ ] `.app` has stable bundle/executable identity, minimum macOS 14.2 and arm64-only payload.
- [ ] Native libraries load from app-relative paths; unknown libraries, absolute build
  paths, wrong architecture and missing notices fail verification.
- [ ] DMG contains the app, Applications link and unsigned first-launch guidance.
- [ ] Physical payload-derived manifest and SHA-256 checksums pass fail-closed mutation tests.
- [ ] Two builds have identical governed logical payload inventory.
- [ ] Packaging verification proves a self-contained app with no repository-local
  paths or undeclared runtime dependencies.

## Result

Заполняется при завершении WI.
