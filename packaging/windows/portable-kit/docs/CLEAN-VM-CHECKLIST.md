# Clean Windows VM checklist

<!-- @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification -->

## Before the run

- Use Windows x64 build 19041 or newer.
- Start from a disposable clean snapshot and keep the kit on a local NTFS path.
- Compare the archive SHA-256 with the separate digest from the trusted handoff.
- Import the public development certificate manually from an administrator setup
  session into Local Machine / Trusted People.
- Sign into a separate local standard-user account. The clean-VM runner refuses an
  account whose token contains the local Administrators SID.
- Confirm that isTranscribe is absent from Installed Apps and that the profile has
  no existing isTranscribe application-data root or legacy Run entry.

## Automated lifecycle

1. Run the preflight command and retain both raw and validated evidence.
2. Start the lifecycle command only on a disposable clean profile.
3. Confirm that validated lifecycle evidence exists and raw evidence is retained.
4. Confirm the final evidence reports no package registration and no sentinel.
5. Revert the snapshot even after a passed run.

## Manual App Installer and capability pass

Use a reset snapshot or a second clean standard-user profile. Open the base MSIX by
double-click, inspect product/publisher/version/capabilities, install and complete
first run. Exercise tray, single instance, autostart, custom recordings folder,
manual and Ask recording with microphone plus process output, close/reboot/relaunch,
then open the upgrade MSIX while the app is running. Uninstall from Installed Apps
and verify that user data remains. Record this separately from PowerShell lifecycle
evidence.
