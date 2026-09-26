#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("osx-arm64", "win-x64")][string]$Rid,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$SourceReceiptPath,
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$CMakePath,
    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"),
    [switch]$EnableVulkan,
    [ValidateRange(1, 64)][int]$Parallelism = 4,
    [string]$WindowsGenerator = "Visual Studio 17 2022",
    [string]$WindowsCCompiler,
    [string]$WindowsCxxCompiler,
    [string]$WindowsMakeProgram,
    [string]$DumpbinPath,
    [string]$MacCCompiler = "/usr/bin/clang",
    [string]$MacCxxCompiler = "/usr/bin/clang++"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
Import-Module (Join-Path $PSScriptRoot "WhisperSupplyChain.psm1") -Force

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.macos
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.windows
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

function Assert-SafeArtifactRoot
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar), $root.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Label cannot be a filesystem root."
    }
    $fullPath
}

function Test-PathOverlap
{
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right)

    $leftFull = [IO.Path]::GetFullPath($Left).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rightFull = [IO.Path]::GetFullPath($Right).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ([string]::Equals($leftFull, $rightFull, $comparison)) { return $true }
    $leftPrefix = $leftFull + [IO.Path]::DirectorySeparatorChar
    $rightPrefix = $rightFull + [IO.Path]::DirectorySeparatorChar
    $leftFull.StartsWith($rightPrefix, $comparison) -or $rightFull.StartsWith($leftPrefix, $comparison)
}

function Invoke-CMake
{
    param([Parameter(Mandatory)][string[]]$Arguments)

    $originalPath = $env:PATH
    $originalCCompiler = $env:CC
    $originalCxxCompiler = $env:CXX
    try
    {
        if ($IsWindows -and $WindowsGenerator -ceq "Ninja" -and
            -not [string]::IsNullOrWhiteSpace($WindowsMakeProgram))
        {
            # ggml's Vulkan shader generator is a nested CMake ExternalProject.
            # It selects its tools by name and must discover the already-validated
            # compiler and make program through the child process environment.
            $toolDirectories = @(
                (Split-Path -Parent $WindowsMakeProgram),
                (Split-Path -Parent $WindowsCCompiler),
                (Split-Path -Parent $WindowsCxxCompiler)) | Select-Object -Unique
            $env:PATH = ($toolDirectories -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $originalPath
            $env:CC = $WindowsCCompiler
            $env:CXX = $WindowsCxxCompiler
        }

        & $script:CMakeExecutable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "CMake failed with exit code $LASTEXITCODE." }
    }
    finally
    {
        $env:PATH = $originalPath
        $env:CC = $originalCCompiler
        $env:CXX = $originalCxxCompiler
    }
}

function Test-IsVisualStudioGenerator
{
    $WindowsGenerator.StartsWith("Visual Studio ", [StringComparison]::Ordinal)
}

function Get-CMakeCacheValue
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$CacheLines,
        [Parameter(Mandatory)][string]$Name,
        [switch]$Required)

    $matches = @($CacheLines | Where-Object { $_ -match "^$([Regex]::Escape($Name)):[^=]+=" })
    if ($matches.Count -eq 0)
    {
        if ($Required) { throw "CMake cache entry '$Name' is missing." }
        return $null
    }
    if ($matches.Count -ne 1) { throw "CMake cache entry '$Name' is duplicated." }
    $matches[0].Substring($matches[0].IndexOf('=') + 1)
}

function Get-CompilerEvidence
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$CacheLines,
        [Parameter(Mandatory)][string]$BuildDirectory,
        [Parameter(Mandatory)][ValidateSet("C", "CXX")][string]$Language)

    $compilerMetadataFiles = @(Get-ChildItem -LiteralPath (Join-Path $BuildDirectory "CMakeFiles") `
        -Recurse -Filter "CMake${Language}Compiler.cmake" -File)
    if ($compilerMetadataFiles.Count -ne 1) { throw "CMake $Language compiler metadata is missing or duplicated." }
    $compilerMetadata = Get-Content -LiteralPath $compilerMetadataFiles[0].FullName -Raw
    $pathMatch = [Regex]::Match($compilerMetadata, "set\(CMAKE_${Language}_COMPILER `"([^`"]+)`"\)")
    $idMatch = [Regex]::Match($compilerMetadata, "set\(CMAKE_${Language}_COMPILER_ID `"([^`"]+)`"\)")
    $versionMatch = [Regex]::Match($compilerMetadata, "set\(CMAKE_${Language}_COMPILER_VERSION `"([^`"]+)`"\)")
    if (-not $pathMatch.Success -or -not $idMatch.Success -or -not $versionMatch.Success)
    {
        throw "CMake $Language compiler identity is incomplete."
    }
    $compilerDeclaration = $pathMatch.Groups[1].Value
    $compilerPath = if ([IO.Path]::IsPathRooted($compilerDeclaration))
    {
        [IO.Path]::GetFullPath($compilerDeclaration)
    }
    else
    {
        $compilerDeclaration
    }
    $compilerHash = if ([IO.Path]::IsPathRooted($compilerPath) -and (Test-Path -LiteralPath $compilerPath -PathType Leaf))
    {
        Get-LowerSha256 -Path $compilerPath
    }
    else
    {
        $null
    }

    [ordered]@{
        language = $Language
        fileName = [IO.Path]::GetFileName($compilerPath)
        path = $compilerPath
        sha256 = $compilerHash
        id = $idMatch.Groups[1].Value
        version = $versionMatch.Groups[1].Value
        flags = Get-CMakeCacheValue -CacheLines $CacheLines -Name "CMAKE_${Language}_FLAGS"
        releaseFlags = Get-CMakeCacheValue -CacheLines $CacheLines -Name "CMAKE_${Language}_FLAGS_RELEASE"
    }
}

function New-PatchedWhisperSourceTree
{
    param([Parameter(Mandatory)][string]$Destination)

    if (Test-Path -LiteralPath $Destination)
    {
        throw "Patched source destination '$Destination' already exists."
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($entry in @(Get-ChildItem -LiteralPath $script:SourceRootFull -Force))
    {
        Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse
    }

    $copiedTree = Get-WhisperSourceTreeEvidence -SourceRoot $Destination
    if ([string]$copiedTree.treeSha256 -cne [string]$script:SourceTree.treeSha256)
    {
        throw "The isolated upstream source copy differs before patching."
    }

    $patchEntry = @($script:Manifest.source.patches)[0]
    $patchPath = Resolve-ManifestOwnedPath `
        -ManifestPath $script:ManifestEvidence.Path `
        -RelativePath ([string]$patchEntry.checkedInPath)
    if ((Get-LowerSha256 -Path $patchPath) -cne [string]$patchEntry.sha256)
    {
        throw "The pinned app-owned source patch differs from the manifest."
    }

    $targetPath = Join-Path $Destination "src/whisper.cpp"
    $sourceBytes = [IO.File]::ReadAllBytes($targetPath)
    $patchBytes = [IO.File]::ReadAllBytes($patchPath)
    $separatorLength = if ($sourceBytes.Length -gt 0 -and $sourceBytes[-1] -eq 10) { 0 } else { 1 }
    $patchedBytes = [byte[]]::new($sourceBytes.Length + $separatorLength + $patchBytes.Length)
    [Buffer]::BlockCopy($sourceBytes, 0, $patchedBytes, 0, $sourceBytes.Length)
    if ($separatorLength -eq 1) { $patchedBytes[$sourceBytes.Length] = 10 }
    [Buffer]::BlockCopy(
        $patchBytes,
        0,
        $patchedBytes,
        $sourceBytes.Length + $separatorLength,
        $patchBytes.Length)
    [IO.File]::WriteAllBytes($targetPath, $patchedBytes)

    [pscustomobject]@{
        Root = [IO.Path]::GetFullPath($Destination)
        Tree = Get-WhisperSourceTreeEvidence -SourceRoot $Destination
        Patch = [ordered]@{
            id = [string]$patchEntry.id
            apply = [string]$patchEntry.apply
            checkedInPath = [string]$patchEntry.checkedInPath
            sha256 = [string]$patchEntry.sha256
        }
    }
}

function Build-Variant
{
    param([Parameter(Mandatory)][string]$Variant)

    $variantEntry = Get-WhisperRidVariant -Manifest $script:Manifest -Rid $Rid -Variant $Variant
    $buildDirectory = Join-Path $script:BuildRootFull "$Rid-$Variant"
    $rawOutputDirectory = Join-Path $buildDirectory "native-output"
    $payloadDirectory = Join-Path (Join-Path $script:OutputRootFull $Rid) $Variant
    if (Test-Path -LiteralPath $buildDirectory) { throw "Build directory '$buildDirectory' already exists; use a fresh scoped BuildRoot." }
    if (Test-Path -LiteralPath $payloadDirectory) { throw "Payload directory '$payloadDirectory' already exists; use a fresh scoped OutputRoot." }
    [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($rawOutputDirectory) | Out-Null
    $patchedSource = New-PatchedWhisperSourceTree `
        -Destination (Join-Path $script:BuildRootFull "$Rid-$Variant-patched-source")

    $configureArguments = [Collections.Generic.List[string]]::new()
    $configureArguments.Add("-S")
    $configureArguments.Add($script:BridgeRoot)
    $configureArguments.Add("-B")
    $configureArguments.Add($buildDirectory)
    if ($Rid -ceq "win-x64")
    {
        $configureArguments.Add("-G")
        $configureArguments.Add($WindowsGenerator)
        if (Test-IsVisualStudioGenerator)
        {
            $configureArguments.Add("-A")
            $configureArguments.Add("x64")
        }
        else
        {
            $configureArguments.Add("-DCMAKE_BUILD_TYPE=Release")
            $configureArguments.Add("-DCMAKE_C_COMPILER=$WindowsCCompiler")
            $configureArguments.Add("-DCMAKE_CXX_COMPILER=$WindowsCxxCompiler")
            $configureArguments.Add("-DCMAKE_MAKE_PROGRAM=$WindowsMakeProgram")
        }
    }

    foreach ($property in @($variantEntry.cmakeCache.PSObject.Properties))
    {
        $configureArguments.Add("-D$($property.Name)=$([string]$property.Value)")
    }
    $configureArguments.Add("-DITW_WHISPER_SOURCE_DIR=$($patchedSource.Root)")
    $configureArguments.Add("-DITW_OUTPUT_DIRECTORY=$rawOutputDirectory")
    $configureArguments.Add("-DCMAKE_EXPORT_COMPILE_COMMANDS=ON")
    if ($Rid -ceq "osx-arm64")
    {
        $configureArguments.Add("-DCMAKE_C_COMPILER=$MacCCompiler")
        $configureArguments.Add("-DCMAKE_CXX_COMPILER=$MacCxxCompiler")
    }

    Invoke-CMake -Arguments $configureArguments.ToArray()

    $cachePath = Join-Path $buildDirectory "CMakeCache.txt"
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) { throw "CMake did not create a cache." }
    $cacheLines = @(Get-Content -LiteralPath $cachePath)
    foreach ($property in @($variantEntry.cmakeCache.PSObject.Properties))
    {
        $actual = Get-CMakeCacheValue -CacheLines $cacheLines -Name $property.Name -Required
        if ([string]$actual -cne [string]$property.Value)
        {
            throw "CMake cache '$($property.Name)' is '$actual', expected '$($property.Value)'."
        }
    }
    foreach ($disabledOption in @($script:Manifest.buildPolicy.forcedDisabledOptions | ForEach-Object { [string]$_ }))
    {
        $actual = Get-CMakeCacheValue -CacheLines $cacheLines -Name $disabledOption -Required
        if ([string]$actual -cne "OFF") { throw "Forbidden native option '$disabledOption' is enabled." }
    }

    $buildArguments = @(
        "--build", $buildDirectory,
        "--config", "Release",
        "--target", "istranscribe_whisper_v1",
        "--parallel", [string]$Parallelism)
    Invoke-CMake -Arguments $buildArguments

    $expectedOutput = Join-Path $rawOutputDirectory ([string]$variantEntry.outputFile)
    if (-not (Test-Path -LiteralPath $expectedOutput -PathType Leaf))
    {
        throw "Native build did not produce '$($variantEntry.outputFile)'."
    }

    $compilerEvidence = @(
        Get-CompilerEvidence -CacheLines $cacheLines -BuildDirectory $buildDirectory -Language C
        Get-CompilerEvidence -CacheLines $cacheLines -BuildDirectory $buildDirectory -Language CXX)
    $sanitizedConfigureArguments = @(
        "-S", "<native/whisper>", "-B", "<build>"
        if ($Rid -ceq "win-x64")
        {
            "-G"
            $WindowsGenerator
            if (Test-IsVisualStudioGenerator)
            {
                "-A"
                "x64"
            }
            else
            {
                "-DCMAKE_BUILD_TYPE=Release"
                "-DCMAKE_C_COMPILER=$WindowsCCompiler"
                "-DCMAKE_CXX_COMPILER=$WindowsCxxCompiler"
                "-DCMAKE_MAKE_PROGRAM=$WindowsMakeProgram"
            }
        }
        foreach ($property in @($variantEntry.cmakeCache.PSObject.Properties))
        {
            "-D$($property.Name)=$([string]$property.Value)"
        }
        "-DITW_WHISPER_SOURCE_DIR=<verified-patched-source>"
        "-DITW_OUTPUT_DIRECTORY=<build-output>"
        "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"
        if ($Rid -ceq "osx-arm64")
        {
            "-DCMAKE_C_COMPILER=$MacCCompiler"
            "-DCMAKE_CXX_COMPILER=$MacCxxCompiler"
        })
    $sanitizedBuildArguments = @(
        "--build", "<build>", "--config", "Release", "--target", "istranscribe_whisper_v1", "--parallel", [string]$Parallelism)

    $receiptDirectory = Join-Path $script:OutputRootFull "receipts"
    $inventoryDirectory = Join-Path $script:OutputRootFull "inventory"
    $receiptPath = Join-Path $receiptDirectory "$Rid-$Variant.build.v1.json"
    $inventoryPath = Join-Path $inventoryDirectory "$Rid-$Variant.inventory.v1.json"
    $receipt = [ordered]@{
        schemaVersion = "istranscribe-whisper-build-v1"
        spec = "spec://modules/app/FEAT-016-local-whisper-transcription#verification"
        manifestSha256 = [string]$script:ManifestEvidence.Sha256
        sourceCommit = [string]$script:Manifest.source.commit
        sourceArchiveSha256 = [string]$script:Manifest.source.archive.sha256
        sourceTreeSha256 = [string]$script:SourceTree.treeSha256
        sourcePatches = @($patchedSource.Patch)
        patchedSourceTreeSha256 = [string]$patchedSource.Tree.treeSha256
        bridgeTreeSha256 = [string]$script:BridgeTree.treeSha256
        toolingTreeSha256 = [string]$script:ToolingTree.treeSha256
        rid = $Rid
        variant = $Variant
        configuration = "Release"
        cmake = [ordered]@{
            path = $script:CMakeExecutable
            sha256 = Get-LowerSha256 -Path $script:CMakeExecutable
            version = $script:CMakeVersion
        }
        compiler = $compilerEvidence
        configureArguments = $sanitizedConfigureArguments
        buildArguments = $sanitizedBuildArguments
        cmakeCache = [ordered]@{
            CMAKE_C_FLAGS = Get-CMakeCacheValue -CacheLines $cacheLines -Name "CMAKE_C_FLAGS"
            CMAKE_C_FLAGS_RELEASE = Get-CMakeCacheValue -CacheLines $cacheLines -Name "CMAKE_C_FLAGS_RELEASE"
            CMAKE_CXX_FLAGS = Get-CMakeCacheValue -CacheLines $cacheLines -Name "CMAKE_CXX_FLAGS"
            CMAKE_CXX_FLAGS_RELEASE = Get-CMakeCacheValue -CacheLines $cacheLines -Name "CMAKE_CXX_FLAGS_RELEASE"
        }
        builtOutputSha256 = Get-LowerSha256 -Path $expectedOutput
    }
    Write-DeterministicJson -Path $receiptPath -Value $receipt

    [IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
    $stagedOutput = Join-Path $payloadDirectory ([string]$variantEntry.outputFile)
    [IO.File]::Copy($expectedOutput, $stagedOutput, $false)
    if ((Get-LowerSha256 -Path $stagedOutput) -cne [string]$receipt.builtOutputSha256)
    {
        throw "Staged native payload differs from the inspected build output."
    }

    $inspectArguments = @{
        Rid = $Rid
        Variant = $Variant
        PayloadRoot = $payloadDirectory
        InventoryPath = $inventoryPath
        SourceReceiptPath = $SourceReceiptPath
        BuildReceiptPath = $receiptPath
        PatchedSourceRoot = $patchedSource.Root
        ManifestPath = $ManifestPath
    }
    if ($Rid -ceq "win-x64") { $inspectArguments.DumpbinPath = $DumpbinPath }
    & (Join-Path $PSScriptRoot "Inspect-WhisperNative.ps1") @inspectArguments
}

$manifestEvidence = Read-WhisperRuntimeManifest -ManifestPath $ManifestPath
$manifest = $manifestEvidence.Document
$script:ManifestEvidence = $manifestEvidence
$script:Manifest = $manifest
$script:SourceRootFull = [IO.Path]::GetFullPath($SourceRoot)
$script:BuildRootFull = Assert-SafeArtifactRoot -Path $BuildRoot -Label "BuildRoot"
$script:OutputRootFull = Assert-SafeArtifactRoot -Path $OutputRoot -Label "OutputRoot"
$script:BridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\native\whisper"))
$script:ToolingRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$script:CMakeExecutable = [IO.Path]::GetFullPath($CMakePath)

$ownedRoots = [ordered]@{
    SourceRoot = $script:SourceRootFull
    BridgeRoot = $script:BridgeRoot
    ToolingRoot = $script:ToolingRoot
    BuildRoot = $script:BuildRootFull
    OutputRoot = $script:OutputRootFull
}
$rootNames = @($ownedRoots.Keys)
for ($leftIndex = 0; $leftIndex -lt $rootNames.Count; $leftIndex++)
{
    for ($rightIndex = $leftIndex + 1; $rightIndex -lt $rootNames.Count; $rightIndex++)
    {
        $leftName = [string]$rootNames[$leftIndex]
        $rightName = [string]$rootNames[$rightIndex]
        if (Test-PathOverlap -Left $ownedRoots[$leftName] -Right $ownedRoots[$rightName])
        {
            throw "$leftName and $rightName must not be equal, ancestors, or descendants."
        }
    }
}

if (-not (Test-Path -LiteralPath $script:CMakeExecutable -PathType Leaf)) { throw "CMakePath must identify an existing absolute executable." }
if ($Rid -ceq "osx-arm64")
{
    if (-not $IsMacOS -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64)
    {
        throw "osx-arm64 native build requires macOS arm64."
    }
    foreach ($compiler in @($MacCCompiler, $MacCxxCompiler))
    {
        if (-not [IO.Path]::IsPathRooted($compiler) -or -not (Test-Path -LiteralPath $compiler -PathType Leaf))
        {
            throw "macOS compiler '$compiler' must be an existing absolute file."
        }
    }
}
else
{
    if (-not $IsWindows) { throw "win-x64 native build requires Windows." }
    if (-not (Test-IsVisualStudioGenerator))
    {
        if ($WindowsGenerator -cne "Ninja")
        {
            throw "WindowsGenerator must be a Visual Studio generator or exact 'Ninja'."
        }
        foreach ($tool in @(
            [pscustomobject]@{ Name = "WindowsCCompiler"; Path = $WindowsCCompiler },
            [pscustomobject]@{ Name = "WindowsCxxCompiler"; Path = $WindowsCxxCompiler },
            [pscustomobject]@{ Name = "WindowsMakeProgram"; Path = $WindowsMakeProgram }))
        {
            if ([string]::IsNullOrWhiteSpace($tool.Path) -or
                -not [IO.Path]::IsPathRooted($tool.Path) -or
                -not (Test-Path -LiteralPath $tool.Path -PathType Leaf))
            {
                throw "$($tool.Name) must identify an existing absolute file for Ninja builds."
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($DumpbinPath) -or -not [IO.Path]::IsPathRooted($DumpbinPath) -or
        -not (Test-Path -LiteralPath $DumpbinPath -PathType Leaf))
    {
        throw "Windows build requires an absolute DumpbinPath for fail-closed inspection."
    }
}

$versionOutput = @(& $script:CMakeExecutable --version 2>&1 | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0 -or $versionOutput.Count -eq 0 -or $versionOutput[0] -notmatch '^cmake version (\d+\.\d+\.\d+)')
{
    throw "CMake version could not be verified."
}
$script:CMakeVersion = $Matches[1]
if ([Version]$script:CMakeVersion -lt [Version][string]$manifest.buildPolicy.cmakeMinimumVersion)
{
    throw "CMake $($script:CMakeVersion) is older than the manifest minimum."
}

$script:SourceTree = Assert-WhisperSourceTree -SourceRoot $script:SourceRootFull -ReceiptPath $SourceReceiptPath -Manifest $manifest -ManifestSha256 $manifestEvidence.Sha256
$script:BridgeTree = Get-WhisperSourceTreeEvidence -SourceRoot $script:BridgeRoot
$script:ToolingTree = Get-WhisperSourceTreeEvidence -SourceRoot $script:ToolingRoot
[IO.Directory]::CreateDirectory($script:BuildRootFull) | Out-Null
[IO.Directory]::CreateDirectory($script:OutputRootFull) | Out-Null

if ($Rid -ceq "osx-arm64")
{
    Build-Variant -Variant "metal"
}
else
{
    Build-Variant -Variant "cpu"
    if ($EnableVulkan) { Build-Variant -Variant "vulkan" }
}
