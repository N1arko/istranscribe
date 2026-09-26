#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$KitDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
$expectedBaseHash = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B"
$expectedUpgradeHash = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3"
$expectedCertificateHash = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC"
$expectedCertificateThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$expectedHarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$expectedPublisher = "CN=isTranscribe Development"
$expectedIdentity = "isTranscribe.Desktop"
$expectedApplicationId = "App"
$expectedExecutable = "IsTranscribe.Desktop.exe"

$requiredFiles = @(
    "README-FIRST.txt",
    "TEST_ONLY_DO_NOT_DISTRIBUTE.txt",
    "acceptance-kit-manifest.json",
    "SHA256SUMS.txt",
    "Run-Clean-VM-Preflight.cmd",
    "Run-Clean-VM-Lifecycle.cmd",
    "Run-Windows-Sandbox.cmd",
    "packages/2.0.0/dependency-license-inventory.json",
    "packages/2.0.0/DEVELOPMENT_ONLY.txt",
    "packages/2.0.0/isTranscribe-2.0.0-dev-win-x64.msix",
    "packages/2.0.0/isTranscribe-development-certificate.cer",
    "packages/2.0.0/release-manifest.json",
    "packages/2.0.0/SHA256SUMS.txt",
    "packages/2.0.1/dependency-license-inventory.json",
    "packages/2.0.1/DEVELOPMENT_ONLY.txt",
    "packages/2.0.1/isTranscribe-2.0.1-dev-win-x64.msix",
    "packages/2.0.1/isTranscribe-development-certificate.cer",
    "packages/2.0.1/release-manifest.json",
    "packages/2.0.1/SHA256SUMS.txt",
    "tools/Test-AcceptanceKit.ps1",
    "tools/Invoke-AcceptanceKit.ps1",
    "tools/New-WindowsSandboxConfiguration.ps1",
    "tools/Test-WindowsReleaseLifecycle.ps1",
    "tools/sandbox/Run-In-Sandbox.cmd",
    "docs/CLEAN-VM-CHECKLIST.md",
    "docs/COVERAGE.md"
)

function Assert-LocalAbsolutePath
{
    param([Parameter(Mandatory)][string]$Path)

    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith("\\", [StringComparison]::Ordinal))
    {
        throw "Acceptance-kit paths must be absolute local paths."
    }
}

function Assert-NoReparsePointInPath
{
    param([Parameter(Mandatory)][string]$Path)

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

function Assert-SafeRelativePath
{
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains("\") -or
        $Path.Contains(":") -or
        $Path.StartsWith("/", [StringComparison]::Ordinal) -or
        $Path.EndsWith("/", [StringComparison]::Ordinal))
    {
        throw "Unsafe acceptance-kit relative path: '$Path'."
    }
    foreach ($segment in $Path.Split('/'))
    {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq "." -or $segment -eq ".." -or
            $segment.EndsWith(".", [StringComparison]::Ordinal) -or
            $segment.EndsWith(" ", [StringComparison]::Ordinal))
        {
            throw "Unsafe acceptance-kit path segment in '$Path'."
        }
    }
}

function Get-ImmutableKitFiles
{
    param([Parameter(Mandatory)][string]$Root)

    $rootWithSeparator = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    $result = [Collections.Generic.List[object]]::new()
    while ($pending.Count -gt 0)
    {
        $directory = $pending.Pop()
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($directory))
        {
            $entry = Get-Item -LiteralPath $entryPath -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            {
                throw "Acceptance kit contains a reparse point: $entryPath"
            }
            if ($entry.PSIsContainer)
            {
                $pending.Push($entry.FullName)
                continue
            }
            $relative = $entry.FullName.Substring($rootWithSeparator.Length).Replace('\', '/')
            Assert-SafeRelativePath -Path $relative
            $result.Add([pscustomobject]@{ RelativePath = $relative; FullPath = $entry.FullName; Length = $entry.Length })
        }
    }
    return @($result)
}

function Assert-ExactPathSet
{
    param(
        [Parameter(Mandatory)][string[]]$Actual,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Actual)
    {
        if (-not $actualSet.Add($path)) { throw "$Label contains a duplicate or case-colliding path: $path" }
    }
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Expected) { [void]$expectedSet.Add($path) }
    if (-not $actualSet.SetEquals($expectedSet))
    {
        throw "$Label does not match the exact acceptance-kit allowlist."
    }
}

function Read-MsixMetadata
{
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try
    {
        $entry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $entry) { throw "MSIX has no AppxManifest.xml: $Path" }
        $stream = $entry.Open()
        try
        {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try
            {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }

    $identity = $document.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $application = $document.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    if ($null -eq $identity -or $null -eq $application) { throw "MSIX manifest identity/application is missing." }
    return [pscustomobject]@{
        Name = $identity.GetAttribute("Name")
        Publisher = $identity.GetAttribute("Publisher")
        Version = $identity.GetAttribute("Version")
        Architecture = $identity.GetAttribute("ProcessorArchitecture")
        ApplicationId = $application.GetAttribute("Id")
        Executable = $application.GetAttribute("Executable")
    }
}

$kitRoot = [IO.Path]::GetFullPath($KitDirectory)
Assert-LocalAbsolutePath -Path $kitRoot
Assert-NoReparsePointInPath -Path $kitRoot
if (-not (Test-Path -LiteralPath $kitRoot -PathType Container)) { throw "Acceptance kit directory is missing." }

$files = @(Get-ImmutableKitFiles -Root $kitRoot)
Assert-ExactPathSet -Actual @($files.RelativePath) -Expected $requiredFiles -Label "Acceptance kit"
foreach ($file in $files)
{
    $extension = [IO.Path]::GetExtension($file.RelativePath)
    if ($extension -in @(".pfx", ".p12", ".p7b", ".pem", ".key"))
    {
        throw "Acceptance kit contains forbidden private-key material: $($file.RelativePath)"
    }
}

$checksumPath = Join-Path $kitRoot "SHA256SUMS.txt"
$checksumLines = @(Get-Content -LiteralPath $checksumPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$checksumEntries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$checksumOrder = [Collections.Generic.List[string]]::new()
foreach ($line in $checksumLines)
{
    if ($line -notmatch '^(?<hash>[a-f0-9]{64})  (?<path>.+)$') { throw "Invalid root checksum line." }
    $relativePath = $Matches.path
    Assert-SafeRelativePath -Path $relativePath
    if ($checksumEntries.ContainsKey($relativePath)) { throw "Duplicate root checksum path: $relativePath" }
    $checksumEntries.Add($relativePath, $Matches.hash)
    $checksumOrder.Add($relativePath)
}
$expectedChecksummed = @($requiredFiles | Where-Object { $_ -ne "SHA256SUMS.txt" })
Assert-ExactPathSet -Actual @($checksumEntries.Keys) -Expected $expectedChecksummed -Label "Root checksum ledger"
$ordinalSorted = [string[]]@($checksumEntries.Keys)
[Array]::Sort($ordinalSorted, [StringComparer]::Ordinal)
if ($checksumOrder.Count -ne $ordinalSorted.Length)
{
    throw "Root checksum ledger is not ordinal-sorted."
}
for ($index = 0; $index -lt $ordinalSorted.Length; $index++)
{
    if ($checksumOrder[$index] -cne $ordinalSorted[$index]) { throw "Root checksum ledger is not ordinal-sorted." }
}
foreach ($entry in $checksumEntries.GetEnumerator())
{
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $kitRoot $entry.Key.Replace('/', '\')) -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $entry.Value, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Acceptance-kit file hash mismatch: $($entry.Key)"
    }
}

$manifest = Get-Content -LiteralPath (Join-Path $kitRoot "acceptance-kit-manifest.json") -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -cne "infra-005-acceptance-kit-v1" -or
    -not $manifest.classification.testOnly -or $manifest.classification.publicEligible -or
    $manifest.classification.containsPrivateKey -or $manifest.platform.architecture -cne "x64" -or
    [int]$manifest.platform.minimumWindowsBuild -ne 19041 -or
    $manifest.identity.name -cne $expectedIdentity -or $manifest.identity.applicationId -cne $expectedApplicationId -or
    $manifest.identity.publisher -cne $expectedPublisher -or
    $manifest.identity.signerThumbprint -cne $expectedCertificateThumbprint -or
    $manifest.sourceRevision -cne "infra-008-local-lifecycle")
{
    throw "Acceptance-kit manifest classification/platform/identity contract failed."
}
if ($manifest.base.semanticVersion -cne "2.0.0" -or $manifest.base.msixVersion -cne "2.0.0.65535" -or
    $manifest.base.sha256 -cne $expectedBaseHash.ToLowerInvariant() -or
    $manifest.upgrade.semanticVersion -cne "2.0.1" -or $manifest.upgrade.msixVersion -cne "2.0.1.65535" -or
    $manifest.upgrade.sha256 -cne $expectedUpgradeHash.ToLowerInvariant() -or
    $manifest.certificate.sha256 -cne $expectedCertificateHash.ToLowerInvariant() -or
    $manifest.certificate.thumbprint -cne $expectedCertificateThumbprint -or
    $manifest.certificate.hasPrivateKey -or
    $manifest.harness.sha256 -cne $expectedHarnessHash.ToLowerInvariant() -or
    [int]$manifest.harness.evidenceSchemaVersion -ne 2)
{
    throw "Acceptance-kit pinned input contract failed."
}
$manifestPayloadPaths = @($manifest.files | ForEach-Object { [string]$_.relativePath })
$expectedManifestPayload = @($requiredFiles | Where-Object { $_ -notin @("acceptance-kit-manifest.json", "SHA256SUMS.txt") })
Assert-ExactPathSet -Actual $manifestPayloadPaths -Expected $expectedManifestPayload -Label "Manifest file ledger"
foreach ($manifestFile in $manifest.files)
{
    Assert-SafeRelativePath -Path $manifestFile.relativePath
    $actualFile = $files | Where-Object RelativePath -CEQ $manifestFile.relativePath
    if ($null -eq $actualFile -or [long]$manifestFile.sizeBytes -ne $actualFile.Length -or
        $manifestFile.sha256 -cne ((Get-FileHash -LiteralPath $actualFile.FullPath -Algorithm SHA256).Hash.ToLowerInvariant()))
    {
        throw "Manifest file ledger mismatch: $($manifestFile.relativePath)"
    }
}

$baseMsix = Join-Path $kitRoot "packages\2.0.0\isTranscribe-2.0.0-dev-win-x64.msix"
$upgradeMsix = Join-Path $kitRoot "packages\2.0.1\isTranscribe-2.0.1-dev-win-x64.msix"
$baseMetadata = Read-MsixMetadata -Path $baseMsix
$upgradeMetadata = Read-MsixMetadata -Path $upgradeMsix
if ($baseMetadata.Name -cne $expectedIdentity -or $upgradeMetadata.Name -cne $expectedIdentity -or
    $baseMetadata.Publisher -cne $expectedPublisher -or $upgradeMetadata.Publisher -cne $expectedPublisher -or
    $baseMetadata.Version -cne "2.0.0.65535" -or $upgradeMetadata.Version -cne "2.0.1.65535" -or
    $baseMetadata.Architecture -cne "x64" -or $upgradeMetadata.Architecture -cne "x64" -or
    $baseMetadata.ApplicationId -cne $expectedApplicationId -or $upgradeMetadata.ApplicationId -cne $expectedApplicationId -or
    $baseMetadata.Executable -cne $expectedExecutable -or $upgradeMetadata.Executable -cne $expectedExecutable)
{
    throw "MSIX manifest contract failed."
}
foreach ($msix in @($baseMsix, $upgradeMsix))
{
    $signature = Get-AuthenticodeSignature -LiteralPath $msix
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -cne $expectedCertificateThumbprint -or
        $signature.SignerCertificate.Subject -cne $expectedPublisher)
    {
        throw "MSIX signer contract failed: $msix"
    }
}
foreach ($version in @("2.0.0", "2.0.1"))
{
    $releaseManifest = Get-Content -LiteralPath (Join-Path $kitRoot "packages\$version\release-manifest.json") -Raw | ConvertFrom-Json
    $expectedSourceRevision = if ($version -ceq "2.0.0") { "infra-008-local-lifecycle-base" } else { "infra-008-local-lifecycle-upgrade" }
    if ($releaseManifest.signing.mode -cne "development" -or $releaseManifest.signing.publicEligible -or
        $releaseManifest.signing.trustStatus -cne "DevelopmentUntrustedRoot" -or
        $releaseManifest.build.sourceRevision -cne $expectedSourceRevision -or
        $releaseManifest.runtimeIdentifier -cne "win-x64" -or $releaseManifest.architecture -cne "x64")
    {
        throw "Source release manifest is not an exact development-only win-x64 artifact: $version"
    }
}

foreach ($certificatePath in @(
    (Join-Path $kitRoot "packages\2.0.0\isTranscribe-development-certificate.cer"),
    (Join-Path $kitRoot "packages\2.0.1\isTranscribe-development-certificate.cer")))
{
    if ((Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash -cne $expectedCertificateHash)
    {
        throw "Development certificate hash mismatch."
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try
    {
        if ($certificate.HasPrivateKey -or $certificate.Thumbprint -cne $expectedCertificateThumbprint -or
            $certificate.Subject -cne $expectedPublisher)
        {
            throw "Development certificate identity/private-key contract failed."
        }
        $hasCodeSigning = $false
        $hasDigitalSignature = $false
        foreach ($extension in $certificate.Extensions)
        {
            if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension])
            {
                foreach ($oid in $extension.EnhancedKeyUsages)
                {
                    if ($oid.Value -ceq "1.3.6.1.5.5.7.3.3") { $hasCodeSigning = $true }
                }
            }
            if ($extension -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension] -and
                ($extension.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ne 0)
            {
                $hasDigitalSignature = $true
            }
            if ($extension -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] -and
                $extension.CertificateAuthority)
            {
                throw "Development certificate must not be a certificate authority."
            }
        }
        if (-not $hasCodeSigning -or -not $hasDigitalSignature) { throw "Development certificate usage contract failed." }
    }
    finally { $certificate.Dispose() }
}

if ((Get-FileHash -LiteralPath (Join-Path $kitRoot "tools\Test-WindowsReleaseLifecycle.ps1") -Algorithm SHA256).Hash -cne $expectedHarnessHash)
{
    throw "Lifecycle harness hash mismatch."
}

[ordered]@{
    schemaVersion = "infra-005-acceptance-kit-verification-v1"
    status = "passed"
    kitDirectory = $kitRoot
    fileCount = $files.Count
    sourceRevision = $manifest.sourceRevision
    testOnly = $true
    publicEligible = $false
    baseSha256 = $expectedBaseHash.ToLowerInvariant()
    upgradeSha256 = $expectedUpgradeHash.ToLowerInvariant()
    signerThumbprint = $expectedCertificateThumbprint
    harnessSha256 = $expectedHarnessHash.ToLowerInvariant()
} | ConvertTo-Json -Compress
