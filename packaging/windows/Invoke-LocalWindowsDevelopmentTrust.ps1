#Requires -Version 7.0
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Add", "Remove")]
    [string]$Action,
    [Parameter(Mandatory)]
    [string]$CertificatePath,
    [Parameter(Mandatory)]
    [string]$ResultPath,
    [Parameter(Mandatory)]
    [string]$RunId,
    [Parameter(Mandatory)]
    [string]$OwnershipToken,
    [string]$AddProofPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing
# This intentionally narrow elevated boundary can mutate only one pinned public
# development certificate in LocalMachine/TrustedPeople.
$ExpectedCertificateSha256 = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC"
$ExpectedThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$ExpectedSubject = "CN=isTranscribe Development"
$StorePath = "Cert:\LocalMachine\TrustedPeople"

function Publish-JsonAtomically
{
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container))
    {
        throw "The trust-helper result directory must already exist."
    }
    $temporary = Join-Path $directory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($fullPath)), [guid]::NewGuid())
    try
    {
        [IO.File]::WriteAllText(
            $temporary,
            ($Value | ConvertTo-Json -Depth 6),
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $fullPath)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporary -PathType Leaf)
        {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

if ($RunId -notmatch '^[0-9a-f]{32}$' -or $OwnershipToken -notmatch '^[0-9a-f]{64}$')
{
    throw "The trust-helper ownership identity is invalid."
}
if ((Get-FileHash -LiteralPath $CertificatePath -Algorithm SHA256).Hash -cne $ExpectedCertificateSha256)
{
    throw "The development certificate file hash does not match the reviewed artifact."
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
try
{
    if ($certificate.Thumbprint -cne $ExpectedThumbprint -or
        $certificate.Subject -cne $ExpectedSubject -or
        $certificate.HasPrivateKey)
    {
        throw "The development certificate identity or private-key boundary is invalid."
    }
}
finally
{
    $certificate.Dispose()
}

$result = [ordered]@{
    schemaVersion = "infra-005-local-trust-v1"
    status = "failed"
    action = $Action
    runId = $RunId
    ownershipToken = $OwnershipToken
    certificateSha256 = $ExpectedCertificateSha256
    certificateThumbprint = $ExpectedThumbprint
    certificateSubject = $ExpectedSubject
    store = "LocalMachine/TrustedPeople"
    changed = $false
    completedUtc = $null
}

try
{
    $existing = Get-Item -LiteralPath "$StorePath\$ExpectedThumbprint" -ErrorAction SilentlyContinue
    if ($Action -ceq "Add")
    {
        if ($null -ne $existing)
        {
            throw "The exact development certificate already exists in LocalMachine/TrustedPeople."
        }
        $import = Import-Certificate -FilePath $CertificatePath -CertStoreLocation $StorePath
        if ($null -eq $import -or $import.Thumbprint -cne $ExpectedThumbprint)
        {
            throw "The development certificate import did not return the pinned identity."
        }
        $after = Get-Item -LiteralPath "$StorePath\$ExpectedThumbprint" -ErrorAction Stop
        if ($after.Subject -cne $ExpectedSubject -or $after.HasPrivateKey)
        {
            throw "The imported development trust entry failed post-verification."
        }
        $result.changed = $true
    }
    else
    {
        if ([string]::IsNullOrWhiteSpace($AddProofPath) -or
            -not (Test-Path -LiteralPath $AddProofPath -PathType Leaf))
        {
            throw "Removal requires the exact successful Add proof."
        }
        $proof = Get-Content -LiteralPath $AddProofPath -Raw | ConvertFrom-Json
        if ($proof.schemaVersion -cne $result.schemaVersion -or
            $proof.status -cne "passed" -or $proof.action -cne "Add" -or
            -not [bool]$proof.changed -or $proof.runId -cne $RunId -or
            $proof.ownershipToken -cne $OwnershipToken -or
            $proof.certificateThumbprint -cne $ExpectedThumbprint)
        {
            throw "The Add proof does not establish ownership of this trust entry."
        }
        if ($null -ne $existing)
        {
            if ($existing.Subject -cne $ExpectedSubject -or $existing.HasPrivateKey)
            {
                throw "The current trust entry no longer matches the owned public certificate."
            }
            Remove-Item -LiteralPath "$StorePath\$ExpectedThumbprint" -Force
            $result.changed = $true
        }
        if ($null -ne (Get-Item -LiteralPath "$StorePath\$ExpectedThumbprint" -ErrorAction SilentlyContinue))
        {
            throw "The owned development trust entry remained after removal."
        }
    }
    $result.status = "passed"
}
finally
{
    $result.completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Publish-JsonAtomically -Value $result -Path $ResultPath
}

