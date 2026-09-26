#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("osx-arm64", "win-x64")][string]$Rid,
    [Parameter(Mandatory)][ValidateSet("metal", "cpu", "vulkan")][string]$Variant,
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][string]$SourceReceiptPath,
    [Parameter(Mandatory)][string]$BuildReceiptPath,
    [Parameter(Mandatory)][string]$PatchedSourceRoot,
    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"),
    [string]$DumpbinPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
Import-Module (Join-Path $PSScriptRoot "WhisperSupplyChain.psm1") -Force

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy

function Assert-ExactOrdinalSet
{
    param(
        [Parameter(Mandatory)][string]$Label,
        [AllowEmptyCollection()][string[]]$Actual,
        [AllowEmptyCollection()][string[]]$Expected)

    $actualSorted = @(Get-OrdinalSortedUnique -Values @($Actual))
    $expectedSorted = @(Get-OrdinalSortedUnique -Values @($Expected))
    if (($actualSorted -join "`n") -cne ($expectedSorted -join "`n"))
    {
        throw "$Label differs from the exact manifest set."
    }
    $actualSorted
}

function Invoke-ExactNativeTool
{
    param(
        [Parameter(Mandatory)][string]$ToolPath,
        [Parameter(Mandatory)][string[]]$Arguments)

    if (-not [IO.Path]::IsPathRooted($ToolPath) -or -not (Test-Path -LiteralPath $ToolPath -PathType Leaf))
    {
        throw "Native inspection tool '$ToolPath' must be an existing absolute file."
    }
    $output = @(& $ToolPath @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw "Native inspection tool '$ToolPath' failed with exit code $LASTEXITCODE." }
    $output
}

$manifestEvidence = Read-WhisperRuntimeManifest -ManifestPath $ManifestPath
$manifest = $manifestEvidence.Document
$variantEntry = Get-WhisperRidVariant -Manifest $manifest -Rid $Rid -Variant $Variant
$payload = [IO.Path]::GetFullPath($PayloadRoot)
if (-not (Test-Path -LiteralPath $payload -PathType Container)) { throw "Payload root '$payload' is missing." }

$expectedOutput = [string]$variantEntry.outputFile
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -Force -File)
if ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Name -cne $expectedOutput -or
    $payloadFiles[0].DirectoryName -cne $payload)
{
    throw "Native product payload must contain only '$expectedOutput' at its root."
}
$payloadDirectories = @(Get-ChildItem -LiteralPath $payload -Recurse -Force -Directory)
if ($payloadDirectories.Count -ne 0) { throw "Nested native product payload directories are forbidden." }
if ($null -ne $payloadFiles[0].LinkType -or ($payloadFiles[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
{
    throw "Native product payload cannot be a link or reparse point."
}

foreach ($pattern in @($manifest.buildPolicy.forbiddenProductPayloadPatterns | ForEach-Object { [string]$_ }))
{
    if ($payloadFiles[0].Name -like $pattern) { throw "Native product payload matches forbidden pattern '$pattern'." }
}

$libraryPath = $payloadFiles[0].FullName
$patchedSource = [IO.Path]::GetFullPath($PatchedSourceRoot)
if (-not (Test-Path -LiteralPath $patchedSource -PathType Container))
{
    throw "Patched source root '$patchedSource' is missing."
}
$architectures = @()
$symbols = @()
$dependencies = @()
$installName = $null

if ($Rid -ceq "osx-arm64")
{
    if (-not $IsMacOS) { throw "osx-arm64 inspection must run on macOS." }
    $architectures = @((Invoke-ExactNativeTool -ToolPath "/usr/bin/lipo" -Arguments @("-archs", $libraryPath)) -join " " -split '\s+' | Where-Object { $_ })
    $rawSymbols = @(Invoke-ExactNativeTool -ToolPath "/usr/bin/nm" -Arguments @("-gUj", $libraryPath))
    $symbols = @($rawSymbols | ForEach-Object {
        $value = $_.Trim()
        if ($value.StartsWith("_istranscribe_", [StringComparison]::Ordinal)) { $value.Substring(1) } else { $value }
    } | Where-Object { $_ })

    $linkLines = @(Invoke-ExactNativeTool -ToolPath "/usr/bin/otool" -Arguments @("-L", $libraryPath))
    $linkEntries = @($linkLines | Select-Object -Skip 1 | ForEach-Object {
        $trimmed = $_.Trim()
        $separator = $trimmed.IndexOf(" (", [StringComparison]::Ordinal)
        if ($separator -gt 0) { $trimmed.Substring(0, $separator) } else { $null }
    } | Where-Object { $_ })
    if ($linkEntries.Count -lt 1 -or [string]$linkEntries[0] -cne "@rpath/$expectedOutput")
    {
        throw "Native macOS install name is not the exact app-owned @rpath identity."
    }
    $installName = [string]$linkEntries[0]
    $dependencies = @($linkEntries | Select-Object -Skip 1)
}
else
{
    if (-not $IsWindows) { throw "win-x64 inspection must run on Windows." }
    if ([string]::IsNullOrWhiteSpace($DumpbinPath)) { throw "DumpbinPath is required for Windows native inspection." }
    $dumpbin = [IO.Path]::GetFullPath($DumpbinPath)

    $headers = @(Invoke-ExactNativeTool -ToolPath $dumpbin -Arguments @("/nologo", "/headers", $libraryPath))
    if (-not ($headers -match '^\s*8664 machine \(x64\)\s*$')) { throw "Native Windows payload is not exact x64 PE." }
    $architectures = @("x64")

    $exports = @(Invoke-ExactNativeTool -ToolPath $dumpbin -Arguments @("/nologo", "/exports", $libraryPath))
    foreach ($line in $exports)
    {
        if ($line -match '^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)\s*$')
        {
            $symbols += $Matches[1]
        }
    }

    $dependents = @(Invoke-ExactNativeTool -ToolPath $dumpbin -Arguments @("/nologo", "/dependents", $libraryPath))
    foreach ($line in $dependents)
    {
        if ($line -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $dependencies += $Matches[1] }
    }
}

$expectedArchitectures = @($manifest.rids | Where-Object { [string]$_.rid -ceq $Rid } | ForEach-Object { $_.architectures } | ForEach-Object { [string]$_ })
$architectures = @(Assert-ExactOrdinalSet -Label "Native architecture set" -Actual $architectures -Expected $expectedArchitectures)
$symbols = @(Assert-ExactOrdinalSet -Label "Native exported symbol set" -Actual $symbols -Expected @($manifest.abi.requiredSymbols | ForEach-Object { [string]$_ }))

if ($Rid -ceq "win-x64")
{
    $dependencies = @($dependencies | ForEach-Object { $_.ToUpperInvariant() })
    $expectedDependencies = @($variantEntry.allowedDependencies | ForEach-Object { ([string]$_).ToUpperInvariant() })
}
else
{
    $expectedDependencies = @($variantEntry.allowedDependencies | ForEach-Object { [string]$_ })
}
$dependencies = @(Assert-ExactOrdinalSet -Label "Native direct dependency set" -Actual $dependencies -Expected $expectedDependencies)

foreach ($receipt in @($SourceReceiptPath, $BuildReceiptPath))
{
    if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) { throw "Required supply-chain receipt '$receipt' is missing." }
}
$sourceReceipt = Get-Content -LiteralPath $SourceReceiptPath -Raw | ConvertFrom-Json
$buildReceipt = Get-Content -LiteralPath $BuildReceiptPath -Raw | ConvertFrom-Json
$patchedSourceTree = Get-WhisperSourceTreeEvidence -SourceRoot $patchedSource
$manifestPatch = @($manifest.source.patches)
$receiptPatch = @($buildReceipt.sourcePatches)
if ([string]$sourceReceipt.schemaVersion -cne "istranscribe-whisper-source-v1" -or
    [string]$sourceReceipt.manifestSha256 -cne [string]$manifestEvidence.Sha256 -or
    [string]$sourceReceipt.commit -cne [string]$manifest.source.commit -or
    [string]$buildReceipt.schemaVersion -cne "istranscribe-whisper-build-v1" -or
    [string]$buildReceipt.manifestSha256 -cne [string]$manifestEvidence.Sha256 -or
    [string]$buildReceipt.sourceCommit -cne [string]$manifest.source.commit -or
    [string]$buildReceipt.sourceArchiveSha256 -cne [string]$manifest.source.archive.sha256 -or
    $manifestPatch.Count -ne 1 -or $receiptPatch.Count -ne 1 -or
    [string]$receiptPatch[0].id -cne [string]$manifestPatch[0].id -or
    [string]$receiptPatch[0].apply -cne [string]$manifestPatch[0].apply -or
    [string]$receiptPatch[0].checkedInPath -cne [string]$manifestPatch[0].checkedInPath -or
    [string]$receiptPatch[0].sha256 -cne [string]$manifestPatch[0].sha256 -or
    [string]$buildReceipt.patchedSourceTreeSha256 -cne [string]$patchedSourceTree.treeSha256 -or
    [string]$buildReceipt.rid -cne $Rid -or [string]$buildReceipt.variant -cne $Variant -or
    [string]$buildReceipt.sourceTreeSha256 -cne [string]$sourceReceipt.tree.treeSha256)
{
    throw "Source/build receipts do not describe this native payload."
}


$binaryText = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($libraryPath))
$privateRoots = @(
    $patchedSource,
    (Split-Path -Parent $patchedSource),
    ([IO.Path]::GetFullPath((Split-Path -Parent $manifestEvidence.Path))),
    ([IO.Path]::GetFullPath($PSScriptRoot)))
foreach ($privateRoot in $privateRoots | Select-Object -Unique)
{
    foreach ($candidate in @(
        $privateRoot,
        $privateRoot.Replace('\', '/'),
        $privateRoot.Replace('/', '\')) | Select-Object -Unique)
    {
        if ($binaryText.Contains($candidate, [StringComparison]::Ordinal))
        {
            throw "Native payload embeds a private source or build root."
        }
    }
}
if ([string]$buildReceipt.builtOutputSha256 -cne (Get-LowerSha256 -Path $libraryPath))
{
    throw "Native payload differs from its build receipt."
}
$bridgeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $manifestEvidence.Path))
$bridgeTree = Get-WhisperSourceTreeEvidence -SourceRoot $bridgeRoot
if ([string]$buildReceipt.bridgeTreeSha256 -cne [string]$bridgeTree.treeSha256)
{
    throw "Native bridge source differs from its build receipt."
}
$toolingTree = Get-WhisperSourceTreeEvidence -SourceRoot ([IO.Path]::GetFullPath($PSScriptRoot))
if ([string]$buildReceipt.toolingTreeSha256 -cne [string]$toolingTree.treeSha256)
{
    throw "Native supply-chain tooling differs from its build receipt."
}

$licenseInventory = @($manifest.licenses | ForEach-Object {
    $licensePath = Resolve-ManifestOwnedPath -ManifestPath $manifestEvidence.Path -RelativePath ([string]$_.checkedInPath)
    [ordered]@{
        component = [string]$_.component
        spdx = [string]$_.spdx
        sourceUri = [string]$_.sourceUri
        sourceCommit = [string]$_.sourceCommit
        checkedInPath = [string]$_.checkedInPath
        sha256 = Get-LowerSha256 -Path $licensePath
    }
})

$inventory = [ordered]@{
    schemaVersion = "istranscribe-whisper-native-inventory-v1"
    spec = "spec://modules/app/FEAT-016-local-whisper-transcription#verification"
    runtimeVersion = [string]$manifest.runtime.version
    sourceCommit = [string]$manifest.source.commit
    sourceArchiveSha256 = [string]$manifest.source.archive.sha256
    sourceTreeSha256 = [string]$sourceReceipt.tree.treeSha256
    sourcePatches = @($receiptPatch)
    patchedSourceTreeSha256 = [string]$patchedSourceTree.treeSha256
    manifestSha256 = [string]$manifestEvidence.Sha256
    bridgeTreeSha256 = [string]$bridgeTree.treeSha256
    toolingTreeSha256 = [string]$toolingTree.treeSha256
    rid = $Rid
    variant = $Variant
    required = [bool]$variantEntry.required
    architectures = $architectures
    compiler = $buildReceipt.compiler
    cmake = $buildReceipt.cmake
    configureArguments = @($buildReceipt.configureArguments)
    buildArguments = @($buildReceipt.buildArguments)
    output = [ordered]@{
        path = $expectedOutput
        size = [long]$payloadFiles[0].Length
        sha256 = Get-LowerSha256 -Path $libraryPath
        installName = $installName
        exportedSymbols = $symbols
        directDependencies = $dependencies
    }
    licenses = $licenseInventory
}
Write-DeterministicJson -Path $InventoryPath -Value $inventory

[pscustomobject]@{
    InventoryPath = [IO.Path]::GetFullPath($InventoryPath)
    LibraryPath = $libraryPath
    Sha256 = $inventory.output.sha256
    Architectures = $architectures
    Dependencies = $dependencies
}
