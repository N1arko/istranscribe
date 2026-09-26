Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

$script:ExpectedSchema = "istranscribe-whisper-native-v1"
$script:ExpectedCommit = "f049fff95a089aa9969deb009cdd4892b3e74916"
$script:ExpectedArchiveSha256 = "279af4ce60dbf397362868f3bacc75b56a4332ac2541cae155070093f6aaf0e3"
$script:ExpectedArchiveSize = 9016232
$script:ExpectedArchiveUri = "https://github.com/ggml-org/whisper.cpp/archive/f049fff95a089aa9969deb009cdd4892b3e74916.tar.gz"
$script:ExpectedSourceTreeSha256 = "51acd4d77bcf043f54374010b16f5506d69132105fcc24e8803364a4b6b0d549"
$script:ExpectedSourceFileCount = 1882
$script:ExpectedSourceTotalBytes = 36382209
$script:ExpectedPatchId = "primary-backend-introspection-v1"
$script:ExpectedPatchApply = "append-to-src-whisper.cpp"
$script:ExpectedPatchPath = "patches/0001-primary-backend-introspection.inc"
$script:ExpectedPatchSha256 = "1454dafbd55dda3a9ac64fc7941b41e4f98ea6eff012badf1d6834247b4e0f22"
$script:TreeHashAlgorithm = "sha256-utf8-ordinal-path-lf-sha256-lf-size-lf"
$script:ExpectedRequiredSymbols = @(
    "istranscribe_whisper_abi_version_v1",
    "istranscribe_whisper_context_cancel_v1",
    "istranscribe_whisper_context_create_v1",
    "istranscribe_whisper_context_destroy_v1",
    "istranscribe_whisper_context_get_info_v1",
    "istranscribe_whisper_context_segment_count_v1",
    "istranscribe_whisper_context_segment_get_v1",
    "istranscribe_whisper_context_timings_v1",
    "istranscribe_whisper_context_transcribe_v1",
    "istranscribe_whisper_last_error_v1",
    "istranscribe_whisper_probe_backend_v1")

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Required file '$Path' is missing."
    }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256
{
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-OrdinalSortedUnique
{
    param([AllowEmptyCollection()][string[]]$Values)

    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in @($Values))
    {
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Empty manifest value is forbidden." }
        if (-not $set.Add($value)) { throw "Duplicate manifest value '$value'." }
    }
    $result = [string[]]@($set)
    [Array]::Sort($result, [StringComparer]::Ordinal)
    $result
}

function Test-SafeRelativePath
{
    param([Parameter(Mandatory)][string]$Path)

    -not [IO.Path]::IsPathRooted($Path) -and
        $Path -notmatch '(^|[\/])\.\.([\/]|$)' -and
        $Path -notmatch '(^|[\/])\.([\/]|$)' -and
        $Path -notmatch '[:\x00]'
}

function Resolve-ManifestOwnedPath
{
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RelativePath)

    if (-not (Test-SafeRelativePath -Path $RelativePath))
    {
        throw "Unsafe manifest-owned path '$RelativePath'."
    }
    $manifestRoot = [IO.Path]::GetFullPath((Split-Path -Parent $ManifestPath))
    $resolved = [IO.Path]::GetFullPath((Join-Path $manifestRoot $RelativePath))
    if (-not $resolved.StartsWith(
        $manifestRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Manifest-owned path '$RelativePath' escapes its root."
    }
    $resolved
}

function Read-WhisperRuntimeManifest
{
    param([Parameter(Mandatory)][string]$ManifestPath)

    $fullPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
    {
        throw "Whisper runtime manifest '$fullPath' is missing."
    }

    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ([string]$manifest.schemaVersion -cne $script:ExpectedSchema) { throw "Unsupported Whisper runtime manifest schema." }
    if ([string]$manifest.runtime.version -cne "v1.9.1") { throw "Unexpected whisper.cpp version." }
    if ([int]$manifest.runtime.abiVersion -ne 1) { throw "Unexpected native ABI version." }
    if ([int]$manifest.runtime.sampleRate -ne 16000 -or [long]$manifest.runtime.maximumSamplesPerCall -ne 4800000)
    {
        throw "Native audio bounds differ from FEAT-016."
    }
    if ([string]$manifest.source.commit -cne $script:ExpectedCommit) { throw "Unexpected whisper.cpp source commit." }
    if ([string]$manifest.source.tag -cne "v1.9.1") { throw "Unexpected whisper.cpp source tag." }
    if ([string]$manifest.source.archive.uri -cne $script:ExpectedArchiveUri) { throw "Unexpected whisper.cpp archive URI." }
    if ([long]$manifest.source.archive.sizeBytes -ne $script:ExpectedArchiveSize) { throw "Unexpected whisper.cpp archive size." }
    if ([string]$manifest.source.archive.sha256 -cne $script:ExpectedArchiveSha256) { throw "Unexpected whisper.cpp archive SHA-256." }
    if ([string]$manifest.source.archive.format -cne "tar.gz") { throw "Unexpected whisper.cpp archive format." }
    if ([string]$manifest.source.archive.rootDirectory -cne "whisper.cpp-$($script:ExpectedCommit)")
    {
        throw "Unexpected source archive root."
    }
    if ([string]$manifest.source.tree.algorithm -cne $script:TreeHashAlgorithm -or
        [string]$manifest.source.tree.sha256 -cne $script:ExpectedSourceTreeSha256 -or
        [int]$manifest.source.tree.fileCount -ne $script:ExpectedSourceFileCount -or
        [long]$manifest.source.tree.totalBytes -ne $script:ExpectedSourceTotalBytes)
    {
        throw "Unexpected canonical whisper.cpp source tree identity."
    }

    $archiveUri = [Uri]$manifest.source.archive.uri
    if ($archiveUri.Scheme -cne "https" -or $archiveUri.Host -cne "github.com")
    {
        throw "Whisper source must use the pinned GitHub HTTPS archive."
    }

    $patches = @($manifest.source.patches)
    if ($patches.Count -ne 1 -or
        [string]$patches[0].id -cne $script:ExpectedPatchId -or
        [string]$patches[0].apply -cne $script:ExpectedPatchApply -or
        [string]$patches[0].checkedInPath -cne $script:ExpectedPatchPath -or
        [string]$patches[0].sha256 -cne $script:ExpectedPatchSha256)
    {
        throw "The app-owned pinned source patch set is unexpected."
    }
    $patchPath = Resolve-ManifestOwnedPath -ManifestPath $fullPath -RelativePath $script:ExpectedPatchPath
    if ((Get-LowerSha256 -Path $patchPath) -cne $script:ExpectedPatchSha256)
    {
        throw "The app-owned pinned source patch does not match its manifest hash."
    }

    $requiredSymbols = @(Get-OrdinalSortedUnique -Values @($manifest.abi.requiredSymbols | ForEach-Object { [string]$_ }))
    $expectedSymbols = @(Get-OrdinalSortedUnique -Values $script:ExpectedRequiredSymbols)
    if (($requiredSymbols -join "`n") -cne ($expectedSymbols -join "`n") -or
        [string]$manifest.abi.callingConvention -cne "cdecl" -or
        [string]$manifest.abi.stringEncoding -cne "utf-8-pointer-length")
    {
        throw "Native ABI manifest is incomplete."
    }

    $rids = @(Get-OrdinalSortedUnique -Values @($manifest.rids | ForEach-Object { [string]$_.rid }))
    if (($rids -join "|") -cne "osx-arm64|win-x64") { throw "Native RID set must be exactly osx-arm64 and win-x64." }

    foreach ($license in @($manifest.licenses))
    {
        $licensePath = Resolve-ManifestOwnedPath -ManifestPath $fullPath -RelativePath ([string]$license.checkedInPath)
        $actualHash = Get-LowerSha256 -Path $licensePath
        if ($actualHash -cne [string]$license.sha256)
        {
            throw "Checked-in license '$($license.checkedInPath)' does not match its manifest hash."
        }
    }

    [pscustomobject]@{
        Path = $fullPath
        Document = $manifest
        Sha256 = Get-LowerSha256 -Path $fullPath
    }
}

function Get-WhisperRidVariant
{
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string]$Variant)

    $ridEntry = @($Manifest.rids | Where-Object { [string]$_.rid -ceq $Rid })
    if ($ridEntry.Count -ne 1) { throw "RID '$Rid' is absent or duplicated in the runtime manifest." }
    $variantEntry = @($ridEntry[0].variants | Where-Object { [string]$_.id -ceq $Variant })
    if ($variantEntry.Count -ne 1) { throw "Variant '$Rid/$Variant' is absent or duplicated in the runtime manifest." }
    $variantEntry[0]
}

function Get-WhisperSourceTreeEvidence
{
    param([Parameter(Mandatory)][string]$SourceRoot)

    $root = [IO.Path]::GetFullPath($SourceRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Source root '$root' is missing." }

    $rootItem = Get-Item -LiteralPath $root -Force
    if ($null -ne $rootItem.LinkType -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Source root cannot be a link or reparse point."
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $root -Recurse -Force -Directory))
    {
        if ($null -ne $directory.LinkType -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Source tree contains a directory link or reparse point: '$($directory.FullName)'."
        }
    }

    $entriesByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -Force -File))
    {
        if ($null -ne $file.LinkType -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Source tree contains a link or reparse point: '$($file.FullName)'."
        }
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if (-not (Test-SafeRelativePath -Path $relative)) { throw "Unsafe source path '$relative'." }
        $entry = [pscustomobject][ordered]@{
            path = $relative
            sha256 = Get-LowerSha256 -Path $file.FullName
            size = [long]$file.Length
        }
        if (-not $entriesByPath.TryAdd($relative, $entry)) { throw "Duplicate source path '$relative'." }
    }

    $orderedPaths = @(Get-OrdinalSortedUnique -Values @($entriesByPath.Keys))
    $ordered = @($orderedPaths | ForEach-Object { $entriesByPath[$_] })
    $builder = [Text.StringBuilder]::new()
    [long]$totalBytes = 0
    foreach ($entry in $ordered)
    {
        [void]$builder.Append([string]$entry.path).Append("`n")
        [void]$builder.Append([string]$entry.sha256).Append("`n")
        [void]$builder.Append([long]$entry.size).Append("`n")
        $totalBytes += [long]$entry.size
    }

    [pscustomobject]@{
        algorithm = $script:TreeHashAlgorithm
        treeSha256 = Get-TextSha256 -Text $builder.ToString()
        fileCount = $ordered.Count
        totalBytes = $totalBytes
        files = $ordered
    }
}

function Assert-WhisperSourceTree
{
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$ManifestSha256)

    $fullReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not (Test-Path -LiteralPath $fullReceiptPath -PathType Leaf))
    {
        throw "Source receipt '$fullReceiptPath' is missing."
    }
    $receipt = Get-Content -LiteralPath $fullReceiptPath -Raw | ConvertFrom-Json
    if ([string]$receipt.schemaVersion -cne "istranscribe-whisper-source-v1" -or
        [string]$receipt.manifestSha256 -cne $ManifestSha256 -or
        [string]$receipt.commit -cne [string]$Manifest.source.commit -or
        [string]$receipt.archiveSha256 -cne [string]$Manifest.source.archive.sha256 -or
        [string]$receipt.tree.algorithm -cne $script:TreeHashAlgorithm)
    {
        throw "Source receipt does not match the pinned runtime manifest."
    }

    $evidence = Get-WhisperSourceTreeEvidence -SourceRoot $SourceRoot
    if ([string]$evidence.algorithm -cne [string]$Manifest.source.tree.algorithm -or
        [string]$evidence.treeSha256 -cne [string]$Manifest.source.tree.sha256 -or
        [int]$evidence.fileCount -ne [int]$Manifest.source.tree.fileCount -or
        [long]$evidence.totalBytes -ne [long]$Manifest.source.tree.totalBytes -or
        [string]$receipt.tree.treeSha256 -cne [string]$evidence.treeSha256 -or
        [int]$receipt.tree.fileCount -ne [int]$evidence.fileCount -or
        [long]$receipt.tree.totalBytes -ne [long]$evidence.totalBytes)
    {
        throw "Whisper source tree differs from its verified extraction receipt."
    }

    $sourceLicense = Join-Path ([IO.Path]::GetFullPath($SourceRoot)) "LICENSE"
    $licenseEntry = @($Manifest.licenses | Where-Object {
        $_.PSObject.Properties.Name -contains "archivePath" -and [string]$_.archivePath -ceq "LICENSE"
    })
    if ($licenseEntry.Count -ne 1 -or (Get-LowerSha256 -Path $sourceLicense) -cne [string]$licenseEntry[0].sha256)
    {
        throw "Pinned source license differs from the release manifest."
    }
    $evidence
}

function Write-DeterministicJson
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 100) -replace "`r`n", "`n" -replace "`r", "`n"
    $temporaryPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).partial"
    try
    {
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json.TrimEnd("`n") + "`n",
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $fullPath, $true)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf)
        {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

Export-ModuleMember -Function @(
    "Assert-WhisperSourceTree",
    "Get-LowerSha256",
    "Get-OrdinalSortedUnique",
    "Get-TextSha256",
    "Get-WhisperRidVariant",
    "Get-WhisperSourceTreeEvidence",
    "Read-WhisperRuntimeManifest",
    "Resolve-ManifestOwnedPath",
    "Test-SafeRelativePath",
    "Write-DeterministicJson")
