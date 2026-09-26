isTranscribe installer acceptance kit — DEVELOPMENT TEST ONLY

This kit validates the exact development-signed Windows x64 packages 2.0.0 and
2.0.1. It is tester tooling. It is not an installer for distribution and it is
not eligible for a public release.

Authenticity
Before extraction, compare the ZIP SHA-256 with the separate .sha256 file received
through the trusted handoff channel. The checksum ledger inside the ZIP detects
corruption after extraction; it cannot authenticate a ZIP that was replaced
together with all of its contents.

Clean Windows VM — required standard-user scenario
1. Use a disposable Windows x64 VM, build 19041 or newer, with a clean snapshot.
2. In a separate administrator setup session, open
   packages\2.0.1\isTranscribe-development-certificate.cer and manually install
   it into Local Machine / Trusted People. Verify thumbprint
   66075815637CA0D5059B64D57C5E22100B142024 and subject
   CN=isTranscribe Development. Sign out of the administrator session.
3. Sign in with a local standard-user account that is not a member of the local
   Administrators group.
4. Run Run-Clean-VM-Preflight.cmd. It performs no package or application-data
   mutation and writes evidence beside the immutable kit directory.
5. Revert the snapshot or use a second clean standard-user profile when the manual
   App Installer double-click flow must start from a pristine profile.
6. Run Run-Clean-VM-Lifecycle.cmd on a clean disposable profile for the automated
   install, launch, upgrade, downgrade rejection, uninstall and reinstall contour.
7. Preserve the external evidence directory, then revert the VM. Remove temporary
   certificate trust by reverting the snapshot.

Windows Sandbox — administrator-labelled lifecycle only
1. Keep the extracted kit on the Windows Sandbox host.
2. Run Run-Windows-Sandbox.cmd. The generated .wsb uses absolute host paths and is
   valid only on that host.
3. Inside Sandbox, manually trust the same public certificate in Local Machine /
   Trusted People, then run C:\isTranscribeAcceptance\kit\tools\sandbox\
   Run-In-Sandbox.cmd.
4. Close Sandbox after collecting the staging evidence. Sandbox evidence is
   explicitly labelled administrator/Sandbox and does not prove standard-user use.

Read docs\CLEAN-VM-CHECKLIST.md and docs\COVERAGE.md before claiming acceptance.
No script in this kit imports a certificate, elevates itself, enables Developer
Mode, changes a persistent execution policy or modifies host security settings.
