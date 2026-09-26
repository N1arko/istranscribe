[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseMsixPath,
    [Parameter(Mandatory)]
    [string]$UpgradeMsixPath,
    [Parameter(Mandatory)]
    [string]$EvidencePath,
    [string]$PackageName = "isTranscribe.Desktop",
    [string]$ApplicationId = "App",
    [version]$ExpectedBasePackageVersion = [version]"2.0.0.65535",
    [version]$ExpectedUpgradePackageVersion = [version]"2.0.1.65535",
    [ValidateRange(5, 120)]
    [int]$OperationTimeoutSeconds = 30,
    [switch]$PreflightOnly,
    [switch]$LeaveInstalled,
    [switch]$AllowExistingDataRootForLocalSmoke,
    [switch]$OverwriteEvidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
$SpecReference = "spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification"
$RunId = [guid]::NewGuid().ToString("D")
$StartedUtc = [DateTimeOffset]::UtcNow
$CurrentStage = "initialization"
$FailureReason = $null
$CapturedFailure = $null
$CleanupFailure = $null
$PackageMutationStarted = $false
$UserDataMutationStarted = $false
$OwnsPackageRegistration = $null
$SentinelCreated = $false
$SentinelRemoved = $false
$BaseRegistration = $null
$UpgradeRegistration = $null
$PrimaryProcessId = $null
$OwnedProcesses = [System.Collections.Generic.Dictionary[int, object]]::new()
$Steps = [System.Collections.Generic.List[object]]::new()
$PreInstallIntegrity = [System.Collections.Generic.List[object]]::new()

if ($null -eq ("IsTranscribe.Acceptance.WinTrustVerifier" -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace IsTranscribe.Acceptance
{
    public static class WinTrustVerifier
    {
        private static readonly Guid GenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            WinTrustData data);

        public static uint Verify(string filePath)
        {
            using (var fileInfo = new WinTrustFileInfo(filePath))
            using (var data = new WinTrustData(fileInfo))
            {
                return unchecked((uint)WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, data));
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;

            public WinTrustFileInfo(string filePath)
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath);
                hFile = IntPtr.Zero;
                pgKnownSubject = IntPtr.Zero;
            }

            public void Dispose()
            {
                Marshal.FreeCoTaskMem(pcwszFilePath);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;

            public WinTrustData(WinTrustFileInfo fileInfo)
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                pPolicyCallbackData = IntPtr.Zero;
                pSIPClientData = IntPtr.Zero;
                dwUIChoice = 2;
                fdwRevocationChecks = 0;
                dwUnionChoice = 1;
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, pFile, false);
                dwStateAction = 0;
                hWVTStateData = IntPtr.Zero;
                pwszURLReference = IntPtr.Zero;
                dwProvFlags = 0x1000;
                dwUIContext = 0;
            }

            public void Dispose()
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
'@
}

function Assert-SafeIdentityValue
{
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$ParameterName
    )

    if ($Value -notmatch '^[A-Za-z0-9._-]+$')
    {
        throw "$ParameterName contains characters that are not valid for this acceptance harness."
    }
}

function Test-ChildPath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathInsideOrEqual
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    return [string]::Equals($fullPath, $fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith(
            $fullParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

function Get-CanonicalDataSnapshot
{
    param([Parameter(Mandatory)][string]$RootPath)

    return [ordered]@{
        rootPresent = Test-Path -LiteralPath $RootPath -PathType Container
        configDirectoryPresent = Test-Path -LiteralPath (Join-Path $RootPath "config") -PathType Container
        dataDirectoryPresent = Test-Path -LiteralPath (Join-Path $RootPath "data") -PathType Container
        tempDirectoryPresent = Test-Path -LiteralPath (Join-Path $RootPath "temp") -PathType Container
        logsDirectoryPresent = Test-Path -LiteralPath (Join-Path $RootPath "logs") -PathType Container
        settingsFilePresent = Test-Path -LiteralPath (Join-Path $RootPath "config\settings.json") -PathType Leaf
        secretsFilePresent = Test-Path -LiteralPath (Join-Path $RootPath "config\secrets.bin") -PathType Leaf
        databaseFilePresent = Test-Path -LiteralPath (Join-Path $RootPath "data\app.db") -PathType Leaf
    }
}

function Read-MsixMetadata
{
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try
    {
        $manifestEntry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $manifestEntry)
        {
            throw "The MSIX does not contain AppxManifest.xml."
        }

        $stream = $manifestEntry.Open()
        try
        {
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
            try
            {
                [xml]$manifest = $reader.ReadToEnd()
            }
            finally
            {
                $reader.Dispose()
            }
        }
        finally
        {
            $stream.Dispose()
        }

        $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
        $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
        if ($null -eq $identity)
        {
            throw "The MSIX identity is missing."
        }

        $applications = @($manifest.SelectNodes("/f:Package/f:Applications/f:Application", $namespaces))
        $application = @($applications | Where-Object { $_.GetAttribute("Id") -ceq $ApplicationId })
        if ($application.Count -ne 1)
        {
            throw "The MSIX must contain exactly one application with the requested application id."
        }

        $executable = $application[0].GetAttribute("Executable")
        if ([string]::IsNullOrWhiteSpace($executable) -or
            [IO.Path]::GetExtension($executable) -ine ".exe" -or
            $executable -notmatch '^[A-Za-z0-9._-]+\.exe$')
        {
            throw "The packaged application executable is invalid."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute("Name")
            Publisher = $identity.GetAttribute("Publisher")
            Version = [version]$identity.GetAttribute("Version")
            Architecture = $identity.GetAttribute("ProcessorArchitecture")
            ApplicationId = $application[0].GetAttribute("Id")
            Executable = $executable
            ProcessName = [IO.Path]::GetFileNameWithoutExtension($executable)
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

function Get-Sha256Hex
{
    param([Parameter(Mandatory)][IO.Stream]$Stream)

    $Stream.Position = 0
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try
    {
        return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace("-", [string]::Empty)
    }
    finally
    {
        $algorithm.Dispose()
        $Stream.Position = 0
    }
}

function Get-MsixSecurityObservation
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedPublisher,
        [IO.Stream]$OpenStream
    )

    $ownsStream = $null -eq $OpenStream
    $stream = $OpenStream
    if ($ownsStream)
    {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
    }

    try
    {
        $sha256 = Get-Sha256Hex -Stream $stream
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        $signerSubject = if ($null -eq $signature.SignerCertificate) {
            $null
        } else {
            $signature.SignerCertificate.Subject
        }
        $signerThumbprint = if ($null -eq $signature.SignerCertificate) {
            $null
        } else {
            $signature.SignerCertificate.Thumbprint.Replace(" ", [string]::Empty).ToUpperInvariant()
        }
        $publisherMatches = $null -ne $signerSubject -and [string]::Equals(
            $signerSubject,
            $ExpectedPublisher,
            [StringComparison]::Ordinal)
        $winTrustResult = [IsTranscribe.Acceptance.WinTrustVerifier]::Verify($Path)

        return [pscustomobject][ordered]@{
            sha256 = $sha256
            authenticodeStatus = $signature.Status.ToString()
            signerSubject = $signerSubject
            signerThumbprint = $signerThumbprint
            publisherMatchesManifest = $publisherMatches
            winVerifyTrust = ('0x{0:X8}' -f $winTrustResult)
        }
    }
    finally
    {
        if ($ownsStream -and $null -ne $stream)
        {
            $stream.Dispose()
        }
    }
}

function Assert-MsixSecurityObservation
{
    param([Parameter(Mandatory)][object]$Observation)

    if ([string]::IsNullOrWhiteSpace($Observation.signerSubject) -or
        [string]::IsNullOrWhiteSpace($Observation.signerThumbprint) -or
        [string]::Equals($Observation.authenticodeStatus, "NotSigned", [StringComparison]::OrdinalIgnoreCase))
    {
        $script:FailureReason = "package_not_signed"
        throw "The MSIX does not contain a usable embedded signer."
    }

    if ([string]::Equals($Observation.authenticodeStatus, "HashMismatch", [StringComparison]::OrdinalIgnoreCase))
    {
        $script:FailureReason = "package_signature_hash_mismatch"
        throw "The MSIX embedded signature reports a content hash mismatch."
    }

    if (-not $Observation.publisherMatchesManifest)
    {
        $script:FailureReason = "signature_publisher_mismatch"
        throw "The embedded signer subject does not match the package publisher identity."
    }

    if ([string]::Equals($Observation.winVerifyTrust, "0x00000000", [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($Observation.authenticodeStatus, "Valid", [StringComparison]::OrdinalIgnoreCase))
    {
        return
    }

    if ([string]::Equals($Observation.winVerifyTrust, "0x800B0109", [StringComparison]::OrdinalIgnoreCase))
    {
        $script:FailureReason = "certificate_not_trusted_for_appx"
        throw "The intact MSIX has a matching signer whose certificate chain is not trusted for AppX."
    }

    $script:FailureReason = "package_signature_verification_failed"
    throw "The MSIX failed WinVerifyTrust or Authenticode validation."
}

function Open-VerifiedPackageLease
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedPublisher,
        [Parameter(Mandatory)][object]$InitialObservation,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$PackageRole
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try
    {
        $observation = Get-MsixSecurityObservation `
            -Path $Path `
            -ExpectedPublisher $ExpectedPublisher `
            -OpenStream $stream
        $integrityMatches = [string]::Equals(
            $observation.sha256,
            $InitialObservation.sha256,
            [StringComparison]::OrdinalIgnoreCase)
        $signerMatches = [string]::Equals(
            $observation.signerSubject,
            $InitialObservation.signerSubject,
            [StringComparison]::Ordinal) -and [string]::Equals(
            $observation.signerThumbprint,
            $InitialObservation.signerThumbprint,
            [StringComparison]::OrdinalIgnoreCase)
        $evidenceObservation = [pscustomobject][ordered]@{
            operation = $Operation
            packageRole = $PackageRole
            observedUtc = [DateTimeOffset]::UtcNow.ToString("O")
            sha256 = $observation.sha256
            matchesInitialSha256 = $integrityMatches
            signerSubject = $observation.signerSubject
            signerThumbprint = $observation.signerThumbprint
            matchesInitialSigner = $signerMatches
            authenticodeStatus = $observation.authenticodeStatus
            winVerifyTrust = $observation.winVerifyTrust
        }
        $script:PreInstallIntegrity.Add($evidenceObservation)

        if (-not $integrityMatches)
        {
            $script:FailureReason = "package_hash_mismatch_before_install"
            throw "The MSIX SHA-256 changed after preflight."
        }
        if (-not $signerMatches)
        {
            $script:FailureReason = "package_signer_changed_before_install"
            throw "The MSIX embedded signer changed after preflight."
        }
        Assert-MsixSecurityObservation -Observation $observation

        return [pscustomobject]@{
            Stream = $stream
            Observation = $observation
        }
    }
    catch
    {
        $stream.Dispose()
        throw
    }
}

function Get-ExactPackageRegistration
{
    param([Parameter(Mandatory)][string]$ExpectedName)

    $matches = @(Get-AppxPackage -Name $ExpectedName -ErrorAction Stop |
        Where-Object { [string]::Equals($_.Name, $ExpectedName, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -gt 1)
    {
        throw "More than one current-user package registration has the requested identity."
    }

    if ($matches.Count -eq 0)
    {
        return $null
    }

    return $matches[0]
}

function Get-PackageRegistrationByFullName
{
    param(
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$ExpectedPackageFullName
    )

    $matches = @(Get-AppxPackage -Name $ExpectedName -ErrorAction Stop | Where-Object {
        [string]::Equals(
            $_.PackageFullName,
            $ExpectedPackageFullName,
            [StringComparison]::Ordinal)
    })
    if ($matches.Count -gt 1)
    {
        throw "More than one registration has the exact owned package full name."
    }
    if ($matches.Count -eq 0)
    {
        return $null
    }

    return $matches[0]
}

function Wait-ForPackageVersion
{
    param(
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][version]$ExpectedVersion,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do
    {
        $registration = Get-ExactPackageRegistration -ExpectedName $ExpectedName
        if ($null -ne $registration -and
            [version]$registration.Version -eq $ExpectedVersion -and
            -not [string]::IsNullOrWhiteSpace($registration.InstallLocation) -and
            (Test-Path -LiteralPath $registration.InstallLocation -PathType Container))
        {
            return $registration
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The expected package version did not become registered before the timeout."
}

function Wait-ForPackageAbsent
{
    param(
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do
    {
        if ($null -eq (Get-ExactPackageRegistration -ExpectedName $ExpectedName))
        {
            return
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The package registration remained present after the timeout."
}

function Wait-ForPackageFullNameAbsent
{
    param(
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$ExpectedPackageFullName,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do
    {
        if ($null -eq (Get-PackageRegistrationByFullName `
                -ExpectedName $ExpectedName `
                -ExpectedPackageFullName $ExpectedPackageFullName))
        {
            return
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The exact owned package registration remained present after the timeout."
}

function Wait-ForStartMenuRegistration
{
    param(
        [Parameter(Mandatory)][string]$ApplicationUserModelId,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do
    {
        $matches = @(Get-StartApps | Where-Object {
            [string]::Equals($_.AppID, $ApplicationUserModelId, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matches.Count -ge 1)
        {
            return
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The packaged application did not appear in Start Menu registration before the timeout."
}

function Register-OwnedProcesses
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Diagnostics.Process[]]$Processes,
        [Parameter(Mandatory)][string]$InstallLocation
    )

    foreach ($process in $Processes)
    {
        try
        {
            $startTicks = $process.StartTime.ToUniversalTime().Ticks
            $processPath = $process.Path
            if (-not (Test-ChildPath -Path $processPath -ParentPath $InstallLocation))
            {
                continue
            }
            $script:OwnedProcesses[$process.Id] = [pscustomobject]@{
                StartTicks = $startTicks
                InstallLocation = [IO.Path]::GetFullPath($InstallLocation)
                ProcessName = $process.ProcessName
            }
        }
        catch
        {
            # A short-lived secondary process may exit while the process list is sampled.
        }
    }
}

function Get-PackageApplicationProcesses
{
    param(
        [Parameter(Mandatory)][string]$ExpectedProcessName,
        [Parameter(Mandatory)][string]$InstallLocation
    )

    $script:PackageProcessPathInspectionUnavailable = $false
    $owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($process in @(Get-Process -Name $ExpectedProcessName -ErrorAction SilentlyContinue))
    {
        try
        {
            $processPath = $process.Path
            if ([string]::IsNullOrWhiteSpace($processPath))
            {
                throw "Process path is empty."
            }
            $insidePackage = Test-ChildPath -Path $processPath -ParentPath $InstallLocation
        }
        catch
        {
            $processExitedDuringInspection = $false
            try
            {
                $process.Refresh()
                $processExitedDuringInspection = $process.HasExited
            }
            catch
            {
                try
                {
                    [void][Diagnostics.Process]::GetProcessById($process.Id)
                }
                catch [ArgumentException]
                {
                    $processExitedDuringInspection = $true
                }
                catch
                {
                    $processExitedDuringInspection = $false
                }
            }
            if ($processExitedDuringInspection)
            {
                continue
            }
            $script:PackageProcessPathInspectionUnavailable = $true
            continue
        }

        if (-not $insidePackage)
        {
            $script:FailureReason = "matching_process_outside_package"
            throw "A matching process is running outside the exact package install location."
        }
        $owned.Add($process)
    }

    return $owned.ToArray()
}

function Wait-ForStableSingleProcess
{
    param(
        [Parameter(Mandatory)][string]$ExpectedProcessName,
        [Parameter(Mandatory)][string]$InstallLocation,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [int]$ExpectedProcessId = -1
    )

    $hasExpectedProcessId = $PSBoundParameters.ContainsKey("ExpectedProcessId")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableSamples = 0
    do
    {
        $processes = @(Get-PackageApplicationProcesses `
            -ExpectedProcessName $ExpectedProcessName `
            -InstallLocation $InstallLocation)
        Register-OwnedProcesses -Processes $processes -InstallLocation $InstallLocation
        $matchesExpectedId = -not $hasExpectedProcessId -or
            ($processes.Count -eq 1 -and $processes[0].Id -eq $ExpectedProcessId)
        if (-not $script:PackageProcessPathInspectionUnavailable -and
            $processes.Count -eq 1 -and $matchesExpectedId)
        {
            $stableSamples++
            if ($stableSamples -ge 4)
            {
                return $processes[0]
            }
        }
        else
        {
            $stableSamples = 0
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($script:PackageProcessPathInspectionUnavailable)
    {
        $script:FailureReason = "package_process_path_unavailable"
        throw "A live matching process path remained unavailable through the bounded inspection window."
    }
    throw "The application did not settle to the expected single process before the timeout."
}

function Test-OwnedProcessStillRunning
{
    param([Parameter(Mandatory)][object]$Owned)

    $process = Get-Process -Id $Owned.Key -ErrorAction SilentlyContinue
    if ($null -eq $process)
    {
        return $false
    }

    try
    {
        return $process.StartTime.ToUniversalTime().Ticks -eq $Owned.Value.StartTicks -and
            [string]::Equals(
                $process.ProcessName,
                $Owned.Value.ProcessName,
                [StringComparison]::OrdinalIgnoreCase) -and
            (Test-ChildPath -Path $process.Path -ParentPath $Owned.Value.InstallLocation)
    }
    catch
    {
        return $false
    }
}

function Wait-ForOwnedProcessesToExit
{
    param([Parameter(Mandatory)][int]$TimeoutSeconds)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do
    {
        $running = $false
        foreach ($owned in @($script:OwnedProcesses.GetEnumerator()))
        {
            if (Test-OwnedProcessStillRunning -Owned $owned)
            {
                $running = $true
                break
            }
        }
        if (-not $running)
        {
            return
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The package update did not close the previous application process before the timeout."
}

function Stop-OwnedProcesses
{
    $stoppedCount = 0
    foreach ($owned in @($script:OwnedProcesses.GetEnumerator()))
    {
        $process = Get-Process -Id $owned.Key -ErrorAction SilentlyContinue
        if ($null -eq $process)
        {
            continue
        }

        try
        {
            $isExactOwnedProcess =
                $process.StartTime.ToUniversalTime().Ticks -eq $owned.Value.StartTicks -and
                [string]::Equals(
                    $process.ProcessName,
                    $owned.Value.ProcessName,
                    [StringComparison]::OrdinalIgnoreCase) -and
                (Test-ChildPath -Path $process.Path -ParentPath $owned.Value.InstallLocation)
            if ($isExactOwnedProcess)
            {
                Stop-Process -InputObject $process -Force -ErrorAction Stop
                $stoppedCount++
            }
        }
        catch { }
    }

    $remainingCount = @($script:OwnedProcesses.GetEnumerator() | Where-Object {
        Test-OwnedProcessStillRunning -Owned $_
    }).Count
    if ($remainingCount -gt 0)
    {
        throw "One or more exact harness-owned application processes remain after cleanup."
    }
    return $stoppedCount
}

function Test-IsPackageDowngradeRejection
{
    param([Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception)
    {
        if ($exception.HResult -in @(0x80073CFB, 0x80073D06))
        {
            return $true
        }
        $exception = $exception.InnerException
    }

    # DeploymentException can wrap the native HRESULT in its diagnostic text.
    $diagnostic = $ErrorRecord.Exception.ToString()
    return $diagnostic.IndexOf("0x80073CFB", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $diagnostic.IndexOf("0x80073D06", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-Sentinel
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedContent
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return $false
    }

    return [string]::Equals(
        [IO.File]::ReadAllText($Path),
        $ExpectedContent,
        [StringComparison]::Ordinal)
}

function Test-LegacyRunValuePresent
{
    $runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $value = Get-ItemProperty `
        -LiteralPath $runKeyPath `
        -Name "isTranscribe" `
        -ErrorAction SilentlyContinue
    return $null -ne $value
}

function Test-NamedProcessPresent
{
    param([Parameter(Mandatory)][string[]]$Names)

    foreach ($name in $Names)
    {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue))
        {
            if ($null -ne $process)
            {
                return $true
            }
        }
    }

    return $false
}

function Write-EvidenceAtomically
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$AllowOverwrite
    )

    $directory = Split-Path -Parent $Destination
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($Destination), [guid]::NewGuid().ToString("N"))
    $backupPath = Join-Path $directory (
        ".{0}.{1}.bak" -f [IO.Path]::GetFileName($Destination), [guid]::NewGuid().ToString("N"))
    try
    {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($Value | ConvertTo-Json -Depth 16),
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Destination -PathType Leaf)
        {
            if (-not $AllowOverwrite)
            {
                throw "The evidence destination appeared during the run and overwrite was not authorized."
            }
            [IO.File]::Replace($temporaryPath, $Destination, $backupPath, $true)
        }
        else
        {
            [IO.File]::Move($temporaryPath, $Destination)
        }
    }
    finally
    {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf)
        {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf)
        {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-LifecycleStep
{
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $script:CurrentStage = $Name
    $step = [ordered]@{
        name = $Name
        status = "running"
        startedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        completedUtc = $null
    }
    try
    {
        & $Action | Out-Null
        $step.status = "passed"
    }
    catch
    {
        $step.status = "failed"
        throw
    }
    finally
    {
        $step.completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        $script:Steps.Add([pscustomobject]$step)
    }
}

Assert-SafeIdentityValue -Value $PackageName -ParameterName "PackageName"
Assert-SafeIdentityValue -Value $ApplicationId -ParameterName "ApplicationId"

$BaseMsixFullPath = [IO.Path]::GetFullPath($BaseMsixPath)
$UpgradeMsixFullPath = [IO.Path]::GetFullPath($UpgradeMsixPath)
$EvidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$LocalApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($LocalApplicationData))
{
    throw "Windows did not provide the current user's Local Application Data path."
}
$CanonicalDataRoot = Join-Path $LocalApplicationData "isTranscribe"
if (-not (Test-ChildPath -Path $CanonicalDataRoot -ParentPath $LocalApplicationData))
{
    throw "The canonical data root is outside the current user's Local Application Data directory."
}
if (Test-PathInsideOrEqual -Path $EvidenceFullPath -ParentPath $CanonicalDataRoot)
{
    throw "EvidencePath must be outside the canonical isTranscribe data root."
}

$EvidenceDirectory = Split-Path -Parent $EvidenceFullPath
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory))
{
    throw "EvidencePath must resolve to a file in a directory."
}
if ([IO.Path]::GetExtension($EvidenceFullPath) -ine ".json")
{
    throw "EvidencePath must use the .json extension."
}
if ((Test-Path -LiteralPath $EvidenceFullPath) -and -not $OverwriteEvidence)
{
    throw "The evidence file already exists. Use -OverwriteEvidence for an intentional replacement."
}
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null

$SentinelPath = Join-Path $CanonicalDataRoot ".infra005-lifecycle-$RunId.sentinel"
$SentinelContent = "INFRA-005.A lifecycle acceptance $RunId"
$InitialDataSnapshot = Get-CanonicalDataSnapshot -RootPath $CanonicalDataRoot

$Evidence = [ordered]@{
    schemaVersion = 2
    spec = $SpecReference
    runId = $RunId
    startedUtc = $StartedUtc.ToString("O")
    completedUtc = $null
    status = "running"
    reason = $null
    mode = if ($PreflightOnly) { "preflight_only" } elseif ($AllowExistingDataRootForLocalSmoke) {
        "local_existing_profile_lifecycle"
    } else { "lifecycle" }
    inputs = [ordered]@{
        basePackageFile = [IO.Path]::GetFileName($BaseMsixFullPath)
        upgradePackageFile = [IO.Path]::GetFileName($UpgradeMsixFullPath)
        packageName = $PackageName
        applicationId = $ApplicationId
        expectedBasePackageVersion = $ExpectedBasePackageVersion.ToString()
        expectedUpgradePackageVersion = $ExpectedUpgradePackageVersion.ToString()
        leaveInstalled = [bool]$LeaveInstalled
        allowExistingDataRootForLocalSmoke = [bool]$AllowExistingDataRootForLocalSmoke
        packages = [ordered]@{
            base = [ordered]@{
                sha256 = $null
                signerSubject = $null
                signerThumbprint = $null
                authenticodeStatus = $null
                winVerifyTrust = $null
            }
            upgrade = [ordered]@{
                sha256 = $null
                signerSubject = $null
                signerThumbprint = $null
                authenticodeStatus = $null
                winVerifyTrust = $null
            }
        }
    }
    preflight = [ordered]@{
        windowsX64 = $false
        requiredCommandsPresent = $false
        packageFilesValid = $false
        packageRegistrationAbsent = $false
        canonicalDataRootAbsent = -not $InitialDataSnapshot.rootPresent
        legacyRunValueAbsent = $false
        legacyProcessAbsent = $false
        currentProcessAbsent = $false
        cleanDisposableProfileEligible = $false
        localExistingProfileEligible = $false
        selectedProfileEligible = $false
        baseSignatureTrustedForAppx = $false
        upgradeSignatureTrustedForAppx = $false
        canonicalDataBefore = $InitialDataSnapshot
    }
    lifecycle = [ordered]@{
        baseRegistered = $false
        startMenuRegistered = $false
        firstLaunchSingleProcess = $false
        repeatedLaunchReusedProcess = $false
        sentinelCreated = $false
        sentinelPreservedAcrossUpgrade = $false
        upgradeRegistered = $false
        downgradeRejected = $false
        sentinelPreservedAfterUninstall = $false
        packageAbsentAfterUninstall = $false
        canonicalDataAfterUninstall = $null
        reinstallRegistered = $false
        sentinelPresentAfterReinstall = $false
        reinstallLaunchVerified = $false
        canonicalDataAfterReinstall = $null
    }
    preInstallIntegrity = $PreInstallIntegrity
    coverage = [ordered]@{
        deploymentContour = "PowerShell Add-AppxPackage with ForceApplicationShutdown for the running upgrade"
        covered = @(
            "package registration and Start Menu registration",
            "package-scoped launch and single-instance reuse",
            "PowerShell deployment upgrade",
            "default downgrade rejection",
            "package removal and reinstall",
            "low-level canonical-root sentinel preservation"
        )
        notCovered = [ordered]@{
            appInstallerUiRunningUpdateFlow = "not covered; this harness uses the PowerShell deployment contour"
            customRecordingsFolder = "not covered"
            audioCapture = "not covered"
            processLoopback = "not covered"
            autostart = "not covered"
        }
        sentinelMeaning = "low-level canonical-root preservation only; it does not prove application-level settings, database, recording, or secret recovery"
    }
    safety = [ordered]@{
        securitySettingsModified = $false
        preflightWasReadOnly = $true
        packageMutationStarted = $false
        userDataMutationStarted = $false
        appPackageAndCanonicalDataUntouchedByPreflight = $true
    }
    steps = $Steps
    cleanup = [ordered]@{
        ownedProcessesStopped = 0
        ownedProcessCleanupFailed = $false
        ownedProcessesRemaining = 0
        recoveredOwnedRegistration = $false
        harnessPackageRegistrationRemoved = $false
        sentinelRemoved = $false
        packageLeftInstalledByRequest = [bool]$LeaveInstalled
    }
    final = $null
    failure = $null
}

try
{
    Invoke-LifecycleStep -Name "preflight_platform_and_tools" -Action {
        if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [Runtime.InteropServices.OSPlatform]::Windows) -or
            [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
                [Runtime.InteropServices.Architecture]::X64)
        {
            $script:FailureReason = "windows_x64_required"
            throw "Lifecycle acceptance requires native Windows x64."
        }

        foreach ($command in @(
            "Add-AppxPackage",
            "Get-AppxPackage",
            "Get-AuthenticodeSignature",
            "Get-StartApps",
            "Remove-AppxPackage"))
        {
            if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue))
            {
                $script:FailureReason = "required_windows_command_missing"
                throw "A required Windows package command is unavailable."
            }
        }

        $Evidence.preflight.windowsX64 = $true
        $Evidence.preflight.requiredCommandsPresent = $true
    }

    Invoke-LifecycleStep -Name "preflight_package_metadata" -Action {
        foreach ($path in @($BaseMsixFullPath, $UpgradeMsixFullPath))
        {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                [IO.Path]::GetExtension($path) -ine ".msix")
            {
                $script:FailureReason = "msix_input_missing_or_invalid"
                throw "Both lifecycle inputs must be existing MSIX files."
            }
        }
        if ([string]::Equals($BaseMsixFullPath, $UpgradeMsixFullPath, [StringComparison]::OrdinalIgnoreCase))
        {
            $script:FailureReason = "base_and_upgrade_paths_match"
            throw "Base and upgrade package paths must be different."
        }

        $script:BaseMetadata = Read-MsixMetadata -Path $BaseMsixFullPath
        $script:UpgradeMetadata = Read-MsixMetadata -Path $UpgradeMsixFullPath
        if ($BaseMetadata.Name -cne $PackageName -or $UpgradeMetadata.Name -cne $PackageName -or
            $BaseMetadata.Publisher -cne $UpgradeMetadata.Publisher -or
            $BaseMetadata.Architecture -ine "x64" -or $UpgradeMetadata.Architecture -ine "x64" -or
            $BaseMetadata.ApplicationId -cne $ApplicationId -or
            $UpgradeMetadata.ApplicationId -cne $ApplicationId -or
            $BaseMetadata.Executable -cne $UpgradeMetadata.Executable -or
            $BaseMetadata.Version -ne $ExpectedBasePackageVersion -or
            $UpgradeMetadata.Version -ne $ExpectedUpgradePackageVersion -or
            $UpgradeMetadata.Version -le $BaseMetadata.Version)
        {
            $script:FailureReason = "package_identity_or_version_contract_mismatch"
            throw "The base and upgrade packages do not match the requested stable identity and version contract."
        }

        $script:ProcessName = $BaseMetadata.ProcessName
        $Evidence.preflight.packageFilesValid = $true
    }

    Invoke-LifecycleStep -Name "preflight_machine_state" -Action {
        $Evidence.preflight.packageRegistrationAbsent =
            $null -eq (Get-ExactPackageRegistration -ExpectedName $PackageName)
        $Evidence.preflight.canonicalDataRootAbsent =
            -not (Test-Path -LiteralPath $CanonicalDataRoot)
        $Evidence.preflight.legacyRunValueAbsent = -not (Test-LegacyRunValuePresent)
        $Evidence.preflight.legacyProcessAbsent = -not (Test-NamedProcessPresent -Names @(
            "IsTranscribe.App"
        ))
        $currentProcessNames = @($ProcessName, "isTranscribe") |
            Sort-Object -Unique
        $Evidence.preflight.currentProcessAbsent = -not (Test-NamedProcessPresent -Names $currentProcessNames)
        $Evidence.preflight.cleanDisposableProfileEligible =
            $Evidence.preflight.packageRegistrationAbsent -and
            $Evidence.preflight.canonicalDataRootAbsent -and
            $Evidence.preflight.legacyRunValueAbsent -and
            $Evidence.preflight.legacyProcessAbsent -and
            $Evidence.preflight.currentProcessAbsent
        $Evidence.preflight.localExistingProfileEligible =
            $AllowExistingDataRootForLocalSmoke -and
            $Evidence.preflight.packageRegistrationAbsent -and
            -not $Evidence.preflight.canonicalDataRootAbsent -and
            $Evidence.preflight.legacyRunValueAbsent -and
            $Evidence.preflight.legacyProcessAbsent -and
            $Evidence.preflight.currentProcessAbsent
        $Evidence.preflight.selectedProfileEligible =
            $Evidence.preflight.cleanDisposableProfileEligible -or
            $Evidence.preflight.localExistingProfileEligible
    }

    Invoke-LifecycleStep -Name "preflight_signature_trust" -Action {
        $script:BaseSecurityObservation = Get-MsixSecurityObservation `
            -Path $BaseMsixFullPath `
            -ExpectedPublisher $BaseMetadata.Publisher
        $Evidence.inputs.packages.base = [ordered]@{
            sha256 = $BaseSecurityObservation.sha256
            signerSubject = $BaseSecurityObservation.signerSubject
            signerThumbprint = $BaseSecurityObservation.signerThumbprint
            authenticodeStatus = $BaseSecurityObservation.authenticodeStatus
            winVerifyTrust = $BaseSecurityObservation.winVerifyTrust
            publisherMatchesManifest = $BaseSecurityObservation.publisherMatchesManifest
        }
        $script:UpgradeSecurityObservation = Get-MsixSecurityObservation `
            -Path $UpgradeMsixFullPath `
            -ExpectedPublisher $UpgradeMetadata.Publisher
        $Evidence.inputs.packages.upgrade = [ordered]@{
            sha256 = $UpgradeSecurityObservation.sha256
            signerSubject = $UpgradeSecurityObservation.signerSubject
            signerThumbprint = $UpgradeSecurityObservation.signerThumbprint
            authenticodeStatus = $UpgradeSecurityObservation.authenticodeStatus
            winVerifyTrust = $UpgradeSecurityObservation.winVerifyTrust
            publisherMatchesManifest = $UpgradeSecurityObservation.publisherMatchesManifest
        }
        Assert-MsixSecurityObservation -Observation $BaseSecurityObservation
        $Evidence.preflight.baseSignatureTrustedForAppx = $true
        Assert-MsixSecurityObservation -Observation $UpgradeSecurityObservation
        $Evidence.preflight.upgradeSignatureTrustedForAppx = $true
    }

    if ($PreflightOnly)
    {
        $Evidence.status = "passed"
        $Evidence.reason = "preflight_completed_without_mutation"
    }
    else
    {
        Invoke-LifecycleStep -Name "enforce_disposable_clean_profile" -Action {
            if (-not $Evidence.preflight.packageRegistrationAbsent)
            {
                $script:FailureReason = "preexisting_package_registration"
                throw "Full lifecycle requires the package identity to be absent before installation."
            }
            if (-not $Evidence.preflight.canonicalDataRootAbsent -and
                -not $AllowExistingDataRootForLocalSmoke)
            {
                $script:FailureReason = "canonical_data_root_already_exists"
                throw "Full lifecycle requires a disposable profile without the canonical data root."
            }
            if (-not $Evidence.preflight.legacyRunValueAbsent)
            {
                $script:FailureReason = "legacy_run_value_already_exists"
                throw "Full lifecycle requires the legacy isTranscribe Run value to be absent."
            }
            if (-not $Evidence.preflight.legacyProcessAbsent -or
                -not $Evidence.preflight.currentProcessAbsent)
            {
                $script:FailureReason = "isTranscribe_process_already_running"
                throw "Full lifecycle requires legacy and current isTranscribe processes to be absent."
            }
            if (-not $Evidence.preflight.selectedProfileEligible)
            {
                $script:FailureReason = "eligible_local_profile_required"
                throw "Full lifecycle requires either a disposable clean profile or the explicit existing-data local-smoke mode."
            }
        }

        Invoke-LifecycleStep -Name "install_base" -Action {
            $lease = Open-VerifiedPackageLease `
                -Path $BaseMsixFullPath `
                -ExpectedPublisher $BaseMetadata.Publisher `
                -InitialObservation $BaseSecurityObservation `
                -Operation "install_base" `
                -PackageRole "base"
            try
            {
                $script:PackageMutationStarted = $true
                $Evidence.safety.packageMutationStarted = $true
                $Evidence.safety.appPackageAndCanonicalDataUntouchedByPreflight = $false
                Add-AppxPackage -Path $BaseMsixFullPath -ErrorAction Stop
            }
            finally
            {
                $lease.Stream.Dispose()
            }
            $script:BaseRegistration = Wait-ForPackageVersion `
                -ExpectedName $PackageName `
                -ExpectedVersion $ExpectedBasePackageVersion `
                -TimeoutSeconds $OperationTimeoutSeconds
            $script:OwnsPackageRegistration = $BaseRegistration.PackageFullName
            $Evidence.lifecycle.baseRegistered = $true

            $script:ApplicationUserModelId = "$($BaseRegistration.PackageFamilyName)!$ApplicationId"
            Wait-ForStartMenuRegistration `
                -ApplicationUserModelId $ApplicationUserModelId `
                -TimeoutSeconds $OperationTimeoutSeconds
            $Evidence.lifecycle.startMenuRegistered = $true
        }

        Invoke-LifecycleStep -Name "launch_and_single_instance" -Action {
            $script:UserDataMutationStarted = $true
            $Evidence.safety.userDataMutationStarted = $true
            $explorer = Join-Path $env:SystemRoot "explorer.exe"
            Start-Process -FilePath $explorer -ArgumentList "shell:AppsFolder\$ApplicationUserModelId"
            $primary = Wait-ForStableSingleProcess `
                -ExpectedProcessName $ProcessName `
                -InstallLocation $BaseRegistration.InstallLocation `
                -TimeoutSeconds $OperationTimeoutSeconds
            $script:PrimaryProcessId = $primary.Id
            $Evidence.lifecycle.firstLaunchSingleProcess = $true

            Start-Process -FilePath $explorer -ArgumentList "shell:AppsFolder\$ApplicationUserModelId"
            $reused = Wait-ForStableSingleProcess `
                -ExpectedProcessName $ProcessName `
                -InstallLocation $BaseRegistration.InstallLocation `
                -ExpectedProcessId $PrimaryProcessId `
                -TimeoutSeconds $OperationTimeoutSeconds
            if ($reused.Id -ne $PrimaryProcessId)
            {
                throw "The repeated launch did not reuse the primary process."
            }
            $Evidence.lifecycle.repeatedLaunchReusedProcess = $true
        }

        Invoke-LifecycleStep -Name "create_data_sentinel" -Action {
            if (-not (Test-Path -LiteralPath $CanonicalDataRoot -PathType Container))
            {
                throw "The application did not create its canonical data root after launch."
            }
            if ((Get-Item -LiteralPath $CanonicalDataRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
            {
                throw "The canonical data root became a reparse point during acceptance."
            }
            if (Test-Path -LiteralPath $SentinelPath)
            {
                throw "The unique lifecycle sentinel path is already occupied."
            }

            [IO.File]::WriteAllText($SentinelPath, $SentinelContent, [Text.UTF8Encoding]::new($false))
            $script:SentinelCreated = $true
            $script:UserDataMutationStarted = $true
            $Evidence.safety.userDataMutationStarted = $true
            $Evidence.lifecycle.sentinelCreated = Test-Sentinel `
                -Path $SentinelPath `
                -ExpectedContent $SentinelContent
            if (-not $Evidence.lifecycle.sentinelCreated)
            {
                throw "The lifecycle sentinel could not be verified after creation."
            }
        }

        Invoke-LifecycleStep -Name "upgrade_in_place" -Action {
            $lease = Open-VerifiedPackageLease `
                -Path $UpgradeMsixFullPath `
                -ExpectedPublisher $UpgradeMetadata.Publisher `
                -InitialObservation $UpgradeSecurityObservation `
                -Operation "upgrade_in_place" `
                -PackageRole "upgrade"
            try
            {
                Add-AppxPackage `
                    -Path $UpgradeMsixFullPath `
                    -ForceApplicationShutdown `
                    -ErrorAction Stop
            }
            finally
            {
                $lease.Stream.Dispose()
            }
            $script:UpgradeRegistration = Wait-ForPackageVersion `
                -ExpectedName $PackageName `
                -ExpectedVersion $ExpectedUpgradePackageVersion `
                -TimeoutSeconds $OperationTimeoutSeconds
            $script:OwnsPackageRegistration = $UpgradeRegistration.PackageFullName
            Wait-ForOwnedProcessesToExit -TimeoutSeconds $OperationTimeoutSeconds
            $Evidence.lifecycle.upgradeRegistered = $true
            $Evidence.lifecycle.sentinelPreservedAcrossUpgrade = Test-Sentinel `
                -Path $SentinelPath `
                -ExpectedContent $SentinelContent
            if (-not $Evidence.lifecycle.sentinelPreservedAcrossUpgrade)
            {
                throw "The lifecycle sentinel changed or disappeared during upgrade."
            }
        }

        Invoke-LifecycleStep -Name "reject_downgrade" -Action {
            $downgradeFailure = $null
            $lease = Open-VerifiedPackageLease `
                -Path $BaseMsixFullPath `
                -ExpectedPublisher $BaseMetadata.Publisher `
                -InitialObservation $BaseSecurityObservation `
                -Operation "reject_downgrade" `
                -PackageRole "base"
            try
            {
                Add-AppxPackage `
                    -Path $BaseMsixFullPath `
                    -ForceApplicationShutdown `
                    -ErrorAction Stop
            }
            catch
            {
                $downgradeFailure = $_
            }
            finally
            {
                $lease.Stream.Dispose()
            }

            if ($null -eq $downgradeFailure)
            {
                throw "Windows accepted a lower package version."
            }
            if (-not (Test-IsPackageDowngradeRejection -ErrorRecord $downgradeFailure))
            {
                throw "The downgrade attempt failed for an unexpected deployment reason."
            }

            $registration = Wait-ForPackageVersion `
                -ExpectedName $PackageName `
                -ExpectedVersion $ExpectedUpgradePackageVersion `
                -TimeoutSeconds $OperationTimeoutSeconds
            if ([version]$registration.Version -ne $ExpectedUpgradePackageVersion)
            {
                throw "The registered package version changed after the downgrade attempt."
            }
            $Evidence.lifecycle.downgradeRejected = $true
        }

        Invoke-LifecycleStep -Name "uninstall_preserves_data" -Action {
            if ([string]::IsNullOrWhiteSpace($OwnsPackageRegistration))
            {
                throw "The harness does not own an exact package registration before uninstall."
            }
            $ownedPackageFullName = $OwnsPackageRegistration
            $registration = Get-PackageRegistrationByFullName `
                -ExpectedName $PackageName `
                -ExpectedPackageFullName $ownedPackageFullName
            if ($null -eq $registration)
            {
                throw "The exact owned upgraded package registration is missing before uninstall."
            }
            Remove-AppxPackage -Package $ownedPackageFullName -ErrorAction Stop
            Wait-ForPackageFullNameAbsent `
                -ExpectedName $PackageName `
                -ExpectedPackageFullName $ownedPackageFullName `
                -TimeoutSeconds $OperationTimeoutSeconds
            $script:OwnsPackageRegistration = $null
            Wait-ForPackageAbsent `
                -ExpectedName $PackageName `
                -TimeoutSeconds $OperationTimeoutSeconds
            $Evidence.lifecycle.packageAbsentAfterUninstall = $true
            $Evidence.lifecycle.sentinelPreservedAfterUninstall = Test-Sentinel `
                -Path $SentinelPath `
                -ExpectedContent $SentinelContent
            if (-not $Evidence.lifecycle.sentinelPreservedAfterUninstall)
            {
                throw "The lifecycle sentinel changed or disappeared during uninstall."
            }
            $Evidence.lifecycle.canonicalDataAfterUninstall =
                Get-CanonicalDataSnapshot -RootPath $CanonicalDataRoot
        }

        Invoke-LifecycleStep -Name "reinstall_and_recover_data" -Action {
            $lease = Open-VerifiedPackageLease `
                -Path $UpgradeMsixFullPath `
                -ExpectedPublisher $UpgradeMetadata.Publisher `
                -InitialObservation $UpgradeSecurityObservation `
                -Operation "reinstall_and_recover_data" `
                -PackageRole "upgrade"
            try
            {
                Add-AppxPackage -Path $UpgradeMsixFullPath -ErrorAction Stop
            }
            finally
            {
                $lease.Stream.Dispose()
            }
            $script:UpgradeRegistration = Wait-ForPackageVersion `
                -ExpectedName $PackageName `
                -ExpectedVersion $ExpectedUpgradePackageVersion `
                -TimeoutSeconds $OperationTimeoutSeconds
            $script:OwnsPackageRegistration = $UpgradeRegistration.PackageFullName
            $Evidence.lifecycle.reinstallRegistered = $true
            $Evidence.lifecycle.sentinelPresentAfterReinstall = Test-Sentinel `
                -Path $SentinelPath `
                -ExpectedContent $SentinelContent
            if (-not $Evidence.lifecycle.sentinelPresentAfterReinstall)
            {
                throw "The lifecycle sentinel is unavailable after reinstall."
            }
            $Evidence.lifecycle.canonicalDataAfterReinstall =
                Get-CanonicalDataSnapshot -RootPath $CanonicalDataRoot

            $explorer = Join-Path $env:SystemRoot "explorer.exe"
            Start-Process -FilePath $explorer -ArgumentList "shell:AppsFolder\$ApplicationUserModelId"
            $reinstalledProcess = Wait-ForStableSingleProcess `
                -ExpectedProcessName $ProcessName `
                -InstallLocation $UpgradeRegistration.InstallLocation `
                -TimeoutSeconds $OperationTimeoutSeconds
            if ($null -eq $reinstalledProcess)
            {
                throw "The reinstalled package did not launch from its exact install location."
            }
            $Evidence.lifecycle.reinstallLaunchVerified = $true
        }

        $Evidence.status = "passed"
        $Evidence.reason = "lifecycle_completed"
    }
}
catch
{
    $CapturedFailure = $_
    $Evidence.status = if (-not $PackageMutationStarted -and
        [string]::Equals(
            $FailureReason,
            "certificate_not_trusted_for_appx",
            [StringComparison]::Ordinal)) {
        "blocked"
    } else {
        "failed"
    }
    $Evidence.reason = if ([string]::IsNullOrWhiteSpace($FailureReason)) {
        "lifecycle_assertion_failed"
    } else {
        $FailureReason
    }
    $Evidence.failure = [ordered]@{
        stage = $CurrentStage
        exceptionType = $_.Exception.GetType().FullName
        hresult = $_.Exception.HResult
    }
}
finally
{
    if ($OwnedProcesses.Count -gt 0)
    {
        try
        {
            $Evidence.cleanup.ownedProcessesStopped = Stop-OwnedProcesses
        }
        catch
        {
            $Evidence.cleanup.ownedProcessCleanupFailed = $true
            $CleanupFailure = $_
        }
    }

    if ($PackageMutationStarted -and -not $LeaveInstalled)
    {
        try
        {
            $registration = $null
            if (-not [string]::IsNullOrWhiteSpace($OwnsPackageRegistration))
            {
                $registration = Get-PackageRegistrationByFullName `
                    -ExpectedName $PackageName `
                    -ExpectedPackageFullName $OwnsPackageRegistration
            }
            if ($null -eq $registration)
            {
                $candidate = Get-ExactPackageRegistration -ExpectedName $PackageName
                if ($null -ne $candidate)
                {
                    $candidateVersion = [version]$candidate.Version
                    if ($candidateVersion -notin @(
                            $ExpectedBasePackageVersion,
                            $ExpectedUpgradePackageVersion) -or
                        $candidate.Publisher -cne $BaseMetadata.Publisher)
                    {
                        throw "The post-mutation package registration does not match an exact harness-owned package."
                    }

                    $registration = $candidate
                    $script:OwnsPackageRegistration = $candidate.PackageFullName
                    $Evidence.cleanup.recoveredOwnedRegistration = $true
                }
            }
            if ($null -ne $registration)
            {
                $ownedPackageFullName = $registration.PackageFullName
                Remove-AppxPackage -Package $ownedPackageFullName -ErrorAction Stop
                Wait-ForPackageFullNameAbsent `
                    -ExpectedName $PackageName `
                    -ExpectedPackageFullName $ownedPackageFullName `
                    -TimeoutSeconds $OperationTimeoutSeconds
                $Evidence.cleanup.harnessPackageRegistrationRemoved = $true
            }
            $OwnsPackageRegistration = $null
        }
        catch
        {
            $CleanupFailure = $_
        }
    }

    if ($SentinelCreated)
    {
        try
        {
            if (-not (Test-ChildPath -Path $SentinelPath -ParentPath $CanonicalDataRoot))
            {
                throw "The lifecycle sentinel path moved outside the canonical data root."
            }
            if (Test-Path -LiteralPath $SentinelPath)
            {
                Remove-Item -LiteralPath $SentinelPath -Force
            }
            $SentinelRemoved = -not (Test-Path -LiteralPath $SentinelPath)
            $Evidence.cleanup.sentinelRemoved = $SentinelRemoved
        }
        catch
        {
            if ($null -eq $CleanupFailure)
            {
                $CleanupFailure = $_
            }
        }
    }

    if ($null -ne $CleanupFailure)
    {
        if ($null -eq $CapturedFailure)
        {
            $CapturedFailure = $CleanupFailure
            $Evidence.status = "failed"
            $Evidence.reason = "cleanup_failed"
            $Evidence.failure = [ordered]@{
                stage = "cleanup"
                exceptionType = $CleanupFailure.Exception.GetType().FullName
                hresult = $CleanupFailure.Exception.HResult
            }
        }
        else
        {
            $Evidence.cleanup["cleanupFailureType"] = $CleanupFailure.Exception.GetType().FullName
            $Evidence.cleanup["cleanupFailureHresult"] = $CleanupFailure.Exception.HResult
        }
    }

    $finalRegistration = $null
    try
    {
        $finalRegistration = Get-ExactPackageRegistration -ExpectedName $PackageName
    }
    catch
    {
        # The earlier lifecycle result remains authoritative if final observation is unavailable.
    }

    $Evidence.safety.packageMutationStarted = $PackageMutationStarted
    $Evidence.safety.userDataMutationStarted = $UserDataMutationStarted
    $Evidence.safety.appPackageAndCanonicalDataUntouchedByPreflight =
        -not $PackageMutationStarted -and -not $UserDataMutationStarted
    $Evidence.cleanup.ownedProcessesRemaining = @($OwnedProcesses.GetEnumerator() | Where-Object {
        Test-OwnedProcessStillRunning -Owned $_
    }).Count
    $Evidence.final = [ordered]@{
        packageRegistered = $null -ne $finalRegistration
        sentinelPresent = Test-Path -LiteralPath $SentinelPath -PathType Leaf
        ownedProcessesRunning = $Evidence.cleanup.ownedProcessesRemaining
        canonicalData = Get-CanonicalDataSnapshot -RootPath $CanonicalDataRoot
    }
    $Evidence.completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Write-EvidenceAtomically `
        -Value $Evidence `
        -Destination $EvidenceFullPath `
        -AllowOverwrite:$OverwriteEvidence
}

if ($null -ne $CapturedFailure)
{
    throw "Windows release lifecycle acceptance ended with status '$($Evidence.status)' at stage '$CurrentStage'. See the machine-readable evidence file."
}

Write-Output $EvidenceFullPath
