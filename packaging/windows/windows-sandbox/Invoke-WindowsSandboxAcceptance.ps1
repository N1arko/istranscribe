[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
$expectedBaseSha256 = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B"
$expectedUpgradeSha256 = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3"
$expectedHarnessSha256 = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$expectedCertificateThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$packagesRoot = "C:\isTranscribeAcceptance\packages"
$toolsRoot = "C:\isTranscribeAcceptance\tools"
$evidenceRoot = "C:\isTranscribeAcceptance\evidence"
$localRoot = "C:\isTranscribeAcceptance\local"

function Assert-File
{
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Required Sandbox acceptance input is missing: $Path"
    }
}

function Assert-FileHash
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Sandbox acceptance input hash mismatch: $Path"
    }
}

function Publish-Evidence
{
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf))
    {
        return
    }

    $destination = Join-Path $evidenceRoot $Name
    if (Test-Path -LiteralPath $destination)
    {
        throw "Sandbox evidence already exists; start a new disposable run with an empty staging directory: $destination"
    }

    $temporary = Join-Path $evidenceRoot (".{0}.{1}.tmp" -f $Name, [guid]::NewGuid())
    try
    {
        Copy-Item -LiteralPath $Source -Destination $temporary
        [IO.File]::Move($temporary, $destination)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporary)
        {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Invoke-HarnessPass
{
    param(
        [Parameter(Mandatory)][string]$HarnessPath,
        [Parameter(Mandatory)][string]$BaseMsixPath,
        [Parameter(Mandatory)][string]$UpgradeMsixPath,
        [Parameter(Mandatory)][string]$LocalEvidencePath,
        [Parameter(Mandatory)][string]$PublishedEvidenceName,
        [switch]$PreflightOnly
    )

    $capturedFailure = $null
    try
    {
        $arguments = @{
            BaseMsixPath = $BaseMsixPath
            UpgradeMsixPath = $UpgradeMsixPath
            EvidencePath = $LocalEvidencePath
            OperationTimeoutSeconds = 120
        }
        if ($PreflightOnly)
        {
            $arguments.PreflightOnly = $true
        }

        & $HarnessPath @arguments | Write-Output
    }
    catch
    {
        $capturedFailure = $_
    }
    finally
    {
        Publish-Evidence -Source $LocalEvidencePath -Name $PublishedEvidenceName
    }

    if ($null -ne $capturedFailure)
    {
        throw $capturedFailure
    }

    $evidence = Get-Content -LiteralPath $LocalEvidencePath -Raw | ConvertFrom-Json
    if (-not [string]::Equals($evidence.status, "passed", [StringComparison]::Ordinal))
    {
        throw "Sandbox acceptance pass did not produce passed evidence: $PublishedEvidenceName"
    }
}

$sourceBaseMsix = Join-Path $packagesRoot "2.0.0\isTranscribe-2.0.0-dev-win-x64.msix"
$sourceUpgradeMsix = Join-Path $packagesRoot "2.0.1\isTranscribe-2.0.1-dev-win-x64.msix"
$sourceCertificate = Join-Path $packagesRoot "2.0.1\isTranscribe-development-certificate.cer"
$sourceHarness = Join-Path $toolsRoot "Test-WindowsReleaseLifecycle.ps1"
foreach ($source in @($sourceBaseMsix, $sourceUpgradeMsix, $sourceCertificate, $sourceHarness))
{
    Assert-File -Path $source
}
if (-not (Test-Path -LiteralPath $evidenceRoot -PathType Container))
{
    throw "The dedicated writable evidence mapping is unavailable."
}
if (@(Get-ChildItem -LiteralPath $evidenceRoot -Force).Count -ne 0)
{
    throw "The dedicated writable evidence mapping must be empty at the start of the run."
}
if (Test-Path -LiteralPath $localRoot)
{
    throw "The Sandbox-local working directory already exists; start a new disposable Sandbox."
}

[void](Assert-FileHash -Path $sourceBaseMsix -ExpectedSha256 $expectedBaseSha256)
[void](Assert-FileHash -Path $sourceUpgradeMsix -ExpectedSha256 $expectedUpgradeSha256)
[void](Assert-FileHash -Path $sourceHarness -ExpectedSha256 $expectedHarnessSha256)
$sourcePublicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($sourceCertificate)
try
{
    if ($sourcePublicCertificate.HasPrivateKey -or
        -not [string]::Equals(
            $sourcePublicCertificate.Thumbprint,
            $expectedCertificateThumbprint,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The mapped development certificate does not match the public test certificate contract."
    }
}
finally
{
    $sourcePublicCertificate.Dispose()
}
foreach ($packagePath in @($sourceBaseMsix, $sourceUpgradeMsix))
{
    $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
    if ($null -eq $signature.SignerCertificate -or
        -not [string]::Equals(
            $signature.SignerCertificate.Thumbprint,
            $expectedCertificateThumbprint,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "An MSIX signer does not match the expected development certificate: $packagePath"
    }
}

$trustedCertificate = Get-ChildItem `
    -LiteralPath "Cert:\LocalMachine\TrustedPeople\$expectedCertificateThumbprint" `
    -ErrorAction SilentlyContinue
if ($null -eq $trustedCertificate)
{
    throw "Manually trust the development certificate in Local Machine / Trusted People inside this disposable Sandbox, then rerun this command in the same Sandbox."
}

[void][IO.Directory]::CreateDirectory($localRoot)
$localBaseMsix = Join-Path $localRoot "isTranscribe-2.0.0-dev-win-x64.msix"
$localUpgradeMsix = Join-Path $localRoot "isTranscribe-2.0.1-dev-win-x64.msix"
$localCertificate = Join-Path $localRoot "isTranscribe-development-certificate.cer"
$localHarness = Join-Path $localRoot "Test-WindowsReleaseLifecycle.ps1"
Copy-Item -LiteralPath $sourceBaseMsix -Destination $localBaseMsix
Copy-Item -LiteralPath $sourceUpgradeMsix -Destination $localUpgradeMsix
Copy-Item -LiteralPath $sourceCertificate -Destination $localCertificate
Copy-Item -LiteralPath $sourceHarness -Destination $localHarness

Assert-FileHash -Path $localBaseMsix -ExpectedSha256 $expectedBaseSha256
Assert-FileHash -Path $localUpgradeMsix -ExpectedSha256 $expectedUpgradeSha256
Assert-FileHash -Path $localHarness -ExpectedSha256 $expectedHarnessSha256
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($localCertificate)
try
{
    if ($certificate.HasPrivateKey -or
        -not [string]::Equals(
            $certificate.Thumbprint,
            $expectedCertificateThumbprint,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The mapped development certificate does not match the public test certificate contract."
    }
}
finally
{
    $certificate.Dispose()
}
$preflightEvidence = Join-Path $localRoot "preflight.json"
Invoke-HarnessPass `
    -HarnessPath $localHarness `
    -BaseMsixPath $localBaseMsix `
    -UpgradeMsixPath $localUpgradeMsix `
    -LocalEvidencePath $preflightEvidence `
    -PublishedEvidenceName "windows-sandbox-preflight.json" `
    -PreflightOnly

$lifecycleEvidence = Join-Path $localRoot "lifecycle.json"
Invoke-HarnessPass `
    -HarnessPath $localHarness `
    -BaseMsixPath $localBaseMsix `
    -UpgradeMsixPath $localUpgradeMsix `
    -LocalEvidencePath $lifecycleEvidence `
    -PublishedEvidenceName "windows-sandbox-lifecycle.json"

Write-Output "Sandbox installer lifecycle acceptance passed. Close Windows Sandbox to discard its package and temporary certificate trust."
