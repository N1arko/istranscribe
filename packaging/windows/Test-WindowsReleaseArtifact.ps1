#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$ReleaseDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#acceptance
# @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#release-contract
# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#release-contract
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance
$ExpectedProduct = "isTranscribe"
$ExpectedReleaseManifestSchema = "infra-005-release-v3"
$ExpectedDependencyManifestSchema = "infra-005-dependencies-v2"
$ExpectedRuntimeIdentifier = "win-x64"
$ExpectedArchitecture = "x64"
$ExpectedMinimumWindowsVersion = "10.0.19041.0"
$ExpectedWindowsSdkNetRefVersion = "10.0.26100.84"
$ExpectedWindowsSdkToolsBinaryVersion = "10.0.28000.0"
$ExpectedBuildToolsPackage = "Microsoft.Windows.SDK.BuildTools"
$ExpectedRuntimePackage = "Microsoft.NETCore.App.Runtime.win-x64"
$ExpectedSdkReferencePackage = "Microsoft.Windows.SDK.NET.Ref"
$ExpectedApplicationExecutable = "IsTranscribe.Desktop.exe"
$ExpectedApplicationId = "App"
$ExpectedStartupTaskId = "isTranscribeStartup"
$ExpectedStartupParameters = "--autostart"
$ChecksumFileName = "SHA256SUMS.txt"
$ReleaseManifestFileName = "release-manifest.json"
$DependencyInventoryFileName = "dependency-license-inventory.json"
$NoticePolicyPath = Join-Path $PSScriptRoot "notices\third-party-notice-policy.json"
$DevelopmentMarkerFileName = "DEVELOPMENT_ONLY.txt"
$DevelopmentCertificateFileName = "isTranscribe-development-certificate.cer"
$BuildToolsProjectFileName = "WindowsSdkTools.csproj"
$DesktopLockRelativePath = "src\IsTranscribe.App.Windows\packages.lock.json"
$ReleaseIdentityPolicyFileName = "release-identity-policy.json"
$Sha256HashMethod = "http://www.w3.org/2001/04/xmlenc#sha256"
$BlockSizeBytes = 65536

$ExpectedAssets = [ordered]@{
    "Assets\StoreLogo.png" = @(50, 50)
    "Assets\Square44x44Logo.png" = @(44, 44)
    "Assets\Square150x150Logo.png" = @(150, 150)
    "Assets\Wide310x150Logo.png" = @(310, 150)
    "Assets\Square310x310Logo.png" = @(310, 310)
}

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing
if ($null -eq ("IsTranscribe.Release.ArtifactWinTrustVerifier" -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace IsTranscribe.Release
{
    public static class ArtifactWinTrustVerifier
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

function Assert-Condition
{
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition)
    {
        throw $Message
    }
}

function Assert-Equal
{
    param(
        [AllowNull()][object]$Actual,
        [AllowNull()][object]$Expected,
        [Parameter(Mandatory)][string]$Field
    )

    if (-not [object]::Equals($Actual, $Expected))
    {
        throw "$Field must be '$Expected'; found '$Actual'."
    }
}

function Assert-TextEqual
{
    param(
        [AllowNull()][string]$Actual,
        [AllowNull()][string]$Expected,
        [Parameter(Mandatory)][string]$Field,
        [switch]$IgnoreCase
    )

    $comparison = if ($IgnoreCase)
    {
        [StringComparison]::OrdinalIgnoreCase
    }
    else
    {
        [StringComparison]::Ordinal
    }
    if (-not [string]::Equals($Actual, $Expected, $comparison))
    {
        throw "$Field must be '$Expected'; found '$Actual'."
    }
}

function Assert-NonEmptyText
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value))
    {
        throw "$Field must be a non-empty string."
    }
}

function Assert-JsonBoolean
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][bool]$Expected,
        [Parameter(Mandatory)][string]$Field
    )

    Assert-Condition -Condition ($Value -is [bool]) -Message "$Field must be a JSON boolean."
    Assert-Equal -Actual $Value -Expected $Expected -Field $Field
}

function Assert-JsonBooleanType
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field
    )

    Assert-Condition -Condition ($Value -is [bool]) -Message "$Field must be a JSON boolean."
}

function Assert-JsonInteger
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [int64]$Minimum = [int64]::MinValue
    )

    $isInteger = $Value -is [sbyte] -or
        $Value -is [byte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64]
    Assert-Condition -Condition $isInteger -Message "$Field must be a JSON integer."
    Assert-Condition -Condition ([int64]$Value -ge $Minimum) -Message "$Field must be at least $Minimum."
}

function Get-OptionalProperty
{
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property)
    {
        return $null
    }
    $property.Value
}

function Read-JsonFile
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    try
    {
        Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch
    {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Read-ReleaseIdentityPolicy
{
    param([Parameter(Mandatory)][string]$InstallerRoot)

    $path = Join-Path $InstallerRoot $ReleaseIdentityPolicyFileName
    Assert-Condition -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
        -Message "Checked-in release identity policy '$path' is missing."
    $policy = Read-JsonFile -Path $path -Description "Release identity policy"
    Assert-TextEqual -Actual ([string]$policy.schemaVersion) `
        -Expected "infra-005-release-identity-policy-v1" `
        -Field "Release identity policy schemaVersion"
    Assert-TextEqual -Actual ([string]$policy.packageName) -Expected "isTranscribe.Desktop" `
        -Field "Release identity policy packageName"
    Assert-TextEqual -Actual ([string]$policy.publisherDisplayName) -Expected "isTranscribe" `
        -Field "Release identity policy publisherDisplayName"
    Assert-TextEqual -Actual ([string]$policy.developmentPublisher) -Expected "CN=isTranscribe Development" `
        -Field "Release identity policy developmentPublisher"
    Assert-JsonBoolean -Value $policy.rollover.requiresReviewedPolicyChange -Expected $true `
        -Field "Release identity rollover review flag"
    Assert-NonEmptyText -Value $policy.rollover.instructions -Field "Release identity rollover instructions"

    $publisherProperty = $policy.PSObject.Properties["productionPublisherSubjects"]
    Assert-Condition -Condition ($null -ne $publisherProperty) `
        -Message "Release identity policy has no productionPublisherSubjects array."
    $productionPublishers = @($publisherProperty.Value)
    $uniquePublishers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($publisher in $productionPublishers)
    {
        Assert-NonEmptyText -Value $publisher -Field "Approved production publisher subject"
        Assert-Condition -Condition $uniquePublishers.Add([string]$publisher) `
            -Message "Release identity policy contains duplicate production publisher '$publisher'."
        Assert-Condition -Condition (-not [string]::Equals(
            [string]$publisher,
            [string]$policy.developmentPublisher,
            [StringComparison]::OrdinalIgnoreCase)) `
            -Message "Development publisher cannot be approved for production."
    }

    [pscustomobject]@{
        Path = $path
        SchemaVersion = [string]$policy.schemaVersion
        Sha256 = Get-LowerSha256 -Path $path
        PackageName = [string]$policy.packageName
        PublisherDisplayName = [string]$policy.publisherDisplayName
        DevelopmentPublisher = [string]$policy.developmentPublisher
        ProductionPublisherSubjects = @($productionPublishers)
    }
}

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256
{
    param([AllowEmptyString()][string]$Text)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-OrdinalSortedUnique
{
    param([AllowEmptyCollection()][string[]]$Values)
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in @($Values)) { [void]$set.Add($value) }
    $result = [string[]]@($set)
    [Array]::Sort($result, [StringComparer]::Ordinal)
    $result
}

function Assert-PackagedDependencyPayloadTrees
{
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)][string]$PackageRoot)

    $policy = Read-JsonFile -Path $NoticePolicyPath -Description "Third-party notice policy"
    $policyByIdentity = @{}
    foreach ($entry in @($policy.packages)) { $policyByIdentity["$($entry.id)/$($entry.version)"] = $entry }
    foreach ($dependency in @($Inventory.dependencies))
    {
        $identity = "$($dependency.id)/$($dependency.version)"
        $builder = [Text.StringBuilder]::new()
        foreach ($asset in @(Get-OrdinalSortedUnique -Values @($dependency.redistributedAssets)))
        {
            $path = Join-Path $PackageRoot $asset
            Assert-Condition -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
                -Message "Attributed package asset '$asset' for '$identity' is absent from the MSIX."
            [void]$builder.Append($asset).Append("`n").Append((Get-LowerSha256 -Path $path)).Append("`n")
        }
        $treeHash = Get-TextSha256 -Text $builder.ToString()
        Assert-TextEqual -Actual $treeHash -Expected ([string]$policyByIdentity[$identity].payloadTreeSha256) `
            -Field "Packaged payload tree '$identity'" -IgnoreCase
    }

    $interOverride = @($policy.overrides | Where-Object package -EQ 'Avalonia.Fonts.Inter/12.1.0') | Select-Object -First 1
    Assert-TextEqual -Actual (Get-LowerSha256 -Path (Join-Path $PackageRoot 'Avalonia.Fonts.Inter.dll')) `
        -Expected ([string]$interOverride.payloadSha256) -Field "Packaged Inter payload SHA-256" -IgnoreCase
}

function Assert-SafeTopLevelFileName
{
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-Condition -Condition (-not [string]::IsNullOrWhiteSpace($Name)) -Message "$Description is empty."
    Assert-Condition -Condition (-not [IO.Path]::IsPathRooted($Name)) -Message "$Description must be a top-level file name."
    Assert-Condition -Condition ([IO.Path]::GetFileName($Name) -eq $Name) -Message "$Description must not contain a directory path."
    Assert-Condition -Condition ($Name -notin @(".", "..")) -Message "$Description is not a safe file name."
}

function Assert-ExactReleaseFileAllowlist
{
    param(
        [Parameter(Mandatory)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$MsixFileName
    )

    $privateKeyExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(
        ".pfx", ".p12", ".pkcs12", ".pem", ".key", ".pvk", ".snk", ".jks", ".keystore",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".cab", ".nupkg"))
    {
        [void]$privateKeyExtensions.Add($extension)
    }
    $privateMaterial = @($Files | Where-Object {
        $privateKeyExtensions.Contains($_.Extension) -or
        $_.Name -match '(?i)(private[-_. ]?key|signing[-_. ]?secret)'
    })
    if ($privateMaterial.Count -gt 0)
    {
        throw "Release directory contains a forbidden private-key or archive file: $($privateMaterial.Name -join ', ')."
    }

    $expectedNames = @(
        $MsixFileName,
        $ReleaseManifestFileName,
        $DependencyInventoryFileName,
        $ChecksumFileName)
    if ($Channel -eq "development")
    {
        $expectedNames += @($DevelopmentMarkerFileName, $DevelopmentCertificateFileName)
    }
    $actualNames = @($Files | ForEach-Object Name)
    Assert-Equal -Actual $actualNames.Count -Expected $expectedNames.Count -Field "Release top-level file count"
    foreach ($expectedName in $expectedNames)
    {
        Assert-Equal -Actual @($actualNames | Where-Object {
            [string]::Equals($_, $expectedName, [StringComparison]::Ordinal)
        }).Count -Expected 1 -Field "Top-level file '$expectedName' occurrence count"
    }
    foreach ($actualName in $actualNames)
    {
        Assert-Condition -Condition ($expectedNames -ccontains $actualName) `
            -Message "Release top-level file '$actualName' is outside the $Channel allowlist."
    }
}

function Assert-ChildPath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-Condition -Condition ($fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) `
        -Message "Path '$fullPath' escapes the verifier-owned temporary root."
}

function Remove-SafeTemporaryDirectory
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParentPath
    )

    Assert-ChildPath -Path $Path -ParentPath $ParentPath
    if (Test-Path -LiteralPath $Path)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function ConvertTo-ExpectedMsixVersion
{
    param([Parameter(Mandatory)][string]$SemanticVersion)

    $pattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<metadata>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $match = [regex]::Match($SemanticVersion, $pattern)
    Assert-Condition -Condition $match.Success -Message "semanticVersion '$SemanticVersion' is not valid semantic versioning."

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $patch = [int]$match.Groups['patch'].Value
    foreach ($component in @($major, $minor, $patch))
    {
        Assert-Condition -Condition ($component -le 65535) -Message "MSIX version components must not exceed 65535."
    }

    $prerelease = $match.Groups['prerelease'].Value
    if ([string]::IsNullOrWhiteSpace($prerelease))
    {
        $revision = 65535
    }
    else
    {
        $identifiers = $prerelease.Split('.')
        $stage = $identifiers[0].ToLowerInvariant()
        $sequence = 0
        if ($identifiers.Count -gt 1)
        {
            $parsed = [int]::TryParse($identifiers[-1], [ref]$sequence)
            Assert-Condition -Condition ($parsed -and $sequence -ge 0 -and $sequence -le 9999) `
                -Message "Prerelease versions must end in a sequence from 0 to 9999."
        }
        $stageBase = switch ($stage)
        {
            "dev" { 0 }
            "alpha" { 10000 }
            "beta" { 20000 }
            "rc" { 30000 }
            default { throw "Supported prerelease stages are dev, alpha, beta and rc." }
        }
        $revision = $stageBase + $sequence
    }

    "$major.$minor.$patch.$revision"
}

function ConvertTo-DateTimeOffsetValue
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Field
    )

    if ($Value -is [DateTimeOffset])
    {
        return [DateTimeOffset]$Value
    }
    if ($Value -is [DateTime])
    {
        return [DateTimeOffset]([DateTime]$Value)
    }
    $parsed = [DateTimeOffset]::MinValue
    $valid = [DateTimeOffset]::TryParse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed)
    Assert-Condition -Condition $valid -Message "$Field is not a round-trip timestamp."
    $parsed
}

function Assert-RoundTripTimestamp
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Field
    )

    [void](ConvertTo-DateTimeOffsetValue -Value $Value -Field $Field)
}

function Read-And-VerifyChecksums
{
    param(
        [Parameter(Mandatory)][string]$ReleaseRoot,
        [Parameter(Mandatory)][System.IO.FileInfo[]]$TopLevelFiles
    )

    $checksumPath = Join-Path $ReleaseRoot $ChecksumFileName
    Assert-Condition -Condition (Test-Path -LiteralPath $checksumPath -PathType Leaf) `
        -Message "$ChecksumFileName is missing."

    $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lineNumber = 0
    foreach ($line in @(Get-Content -LiteralPath $checksumPath -Encoding ascii))
    {
        $lineNumber++
        $match = [regex]::Match($line, '^(?<hash>[0-9A-Fa-f]{64}) {2}(?<name>[^\r\n]+)$')
        Assert-Condition -Condition $match.Success -Message "$ChecksumFileName line $lineNumber has an invalid format."
        $name = $match.Groups['name'].Value
        Assert-SafeTopLevelFileName -Name $name -Description "$ChecksumFileName line $lineNumber file"
        Assert-Condition -Condition (-not [string]::Equals($name, $ChecksumFileName, [StringComparison]::OrdinalIgnoreCase)) `
            -Message "$ChecksumFileName must not hash itself."
        Assert-Condition -Condition (-not $entries.ContainsKey($name)) -Message "$ChecksumFileName lists '$name' more than once."
        $entries.Add($name, $match.Groups['hash'].Value.ToLowerInvariant())
    }

    $expectedFiles = @($TopLevelFiles | Where-Object { $_.Name -ne $ChecksumFileName })
    Assert-Equal -Actual $entries.Count -Expected $expectedFiles.Count -Field "$ChecksumFileName entry count"
    foreach ($file in $expectedFiles)
    {
        Assert-Condition -Condition $entries.ContainsKey($file.Name) -Message "$ChecksumFileName does not cover '$($file.Name)'."
        $actualHash = Get-LowerSha256 -Path $file.FullName
        Assert-TextEqual -Actual $entries[$file.Name] -Expected $actualHash -Field "Checksum for $($file.Name)" -IgnoreCase
    }
    foreach ($name in $entries.Keys)
    {
        Assert-Condition -Condition (Test-Path -LiteralPath (Join-Path $ReleaseRoot $name) -PathType Leaf) `
            -Message "$ChecksumFileName references missing top-level file '$name'."
    }

    $entries
}

function Resolve-LockedMakeAppx
{
    param([Parameter(Mandatory)][string]$InstallerRoot)

    $lockPath = Join-Path $InstallerRoot "packages.lock.json"
    $assetsPath = Join-Path $InstallerRoot "obj\project.assets.json"
    $projectPath = Join-Path $InstallerRoot $BuildToolsProjectFileName
    foreach ($required in @($lockPath, $projectPath))
    {
        Assert-Condition -Condition (Test-Path -LiteralPath $required -PathType Leaf) `
            -Message "Locked Windows SDK tool input '$required' is missing."
    }

    $restoreOutput = @(& dotnet restore $projectPath --locked-mode --nologo 2>&1)
    $restoreExitCode = $LASTEXITCODE
    if ($restoreExitCode -ne 0)
    {
        throw "Locked Windows SDK BuildTools restore failed (exit $restoreExitCode): $($restoreOutput -join [Environment]::NewLine)"
    }
    Assert-Condition -Condition (Test-Path -LiteralPath $assetsPath -PathType Leaf) `
        -Message "Locked Windows SDK restore did not produce '$assetsPath'."

    $lock = Read-JsonFile -Path $lockPath -Description "Windows SDK tools lock file"
    $framework = $lock.dependencies.PSObject.Properties | Select-Object -First 1
    Assert-Condition -Condition ($null -ne $framework) -Message "Windows SDK tools lock file has no target framework."
    $lockedPackageProperty = $framework.Value.PSObject.Properties[$ExpectedBuildToolsPackage]
    Assert-Condition -Condition ($null -ne $lockedPackageProperty) `
        -Message "Windows SDK tools lock file does not pin $ExpectedBuildToolsPackage."
    $lockedVersion = [string]$lockedPackageProperty.Value.resolved
    Assert-NonEmptyText -Value $lockedVersion -Field "Locked BuildTools version"
    Assert-TextEqual -Actual ([string]$lockedPackageProperty.Value.type) -Expected "Direct" `
        -Field "Locked BuildTools dependency type" -IgnoreCase
    Assert-TextEqual -Actual ([string]$lockedPackageProperty.Value.requested) `
        -Expected "[$lockedVersion, $lockedVersion]" -Field "Locked BuildTools requested range"
    $lockedContentHash = [string]$lockedPackageProperty.Value.contentHash
    Assert-NonEmptyText -Value $lockedContentHash -Field "Locked BuildTools content hash"

    $assets = Read-JsonFile -Path $assetsPath -Description "Windows SDK tools restore assets"
    $libraryName = "$ExpectedBuildToolsPackage/$lockedVersion"
    $libraryProperty = $assets.libraries.PSObject.Properties[$libraryName]
    Assert-Condition -Condition ($null -ne $libraryProperty) `
        -Message "Restore assets do not contain locked library '$libraryName'."
    Assert-TextEqual -Actual ([string]$libraryProperty.Value.type) -Expected "package" `
        -Field "BuildTools restored library type" -IgnoreCase
    Assert-TextEqual -Actual ([string]$libraryProperty.Value.sha512) -Expected $lockedContentHash `
        -Field "BuildTools restore/lock content hash"
    $packageFolder = $assets.packageFolders.PSObject.Properties | Select-Object -First 1
    Assert-Condition -Condition ($null -ne $packageFolder) -Message "Restore assets do not identify the NuGet package root."
    $packageRoot = Join-Path $packageFolder.Name ([string]$libraryProperty.Value.path)
    Assert-Condition -Condition (Test-Path -LiteralPath $packageRoot -PathType Container) `
        -Message "Locked BuildTools package root '$packageRoot' is missing."

    $packageHashFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter "*.nupkg.sha512" -File)
    Assert-Equal -Actual $packageHashFiles.Count -Expected 1 -Field "BuildTools package SHA-512 sidecar count"
    $packageArchives = @(Get-ChildItem -LiteralPath $packageRoot -Filter "*.nupkg" -File)
    Assert-Equal -Actual $packageArchives.Count -Expected 1 -Field "BuildTools NuGet archive count"
    $rawPackageHash = (Get-Content -LiteralPath $packageHashFiles[0].FullName -Raw).Trim()
    $computedRawPackageHash = [Convert]::ToBase64String(
        [Convert]::FromHexString((Get-FileHash -LiteralPath $packageArchives[0].FullName -Algorithm SHA512).Hash))
    Assert-TextEqual -Actual $rawPackageHash -Expected $computedRawPackageHash `
        -Field "BuildTools archive SHA-512 sidecar"
    $packageMetadataPath = Join-Path $packageRoot ".nupkg.metadata"
    Assert-Condition -Condition (Test-Path -LiteralPath $packageMetadataPath -PathType Leaf) `
        -Message "BuildTools NuGet metadata is missing."
    $packageMetadata = Read-JsonFile -Path $packageMetadataPath -Description "BuildTools NuGet metadata"
    Assert-TextEqual -Actual ([string]$packageMetadata.contentHash) -Expected $lockedContentHash `
        -Field "BuildTools NuGet metadata/lock content hash"

    $expectedToolRoot = Join-Path $packageRoot "bin\$ExpectedWindowsSdkToolsBinaryVersion\x64"
    $makeAppxPath = Join-Path $expectedToolRoot "makeappx.exe"
    $signToolPath = Join-Path $expectedToolRoot "signtool.exe"
    Assert-Condition -Condition (Test-Path -LiteralPath $makeAppxPath -PathType Leaf) `
        -Message "Exact locked MakeAppx path '$makeAppxPath' is missing."
    Assert-Condition -Condition (Test-Path -LiteralPath $signToolPath -PathType Leaf) `
        -Message "Exact locked SignTool path '$signToolPath' is missing."
    Assert-TextEqual -Actual (Get-Item -LiteralPath $makeAppxPath).VersionInfo.ProductVersion `
        -Expected $lockedVersion -Field "MakeAppx product version"
    Assert-TextEqual -Actual (Get-Item -LiteralPath $signToolPath).VersionInfo.ProductVersion `
        -Expected $lockedVersion -Field "SignTool product version"

    [pscustomobject]@{
        Version = $lockedVersion
        ContentHash = $lockedContentHash
        GlobalPackagesRoot = $packageFolder.Name
        PackageRoot = $packageRoot
        MakeAppxPath = $makeAppxPath
        SignToolPath = $signToolPath
    }
}

function Assert-MsixSignatureIntegrity
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][object]$ReleaseManifest,
        [Parameter(Mandatory)][string]$SignToolPath
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $authenticodeStatus = $signature.Status.ToString()
    Assert-Condition -Condition ($authenticodeStatus -notin @("HashMismatch", "NotSigned")) `
        -Message "MSIX Authenticode integrity failed with status '$authenticodeStatus'."
    Assert-Condition -Condition ($null -ne $signature.SignerCertificate) `
        -Message "MSIX has no embedded signer certificate."
    Assert-TextEqual -Actual $signature.SignerCertificate.Subject `
        -Expected ([string]$ReleaseManifest.signing.certificateSubject) -Field "Embedded signer subject"
    Assert-TextEqual -Actual $signature.SignerCertificate.Thumbprint `
        -Expected ([string]$ReleaseManifest.signing.certificateThumbprint) `
        -Field "Embedded signer thumbprint" -IgnoreCase

    $winTrustResult = [IsTranscribe.Release.ArtifactWinTrustVerifier]::Verify($Path)
    $winTrustCode = '0x{0:X8}' -f $winTrustResult
    $signToolArguments = @(
        "verify", "/pa", "/all", "/sha1",
        [string]$ReleaseManifest.signing.certificateThumbprint,
        "/v", "/debug")
    if ($Channel -eq "public")
    {
        $signToolArguments += "/tw"
    }
    $signToolArguments += $Path
    $signToolOutput = @(& $SignToolPath @signToolArguments 2>&1)
    $signToolExitCode = $LASTEXITCODE

    if ($Channel -eq "public")
    {
        Assert-Equal -Actual ([uint32]$winTrustResult) -Expected ([uint32]0) `
            -Field "Public WinVerifyTrust result"
        Assert-Equal -Actual $signToolExitCode -Expected 0 -Field "Public SignTool verify exit code"
        Assert-TextEqual -Actual $authenticodeStatus -Expected "Valid" -Field "Public Authenticode status"
        Assert-Condition -Condition ($null -ne $signature.TimeStamperCertificate) `
            -Message "Public MSIX has no embedded timestamp signer."
        Assert-TextEqual -Actual ([string]$ReleaseManifest.signing.trustStatus) -Expected "Valid" `
            -Field "Public manifest trustStatus"
        return [pscustomobject]@{
            Status = "Valid"
            AuthenticodeStatus = $authenticodeStatus
            WinTrustCode = $winTrustCode
            SignToolExitCode = $signToolExitCode
        }
    }

    if ($winTrustResult -eq 0)
    {
        Assert-Equal -Actual $signToolExitCode -Expected 0 -Field "Development SignTool verify exit code"
        Assert-TextEqual -Actual $authenticodeStatus -Expected "Valid" -Field "Development Authenticode status"
        Assert-TextEqual -Actual ([string]$ReleaseManifest.signing.trustStatus) -Expected "Valid" `
            -Field "Development manifest trustStatus"
        $status = "Valid"
    }
    elseif ($winTrustCode -eq "0x800B0109")
    {
        Assert-Condition -Condition ($signToolExitCode -ne 0) `
            -Message "Development SignTool unexpectedly trusted a WinVerifyTrust-untrusted package."
        Assert-TextEqual -Actual $authenticodeStatus -Expected "UnknownError" `
            -Field "Development untrusted-root Authenticode status"
        Assert-TextEqual -Actual ([string]$ReleaseManifest.signing.trustStatus) `
            -Expected "DevelopmentUntrustedRoot" -Field "Development manifest trustStatus"
        $status = "DevelopmentUntrustedRoot"
    }
    else
    {
        $signToolSummary = ($signToolOutput -join [Environment]::NewLine).Trim()
        throw "Development MSIX signature integrity failed: Authenticode=$authenticodeStatus; WinVerifyTrust=$winTrustCode; SignToolExit=$signToolExitCode. $signToolSummary"
    }

    [pscustomobject]@{
        Status = $status
        AuthenticodeStatus = $authenticodeStatus
        WinTrustCode = $winTrustCode
        SignToolExitCode = $signToolExitCode
    }
}

function Assert-LicenseInventory
{
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)][object]$ReleaseManifest,
        [Parameter(Mandatory)][string]$InventoryPath,
        [Parameter(Mandatory)][string]$DesktopLockPath,
        [Parameter(Mandatory)][string]$PackagedDepsPath,
        [Parameter(Mandatory)][object]$LockedTools
    )

    Assert-TextEqual -Actual ([string]$Inventory.schemaVersion) -Expected $ExpectedDependencyManifestSchema `
        -Field "Dependency inventory schemaVersion"
    Assert-TextEqual -Actual ([string]$Inventory.product) -Expected $ExpectedProduct -Field "Dependency inventory product"
    Assert-TextEqual -Actual ([string]$Inventory.semanticVersion) -Expected ([string]$ReleaseManifest.semanticVersion) `
        -Field "Dependency inventory semanticVersion"
    Assert-TextEqual -Actual ([string]$Inventory.generatedUtc) -Expected ([string]$ReleaseManifest.build.createdUtc) `
        -Field "Dependency inventory generatedUtc"
    Assert-RoundTripTimestamp -Value ([string]$Inventory.generatedUtc) -Field "Dependency inventory generatedUtc"

    $dependencies = @($Inventory.dependencies)
    $noticePolicy = Read-JsonFile -Path $NoticePolicyPath -Description "Third-party notice policy"
    Assert-TextEqual -Actual ([string]$Inventory.notice.policySha256) `
        -Expected (Get-LowerSha256 -Path $NoticePolicyPath) -Field "Notice policy SHA-256" -IgnoreCase
    Assert-TextEqual -Actual ([string]$noticePolicy.schemaVersion) `
        -Expected "infra-005-third-party-notices-v1" -Field "Notice policy schema"
    Assert-Equal -Actual @($noticePolicy.packages).Count -Expected 43 -Field "Notice policy package count"
    $noticePolicyByIdentity = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($policyEntry in @($noticePolicy.packages))
    {
        $policyIdentity = "$($policyEntry.id)/$($policyEntry.version)"
        Assert-Condition -Condition $noticePolicyByIdentity.TryAdd($policyIdentity, $policyEntry) `
            -Message "Notice policy contains duplicate '$policyIdentity'."
    }
    Assert-JsonInteger -Value $Inventory.packageCount -Field "Dependency inventory packageCount" -Minimum 0
    Assert-JsonInteger -Value $Inventory.redistributedPackageCount `
        -Field "Dependency inventory redistributedPackageCount" -Minimum 1
    Assert-Equal -Actual ([int64]$Inventory.packageCount) -Expected ([int64]$dependencies.Count) `
        -Field "Dependency inventory packageCount"
    Assert-Condition -Condition ($dependencies.Count -gt 0) -Message "Dependency inventory is empty."

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inventoryById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $missingLicenseCount = 0
    $redistributedCount = 0
    foreach ($dependency in $dependencies)
    {
        Assert-NonEmptyText -Value $dependency.id -Field "Dependency id"
        Assert-Condition -Condition $ids.Add([string]$dependency.id) `
            -Message "Dependency inventory contains duplicate id '$($dependency.id)'."
        $inventoryById.Add([string]$dependency.id, $dependency)
        Assert-NonEmptyText -Value $dependency.version -Field "Dependency '$($dependency.id)' version"
        $noticeIdentity = "$($dependency.id)/$($dependency.version)"
        Assert-Condition -Condition $noticePolicyByIdentity.ContainsKey($noticeIdentity) `
            -Message "Dependency '$noticeIdentity' is absent from the checked-in notice policy."
        $noticePolicyEntry = $noticePolicyByIdentity[$noticeIdentity]
        Assert-NonEmptyText -Value $dependency.acquisition -Field "Dependency '$($dependency.id)' acquisition"
        Assert-Condition -Condition ($dependency.direct -is [bool]) `
            -Message "Dependency '$($dependency.id)' direct must be a JSON boolean."
        Assert-NonEmptyText -Value $dependency.contentHashSha512 -Field "Dependency '$($dependency.id)' contentHashSha512"
        try
        {
            $decodedHash = [Convert]::FromBase64String([string]$dependency.contentHashSha512)
        }
        catch
        {
            throw "Dependency '$($dependency.id)' contentHashSha512 is not valid Base64."
        }
        Assert-Equal -Actual $decodedHash.Length -Expected 64 -Field "Dependency '$($dependency.id)' SHA-512 byte length"

        $licenseExpression = Get-OptionalProperty -Object $dependency.license -Name "expression"
        $licenseFile = Get-OptionalProperty -Object $dependency.license -Name "file"
        $licenseUrl = Get-OptionalProperty -Object $dependency.license -Name "url"
        $hasLicenseMetadata = -not (
            [string]::IsNullOrWhiteSpace([string]$licenseExpression) -and
            [string]::IsNullOrWhiteSpace([string]$licenseFile) -and
            [string]::IsNullOrWhiteSpace([string]$licenseUrl))
        if (-not $hasLicenseMetadata)
        {
            $missingLicenseCount++
        }
        Assert-JsonBooleanType -Value $dependency.redistributed -Field "Dependency '$($dependency.id)' redistributed"
        Assert-JsonBooleanType -Value $dependency.requireLicenseAcceptance `
            -Field "Dependency '$($dependency.id)' requireLicenseAcceptance"
        $redistributedAssets = @($dependency.redistributedAssets)
        Assert-NonEmptyText -Value $dependency.notice.strategy -Field "Dependency '$($dependency.id)' notice strategy"
        Assert-Condition -Condition (([string]$dependency.notice.assetListSha256) -match '^[0-9a-f]{64}$') `
            -Message "Dependency '$($dependency.id)' notice asset-list SHA-256 is invalid."
        Assert-Condition -Condition (([string]$dependency.notice.payloadTreeSha256) -match '^[0-9a-f]{64}$') `
            -Message "Dependency '$($dependency.id)' notice payload-tree SHA-256 is invalid."
        Assert-TextEqual -Actual ([string]$dependency.contentHashSha512) `
            -Expected ([string]$noticePolicyEntry.contentHashSha512) -Field "Dependency '$noticeIdentity' policy SHA-512"
        Assert-TextEqual -Actual ([string]$dependency.notice.assetListSha256) `
            -Expected ([string]$noticePolicyEntry.assetListSha256) -Field "Dependency '$noticeIdentity' policy asset-list SHA-256" -IgnoreCase
        Assert-TextEqual -Actual ([string]$dependency.notice.payloadTreeSha256) `
            -Expected ([string]$noticePolicyEntry.payloadTreeSha256) -Field "Dependency '$noticeIdentity' policy payload-tree SHA-256" -IgnoreCase
        Assert-Condition -Condition ([bool]$dependency.redistributed -eq [bool]$noticePolicyEntry.redistributed) `
            -Message "Dependency '$noticeIdentity' redistribution flag differs from notice policy."
        if ([bool]$dependency.redistributed)
        {
            $redistributedCount++
            Assert-Condition -Condition ($redistributedAssets.Count -gt 0 -and @($dependency.notice.material).Count -gt 0) `
                -Message "Redistributed dependency '$($dependency.id)' has incomplete notice evidence."
        }
        else
        {
            Assert-Equal -Actual $redistributedAssets.Count -Expected 0 `
                -Field "Excluded dependency '$($dependency.id)' asset count"
            Assert-TextEqual -Actual ([string]$dependency.notice.strategy) -Expected "excluded-no-payload" `
                -Field "Excluded dependency '$($dependency.id)' notice strategy"
        }
    }
    Assert-JsonInteger -Value $Inventory.missingLicenseMetadataCount `
        -Field "Dependency inventory missingLicenseMetadataCount" -Minimum 0
    Assert-Equal -Actual ([int64]$Inventory.missingLicenseMetadataCount) -Expected ([int64]$missingLicenseCount) `
        -Field "Dependency inventory missingLicenseMetadataCount"
    Assert-Equal -Actual $missingLicenseCount -Expected 0 -Field "Dependencies without license metadata"
    Assert-Equal -Actual $redistributedCount -Expected ([int]$Inventory.redistributedPackageCount) `
        -Field "Dependency inventory redistributedPackageCount"
    Assert-Equal -Actual $dependencies.Count -Expected 43 -Field "Exact notice-policy package count"
    Assert-Equal -Actual $redistributedCount -Expected 33 -Field "Exact redistributed package count"
    Assert-TextEqual -Actual ([string]$Inventory.notice.file) -Expected "THIRD-PARTY-NOTICES.txt" `
        -Field "Dependency inventory notice file"
    Assert-TextEqual -Actual ([string]$Inventory.notice.policyFile) -Expected "third-party-notice-policy.json" `
        -Field "Dependency inventory notice policy file"
    foreach ($hashField in @("sha256", "policySha256"))
    {
        Assert-Condition -Condition (([string]$Inventory.notice.$hashField) -match '^[0-9a-f]{64}$') `
            -Message "Dependency inventory notice $hashField is invalid."
    }
    Assert-JsonInteger -Value $Inventory.notice.sizeBytes -Field "Dependency inventory notice sizeBytes" -Minimum 1

    Assert-Condition -Condition (Test-Path -LiteralPath $DesktopLockPath -PathType Leaf) `
        -Message "Desktop package lock '$DesktopLockPath' is missing."
    $desktopLock = Read-JsonFile -Path $DesktopLockPath -Description "Desktop package lock"
    $expectedDependencies = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($frameworkName in @("net10.0", "net10.0/win-x64"))
    {
        $frameworkProperty = $desktopLock.dependencies.PSObject.Properties[$frameworkName]
        Assert-Condition -Condition ($null -ne $frameworkProperty) `
            -Message "Desktop package lock has no '$frameworkName' graph."
        foreach ($packageProperty in $frameworkProperty.Value.PSObject.Properties)
        {
            $lockEntry = $packageProperty.Value
            if ([string]::Equals([string]$lockEntry.type, "Project", [StringComparison]::OrdinalIgnoreCase))
            {
                continue
            }
            Assert-NonEmptyText -Value $lockEntry.resolved -Field "Locked dependency '$($packageProperty.Name)' version"
            Assert-NonEmptyText -Value $lockEntry.contentHash -Field "Locked dependency '$($packageProperty.Name)' content hash"
            if ($expectedDependencies.ContainsKey($packageProperty.Name))
            {
                $existing = $expectedDependencies[$packageProperty.Name]
                Assert-TextEqual -Actual ([string]$lockEntry.resolved) -Expected ([string]$existing.Version) `
                    -Field "Cross-framework lock version for '$($packageProperty.Name)'"
                Assert-TextEqual -Actual ([string]$lockEntry.contentHash) -Expected ([string]$existing.ContentHash) `
                    -Field "Cross-framework lock content hash for '$($packageProperty.Name)'"
                continue
            }
            $expectedDependencies.Add($packageProperty.Name, [pscustomobject]@{
                Id = $packageProperty.Name
                Version = [string]$lockEntry.resolved
                ContentHash = [string]$lockEntry.contentHash
                Source = "DesktopLock"
            })
        }
    }

    $expectedDependencies.Add($ExpectedBuildToolsPackage, [pscustomobject]@{
        Id = $ExpectedBuildToolsPackage
        Version = [string]$LockedTools.Version
        ContentHash = [string]$LockedTools.ContentHash
        Source = "BuildToolsLock"
    })

    Assert-Condition -Condition (Test-Path -LiteralPath $PackagedDepsPath -PathType Leaf) `
        -Message "Packaged IsTranscribe.Desktop.deps.json is missing."
    $packagedDeps = Read-JsonFile -Path $PackagedDepsPath -Description "Packaged Desktop deps manifest"
    $runtimeLibraries = @($packagedDeps.libraries.PSObject.Properties | Where-Object {
        [string]::Equals([string]$_.Value.type, "runtimepack", [StringComparison]::OrdinalIgnoreCase)
    })
    Assert-Equal -Actual $runtimeLibraries.Count -Expected 1 -Field "Packaged runtime-pack library count"
    $runtimeMatches = @($runtimeLibraries | Where-Object {
        $_.Name -like "runtimepack.$ExpectedRuntimePackage/*"
    })
    Assert-Equal -Actual $runtimeMatches.Count -Expected 1 -Field "Packaged .NET runtime-pack occurrence count"
    $runtimeIdentity = [string]$runtimeMatches[0].Name
    $runtimeSeparator = $runtimeIdentity.LastIndexOf('/')
    Assert-Condition -Condition ($runtimeSeparator -gt 0) -Message "Packaged runtime-pack identity is invalid."
    $runtimeVersion = $runtimeIdentity.Substring($runtimeSeparator + 1)
    Assert-TextEqual -Actual $runtimeVersion -Expected ([string]$ReleaseManifest.build.dotnetRuntimePackVersion) `
        -Field "Packaged runtime-pack version"
    $runtimePackageRoot = Join-Path `
        (Join-Path ([string]$LockedTools.GlobalPackagesRoot) $ExpectedRuntimePackage.ToLowerInvariant()) `
        $runtimeVersion.ToLowerInvariant()
    $runtimeHashFiles = @(Get-ChildItem -LiteralPath $runtimePackageRoot -Filter "*.nupkg.sha512" -File -ErrorAction SilentlyContinue)
    Assert-Equal -Actual $runtimeHashFiles.Count -Expected 1 -Field "Runtime-pack SHA-512 sidecar count"
    $runtimePackageFiles = @(Get-ChildItem -LiteralPath $runtimePackageRoot -Filter "*.nupkg" -File -ErrorAction SilentlyContinue)
    Assert-Equal -Actual $runtimePackageFiles.Count -Expected 1 -Field "Runtime-pack NuGet archive count"
    $rawPackageHash = (Get-Content -LiteralPath $runtimeHashFiles[0].FullName -Raw).Trim()
    $computedRawPackageHash = [Convert]::ToBase64String(
        [Convert]::FromHexString((Get-FileHash -LiteralPath $runtimePackageFiles[0].FullName -Algorithm SHA512).Hash))
    Assert-TextEqual -Actual $rawPackageHash -Expected $computedRawPackageHash `
        -Field "Runtime-pack archive SHA-512 sidecar"
    Assert-Condition -Condition $expectedDependencies.ContainsKey($ExpectedRuntimePackage) `
        -Message "Desktop package lock does not pin the self-contained runtime pack."
    $lockedRuntime = $expectedDependencies[$ExpectedRuntimePackage]
    $runtimeMetadataPath = Join-Path $runtimePackageRoot ".nupkg.metadata"
    Assert-Condition -Condition (Test-Path -LiteralPath $runtimeMetadataPath -PathType Leaf) `
        -Message "Runtime-pack NuGet metadata is missing."
    $runtimeMetadata = Read-JsonFile -Path $runtimeMetadataPath -Description "Runtime-pack NuGet metadata"
    Assert-TextEqual -Actual ([string]$lockedRuntime.Version) -Expected $runtimeVersion `
        -Field "Runtime-pack lock/packaged version"
    Assert-TextEqual -Actual ([string]$runtimeMetadata.contentHash) `
        -Expected ([string]$lockedRuntime.ContentHash) `
        -Field "Runtime-pack NuGet metadata/lock content hash"

    Assert-Equal -Actual $inventoryById.Count -Expected $expectedDependencies.Count `
        -Field "Inventory/locked dependency count"
    foreach ($expected in $expectedDependencies.Values)
    {
        Assert-Condition -Condition $inventoryById.ContainsKey($expected.Id) `
            -Message "Dependency inventory is missing locked dependency '$($expected.Id)'."
        $actual = $inventoryById[$expected.Id]
        Assert-TextEqual -Actual ([string]$actual.id) -Expected ([string]$expected.Id) `
            -Field "Dependency id casing for '$($expected.Id)'"
        Assert-TextEqual -Actual ([string]$actual.version) -Expected ([string]$expected.Version) `
            -Field "Dependency '$($expected.Id)' version"
        Assert-TextEqual -Actual ([string]$actual.contentHashSha512) -Expected ([string]$expected.ContentHash) `
            -Field "Dependency '$($expected.Id)' locked content hash"
    }
    foreach ($actualId in $inventoryById.Keys)
    {
        Assert-Condition -Condition $expectedDependencies.ContainsKey($actualId) `
            -Message "Dependency inventory contains unlocked dependency '$actualId'."
    }

    foreach ($packagedLibrary in @($packagedDeps.libraries.PSObject.Properties | Where-Object {
        [string]::Equals([string]$_.Value.type, "package", [StringComparison]::OrdinalIgnoreCase)
    }))
    {
        $separator = $packagedLibrary.Name.LastIndexOf('/')
        Assert-Condition -Condition ($separator -gt 0) `
            -Message "Packaged dependency identity '$($packagedLibrary.Name)' is invalid."
        $packageId = $packagedLibrary.Name.Substring(0, $separator)
        $packageVersion = $packagedLibrary.Name.Substring($separator + 1)
        Assert-Condition -Condition $inventoryById.ContainsKey($packageId) `
            -Message "Packaged dependency '$packageId' is absent from inventory."
        $inventoryDependency = $inventoryById[$packageId]
        Assert-TextEqual -Actual $packageVersion -Expected ([string]$inventoryDependency.version) `
            -Field "Packaged dependency '$packageId' version"
        $depsHash = [string]$packagedLibrary.Value.sha512
        if (-not [string]::IsNullOrWhiteSpace($depsHash))
        {
            Assert-Condition -Condition $depsHash.StartsWith("sha512-", [StringComparison]::Ordinal) `
                -Message "Packaged dependency '$packageId' has an invalid deps SHA-512 prefix."
            Assert-TextEqual -Actual $depsHash.Substring(7) `
                -Expected ([string]$inventoryDependency.contentHashSha512) `
                -Field "Packaged dependency '$packageId' deps/inventory content hash"
        }
    }

    $requiredPackages = @(
        [pscustomobject]@{ Id = $ExpectedBuildToolsPackage; Acquisition = "buildTool" },
        [pscustomobject]@{ Id = $ExpectedSdkReferencePackage; Acquisition = "packageDownload" },
        [pscustomobject]@{ Id = $ExpectedRuntimePackage; Acquisition = "runtimePack" })
    foreach ($required in $requiredPackages)
    {
        Assert-Condition -Condition $inventoryById.ContainsKey($required.Id) `
            -Message "Dependency inventory is missing '$($required.Id)'."
        $actual = $inventoryById[$required.Id]
        Assert-TextEqual -Actual ([string]$actual.acquisition) -Expected ([string]$required.Acquisition) `
            -Field "Dependency '$($required.Id)' acquisition"
        Assert-JsonBoolean -Value $actual.direct -Expected $true -Field "Dependency '$($required.Id)' direct flag"
    }
    Assert-TextEqual -Actual ([string]$inventoryById[$ExpectedSdkReferencePackage].version) `
        -Expected $ExpectedWindowsSdkNetRefVersion -Field "Windows SDK .NET reference version"

    $inventoryHash = Get-LowerSha256 -Path $InventoryPath
    Assert-TextEqual -Actual ([string]$ReleaseManifest.supplyChain.inventorySha256) -Expected $inventoryHash `
        -Field "Release manifest inventorySha256" -IgnoreCase
    Assert-JsonInteger -Value $ReleaseManifest.supplyChain.dependencyCount `
        -Field "Release manifest dependencyCount" -Minimum 0
    Assert-Equal -Actual ([int64]$ReleaseManifest.supplyChain.dependencyCount) `
        -Expected ([int64]$dependencies.Count) `
        -Field "Release manifest dependencyCount"
    Assert-Equal -Actual ([int64]$ReleaseManifest.supplyChain.redistributedDependencyCount) `
        -Expected ([int64]$redistributedCount) -Field "Release manifest redistributedDependencyCount"
    Assert-TextEqual -Actual ([string]$ReleaseManifest.supplyChain.thirdPartyNoticeFile) `
        -Expected ([string]$Inventory.notice.file) -Field "Release manifest notice file"
    Assert-TextEqual -Actual ([string]$ReleaseManifest.supplyChain.thirdPartyNoticeSha256) `
        -Expected ([string]$Inventory.notice.sha256) -Field "Release manifest notice SHA-256" -IgnoreCase
    Assert-Equal -Actual ([int64]$ReleaseManifest.supplyChain.thirdPartyNoticeSizeBytes) `
        -Expected ([int64]$Inventory.notice.sizeBytes) -Field "Release manifest notice size"
    Assert-TextEqual -Actual ([string]$ReleaseManifest.supplyChain.thirdPartyNoticePolicyFile) `
        -Expected ([string]$Inventory.notice.policyFile) -Field "Release manifest notice policy file"
    Assert-TextEqual -Actual ([string]$ReleaseManifest.supplyChain.thirdPartyNoticePolicySha256) `
        -Expected ([string]$Inventory.notice.policySha256) -Field "Release manifest notice policy SHA-256" -IgnoreCase

    $dependencies.Count
}

function Assert-DevelopmentCertificate
{
    param(
        [Parameter(Mandatory)][string]$ReleaseRoot,
        [Parameter(Mandatory)][object]$ReleaseManifest,
        [Parameter(Mandatory)][string]$Channel
    )

    $declaredFile = Get-OptionalProperty -Object $ReleaseManifest.supplyChain -Name "developmentCertificateFile"
    $declaredHash = Get-OptionalProperty -Object $ReleaseManifest.supplyChain -Name "developmentCertificateSha256"
    $certificateFiles = @(Get-ChildItem -LiteralPath $ReleaseRoot -Filter "*.cer" -File)

    if ($Channel -eq "public")
    {
        Assert-Condition -Condition ([string]::IsNullOrWhiteSpace([string]$declaredFile)) `
            -Message "Public release manifest must not declare a development certificate."
        Assert-Condition -Condition ([string]::IsNullOrWhiteSpace([string]$declaredHash)) `
            -Message "Public release manifest must not declare a development certificate hash."
        Assert-Equal -Actual $certificateFiles.Count -Expected 0 -Field "Public release .cer file count"
        return $false
    }

    $hasFileClaim = -not [string]::IsNullOrWhiteSpace([string]$declaredFile)
    $hasHashClaim = -not [string]::IsNullOrWhiteSpace([string]$declaredHash)
    Assert-Equal -Actual $hasFileClaim -Expected $hasHashClaim -Field "Development certificate file/hash declaration parity"
    Assert-Condition -Condition $hasFileClaim `
        -Message "Development release must declare its public test certificate and SHA-256."

    Assert-TextEqual -Actual ([string]$declaredFile) -Expected $DevelopmentCertificateFileName `
        -Field "Development certificate file"
    Assert-Equal -Actual $certificateFiles.Count -Expected 1 -Field "Development release .cer file count"
    $certificatePath = Join-Path $ReleaseRoot $DevelopmentCertificateFileName
    Assert-Condition -Condition (Test-Path -LiteralPath $certificatePath -PathType Leaf) `
        -Message "Declared development certificate is missing."
    $certificateHash = Get-LowerSha256 -Path $certificatePath
    Assert-TextEqual -Actual ([string]$declaredHash) -Expected $certificateHash `
        -Field "Development certificate SHA-256" -IgnoreCase

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try
    {
        Assert-Equal -Actual $certificate.HasPrivateKey -Expected $false -Field "Development public certificate private-key flag"
        Assert-TextEqual -Actual $certificate.Subject -Expected ([string]$ReleaseManifest.signing.certificateSubject) `
            -Field "Development certificate subject"
        Assert-TextEqual -Actual $certificate.Thumbprint -Expected ([string]$ReleaseManifest.signing.certificateThumbprint) `
            -Field "Development certificate thumbprint" -IgnoreCase
        $declaredExpiry = ConvertTo-DateTimeOffsetValue `
            -Value $ReleaseManifest.signing.certificateNotAfterUtc `
            -Field "Development certificate expiry"
        Assert-Equal -Actual $certificate.NotAfter.ToUniversalTime().Ticks `
            -Expected $declaredExpiry.UtcDateTime.Ticks `
            -Field "Development certificate expiry ticks"
        $enhancedKeyUsage = $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
            Select-Object -First 1
        $hasCodeSigningUsage = $null -ne $enhancedKeyUsage -and
            $null -ne ($enhancedKeyUsage.EnhancedKeyUsages |
                Where-Object Value -EQ "1.3.6.1.5.5.7.3.3" |
                Select-Object -First 1)
        Assert-Condition -Condition $hasCodeSigningUsage `
            -Message "Development certificate does not include the Code Signing enhanced key usage."
    }
    finally
    {
        $certificate.Dispose()
    }

    $true
}

function Resolve-PackagePayloadPath
{
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$RelativePath
    )

    Assert-Condition -Condition (-not [string]::IsNullOrWhiteSpace($RelativePath)) `
        -Message "Package payload path is empty."
    Assert-Condition -Condition (-not [IO.Path]::IsPathRooted($RelativePath)) `
        -Message "Package payload path '$RelativePath' is rooted."
    $segments = @($RelativePath -split '[\\/]')
    Assert-Condition -Condition ($segments.Count -gt 0 -and $segments -notcontains ".." -and $segments -notcontains ".") `
        -Message "Package payload path '$RelativePath' is unsafe."
    $platformPath = $RelativePath.Replace('\', [IO.Path]::DirectorySeparatorChar).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath((Join-Path $PackageRoot $platformPath))
    Assert-ChildPath -Path $resolved -ParentPath $PackageRoot
    $resolved
}

function Get-PngDimensions
{
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    Assert-Condition -Condition ($bytes.Length -ge 24) -Message "PNG '$Path' is truncated."
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Count; $index++)
    {
        Assert-Equal -Actual ([int]$bytes[$index]) -Expected $signature[$index] -Field "PNG '$Path' signature byte $index"
    }
    Assert-TextEqual -Actual ([Text.Encoding]::ASCII.GetString($bytes, 12, 4)) -Expected "IHDR" `
        -Field "PNG '$Path' first chunk"
    $width = ([uint64]$bytes[16] * 16777216) + ([uint64]$bytes[17] * 65536) +
        ([uint64]$bytes[18] * 256) + [uint64]$bytes[19]
    $height = ([uint64]$bytes[20] * 16777216) + ([uint64]$bytes[21] * 65536) +
        ([uint64]$bytes[22] * 256) + [uint64]$bytes[23]
    @([int]$width, [int]$height)
}

function Assert-AppxBlockMap
{
    param([Parameter(Mandatory)][string]$PackageRoot)

    $blockMapPath = Join-Path $PackageRoot "AppxBlockMap.xml"
    Assert-Condition -Condition (Test-Path -LiteralPath $blockMapPath -PathType Leaf) `
        -Message "MSIX has no AppxBlockMap.xml."
    [xml]$blockMap = Get-Content -LiteralPath $blockMapPath -Raw -Encoding utf8
    $namespaces = [Xml.XmlNamespaceManager]::new($blockMap.NameTable)
    $namespaces.AddNamespace("b", "http://schemas.microsoft.com/appx/2010/blockmap")
    $namespaces.AddNamespace("b4", "http://schemas.microsoft.com/appx/2021/blockmap")
    Assert-TextEqual -Actual $blockMap.DocumentElement.GetAttribute("HashMethod") -Expected $Sha256HashMethod `
        -Field "AppxBlockMap HashMethod"

    $fileNodes = @($blockMap.SelectNodes("/b:BlockMap/b:File", $namespaces))
    Assert-Condition -Condition ($fileNodes.Count -gt 0) -Message "AppxBlockMap contains no files."
    $coveredFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $totalBlocks = 0
    foreach ($fileNode in $fileNodes)
    {
        $relativePath = [string]$fileNode.GetAttribute("Name")
        Assert-Condition -Condition $coveredFiles.Add($relativePath) `
            -Message "AppxBlockMap lists '$relativePath' more than once."
        $payloadPath = Resolve-PackagePayloadPath -PackageRoot $PackageRoot -RelativePath $relativePath
        Assert-Condition -Condition (Test-Path -LiteralPath $payloadPath -PathType Leaf) `
            -Message "AppxBlockMap references missing payload '$relativePath'."
        $file = Get-Item -LiteralPath $payloadPath
        Assert-Equal -Actual ([int64]$fileNode.GetAttribute("Size")) -Expected ([int64]$file.Length) `
            -Field "AppxBlockMap size for '$relativePath'"

        $blockNodes = @($fileNode.SelectNodes("b:Block", $namespaces))
        $expectedBlockCount = [int][Math]::Ceiling($file.Length / [double]$BlockSizeBytes)
        Assert-Equal -Actual $blockNodes.Count -Expected $expectedBlockCount `
            -Field "AppxBlockMap block count for '$relativePath'"
        $stream = [IO.File]::OpenRead($payloadPath)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try
        {
            foreach ($blockNode in $blockNodes)
            {
                $remaining = [Math]::Min($BlockSizeBytes, $stream.Length - $stream.Position)
                $chunk = [byte[]]::new([int]$remaining)
                $offset = 0
                while ($offset -lt $chunk.Length)
                {
                    $read = $stream.Read($chunk, $offset, $chunk.Length - $offset)
                    Assert-Condition -Condition ($read -gt 0) `
                        -Message "Unexpected end of payload '$relativePath' while verifying AppxBlockMap."
                    $offset += $read
                }
                $actualBlockHash = [Convert]::ToBase64String($sha256.ComputeHash($chunk))
                Assert-TextEqual -Actual ([string]$blockNode.GetAttribute("Hash")) -Expected $actualBlockHash `
                    -Field "AppxBlockMap SHA-256 block for '$relativePath'"
                $totalBlocks++
            }
            Assert-Equal -Actual $stream.Position -Expected $stream.Length `
                -Field "AppxBlockMap consumed length for '$relativePath'"
        }
        finally
        {
            $sha256.Dispose()
            $stream.Dispose()
        }

        $fileHashNode = $fileNode.SelectSingleNode("b4:FileHash", $namespaces)
        if ($null -ne $fileHashNode)
        {
            $actualFileHash = [Convert]::ToBase64String(
                [Convert]::FromHexString((Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash))
            Assert-TextEqual -Actual ([string]$fileHashNode.GetAttribute("Hash")) -Expected $actualFileHash `
                -Field "AppxBlockMap full SHA-256 for '$relativePath'"
        }
    }

    $reservedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($reserved in @(
        "AppxBlockMap.xml",
        "AppxSignature.p7x",
        "[Content_Types].xml",
        "AppxMetadata\CodeIntegrity.cat"))
    {
        [void]$reservedFiles.Add($reserved)
    }
    foreach ($payloadFile in @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse))
    {
        $relative = [IO.Path]::GetRelativePath($PackageRoot, $payloadFile.FullName).Replace('/', '\')
        if (-not $reservedFiles.Contains($relative))
        {
            Assert-Condition -Condition $coveredFiles.Contains($relative) `
                -Message "MSIX payload '$relative' is absent from AppxBlockMap."
        }
    }

    [pscustomobject]@{
        FileCount = $fileNodes.Count
        BlockCount = $totalBlocks
    }
}

function Assert-RenderedManifest
{
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][object]$ReleaseManifest
    )

    $manifestPath = Join-Path $PackageRoot "AppxManifest.xml"
    Assert-Condition -Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        -Message "MSIX has no AppxManifest.xml."
    [xml]$appxManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8
    $namespaces = [Xml.XmlNamespaceManager]::new($appxManifest.NameTable)
    $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $namespaces.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
    $namespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
    $namespaces.AddNamespace("desktop6", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6")
    $namespaces.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")

    $identityNodes = @($appxManifest.SelectNodes("/f:Package/f:Identity", $namespaces))
    Assert-Equal -Actual $identityNodes.Count -Expected 1 -Field "MSIX Identity count"
    $identity = $identityNodes[0]
    Assert-TextEqual -Actual $identity.GetAttribute("Name") -Expected ([string]$ReleaseManifest.package.identityName) `
        -Field "MSIX identity name"
    Assert-TextEqual -Actual $identity.GetAttribute("Publisher") -Expected ([string]$ReleaseManifest.package.publisher) `
        -Field "MSIX publisher"
    Assert-TextEqual -Actual $identity.GetAttribute("Version") -Expected ([string]$ReleaseManifest.msixVersion) `
        -Field "MSIX version"
    Assert-TextEqual -Actual $identity.GetAttribute("ProcessorArchitecture") -Expected $ExpectedArchitecture `
        -Field "MSIX processor architecture" -IgnoreCase

    $targetFamilies = @($appxManifest.SelectNodes("/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name='Windows.Desktop']", $namespaces))
    Assert-Equal -Actual $targetFamilies.Count -Expected 1 -Field "Windows.Desktop target family count"
    Assert-TextEqual -Actual $targetFamilies[0].GetAttribute("MinVersion") -Expected $ExpectedMinimumWindowsVersion `
        -Field "Windows.Desktop minimum version"

    $applications = @($appxManifest.SelectNodes("/f:Package/f:Applications/f:Application", $namespaces))
    Assert-Equal -Actual $applications.Count -Expected 1 -Field "MSIX application count"
    $application = $applications[0]
    Assert-TextEqual -Actual $application.GetAttribute("Id") -Expected ([string]$ReleaseManifest.package.applicationId) `
        -Field "MSIX application id"
    Assert-TextEqual -Actual $application.GetAttribute("Executable") -Expected $ExpectedApplicationExecutable `
        -Field "MSIX application executable"
    Assert-TextEqual -Actual $application.GetAttribute("EntryPoint") -Expected "Windows.FullTrustApplication" `
        -Field "MSIX application entry point"
    Assert-TextEqual -Actual $application.GetAttribute("RuntimeBehavior", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected "packagedClassicApp" -Field "MSIX runtime behavior"
    Assert-TextEqual -Actual $application.GetAttribute("TrustLevel", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected "mediumIL" -Field "MSIX trust level"

    $fullTrust = @($appxManifest.SelectNodes("/f:Package/f:Capabilities/rescap:Capability[@Name='runFullTrust']", $namespaces))
    Assert-Equal -Actual $fullTrust.Count -Expected 1 -Field "runFullTrust capability count"
    $unvirtualized = @($appxManifest.SelectNodes("/f:Package/f:Capabilities/rescap:Capability[@Name='unvirtualizedResources']", $namespaces))
    Assert-Equal -Actual $unvirtualized.Count -Expected 1 -Field "unvirtualizedResources capability count"
    $microphone = @($appxManifest.SelectNodes("/f:Package/f:Capabilities/f:DeviceCapability[@Name='microphone']", $namespaces))
    Assert-Equal -Actual $microphone.Count -Expected 1 -Field "microphone capability count"

    $fileVirtualization = @($appxManifest.SelectNodes("/f:Package/f:Properties/desktop6:FileSystemWriteVirtualization", $namespaces))
    Assert-Equal -Actual $fileVirtualization.Count -Expected 1 -Field "FileSystemWriteVirtualization count"
    Assert-TextEqual -Actual $fileVirtualization[0].InnerText.Trim() -Expected "disabled" `
        -Field "FileSystemWriteVirtualization"
    $registryVirtualization = @($appxManifest.SelectNodes("/f:Package/f:Properties/desktop6:RegistryWriteVirtualization", $namespaces))
    Assert-Equal -Actual $registryVirtualization.Count -Expected 1 -Field "RegistryWriteVirtualization count"
    Assert-TextEqual -Actual $registryVirtualization[0].InnerText.Trim() -Expected "disabled" `
        -Field "RegistryWriteVirtualization"

    $startupExtensions = @($application.SelectNodes("f:Extensions/desktop:Extension[@Category='windows.startupTask']", $namespaces))
    Assert-Equal -Actual $startupExtensions.Count -Expected 1 -Field "windows.startupTask extension count"
    $startupExtension = $startupExtensions[0]
    Assert-TextEqual -Actual $startupExtension.GetAttribute("Executable") -Expected $ExpectedApplicationExecutable `
        -Field "StartupTask executable"
    Assert-TextEqual -Actual $startupExtension.GetAttribute("EntryPoint") -Expected "Windows.FullTrustApplication" `
        -Field "StartupTask entry point"
    Assert-TextEqual -Actual $startupExtension.GetAttribute("Parameters", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected $ExpectedStartupParameters -Field "StartupTask parameters"
    $startupTasks = @($startupExtension.SelectNodes("desktop:StartupTask", $namespaces))
    Assert-Equal -Actual $startupTasks.Count -Expected 1 -Field "StartupTask declaration count"
    Assert-TextEqual -Actual $startupTasks[0].GetAttribute("TaskId") -Expected $ExpectedStartupTaskId `
        -Field "StartupTask id"
    Assert-TextEqual -Actual $startupTasks[0].GetAttribute("Enabled") -Expected "false" `
        -Field "StartupTask default state" -IgnoreCase

    $propertyLogo = $appxManifest.SelectSingleNode("/f:Package/f:Properties/f:Logo", $namespaces)
    Assert-Condition -Condition ($null -ne $propertyLogo) -Message "MSIX Properties Logo is missing."
    Assert-TextEqual -Actual $propertyLogo.InnerText.Trim() -Expected "Assets\StoreLogo.png" `
        -Field "MSIX Store logo"
    $publisherDisplayName = $appxManifest.SelectSingleNode("/f:Package/f:Properties/f:PublisherDisplayName", $namespaces)
    Assert-Condition -Condition ($null -ne $publisherDisplayName) -Message "MSIX PublisherDisplayName is missing."
    Assert-TextEqual -Actual $publisherDisplayName.InnerText.Trim() `
        -Expected ([string]$ReleaseManifest.package.publisherDisplayName) `
        -Field "MSIX publisher display name"
    $visualElements = $application.SelectSingleNode("uap:VisualElements", $namespaces)
    Assert-Condition -Condition ($null -ne $visualElements) -Message "MSIX VisualElements is missing."
    Assert-TextEqual -Actual $visualElements.GetAttribute("Square44x44Logo") -Expected "Assets\Square44x44Logo.png" `
        -Field "MSIX Square44 logo"
    Assert-TextEqual -Actual $visualElements.GetAttribute("Square150x150Logo") -Expected "Assets\Square150x150Logo.png" `
        -Field "MSIX Square150 logo"
    $defaultTile = $visualElements.SelectSingleNode("uap:DefaultTile", $namespaces)
    Assert-Condition -Condition ($null -ne $defaultTile) -Message "MSIX DefaultTile is missing."
    Assert-TextEqual -Actual $defaultTile.GetAttribute("Wide310x150Logo") -Expected "Assets\Wide310x150Logo.png" `
        -Field "MSIX Wide310 logo"
    Assert-TextEqual -Actual $defaultTile.GetAttribute("Square310x310Logo") -Expected "Assets\Square310x310Logo.png" `
        -Field "MSIX Square310 logo"

    foreach ($asset in $ExpectedAssets.GetEnumerator())
    {
        $assetPath = Resolve-PackagePayloadPath -PackageRoot $PackageRoot -RelativePath $asset.Key
        Assert-Condition -Condition (Test-Path -LiteralPath $assetPath -PathType Leaf) `
            -Message "Required MSIX asset '$($asset.Key)' is missing."
        $dimensions = @(Get-PngDimensions -Path $assetPath)
        Assert-Equal -Actual $dimensions[0] -Expected $asset.Value[0] -Field "Asset '$($asset.Key)' width"
        Assert-Equal -Actual $dimensions[1] -Expected $asset.Value[1] -Field "Asset '$($asset.Key)' height"
    }

    [pscustomobject]@{
        Identity = $identity.GetAttribute("Name")
        Version = $identity.GetAttribute("Version")
        MinimumWindowsVersion = $targetFamilies[0].GetAttribute("MinVersion")
        AssetCount = $ExpectedAssets.Count
    }
}

$installerRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $installerRoot "..\.."))
$identityPolicy = Read-ReleaseIdentityPolicy -InstallerRoot $installerRoot
$resolvedRelease = Resolve-Path -LiteralPath $ReleaseDirectory -ErrorAction Stop
Assert-Condition -Condition ((Get-Item -LiteralPath $resolvedRelease.Path).PSIsContainer) `
    -Message "ReleaseDirectory must be a directory."
$releaseRoot = [IO.Path]::GetFullPath($resolvedRelease.Path)
$channel = (Split-Path -Leaf (Split-Path -Parent $releaseRoot)).ToLowerInvariant()
Assert-Condition -Condition ($channel -in @("development", "public")) `
    -Message "Release directory must be directly under a development or public channel directory."

$topLevelDirectories = @(Get-ChildItem -LiteralPath $releaseRoot -Force -Directory)
Assert-Equal -Actual $topLevelDirectories.Count -Expected 0 -Field "Release top-level directory count"
$topLevelFiles = @(Get-ChildItem -LiteralPath $releaseRoot -Force -File)
Assert-Condition -Condition ($topLevelFiles.Count -gt 0) -Message "Release directory is empty."
$msixFiles = @($topLevelFiles | Where-Object { $_.Extension -eq ".msix" })
Assert-Equal -Actual $msixFiles.Count -Expected 1 -Field "Release MSIX count"
$msix = $msixFiles[0]

foreach ($requiredFile in @($ReleaseManifestFileName, $DependencyInventoryFileName, $ChecksumFileName))
{
    Assert-Condition -Condition (Test-Path -LiteralPath (Join-Path $releaseRoot $requiredFile) -PathType Leaf) `
        -Message "Required release file '$requiredFile' is missing."
}
$legacyTopLevelFiles = @($topLevelFiles | Where-Object { $_.Extension -in @(".ps1", ".cmd", ".bat", ".pdb") })
Assert-Equal -Actual $legacyTopLevelFiles.Count -Expected 0 -Field "Release legacy/debug top-level file count"
$symbolsArchives = @($topLevelFiles | Where-Object { $_.Name -like "*-symbols.zip" })
Assert-Equal -Actual $symbolsArchives.Count -Expected 0 -Field "Release symbols archive count"

$checksums = Read-And-VerifyChecksums -ReleaseRoot $releaseRoot -TopLevelFiles $topLevelFiles
$releaseManifestPath = Join-Path $releaseRoot $ReleaseManifestFileName
$inventoryPath = Join-Path $releaseRoot $DependencyInventoryFileName
$releaseManifest = Read-JsonFile -Path $releaseManifestPath -Description "Release manifest"
$inventory = Read-JsonFile -Path $inventoryPath -Description "Dependency/license inventory"

Assert-TextEqual -Actual ([string]$releaseManifest.schemaVersion) -Expected $ExpectedReleaseManifestSchema `
    -Field "Release manifest schemaVersion"
Assert-TextEqual -Actual ([string]$releaseManifest.product) -Expected $ExpectedProduct -Field "Release manifest product"
Assert-TextEqual -Actual ([string]$releaseManifest.runtimeIdentifier) -Expected $ExpectedRuntimeIdentifier `
    -Field "Release runtimeIdentifier"
Assert-TextEqual -Actual ([string]$releaseManifest.architecture) -Expected $ExpectedArchitecture `
    -Field "Release architecture" -IgnoreCase
Assert-TextEqual -Actual ([string]$releaseManifest.configuration) -Expected "Release" -Field "Release configuration"
Assert-JsonBoolean -Value $releaseManifest.build.selfContained -Expected $true -Field "Release selfContained flag"
Assert-JsonBoolean -Value $releaseManifest.build.trimmed -Expected $false -Field "Release trimmed flag"
Assert-NonEmptyText -Value $releaseManifest.build.sourceRevision -Field "Release sourceRevision"
Assert-NonEmptyText -Value $releaseManifest.build.dotnetSdkVersion -Field "Release dotnetSdkVersion"
Assert-NonEmptyText -Value $releaseManifest.build.dotnetRuntimePackVersion -Field "Release dotnetRuntimePackVersion"
Assert-NonEmptyText -Value $releaseManifest.build.windowsSdkBuildToolsVersion -Field "Release windowsSdkBuildToolsVersion"
Assert-RoundTripTimestamp -Value ([string]$releaseManifest.build.createdUtc) -Field "Release createdUtc"
$expectedAudioCodecProperties = @(
    "primaryExtension", "container", "codec", "implementation", "managedWrapper", "sampleRateHz",
    "channels", "bitsPerSample", "bitRate", "bundledCodecBinaryCount", "patentProgramStatus", "evidenceUrls")
$actualAudioCodecProperties = @($releaseManifest.audioCodec.PSObject.Properties.Name)
Assert-Equal -Actual $actualAudioCodecProperties.Count -Expected $expectedAudioCodecProperties.Count `
    -Field "Audio codec property count"
foreach ($property in $expectedAudioCodecProperties)
{
    Assert-Condition -Condition ($actualAudioCodecProperties -ccontains $property) `
        -Message "Release audio codec is missing exact property '$property'."
}
Assert-Condition -Condition ($releaseManifest.PSObject.Properties.Name -notcontains "compliance") `
    -Message "Release manifest must not retain the obsolete AAC compliance review field."
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.primaryExtension) -Expected ".mp3" `
    -Field "Audio primary extension"
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.container) -Expected "MP3" `
    -Field "Audio container"
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.codec) -Expected "MPEG-1 Layer III" `
    -Field "Audio codec"
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.implementation) -Expected "Windows Media Foundation" `
    -Field "Audio implementation"
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.managedWrapper) -Expected "NAudio" `
    -Field "Audio managed wrapper"
foreach ($codecInteger in @(
    @{ Field = "sampleRateHz"; Expected = 48000 },
    @{ Field = "channels"; Expected = 2 },
    @{ Field = "bitsPerSample"; Expected = 16 },
    @{ Field = "bitRate"; Expected = 128000 },
    @{ Field = "bundledCodecBinaryCount"; Expected = 0 }))
{
    Assert-JsonInteger -Value $releaseManifest.audioCodec.($codecInteger.Field) `
        -Field "Audio codec $($codecInteger.Field)" -Minimum 0
    Assert-Equal -Actual ([int]$releaseManifest.audioCodec.($codecInteger.Field)) `
        -Expected $codecInteger.Expected -Field "Audio codec $($codecInteger.Field)"
}
Assert-TextEqual -Actual ([string]$releaseManifest.audioCodec.patentProgramStatus) `
    -Expected "Fraunhofer MP3 licensing program terminated 2017-04-23" -Field "Audio patent-program status"
$expectedCodecEvidenceUrls = @(
    "https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder",
    "https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456",
    "https://www.iis.fraunhofer.de/en/ff/amm/consumer-electronics/mp3.html",
    "https://www.audioblog.iis.fraunhofer.com/mp3-software-patents-licenses")
$actualCodecEvidenceUrls = @($releaseManifest.audioCodec.evidenceUrls)
Assert-Equal -Actual $actualCodecEvidenceUrls.Count -Expected $expectedCodecEvidenceUrls.Count `
    -Field "Audio codec evidence URL count"
for ($index = 0; $index -lt $expectedCodecEvidenceUrls.Count; $index++)
{
    Assert-TextEqual -Actual ([string]$actualCodecEvidenceUrls[$index]) -Expected $expectedCodecEvidenceUrls[$index] `
        -Field "Audio codec evidence URL $($index + 1)"
}
Assert-TextEqual -Actual ([string]$releaseManifest.supplyChain.identityPolicySchemaVersion) `
    -Expected $identityPolicy.SchemaVersion -Field "Release identity policy schemaVersion evidence"
Assert-TextEqual -Actual ([string]$releaseManifest.supplyChain.identityPolicySha256) `
    -Expected $identityPolicy.Sha256 -Field "Release identity policy SHA-256 evidence" -IgnoreCase

$semanticVersion = [string]$releaseManifest.semanticVersion
$expectedMsixVersion = ConvertTo-ExpectedMsixVersion -SemanticVersion $semanticVersion
Assert-TextEqual -Actual ([string]$releaseManifest.msixVersion) -Expected $expectedMsixVersion `
    -Field "Release msixVersion"
$safeVersion = $semanticVersion -replace '[^0-9A-Za-z.-]', '_'
Assert-TextEqual -Actual (Split-Path -Leaf $releaseRoot) -Expected $safeVersion -Field "Release directory version"

$signingMode = ([string]$releaseManifest.signing.mode).ToLowerInvariant()
$expectedChannel = if ($signingMode -eq "development")
{
    "development"
}
elseif ($signingMode -eq "production")
{
    "public"
}
else
{
    throw "Release signing mode '$signingMode' is unsupported."
}
Assert-TextEqual -Actual $channel -Expected $expectedChannel -Field "Release distribution channel"
$packageQualifier = if ($channel -eq "development") { "-dev" } else { [string]::Empty }
$expectedMsixFileName = "isTranscribe-$safeVersion$packageQualifier-win-x64.msix"
Assert-TextEqual -Actual $msix.Name -Expected $expectedMsixFileName -Field "MSIX file name"
Assert-ExactReleaseFileAllowlist -Files $topLevelFiles -Channel $channel -MsixFileName $expectedMsixFileName
Assert-TextEqual -Actual ([string]$releaseManifest.package.file) -Expected $msix.Name -Field "Manifest package file"
Assert-JsonInteger -Value $releaseManifest.package.sizeBytes -Field "Manifest package sizeBytes" -Minimum 1
Assert-Equal -Actual ([int64]$releaseManifest.package.sizeBytes) -Expected ([int64]$msix.Length) `
    -Field "Manifest package sizeBytes"
$msixHash = Get-LowerSha256 -Path $msix.FullName
Assert-TextEqual -Actual ([string]$releaseManifest.package.sha256) -Expected $msixHash `
    -Field "Manifest package SHA-256" -IgnoreCase
Assert-TextEqual -Actual ([string]$releaseManifest.package.applicationId) -Expected $ExpectedApplicationId `
    -Field "Manifest applicationId"
Assert-TextEqual -Actual ([string]$releaseManifest.package.identityName) -Expected $identityPolicy.PackageName `
    -Field "Manifest stable package identity"
Assert-NonEmptyText -Value $releaseManifest.package.publisher -Field "Manifest publisher"
Assert-TextEqual -Actual ([string]$releaseManifest.package.publisherDisplayName) `
    -Expected $identityPolicy.PublisherDisplayName -Field "Manifest publisherDisplayName"
Assert-TextEqual -Actual ([string]$releaseManifest.signing.certificateSubject) `
    -Expected ([string]$releaseManifest.package.publisher) -Field "Signing subject/package publisher"
Assert-Condition -Condition (([string]$releaseManifest.signing.certificateThumbprint) -match '^[0-9A-Fa-f]{40}$') `
    -Message "Signing certificate thumbprint is invalid."
Assert-RoundTripTimestamp -Value ([string]$releaseManifest.signing.certificateNotAfterUtc) `
    -Field "Signing certificateNotAfterUtc"
Assert-JsonBooleanType -Value $releaseManifest.signing.publicEligible -Field "Signing publicEligible flag"
Assert-JsonBooleanType -Value $releaseManifest.signing.timestampRequested -Field "Signing timestampRequested flag"
Assert-JsonBooleanType -Value $releaseManifest.signing.timestampVerified -Field "Signing timestampVerified flag"
Assert-JsonBoolean -Value $releaseManifest.signing.verified -Expected $true -Field "Signing verified flag"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.localAppDataRoot.knownFolder) `
    -Expected "LocalApplicationData" -Field "Release LocalAppData known folder"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.localAppDataRoot.relativePath) `
    -Expected "isTranscribe" -Field "Release LocalAppData relative path"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.localAppDataRoot.resolvedAtRuntimeBy) `
    -Expected "Environment.SpecialFolder.LocalApplicationData" -Field "Release LocalAppData resolver"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.documentsRoot.knownFolder) `
    -Expected "MyDocuments" -Field "Release Documents known folder"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.documentsRoot.relativePath) `
    -Expected "isTranscribe" -Field "Release Documents relative path"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.documentsRoot.resolvedAtRuntimeBy) `
    -Expected "Environment.SpecialFolder.MyDocuments" -Field "Release Documents resolver"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.recordingsRoot.source) `
    -Expected "settings.recordingsFolder" -Field "Release recordings root source"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.recordingsRoot.defaultRelativePath) `
    -Expected "Recordings" -Field "Release default recordings relative path"
Assert-JsonBoolean -Value $releaseManifest.dataPersistence.recordingsRoot.customPathPreserved `
    -Expected $true -Field "Release custom recordings preservation"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.fileSystemWriteVirtualization) `
    -Expected "disabled" -Field "Release file-system virtualization"
Assert-TextEqual -Actual ([string]$releaseManifest.dataPersistence.registryWriteVirtualization) `
    -Expected "disabled" -Field "Release registry virtualization"

if ($channel -eq "development")
{
    Assert-TextEqual -Actual ([string]$releaseManifest.package.publisher) `
        -Expected $identityPolicy.DevelopmentPublisher -Field "Development package publisher identity"
}
else
{
    Assert-Condition -Condition ($identityPolicy.ProductionPublisherSubjects.Count -gt 0) `
        -Message "Release identity policy has no approved production publisher. Complete a reviewed policy rollover first."
    Assert-Condition -Condition (
        [string]$releaseManifest.package.publisher -cin $identityPolicy.ProductionPublisherSubjects) `
        -Message "Public package publisher is absent from the checked-in release identity allowlist."
}

$markerPath = Join-Path $releaseRoot $DevelopmentMarkerFileName
if ($channel -eq "development")
{
    Assert-JsonBoolean -Value $releaseManifest.signing.publicEligible -Expected $false `
        -Field "Development publicEligible flag"
    Assert-JsonBoolean -Value $releaseManifest.signing.timestampVerified -Expected $false `
        -Field "Development timestampVerified flag"
    Assert-Condition -Condition (Test-Path -LiteralPath $markerPath -PathType Leaf) `
        -Message "Development release marker is missing."
    $markerText = Get-Content -LiteralPath $markerPath -Raw -Encoding ascii
    Assert-Condition -Condition ($markerText.StartsWith("DEVELOPMENT ONLY", [StringComparison]::Ordinal)) `
        -Message "Development release marker is invalid."
    Assert-Condition -Condition ($msix.Name -match '-dev-win-x64\.msix$') `
        -Message "Development MSIX file name has no dev marker."
}
else
{
    Assert-JsonBoolean -Value $releaseManifest.signing.publicEligible -Expected $true `
        -Field "Production publicEligible flag"
    Assert-JsonBoolean -Value $releaseManifest.signing.timestampRequested -Expected $true `
        -Field "Production timestampRequested flag"
    Assert-JsonBoolean -Value $releaseManifest.signing.timestampVerified -Expected $true `
        -Field "Production timestampVerified flag"
    Assert-TextEqual -Actual ([string]$releaseManifest.signing.trustStatus) -Expected "Valid" `
        -Field "Production trustStatus"
    Assert-Condition -Condition (-not (Test-Path -LiteralPath $markerPath)) `
        -Message "Public release contains a development marker."
    Assert-Condition -Condition ($msix.Name -notmatch '-dev-') `
        -Message "Public MSIX file name contains a development marker."
}

$developmentCertificateDeclared = Assert-DevelopmentCertificate `
    -ReleaseRoot $releaseRoot `
    -ReleaseManifest $releaseManifest `
    -Channel $channel

$lockedTools = Resolve-LockedMakeAppx -InstallerRoot $installerRoot
Assert-TextEqual -Actual ([string]$releaseManifest.build.windowsSdkBuildToolsVersion) `
    -Expected ([string]$lockedTools.Version) -Field "Release/locked BuildTools version"
$signatureResult = Assert-MsixSignatureIntegrity `
    -Path $msix.FullName `
    -Channel $channel `
    -ReleaseManifest $releaseManifest `
    -SignToolPath $lockedTools.SignToolPath

Assert-TextEqual -Actual ([string]$releaseManifest.supplyChain.inventoryFile) `
    -Expected $DependencyInventoryFileName -Field "Manifest inventoryFile"
Assert-TextEqual -Actual ([string]$releaseManifest.supplyChain.checksumFile) `
    -Expected $ChecksumFileName -Field "Manifest checksumFile"
Assert-Condition `
    -Condition ($releaseManifest.supplyChain.PSObject.Properties.Name -notcontains "symbolsFile") `
    -Message "Distributable release metadata must not expose an internal symbols archive."

$temporaryParent = Join-Path ([IO.Path]::GetTempPath()) "isTranscribe-release-verifier"
$temporaryRoot = Join-Path $temporaryParent ([Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$verificationError = $null
try
{
    $unpackOutput = @(& $lockedTools.MakeAppxPath unpack /p $msix.FullName /d $temporaryRoot /o 2>&1)
    $unpackExitCode = $LASTEXITCODE
    if ($unpackExitCode -ne 0)
    {
        throw "MakeAppx could not unpack the MSIX (exit $unpackExitCode): $($unpackOutput -join [Environment]::NewLine)"
    }

    Assert-Condition -Condition (Test-Path -LiteralPath (Join-Path $temporaryRoot "AppxSignature.p7x") -PathType Leaf) `
        -Message "MSIX has no AppxSignature.p7x."
    foreach ($runtimeFile in @(
        $ExpectedApplicationExecutable,
        "THIRD-PARTY-NOTICES.txt",
        "coreclr.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll"))
    {
        Assert-Condition -Condition (Test-Path -LiteralPath (Join-Path $temporaryRoot $runtimeFile) -PathType Leaf) `
            -Message "Self-contained runtime payload '$runtimeFile' is missing."
    }

    $thirdPartyNoticePath = Join-Path $temporaryRoot "THIRD-PARTY-NOTICES.txt"
    $thirdPartyNoticeBytes = [IO.File]::ReadAllBytes($thirdPartyNoticePath)
    Assert-Condition -Condition ($thirdPartyNoticeBytes.Length -gt 0 -and $thirdPartyNoticeBytes[-1] -eq 0x0A) `
        -Message "Third-party notice must end with LF."
    Assert-Condition -Condition (-not ($thirdPartyNoticeBytes.Length -ge 3 -and
        $thirdPartyNoticeBytes[0] -eq 0xEF -and $thirdPartyNoticeBytes[1] -eq 0xBB -and $thirdPartyNoticeBytes[2] -eq 0xBF)) `
        -Message "Third-party notice must not contain a UTF-8 BOM."
    try { $thirdPartyNotice = [Text.UTF8Encoding]::new($false, $true).GetString($thirdPartyNoticeBytes) }
    catch { throw "Third-party notice is not strict UTF-8." }
    Assert-Condition -Condition (-not $thirdPartyNotice.Contains("`r", [StringComparison]::Ordinal)) `
        -Message "Third-party notice must use deterministic LF line endings."
    Assert-Condition -Condition (
        $thirdPartyNotice.Contains("Microsoft.Windows.SDK.NET.Ref/10.0.26100.84", [StringComparison]::Ordinal) -and
        $thirdPartyNotice.Contains("SIL OPEN FONT LICENSE Version 1.1", [StringComparison]::Ordinal)) `
        -Message "Third-party notice lacks required Windows SDK or Inter/OFL evidence."
    Assert-TextEqual -Actual (Get-LowerSha256 -Path $thirdPartyNoticePath) `
        -Expected ([string]$releaseManifest.supplyChain.thirdPartyNoticeSha256) `
        -Field "Packaged third-party notice SHA-256" -IgnoreCase
    Assert-Equal -Actual ([int64](Get-Item -LiteralPath $thirdPartyNoticePath).Length) `
        -Expected ([int64]$releaseManifest.supplyChain.thirdPartyNoticeSizeBytes) `
        -Field "Packaged third-party notice size"
    Assert-PackagedDependencyPayloadTrees -Inventory $inventory -PackageRoot $temporaryRoot

    $platformAssemblyPath = Join-Path $temporaryRoot "IsTranscribe.Platform.Windows.dll"
    $platformAssemblyBytes = [IO.File]::ReadAllBytes($platformAssemblyPath)
    $platformAssemblyAscii = [Text.Encoding]::ASCII.GetString($platformAssemblyBytes)
    $platformAssemblyUnicode = [Text.Encoding]::Unicode.GetString($platformAssemblyBytes)
    $platformAssemblyUnicodeOffsetOne = if ($platformAssemblyBytes.Length -gt 1)
    {
        [Text.Encoding]::Unicode.GetString($platformAssemblyBytes, 1, $platformAssemblyBytes.Length - 1)
    }
    else { "" }
    foreach ($requiredCodecSymbol in @(
        "WindowsMediaFoundationMp3Encoder",
        "windows-media-foundation-mp3",
        "mp3_encoder_unavailable"))
    {
        Assert-Condition -Condition (
            $platformAssemblyAscii.Contains($requiredCodecSymbol, [StringComparison]::Ordinal) -or
            $platformAssemblyUnicode.Contains($requiredCodecSymbol, [StringComparison]::Ordinal) -or
            $platformAssemblyUnicodeOffsetOne.Contains($requiredCodecSymbol, [StringComparison]::Ordinal)) `
            -Message "Packaged Windows runtime is missing required MP3 composition symbol '$requiredCodecSymbol'."
    }
    foreach ($forbiddenCodecSymbol in @(
        "WindowsMediaFoundationAacEncoder",
        "windows-media-foundation-aac",
        "aac_encoder_unavailable"))
    {
        Assert-Condition -Condition (-not (
            $platformAssemblyAscii.Contains($forbiddenCodecSymbol, [StringComparison]::OrdinalIgnoreCase) -or
            $platformAssemblyUnicode.Contains($forbiddenCodecSymbol, [StringComparison]::OrdinalIgnoreCase) -or
            $platformAssemblyUnicodeOffsetOne.Contains($forbiddenCodecSymbol, [StringComparison]::OrdinalIgnoreCase))) `
            -Message "Packaged Windows runtime retains forbidden AAC composition symbol '$forbiddenCodecSymbol'."
    }

    $packagedDepsPath = Join-Path $temporaryRoot "IsTranscribe.Desktop.deps.json"
    $desktopLockPath = Join-Path $repoRoot $DesktopLockRelativePath
    $dependencyCount = Assert-LicenseInventory `
        -Inventory $inventory `
        -ReleaseManifest $releaseManifest `
        -InventoryPath $inventoryPath `
        -DesktopLockPath $desktopLockPath `
        -PackagedDepsPath $packagedDepsPath `
        -LockedTools $lockedTools
    $localPayloadVerifier = Join-Path $repoRoot "eng\transcription\Test-LocalTranscriptionPayload.ps1"
    Assert-Condition `
        -Condition (Test-Path -LiteralPath $localPayloadVerifier -PathType Leaf) `
        -Message "The local-transcription package verifier is missing."
    $localPayloadVerificationOutput = @(& pwsh -NoProfile -File $localPayloadVerifier `
        -Rid $ExpectedRuntimeIdentifier `
        -PackageRoot $temporaryRoot `
        -AllowAbsent 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Local-transcription package verification failed: $($localPayloadVerificationOutput -join [Environment]::NewLine)"
    }
    $manifestResult = Assert-RenderedManifest -PackageRoot $temporaryRoot -ReleaseManifest $releaseManifest
    $blockMapResult = Assert-AppxBlockMap -PackageRoot $temporaryRoot
    $forbiddenPayload = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse | Where-Object {
        $_.Extension -in @(
            ".pdb", ".ps1", ".cmd", ".bat",
            ".pfx", ".p12", ".pkcs12", ".pem", ".key", ".pvk", ".snk", ".jks", ".keystore",
            ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".cab", ".nupkg") -or
        $_.Name -eq "Microsoft.Windows.SDK.NET.xml" -or
        $_.Name -eq "IsTranscribe.App.exe" -or
        $_.BaseName -match '(?i)^(?:ffmpeg|ffprobe|lame|libmp3lame|faac|fdkaac|fdk-aac)(?:[-_.].*)?$'
    })
    if ($forbiddenPayload.Count -gt 0)
    {
        $forbiddenNames = @($forbiddenPayload | ForEach-Object {
            [IO.Path]::GetRelativePath($temporaryRoot, $_.FullName)
        }) -join ", "
        throw "MSIX contains forbidden debug, legacy or SDK documentation payload: $forbiddenNames."
    }
    $payloadFileCount = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse).Count
}
catch
{
    $verificationError = $_
    throw
}
finally
{
    try
    {
        Remove-SafeTemporaryDirectory -Path $temporaryRoot -ParentPath $temporaryParent
    }
    catch
    {
        if ($null -eq $verificationError)
        {
            throw
        }
        $verificationError.Exception.Data["TemporaryCleanupFailure"] = $_.Exception.Message
    }
}

$result = [ordered]@{
    schemaVersion = "infra-005-artifact-verification-v1"
    status = "passed"
    releaseDirectory = $releaseRoot
    channel = $channel
    semanticVersion = $semanticVersion
    msix = [ordered]@{
        file = $msix.Name
        sizeBytes = $msix.Length
        sha256 = $msixHash
        identity = $manifestResult.Identity
        version = $manifestResult.Version
        architecture = $ExpectedArchitecture
        minimumWindowsVersion = $manifestResult.MinimumWindowsVersion
        payloadFileCount = $payloadFileCount
        blockMapFileCount = $blockMapResult.FileCount
        verifiedBlockCount = $blockMapResult.BlockCount
        assetCount = $manifestResult.AssetCount
    }
    signing = [ordered]@{
        mode = $signingMode
        publicEligible = [bool]$releaseManifest.signing.publicEligible
        developmentCertificateDeclared = [bool]$developmentCertificateDeclared
        integrityStatus = $signatureResult.Status
        authenticodeStatus = $signatureResult.AuthenticodeStatus
        winTrustCode = $signatureResult.WinTrustCode
        signToolExitCode = $signatureResult.SignToolExitCode
    }
    supplyChain = [ordered]@{
        checksummedFileCount = $checksums.Count
        dependencyCount = $dependencyCount
        missingLicenseMetadataCount = [int]$inventory.missingLicenseMetadataCount
        lockedBuildToolsVersion = $lockedTools.Version
    }
}
$result | ConvertTo-Json -Depth 6 -Compress
