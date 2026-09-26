isTranscribe Windows installer acceptance

Purpose
This tester-only fixture checks development MSIX 2.0.0 -> 2.0.1 install, running
upgrade, downgrade rejection, uninstall data preservation, reinstall and final
cleanup in one disposable Windows Sandbox session.

Requirements
- Windows 10 2004 (build 19041) or newer, or Windows 11, on a Pro,
  Enterprise or Education x64 edition with Windows Sandbox enabled.
- The generated isTranscribe-installer-acceptance.wsb file.
- An empty host sandbox-staging directory created by the generator.

The .wsb contains absolute paths from the computer that generated it. Keep the
repository and release artifacts on the Windows Sandbox host, and rerun
New-WindowsSandboxAcceptance.ps1 there before opening the configuration. Copying
the .wsb file by itself to another computer does not create a portable fixture.

Inside Windows Sandbox
1. Open C:\isTranscribeAcceptance\packages\2.0.1.
2. Open isTranscribe-development-certificate.cer.
3. Choose Install Certificate, Local Machine, Place all certificates in the
   following store, Trusted People. Approve the Sandbox-local UAC prompt.
4. Confirm the certificate thumbprint is
   66075815637CA0D5059B64D57C5E22100B142024 and the subject is
   CN=isTranscribe Development.
5. Open C:\isTranscribeAcceptance\tools\windows-sandbox and double-click
   Run-IsTranscribe-Installer-Acceptance.cmd.
6. Review windows-sandbox-preflight.json and windows-sandbox-lifecycle.json in
   C:\isTranscribeAcceptance\evidence. These files persist in the host's
   sandbox-staging directory and must be reviewed before promotion to canonical
   acceptance evidence.
7. Close Windows Sandbox. Closing it discards the installed package, app data,
   local working files and the temporary machine certificate trust.

Safety boundary
The configuration maps packages and installer tools read-only. Only the dedicated
sandbox-staging directory is writable from Sandbox. Networking, clipboard,
printer, microphone, camera and vGPU access are disabled. No script imports a
certificate, enables Developer Mode, changes a persistent PowerShell execution
policy, enables Windows Sandbox or changes host security settings. The command
runner uses a process-only execution policy for the disposable PowerShell process.
Certificate trust is always the explicit manual step above.

Coverage boundary
This lifecycle fixture does not validate App Installer UI, a true standard-user
account, reboot/autostart, audio capture, process loopback, custom recordings
folders, Zen/Zoom meetings or production certificate trust. Those remain clean-VM
and live acceptance scenarios. This development certificate and these MSIX files
must never be published as the public installer.
