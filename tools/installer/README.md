# Compatibility forwarding command

Windows packaging now lives in `packaging/windows`. The checked-in
`Build-WindowsRelease.ps1` wrapper preserves the documented engineering command
while forwarding all arguments to the canonical script. New automation should
call `packaging/windows/Build-WindowsRelease.ps1` directly.

@spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#structure
