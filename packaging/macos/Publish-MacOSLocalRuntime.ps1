#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WhisperNativeOutputRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$Configuration = "Release",
    [string]$RuntimeFrameworkVersion = "10.0.11"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.macos
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

if (-not $IsMacOS -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
    [Runtime.InteropServices.Architecture]::Arm64)
{
    throw "The macOS local-runtime publish must run on macOS arm64."
}

function Invoke-NativeCommand
{
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Assert-ChildPath
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::Ordinal))
    {
        throw "Scoped publish path '$fullPath' escapes '$fullParent'."
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$appProject = Join-Path $repoRoot "src\IsTranscribe.App.MacOS\IsTranscribe.App.MacOS.csproj"
$workerProject = Join-Path $repoRoot "src\IsTranscribe.Transcription.Worker\IsTranscribe.Transcription.Worker.csproj"
$composer = Join-Path $repoRoot "eng\transcription\Compose-LocalTranscriptionPayload.ps1"
$verifier = Join-Path $repoRoot "eng\transcription\Test-LocalTranscriptionPayload.ps1"
$manifest = Join-Path $repoRoot "native\whisper\runtime-manifest.v1.json"
$nativeRoot = [IO.Path]::GetFullPath($WhisperNativeOutputRoot)
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$outputParent = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($outputParent) -or
    [string]::Equals($output, [IO.Path]::GetPathRoot($output), [StringComparison]::Ordinal))
{
    throw "OutputRoot must identify a scoped non-root destination."
}
if (Test-Path -LiteralPath $output)
{
    throw "OutputRoot already exists; choose a fresh destination."
}
foreach ($required in @(
    $appProject,
    $workerProject,
    $composer,
    $verifier,
    $manifest,
    (Join-Path $nativeRoot "osx-arm64/metal/istranscribe_whisper_v1.dylib"),
    (Join-Path $nativeRoot "inventory/osx-arm64-metal.inventory.v1.json")))
{
    if (-not (Test-Path -LiteralPath $required -PathType Leaf))
    {
        throw "Required macOS local-runtime input '$required' is missing."
    }
}

[IO.Directory]::CreateDirectory($outputParent) | Out-Null
$stagingParent = Join-Path $outputParent ".istranscribe-local-runtime-staging"
$stagingRoot = Join-Path $stagingParent ([Guid]::NewGuid().ToString("N"))
$appPublishRoot = Join-Path $stagingRoot "application"
$workerPublishRoot = Join-Path $stagingRoot "worker"
$buildRoot = Join-Path $stagingRoot "build"
try
{
    [IO.Directory]::CreateDirectory($appPublishRoot) | Out-Null
    [IO.Directory]::CreateDirectory($workerPublishRoot) | Out-Null

    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "restore", $appProject,
        "-r", "osx-arm64",
        "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion"
    ) -FailureMessage "macOS application restore failed."
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "restore", $workerProject,
        "-r", "osx-arm64",
        "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion"
    ) -FailureMessage "macOS worker restore failed."

    $commonPublishArguments = @(
        "-c", $Configuration,
        "-r", "osx-arm64",
        "--self-contained", "true",
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion",
        "-p:DebugSymbols=false",
        "-p:DebugType=None")
    $applicationPublishArguments = @(
        "publish", $appProject) + $commonPublishArguments + @(
        "-p:BaseOutputPath=$buildRoot/application/",
        "-o", $appPublishRoot
    )
    Invoke-NativeCommand -FilePath "dotnet" -Arguments $applicationPublishArguments `
        -FailureMessage "macOS application publish failed."
    $workerPublishArguments = @(
        "publish", $workerProject) + $commonPublishArguments + @(
        "-p:BaseOutputPath=$buildRoot/worker/",
        "-o", $workerPublishRoot
    )
    Invoke-NativeCommand -FilePath "dotnet" -Arguments $workerPublishArguments `
        -FailureMessage "macOS worker publish failed."

    Invoke-NativeCommand -FilePath "pwsh" -Arguments @(
        "-NoProfile", "-File", $composer,
        "-Rid", "osx-arm64",
        "-ApplicationPublishRoot", $appPublishRoot,
        "-WorkerPublishRoot", $workerPublishRoot,
        "-NativeOutputRoot", $nativeRoot,
        "-ManifestPath", $manifest
    ) -FailureMessage "macOS local-runtime composition failed."
    Invoke-NativeCommand -FilePath "pwsh" -Arguments @(
        "-NoProfile", "-File", $verifier,
        "-Rid", "osx-arm64",
        "-PackageRoot", $appPublishRoot
    ) -FailureMessage "macOS local-runtime verification failed."

    Move-Item -LiteralPath $appPublishRoot -Destination $output
    Write-Host "Verified macOS local-runtime payload: $output"
}
finally
{
    Assert-ChildPath -Path $stagingRoot -Parent $stagingParent
    if (Test-Path -LiteralPath $stagingRoot)
    {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    if ((Test-Path -LiteralPath $stagingParent -PathType Container) -and
        @(Get-ChildItem -LiteralPath $stagingParent -Force).Count -eq 0)
    {
        Remove-Item -LiteralPath $stagingParent -Force
    }
}
