#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$EvidencePath,
    [switch]$ConfirmTemporaryMachineTrustAndLocalPackageMutation,
    [switch]$RecoverOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
$packageName = "isTranscribe.Desktop"
$expectedPublisher = "CN=isTranscribe Development"
$expectedThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$expectedBaseHash = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B"
$expectedUpgradeHash = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3"
$expectedCertificateHash = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC"
$expectedHarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$expectedTrustHelperHash = "0B370AE48B9099FDFC0EDA22C8CF523D547C8764AAA4070D0B14536242215185"
$allowedVersions = @([version]"2.0.0.65535", [version]"2.0.1.65535")

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
                throw "Local lifecycle paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
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
                throw "Local lifecycle data must not contain reparse points: $entryPath"
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}

function Write-JsonAtomically
{
    param([object]$Value, [string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = Split-Path -Parent $fullPath
    [void][IO.Directory]::CreateDirectory($directory)
    Assert-NoReparsePointInPath -Path $directory
    $temporary = Join-Path $directory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($fullPath)), [guid]::NewGuid())
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $fullPath)
    {
        $backup = Join-Path $directory (".{0}.{1}.bak" -f ([IO.Path]::GetFileName($fullPath)), [guid]::NewGuid())
        try
        {
            [IO.File]::Replace($temporary, $fullPath, $backup, $true)
            Remove-Item -LiteralPath $backup -Force
        }
        finally
        {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        }
    }
    else { [IO.File]::Move($temporary, $fullPath) }
}

function Get-DataManifest
{
    param([string]$Root, [switch]$HashContent)
    if (-not (Test-Path -LiteralPath $Root -PathType Container))
    {
        return [ordered]@{ rootPresent = $false; rootAclSddl = $null; files = @() }
    }
    Assert-NoReparsePointInPath -Path $Root
    Assert-NoReparsePointInTree -Root $Root
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $files = foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)
    {
        $entry = [ordered]@{
            relativePath = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            sizeBytes = $file.Length
            lastWriteUtcTicks = $file.LastWriteTimeUtc.Ticks
        }
        if ($HashContent) { $entry.sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        $entry
    }
    return [ordered]@{ rootPresent = $true; rootAclSddl = (Get-Acl -LiteralPath $Root).Sddl; files = @($files) }
}

function Get-DocumentsManifest
{
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container))
    {
        return [ordered]@{ rootPresent = $false; entries = @() }
    }
    Assert-NoReparsePointInPath -Path $Root
    $rootFullPath = [IO.Path]::GetFullPath($Root)
    $prefix = $rootFullPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootFullPath)
    $entries = [Collections.Generic.List[object]]::new()
    while ($pending.Count -gt 0)
    {
        $directory = $pending.Pop()
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($directory))
        {
            $item = Get-Item -LiteralPath $entryPath -Force
            $isReparse = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            $entries.Add([pscustomobject][ordered]@{
                    relativePath = $item.FullName.Substring($prefix.Length).Replace('\', '/')
                    isDirectory = [bool]$item.PSIsContainer
                    isReparsePoint = $isReparse
                    sizeBytes = if ($item.PSIsContainer -or $isReparse) { $null } else { $item.Length }
                    lastWriteUtcTicks = $item.LastWriteTimeUtc.Ticks
                    attributes = [int]$item.Attributes
                })
            if ($item.PSIsContainer -and -not $isReparse) { $pending.Push($item.FullName) }
        }
    }
    return [ordered]@{
        rootPresent = $true
        entries = @($entries | Sort-Object relativePath)
    }
}

function Get-ManifestDigest
{
    param([object]$Manifest)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Manifest | ConvertTo-Json -Depth 8 -Compress))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-ContentDigest
{
    param([object]$Manifest)
    $projection = [ordered]@{
        rootPresent = $Manifest.rootPresent
        files = @($Manifest.files | ForEach-Object {
                [ordered]@{
                    relativePath = $_.relativePath
                    sizeBytes = $_.sizeBytes
                    sha256 = $_.sha256
                }
            })
    }
    return Get-ManifestDigest -Manifest $projection
}

function Copy-DirectoryContent
{
    param([string]$Source, [string]$Destination)
    [void][IO.Directory]::CreateDirectory($Destination)
    $prefix = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($directory in Get-ChildItem -LiteralPath $Source -Recurse -Directory -Force | Sort-Object FullName)
    {
        [void][IO.Directory]::CreateDirectory((Join-Path $Destination $directory.FullName.Substring($prefix.Length)))
    }
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force | Sort-Object FullName)
    {
        $target = Join-Path $Destination $file.FullName.Substring($prefix.Length)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [IO.File]::Copy($file.FullName, $target, $false)
        [IO.File]::SetLastWriteTimeUtc($target, $file.LastWriteTimeUtc)
    }
}

function Copy-PinnedFile
{
    param([string]$Source, [string]$Destination, [string]$ExpectedHash)
    $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try
    {
        $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $sourceStream.CopyTo($destinationStream); $destinationStream.Flush($true) }
        finally { $destinationStream.Dispose() }
    }
    finally { $sourceStream.Dispose() }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -cne $ExpectedHash)
    {
        throw "A copied local lifecycle input does not match its reviewed SHA-256 pin."
    }
}

function Open-PinnedReadLease
{
    param([string]$Path, [string]$ExpectedHash)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -cne $ExpectedHash)
    {
        $stream.Dispose()
        throw "A local lifecycle input changed before its read lease was acquired."
    }
    $stream.Position = 0
    return $stream
}

function Open-ResultIdentityLease
{
    param([string]$Path, [switch]$RequireExisting, [switch]$RequireNew)
    Assert-NoReparsePointInPath -Path $Path
    if ($RequireExisting -and $RequireNew) { throw "A result identity lease cannot be both existing and new." }
    if ($RequireExisting -and -not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "The elevated trust intent receipt is missing."
    }
    if ($RequireNew)
    {
        return [IO.File]::Open(
            $Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    }
    if ($RequireExisting)
    {
        return [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    }
    throw "A result identity lease must explicitly require an existing or new file."
}

function Initialize-SqliteProbeFromPackage
{
    param([string]$PackagePath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [void][IO.Directory]::CreateDirectory($Destination)
    Assert-NoReparsePointInPath -Path $Destination
    $required = @(
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "Microsoft.Data.Sqlite.dll",
        "e_sqlite3.dll")
    $expectedHashes = @{}
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try
    {
        foreach ($name in $required)
        {
            $entries = @($archive.Entries | Where-Object FullName -CEQ $name)
            if ($entries.Count -ne 1) { throw "The pinned package does not contain one exact SQLite probe dependency." }
            $hashInput = $entries[0].Open()
            try { $expectedHashes[$name] = (Get-FileHash -InputStream $hashInput -Algorithm SHA256).Hash }
            finally { $hashInput.Dispose() }
            $target = Join-Path $Destination $name
            if (Test-Path -LiteralPath $target -PathType Leaf)
            {
                if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
                {
                    throw "The retained SQLite probe cache contains a reparse point."
                }
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $expectedHashes[$name])
                {
                    throw "The retained SQLite probe cache differs from the pinned package."
                }
            }
            else
            {
                $input = $entries[0].Open()
                try
                {
                    $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
                }
                finally { $input.Dispose() }
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $expectedHashes[$name])
                {
                    throw "The extracted SQLite probe cache differs from the pinned package."
                }
            }
        }
    }
    finally { $archive.Dispose() }

    Assert-NoReparsePointInTree -Root $Destination
    $actualFiles = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force)
    if ($actualFiles.Count -ne $required.Count -or
        @($actualFiles | Where-Object { $_.DirectoryName -cne [IO.Path]::GetFullPath($Destination) -or $_.Name -notin $required }).Count -ne 0)
    {
        throw "The retained SQLite probe cache is outside its exact file allowlist."
    }

    $cacheLeases = [Collections.Generic.List[IO.FileStream]]::new()
    try
    {
        foreach ($name in $required)
        {
            $cacheLeases.Add((Open-PinnedReadLease -Path (Join-Path $Destination $name) -ExpectedHash $expectedHashes[$name]))
        }
        $env:PATH = $Destination + [IO.Path]::PathSeparator + $env:PATH
        foreach ($name in @(
                "SQLitePCLRaw.core.dll",
                "SQLitePCLRaw.provider.e_sqlite3.dll",
                "SQLitePCLRaw.batteries_v2.dll",
                "Microsoft.Data.Sqlite.dll"))
        {
            [void][Reflection.Assembly]::LoadFrom((Join-Path $Destination $name))
        }
        [SQLitePCL.Batteries_V2]::Init()
    }
    finally
    {
        foreach ($lease in $cacheLeases) { $lease.Dispose() }
    }
}

function Get-SqliteSnapshot
{
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return [ordered]@{ present = $false; integrity = $null; userVersion = $null; tables = @() }
    }
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        "Data Source=$Path;Mode=ReadOnly;Cache=Private;Pooling=False")
    try
    {
        $connection.Open()
        $command = $connection.CreateCommand()
        try { $command.CommandText = "PRAGMA integrity_check;"; $integrity = [string]$command.ExecuteScalar() }
        finally { $command.Dispose() }
        $command = $connection.CreateCommand()
        try { $command.CommandText = "PRAGMA user_version;"; $userVersion = [int64]$command.ExecuteScalar() }
        finally { $command.Dispose() }
        $command = $connection.CreateCommand()
        try
        {
            $command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            $reader = $command.ExecuteReader()
            try
            {
                $names = [Collections.Generic.List[string]]::new()
                while ($reader.Read()) { $names.Add($reader.GetString(0)) }
            }
            finally { $reader.Dispose() }
        }
        finally { $command.Dispose() }
        $tables = foreach ($name in $names)
        {
            if ($name -notmatch '^[A-Za-z0-9_]+$') { throw "SQLite contains a table name outside the probe allowlist." }
            $command = $connection.CreateCommand()
            try { $command.CommandText = "SELECT COUNT(*) FROM `"$name`";"; $count = [int64]$command.ExecuteScalar() }
            finally { $command.Dispose() }
            [ordered]@{ name = $name; rowCount = $count }
        }
        return [ordered]@{ present = $true; integrity = $integrity; userVersion = $userVersion; tables = @($tables) }
    }
    finally { $connection.Dispose() }
}

function Get-SettingsSnapshot
{
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return [ordered]@{ present = $false; parsed = $false; properties = @() }
    }
    $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    return [ordered]@{
        present = $true
        parsed = $null -ne $settings
        properties = @($settings.PSObject.Properties.Name | Sort-Object)
    }
}

function Get-LocalMachineTrustedCertificates
{
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        "TrustedPeople", [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try
    {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return @($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint)
    }
    finally { $store.Dispose() }
}

function Invoke-ElevatedTrustAction
{
    param(
        [string]$Action,
        [string]$HelperPath,
        [string]$CertificatePath,
        [string]$ResultPath,
        [string]$OwnershipToken)
    foreach ($path in @($HelperPath, $CertificatePath, $ResultPath)) { Assert-NoReparsePointInPath -Path $path }
    $helperLease = Open-PinnedReadLease -Path $HelperPath -ExpectedHash $expectedTrustHelperHash
    $certificateLease = $null
    $resultLease = $null
    $intentLease = $null
    try
    {
        $certificateLease = Open-PinnedReadLease -Path $CertificatePath -ExpectedHash $expectedCertificateHash
        $intentPath = Join-Path (Split-Path -Parent $HelperPath) "trust-add.intent.json"
        if ($Action -ceq "Add")
        {
            $intentLease = Open-ResultIdentityLease -Path $intentPath -RequireNew
            $resultLease = Open-ResultIdentityLease -Path $ResultPath -RequireNew
        }
        else
        {
            $intentLease = Open-ResultIdentityLease -Path $intentPath -RequireExisting
            $resultLease = Open-ResultIdentityLease -Path $ResultPath -RequireNew
        }
        $arguments = @(
            "-NoLogo", "-NoProfile", "-File", $HelperPath,
            "-Action", $Action,
            "-CertificatePath", $CertificatePath,
            "-ResultPath", $ResultPath,
            "-OwnershipToken", $OwnershipToken)
        $argumentLine = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join " "
        $process = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argumentLine `
            -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $ResultPath -PathType Leaf))
        {
            throw "The elevated trust helper did not complete successfully."
        }
        $result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
        $expectedPhase = if ($Action -ceq "Add") { "added" } else { "removed" }
        if ($result.schemaVersion -cne "infra-005-local-machine-trust-v1" -or
            $result.ownershipToken -cne $OwnershipToken -or $result.action -cne $Action -or
            $result.status -cne "passed" -or $result.phase -cne $expectedPhase -or
            $result.thumbprint -cne $expectedThumbprint -or $result.privateKeyImported)
        {
            throw "The elevated trust result failed its exact ownership and postcondition contract."
        }
        return $result
    }
    finally
    {
        if ($null -ne $intentLease) { $intentLease.Dispose() }
        if ($null -ne $resultLease) { $resultLease.Dispose() }
        if ($null -ne $certificateLease) { $certificateLease.Dispose() }
        $helperLease.Dispose()
    }
}

function Remove-ExactDevelopmentPackage
{
    $registrations = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
    foreach ($registration in $registrations)
    {
        if ([version]$registration.Version -notin $allowedVersions -or $registration.Publisher -cne $expectedPublisher)
        {
            throw "Refusing to remove a package outside the reviewed development identities."
        }
        foreach ($process in Get-Process -Name "IsTranscribe.Desktop", "isTranscribe" -ErrorAction SilentlyContinue)
        {
            if (Test-PathInsideOrEqual -Path $process.Path -ParentPath $registration.InstallLocation)
            {
                Stop-Process -InputObject $process -Force -ErrorAction Stop
            }
        }
        Remove-AppxPackage -Package $registration.PackageFullName -ErrorAction Stop
    }
}

function Remove-OwnedLifecycleSentinels
{
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Force -Filter ".infra005-lifecycle-*.sentinel")
    {
        if ($file.Name -notmatch '^\.infra005-lifecycle-([0-9a-fA-F-]{36})\.sentinel$') { continue }
        $runId = $Matches[1]
        $content = Get-Content -LiteralPath $file.FullName -Raw
        if ($content -ceq "INFRA-005.A lifecycle acceptance $runId")
        {
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}

function Remove-OwnedTransaction
{
    param([string]$Path, [string]$Parent)
    if (-not (Test-PathInsideOrEqual -Path $Path -ParentPath $Parent) -or
        [string]::Equals([IO.Path]::GetFullPath($Path), [IO.Path]::GetFullPath($Parent), [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove a transaction outside its exact parent."
    }
    Assert-NoReparsePointInPath -Path $Path
    Assert-NoReparsePointInTree -Root $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

if (-not $ConfirmTemporaryMachineTrustAndLocalPackageMutation -and -not $RecoverOnly)
{
    throw "Pass -ConfirmTemporaryMachineTrustAndLocalPackageMutation only after explicit user approval."
}
if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) { throw "Local lifecycle requires Windows x64." }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw "Run the local lifecycle from a normal medium-token terminal."
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Join-Path $PSScriptRoot "..\.." }
$repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot)
$localEvidenceRoot = Join-Path $repositoryFullPath "artifacts\acceptance\INFRA-005\windows-x64\local-host"
if ([string]::IsNullOrWhiteSpace($EvidencePath)) { $EvidencePath = Join-Path $localEvidenceRoot "local-existing-profile-lifecycle.json" }
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
if (-not (Test-PathInsideOrEqual -Path $evidenceFullPath -ParentPath $localEvidenceRoot) -or
    [string]::Equals($evidenceFullPath, [IO.Path]::GetFullPath($localEvidenceRoot), [StringComparison]::OrdinalIgnoreCase))
{
    throw "EvidencePath must be a file inside the owned local-host acceptance directory."
}
foreach ($path in @($repositoryFullPath, $localEvidenceRoot, $evidenceFullPath)) { Assert-NoReparsePointInPath -Path $path }
[void][IO.Directory]::CreateDirectory($localEvidenceRoot)
Assert-NoReparsePointInPath -Path $localEvidenceRoot

$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$canonicalRoot = Join-Path $localApplicationData "isTranscribe"
$documentsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)) "isTranscribe"
$transactionParent = Join-Path $localApplicationData "isTranscribeAcceptance"
$journalPath = Join-Path $localEvidenceRoot "local-existing-profile.recovery.json"
foreach ($protectedRoot in @($canonicalRoot, $documentsRoot, $transactionParent))
{
    if ((Test-PathInsideOrEqual -Path $localEvidenceRoot -ParentPath $protectedRoot) -or
        (Test-PathInsideOrEqual -Path $protectedRoot -ParentPath $localEvidenceRoot))
    {
        throw "Evidence and user-data contours must remain disjoint."
    }
}

$certificateSource = Join-Path $repositoryFullPath "artifacts\release\windows-x64\development\2.0.1\isTranscribe-development-certificate.cer"
$trustHelperSource = Join-Path $PSScriptRoot "Set-LocalDevelopmentCertificateTrust.ps1"

if (Test-Path -LiteralPath $journalPath -PathType Leaf)
{
    $stale = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    if ($stale.schemaVersion -cne "infra-005-local-machine-existing-profile-recovery-v1" -or
        [string]$stale.runId -cnotmatch '^[0-9a-f]{32}$' -or
        [string]$stale.ownershipToken -cne [string]$stale.runId -or
        [string]$stale.backupContentDigest -cnotmatch '^[0-9a-f]{64}$' -or
        [int64]$stale.backupFileCount -lt 1 -or
        $stale.machineTrustPreexisted)
    {
        throw "The existing local lifecycle journal has an unknown schema."
    }
    $transactionFullPath = [IO.Path]::GetFullPath([string]$stale.transactionRoot)
    if (-not (Test-PathInsideOrEqual -Path $transactionFullPath -ParentPath $transactionParent) -or
        [string]::Equals($transactionFullPath, [IO.Path]::GetFullPath($transactionParent), [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The recovery transaction path is outside its owned parent."
    }
    $staleBackup = [IO.Path]::GetFullPath([string]$stale.dataBackup)
    if (-not (Test-PathInsideOrEqual -Path $staleBackup -ParentPath $transactionFullPath) -or
        -not (Test-Path -LiteralPath $staleBackup -PathType Container))
    {
        throw "The retained recovery backup is missing or outside its exact transaction."
    }
    Assert-NoReparsePointInPath -Path $transactionFullPath
    Assert-NoReparsePointInPath -Path $staleBackup
    $staleBackupManifest = Get-DataManifest -Root $staleBackup -HashContent
    if ((Get-ContentDigest -Manifest $staleBackupManifest) -cne [string]$stale.backupContentDigest -or
        @($staleBackupManifest.files).Count -ne [int64]$stale.backupFileCount)
    {
        throw "The retained recovery backup no longer matches its verified content digest."
    }
    if (-not $stale.completed)
    {
        Remove-ExactDevelopmentPackage
        if (@(Get-Process -Name "IsTranscribe.Desktop", "isTranscribe", "IsTranscribe.App" -ErrorAction SilentlyContinue).Count -eq 0)
        {
            Remove-OwnedLifecycleSentinels -Root $canonicalRoot
        }
        $machineMatches = @(Get-LocalMachineTrustedCertificates)
        if ($machineMatches.Count -ne 0 -and -not $stale.machineTrustAddStarted)
        {
            throw "Recovery found machine trust that is not owned by the lifecycle journal."
        }
        if ($machineMatches.Count -ne 0)
        {
            $staleHelper = Join-Path $transactionFullPath "Set-LocalDevelopmentCertificateTrust.ps1"
            $staleCertificate = Join-Path $transactionFullPath "development.cer"
            $staleRemoveResult = Join-Path $transactionFullPath ("trust-remove-{0}.json" -f [guid]::NewGuid().ToString("N"))
            Invoke-ElevatedTrustAction -Action Remove -HelperPath $staleHelper `
                -CertificatePath $staleCertificate -ResultPath $staleRemoveResult `
                -OwnershipToken ([string]$stale.ownershipToken) | Out-Null
        }
        if (@(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue).Count -ne 0 -or
            @(Get-Process -Name "IsTranscribe.Desktop", "isTranscribe", "IsTranscribe.App" -ErrorAction SilentlyContinue).Count -ne 0 -or
            @(Get-LocalMachineTrustedCertificates).Count -ne 0)
        {
            throw "Recovery could not restore package/process/machine-trust invariants."
        }
        $stale.completed = $true
        $stale.phase = "host_state_recovered_backup_retained"
        Write-JsonAtomically -Value $stale -Path $journalPath
    }
    if ($RecoverOnly) { Write-Output $journalPath; return }
    if (-not $stale.completed) { throw "The previous local lifecycle still requires recovery." }
}
elseif ($RecoverOnly) { throw "No local lifecycle recovery journal exists." }

if (Test-Path -LiteralPath $evidenceFullPath) { throw "The local lifecycle evidence file already exists." }
if (-not (Test-Path -LiteralPath $canonicalRoot -PathType Container)) { throw "Existing-profile local smoke requires the canonical data root." }
Assert-NoReparsePointInPath -Path $canonicalRoot
Assert-NoReparsePointInTree -Root $canonicalRoot
if (@(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue).Count -ne 0 -or
    @(Get-Process -Name "IsTranscribe.Desktop", "isTranscribe", "IsTranscribe.App" -ErrorAction SilentlyContinue).Count -ne 0 -or
    @(Get-LocalMachineTrustedCertificates).Count -ne 0)
{
    throw "Package, process and machine development trust must be absent before the local lifecycle."
}

$currentUserBuildCertificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$expectedThumbprint" -ErrorAction SilentlyContinue
if ($null -eq $currentUserBuildCertificate -or -not $currentUserBuildCertificate.HasPrivateKey)
{
    throw "The reviewed build certificate in CurrentUser/My must remain present and private."
}
$buildCertificateRawHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($currentUserBuildCertificate.RawData))

$runId = [guid]::NewGuid().ToString("N")
$transactionRoot = Join-Path $transactionParent $runId
Assert-NoReparsePointInPath -Path $transactionParent
[void][IO.Directory]::CreateDirectory($transactionRoot)
Assert-NoReparsePointInPath -Path $transactionRoot
$localBase = Join-Path $transactionRoot "base.msix"
$localUpgrade = Join-Path $transactionRoot "upgrade.msix"
$localCertificate = Join-Path $transactionRoot "development.cer"
$localHarness = Join-Path $transactionRoot "Test-WindowsReleaseLifecycle.ps1"
$localTrustHelper = Join-Path $transactionRoot "Set-LocalDevelopmentCertificateTrust.ps1"
$trustAddResult = Join-Path $transactionRoot "trust-add.json"
$trustRemoveResult = Join-Path $transactionRoot ("trust-remove-{0}.json" -f [guid]::NewGuid().ToString("N"))
$dataBackup = Join-Path $transactionRoot "data-content-backup"
$sqliteProbe = Join-Path $transactionParent ("sqlite-probe-cache-{0}" -f $expectedUpgradeHash.ToLowerInvariant())
$preflightPath = Join-Path $localEvidenceRoot "local-existing-profile.preflight.json"
$harnessEvidencePath = Join-Path $localEvidenceRoot "local-existing-profile.harness.json"
$preparationCommitted = $false
try
{
    foreach ($path in @($preflightPath, $harnessEvidencePath)) { if (Test-Path -LiteralPath $path) { throw "Local evidence already exists: $path" } }

    $sourceBase = Join-Path $repositoryFullPath "artifacts\release\windows-x64\development\2.0.0\isTranscribe-2.0.0-dev-win-x64.msix"
    $sourceUpgrade = Join-Path $repositoryFullPath "artifacts\release\windows-x64\development\2.0.1\isTranscribe-2.0.1-dev-win-x64.msix"
    $sourceHarness = Join-Path $PSScriptRoot "Test-WindowsReleaseLifecycle.ps1"
    foreach ($path in @($sourceBase, $sourceUpgrade, $certificateSource, $sourceHarness, $trustHelperSource)) { Assert-NoReparsePointInPath -Path $path }
    Copy-PinnedFile -Source $sourceBase -Destination $localBase -ExpectedHash $expectedBaseHash
    Copy-PinnedFile -Source $sourceUpgrade -Destination $localUpgrade -ExpectedHash $expectedUpgradeHash
    Copy-PinnedFile -Source $certificateSource -Destination $localCertificate -ExpectedHash $expectedCertificateHash
    Copy-PinnedFile -Source $sourceHarness -Destination $localHarness -ExpectedHash $expectedHarnessHash
    Copy-PinnedFile -Source $trustHelperSource -Destination $localTrustHelper -ExpectedHash $expectedTrustHelperHash
    $preparationPackageLease = Open-PinnedReadLease -Path $localUpgrade -ExpectedHash $expectedUpgradeHash
    try { Initialize-SqliteProbeFromPackage -PackagePath $localUpgrade -Destination $sqliteProbe }
    finally { $preparationPackageLease.Dispose() }

    $documentsBaseline = Get-DocumentsManifest -Root $documentsRoot
    $settingsPath = Join-Path $canonicalRoot "config\settings.json"
    $secretsPath = Join-Path $canonicalRoot "config\secrets.bin"
    $databasePath = Join-Path $canonicalRoot "data\app.db"
    $ephemeralDataRelativePaths = @("data/app.db-shm", "data/app.db-wal")
    $settingsBaseline = Get-SettingsSnapshot -Path $settingsPath
    $databaseBaseline = Get-SqliteSnapshot -Path $databasePath
    $secretsBaselineHash = if (Test-Path -LiteralPath $secretsPath -PathType Leaf) {
        (Get-FileHash -LiteralPath $secretsPath -Algorithm SHA256).Hash
    } else { $null }
    if (-not $settingsBaseline.present -or -not $settingsBaseline.parsed -or
        -not $databaseBaseline.present -or $databaseBaseline.integrity -cne "ok" -or
        [string]::IsNullOrWhiteSpace($secretsBaselineHash))
    {
        throw "The existing profile did not pass baseline settings, secrets and SQLite integrity checks."
    }
    $baseline = Get-DataManifest -Root $canonicalRoot -HashContent
    Copy-DirectoryContent -Source $canonicalRoot -Destination $dataBackup
    $backupManifest = Get-DataManifest -Root $dataBackup -HashContent
    $backupContentDigest = Get-ContentDigest -Manifest $backupManifest
    if ($backupContentDigest -cne (Get-ContentDigest -Manifest $baseline))
    {
        throw "The content backup does not match the existing data baseline."
    }

    $journal = [ordered]@{
        schemaVersion = "infra-005-local-machine-existing-profile-recovery-v1"
        runId = $runId
        ownershipToken = $runId
        phase = "inputs_locked"
        transactionRoot = $transactionRoot
        dataBackup = $dataBackup
        backupContentDigest = $backupContentDigest
        backupFileCount = @($backupManifest.files).Count
        machineTrustPreexisted = $false
        machineTrustAddStarted = $false
        completed = $false
    }
    Write-JsonAtomically -Value $journal -Path $journalPath
    $preparationCommitted = $true
}
catch
{
    $preparationFailure = $_
    if (-not $preparationCommitted -and -not (Test-Path -LiteralPath $journalPath))
    {
        Remove-OwnedTransaction -Path $transactionRoot -Parent $transactionParent
    }
    throw $preparationFailure
}

$evidence = [ordered]@{
    schemaVersion = "infra-005-local-existing-profile-lifecycle-v1"
    spec = "spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification"
    runId = $runId
    transactionRoot = $transactionRoot
    dataBackup = $dataBackup
    status = "running"
    startedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    localSmokeOnly = $true
    standardUserProven = $false
    existingDataRootStayedInPlace = $true
    baselineFileCount = @($baseline.files).Count
    contentBackupVerified = $true
    contentBackupVerifiedAfterCleanup = $false
    contentBackupRetained = $true
    packageLifecyclePassed = $false
    baselineFilesPreserved = $false
    rootAclPreserved = $false
    documentsMetadataUnchanged = $false
    packageAbsentAfterCleanup = $false
    processesAbsentAfterCleanup = $false
    machineTrustRemoved = $false
    buildCertificatePreserved = $false
    settingsReadable = $false
    secretsUnchanged = $false
    sqliteIntegrityPassed = $false
    sqliteBaselineRowsPreserved = $false
    cleanupFailures = [Collections.Generic.List[string]]::new()
}

$leases = [Collections.Generic.List[IO.FileStream]]::new()
$capturedFailure = $null
try
{
    $leases.Add((Open-PinnedReadLease -Path $localBase -ExpectedHash $expectedBaseHash))
    $leases.Add((Open-PinnedReadLease -Path $localUpgrade -ExpectedHash $expectedUpgradeHash))
    $leases.Add((Open-PinnedReadLease -Path $localCertificate -ExpectedHash $expectedCertificateHash))
    $leases.Add((Open-PinnedReadLease -Path $localHarness -ExpectedHash $expectedHarnessHash))
    $leases.Add((Open-PinnedReadLease -Path $localTrustHelper -ExpectedHash $expectedTrustHelperHash))

    $journal.machineTrustAddStarted = $true
    $journal.phase = "machine_trust_add_started"
    Write-JsonAtomically -Value $journal -Path $journalPath
    Invoke-ElevatedTrustAction -Action Add -HelperPath $localTrustHelper `
        -CertificatePath $localCertificate -ResultPath $trustAddResult -OwnershipToken $runId | Out-Null
    $journal.phase = "machine_trust_added"
    Write-JsonAtomically -Value $journal -Path $journalPath

    & $localHarness -BaseMsixPath $localBase -UpgradeMsixPath $localUpgrade `
        -EvidencePath $preflightPath -PreflightOnly
    $preflight = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json
    if ($preflight.status -cne "passed" -or -not $preflight.preflight.baseSignatureTrustedForAppx -or
        -not $preflight.preflight.upgradeSignatureTrustedForAppx -or
        -not $preflight.preflight.packageRegistrationAbsent -or
        -not $preflight.preflight.currentProcessAbsent)
    {
        throw "Temporary machine trust did not satisfy the non-mutating package preflight."
    }

    & $localHarness -BaseMsixPath $localBase -UpgradeMsixPath $localUpgrade `
        -EvidencePath $harnessEvidencePath -AllowExistingDataRootForLocalSmoke
    $harnessEvidence = Get-Content -LiteralPath $harnessEvidencePath -Raw | ConvertFrom-Json
    if ($harnessEvidence.status -cne "passed" -or
        $harnessEvidence.mode -cne "local_existing_profile_lifecycle" -or
        -not $harnessEvidence.preflight.localExistingProfileEligible -or
        -not $harnessEvidence.lifecycle.downgradeRejected -or
        -not $harnessEvidence.lifecycle.packageAbsentAfterUninstall -or
        $harnessEvidence.final.packageRegistered -or $harnessEvidence.final.ownedProcessesRunning -ne 0)
    {
        throw "The existing-profile package lifecycle failed its exact evidence contract."
    }
    $evidence.packageLifecyclePassed = $true
}
catch { $capturedFailure = $_ }
finally
{
    foreach ($lease in $leases) { $lease.Dispose() }
    try { Remove-ExactDevelopmentPackage }
    catch { $evidence.cleanupFailures.Add("package_cleanup_failed"); if ($null -eq $capturedFailure) { $capturedFailure = $_ } }
    try
    {
        if ($journal.machineTrustAddStarted -and @(Get-LocalMachineTrustedCertificates).Count -ne 0)
        {
            Invoke-ElevatedTrustAction -Action Remove -HelperPath $localTrustHelper `
                -CertificatePath $localCertificate -ResultPath $trustRemoveResult -OwnershipToken $runId | Out-Null
        }
    }
    catch { $evidence.cleanupFailures.Add("machine_trust_cleanup_failed"); if ($null -eq $capturedFailure) { $capturedFailure = $_ } }
    if (@(Get-Process -Name "IsTranscribe.Desktop", "isTranscribe", "IsTranscribe.App" -ErrorAction SilentlyContinue).Count -eq 0)
    {
        Remove-OwnedLifecycleSentinels -Root $canonicalRoot
    }

    try
    {
        $evidence.contentBackupRetained = Test-Path -LiteralPath $dataBackup -PathType Container
        if ($evidence.contentBackupRetained)
        {
            $backupAfterCleanup = Get-DataManifest -Root $dataBackup -HashContent
            $evidence.contentBackupVerifiedAfterCleanup =
                (Get-ContentDigest -Manifest $backupAfterCleanup) -ceq $backupContentDigest -and
                @($backupAfterCleanup.files).Count -eq @($backupManifest.files).Count
        }
    }
    catch
    {
        $evidence.cleanupFailures.Add("backup_integrity_check_failed")
        if ($null -eq $capturedFailure) { $capturedFailure = $_ }
    }
    try
    {
        $after = Get-DataManifest -Root $canonicalRoot
        $afterPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in @($after.files)) { [void]$afterPaths.Add([string]$file.relativePath) }
        $missing = @($baseline.files | Where-Object {
                [string]$_.relativePath -notin $ephemeralDataRelativePaths -and
                -not $afterPaths.Contains([string]$_.relativePath)
            })
        $evidence.baselineFilesPreserved = $after.rootPresent -and $missing.Count -eq 0
        $evidence.rootAclPreserved = $after.rootAclSddl -ceq $baseline.rootAclSddl
        $settingsAfter = Get-SettingsSnapshot -Path $settingsPath
        $evidence.settingsReadable = $settingsAfter.present -and $settingsAfter.parsed -and
            @($settingsBaseline.properties | Where-Object { $_ -notin $settingsAfter.properties }).Count -eq 0
        $evidence.secretsUnchanged = (Test-Path -LiteralPath $secretsPath -PathType Leaf) -and
            (Get-FileHash -LiteralPath $secretsPath -Algorithm SHA256).Hash -ceq $secretsBaselineHash
        $databaseAfter = Get-SqliteSnapshot -Path $databasePath
        $evidence.sqliteIntegrityPassed = $databaseAfter.present -and $databaseAfter.integrity -ceq "ok" -and
            $databaseAfter.userVersion -ge $databaseBaseline.userVersion
        $afterTables = @{}
        foreach ($table in @($databaseAfter.tables)) { $afterTables[[string]$table.name] = [int64]$table.rowCount }
        $evidence.sqliteBaselineRowsPreserved = @($databaseBaseline.tables | Where-Object {
                -not $afterTables.ContainsKey([string]$_.name) -or
                $afterTables[[string]$_.name] -lt [int64]$_.rowCount
            }).Count -eq 0
        $evidence.documentsMetadataUnchanged =
            (Get-ManifestDigest -Manifest (Get-DocumentsManifest -Root $documentsRoot)) -ceq
            (Get-ManifestDigest -Manifest $documentsBaseline)
    }
    catch
    {
        $evidence.cleanupFailures.Add("data_integrity_check_failed")
        if ($null -eq $capturedFailure) { $capturedFailure = $_ }
    }
    $evidence.packageAbsentAfterCleanup = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue).Count -eq 0
    $evidence.processesAbsentAfterCleanup = @(
        Get-Process -Name "IsTranscribe.Desktop", "isTranscribe", "IsTranscribe.App" -ErrorAction SilentlyContinue).Count -eq 0
    $evidence.machineTrustRemoved = @(Get-LocalMachineTrustedCertificates).Count -eq 0
    $buildAfter = Get-Item -LiteralPath "Cert:\CurrentUser\My\$expectedThumbprint" -ErrorAction SilentlyContinue
    $evidence.buildCertificatePreserved = $null -ne $buildAfter -and $buildAfter.HasPrivateKey -and
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buildAfter.RawData)) -ceq $buildCertificateRawHash

    if (-not $evidence.contentBackupRetained -or -not $evidence.contentBackupVerifiedAfterCleanup -or
        -not $evidence.baselineFilesPreserved -or -not $evidence.rootAclPreserved -or
        -not $evidence.settingsReadable -or -not $evidence.secretsUnchanged -or
        -not $evidence.sqliteIntegrityPassed -or -not $evidence.sqliteBaselineRowsPreserved -or
        -not $evidence.documentsMetadataUnchanged -or -not $evidence.packageAbsentAfterCleanup -or
        -not $evidence.processesAbsentAfterCleanup -or -not $evidence.machineTrustRemoved -or
        -not $evidence.buildCertificatePreserved)
    {
        if ($null -eq $capturedFailure)
        {
            $capturedFailure = [System.Management.Automation.ErrorRecord]::new(
                [InvalidOperationException]::new("Local lifecycle cleanup did not restore every required invariant."),
                "INFRA005_LOCAL_CLEANUP", [System.Management.Automation.ErrorCategory]::InvalidResult, $null)
        }
    }

    $evidence.status = if ($null -eq $capturedFailure) { "passed" } else { "failed" }
    $evidence.completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    if ($null -ne $capturedFailure)
    {
        $evidence.failure = [ordered]@{
            exceptionType = $capturedFailure.Exception.GetType().FullName
            hresult = $capturedFailure.Exception.HResult
        }
    }
    Write-JsonAtomically -Value $evidence -Path $evidenceFullPath
    $journal.phase = if ($null -eq $capturedFailure) { "completed" } else { "requires_recovery" }
    $journal.completed = $null -eq $capturedFailure
    Write-JsonAtomically -Value $journal -Path $journalPath

}

if ($null -ne $capturedFailure)
{
    throw "Local existing-profile lifecycle failed; review evidence and the recovery journal."
}
Write-Output $evidenceFullPath
