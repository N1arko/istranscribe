#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("osx-arm64", "win-x64")][string]$Rid,
    [Parameter(Mandatory)][string]$PackageRoot,
    [switch]$AllowAbsent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance

$ExpectedManifestSha256 = "fe37a0d085edaab7925c25f8e62514fe714dda4c66cfe3f2c48e2bea191d2feb"
$WorkerBaseName = "IsTranscribe.Transcription.Worker"

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-LowerSha256
{
    param([AllowEmptyString()][string]$Value, [Parameter(Mandatory)][string]$Label)
    if ($Value -cnotmatch '^[0-9a-f]{64}$') { throw "$Label is not a lowercase SHA-256." }
}

function Get-SafePackagedFile
{
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)')
    {
        throw "Unsafe packaged path '$RelativePath'."
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, $comparison) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Packaged file '$RelativePath' is missing or escapes the package root."
    }
    $item = Get-Item -LiteralPath $path -Force
    if ($null -ne $item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Packaged file '$RelativePath' cannot be a link or reparse point."
    }
    $path
}

$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $root -PathType Container))
{
    throw "Package root '$root' is missing."
}
$rootItem = Get-Item -LiteralPath $root -Force
if ($null -ne $rootItem.LinkType -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
{
    throw "Package root cannot be a link or reparse point."
}

$inventoryRelativePath = "native/runtime-inventory.v1.json"
$inventoryPath = Join-Path $root $inventoryRelativePath
if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf))
{
    if ($AllowAbsent)
    {
        $partialPayload = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Where-Object {
            $relativePath = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            $_.Name.StartsWith($WorkerBaseName, [StringComparison]::OrdinalIgnoreCase) -or
            $relativePath -ieq "native/runtime-manifest.v1.json" -or
            $_.Name.StartsWith("istranscribe_whisper_v1", [StringComparison]::OrdinalIgnoreCase)
        })
        if ($partialPayload.Count -gt 0)
        {
            throw "The package contains a partial local-transcription payload without its composition receipt."
        }
        [pscustomobject]@{ Present = $false; Rid = $Rid } | ConvertTo-Json -Compress
        return
    }
    throw "The packaged local-transcription runtime inventory is missing."
}
$inventory = Get-Content -LiteralPath $inventoryPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([string]$inventory.schemaVersion -cne "istranscribe-local-runtime-package-v1" -or
    [string]$inventory.rid -cne $Rid -or -not [bool]$inventory.worker.selfContained)
{
    throw "The packaged local-transcription runtime inventory is incompatible."
}

$workerExecutableName = if ($Rid -ceq "win-x64") { "$WorkerBaseName.exe" } else { $WorkerBaseName }
if ([string]$inventory.worker.executable -cne $workerExecutableName)
{
    throw "The packaged worker executable identity is incorrect."
}
$requiredWorkerPaths = @(
    $workerExecutableName,
    "$WorkerBaseName.dll",
    "$WorkerBaseName.deps.json",
    "$WorkerBaseName.runtimeconfig.json")
foreach ($requiredWorkerPath in $requiredWorkerPaths)
{
    [void](Get-SafePackagedFile -Root $root -RelativePath $requiredWorkerPath)
}
$workerFiles = @($inventory.worker.files)
if ($workerFiles.Count -lt $requiredWorkerPaths.Count)
{
    throw "The packaged worker inventory is incomplete."
}
$workerPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($workerFile in $workerFiles)
{
    $relativePath = [string]$workerFile.path
    if (-not $workerPathSet.Add($relativePath))
    {
        throw "The packaged worker inventory repeats '$relativePath'."
    }
    if ([string]$workerFile.disposition -notin @("worker", "shared-byte-identical"))
    {
        throw "The packaged worker disposition for '$relativePath' is invalid."
    }
    Assert-LowerSha256 -Value ([string]$workerFile.sha256) -Label "Worker '$relativePath' hash"
    $path = Get-SafePackagedFile -Root $root -RelativePath $relativePath
    if ((Get-Item -LiteralPath $path).Length -ne [long]$workerFile.sizeBytes -or
        (Get-LowerSha256 -Path $path) -cne [string]$workerFile.sha256)
    {
        throw "worker_payload_mismatch: '$relativePath' differs from its composition receipt."
    }
}
foreach ($requiredWorkerPath in $requiredWorkerPaths)
{
    if (-not $workerPathSet.Contains($requiredWorkerPath))
    {
        throw "The packaged worker inventory omits '$requiredWorkerPath'."
    }
}
$hostFxrPath = if ($Rid -ceq "win-x64") { "hostfxr.dll" } else { "libhostfxr.dylib" }
if (-not $workerPathSet.Contains($hostFxrPath))
{
    throw "The packaged worker inventory does not prove a self-contained runtime."
}
$workerDepsPath = Get-SafePackagedFile `
    -Root $root `
    -RelativePath "$WorkerBaseName.deps.json"
$workerDeps = Get-Content -LiteralPath $workerDepsPath -Raw -Encoding utf8 | ConvertFrom-Json
if (-not ([string]$workerDeps.runtimeTarget.name).EndsWith(
        "/$Rid",
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "The packaged worker deps.json does not target '$Rid'."
}
$workerRuntimeConfigPath = Get-SafePackagedFile `
    -Root $root `
    -RelativePath "$WorkerBaseName.runtimeconfig.json"
$workerRuntimeConfig = Get-Content `
    -LiteralPath $workerRuntimeConfigPath `
    -Raw `
    -Encoding utf8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$workerRuntimeConfig.runtimeOptions.tfm))
{
    throw "The packaged worker runtimeconfig.json has no target framework."
}

Assert-LowerSha256 `
    -Value ([string]$inventory.native.runtimeManifestSha256) `
    -Label "Native runtime manifest hash"
# @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
if (-not $inventory.PSObject.Properties["speaker"] -or $inventory.speaker.ttsEnabled -ne $false -or
    $inventory.speaker.eigenMpl2Only -ne $true -or $inventory.speaker.runtimeVersion -cne "sherpa-onnx/1.12.14") {
    throw "The reviewed speaker processing payload is missing."
}
$speakerPaths = @($inventory.speaker.files | ForEach-Object { [string]$_.path })
$requiredSpeakerLibraries = if ($Rid -ceq "osx-arm64") { @("libonnxruntime.1.17.1.dylib", "libsherpa-onnx-c-api.dylib") }
    else { @("onnxruntime.dll", "sherpa-onnx-c-api.dll") }
if ($speakerPaths.Count -ne 17 -or @($speakerPaths | Sort-Object -Unique).Count -ne 17 -or
    (@($speakerPaths | Where-Object { -not $_.StartsWith("speaker-licenses/", [StringComparison]::Ordinal) } | Sort-Object) -join "`n") -cne
    (@($requiredSpeakerLibraries | Sort-Object) -join "`n") -or $speakerPaths -cnotcontains "speaker-licenses/SOURCE-AND-NOTICES.md") {
    throw "The speaker runtime file or license set is incomplete."
}
foreach ($entry in @($inventory.speaker.files)) {
    $path = Get-SafePackagedFile -Root $root -RelativePath ([string]$entry.path)
    if ((Get-LowerSha256 -Path $path) -cne [string]$entry.sha256 -or (Get-Item -LiteralPath $path).Length -ne [long]$entry.sizeBytes) {
        throw "Speaker processing asset integrity mismatch."
    }
}
if ([string]$inventory.native.runtimeManifestSha256 -cne $ExpectedManifestSha256)
{
    throw "The packaged native runtime manifest does not match the app-owned pin."
}
$manifestPath = Get-SafePackagedFile `
    -Root $root `
    -RelativePath ([string]$inventory.native.runtimeManifestPath)
if ((Get-LowerSha256 -Path $manifestPath) -cne $ExpectedManifestSha256)
{
    throw "The packaged native runtime manifest bytes changed."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([string]$manifest.schemaVersion -cne "istranscribe-whisper-native-v1" -or
    [string]$manifest.source.commit -cne [string]$inventory.native.sourceCommit -or
    [string]$manifest.source.archive.sha256 -cne [string]$inventory.native.sourceArchiveSha256)
{
    throw "The packaged native runtime source identity is inconsistent."
}

$libraries = @($inventory.native.libraries)
$requiredVariant = if ($Rid -ceq "win-x64") { "cpu" } else { "metal" }
if (@($libraries | Where-Object variant -CEQ $requiredVariant).Count -ne 1)
{
    throw "The packaged native runtime lacks the required '$requiredVariant' variant."
}
$nativeExpectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$nativeExpectedFiles.Add("runtime-inventory.v1.json")
[void]$nativeExpectedFiles.Add("runtime-manifest.v1.json")
foreach ($library in $libraries)
{
    $relativePath = [string]$library.path
    if (-not $relativePath.StartsWith("native/", [StringComparison]::Ordinal) -or
        [string]$library.variant -notin @("cpu", "metal", "vulkan"))
    {
        throw "The packaged native library declaration is invalid."
    }
    Assert-LowerSha256 -Value ([string]$library.sha256) -Label "Native '$relativePath' hash"
    $path = Get-SafePackagedFile -Root $root -RelativePath $relativePath
    if ((Get-Item -LiteralPath $path).Length -ne [long]$library.sizeBytes -or
        (Get-LowerSha256 -Path $path) -cne [string]$library.sha256)
    {
        throw "Packaged native library '$relativePath' differs from its inspected inventory."
    }
    if (-not $nativeExpectedFiles.Add([IO.Path]::GetFileName($relativePath)))
    {
        throw "The packaged native inventory repeats '$relativePath'."
    }
}
$actualNativeFiles = @(Get-ChildItem -LiteralPath (Join-Path $root "native") -Force -File |
    ForEach-Object Name | Sort-Object)
$expectedNativeFiles = @($nativeExpectedFiles | Sort-Object)
if (($actualNativeFiles -join "`n") -cne ($expectedNativeFiles -join "`n"))
{
    throw "The packaged native directory contains an untracked or missing file."
}

$manifestLicenseByComponent = @{}
foreach ($license in @($manifest.licenses))
{
    if ($manifestLicenseByComponent.ContainsKey([string]$license.component))
    {
        throw "The native runtime manifest repeats a license component."
    }
    $manifestLicenseByComponent[[string]$license.component] = $license
}
$receiptLicenses = @($inventory.native.licenses)
if ($receiptLicenses.Count -ne $manifestLicenseByComponent.Count)
{
    throw "The packaged native license inventory is incomplete."
}
foreach ($license in $receiptLicenses)
{
    $component = [string]$license.component
    if (-not $manifestLicenseByComponent.ContainsKey($component))
    {
        throw "The packaged native license '$component' is absent from the manifest."
    }
    $expected = $manifestLicenseByComponent[$component]
    if ([string]$license.spdx -cne [string]$expected.spdx -or
        [string]$license.checkedInPath -cne [string]$expected.checkedInPath -or
        [string]$license.sha256 -cne [string]$expected.sha256)
    {
        throw "The packaged native license '$component' differs from the manifest."
    }
}

$forbiddenPatterns = @(
    "*.ggml", "*.gguf", "ggml-*.bin", "ggml_*.bin", "*whisper-cli*", "*whisper-server*",
    "*.py", "*python*", "*ffmpeg*", "*cuda*", "*rocm*", "*openvino*")
foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -Force -File))
{
    foreach ($pattern in $forbiddenPatterns)
    {
        if ($file.Name -like $pattern)
        {
            throw "Forbidden local-transcription payload '$($file.Name)' is packaged."
        }
    }
}

$noticePath = Join-Path $root "THIRD-PARTY-NOTICES.txt"
if (Test-Path -LiteralPath $noticePath -PathType Leaf)
{
    $notice = Get-Content -LiteralPath $noticePath -Raw -Encoding utf8
    foreach ($requiredNoticeText in @(
        "whisper.cpp and ggml",
        "OpenAI Whisper",
        [string]$manifest.source.commit))
    {
        if (-not $notice.Contains($requiredNoticeText, [StringComparison]::Ordinal))
        {
            throw "Packaged third-party notices omit native evidence '$requiredNoticeText'."
        }
    }
}

[pscustomobject]@{
    Present = $true
    Rid = $Rid
    WorkerExecutable = $workerExecutableName
    WorkerFileCount = $workerFiles.Count
    NativeLibraryCount = $libraries.Count
    RuntimeManifestSha256 = $ExpectedManifestSha256
} | ConvertTo-Json -Compress
