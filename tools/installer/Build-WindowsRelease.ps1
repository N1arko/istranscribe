#Requires -Version 7.0

[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [object[]]$ForwardedArguments
)

$canonicalScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..\packaging\windows\Build-WindowsRelease.ps1"))

# @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#structure
& $canonicalScript @ForwardedArguments
exit $LASTEXITCODE
