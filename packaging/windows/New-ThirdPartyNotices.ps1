#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseInventoryPath,
    [Parameter(Mandatory)][string]$PublishedDepsPath,
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$InventoryOutputPath,
    [Parameter(Mandatory)][string]$NoticeOutputPath,
    [Parameter(Mandatory)][string]$ResultOutputPath,
    [string]$PolicyPath = (Join-Path $PSScriptRoot "notices\third-party-notice-policy.json"),
    [string]$NativeRuntimeInventoryPath,
    [string]$NativeRuntimeManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#decisions.policy
# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#decisions.output

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256
{
    param([AllowEmptyString()][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
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

function Get-PayloadTreeSha256
{
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowEmptyCollection()][string[]]$Assets)

    $builder = [Text.StringBuilder]::new()
    foreach ($asset in @(Get-OrdinalSortedUnique -Values $Assets))
    {
        $path = Join-Path $Root $asset
        if (-not (Test-Path -LiteralPath $path -PathType Leaf))
        {
            throw "Attributed published asset '$asset' is missing."
        }
        [void]$builder.Append($asset).Append("`n").Append((Get-LowerSha256 -Path $path)).Append("`n")
    }
    Get-TextSha256 -Text $builder.ToString()
}

function Get-PackageContentSha512
{
    param(
        [Parameter(Mandatory)][string]$GlobalPackagesRoot,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version)

    $root = Join-Path (Join-Path $GlobalPackagesRoot $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
    $archives = @(Get-ChildItem -LiteralPath $root -Filter '*.nupkg' -File)
    if ($archives.Count -ne 1) { throw "Exact NuGet archive is missing for '$Id/$Version'." }
    $stream = [IO.File]::OpenRead($archives[0].FullName)
    try { [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream)) }
    finally { $stream.Dispose() }
}

function Get-NormalizedText
{
    param([Parameter(Mandatory)][string]$Path)
    ((Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd("`n")
}

function Write-DeterministicUtf8
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent))
    {
        [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($parent)) | Out-Null
    }
    $normalized = ($Text -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd("`n") + "`n"
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($Path),
        $normalized,
        [Text.UTF8Encoding]::new($false))
}

function Get-SafePackageFile
{
    param(
        [Parameter(Mandatory)][string]$GlobalPackagesRoot,
        [Parameter(Mandatory)][string]$PackageIdentity,
        [Parameter(Mandatory)][string]$RelativePath)

    $separator = $PackageIdentity.LastIndexOf('/')
    if ($separator -le 0 -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($RelativePath))
    {
        throw "Unsafe package source declaration '$PackageIdentity/$RelativePath'."
    }
    $id = $PackageIdentity.Substring(0, $separator).ToLowerInvariant()
    $version = $PackageIdentity.Substring($separator + 1).ToLowerInvariant()
    $root = [IO.Path]::GetFullPath((Join-Path (Join-Path $GlobalPackagesRoot $id) $version))
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Package source '$PackageIdentity/$RelativePath' is missing or escapes its package root."
    }
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Package source '$PackageIdentity/$RelativePath' is a reparse point."
    }
    $path
}

function Get-PublishedAssetName
{
    param(
        [Parameter(Mandatory)][string]$AssetPath,
        [switch]$Resource)

    $normalized = $AssetPath.Replace('\', '/')
    if ($Resource)
    {
        $parts = @($normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
        if ($parts.Count -lt 2) { throw "Resource asset '$AssetPath' has no culture directory." }
        return "$($parts[-2])/$($parts[-1])"
    }
    [IO.Path]::GetFileName($normalized)
}

function Get-AssetListHash
{
    param([string[]]$Assets)
    $joined = (@(Get-OrdinalSortedUnique -Values $Assets) -join "`n")
    Get-TextSha256 -Text $joined
}

function Read-NuspecMetadata
{
    param(
        [Parameter(Mandatory)][string]$GlobalPackagesRoot,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version)

    $root = Join-Path (Join-Path $GlobalPackagesRoot $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
    $nuspec = Get-ChildItem -LiteralPath $root -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec) { throw "NuSpec is missing for '$Id/$Version'." }
    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $document.package.metadata
    $license = $metadata.SelectSingleNode("./*[local-name()='license']")
    $copyright = $metadata.SelectSingleNode("./*[local-name()='copyright']")
    $authors = $metadata.SelectSingleNode("./*[local-name()='authors']")
    $acceptance = $metadata.SelectSingleNode("./*[local-name()='requireLicenseAcceptance']")
    $repository = $metadata.SelectSingleNode("./*[local-name()='repository']")
    [ordered]@{
        licenseType = if ($null -ne $license) { $license.GetAttribute('type') } else { $null }
        licenseValue = if ($null -ne $license) { $license.InnerText.Trim() } else { $null }
        copyright = if ($null -ne $copyright) { $copyright.InnerText.Trim() } else { $null }
        authors = if ($null -ne $authors) { $authors.InnerText.Trim() } else { $null }
        requireLicenseAcceptance = $null -ne $acceptance -and
            [string]::Equals($acceptance.InnerText.Trim(), 'true', [StringComparison]::OrdinalIgnoreCase)
        repository = [ordered]@{
            url = if ($null -ne $repository) { $repository.GetAttribute('url') } else { $null }
            commit = if ($null -ne $repository) { $repository.GetAttribute('commit') } else { $null }
        }
    }
}

foreach ($requiredPath in @($BaseInventoryPath, $PublishedDepsPath, $PolicyPath))
{
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Required input '$requiredPath' is missing." }
}
if ([string]::IsNullOrWhiteSpace($NativeRuntimeInventoryPath) -ne
    [string]::IsNullOrWhiteSpace($NativeRuntimeManifestPath))
{
    throw "Native runtime inventory and manifest must be supplied together."
}
$publishRootFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PublishRoot).Path)
$baseInventory = Get-Content -LiteralPath $BaseInventoryPath -Raw | ConvertFrom-Json
$deps = Get-Content -LiteralPath $PublishedDepsPath -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
if ([string]$policy.schemaVersion -cne 'infra-005-third-party-notices-v1') { throw "Unsupported notice policy schema." }
if ([string]$baseInventory.schemaVersion -cne 'infra-005-dependencies-v1') { throw "Base inventory schema is not v1." }
if ([string]$policy.assetListHashAlgorithm -cne 'sha256-utf8-lf-joined-published-relative-paths' -or
    [string]$policy.payloadTreeHashAlgorithm -cne 'sha256-utf8-ordinal-path-lf-sha256-lf')
{
    throw "Unsupported notice policy hash algorithm."
}

$assetsPath = Join-Path (Split-Path -Parent $PublishedDepsPath) "..\..\..\..\obj\project.assets.json"
$desktopAssetsCandidates = @(
    (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PublishedDepsPath)))) "obj\project.assets.json"),
    (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))) "src\IsTranscribe.App.Windows\obj\project.assets.json"))
$desktopAssetsPath = $desktopAssetsCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ($null -eq $desktopAssetsPath)
{
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
    $desktopAssetsPath = Join-Path $repoRoot "src\IsTranscribe.App.Windows\obj\project.assets.json"
}
if (-not (Test-Path -LiteralPath $desktopAssetsPath -PathType Leaf)) { throw "Desktop project.assets.json is missing." }
$desktopAssets = Get-Content -LiteralPath $desktopAssetsPath -Raw | ConvertFrom-Json
$globalPackagesRoot = ($desktopAssets.packageFolders.PSObject.Properties | Select-Object -First 1).Name

$policyPackages = @($policy.packages)
if ($policyPackages.Count -ne [int]$policy.expectedPackageCount) { throw "Notice policy package count is inconsistent." }
if (@($policy.archiveSha512.PSObject.Properties).Count -ne $policyPackages.Count)
{
    throw "Notice policy archive hash count is inconsistent."
}
$policyByIdentity = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $policyPackages)
{
    $identity = "$($package.id)/$($package.version)"
    if (-not $policyByIdentity.TryAdd($identity, $package)) { throw "Duplicate policy package '$identity'." }
}
$baseDependencies = @($baseInventory.dependencies)
if ($baseDependencies.Count -ne $policyPackages.Count) { throw "Base inventory and notice policy package counts differ." }
foreach ($dependency in $baseDependencies)
{
    $identity = "$($dependency.id)/$($dependency.version)"
    if (-not $policyByIdentity.ContainsKey($identity)) { throw "Dependency '$identity' is absent from the exact notice policy." }
    $entry = $policyByIdentity[$identity]
    if ([string]$dependency.contentHashSha512 -cne [string]$entry.contentHashSha512)
    {
        throw "Locked SHA-512 changed for '$identity'."
    }
    $physicalHash = Get-PackageContentSha512 -GlobalPackagesRoot $globalPackagesRoot `
        -Id ([string]$dependency.id) -Version ([string]$dependency.version)
    $archiveHashProperty = $policy.archiveSha512.PSObject.Properties[$identity]
    if ($null -eq $archiveHashProperty -or $physicalHash -cne [string]$archiveHashProperty.Value)
    {
        throw "Physical NuGet archive changed for '$identity'."
    }
}

$target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name]
if ($null -eq $target) { throw "Published deps has no exact runtime target '$($deps.runtimeTarget.name)'." }
$actualAssets = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($libraryProperty in $target.Value.PSObject.Properties)
{
    $rawIdentity = $libraryProperty.Name
    $libraryMetadata = $deps.libraries.PSObject.Properties[$rawIdentity]
    if ($null -eq $libraryMetadata -or [string]$libraryMetadata.Value.type -notin @('package', 'runtimepack')) { continue }
    $separator = $rawIdentity.LastIndexOf('/')
    if ($separator -le 0) { continue }
    $rawId = $rawIdentity.Substring(0, $separator)
    $version = $rawIdentity.Substring($separator + 1)
    if ($rawId.StartsWith('IsTranscribe.', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $id = if ($rawId.StartsWith('runtimepack.', [StringComparison]::OrdinalIgnoreCase))
    {
        $rawId.Substring('runtimepack.'.Length)
    }
    else { $rawId }
    $identity = "$id/$version"
    $assets = [Collections.Generic.List[string]]::new()
    foreach ($group in @('runtime', 'native'))
    {
        $node = $libraryProperty.Value.PSObject.Properties[$group]
        if ($null -eq $node) { continue }
        foreach ($asset in $node.Value.PSObject.Properties.Name)
        {
            $published = Get-PublishedAssetName -AssetPath $asset
            if ([IO.Path]::GetExtension($published) -in @('.pdb', '.xml')) { continue }
            if (-not $assets.Contains($published)) { $assets.Add($published) }
        }
    }
    $resourcesNode = $libraryProperty.Value.PSObject.Properties['resources']
    if ($null -ne $resourcesNode)
    {
        foreach ($asset in $resourcesNode.Value.PSObject.Properties.Name)
        {
            $published = Get-PublishedAssetName -AssetPath $asset -Resource
            if (-not $assets.Contains($published)) { $assets.Add($published) }
        }
    }
    $actualAssets[$identity] = $assets
}

$sdkOverride = @($policy.overrides | Where-Object package -EQ 'Microsoft.Windows.SDK.NET.Ref/10.0.26100.84') | Select-Object -First 1
if ($null -eq $sdkOverride) { throw "Windows SDK exact redistribution override is missing." }
$sdkAssets = [Collections.Generic.List[string]]::new()
foreach ($reference in @($sdkOverride.privateReferences))
{
    $path = Join-Path $publishRootFull $reference.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-LowerSha256 -Path $path) -cne [string]$reference.sha256)
    {
        throw "Windows SDK private reference '$($reference.path)' is missing or changed."
    }
    $sdkAssets.Add([string]$reference.path)
}
$actualAssets['Microsoft.Windows.SDK.NET.Ref/10.0.26100.84'] = $sdkAssets

$redistributedCount = 0
foreach ($package in $policyPackages)
{
    $identity = "$($package.id)/$($package.version)"
    $assets = @(if ($actualAssets.ContainsKey($identity)) { @($actualAssets[$identity]) })
    $hash = Get-AssetListHash -Assets $assets
    if ($hash -cne [string]$package.assetListSha256)
    {
        throw "Published asset mapping changed for '$identity' (actual: $hash)."
    }
    $payloadTreeHash = Get-PayloadTreeSha256 -Root $publishRootFull -Assets $assets
    if ($payloadTreeHash -cne [string]$package.payloadTreeSha256)
    {
        throw "Published payload bytes changed for '$identity' (actual: $payloadTreeHash)."
    }
    if ([bool]$package.redistributed -ne ($assets.Count -gt 0)) { throw "Redistribution flag is stale for '$identity'." }
    if ([bool]$package.redistributed)
    {
        $redistributedCount++
        foreach ($asset in $assets)
        {
            if (-not (Test-Path -LiteralPath (Join-Path $publishRootFull $asset) -PathType Leaf))
            {
                throw "Attributed published asset '$asset' for '$identity' is missing."
            }
        }
    }
}
if ($redistributedCount -ne [int]$policy.expectedRedistributedPackageCount) { throw "Redistributed package count changed." }
foreach ($identity in $actualAssets.Keys)
{
    if (-not $policyByIdentity.ContainsKey($identity)) { throw "Published dependency '$identity' has no notice policy entry." }
}

$noticesRoot = Split-Path -Parent ([IO.Path]::GetFullPath($PolicyPath))
$canonicalTexts = [ordered]@{}
$canonicalNames = Get-OrdinalSortedUnique -Values @($policy.canonicalLicenses.PSObject.Properties.Name)
foreach ($licenseName in $canonicalNames)
{
    $licenseProperty = $policy.canonicalLicenses.PSObject.Properties[$licenseName]
    $path = Join-Path $noticesRoot $licenseProperty.Value.path
    if ((Get-LowerSha256 -Path $path) -cne [string]$licenseProperty.Value.sha256) { throw "Canonical license '$($licenseProperty.Name)' changed." }
    $canonicalTexts[$licenseProperty.Name] = Get-NormalizedText -Path $path
}

$verbatimByPackage = @{}
$verbatimSections = [ordered]@{}
$verbatimKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($source in @($policy.verbatimSources))
{
    $sourceIdentity = [string]$source.package
    if (-not $policyByIdentity.ContainsKey($sourceIdentity) -or -not [bool]$policyByIdentity[$sourceIdentity].redistributed)
    {
        throw "Verbatim source references unknown or excluded package '$sourceIdentity'."
    }
    if ([string]$source.kind -notin @('license', 'notice')) { throw "Invalid verbatim source kind for '$sourceIdentity'." }
    $sourceKey = "$sourceIdentity|$($source.kind)|$($source.path)"
    if (-not $verbatimKeys.Add($sourceKey)) { throw "Duplicate verbatim source '$sourceKey'." }
    $path = Get-SafePackageFile -GlobalPackagesRoot $globalPackagesRoot -PackageIdentity $source.package -RelativePath $source.path
    $sha = Get-LowerSha256 -Path $path
    if ($sha -cne [string]$source.sha256) { throw "Verbatim source changed for '$($source.package)/$($source.path)'." }
    if (-not $verbatimByPackage.ContainsKey([string]$source.package)) { $verbatimByPackage[[string]$source.package] = @() }
    $verbatimByPackage[[string]$source.package] += [ordered]@{ kind = [string]$source.kind; path = [string]$source.path; sha256 = $sha }
    if (-not $verbatimSections.Contains($sha)) { $verbatimSections[$sha] = Get-NormalizedText -Path $path }
}

$overridesByPackage = @{}
$overrideTexts = [ordered]@{}
foreach ($override in @($policy.overrides))
{
    $overrideIdentity = [string]$override.package
    if (-not $policyByIdentity.ContainsKey($overrideIdentity) -or -not [bool]$policyByIdentity[$overrideIdentity].redistributed)
    {
        throw "Override references unknown or excluded package '$overrideIdentity'."
    }
    if ($overridesByPackage.ContainsKey($overrideIdentity)) { throw "Duplicate override for '$overrideIdentity'." }
    $path = [IO.Path]::GetFullPath((Join-Path $noticesRoot $override.path))
    if (-not $path.StartsWith($noticesRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-LowerSha256 -Path $path) -cne [string]$override.sha256)
    {
        throw "Notice override changed for '$($override.package)'."
    }
    $overridesByPackage[[string]$override.package] = $override
    $overrideTexts[[string]$override.package] = Get-NormalizedText -Path $path
}

$interOverride = $overridesByPackage['Avalonia.Fonts.Inter/12.1.0']
if ($null -eq $interOverride -or
    (Get-LowerSha256 -Path (Join-Path $publishRootFull 'Avalonia.Fonts.Inter.dll')) -cne [string]$interOverride.payloadSha256)
{
    throw "Inter font payload changed from its reviewed OFL override."
}

foreach ($package in $policyPackages | Where-Object redistributed)
{
    $identity = "$($package.id)/$($package.version)"
    $packageRoot = Join-Path (Join-Path $globalPackagesRoot $package.id.ToLowerInvariant()) $package.version.ToLowerInvariant()
    foreach ($candidate in @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(?i)(notice|third[-_. ]?party)' }))
    {
        $declared = @($policy.verbatimSources | Where-Object {
            [string]$_.package -ieq $identity -and [string]$_.path -ieq $candidate.Name
        })
        if ($declared.Count -ne 1) { throw "Package notice '$identity/$($candidate.Name)' is not declared exactly once." }
    }
}

$noticeDependencies = [Collections.Generic.List[object]]::new()
$packageLines = [Collections.Generic.List[string]]::new()
$baseByIdentity = @{}
foreach ($dependency in $baseDependencies) { $baseByIdentity["$($dependency.id)/$($dependency.version)"] = $dependency }
foreach ($identity in @(Get-OrdinalSortedUnique -Values @($baseByIdentity.Keys)))
{
    $dependency = $baseByIdentity[$identity]
    $policyEntry = $policyByIdentity[$identity]
    $assets = @(if ($actualAssets.ContainsKey($identity)) { @(Get-OrdinalSortedUnique -Values @($actualAssets[$identity])) })
    $nuspec = Read-NuspecMetadata -GlobalPackagesRoot $globalPackagesRoot -Id $dependency.id -Version $dependency.version
    $materials = [Collections.Generic.List[string]]::new()
    $strategy = 'excluded-no-payload'
    if ([bool]$policyEntry.redistributed)
    {
        $strategy = if ($nuspec.licenseType -eq 'expression') { "canonical:$($nuspec.licenseValue)" }
        elseif ($nuspec.licenseType -eq 'file') { "verbatim-license:$($nuspec.licenseValue)" }
        elseif ($overridesByPackage.ContainsKey($identity)) { 'reviewed-override' }
        else { throw "Redistributed dependency '$identity' has no offline license strategy." }
        if ($nuspec.licenseType -eq 'file')
        {
            $matchingLicenseSources = @($policy.verbatimSources | Where-Object {
                [string]$_.package -ieq $identity -and [string]$_.kind -ceq 'license' -and
                [string]$_.path -ceq [string]$nuspec.licenseValue
            })
            if ($matchingLicenseSources.Count -ne 1)
            {
                throw "File-license dependency '$identity' lacks one exact pinned license source."
            }
        }
        if ($nuspec.licenseType -eq 'expression')
        {
            if (-not $canonicalTexts.Contains($nuspec.licenseValue)) { throw "Unknown license expression '$($nuspec.licenseValue)' for '$identity'." }
            $materials.Add("license:$($nuspec.licenseValue)")
        }
        if ($verbatimByPackage.ContainsKey($identity))
        {
            foreach ($source in @($verbatimByPackage[$identity])) { $materials.Add("$($source.kind):$($source.sha256)") }
        }
        if ($overridesByPackage.ContainsKey($identity))
        {
            $materials.Add("override:$([string]$overridesByPackage[$identity].sha256)")
            $additionalProperty = $overridesByPackage[$identity].PSObject.Properties['additionalLicense']
            $additional = if ($null -ne $additionalProperty) { [string]$additionalProperty.Value } else { [string]::Empty }
            if (-not [string]::IsNullOrWhiteSpace($additional))
            {
                if (-not $canonicalTexts.Contains($additional)) { throw "Override for '$identity' names unknown license '$additional'." }
                $materials.Add("license:$additional")
            }
        }
        $attribution = if (-not [string]::IsNullOrWhiteSpace([string]$nuspec.copyright)) { [string]$nuspec.copyright } else { [string]$nuspec.authors }
        $packageLines.Add("- $identity | $attribution | $strategy | assets: $($assets -join ', ') | material: $($materials -join ', ')")
    }
    $noticeDependencies.Add([ordered]@{
        id = [string]$dependency.id
        version = [string]$dependency.version
        direct = [bool]$dependency.direct
        acquisition = [string]$dependency.acquisition
        contentHashSha512 = [string]$dependency.contentHashSha512
        license = $dependency.license
        redistributed = [bool]$policyEntry.redistributed
        redistributedAssets = $assets
        copyright = $nuspec.copyright
        authors = $nuspec.authors
        requireLicenseAcceptance = [bool]$nuspec.requireLicenseAcceptance
        repository = $nuspec.repository
        notice = [ordered]@{
            strategy = $strategy
            material = @($materials)
            assetListSha256 = [string]$policyEntry.assetListSha256
            payloadTreeSha256 = [string]$policyEntry.payloadTreeSha256
        }
    })
}

$sections = [Collections.Generic.List[string]]::new()
$sections.Add("isTranscribe third-party notices`n`nThis file is generated deterministically from exact locked package, payload and notice policy evidence.`n`nPackage attribution index`n$($packageLines -join "`n")")
if (-not [string]::IsNullOrWhiteSpace($NativeRuntimeInventoryPath))
{
    # @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
    # @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
    $nativeInventoryPath = [IO.Path]::GetFullPath($NativeRuntimeInventoryPath)
    $nativeManifestPath = [IO.Path]::GetFullPath($NativeRuntimeManifestPath)
    foreach ($requiredNativePath in @($nativeInventoryPath, $nativeManifestPath))
    {
        if (-not (Test-Path -LiteralPath $requiredNativePath -PathType Leaf))
        {
            throw "Native runtime notice input '$requiredNativePath' is missing."
        }
    }
    $nativeInventory = Get-Content -LiteralPath $nativeInventoryPath -Raw -Encoding utf8 | ConvertFrom-Json
    $nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    $nativeManifestSha256 = Get-LowerSha256 -Path $nativeManifestPath
    if ([string]$nativeInventory.schemaVersion -cne 'istranscribe-local-runtime-package-v1' -or
        [string]$nativeInventory.rid -cne 'win-x64' -or
        [string]$nativeManifest.schemaVersion -cne 'istranscribe-whisper-native-v1' -or
        [string]$nativeInventory.native.runtimeManifestSha256 -cne $nativeManifestSha256 -or
        [string]$nativeInventory.native.sourceCommit -cne [string]$nativeManifest.source.commit -or
        [string]$nativeInventory.native.sourceArchiveSha256 -cne [string]$nativeManifest.source.archive.sha256)
    {
        throw "Native runtime inventory does not match the pinned manifest."
    }
    $packagedManifestPath = [IO.Path]::GetFullPath(
        (Join-Path $publishRootFull ([string]$nativeInventory.native.runtimeManifestPath)))
    if (-not $packagedManifestPath.StartsWith(
            $publishRootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $packagedManifestPath -PathType Leaf) -or
        (Get-LowerSha256 -Path $packagedManifestPath) -cne $nativeManifestSha256)
    {
        throw "Packaged native runtime manifest is missing or changed."
    }

    $nativeIndexLines = [Collections.Generic.List[string]]::new()
    foreach ($library in @($nativeInventory.native.libraries))
    {
        $relativePath = [string]$library.path
        $libraryPath = [IO.Path]::GetFullPath((Join-Path $publishRootFull $relativePath))
        if (-not $libraryPath.StartsWith(
                $publishRootFull + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $libraryPath -PathType Leaf) -or
            (Get-LowerSha256 -Path $libraryPath) -cne [string]$library.sha256)
        {
            throw "Native runtime library '$relativePath' is missing or changed."
        }
        $nativeIndexLines.Add(
            "- $($nativeManifest.runtime.id) $($nativeManifest.runtime.version) | " +
            "$($nativeInventory.rid)/$($library.variant) | $relativePath | SHA-256 $($library.sha256)")
    }
    if ($nativeIndexLines.Count -lt 1)
    {
        throw "Native runtime inventory contains no packaged library."
    }
    $sections.Add(
        "Local transcription native runtime attribution index`n" +
        "Source commit: $($nativeManifest.source.commit)`n" +
        ($nativeIndexLines -join "`n"))

    $manifestRoot = [IO.Path]::GetFullPath((Split-Path -Parent $nativeManifestPath))
    $receiptLicenses = @($nativeInventory.native.licenses)
    $manifestLicenses = @($nativeManifest.licenses)
    if ($receiptLicenses.Count -ne $manifestLicenses.Count -or $manifestLicenses.Count -lt 1)
    {
        throw "Native runtime license inventory differs from the manifest."
    }
    foreach ($license in $manifestLicenses)
    {
        $receiptMatches = @($receiptLicenses | Where-Object {
            [string]$_.component -ceq [string]$license.component -and
            [string]$_.spdx -ceq [string]$license.spdx -and
            [string]$_.checkedInPath -ceq [string]$license.checkedInPath -and
            [string]$_.sha256 -ceq [string]$license.sha256
        })
        if ($receiptMatches.Count -ne 1)
        {
            throw "Native license receipt for '$($license.component)' differs from the manifest."
        }
        $licensePath = [IO.Path]::GetFullPath((Join-Path $manifestRoot ([string]$license.checkedInPath)))
        if (-not $licensePath.StartsWith(
                $manifestRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or
            (Get-LowerSha256 -Path $licensePath) -cne [string]$license.sha256)
        {
            throw "Native license material for '$($license.component)' is missing or changed."
        }
        $sections.Add(
            "Native license: $($license.component) ($($license.spdx), SHA-256 $($license.sha256))`n`n" +
            (Get-NormalizedText -Path $licensePath))
    }
}
$usedCanonical = @(Get-OrdinalSortedUnique -Values @($noticeDependencies.notice.material | ForEach-Object { $_ } | Where-Object { $_ -like 'license:*' } | ForEach-Object { $_.Substring('license:'.Length) }))
foreach ($license in $usedCanonical)
{
    $sections.Add("Canonical license: $license`n`n$($canonicalTexts[$license])")
}
foreach ($identity in @(Get-OrdinalSortedUnique -Values @($overrideTexts.Keys)))
{
    $sections.Add("Reviewed exact-package supplement: $identity`n`n$($overrideTexts[$identity])")
}
foreach ($sha in @(Get-OrdinalSortedUnique -Values @($verbatimSections.Keys)))
{
    $sections.Add("Verbatim package license or notice (SHA-256 $sha)`n`n$($verbatimSections[$sha])")
}
$noticeText = $sections -join "`n`n================================================================================`n`n"
Write-DeterministicUtf8 -Path $NoticeOutputPath -Text $noticeText
$noticeHash = Get-LowerSha256 -Path $NoticeOutputPath
$policyHash = Get-LowerSha256 -Path $PolicyPath

$missingLicenseMetadata = @($noticeDependencies | Where-Object {
    [string]::IsNullOrWhiteSpace([string]$_.license.expression) -and
    [string]::IsNullOrWhiteSpace([string]$_.license.file) -and
    [string]::IsNullOrWhiteSpace([string]$_.license.url)
})
$inventory = [ordered]@{
    schemaVersion = 'infra-005-dependencies-v2'
    product = [string]$baseInventory.product
    semanticVersion = [string]$baseInventory.semanticVersion
    generatedUtc = [string]$baseInventory.generatedUtc
    packageCount = $noticeDependencies.Count
    redistributedPackageCount = $redistributedCount
    missingLicenseMetadataCount = $missingLicenseMetadata.Count
    notice = [ordered]@{
        file = [IO.Path]::GetFileName($NoticeOutputPath)
        sha256 = $noticeHash
        sizeBytes = (Get-Item -LiteralPath $NoticeOutputPath).Length
        policyFile = [IO.Path]::GetFileName($PolicyPath)
        policySha256 = $policyHash
    }
    dependencies = @($noticeDependencies)
}
$inventoryJson = $inventory | ConvertTo-Json -Depth 12
Write-DeterministicUtf8 -Path $InventoryOutputPath -Text $inventoryJson

$result = [ordered]@{
    schemaVersion = 'infra-005-third-party-notice-result-v1'
    packageCount = $noticeDependencies.Count
    redistributedPackageCount = $redistributedCount
    noticeFile = [IO.Path]::GetFileName($NoticeOutputPath)
    noticeSha256 = $noticeHash
    noticeSizeBytes = (Get-Item -LiteralPath $NoticeOutputPath).Length
    policyFile = [IO.Path]::GetFileName($PolicyPath)
    policySha256 = $policyHash
    inventorySha256 = Get-LowerSha256 -Path $InventoryOutputPath
}
Write-DeterministicUtf8 -Path $ResultOutputPath -Text ($result | ConvertTo-Json -Depth 6)
$result | ConvertTo-Json -Depth 6 -Compress
