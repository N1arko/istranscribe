#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$OutputDirectory,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
$baseVersion = "2.0.0"
$upgradeVersion = "2.0.1"
$kitName = "TEST-ONLY-isTranscribe-installer-acceptance-2.0.0-to-2.0.1-win-x64"
$expectedBaseHash = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B"
$expectedUpgradeHash = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3"
$expectedCertificateHash = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC"
$expectedCertificateThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$expectedHarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$expectedReleaseFiles = @(
    "dependency-license-inventory.json", "DEVELOPMENT_ONLY.txt",
    "isTranscribe-development-certificate.cer", "release-manifest.json", "SHA256SUMS.txt")

function Test-PathInsideOrEqual
{
    param([string]$Path, [string]$ParentPath)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    return [string]::Equals($fullPath, $fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePointInPath
{
    param([string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current))
    {
        if (Test-Path -LiteralPath $current)
        {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            {
                throw "Acceptance-kit paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-ChildPath
{
    param([string]$Path, [string]$ParentPath)
    if (-not (Test-PathInsideOrEqual -Path $Path -ParentPath $ParentPath) -or
        [string]::Equals([IO.Path]::GetFullPath($Path), [IO.Path]::GetFullPath($ParentPath), [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to mutate a path outside the acceptance root: $Path"
    }
}

function Assert-NoReparsePointInTree
{
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0)
    {
        $directory = $pending.Pop()
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($directory))
        {
            $entry = Get-Item -LiteralPath $entryPath -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            {
                throw "Refusing to remove a tree containing a reparse point: $entryPath"
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}

function Remove-SafeDirectory
{
    param([string]$Path, [string]$ParentPath)
    Assert-ChildPath -Path $Path -ParentPath $ParentPath
    Assert-NoReparsePointInPath -Path $Path
    Assert-NoReparsePointInTree -Root $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

function Get-RelativeFiles
{
    param([string]$Root)
    $prefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force
    foreach ($file in $files)
    {
        Assert-NoReparsePointInPath -Path $file.FullName
        [pscustomobject]@{
            RelativePath = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            FullPath = $file.FullName
            Length = $file.Length
        }
    }
}

function Get-StreamHash
{
    param([IO.Stream]$Stream)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace("-", "").ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-Utf8NoBom
{
    param([string]$Path, [string]$Value)
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Join-Path $PSScriptRoot "..\.." }
$repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $repositoryFullPath "specs\BOARD.md") -PathType Leaf))
{
    throw "RepositoryRoot does not identify the isTranscribe repository."
}
$acceptanceRoot = Join-Path $repositoryFullPath "artifacts\acceptance\INFRA-005\windows-x64"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $acceptanceRoot "portable-kit" }
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryFullPath $OutputDirectory }
$outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
Assert-ChildPath -Path $outputFullPath -ParentPath $acceptanceRoot
foreach ($path in @($repositoryFullPath, $acceptanceRoot, $outputFullPath)) { Assert-NoReparsePointInPath -Path $path }
if (Test-Path -LiteralPath $outputFullPath)
{
    if (-not $Force) { throw "Portable acceptance-kit output exists; pass -Force for an intentional exact replacement." }
    $ownedOutputManifest = Join-Path $outputFullPath "acceptance-kit-artifact.json"
    if (-not (Test-Path -LiteralPath $ownedOutputManifest -PathType Leaf) -or
        (Get-Content -LiteralPath $ownedOutputManifest -Raw | ConvertFrom-Json).schemaVersion -cne
            "infra-005-acceptance-kit-artifact-v1")
    {
        throw "Existing output is not an owned INFRA-005 portable-kit directory."
    }
}

$releaseRoot = Join-Path $repositoryFullPath "artifacts\release\windows-x64\development"
$baseRelease = Join-Path $releaseRoot $baseVersion
$upgradeRelease = Join-Path $releaseRoot $upgradeVersion
$toolsRoot = Join-Path $repositoryFullPath "packaging\windows"
if ((Test-PathInsideOrEqual -Path $outputFullPath -ParentPath $releaseRoot) -or
    (Test-PathInsideOrEqual -Path $outputFullPath -ParentPath $toolsRoot))
{
    throw "Acceptance-kit output must not overlap release or tool inputs."
}
foreach ($path in @($baseRelease, $upgradeRelease, $toolsRoot)) { Assert-NoReparsePointInPath -Path $path }

$baseMsixName = "isTranscribe-2.0.0-dev-win-x64.msix"
$upgradeMsixName = "isTranscribe-2.0.1-dev-win-x64.msix"
foreach ($release in @(@($baseRelease, $baseMsixName), @($upgradeRelease, $upgradeMsixName)))
{
    $expected = @($expectedReleaseFiles + $release[1])
    $actual = @(Get-ChildItem -LiteralPath $release[0] -File -Force | ForEach-Object Name)
    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $actual) { [void]$actualSet.Add($name) }
    foreach ($name in $expected) { [void]$expectedSet.Add($name) }
    if (-not $actualSet.SetEquals($expectedSet)) { throw "Source release directory has an unexpected file set: $($release[0])" }
}

$artifactVerifier = Join-Path $toolsRoot "Test-WindowsReleaseArtifact.ps1"
$baseVerification = (& $artifactVerifier -ReleaseDirectory $baseRelease | Select-Object -Last 1) | ConvertFrom-Json
$upgradeVerification = (& $artifactVerifier -ReleaseDirectory $upgradeRelease | Select-Object -Last 1) | ConvertFrom-Json
if ($baseVerification.status -cne "passed" -or $upgradeVerification.status -cne "passed" -or
    $baseVerification.channel -cne "development" -or $upgradeVerification.channel -cne "development" -or
    $baseVerification.msix.sha256 -cne $expectedBaseHash.ToLowerInvariant() -or
    $upgradeVerification.msix.sha256 -cne $expectedUpgradeHash.ToLowerInvariant())
{
    throw "Source release artifact verification failed the pinned acceptance-kit contract."
}
$baseManifest = Get-Content -LiteralPath (Join-Path $baseRelease "release-manifest.json") -Raw | ConvertFrom-Json
$upgradeManifest = Get-Content -LiteralPath (Join-Path $upgradeRelease "release-manifest.json") -Raw | ConvertFrom-Json
if ($baseManifest.signing.mode -cne "development" -or $upgradeManifest.signing.mode -cne "development" -or
    $baseManifest.signing.publicEligible -or $upgradeManifest.signing.publicEligible -or
    $baseManifest.build.sourceRevision -cne "infra-008-local-lifecycle-base" -or
    $upgradeManifest.build.sourceRevision -cne "infra-008-local-lifecycle-upgrade" -or
    $baseManifest.package.identityName -cne "isTranscribe.Desktop" -or
    $upgradeManifest.package.identityName -cne "isTranscribe.Desktop")
{
    throw "Source manifests are not the exact INFRA-008 local-lifecycle development artifacts."
}
$certificatePath = Join-Path $upgradeRelease "isTranscribe-development-certificate.cer"
if ((Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash -cne $expectedCertificateHash)
{
    throw "Development certificate hash mismatch."
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
try
{
    if ($certificate.HasPrivateKey -or $certificate.Thumbprint -cne $expectedCertificateThumbprint -or
        $certificate.Subject -cne "CN=isTranscribe Development")
    {
        throw "Only the pinned public development certificate may enter the acceptance kit."
    }
}
finally { $certificate.Dispose() }
$harnessPath = Join-Path $toolsRoot "Test-WindowsReleaseLifecycle.ps1"
if ((Get-FileHash -LiteralPath $harnessPath -Algorithm SHA256).Hash -cne $expectedHarnessHash)
{
    throw "Lifecycle harness changed; review and update the acceptance-kit pins intentionally."
}

$sourceTemplateRoot = Join-Path $toolsRoot "portable-kit"
$sourceTemplateFiles = @(
    "README-FIRST.txt", "TEST_ONLY_DO_NOT_DISTRIBUTE.txt", "Run-Clean-VM-Preflight.cmd",
    "Run-Clean-VM-Lifecycle.cmd", "Run-Windows-Sandbox.cmd", "Test-AcceptanceKit.ps1",
    "Invoke-AcceptanceKit.ps1", "New-WindowsSandboxConfiguration.ps1",
    "sandbox\Run-In-Sandbox.cmd", "docs\CLEAN-VM-CHECKLIST.md", "docs\COVERAGE.md")
foreach ($relative in $sourceTemplateFiles)
{
    $source = Join-Path $sourceTemplateRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Acceptance-kit source template is missing: $relative" }
    Assert-NoReparsePointInPath -Path $source
}

[void][IO.Directory]::CreateDirectory($acceptanceRoot)
Assert-NoReparsePointInPath -Path $acceptanceRoot
$stagingParent = Join-Path $acceptanceRoot ".kit"
[void][IO.Directory]::CreateDirectory($stagingParent)
Assert-ChildPath -Path $stagingParent -ParentPath $acceptanceRoot
Assert-NoReparsePointInPath -Path $stagingParent
$stagingRoot = Join-Path $stagingParent ([guid]::NewGuid().ToString("N"))
Assert-ChildPath -Path $stagingRoot -ParentPath $stagingParent
[void][IO.Directory]::CreateDirectory($stagingRoot)
Assert-NoReparsePointInPath -Path $stagingRoot
$candidateOutput = Join-Path $stagingRoot "o"
$candidateKit = Join-Path $candidateOutput $kitName
[void][IO.Directory]::CreateDirectory($candidateKit)
foreach ($path in @($candidateOutput, $candidateKit)) { Assert-NoReparsePointInPath -Path $path }
try
{
    foreach ($version in @($baseVersion, $upgradeVersion))
    {
        $sourceRelease = Join-Path $releaseRoot $version
        $destinationRelease = Join-Path $candidateKit "packages\$version"
        [void][IO.Directory]::CreateDirectory($destinationRelease)
        foreach ($file in Get-ChildItem -LiteralPath $sourceRelease -File -Force)
        {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destinationRelease $file.Name)
        }
    }
    foreach ($relative in $sourceTemplateFiles)
    {
        $source = Join-Path $sourceTemplateRoot $relative
        $destinationRelative = if ($relative -in @(
                "Test-AcceptanceKit.ps1", "Invoke-AcceptanceKit.ps1", "New-WindowsSandboxConfiguration.ps1")) {
            "tools\$relative"
        } elseif ($relative -eq "sandbox\Run-In-Sandbox.cmd") {
            "tools\sandbox\Run-In-Sandbox.cmd"
        } else { $relative }
        $destination = Join-Path $candidateKit $destinationRelative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath $source -Destination $destination
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $candidateKit "tools"))
    Copy-Item -LiteralPath $harnessPath -Destination (Join-Path $candidateKit "tools\Test-WindowsReleaseLifecycle.ps1")

    $payloadFiles = @(Get-RelativeFiles -Root $candidateKit)
    $payloadPaths = [string[]]@($payloadFiles.RelativePath)
    [Array]::Sort($payloadPaths, [StringComparer]::Ordinal)
    $fileLedger = foreach ($relativePath in $payloadPaths)
    {
        $file = $payloadFiles | Where-Object RelativePath -CEQ $relativePath
        [ordered]@{
            relativePath = $relativePath
            sizeBytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $kitManifest = [ordered]@{
        schemaVersion = "infra-005-acceptance-kit-v1"
        kitId = "INFRA-005-windows-x64-2.0.0-to-2.0.1"
        createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
        sourceRevision = "infra-008-local-lifecycle"
        classification = [ordered]@{ testOnly = $true; publicEligible = $false; containsPrivateKey = $false }
        platform = [ordered]@{ operatingSystem = "Windows"; architecture = "x64"; minimumWindowsBuild = 19041 }
        identity = [ordered]@{
            name = "isTranscribe.Desktop"; applicationId = "App"; publisher = "CN=isTranscribe Development"
            signerThumbprint = $expectedCertificateThumbprint
        }
        base = [ordered]@{
            relativePath = "packages/2.0.0/$baseMsixName"; semanticVersion = "2.0.0"; msixVersion = "2.0.0.65535"
            sizeBytes = (Get-Item -LiteralPath (Join-Path $baseRelease $baseMsixName)).Length
            sha256 = $expectedBaseHash.ToLowerInvariant()
        }
        upgrade = [ordered]@{
            relativePath = "packages/2.0.1/$upgradeMsixName"; semanticVersion = "2.0.1"; msixVersion = "2.0.1.65535"
            sizeBytes = (Get-Item -LiteralPath (Join-Path $upgradeRelease $upgradeMsixName)).Length
            sha256 = $expectedUpgradeHash.ToLowerInvariant()
        }
        certificate = [ordered]@{
            relativePaths = @(
                "packages/2.0.0/isTranscribe-development-certificate.cer",
                "packages/2.0.1/isTranscribe-development-certificate.cer")
            sha256 = $expectedCertificateHash.ToLowerInvariant(); thumbprint = $expectedCertificateThumbprint
            subject = "CN=isTranscribe Development"; hasPrivateKey = $false
            trustMode = "manual_local_machine_trusted_people"
        }
        harness = [ordered]@{
            relativePath = "tools/Test-WindowsReleaseLifecycle.ps1"
            sha256 = $expectedHarnessHash.ToLowerInvariant(); evidenceSchemaVersion = 2
        }
        entrypoints = [ordered]@{
            cleanVmPreflight = "Run-Clean-VM-Preflight.cmd"
            cleanVmLifecycle = "Run-Clean-VM-Lifecycle.cmd"
            windowsSandbox = "Run-Windows-Sandbox.cmd"
        }
        checksum = [ordered]@{
            algorithm = "SHA-256"; file = "SHA256SUMS.txt"; excludesSelf = $true
            relativePathSeparator = "/"; sort = "ordinal"
        }
        coverage = [ordered]@{
            automated = @("PowerShell package lifecycle", "Start Menu and single instance", "downgrade rejection", "exact cleanup")
            notCovered = @("App Installer UI", "reboot and autostart", "audio/process loopback", "custom folders", "real meetings", "production trust")
        }
        files = @($fileLedger)
    }
    $kitManifestPath = Join-Path $candidateKit "acceptance-kit-manifest.json"
    Write-Utf8NoBom -Path $kitManifestPath -Value ($kitManifest | ConvertTo-Json -Depth 10)

    $checksumFiles = @(Get-RelativeFiles -Root $candidateKit)
    $checksumPaths = [string[]]@($checksumFiles.RelativePath)
    [Array]::Sort($checksumPaths, [StringComparer]::Ordinal)
    $checksumLines = foreach ($relativePath in $checksumPaths)
    {
        $file = $checksumFiles | Where-Object RelativePath -CEQ $relativePath
        "{0}  {1}" -f ((Get-FileHash -LiteralPath $file.FullPath -Algorithm SHA256).Hash.ToLowerInvariant()), $relativePath
    }
    [IO.File]::WriteAllLines((Join-Path $candidateKit "SHA256SUMS.txt"), $checksumLines, [Text.Encoding]::ASCII)

    $verification = & (Join-Path $candidateKit "tools\Test-AcceptanceKit.ps1") -KitDirectory $candidateKit | Out-String | ConvertFrom-Json
    if ($verification.status -cne "passed") { throw "Candidate acceptance kit did not pass its standalone verifier." }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporaryArchive = Join-Path $stagingRoot "$kitName.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $candidateOutput, $temporaryArchive, [IO.Compression.CompressionLevel]::NoCompression, $false)
    $kitFilesForArchive = @(Get-RelativeFiles -Root $candidateKit)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try
    {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($entries.Count -ne $kitFilesForArchive.Count) { throw "Acceptance archive entry count mismatch." }
        $totalLength = [long]0
        $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $entries)
        {
            $prefix = "$kitName/"
            if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or
                -not $seenEntries.Add($entry.FullName))
            {
                throw "Acceptance archive contains an unsafe or duplicate entry."
            }
            $relativePath = $entry.FullName.Substring($prefix.Length)
            if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath.Contains("\") -or
                $relativePath.Contains(":") -or $relativePath.Split('/') -contains "..")
            {
                throw "Acceptance archive entry path is unsafe."
            }
            $sourceFile = $kitFilesForArchive | Where-Object RelativePath -CEQ $relativePath
            if ($null -eq $sourceFile -or $entry.Length -ne $sourceFile.Length) { throw "Acceptance archive entry size/path mismatch." }
            $totalLength += $entry.Length
            if ($totalLength -gt 268435456) { throw "Acceptance archive exceeds the extraction safety limit." }
            $stream = $entry.Open()
            try { $entryHash = Get-StreamHash -Stream $stream } finally { $stream.Dispose() }
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($entryHash -cne $sourceHash) { throw "Acceptance archive entry hash mismatch: $relativePath" }
        }
    }
    finally { $archive.Dispose() }

    $archivePath = Join-Path $candidateOutput "$kitName.zip"
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $candidateOutput "$kitName.zip.sha256"),
        "$archiveHash  $kitName.zip`r`n",
        [Text.Encoding]::ASCII)
    $artifactManifest = [ordered]@{
        schemaVersion = "infra-005-acceptance-kit-artifact-v1"
        status = "prepared"
        createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
        classification = [ordered]@{ testOnly = $true; publicEligible = $false }
        archive = [ordered]@{
            file = "$kitName.zip"; sizeBytes = (Get-Item -LiteralPath $archivePath).Length; sha256 = $archiveHash
            externalDigestFile = "$kitName.zip.sha256"
            authenticityNote = "Verify this digest through the trusted handoff channel before extraction."
        }
        extractedDirectory = $kitName
        kitManifestSha256 = (Get-FileHash -LiteralPath $kitManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceRevision = "infra-008-local-lifecycle"
    }
    Write-Utf8NoBom -Path (Join-Path $candidateOutput "acceptance-kit-artifact.json") -Value ($artifactManifest | ConvertTo-Json -Depth 8)

    $backup = Join-Path $acceptanceRoot (".portable-kit.previous-{0}" -f [guid]::NewGuid().ToString("N"))
    $hadPrevious = Test-Path -LiteralPath $outputFullPath
    if ($hadPrevious)
    {
        if (-not $Force) { throw "Portable acceptance-kit output exists; pass -Force for an intentional exact replacement." }
        $previousManifest = Join-Path $outputFullPath "acceptance-kit-artifact.json"
        if (-not (Test-Path -LiteralPath $previousManifest -PathType Leaf) -or
            (Get-Content -LiteralPath $previousManifest -Raw | ConvertFrom-Json).schemaVersion -cne "infra-005-acceptance-kit-artifact-v1")
        {
            throw "Existing output is not an owned INFRA-005 portable-kit directory."
        }
        Move-Item -LiteralPath $outputFullPath -Destination $backup
    }
    try { Move-Item -LiteralPath $candidateOutput -Destination $outputFullPath }
    catch
    {
        if ($hadPrevious -and -not (Test-Path -LiteralPath $outputFullPath) -and (Test-Path -LiteralPath $backup))
        {
            Move-Item -LiteralPath $backup -Destination $outputFullPath
        }
        throw
    }
    if (Test-Path -LiteralPath $backup) { Remove-SafeDirectory -Path $backup -ParentPath $acceptanceRoot }

    Get-Content -LiteralPath (Join-Path $outputFullPath "acceptance-kit-artifact.json") -Raw
}
finally
{
    if (Test-Path -LiteralPath $stagingRoot) { Remove-SafeDirectory -Path $stagingRoot -ParentPath $stagingParent }
    if ((Test-Path -LiteralPath $stagingParent) -and @(Get-ChildItem -LiteralPath $stagingParent -Force).Count -eq 0)
    {
        Remove-Item -LiteralPath $stagingParent -Force
    }
}
