#Requires -Version 7.0
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Add", "Remove")]
    [string]$Action,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$OwnershipToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
$expectedCertificateHash = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC"
$expectedThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$expectedSubject = "CN=isTranscribe Development"

function Test-PathInsideOrEqual
{
    param([string]$Path, [string]$Parent)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
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
                throw "Elevated trust paths must not traverse reparse points."
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Write-JsonDurablyInPlace
{
    param([object]$Value, [string]$Path)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 6))
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write,
        [IO.FileShare]::ReadWrite, 4096, [IO.FileOptions]::WriteThrough)
    try
    {
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

$transactionRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$certificateFullPath = [IO.Path]::GetFullPath($CertificatePath)
$resultFullPath = [IO.Path]::GetFullPath($ResultPath)
foreach ($path in @($transactionRoot, $certificateFullPath, $resultFullPath))
{
    if (-not (Test-PathInsideOrEqual -Path $path -Parent $transactionRoot))
    {
        throw "Elevated trust inputs and results must stay inside the exact transaction directory."
    }
    Assert-NoReparsePointInPath -Path $path
}
$expectedCertificatePath = Join-Path $transactionRoot "development.cer"
$intentPath = Join-Path $transactionRoot "trust-add.intent.json"
if (-not [string]::Equals($certificateFullPath, $expectedCertificatePath, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Elevated trust inputs must use the exact transaction certificate filename."
}
$resultName = [IO.Path]::GetFileName($resultFullPath)
if (($Action -ceq "Add" -and $resultName -cne "trust-add.json") -or
    ($Action -ceq "Remove" -and $resultName -cnotmatch '^trust-remove-[0-9a-f]{32}\.json$'))
{
    throw "Elevated trust results must use the exact action-specific transaction filename contract."
}
if (-not (Test-Path -LiteralPath $resultFullPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $intentPath -PathType Leaf))
{
    throw "Elevated trust result and intent files must be precreated by the medium-token orchestrator."
}
Assert-NoReparsePointInPath -Path $intentPath

if ((Get-FileHash -LiteralPath $certificateFullPath -Algorithm SHA256).Hash -cne $expectedCertificateHash)
{
    throw "The elevated helper received an unreviewed certificate."
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFullPath)
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    "TrustedPeople", [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
$result = [ordered]@{
    schemaVersion = "infra-005-local-machine-trust-v1"
    ownershipToken = $OwnershipToken
    action = $Action
    status = "running"
    phase = "initialized"
    trustPreexisted = $false
    thumbprint = $expectedThumbprint
    subject = $expectedSubject
    privateKeyImported = $false
    certificateAddedByRun = $false
}
$addedByThisProcess = $false
try
{
    if ($certificate.Thumbprint -cne $expectedThumbprint -or $certificate.Subject -cne $expectedSubject -or
        $certificate.HasPrivateKey)
    {
        throw "The elevated helper accepts only the exact public development certificate."
    }
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $matches = @($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint)
    if ($Action -ceq "Add")
    {
        if ($matches.Count -ne 0) { throw "The exact machine-trust certificate already exists." }
        $result.phase = "add_started"
        Write-JsonDurablyInPlace -Value $result -Path $intentPath
        $store.Add($certificate)
        $addedByThisProcess = $true
        $result.certificateAddedByRun = $true
        $matches = @($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint)
        if ($matches.Count -ne 1 -or $matches[0].Subject -cne $expectedSubject -or
            $matches[0].HasPrivateKey -or
            -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($matches[0].RawData, $certificate.RawData))
        {
            throw "The machine-trust add postcondition failed."
        }
        $result.phase = "added"
    }
    else
    {
        $receipt = Get-Content -LiteralPath $intentPath -Raw | ConvertFrom-Json
        if ($receipt.schemaVersion -cne $result.schemaVersion -or
            $receipt.ownershipToken -cne $OwnershipToken -or
            $receipt.action -cne "Add" -or $receipt.trustPreexisted -or
            $receipt.phase -cne "add_started" -or $receipt.status -cne "running" -or
            $receipt.thumbprint -cne $expectedThumbprint -or
            $receipt.subject -cne $expectedSubject -or $receipt.privateKeyImported -or
            $receipt.certificateAddedByRun)
        {
            throw "The machine-trust add receipt does not prove cleanup ownership."
        }
        if ($matches.Count -gt 0)
        {
            if ($matches.Count -ne 1 -or $matches[0].Subject -cne $expectedSubject -or
                $matches[0].HasPrivateKey -or
                -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($matches[0].RawData, $certificate.RawData))
            {
                throw "Machine-trust cleanup found an entry outside the owned public certificate."
            }
            $store.Remove($matches[0])
        }
        if (@($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint).Count -ne 0)
        {
            throw "The machine-trust certificate remains after exact cleanup."
        }
        $result.phase = "removed"
        $result.certificateAddedByRun = $false
    }
    $result.status = "passed"
}
catch
{
    if ($Action -ceq "Add" -and $addedByThisProcess)
    {
        try
        {
            foreach ($match in @($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint))
            {
                if ($match.Subject -ceq $expectedSubject -and -not $match.HasPrivateKey -and
                    [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($match.RawData, $certificate.RawData))
                {
                    $store.Remove($match)
                }
            }
        }
        catch { }
        $result.certificateAddedByRun =
            @($store.Certificates | Where-Object Thumbprint -CEQ $expectedThumbprint).Count -ne 0
    }
    $result.status = "failed"
    $result.exceptionType = $_.Exception.GetType().FullName
    $result.hresult = $_.Exception.HResult
    throw
}
finally
{
    try { Write-JsonDurablyInPlace -Value $result -Path $resultFullPath } catch { }
    $store.Dispose()
    $certificate.Dispose()
}
