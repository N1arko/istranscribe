#Requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionFilter = Join-Path $repositoryRoot "isTranscribe.macos.slnf"
$artifactRoot = Join-Path $repositoryRoot "artifacts\infra-009\macos-verify"

# @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#build-and-test-contour
dotnet restore $solutionFilter --artifacts-path $artifactRoot
dotnet build $solutionFilter -c Release --no-restore --nologo --artifacts-path $artifactRoot
dotnet test $solutionFilter -c Release --no-build --no-restore --nologo --artifacts-path $artifactRoot
