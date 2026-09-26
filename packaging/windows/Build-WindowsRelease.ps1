[CmdletBinding()]
param(
    [string]$Version = "2.0.0",
    [ValidateSet("Development", "Production", "Store")]
    [string]$SigningMode = "Development",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$PackageName = "isTranscribe.Desktop",
    [string]$Publisher,
    [string]$PublisherDisplayName = "isTranscribe",
    [string]$CertificateThumbprint = $env:ISTRANSCRIBE_SIGNING_THUMBPRINT,
    [string]$CertificatePath = $env:ISTRANSCRIBE_SIGNING_PFX_PATH,
    [string]$CertificatePasswordEnvironmentVariable = "ISTRANSCRIBE_SIGNING_PFX_PASSWORD",
    [string]$TimestampUrl,
    [string]$SourceRevision = $env:ISTRANSCRIBE_SOURCE_REVISION,
    [string]$StoreIdentityPath = $env:ISTRANSCRIBE_STORE_IDENTITY_PATH,
    [string]$WhisperNativeOutputRoot = $env:ISTRANSCRIBE_WHISPER_NATIVE_OUTPUT_ROOT,
    [string]$OutputRoot,
    [switch]$CreateDevelopmentCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing
# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#release-contract
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance
$WindowsSdkBuildToolsVersion = "10.0.28000.1839"
$DotNetRuntimePackVersion = "10.0.11"
$ApplicationId = "App"
$PackageDisplayName = "isTranscribe"
$DevelopmentPublisher = "CN=isTranscribe Development"
$ExpectedAssets = @(
    "StoreLogo.png",
    "Square44x44Logo.png",
    "Square150x150Logo.png",
    "Wide310x150Logo.png",
    "Square310x310Logo.png"
)

if ($null -eq ("IsTranscribe.Release.WinTrustVerifier" -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace IsTranscribe.Release
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
                dwUIChoice = 2; // WTD_UI_NONE
                fdwRevocationChecks = 0;
                dwUnionChoice = 1; // WTD_CHOICE_FILE
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, pFile, false);
                dwStateAction = 0;
                hWVTStateData = IntPtr.Zero;
                pwszURLReference = IntPtr.Zero;
                dwProvFlags = 0x1000; // WTD_CACHE_ONLY_URL_RETRIEVAL
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

function Invoke-NativeCommand
{
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Assert-ChildPath
{
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to mutate a path outside '$fullParent': '$fullPath'."
    }
}

function Remove-SafeDirectory
{
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ParentPath
    )

    Assert-ChildPath -Path $Path -ParentPath $ParentPath
    if (Test-Path -LiteralPath $Path)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function ConvertTo-MsixVersion
{
    param([Parameter(Mandatory)][string]$SemanticVersion)

    $pattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<metadata>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $match = [regex]::Match($SemanticVersion, $pattern)
    if (-not $match.Success)
    {
        throw "Version '$SemanticVersion' is not a valid semantic version."
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $patch = [int]$match.Groups['patch'].Value
    foreach ($component in @($major, $minor, $patch))
    {
        if ($component -gt 65535)
        {
            throw "MSIX version components must be between 0 and 65535."
        }
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
            if (-not [int]::TryParse($identifiers[-1], [ref]$sequence) -or $sequence -lt 0 -or $sequence -gt 9999)
            {
                throw "Prerelease versions must end in a numeric sequence from 0 to 9999."
            }
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

    [pscustomobject]@{
        SemanticVersion = $SemanticVersion
        PackageVersion = "$major.$minor.$patch.$revision"
        AssemblyVersion = "$major.$minor.0.0"
    }
}

function Read-StoreIdentityConfiguration
{
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf))
    {
        throw "The configured Store identity file does not exist."
    }
    $rawIdentity = Get-Content -LiteralPath $resolvedPath -Raw -Encoding utf8
    $identityDocument = $null
    try
    {
        $identityDocument = [Text.Json.JsonDocument]::Parse([string]$rawIdentity)
        if ($identityDocument.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object)
        {
            throw "Store identity configuration root must be a JSON object."
        }
        $jsonPropertyNames = @($identityDocument.RootElement.EnumerateObject() | ForEach-Object Name)
        $duplicateProperties = @($jsonPropertyNames | Group-Object -CaseSensitive | Where-Object Count -GT 1)
        if ($duplicateProperties.Count -gt 0)
        {
            throw "Store identity configuration contains duplicate JSON properties."
        }
        $identity = $rawIdentity | ConvertFrom-Json
    }
    catch
    {
        throw "Store identity configuration is invalid JSON: $($_.Exception.Message)"
    }
    finally
    {
        if ($null -ne $identityDocument)
        {
            $identityDocument.Dispose()
        }
    }

    $expectedProperties = @(
        "schemaVersion",
        "configured",
        "reservedProductName",
        "identityName",
        "publisher",
        "publisherDisplayName",
        "packageFamilyName",
        "applicationId")
    $actualProperties = @($identity.PSObject.Properties.Name)
    $missingProperties = @($expectedProperties | Where-Object { $_ -notin $actualProperties })
    $unexpectedProperties = @($actualProperties | Where-Object { $_ -notin $expectedProperties })
    if ($missingProperties.Count -gt 0 -or $unexpectedProperties.Count -gt 0)
    {
        throw "Store identity configuration must contain only the documented schema fields."
    }
    if ($identity.schemaVersion -ne "infra-005-store-identity-v1")
    {
        throw "Store identity configuration schemaVersion is unsupported."
    }
    if ($identity.configured -isnot [bool] -or -not $identity.configured)
    {
        throw "Store identity configuration is not ready. Copy the template and set configured=true only after Partner Center assigns the identity."
    }

    foreach ($field in @("reservedProductName", "identityName", "publisher", "publisherDisplayName", "packageFamilyName", "applicationId"))
    {
        $value = [string]$identity.$field
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[<>\r\n]' -or $value -match '[\x00-\x1F]')
        {
            throw "Store identity field '$field' is empty, has an unresolved placeholder or contains control characters."
        }
    }
    if ([string]$identity.identityName -notmatch '^[A-Za-z0-9.-]{3,50}$')
    {
        throw "Store identityName must contain 3-50 ASCII letters, numbers, dots or hyphens."
    }
    if ([string]$identity.publisher -notmatch '^CN=.{1,253}$')
    {
        throw "Store publisher must be the exact Partner Center distinguished name beginning with 'CN='."
    }
    if ([string]::Equals([string]$identity.publisher, $DevelopmentPublisher, [StringComparison]::Ordinal))
    {
        throw "The development publisher cannot be used for a Store submission."
    }
    if ([string]$identity.publisherDisplayName -match '^\s|\s$' -or ([string]$identity.publisherDisplayName).Length -gt 256)
    {
        throw "Store publisherDisplayName must be 1-256 characters without surrounding whitespace."
    }
    if ([string]$identity.reservedProductName -match '^\s|\s$' -or ([string]$identity.reservedProductName).Length -gt 256)
    {
        throw "Store reservedProductName must be 1-256 characters without surrounding whitespace."
    }
    if ([string]$identity.packageFamilyName -notmatch '^[A-Za-z0-9.-]{3,50}_[a-hjkmnp-tv-z0-9]{13}$')
    {
        throw "Store packageFamilyName does not have the Partner Center package-family format."
    }
    $expectedPublisherId = Get-PackagePublisherId -Publisher ([string]$identity.publisher)
    $expectedPackageFamilyName = "$([string]$identity.identityName)_$expectedPublisherId"
    if (-not [string]::Equals(
        [string]$identity.packageFamilyName,
        $expectedPackageFamilyName,
        [StringComparison]::Ordinal))
    {
        throw "Store packageFamilyName does not match the configured identityName and publisher."
    }
    if (-not [string]::Equals([string]$identity.applicationId, $ApplicationId, [StringComparison]::Ordinal))
    {
        throw "Store applicationId must match the packaged application Id '$ApplicationId'."
    }

    [pscustomobject]@{
        Path = $resolvedPath
        Sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Identity = $identity
    }
}

function Get-PackagePublisherId
{
    param([Parameter(Mandatory)][string]$Publisher)

    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes($Publisher))
    $bits = -join ($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') })
    $bits += "0"
    $alphabet = "0123456789abcdefghjkmnpqrstvwxyz"
    -join (0..12 | ForEach-Object {
        $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)]
    })
}

function Get-BuildToolsPackageRoot
{
    param([Parameter(Mandatory)][string]$ProjectPath)

    $assetsPath = Join-Path (Split-Path -Parent $ProjectPath) "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath))
    {
        throw "Windows SDK BuildTools restore did not create '$assetsPath'."
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $libraryName = "Microsoft.Windows.SDK.BuildTools/$WindowsSdkBuildToolsVersion"
    $library = $assets.libraries.PSObject.Properties | Where-Object Name -EQ $libraryName | Select-Object -First 1
    $packageFolder = $assets.packageFolders.PSObject.Properties | Select-Object -First 1
    if ($null -eq $library -or $null -eq $packageFolder)
    {
        throw "Unable to resolve '$libraryName' from the locked restore graph."
    }

    Join-Path $packageFolder.Name $library.Value.path
}

function Get-WindowsSdkTool
{
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$Name
    )

    $tool = Get-ChildItem -LiteralPath $PackageRoot -Filter $Name -File -Recurse |
        Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $tool)
    {
        throw "The x64 '$Name' tool was not found in Microsoft.Windows.SDK.BuildTools $WindowsSdkBuildToolsVersion."
    }

    $tool.FullName
}

function Get-CodeSigningCertificate
{
    param(
        [Parameter(Mandatory)][string]$Mode,
        [string]$Thumbprint,
        [string]$PfxPath,
        [string]$RequestedPublisher,
        [switch]$AllowCreateDevelopmentCertificate,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$TemporaryThumbprints
    )

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint) -and -not [string]::IsNullOrWhiteSpace($PfxPath))
    {
        throw "Specify a certificate thumbprint or an external PFX path, not both."
    }

    if (-not [string]::IsNullOrWhiteSpace($PfxPath))
    {
        $resolvedPfx = [IO.Path]::GetFullPath($PfxPath)
        if (-not (Test-Path -LiteralPath $resolvedPfx -PathType Leaf))
        {
            throw "The configured PFX file does not exist."
        }

        $repositoryPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($resolvedPfx.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Signing PFX files must be supplied from outside the repository."
        }

        $password = [Environment]::GetEnvironmentVariable($CertificatePasswordEnvironmentVariable)
        if ([string]::IsNullOrEmpty($password))
        {
            throw "Set the '$CertificatePasswordEnvironmentVariable' secret environment variable for PFX signing."
        }

        $before = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' | Select-Object -ExpandProperty Thumbprint)
        $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
        $imported = @(Import-PfxCertificate -FilePath $resolvedPfx -Password $securePassword -CertStoreLocation 'Cert:\CurrentUser\My' -Exportable:$false)
        foreach ($item in $imported)
        {
            if ($item.Thumbprint -notin $before)
            {
                $TemporaryThumbprints.Add($item.Thumbprint)
            }
        }
        $certificate = $imported | Where-Object HasPrivateKey | Select-Object -First 1
        if ($null -eq $certificate)
        {
            throw "The supplied PFX does not contain a private code-signing key."
        }
        return [pscustomobject]@{ Certificate = $certificate; StoreScope = "CurrentUser"; CreatedForDevelopment = $false }
    }

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint))
    {
        $normalized = $Thumbprint.Replace(" ", [string]::Empty)
        foreach ($scope in @("CurrentUser", "LocalMachine"))
        {
            $candidate = Get-Item -LiteralPath "Cert:\$scope\My\$normalized" -ErrorAction SilentlyContinue
            if ($null -ne $candidate)
            {
                return [pscustomobject]@{ Certificate = $candidate; StoreScope = $scope; CreatedForDevelopment = $false }
            }
        }
        throw "The configured signing certificate was not found in a Personal certificate store."
    }

    if ($Mode -eq "Production")
    {
        throw "Production builds require ISTRANSCRIBE_SIGNING_THUMBPRINT or ISTRANSCRIBE_SIGNING_PFX_PATH."
    }

    $publisherSubject = if ([string]::IsNullOrWhiteSpace($RequestedPublisher)) { $DevelopmentPublisher } else { $RequestedPublisher }
    $certificate = Get-ChildItem -Path 'Cert:\CurrentUser\My' -CodeSigningCert |
        Where-Object { $_.Subject -eq $publisherSubject -and $_.HasPrivateKey -and $_.NotAfter -gt [DateTime]::UtcNow } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    $created = $false
    if ($null -eq $certificate -and $AllowCreateDevelopmentCertificate)
    {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $publisherSubject `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -NotAfter ([DateTime]::UtcNow.AddYears(2))

        $created = $true
    }

    if ($null -eq $certificate)
    {
        throw "No development code-signing certificate was found. Supply -CertificateThumbprint or use -CreateDevelopmentCertificate."
    }

    [pscustomobject]@{ Certificate = $certificate; StoreScope = "CurrentUser"; CreatedForDevelopment = $created }
}

function Get-PackageLicenseMetadata
{
    param(
        [Parameter(Mandatory)][string]$GlobalPackagesRoot,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version
    )

    $packageRoot = Join-Path (Join-Path $GlobalPackagesRoot $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
    $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $nuspec)
    {
        return [ordered]@{ expression = $null; file = $null; url = $null; projectUrl = $null; authors = $null }
    }

    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $document.package.metadata
    $licenseNode = $metadata.SelectSingleNode("./*[local-name()='license']")
    $licenseUrlNode = $metadata.SelectSingleNode("./*[local-name()='licenseUrl']")
    $projectUrlNode = $metadata.SelectSingleNode("./*[local-name()='projectUrl']")
    $authorsNode = $metadata.SelectSingleNode("./*[local-name()='authors']")
    $licenseType = if ($null -ne $licenseNode) { $licenseNode.GetAttribute("type") } else { $null }
    $licenseValue = if ($null -ne $licenseNode) { $licenseNode.InnerText.Trim() } else { $null }
    [ordered]@{
        expression = if ($licenseType -eq "expression") { $licenseValue } else { $null }
        file = if ($licenseType -eq "file") { $licenseValue } else { $null }
        url = if ($null -ne $licenseUrlNode) { $licenseUrlNode.InnerText.Trim() } else { $null }
        projectUrl = if ($null -ne $projectUrlNode) { $projectUrlNode.InnerText.Trim() } else { $null }
        authors = if ($null -ne $authorsNode) { $authorsNode.InnerText.Trim() } else { $null }
    }
}

function Write-DependencyInventory
{
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$BuildToolsProjectPath,
        [Parameter(Mandatory)][string]$PublishedDepsPath,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$GeneratedUtc,
        [Parameter(Mandatory)][string]$SemanticVersion,
        [Parameter(Mandatory)][string]$ExpectedRuntimePackVersion,
        [switch]$RequireCompleteLicenseMetadata
    )

    $listOutput = & dotnet list $ProjectPath package --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0)
    {
        throw "Unable to collect the release dependency graph."
    }
    $graph = ($listOutput -join [Environment]::NewLine) | ConvertFrom-Json

    $assetsPath = Join-Path (Split-Path -Parent $ProjectPath) "obj\project.assets.json"
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $globalPackagesRoot = ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name

    $packages = [ordered]@{}
    foreach ($project in $graph.projects)
    {
        foreach ($framework in $project.frameworks)
        {
            foreach ($package in @($framework.topLevelPackages))
            {
                $packages[$package.id.ToLowerInvariant()] = [ordered]@{
                    id = $package.id
                    version = $package.resolvedVersion
                    direct = $true
                    acquisition = "packageReference"
                }
            }
            foreach ($package in @($framework.transitivePackages))
            {
                $key = $package.id.ToLowerInvariant()
                if (-not $packages.Contains($key))
                {
                    $packages[$key] = [ordered]@{
                        id = $package.id
                        version = $package.resolvedVersion
                        direct = $false
                        acquisition = "transitive"
                    }
                }
            }
        }
    }

    $windowsSdkDownload = $assets.libraries.PSObject.Properties |
        Where-Object { $_.Name -like 'Microsoft.Windows.SDK.NET.Ref/*' -and $_.Value.type -eq 'package' } |
        Select-Object -First 1
    if ($null -ne $windowsSdkDownload)
    {
        $separator = $windowsSdkDownload.Name.LastIndexOf('/')
        $id = $windowsSdkDownload.Name.Substring(0, $separator)
        $resolvedVersion = $windowsSdkDownload.Name.Substring($separator + 1)
        $packages[$id.ToLowerInvariant()] = [ordered]@{
            id = $id
            version = $resolvedVersion
            direct = $true
            acquisition = "packageDownload"
        }
    }

    foreach ($assetLibrary in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
    {
        $separator = $assetLibrary.Name.LastIndexOf('/')
        $id = $assetLibrary.Name.Substring(0, $separator)
        $resolvedVersion = $assetLibrary.Name.Substring($separator + 1)
        $key = $id.ToLowerInvariant()
        if (-not $packages.Contains($key))
        {
            $packages[$key] = [ordered]@{
                id = $id
                version = $resolvedVersion
                direct = $false
                acquisition = "assetGraph"
            }
        }
        $packages[$key]["contentHashSha512"] = $assetLibrary.Value.sha512
    }

    $buildToolsAssetsPath = Join-Path (Split-Path -Parent $BuildToolsProjectPath) "obj\project.assets.json"
    $buildToolsAssets = Get-Content -LiteralPath $buildToolsAssetsPath -Raw | ConvertFrom-Json
    $buildToolsLibrary = $buildToolsAssets.libraries.PSObject.Properties |
        Where-Object Name -EQ "Microsoft.Windows.SDK.BuildTools/$WindowsSdkBuildToolsVersion" |
        Select-Object -First 1
    if ($null -eq $buildToolsLibrary)
    {
        throw "The dependency inventory cannot resolve Microsoft.Windows.SDK.BuildTools."
    }
    $packages["microsoft.windows.sdk.buildtools"] = [ordered]@{
        id = "Microsoft.Windows.SDK.BuildTools"
        version = $WindowsSdkBuildToolsVersion
        direct = $true
        acquisition = "buildTool"
        contentHashSha512 = $buildToolsLibrary.Value.sha512
    }

    if (-not (Test-Path -LiteralPath $PublishedDepsPath -PathType Leaf))
    {
        throw "The published dependency manifest is missing."
    }
    $publishedDependencies = Get-Content -LiteralPath $PublishedDepsPath -Raw | ConvertFrom-Json
    foreach ($runtimeLibrary in $publishedDependencies.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -eq "runtimepack" })
    {
        $separator = $runtimeLibrary.Name.LastIndexOf('/')
        if ($separator -le 0)
        {
            throw "The published runtime-pack identity '$($runtimeLibrary.Name)' is invalid."
        }
        $rawId = $runtimeLibrary.Name.Substring(0, $separator)
        $runtimeId = if ($rawId.StartsWith("runtimepack.", [StringComparison]::OrdinalIgnoreCase))
        {
            $rawId.Substring("runtimepack.".Length)
        }
        else
        {
            $rawId
        }
        $runtimeVersion = $runtimeLibrary.Name.Substring($separator + 1)
        if ($runtimeId -eq "Microsoft.NETCore.App.Runtime.win-x64" -and
            $runtimeVersion -ne $ExpectedRuntimePackVersion)
        {
            throw "The published .NET runtime pack '$runtimeVersion' does not match the pinned '$ExpectedRuntimePackVersion'."
        }

        $runtimeAssetName = "$runtimeId/$runtimeVersion"
        $runtimeAsset = $assets.libraries.PSObject.Properties[$runtimeAssetName]
        if ($null -eq $runtimeAsset -or [string]::IsNullOrWhiteSpace([string]$runtimeAsset.Value.sha512))
        {
            throw "The locked NuGet content hash for runtime pack '$runtimeAssetName' is unavailable."
        }
        $packages[$runtimeId.ToLowerInvariant()] = [ordered]@{
            id = $runtimeId
            version = $runtimeVersion
            direct = $true
            acquisition = "runtimePack"
            contentHashSha512 = $runtimeAsset.Value.sha512
        }
    }

    $dependencies = foreach ($package in ($packages.Values | Sort-Object id))
    {
        $license = Get-PackageLicenseMetadata -GlobalPackagesRoot $globalPackagesRoot -Id $package.id -Version $package.version
        [ordered]@{
            id = $package.id
            version = $package.version
            direct = $package.direct
            acquisition = $package.acquisition
            contentHashSha512 = $package.contentHashSha512
            license = $license
        }
    }

    $missingLicenseMetadata = @($dependencies | Where-Object {
        [string]::IsNullOrWhiteSpace($_.license.expression) -and
        [string]::IsNullOrWhiteSpace($_.license.file) -and
        [string]::IsNullOrWhiteSpace($_.license.url)
    })
    if ($RequireCompleteLicenseMetadata -and $missingLicenseMetadata.Count -gt 0)
    {
        $missingIds = ($missingLicenseMetadata | ForEach-Object id) -join ", "
        throw "Production dependency inventory has packages without license metadata: $missingIds."
    }

    $inventory = [ordered]@{
        schemaVersion = "infra-005-dependencies-v1"
        product = "isTranscribe"
        semanticVersion = $SemanticVersion
        generatedUtc = $GeneratedUtc
        packageCount = @($dependencies).Count
        missingLicenseMetadataCount = $missingLicenseMetadata.Count
        dependencies = @($dependencies)
    }
    $inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Destination -Encoding utf8
    @($dependencies).Count
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$appProject = Join-Path $repoRoot "src\IsTranscribe.App.Windows\IsTranscribe.App.Windows.csproj"
$workerProject = Join-Path $repoRoot "src\IsTranscribe.Transcription.Worker\IsTranscribe.Transcription.Worker.csproj"
$localPayloadComposer = Join-Path $repoRoot "eng\transcription\Compose-LocalTranscriptionPayload.ps1"
$localPayloadVerifier = Join-Path $repoRoot "eng\transcription\Test-LocalTranscriptionPayload.ps1"
$whisperRuntimeManifest = Join-Path $repoRoot "native\whisper\runtime-manifest.v1.json"
$buildToolsProject = Join-Path $PSScriptRoot "WindowsSdkTools.csproj"
$manifestTemplate = Join-Path $PSScriptRoot "msix\AppxManifest.template.xml"
$assetsRoot = Join-Path $PSScriptRoot "msix\Assets"
$releaseIdentityPolicyPath = Join-Path $PSScriptRoot "release-identity-policy.json"
$desktopIcon = Join-Path $repoRoot "src\IsTranscribe.Desktop\Assets\isTranscribe.ico"
$artifactsRoot = Join-Path $repoRoot "artifacts"
if ([string]::IsNullOrWhiteSpace($OutputRoot))
{
    $OutputRoot = Join-Path $artifactsRoot "release\windows-x64"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing
if (-not (Test-Path -LiteralPath $releaseIdentityPolicyPath -PathType Leaf))
{
    throw "Checked-in release identity policy is missing."
}
try
{
    $releaseIdentityPolicy = Get-Content -LiteralPath $releaseIdentityPolicyPath -Raw -Encoding utf8 |
        ConvertFrom-Json
}
catch
{
    throw "Release identity policy is invalid JSON: $($_.Exception.Message)"
}
if ($releaseIdentityPolicy.schemaVersion -ne "infra-005-release-identity-policy-v1")
{
    throw "Release identity policy schemaVersion is unsupported."
}
$releaseIdentityPolicyHash = (Get-FileHash -LiteralPath $releaseIdentityPolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$storeIdentityResult = $null
$storeIdentity = $null
if ($SigningMode -eq "Store")
{
    if ([string]::IsNullOrWhiteSpace($StoreIdentityPath))
    {
        throw "Store builds require -StoreIdentityPath or ISTRANSCRIBE_STORE_IDENTITY_PATH."
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        -not [string]::IsNullOrWhiteSpace($CertificatePath) -or
        -not [string]::IsNullOrWhiteSpace($TimestampUrl) -or
        $CreateDevelopmentCertificate)
    {
        throw "Store submissions must not use a local signing certificate, PFX, timestamp or development-certificate creation."
    }

    $storeIdentityResult = Read-StoreIdentityConfiguration -Path $StoreIdentityPath
    $storeIdentity = $storeIdentityResult.Identity
    if ($PSBoundParameters.ContainsKey("PackageName") -and
        -not [string]::Equals($PackageName, [string]$storeIdentity.identityName, [StringComparison]::Ordinal))
    {
        throw "PackageName conflicts with the configured Partner Center identity."
    }
    if ($PSBoundParameters.ContainsKey("Publisher") -and
        -not [string]::Equals($Publisher, [string]$storeIdentity.publisher, [StringComparison]::Ordinal))
    {
        throw "Publisher conflicts with the configured Partner Center identity."
    }
    if ($PSBoundParameters.ContainsKey("PublisherDisplayName") -and
        -not [string]::Equals($PublisherDisplayName, [string]$storeIdentity.publisherDisplayName, [StringComparison]::Ordinal))
    {
        throw "PublisherDisplayName conflicts with the configured Partner Center identity."
    }
    $PackageName = [string]$storeIdentity.identityName
    $Publisher = [string]$storeIdentity.publisher
    $PublisherDisplayName = [string]$storeIdentity.publisherDisplayName
    $PackageDisplayName = [string]$storeIdentity.reservedProductName
}
elseif (-not [string]::Equals($PackageName, [string]$releaseIdentityPolicy.packageName, [StringComparison]::Ordinal))
{
    throw "PackageName must match the checked-in stable release identity policy."
}
if ($SigningMode -ne "Store" -and -not [string]::Equals(
    $PublisherDisplayName,
    [string]$releaseIdentityPolicy.publisherDisplayName,
    [StringComparison]::Ordinal))
{
    throw "PublisherDisplayName must match the checked-in release identity policy."
}
if (-not [string]::Equals(
    $DevelopmentPublisher,
    [string]$releaseIdentityPolicy.developmentPublisher,
    [StringComparison]::Ordinal))
{
    throw "Development publisher constant does not match the checked-in release identity policy."
}
$approvedProductionPublishers = @($releaseIdentityPolicy.productionPublisherSubjects)
if ($SigningMode -eq "Development" -and
    -not [string]::IsNullOrWhiteSpace($Publisher) -and
    -not [string]::Equals($Publisher, $DevelopmentPublisher, [StringComparison]::Ordinal))
{
    throw "Development Publisher must match the checked-in development identity."
}
if ($SigningMode -eq "Production")
{
    if ($approvedProductionPublishers.Count -eq 0)
    {
        throw "Release identity policy has no approved production publisher. Complete a reviewed identity-policy rollover first."
    }
    if (-not [string]::IsNullOrWhiteSpace($Publisher) -and
        $Publisher -cnotin $approvedProductionPublishers)
    {
        throw "Production Publisher is absent from the checked-in release identity allowlist."
    }
}

if ($RuntimeIdentifier -ne "win-x64")
{
    throw "INFRA-005.A publishes only win-x64."
}
if ([string]::IsNullOrWhiteSpace($WhisperNativeOutputRoot))
{
    throw "FEAT-016 Windows release requires -WhisperNativeOutputRoot or ISTRANSCRIBE_WHISPER_NATIVE_OUTPUT_ROOT."
}
$WhisperNativeOutputRoot = [IO.Path]::GetFullPath($WhisperNativeOutputRoot)
foreach ($requiredLocalRuntimeInput in @(
    $workerProject,
    $localPayloadComposer,
    $localPayloadVerifier,
    $whisperRuntimeManifest,
    (Join-Path $WhisperNativeOutputRoot "win-x64\cpu\istranscribe_whisper_v1.dll"),
    (Join-Path $WhisperNativeOutputRoot "inventory\win-x64-cpu.inventory.v1.json")))
{
    if (-not (Test-Path -LiteralPath $requiredLocalRuntimeInput -PathType Leaf))
    {
        throw "Required FEAT-016 release input '$requiredLocalRuntimeInput' is missing."
    }
}
if ($SigningMode -in @("Production", "Store") -and $Configuration -ne "Release")
{
    throw "Production and Store packages must use the Release configuration."
}
if ($PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$')
{
    throw "PackageName must contain 3-50 ASCII letters, numbers, dots or hyphens."
}
if (-not (Test-Path -LiteralPath $manifestTemplate -PathType Leaf))
{
    throw "MSIX manifest template is missing."
}
if (-not (Test-Path -LiteralPath $desktopIcon -PathType Leaf))
{
    throw "Desktop icon is missing."
}
foreach ($asset in $ExpectedAssets)
{
    if (-not (Test-Path -LiteralPath (Join-Path $assetsRoot $asset) -PathType Leaf))
    {
        throw "Required MSIX asset '$asset' is missing."
    }
}

$versionInfo = ConvertTo-MsixVersion -SemanticVersion $Version
$buildUtc = [DateTimeOffset]::UtcNow.ToString("O")
if ([string]::IsNullOrWhiteSpace($SourceRevision))
{
    if ($SigningMode -in @("Production", "Store"))
    {
        throw "Production and Store builds require -SourceRevision or ISTRANSCRIBE_SOURCE_REVISION."
    }
    $SourceRevision = "local-workspace"
}
if ($SigningMode -eq "Production")
{
    if ([string]::IsNullOrWhiteSpace($TimestampUrl))
    {
        throw "Production builds require -TimestampUrl."
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @("http", "https"))
    {
        throw "TimestampUrl must be an absolute HTTP or HTTPS URL."
    }
}

$safeVersion = $Version -replace '[^0-9A-Za-z.-]', '_'
$distributionChannel = switch ($SigningMode)
{
    "Production" { "public" }
    "Store" { "store" }
    default { "development" }
}
$channelRoot = Join-Path $OutputRoot $distributionChannel
$releaseRoot = Join-Path $channelRoot $safeVersion
$stagingParent = Join-Path $OutputRoot ".staging"
$stagingRoot = Join-Path $stagingParent ([Guid]::NewGuid().ToString("N"))
$publishRoot = Join-Path $stagingRoot "publish"
$workerPublishRoot = Join-Path $stagingRoot "worker-publish"
$buildOutputRoot = Join-Path $stagingRoot "build-output"
$packageRoot = Join-Path $stagingRoot "package"
$candidateRoot = Join-Path $stagingRoot "candidate"
$temporaryCertificateThumbprints = [System.Collections.Generic.List[string]]::new()
$releaseLock = $null

New-Item -ItemType Directory -Path $OutputRoot, $channelRoot -Force | Out-Null
$releaseLockPath = Join-Path $channelRoot ".$safeVersion.release.lock"
try
{
    $releaseLock = [IO.FileStream]::new(
        $releaseLockPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None,
        1,
        [IO.FileOptions]::DeleteOnClose)
}
catch [IO.IOException]
{
    throw "Another release build is already active for '$distributionChannel/$safeVersion'."
}

try
{
    New-Item -ItemType Directory -Path $candidateRoot, $publishRoot, $workerPublishRoot, $packageRoot -Force | Out-Null
    if ($SigningMode -eq "Development")
    {
        @(
            "DEVELOPMENT ONLY",
            "This package is signed with a local test identity.",
            "It is not eligible for public distribution."
        ) | Set-Content -LiteralPath (Join-Path $candidateRoot "DEVELOPMENT_ONLY.txt") -Encoding ascii
    }
    elseif ($SigningMode -eq "Store")
    {
        @(
            "STORE SUBMISSION ONLY",
            "This unsigned package must be submitted through Microsoft Partner Center.",
            "Do not distribute it directly to users.",
            "Microsoft Store signing is required before installation."
        ) | Set-Content -LiteralPath (Join-Path $candidateRoot "STORE_SUBMISSION_ONLY.txt") -Encoding ascii
        Copy-Item -LiteralPath $storeIdentityResult.Path -Destination (Join-Path $candidateRoot "store-identity.json")
    }

    Write-Host "Restoring locked Windows SDK packaging tools..."
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "restore", $buildToolsProject, "--locked-mode"
    ) -FailureMessage "Windows SDK BuildTools restore failed."

    Write-Host "Restoring the final Desktop dependency graph..."
    $restoreArguments = @(
        "restore", $appProject,
        "-r", $RuntimeIdentifier,
        "--locked-mode",
        "-p:RuntimeFrameworkVersion=$DotNetRuntimePackVersion")
    Invoke-NativeCommand -FilePath "dotnet" -Arguments $restoreArguments -FailureMessage "Desktop restore failed."

    Write-Host "Restoring the isolated local-transcription worker for the exact release RID..."
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "restore", $workerProject,
        "-r", $RuntimeIdentifier,
        "-p:RuntimeFrameworkVersion=$DotNetRuntimePackVersion"
    ) -FailureMessage "Local transcription worker restore failed."

    $certificateResult = $null
    $certificate = $null
    if ($SigningMode -ne "Store")
    {
        $certificateResult = Get-CodeSigningCertificate `
            -Mode $SigningMode `
            -Thumbprint $CertificateThumbprint `
            -PfxPath $CertificatePath `
            -RequestedPublisher $Publisher `
            -AllowCreateDevelopmentCertificate:$CreateDevelopmentCertificate `
            -RepositoryRoot $repoRoot `
            -TemporaryThumbprints $temporaryCertificateThumbprints
        $certificate = $certificateResult.Certificate
        if (-not $certificate.HasPrivateKey)
        {
            throw "The signing certificate does not have a private key."
        }
        if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow)
        {
            throw "The signing certificate has expired."
        }
        if ($certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow)
        {
            throw "The signing certificate is not valid yet."
        }
        $enhancedKeyUsage = $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
            Select-Object -First 1
        $hasCodeSigningUsage = $null -ne $enhancedKeyUsage -and
            $null -ne ($enhancedKeyUsage.EnhancedKeyUsages | Where-Object Value -EQ "1.3.6.1.5.5.7.3.3" | Select-Object -First 1)
        if (-not $hasCodeSigningUsage)
        {
            throw "The signing certificate must include the Code Signing enhanced key usage."
        }

        if ([string]::IsNullOrWhiteSpace($Publisher))
        {
            $Publisher = $certificate.Subject
        }
        if (-not [string]::Equals($Publisher, $certificate.Subject, [StringComparison]::OrdinalIgnoreCase))
        {
            throw "The manifest publisher must exactly match the signing certificate subject."
        }
        if ($SigningMode -eq "Development" -and
            -not [string]::Equals($certificate.Subject, $DevelopmentPublisher, [StringComparison]::Ordinal))
        {
            throw "Development signing certificate subject must match the checked-in development identity."
        }
        if ($SigningMode -eq "Production" -and $certificate.Subject -cnotin $approvedProductionPublishers)
        {
            throw "Production signing certificate subject is absent from the checked-in release identity allowlist."
        }
        if ($SigningMode -eq "Production" -and $Publisher -eq $DevelopmentPublisher)
        {
            throw "The development publisher cannot be used for a production package."
        }
        if ($SigningMode -eq "Production" -and $certificate.Subject -eq $certificate.Issuer)
        {
            throw "Production packages cannot use a self-signed certificate."
        }
    }

    $developmentCertificateFileName = $null
    $developmentCertificatePath = $null
    if ($SigningMode -eq "Development")
    {
        $developmentCertificateFileName = "isTranscribe-development-certificate.cer"
        $developmentCertificatePath = Join-Path $candidateRoot $developmentCertificateFileName
        [IO.File]::WriteAllBytes(
            $developmentCertificatePath,
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }

    Write-Host "Publishing the self-contained Avalonia win-x64 payload..."
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "publish", $appProject,
        "-c", $Configuration,
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:RuntimeFrameworkVersion=$DotNetRuntimePackVersion",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-p:BaseOutputPath=$buildOutputRoot\",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$($versionInfo.AssemblyVersion)",
        "-p:FileVersion=$($versionInfo.PackageVersion)",
        "-p:InformationalVersion=$Version",
        "-p:Product=isTranscribe",
        "-p:Company=$PublisherDisplayName",
        "-o", $publishRoot
    ) -FailureMessage "Desktop publish failed."

    Write-Host "Publishing the isolated local-transcription worker into a separate payload..."
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "publish", $workerProject,
        "-c", $Configuration,
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:RuntimeFrameworkVersion=$DotNetRuntimePackVersion",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-p:BaseOutputPath=$buildOutputRoot\worker\",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$($versionInfo.AssemblyVersion)",
        "-p:FileVersion=$($versionInfo.PackageVersion)",
        "-p:InformationalVersion=$Version",
        "-p:Product=isTranscribe",
        "-p:Company=$PublisherDisplayName",
        "-o", $workerPublishRoot
    ) -FailureMessage "Local transcription worker publish failed."

    Get-ChildItem -LiteralPath $publishRoot, $workerPublishRoot -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force
    $windowsSdkDocumentation = Join-Path $publishRoot "Microsoft.Windows.SDK.NET.xml"
    if (Test-Path -LiteralPath $windowsSdkDocumentation)
    {
        Remove-Item -LiteralPath $windowsSdkDocumentation -Force
    }

    Write-Host "Composing the verified worker and native Whisper payload..."
    Invoke-NativeCommand -FilePath "pwsh" -Arguments @(
        "-NoProfile", "-File", $localPayloadComposer,
        "-Rid", $RuntimeIdentifier,
        "-ApplicationPublishRoot", $publishRoot,
        "-WorkerPublishRoot", $workerPublishRoot,
        "-NativeOutputRoot", $WhisperNativeOutputRoot,
        "-ManifestPath", $whisperRuntimeManifest
    ) -FailureMessage "Local transcription payload composition failed."

    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $packageRoot -Recurse -Force
    Copy-Item -LiteralPath $assetsRoot -Destination (Join-Path $packageRoot "Assets") -Recurse -Force

    $inventoryPath = Join-Path $candidateRoot "dependency-license-inventory.json"
    [void](Write-DependencyInventory `
        -ProjectPath $appProject `
        -BuildToolsProjectPath $buildToolsProject `
        -PublishedDepsPath (Join-Path $publishRoot "IsTranscribe.Desktop.deps.json") `
        -Destination $inventoryPath `
        -GeneratedUtc $buildUtc `
        -SemanticVersion $Version `
        -ExpectedRuntimePackVersion $DotNetRuntimePackVersion `
        -RequireCompleteLicenseMetadata:($SigningMode -in @("Production", "Store")))
    $noticeFileName = "THIRD-PARTY-NOTICES.txt"
    $noticePath = Join-Path $packageRoot $noticeFileName
    $noticeResultPath = Join-Path $stagingRoot "third-party-notice-result.json"
    Write-Host "Generating deterministic third-party notices..."
    Invoke-NativeCommand -FilePath "pwsh" -Arguments @(
        "-NoProfile", "-File", (Join-Path $PSScriptRoot "New-ThirdPartyNotices.ps1"),
        "-BaseInventoryPath", $inventoryPath,
        "-PublishedDepsPath", (Join-Path $publishRoot "IsTranscribe.Desktop.deps.json"),
        "-PublishRoot", $publishRoot,
        "-InventoryOutputPath", $inventoryPath,
        "-NoticeOutputPath", $noticePath,
        "-ResultOutputPath", $noticeResultPath,
        "-NativeRuntimeInventoryPath", (Join-Path $publishRoot "native\runtime-inventory.v1.json"),
        "-NativeRuntimeManifestPath", $whisperRuntimeManifest
    ) -FailureMessage "Third-party notice generation failed."
    $noticeResult = Get-Content -LiteralPath $noticeResultPath -Raw | ConvertFrom-Json
    $dependencyCount = [int]$noticeResult.packageCount
    $redistributedDependencyCount = [int]$noticeResult.redistributedPackageCount

    $template = Get-Content -LiteralPath $manifestTemplate -Raw
    $tokens = [ordered]@{
        "{{PACKAGE_NAME}}" = [Security.SecurityElement]::Escape($PackageName)
        "{{PUBLISHER}}" = [Security.SecurityElement]::Escape($Publisher)
        "{{PACKAGE_VERSION}}" = $versionInfo.PackageVersion
        "{{PUBLISHER_DISPLAY_NAME}}" = [Security.SecurityElement]::Escape($PublisherDisplayName)
        "{{PACKAGE_DISPLAY_NAME}}" = [Security.SecurityElement]::Escape($PackageDisplayName)
    }
    foreach ($token in $tokens.GetEnumerator())
    {
        $template = $template.Replace($token.Key, $token.Value)
    }
    if ($template.Contains("{{", [StringComparison]::Ordinal))
    {
        throw "The rendered MSIX manifest contains an unresolved token."
    }
    if ($SigningMode -eq "Store")
    {
        [xml]$storeManifestDocument = $template
        $storeIneligibleNodes = @($storeManifestDocument.SelectNodes(
            "//*[local-name()='FileSystemWriteVirtualization' or " +
            "local-name()='RegistryWriteVirtualization' or " +
            "(local-name()='Capability' and @Name='unvirtualizedResources')]"))
        foreach ($node in $storeIneligibleNodes)
        {
            [void]$node.ParentNode.RemoveChild($node)
        }
        if ($storeIneligibleNodes.Count -ne 3)
        {
            throw "Store manifest preparation did not remove the expected restricted virtualization declarations."
        }
        $template = $storeManifestDocument.OuterXml
    }
    $manifestPath = Join-Path $packageRoot "AppxManifest.xml"
    $template | Set-Content -LiteralPath $manifestPath -Encoding utf8

    Write-Host "Verifying the composed local-transcription package payload..."
    Invoke-NativeCommand -FilePath "pwsh" -Arguments @(
        "-NoProfile", "-File", $localPayloadVerifier,
        "-Rid", $RuntimeIdentifier,
        "-PackageRoot", $packageRoot
    ) -FailureMessage "Local transcription package verification failed."

    $buildToolsRoot = Get-BuildToolsPackageRoot -ProjectPath $buildToolsProject
    $makeAppx = Get-WindowsSdkTool -PackageRoot $buildToolsRoot -Name "makeappx.exe"
    $signTool = if ($SigningMode -eq "Store") { $null } else { Get-WindowsSdkTool -PackageRoot $buildToolsRoot -Name "signtool.exe" }
    $msixFileName = switch ($SigningMode)
    {
        "Development" { "isTranscribe-$safeVersion-dev-win-x64.msix" }
        "Store" { "isTranscribe-$safeVersion-store-win-x64.msix" }
        default { "isTranscribe-$safeVersion-win-x64.msix" }
    }
    $msixPath = Join-Path $candidateRoot $msixFileName

    Write-Host "Creating the MSIX package..."
    Invoke-NativeCommand -FilePath $makeAppx -Arguments @(
        "pack", "/d", $packageRoot, "/p", $msixPath, "/h", "SHA256", "/o"
    ) -FailureMessage "MakeAppx failed."

    if ($SigningMode -eq "Store")
    {
        Write-Host "Verifying the unsigned Microsoft Store submission package..."
        $signatureObservation = Get-AuthenticodeSignature -LiteralPath $msixPath
        if ($signatureObservation.Status -ne "NotSigned" -or $null -ne $signatureObservation.SignerCertificate)
        {
            throw "Store submission package must remain unsigned until Microsoft Store certification."
        }
        Add-Type -AssemblyName System.IO.Compression
        $storeArchive = [IO.Compression.ZipFile]::OpenRead($msixPath)
        try
        {
            if ($null -ne ($storeArchive.Entries | Where-Object FullName -EQ "AppxSignature.p7x" | Select-Object -First 1))
            {
                throw "Unsigned Store submission unexpectedly contains AppxSignature.p7x."
            }
        }
        finally
        {
            $storeArchive.Dispose()
        }
        $signerMatches = $false
        $signatureTrustStatus = "StoreSubmissionUnsigned"
        $timestampVerified = $false
    }
    else
    {
        Write-Host "Signing the MSIX package ($SigningMode)..."
        $signArguments = @("sign", "/fd", "SHA256", "/sha1", $certificate.Thumbprint, "/s", "My")
        if ($certificateResult.StoreScope -eq "LocalMachine")
        {
            $signArguments += "/sm"
        }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl))
        {
            $signArguments += @("/tr", $TimestampUrl, "/td", "SHA256")
        }
        $signArguments += $msixPath
        Invoke-NativeCommand -FilePath $signTool -Arguments $signArguments -FailureMessage "MSIX signing failed."
        $signatureObservation = Get-AuthenticodeSignature -LiteralPath $msixPath
        $signerMatches = $null -ne $signatureObservation.SignerCertificate -and
            $signatureObservation.SignerCertificate.Thumbprint -eq $certificate.Thumbprint
        if ($SigningMode -eq "Production")
        {
            Invoke-NativeCommand -FilePath $signTool -Arguments @(
                "verify", "/pa", "/all", "/tw", "/sha1", $certificate.Thumbprint, "/v", $msixPath
            ) -FailureMessage "MSIX signature verification failed."
            if ($signatureObservation.Status -ne "Valid" -or -not $signerMatches)
            {
                throw "The production package signer does not match the selected trusted certificate."
            }
            $signatureTrustStatus = "Valid"
            $timestampVerified = $true
        }
        else
        {
            $verifyOutput = @(& $signTool verify /pa /all /sha1 $certificate.Thumbprint /v /debug $msixPath 2>&1)
            $verifyExitCode = $LASTEXITCODE
            $verifyText = $verifyOutput -join [Environment]::NewLine
            $winTrustResult = [IsTranscribe.Release.WinTrustVerifier]::Verify($msixPath)
            $winTrustCode = '0x{0:X8}' -f $winTrustResult
            if ($verifyExitCode -eq 0)
            {
                $signatureTrustStatus = "Valid"
            }
            elseif ($winTrustCode -eq '0x800B0109' -and $signerMatches)
            {
                $signatureTrustStatus = "DevelopmentUntrustedRoot"
            }
            else
            {
                $verifyOutput | ForEach-Object { Write-Host $_ }
                throw "Development MSIX signature verification failed with exit code $verifyExitCode."
            }
            $timestampVerified = $false
        }
    }

    $msixItem = Get-Item -LiteralPath $msixPath
    $msixHash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $inventoryHash = (Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $developmentCertificateHash = if ($null -ne $developmentCertificatePath)
    {
        (Get-FileHash -LiteralPath $developmentCertificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    else
    {
        $null
    }
    $storeIdentityFileName = if ($SigningMode -eq "Store") { "store-identity.json" } else { $null }
    $storeIdentityOutputPath = if ($null -ne $storeIdentityFileName)
    {
        Join-Path $candidateRoot $storeIdentityFileName
    }
    else
    {
        $null
    }
    $storeIdentityHash = if ($null -ne $storeIdentityOutputPath)
    {
        (Get-FileHash -LiteralPath $storeIdentityOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    else
    {
        $null
    }
    if ($SigningMode -eq "Store" -and
        -not [string]::Equals($storeIdentityHash, $storeIdentityResult.Sha256, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Store identity snapshot does not match the validated release configuration."
    }
    $dotnetSdkVersion = (& dotnet --version).Trim()
    $manifest = [ordered]@{
        schemaVersion = "infra-005-release-v3"
        product = "isTranscribe"
        semanticVersion = $Version
        msixVersion = $versionInfo.PackageVersion
        runtimeIdentifier = $RuntimeIdentifier
        architecture = "x64"
        configuration = $Configuration
        build = [ordered]@{
            createdUtc = $buildUtc
            sourceRevision = $SourceRevision
            dotnetSdkVersion = $dotnetSdkVersion
            dotnetRuntimePackVersion = $DotNetRuntimePackVersion
            windowsSdkBuildToolsVersion = $WindowsSdkBuildToolsVersion
            selfContained = $true
            trimmed = $false
        }
        package = [ordered]@{
            identityName = $PackageName
            publisher = $Publisher
            publisherDisplayName = $PublisherDisplayName
            applicationId = $ApplicationId
            file = $msixFileName
            sizeBytes = $msixItem.Length
            sha256 = $msixHash
        }
        signing = [ordered]@{
            mode = $SigningMode.ToLowerInvariant()
            publicEligible = $SigningMode -eq "Production" -and $timestampVerified
            certificateSubject = if ($SigningMode -eq "Store") { $null } else { $certificate.Subject }
            certificateThumbprint = if ($SigningMode -eq "Store") { $null } else { $certificate.Thumbprint.ToLowerInvariant() }
            certificateNotAfterUtc = if ($SigningMode -eq "Store") { $null } else { $certificate.NotAfter.ToUniversalTime().ToString("O") }
            timestampRequested = -not [string]::IsNullOrWhiteSpace($TimestampUrl)
            timestampVerified = $timestampVerified
            trustStatus = $signatureTrustStatus
            verified = $true
            storeSigningRequired = $SigningMode -eq "Store"
            submissionEligible = $SigningMode -eq "Store"
        }
        audioCodec = [ordered]@{
            primaryExtension = ".mp3"
            container = "MP3"
            codec = "MPEG-1 Layer III"
            implementation = "Windows Media Foundation"
            managedWrapper = "NAudio"
            sampleRateHz = 48000
            channels = 2
            bitsPerSample = 16
            bitRate = 128000
            bundledCodecBinaryCount = 0
            patentProgramStatus = "Fraunhofer MP3 licensing program terminated 2017-04-23"
            evidenceUrls = @(
                "https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder",
                "https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456",
                "https://www.iis.fraunhofer.de/en/ff/amm/consumer-electronics/mp3.html",
                "https://www.audioblog.iis.fraunhofer.com/mp3-software-patents-licenses"
            )
        }
        supplyChain = [ordered]@{
            inventoryFile = "dependency-license-inventory.json"
            inventorySha256 = $inventoryHash
            dependencyCount = $dependencyCount
            redistributedDependencyCount = $redistributedDependencyCount
            checksumFile = "SHA256SUMS.txt"
            thirdPartyNoticeFile = [string]$noticeResult.noticeFile
            thirdPartyNoticeSha256 = [string]$noticeResult.noticeSha256
            thirdPartyNoticeSizeBytes = [long]$noticeResult.noticeSizeBytes
            thirdPartyNoticePolicyFile = [string]$noticeResult.policyFile
            thirdPartyNoticePolicySha256 = [string]$noticeResult.policySha256
            identityPolicySchemaVersion = $releaseIdentityPolicy.schemaVersion
            identityPolicySha256 = $releaseIdentityPolicyHash
            developmentCertificateFile = $developmentCertificateFileName
            developmentCertificateSha256 = $developmentCertificateHash
            storeIdentityFile = $storeIdentityFileName
            storeIdentitySha256 = $storeIdentityHash
        }
        dataPersistence = [ordered]@{
            localAppDataRoot = [ordered]@{
                knownFolder = "LocalApplicationData"
                relativePath = "isTranscribe"
                resolvedAtRuntimeBy = "Environment.SpecialFolder.LocalApplicationData"
            }
            documentsRoot = [ordered]@{
                knownFolder = "MyDocuments"
                relativePath = "isTranscribe"
                resolvedAtRuntimeBy = "Environment.SpecialFolder.MyDocuments"
            }
            recordingsRoot = [ordered]@{
                source = "settings.recordingsFolder"
                defaultRelativePath = "Recordings"
                customPathPreserved = $true
            }
            fileSystemWriteVirtualization = if ($SigningMode -eq "Store") { "fullTrustPassThrough" } else { "disabled" }
            registryWriteVirtualization = if ($SigningMode -eq "Store") { "fullTrustPassThrough" } else { "disabled" }
            restrictedUnvirtualizedResourcesDeclared = $SigningMode -ne "Store"
        }
    }

    $releaseManifestPath = Join-Path $candidateRoot "release-manifest.json"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestPath -Encoding utf8
    $releaseManifestHash = (Get-FileHash -LiteralPath $releaseManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    @(
        "$msixHash  $msixFileName",
        "$inventoryHash  dependency-license-inventory.json",
        "$releaseManifestHash  release-manifest.json"
    ) | Set-Content -LiteralPath (Join-Path $candidateRoot "SHA256SUMS.txt") -Encoding ascii

    $developmentMarker = Join-Path $candidateRoot "DEVELOPMENT_ONLY.txt"
    if (Test-Path -LiteralPath $developmentMarker)
    {
        $markerHash = (Get-FileHash -LiteralPath $developmentMarker -Algorithm SHA256).Hash.ToLowerInvariant()
        "$markerHash  DEVELOPMENT_ONLY.txt" | Add-Content -LiteralPath (Join-Path $candidateRoot "SHA256SUMS.txt") -Encoding ascii
    }
    if ($null -ne $developmentCertificatePath)
    {
        "$developmentCertificateHash  $developmentCertificateFileName" |
            Add-Content -LiteralPath (Join-Path $candidateRoot "SHA256SUMS.txt") -Encoding ascii
    }
    $storeSubmissionMarker = Join-Path $candidateRoot "STORE_SUBMISSION_ONLY.txt"
    if (Test-Path -LiteralPath $storeSubmissionMarker)
    {
        $storeMarkerHash = (Get-FileHash -LiteralPath $storeSubmissionMarker -Algorithm SHA256).Hash.ToLowerInvariant()
        "$storeMarkerHash  STORE_SUBMISSION_ONLY.txt" |
            Add-Content -LiteralPath (Join-Path $candidateRoot "SHA256SUMS.txt") -Encoding ascii
    }
    if ($null -ne $storeIdentityOutputPath)
    {
        "$storeIdentityHash  $storeIdentityFileName" |
            Add-Content -LiteralPath (Join-Path $candidateRoot "SHA256SUMS.txt") -Encoding ascii
    }

    $backupRoot = Join-Path $channelRoot ".$safeVersion.previous-$([Guid]::NewGuid().ToString('N'))"
    $hadPreviousRelease = Test-Path -LiteralPath $releaseRoot
    if ($hadPreviousRelease)
    {
        Move-Item -LiteralPath $releaseRoot -Destination $backupRoot
    }
    try
    {
        Move-Item -LiteralPath $candidateRoot -Destination $releaseRoot
    }
    catch
    {
        if ($hadPreviousRelease -and -not (Test-Path -LiteralPath $releaseRoot) -and (Test-Path -LiteralPath $backupRoot))
        {
            Move-Item -LiteralPath $backupRoot -Destination $releaseRoot
        }
        throw
    }
    if (Test-Path -LiteralPath $backupRoot)
    {
        Remove-SafeDirectory -Path $backupRoot -ParentPath $channelRoot
    }

    Write-Host "Windows release created: $(Join-Path $releaseRoot $msixFileName)"
    Write-Host "Release manifest: $(Join-Path $releaseRoot 'release-manifest.json')"
    Write-Host "Dependency/license inventory: $(Join-Path $releaseRoot 'dependency-license-inventory.json')"
}
finally
{
    $releaseLock.Dispose()
    try
    {
        if (Test-Path -LiteralPath $stagingRoot)
        {
            Remove-SafeDirectory -Path $stagingRoot -ParentPath $stagingParent
        }
    }
    finally
    {
        $certificateCleanupErrors = [System.Collections.Generic.List[object]]::new()
        foreach ($thumbprint in $temporaryCertificateThumbprints)
        {
            try
            {
                $certificatePath = "Cert:\CurrentUser\My\$thumbprint"
                if (Test-Path -LiteralPath $certificatePath)
                {
                    Remove-Item -LiteralPath $certificatePath -Force
                }
            }
            catch
            {
                $certificateCleanupErrors.Add($_)
            }
        }
        if ($certificateCleanupErrors.Count -gt 0)
        {
            throw "One or more temporary signing certificates could not be removed."
        }
    }
}
