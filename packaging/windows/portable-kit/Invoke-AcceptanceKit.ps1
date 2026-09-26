#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("CleanVmPreflight", "CleanVmLifecycle", "SandboxLifecycle")]
    [string]$Mode,
    [string]$KitRoot,
    [string]$EvidenceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
$expectedBaseHash = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B"
$expectedUpgradeHash = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3"
$expectedHarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$expectedCertificateThumbprint = "66075815637CA0D5059B64D57C5E22100B142024"
$expectedPublisher = "CN=isTranscribe Development"
$runStartedUtc = [DateTimeOffset]::UtcNow

function Test-PathInsideOrEqual
{
    param([string]$Path, [string]$ParentPath)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    return [string]::Equals($fullPath, $fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LocalPath
{
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith("\\", [StringComparison]::Ordinal))
    {
        throw "Acceptance runtime requires absolute local paths; UNC/network paths are refused."
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
                throw "Acceptance runtime paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Copy-LockedFile
{
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $destinationCreated = $false
    try
    {
        $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $destinationCreated = $true
        try { $sourceStream.CopyTo($destinationStream); $destinationStream.Flush($true) }
        finally { $destinationStream.Dispose() }
    }
    catch
    {
        if ($destinationCreated -and (Test-Path -LiteralPath $Destination -PathType Leaf))
        {
            Remove-Item -LiteralPath $Destination -Force
        }
        throw
    }
    finally { $sourceStream.Dispose() }
}

function Assert-LocalInput
{
    param([string]$Path, [string]$ExpectedHash, [switch]$Msix)
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $ExpectedHash)
    {
        throw "Acceptance local input hash mismatch: $Path"
    }
    if ($Msix)
    {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -cne $expectedCertificateThumbprint -or
            $signature.SignerCertificate.Subject -cne $expectedPublisher)
        {
            throw "Acceptance local MSIX signer mismatch: $Path"
        }
    }
}

function Get-OpenStreamHash
{
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $originalPosition = $Stream.Position
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace("-", "") }
    finally { $sha.Dispose(); $Stream.Position = $originalPosition }
}

function Publish-FileAtomically
{
    param([string]$Source, [string]$Directory, [string]$Name)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return }
    [void][IO.Directory]::CreateDirectory($Directory)
    Assert-NoReparsePointInPath -Path $Directory
    $destination = Join-Path $Directory $Name
    if (Test-Path -LiteralPath $destination) { throw "Evidence destination already exists: $destination" }
    $temporary = Join-Path $Directory (".{0}.{1}.tmp" -f $Name, [guid]::NewGuid())
    try
    {
        Copy-LockedFile -Source $Source -Destination $temporary
        [IO.File]::Move($temporary, $destination)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Assert-BooleanProperties
{
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string[]]$Names, [string]$Label)
    foreach ($name in $Names)
    {
        if (-not [bool]$Object.$name) { throw "$Label did not prove '$name'." }
    }
}

function Assert-HarnessEvidence
{
    param([string]$Path, [ValidateSet("preflight_only", "lifecycle")][string]$ExpectedMode)
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$evidence.schemaVersion -ne 2 -or $evidence.status -cne "passed" -or
        $evidence.mode -cne $ExpectedMode -or [bool]$evidence.inputs.leaveInstalled -or
        $evidence.inputs.expectedBasePackageVersion -cne "2.0.0.65535" -or
        $evidence.inputs.expectedUpgradePackageVersion -cne "2.0.1.65535" -or
        -not [string]::Equals($evidence.inputs.packages.base.sha256, $expectedBaseHash, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($evidence.inputs.packages.upgrade.sha256, $expectedUpgradeHash, [StringComparison]::OrdinalIgnoreCase) -or
        $evidence.inputs.packages.base.signerThumbprint -cne $expectedCertificateThumbprint -or
        $evidence.inputs.packages.upgrade.signerThumbprint -cne $expectedCertificateThumbprint)
    {
        throw "Lifecycle evidence identity/hash/status contract failed."
    }
    Assert-BooleanProperties -Object $evidence.preflight -Label "Lifecycle preflight" -Names @(
        "windowsX64", "requiredCommandsPresent", "packageFilesValid", "packageRegistrationAbsent",
        "canonicalDataRootAbsent", "legacyRunValueAbsent", "legacyProcessAbsent", "currentProcessAbsent",
        "cleanDisposableProfileEligible", "baseSignatureTrustedForAppx", "upgradeSignatureTrustedForAppx")
    if ($evidence.steps | Where-Object { $_.status -cne "passed" }) { throw "Lifecycle evidence contains a failed/incomplete step." }
    if ([bool]$evidence.final.packageRegistered -or [bool]$evidence.final.sentinelPresent -or
        [int]$evidence.final.ownedProcessesRunning -ne 0 -or
        [bool]$evidence.safety.securitySettingsModified)
    {
        throw "Lifecycle evidence final safety contract failed."
    }
    $started = [DateTimeOffset]::Parse([string]$evidence.startedUtc)
    $completed = [DateTimeOffset]::Parse([string]$evidence.completedUtc)
    if ($started -lt $runStartedUtc.AddMinutes(-1) -or $completed -lt $started -or
        $completed -gt [DateTimeOffset]::UtcNow.AddMinutes(1) -or [string]::IsNullOrWhiteSpace($evidence.runId))
    {
        throw "Lifecycle evidence timestamps/run identity are invalid for this run."
    }
    if ($ExpectedMode -ceq "preflight_only")
    {
        if ([bool]$evidence.safety.packageMutationStarted -or [bool]$evidence.safety.userDataMutationStarted -or
            -not [bool]$evidence.safety.appPackageAndCanonicalDataUntouchedByPreflight)
        {
            throw "Preflight evidence reports a mutation."
        }
    }
    else
    {
        Assert-BooleanProperties -Object $evidence.lifecycle -Label "Lifecycle" -Names @(
            "baseRegistered", "startMenuRegistered", "firstLaunchSingleProcess", "repeatedLaunchReusedProcess",
            "sentinelCreated", "sentinelPreservedAcrossUpgrade", "upgradeRegistered", "downgradeRejected",
            "sentinelPreservedAfterUninstall", "packageAbsentAfterUninstall", "reinstallRegistered",
            "sentinelPresentAfterReinstall", "reinstallLaunchVerified")
        if (-not [bool]$evidence.cleanup.harnessPackageRegistrationRemoved -or
            -not [bool]$evidence.cleanup.sentinelRemoved -or
            [bool]$evidence.cleanup.ownedProcessCleanupFailed -or
            [int]$evidence.cleanup.ownedProcessesRemaining -ne 0)
        {
            throw "Lifecycle evidence does not prove exact cleanup."
        }
    }
    return $evidence
}

function Invoke-HarnessEvidencePass
{
    param([string]$PassName, [string]$HarnessPath, [string]$BasePath, [string]$UpgradePath,
        [string]$LocalEvidencePath, [string]$RawDirectory, [string]$ValidatedDirectory, [switch]$PreflightOnly)
    $captured = $null
    $baseLease = $null
    $upgradeLease = $null
    $harnessLease = $null
    try
    {
        $baseLease = [IO.File]::Open($BasePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $upgradeLease = [IO.File]::Open($UpgradePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $harnessLease = [IO.File]::Open($HarnessPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ((Get-OpenStreamHash -Stream $baseLease) -cne $expectedBaseHash -or
            (Get-OpenStreamHash -Stream $upgradeLease) -cne $expectedUpgradeHash -or
            (Get-OpenStreamHash -Stream $harnessLease) -cne $expectedHarnessHash)
        {
            throw "Acceptance inputs changed before a lifecycle pass."
        }
        foreach ($packagePath in @($BasePath, $UpgradePath))
        {
            $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
            if ($null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Thumbprint -cne $expectedCertificateThumbprint)
            {
                throw "Acceptance signer changed before a lifecycle pass."
            }
        }
        $currentTrust = Get-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$expectedCertificateThumbprint" -ErrorAction SilentlyContinue
        if ($null -eq $currentTrust -or $currentTrust.Subject -cne $expectedPublisher -or $currentTrust.HasPrivateKey)
        {
            throw "Manual development-certificate trust changed before a lifecycle pass."
        }
        $arguments = @{
            BaseMsixPath = $BasePath
            UpgradeMsixPath = $UpgradePath
            EvidencePath = $LocalEvidencePath
            OperationTimeoutSeconds = 120
        }
        if ($PreflightOnly) { $arguments.PreflightOnly = $true }
        & $HarnessPath @arguments | Out-Null
    }
    catch { $captured = $_ }
    finally
    {
        if ($null -ne $harnessLease) { $harnessLease.Dispose() }
        if ($null -ne $upgradeLease) { $upgradeLease.Dispose() }
        if ($null -ne $baseLease) { $baseLease.Dispose() }
        Publish-FileAtomically -Source $LocalEvidencePath -Directory $RawDirectory -Name "$PassName.raw.json"
    }
    if ($null -ne $captured) { throw $captured }
    $expectedMode = if ($PreflightOnly) { "preflight_only" } else { "lifecycle" }
    [void](Assert-HarnessEvidence -Path $LocalEvidencePath -ExpectedMode $expectedMode)
    Publish-FileAtomically -Source $LocalEvidencePath -Directory $ValidatedDirectory -Name "$PassName.passed.json"
}

if ([string]::IsNullOrWhiteSpace($KitRoot)) { $KitRoot = Split-Path -Parent $PSScriptRoot }
$kitFullPath = [IO.Path]::GetFullPath($KitRoot)
if ([string]::IsNullOrWhiteSpace($EvidenceRoot))
{
    $EvidenceRoot = Join-Path (Split-Path -Parent $kitFullPath) ("isTranscribe-{0}-evidence" -f $Mode.ToLowerInvariant())
}
$evidenceFullPath = [IO.Path]::GetFullPath($EvidenceRoot)
$tempBase = Join-Path ([IO.Path]::GetTempPath()) "isTranscribeAcceptance"
$localRoot = Join-Path $tempBase ([guid]::NewGuid().ToString("N"))
foreach ($path in @($kitFullPath, $evidenceFullPath, $localRoot)) { Assert-LocalPath -Path $path; Assert-NoReparsePointInPath -Path $path }
if ((Test-PathInsideOrEqual -Path $evidenceFullPath -ParentPath $kitFullPath) -or
    (Test-PathInsideOrEqual -Path $kitFullPath -ParentPath $evidenceFullPath) -or
    (Test-PathInsideOrEqual -Path $localRoot -ParentPath $kitFullPath) -or
    (Test-PathInsideOrEqual -Path $localRoot -ParentPath $evidenceFullPath))
{
    throw "Kit, evidence and local working paths must be separate and non-overlapping."
}
if ((Test-Path -LiteralPath $evidenceFullPath) -and @(Get-ChildItem -LiteralPath $evidenceFullPath -Force).Count -ne 0)
{
    throw "Evidence directory must be absent or empty for a new acceptance run."
}

if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 19041)
{
    throw "Acceptance kit requires Windows x64 build 19041 or newer."
}
$verificationOutput = & (Join-Path $kitFullPath "tools\Test-AcceptanceKit.ps1") -KitDirectory $kitFullPath | Out-String
$kitVerification = $verificationOutput | ConvertFrom-Json
if ($kitVerification.status -cne "passed") { throw "Acceptance-kit verification did not pass." }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$effectiveAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$whoami = Join-Path $env:SystemRoot "System32\whoami.exe"
$groupOutput = (& $whoami /groups /fo csv /nh 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Could not inspect the current account group token." }
$accountHasAdministratorsSid = $groupOutput -match 'S-1-5-32-544'
$integrityMatch = [regex]::Match($groupOutput, 'S-1-16-(?<rid>[0-9]+)')
if (-not $integrityMatch.Success) { throw "Could not determine the current token integrity level." }
$integrityRid = [int]$integrityMatch.Groups["rid"].Value
$tokenElevated = $effectiveAdministrator -or $integrityRid -ge 12288
$requiresStandardUser = $Mode.StartsWith("CleanVm", [StringComparison]::Ordinal)
if ($requiresStandardUser -and ($tokenElevated -or $accountHasAdministratorsSid))
{
    throw "Clean-VM acceptance requires a standard-user account that is not a member of local Administrators."
}

$trustedCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$expectedCertificateThumbprint" -ErrorAction SilentlyContinue
if ($null -eq $trustedCertificate -or $trustedCertificate.Subject -cne $expectedPublisher -or $trustedCertificate.HasPrivateKey)
{
    throw "An administrator must manually trust the bundled public development certificate in Local Machine / Trusted People before this disposable run."
}

[void][IO.Directory]::CreateDirectory($evidenceFullPath)
Assert-NoReparsePointInPath -Path $evidenceFullPath
[void][IO.Directory]::CreateDirectory($localRoot)
$localFiles = [Collections.Generic.List[string]]::new()
try
{
    $sourceBase = Join-Path $kitFullPath "packages\2.0.0\isTranscribe-2.0.0-dev-win-x64.msix"
    $sourceUpgrade = Join-Path $kitFullPath "packages\2.0.1\isTranscribe-2.0.1-dev-win-x64.msix"
    $sourceHarness = Join-Path $kitFullPath "tools\Test-WindowsReleaseLifecycle.ps1"
    $localBase = Join-Path $localRoot "base.msix"
    $localUpgrade = Join-Path $localRoot "upgrade.msix"
    $localHarness = Join-Path $localRoot "Test-WindowsReleaseLifecycle.ps1"
    foreach ($pair in @(@($sourceBase, $localBase), @($sourceUpgrade, $localUpgrade), @($sourceHarness, $localHarness)))
    {
        $localFiles.Add($pair[1])
        Copy-LockedFile -Source $pair[0] -Destination $pair[1]
    }
    Assert-LocalInput -Path $localBase -ExpectedHash $expectedBaseHash -Msix
    Assert-LocalInput -Path $localUpgrade -ExpectedHash $expectedUpgradeHash -Msix
    Assert-LocalInput -Path $localHarness -ExpectedHash $expectedHarnessHash

    $rawDirectory = Join-Path $evidenceFullPath "raw"
    $validatedDirectory = Join-Path $evidenceFullPath "validated-staging"
    $readinessPath = Join-Path $localRoot "kit-readiness.json"
    $localFiles.Add($readinessPath)
    $readiness = [ordered]@{
        schemaVersion = "infra-005-acceptance-kit-readiness-v1"
        status = "passed"
        startedUtc = $runStartedUtc.ToString("O")
        mode = $Mode
        sourceRevision = $kitVerification.sourceRevision
        kitDirectory = $kitFullPath
        evidenceDirectory = $evidenceFullPath
        windowsBuild = [Environment]::OSVersion.Version.Build
        windowsX64 = [Environment]::Is64BitOperatingSystem
        userName = $identity.Name
        userSid = $identity.User.Value
        effectiveAdministrator = $effectiveAdministrator
        accountHasAdministratorsSid = $accountHasAdministratorsSid
        integritySid = $integrityMatch.Value
        integrityRid = $integrityRid
        tokenElevated = $tokenElevated
        standardUserRequired = $requiresStandardUser
        standardUserRequirementSatisfied = -not $requiresStandardUser -or (-not $tokenElevated -and -not $accountHasAdministratorsSid)
        certificateTrustWasManual = $true
        securitySettingsModified = $false
        evidenceIsStagingNotCanonical = $true
        coverageLabel = if ($requiresStandardUser) { "standard_user_powershell_deployment" } else { "sandbox_administrator_powershell_deployment" }
    }
    [IO.File]::WriteAllText($readinessPath, ($readiness | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Publish-FileAtomically -Source $readinessPath -Directory $rawDirectory -Name "kit-readiness.raw.json"
    Publish-FileAtomically -Source $readinessPath -Directory $validatedDirectory -Name "kit-readiness.passed.json"

    $preflightPath = Join-Path $localRoot "preflight.json"
    $localFiles.Add($preflightPath)
    $labelPrefix = if ($requiresStandardUser) { "windows-standard-user" } else { "windows-sandbox-administrator" }
    Invoke-HarnessEvidencePass -PassName "$labelPrefix-preflight" -HarnessPath $localHarness `
        -BasePath $localBase -UpgradePath $localUpgrade -LocalEvidencePath $preflightPath `
        -RawDirectory $rawDirectory -ValidatedDirectory $validatedDirectory -PreflightOnly

    if ($Mode -ne "CleanVmPreflight")
    {
        $lifecyclePath = Join-Path $localRoot "lifecycle.json"
        $localFiles.Add($lifecyclePath)
        Invoke-HarnessEvidencePass -PassName "$labelPrefix-lifecycle" -HarnessPath $localHarness `
            -BasePath $localBase -UpgradePath $localUpgrade -LocalEvidencePath $lifecyclePath `
            -RawDirectory $rawDirectory -ValidatedDirectory $validatedDirectory
    }
}
finally
{
    foreach ($path in $localFiles)
    {
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Test-PathInsideOrEqual -Path $path -ParentPath $localRoot))
        {
            Remove-Item -LiteralPath $path -Force
        }
    }
    if ((Test-Path -LiteralPath $localRoot -PathType Container) -and @(Get-ChildItem -LiteralPath $localRoot -Force).Count -eq 0)
    {
        Remove-Item -LiteralPath $localRoot -Force
    }
}

Write-Output "Acceptance pass completed. Evidence is staging and requires host-side review before canonical promotion: $evidenceFullPath"
