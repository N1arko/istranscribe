#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"),
    [string]$ArchivePath,
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "WhisperSupplyChain.psm1") -Force

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

function Test-ArchiveEntries
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRoot)

    $archiveStream = [IO.File]::OpenRead($Path)
    try
    {
        $gzipStream = [IO.Compression.GZipStream]::new(
            $archiveStream,
            [IO.Compression.CompressionMode]::Decompress)
        try
        {
            $reader = [System.Formats.Tar.TarReader]::new($gzipStream, $false)
            try
            {
                [int]$entryCount = 0
                while ($null -ne ($entry = $reader.GetNextEntry()))
                {
                    $entryCount++
                    $name = [string]$entry.Name
                    if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::GlobalExtendedAttributes)
                    {
                        if ($name -cne "pax_global_header") { throw "Unexpected global archive metadata '$name'." }
                        continue
                    }
                    if ($entry.EntryType -notin @(
                        [System.Formats.Tar.TarEntryType]::Directory,
                        [System.Formats.Tar.TarEntryType]::RegularFile,
                        [System.Formats.Tar.TarEntryType]::V7RegularFile))
                    {
                        throw "Archive entry '$name' has forbidden type '$($entry.EntryType)'."
                    }
                    if ($name.Contains('\') -or [IO.Path]::IsPathRooted($name) -or
                        $name -match '(^|/)\.\.(/|$)' -or $name -match '(^|/)\.(/|$)' -or
                        ($name -cne $ExpectedRoot -and $name -cne "$ExpectedRoot/" -and
                            -not $name.StartsWith("$ExpectedRoot/", [StringComparison]::Ordinal)))
                    {
                        throw "Archive entry '$name' escapes the pinned source root."
                    }
                }
                if ($entryCount -lt 2) { throw "Pinned source archive is empty." }
            }
            finally { $reader.Dispose() }
        }
        finally { $gzipStream.Dispose() }
    }
    finally { $archiveStream.Dispose() }
}

function Expand-VerifiedTarGzip
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Destination)

    $archiveStream = [IO.File]::OpenRead($Path)
    try
    {
        $gzipStream = [IO.Compression.GZipStream]::new(
            $archiveStream,
            [IO.Compression.CompressionMode]::Decompress)
        try
        {
            [System.Formats.Tar.TarFile]::ExtractToDirectory($gzipStream, $Destination, $false)
        }
        finally { $gzipStream.Dispose() }
    }
    finally { $archiveStream.Dispose() }
}

function New-SourceReceipt
{
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)]$ManifestEvidence,
        [Parameter(Mandatory)]$TreeEvidence)

    [ordered]@{
        schemaVersion = "istranscribe-whisper-source-v1"
        spec = "spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation"
        manifestSha256 = $ManifestEvidence.Sha256
        version = [string]$Manifest.runtime.version
        commit = [string]$Manifest.source.commit
        archiveUri = [string]$Manifest.source.archive.uri
        archiveSha256 = [string]$Manifest.source.archive.sha256
        tree = [ordered]@{
            algorithm = [string]$TreeEvidence.algorithm
            treeSha256 = [string]$TreeEvidence.treeSha256
            fileCount = [int]$TreeEvidence.fileCount
            totalBytes = [long]$TreeEvidence.totalBytes
            files = @($TreeEvidence.files)
        }
    }
}

$manifestEvidence = Read-WhisperRuntimeManifest -ManifestPath $ManifestPath
$manifest = $manifestEvidence.Document
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$volumeRoot = [IO.Path]::GetPathRoot($destination)
if ([string]::Equals($destination.TrimEnd([IO.Path]::DirectorySeparatorChar), $volumeRoot.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase))
{
    throw "DestinationRoot cannot be a filesystem root."
}

$sourceParent = Join-Path $destination "source"
$sourceRoot = Join-Path $sourceParent ([string]$manifest.source.commit)
$receiptPath = Join-Path $destination "source-receipt.v1.json"
if (Test-Path -LiteralPath $sourceRoot -PathType Container)
{
    $recoveredTree = Get-WhisperSourceTreeEvidence -SourceRoot $sourceRoot
    if ([string]$recoveredTree.treeSha256 -cne [string]$manifest.source.tree.sha256 -or
        [int]$recoveredTree.fileCount -ne [int]$manifest.source.tree.fileCount -or
        [long]$recoveredTree.totalBytes -ne [long]$manifest.source.tree.totalBytes)
    {
        throw "Existing pinned source tree differs from the canonical archive tree."
    }
    $recoveredReceipt = New-SourceReceipt `
        -Manifest $manifest `
        -ManifestEvidence $manifestEvidence `
        -TreeEvidence $recoveredTree
    Write-DeterministicJson -Path $receiptPath -Value $recoveredReceipt
    $tree = Assert-WhisperSourceTree -SourceRoot $sourceRoot -ReceiptPath $receiptPath -Manifest $manifest -ManifestSha256 $manifestEvidence.Sha256
    [pscustomobject]@{
        SourceRoot = $sourceRoot
        ReceiptPath = $receiptPath
        TreeSha256 = $tree.treeSha256
        Reused = $true
    }
    return
}
if (Test-Path -LiteralPath $sourceRoot) { throw "Pinned source target '$sourceRoot' exists and is not a directory." }
if (Test-Path -LiteralPath $receiptPath) { throw "Source receipt exists without its exact source tree." }

$expectedArchiveHash = [string]$manifest.source.archive.sha256
if (-not [string]::IsNullOrWhiteSpace($ArchivePath))
{
    $archive = [IO.Path]::GetFullPath($ArchivePath)
}
else
{
    $archiveDirectory = Join-Path $destination "archives"
    [IO.Directory]::CreateDirectory($archiveDirectory) | Out-Null
    $archive = Join-Path $archiveDirectory "$expectedArchiveHash.tar.gz"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf))
    {
        if ($Offline) { throw "Pinned source archive is unavailable in offline mode." }

        $partial = Join-Path $archiveDirectory "$expectedArchiveHash.$([Guid]::NewGuid().ToString('N')).partial"
        try
        {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.AllowAutoRedirect = $true
            $client = [Net.Http.HttpClient]::new($handler)
            try
            {
                $client.Timeout = [TimeSpan]::FromMinutes(10)
                $response = $client.GetAsync(
                    [Uri]$manifest.source.archive.uri,
                    [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                try
                {
                    if (-not $response.IsSuccessStatusCode)
                    {
                        throw "Pinned source download failed with HTTP $([int]$response.StatusCode)."
                    }
                    if ($response.Content.Headers.ContentLength -is [long] -and
                        $response.Content.Headers.ContentLength -ne [long]$manifest.source.archive.sizeBytes)
                    {
                        throw "Pinned source download length differs from the manifest."
                    }
                    $input = $response.Content.ReadAsStream()
                    try
                    {
                        $output = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try
                        {
                            $buffer = [byte[]]::new(128 * 1024)
                            [long]$written = 0
                            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0)
                            {
                                $written += $read
                                if ($written -gt [long]$manifest.source.archive.sizeBytes)
                                {
                                    throw "Pinned source download exceeded its manifest size."
                                }
                                $output.Write($buffer, 0, $read)
                            }
                            if ($written -ne [long]$manifest.source.archive.sizeBytes)
                            {
                                throw "Pinned source download ended before its manifest size."
                            }
                        }
                        finally { $output.Dispose() }
                    }
                    finally { $input.Dispose() }
                }
                finally { $response.Dispose() }
            }
            finally
            {
                $client.Dispose()
                $handler.Dispose()
            }

            if ((Get-LowerSha256 -Path $partial) -cne $expectedArchiveHash)
            {
                throw "Downloaded whisper.cpp archive failed SHA-256 verification."
            }
            [IO.File]::Move($partial, $archive, $false)
        }
        finally
        {
            if (Test-Path -LiteralPath $partial -PathType Leaf) { [IO.File]::Delete($partial) }
        }
    }
}

if ((Get-Item -LiteralPath $archive).Length -ne [long]$manifest.source.archive.sizeBytes -or
    (Get-LowerSha256 -Path $archive) -cne $expectedArchiveHash)
{
    throw "Whisper source archive SHA-256 does not match the pinned manifest."
}

$expectedArchiveRoot = [string]$manifest.source.archive.rootDirectory
Test-ArchiveEntries -Path $archive -ExpectedRoot $expectedArchiveRoot

[IO.Directory]::CreateDirectory($destination) | Out-Null
$stagingRoot = Join-Path $destination ".extract-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
Expand-VerifiedTarGzip -Path $archive -Destination $stagingRoot
$stagedSource = Join-Path $stagingRoot $expectedArchiveRoot
if (-not (Test-Path -LiteralPath $stagedSource -PathType Container))
{
    throw "Pinned source root was not produced by extraction."
}

$treeEvidence = Get-WhisperSourceTreeEvidence -SourceRoot $stagedSource
[IO.Directory]::CreateDirectory($sourceParent) | Out-Null
if (Test-Path -LiteralPath $sourceRoot) { throw "Source target appeared during extraction." }
[IO.Directory]::Move($stagedSource, $sourceRoot)
if (@(Get-ChildItem -LiteralPath $stagingRoot -Force).Count -eq 0) { [IO.Directory]::Delete($stagingRoot) }

$receipt = New-SourceReceipt `
    -Manifest $manifest `
    -ManifestEvidence $manifestEvidence `
    -TreeEvidence $treeEvidence
Write-DeterministicJson -Path $receiptPath -Value $receipt

[pscustomobject]@{
    SourceRoot = $sourceRoot
    ReceiptPath = $receiptPath
    TreeSha256 = $treeEvidence.treeSha256
    Reused = $false
}
