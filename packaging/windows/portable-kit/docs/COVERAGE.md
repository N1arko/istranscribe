# Acceptance-kit coverage

<!-- @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification -->

## Automated coverage

- exact package identity, versions, architecture, hashes and development signer;
- clean-profile and certificate-trust preflight;
- Add-AppxPackage install and running upgrade;
- Start Menu registration, package-scoped launch and single-instance reuse;
- default downgrade rejection;
- uninstall, reinstall and low-level canonical-root sentinel preservation;
- final exact package cleanup and machine-readable raw/validated staging evidence.

## Separate live evidence remains required

- App Installer double-click UI and running-update UI;
- first-run onboarding and true application-level settings/database/DPAPI recovery;
- tray, autostart and behavior after a real reboot;
- microphone, WASAPI process loopback and custom recordings folders;
- Zen, Zoom and other real meeting applications;
- production certificate trust and public distribution reputation.

A Sandbox run uses its administrator account and is never labelled standard-user.
The kit carries sourceRevision infra-008-local-lifecycle and proves only these exact binaries.
