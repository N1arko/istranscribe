#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("osx-arm64", "win-x64")][string]$Rid,
    [Parameter(Mandatory)][string]$ApplicationPublishRoot,
    [Parameter(Mandatory)][string]$WorkerPublishRoot,
    [Parameter(Mandatory)][string]$NativeOutputRoot,
    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"),
    [string]$ReceiptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

$ExpectedManifestSha256 = "fe37a0d085edaab7925c25f8e62514fe714dda4c66cfe3f2c48e2bea191d2feb"
$WorkerBaseName = "IsTranscribe.Transcription.Worker"

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ExistingDirectory
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ([string]::Equals($fullPath, $volumeRoot, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Label cannot be a filesystem root."
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container))
    {
        throw "$Label '$fullPath' is missing."
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($null -ne $item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "$Label cannot be a link or reparse point."
    }
    $fullPath
}

function Test-PathOverlap
{
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ([string]::Equals($Left, $Right, $comparison)) { return $true }
    $leftPrefix = $Left + [IO.Path]::DirectorySeparatorChar
    $rightPrefix = $Right + [IO.Path]::DirectorySeparatorChar
    $Left.StartsWith($rightPrefix, $comparison) -or $Right.StartsWith($leftPrefix, $comparison)
}

function Assert-SafePayloadFile
{
    param([Parameter(Mandatory)][IO.FileInfo]$File, [Parameter(Mandatory)][string]$Root)

    if ($null -ne $File.LinkType -or ($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Payload file '$($File.FullName)' cannot be a link or reparse point."
    }
    $relativePath = [IO.Path]::GetRelativePath($Root, $File.FullName)
    if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath -eq ".." -or $relativePath.StartsWith(
        "..$([IO.Path]::DirectorySeparatorChar)",
        [StringComparison]::Ordinal))
    {
        throw "Payload file '$($File.FullName)' escapes its declared root."
    }
    $relativePath
}

function Copy-VerifiedFile
{
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$RelativePath)

    $sourceHash = Get-LowerSha256 -Path $Source
    $sourceLength = (Get-Item -LiteralPath $Source).Length
    if (Test-Path -LiteralPath $Destination)
    {
        if (-not (Test-Path -LiteralPath $Destination -PathType Leaf))
        {
            throw "Publish collision '$RelativePath' is not a regular file."
        }
        $destinationItem = Get-Item -LiteralPath $Destination -Force
        if ($null -ne $destinationItem.LinkType -or
            ($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Publish collision '$RelativePath' is a link or reparse point."
        }
        if ($destinationItem.Length -ne $sourceLength -or
            (Get-LowerSha256 -Path $Destination) -cne $sourceHash)
        {
            throw "publish_collision_mismatch: '$RelativePath' differs between the application and worker payloads."
        }
        return [pscustomobject]@{ Copied = $false; Sha256 = $sourceHash; SizeBytes = $sourceLength }
    }

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Destination)) | Out-Null
    [IO.File]::Copy($Source, $Destination, $false)
    if (-not $IsWindows)
    {
        [IO.File]::SetUnixFileMode($Destination, [IO.File]::GetUnixFileMode($Source))
    }
    if ((Get-LowerSha256 -Path $Destination) -cne $sourceHash)
    {
        throw "Copied payload '$RelativePath' differs from its source."
    }
    [pscustomobject]@{ Copied = $true; Sha256 = $sourceHash; SizeBytes = $sourceLength }
}

function Write-DeterministicJson
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object]$Value)

    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $text = (($Value | ConvertTo-Json -Depth 16) -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd("`n") + "`n"
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$applicationRoot = Get-ExistingDirectory -Path $ApplicationPublishRoot -Label "Application publish root"
$workerRoot = Get-ExistingDirectory -Path $WorkerPublishRoot -Label "Worker publish root"
$nativeRoot = Get-ExistingDirectory -Path $NativeOutputRoot -Label "Native output root"
foreach ($pair in @(
    @($applicationRoot, $workerRoot),
    @($applicationRoot, $nativeRoot),
    @($workerRoot, $nativeRoot)))
{
    if (Test-PathOverlap -Left $pair[0] -Right $pair[1])
    {
        throw "Application, worker and native roots must be separate non-overlapping directories."
    }
}

$manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf) -or
    (Get-LowerSha256 -Path $manifestFullPath) -cne $ExpectedManifestSha256)
{
    throw "The pinned Whisper runtime manifest is missing or changed."
}
$manifest = Get-Content -LiteralPath $manifestFullPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([string]$manifest.schemaVersion -cne "istranscribe-whisper-native-v1")
{
    throw "The Whisper runtime manifest schema is unsupported."
}
$ridEntries = @($manifest.rids | Where-Object rid -CEQ $Rid)
if ($ridEntries.Count -ne 1)
{
    throw "The Whisper runtime manifest must contain one exact '$Rid' entry."
}
$ridEntry = $ridEntries[0]
$requiredVariant = if ($Rid -ceq "win-x64") { "cpu" } else { "metal" }
$variantNames = [Collections.Generic.List[string]]::new()
$variantNames.Add($requiredVariant)
if ($Rid -ceq "win-x64")
{
    $optionalPayload = Join-Path (Join-Path $nativeRoot $Rid) "vulkan"
    $optionalInventory = Join-Path (Join-Path $nativeRoot "inventory") "$Rid-vulkan.inventory.v1.json"
    $hasPayload = Test-Path -LiteralPath $optionalPayload -PathType Container
    $hasInventory = Test-Path -LiteralPath $optionalInventory -PathType Leaf
    if ($hasPayload -ne $hasInventory)
    {
        throw "The optional Vulkan payload and inventory must either both exist or both be absent."
    }
    if ($hasPayload) { $variantNames.Add("vulkan") }
}

$workerExecutableName = if ($Rid -ceq "win-x64") { "$WorkerBaseName.exe" } else { $WorkerBaseName }
foreach ($requiredWorkerFile in @(
    $workerExecutableName,
    "$WorkerBaseName.dll",
    "$WorkerBaseName.deps.json",
    "$WorkerBaseName.runtimeconfig.json"))
{
    if (-not (Test-Path -LiteralPath (Join-Path $workerRoot $requiredWorkerFile) -PathType Leaf))
    {
        throw "The self-contained worker publish is missing '$requiredWorkerFile'."
    }
}
$hostFxrPattern = if ($Rid -ceq "win-x64") { "hostfxr.dll" } else { "libhostfxr.dylib" }
if (-not (Test-Path -LiteralPath (Join-Path $workerRoot $hostFxrPattern) -PathType Leaf))
{
    throw "The worker publish is not a self-contained '$Rid' payload."
}

$workerInventory = [Collections.Generic.List[object]]::new()
$copiedWorkerFiles = 0
$sharedWorkerFiles = 0
foreach ($workerFile in @(Get-ChildItem -LiteralPath $workerRoot -Recurse -Force -File |
    Sort-Object { [IO.Path]::GetRelativePath($workerRoot, $_.FullName) }))
{
    $relativePath = Assert-SafePayloadFile -File $workerFile -Root $workerRoot
    if ($relativePath.EndsWith(".pdb", [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Worker debug symbols cannot enter the product payload."
    }
    $copy = Copy-VerifiedFile `
        -Source $workerFile.FullName `
        -Destination (Join-Path $applicationRoot $relativePath) `
        -RelativePath $relativePath
    if ($copy.Copied) { $copiedWorkerFiles++ } else { $sharedWorkerFiles++ }
    $workerInventory.Add([ordered]@{
        path = $relativePath.Replace('\', '/')
        sizeBytes = [long]$copy.SizeBytes
        sha256 = [string]$copy.Sha256
        disposition = if ($copy.Copied) { "worker" } else { "shared-byte-identical" }
    })
}

$nativeDirectory = Join-Path $applicationRoot "native"
[IO.Directory]::CreateDirectory($nativeDirectory) | Out-Null
$nativeInventory = [Collections.Generic.List[object]]::new()
foreach ($variantName in $variantNames)
{
    $variantEntries = @($ridEntry.variants | Where-Object id -CEQ $variantName)
    if ($variantEntries.Count -ne 1)
    {
        throw "The manifest must contain one exact '$Rid/$variantName' variant."
    }
    $variantEntry = $variantEntries[0]
    $payloadDirectory = Join-Path (Join-Path $nativeRoot $Rid) $variantName
    $inventoryPath = Join-Path (Join-Path $nativeRoot "inventory") "$Rid-$variantName.inventory.v1.json"
    if (-not (Test-Path -LiteralPath $payloadDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $inventoryPath -PathType Leaf))
    {
        throw "The inspected '$Rid/$variantName' native payload is incomplete."
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -Force -File)
    $nestedEntries = @(Get-ChildItem -LiteralPath $payloadDirectory -Force -Recurse |
        Where-Object FullName -NE $payloadFiles[0].FullName)
    $expectedOutputName = [string]$variantEntry.outputFile
    if ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Name -cne $expectedOutputName -or
        $nestedEntries.Count -ne 0)
    {
        throw "The inspected '$Rid/$variantName' native payload has unexpected contents."
    }
    [void](Assert-SafePayloadFile -File $payloadFiles[0] -Root $payloadDirectory)
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ([string]$inventory.schemaVersion -cne "istranscribe-whisper-native-inventory-v1" -or
        [string]$inventory.manifestSha256 -cne $ExpectedManifestSha256 -or
        [string]$inventory.rid -cne $Rid -or
        [string]$inventory.variant -cne $variantName -or
        [string]$inventory.output.path -cne $expectedOutputName -or
        [string]$inventory.output.sha256 -cne (Get-LowerSha256 -Path $payloadFiles[0].FullName))
    {
        throw "The inspected '$Rid/$variantName' native inventory does not match its payload."
    }
    $expectedArchitectures = @($ridEntry.architectures | ForEach-Object { [string]$_ } | Sort-Object)
    $actualArchitectures = @($inventory.architectures | ForEach-Object { [string]$_ } | Sort-Object)
    if (($expectedArchitectures -join "`n") -cne ($actualArchitectures -join "`n"))
    {
        throw "The inspected '$Rid/$variantName' architecture set differs from the manifest."
    }
    $destination = Join-Path $nativeDirectory $expectedOutputName
    $copy = Copy-VerifiedFile `
        -Source $payloadFiles[0].FullName `
        -Destination $destination `
        -RelativePath "native/$expectedOutputName"
    $nativeInventory.Add([ordered]@{
        variant = $variantName
        required = [bool]$variantEntry.required
        path = "native/$expectedOutputName"
        sizeBytes = [long]$copy.SizeBytes
        sha256 = [string]$copy.Sha256
        architectures = $actualArchitectures
        directDependencies = @($inventory.output.directDependencies | ForEach-Object { [string]$_ })
    })
}

$licenseInventory = [Collections.Generic.List[object]]::new()
foreach ($license in @($manifest.licenses))
{
    $licensePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $manifestFullPath) ([string]$license.checkedInPath)))
    $manifestRoot = [IO.Path]::GetFullPath((Split-Path -Parent $manifestFullPath)) + [IO.Path]::DirectorySeparatorChar
    if (-not $licensePath.StartsWith($manifestRoot, [StringComparison]::Ordinal) -or
        -not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or
        (Get-LowerSha256 -Path $licensePath) -cne [string]$license.sha256)
    {
        throw "The native license evidence for '$($license.component)' is missing or changed."
    }
    $licenseInventory.Add([ordered]@{
        component = [string]$license.component
        spdx = [string]$license.spdx
        checkedInPath = [string]$license.checkedInPath
        sha256 = [string]$license.sha256
    })
}

$packagedManifestPath = Join-Path $nativeDirectory "runtime-manifest.v1.json"
[void](Copy-VerifiedFile `
    -Source $manifestFullPath `
    -Destination $packagedManifestPath `
    -RelativePath "native/runtime-manifest.v1.json")
if ([string]::IsNullOrWhiteSpace($ReceiptPath))
{
    $ReceiptPath = Join-Path $nativeDirectory "runtime-inventory.v1.json"
}
$receiptFullPath = [IO.Path]::GetFullPath($ReceiptPath)
$applicationPrefix = $applicationRoot + [IO.Path]::DirectorySeparatorChar
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $receiptFullPath.StartsWith($applicationPrefix, $pathComparison) -or
    [string]::Equals($receiptFullPath, $packagedManifestPath, $pathComparison))
{
    throw "The local runtime receipt must be a distinct file inside the application publish root."
}
if (Test-Path -LiteralPath $receiptFullPath)
{
    throw "The local runtime receipt destination already exists."
}

# @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
$speakerRoot = Join-Path $nativeRoot "speaker/$Rid"
$speakerReceiptPath = Join-Path $speakerRoot "inventory.json"
if (-not (Test-Path -LiteralPath $speakerReceiptPath -PathType Leaf)) { throw "The reviewed speaker runtime is missing. Run Build-SpeakerNative.ps1." }
$speakerReceipt = Get-Content -LiteralPath $speakerReceiptPath -Raw | ConvertFrom-Json
if ($speakerReceipt.schema -cne "speaker-native/v1" -or $speakerReceipt.rid -cne $Rid -or
    $speakerReceipt.sourceCommit -cne "26aa2fa93210376a89de3a65a1a4dd320c37f5e9" -or
    $speakerReceipt.ttsEnabled -ne $false -or $speakerReceipt.eigenMpl2Only -ne $true) {
    throw "The speaker runtime build profile is unreviewed."
}
$speakerFiles = @()
$requiredSpeakerLibraries = if ($Rid -ceq "osx-arm64") { @("libonnxruntime.1.17.1.dylib", "libsherpa-onnx-c-api.dylib") }
    else { @("onnxruntime.dll", "sherpa-onnx-c-api.dll") }
if ((@($speakerReceipt.libraries | ForEach-Object { [string]$_.path } | Sort-Object) -join "`n") -cne
    (@($requiredSpeakerLibraries | Sort-Object) -join "`n") -or @($speakerReceipt.licenses).Count -ne 14) {
    throw "The reviewed speaker runtime file or license set is incomplete."
}
foreach ($entry in @($speakerReceipt.libraries) + @($speakerReceipt.licenses)) {
    $relative = [string]$entry.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains("..")) { throw "Unsafe speaker asset path." }
    $source = Join-Path $speakerRoot $relative
    if ((Get-LowerSha256 -Path $source) -cne [string]$entry.sha256) { throw "Speaker asset integrity mismatch." }
    $target = if ($relative.StartsWith("licenses/", [StringComparison]::Ordinal)) { "speaker-$relative" } else { $relative }
    $copy = Copy-VerifiedFile -Source $source -Destination (Join-Path $applicationRoot $target) -RelativePath $target
    $speakerFiles += [ordered]@{ path = $target; sha256 = $copy.Sha256; sizeBytes = $copy.SizeBytes }
}
$sourceOffer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../native/speaker/SOURCE-AND-NOTICES.md"))
$copy = Copy-VerifiedFile -Source $sourceOffer -Destination (Join-Path $applicationRoot "speaker-licenses/SOURCE-AND-NOTICES.md") -RelativePath "speaker-licenses/SOURCE-AND-NOTICES.md"
$speakerFiles += [ordered]@{ path = "speaker-licenses/SOURCE-AND-NOTICES.md"; sha256 = $copy.Sha256; sizeBytes = $copy.SizeBytes }

$receipt = [ordered]@{
    schemaVersion = "istranscribe-local-runtime-package-v1"
    spec = "spec://modules/app/FEAT-016-local-whisper-transcription#verification"
    rid = $Rid
    speaker = [ordered]@{ runtimeVersion = "sherpa-onnx/1.12.14"; ttsEnabled = $false; eigenMpl2Only = $true; files = $speakerFiles }
    worker = [ordered]@{
        executable = $workerExecutableName
        selfContained = $true
        copiedFileCount = $copiedWorkerFiles
        sharedByteIdenticalFileCount = $sharedWorkerFiles
        files = @($workerInventory)
    }
    native = [ordered]@{
        directory = "native"
        runtimeManifestPath = "native/runtime-manifest.v1.json"
        runtimeManifestSha256 = $ExpectedManifestSha256
        sourceCommit = [string]$manifest.source.commit
        sourceArchiveSha256 = [string]$manifest.source.archive.sha256
        libraries = @($nativeInventory)
        licenses = @($licenseInventory)
    }
}
Write-DeterministicJson -Path $receiptFullPath -Value $receipt

[pscustomobject]@{
    ReceiptPath = $receiptFullPath
    ReceiptSha256 = Get-LowerSha256 -Path $receiptFullPath
    WorkerExecutablePath = Join-Path $applicationRoot $workerExecutableName
    WorkerFileCount = $workerInventory.Count
    CopiedWorkerFileCount = $copiedWorkerFiles
    SharedWorkerFileCount = $sharedWorkerFiles
    NativeLibraryCount = $nativeInventory.Count
} | ConvertTo-Json -Compress
