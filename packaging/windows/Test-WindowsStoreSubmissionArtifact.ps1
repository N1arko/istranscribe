#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [Alias("ReleaseDirectory")]
    [string]$StoreSubmissionDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
# @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#decisions.signing
# @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#decisions.identity
# @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#release-contract
# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#release-contract
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance
$ExpectedProduct = "isTranscribe"
$ExpectedReleaseSchema = "infra-005-release-v3"
$ExpectedStoreIdentitySchema = "infra-005-store-identity-v1"
$ExpectedInventorySchema = "infra-005-dependencies-v2"
$ExpectedRuntimeIdentifier = "win-x64"
$ExpectedArchitecture = "x64"
$ExpectedApplicationId = "App"
$ExpectedApplicationExecutable = "IsTranscribe.Desktop.exe"
$ExpectedMinimumWindowsVersion = "10.0.19041.0"
$ChecksumFileName = "SHA256SUMS.txt"
$ReleaseManifestFileName = "release-manifest.json"
$StoreIdentityFileName = "store-identity.json"
$DependencyInventoryFileName = "dependency-license-inventory.json"
$StoreMarkerFileName = "STORE_SUBMISSION_ONLY.txt"
$NoticePolicyPath = Join-Path $PSScriptRoot "notices\third-party-notice-policy.json"
$DevelopmentPublisher = "CN=isTranscribe Development"
$BlockSizeBytes = 65536
$MaximumJsonBytes = 16MB
$MaximumXmlBytes = 16MB
$MaximumEntryCount = 20000
$MaximumExpandedBytes = 2GB
$ExpectedLocalRuntimeManifestSha256 = "fe37a0d085edaab7925c25f8e62514fe714dda4c66cfe3f2c48e2bea191d2feb"
$LocalRuntimeInventoryPath = "native/runtime-inventory.v1.json"

$ExpectedMarkerLines = @(
    "STORE SUBMISSION ONLY",
    "This unsigned package must be submitted through Microsoft Partner Center.",
    "Do not distribute it directly to users.",
    "Microsoft Store signing is required before installation.")

$RequiredRuntimeFiles = @(
    "IsTranscribe.Desktop.exe",
    "IsTranscribe.Desktop.dll",
    "THIRD-PARTY-NOTICES.txt",
    "IsTranscribe.Desktop.deps.json",
    "IsTranscribe.Desktop.runtimeconfig.json",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll")

$ExpectedAssets = [ordered]@{
    "Assets/StoreLogo.png" = @(50, 50)
    "Assets/Square44x44Logo.png" = @(44, 44)
    "Assets/Square150x150Logo.png" = @(150, 150)
    "Assets/Wide310x150Logo.png" = @(310, 150)
    "Assets/Square310x310Logo.png" = @(310, 310)
}

function Assert-Condition
{
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message)

    if (-not $Condition) { throw $Message }
}

function Assert-TextEqual
{
    param(
        [AllowNull()][string]$Actual,
        [AllowNull()][string]$Expected,
        [Parameter(Mandatory)][string]$Field,
        [switch]$IgnoreCase)

    $comparison = if ($IgnoreCase) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($Actual, $Expected, $comparison))
    {
        throw "$Field must be '$Expected'; found '$Actual'."
    }
}

function Assert-JsonBoolean
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][bool]$Expected,
        [Parameter(Mandatory)][string]$Field)

    if ($Value -isnot [bool] -or [bool]$Value -ne $Expected)
    {
        throw "$Field must be the JSON boolean '$Expected'."
    }
}

function Assert-JsonInteger
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [long]$Minimum = 0)

    if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and
        $Value -isnot [int16] -and $Value -isnot [uint16] -and
        $Value -isnot [int32] -and $Value -isnot [uint32] -and
        $Value -isnot [int64] -and $Value -isnot [uint64])
    {
        throw "$Field must be a JSON integer."
    }
    if ([decimal]$Value -lt $Minimum) { throw "$Field must be at least $Minimum." }
}

function Assert-NonEmptyResolvedText
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [int]$MaximumLength = 512)

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Value))
    {
        throw "$Field must be a non-empty JSON string."
    }
    $text = [string]$Value
    if ($text.Length -gt $MaximumLength -or $text -cne $text.Trim() -or
        $text -match '[\x00-\x1F]' -or $text -match '(?i)(<[^>]+>|\{\{[^}]+\}\}|__[^_]+__|TODO|CHANGEME|REPLACE[_ -]?ME)')
    {
        throw "$Field contains surrounding whitespace, control characters, an unresolved placeholder or invalid length."
    }
}

function Assert-ExactJsonProperties
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Description)

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count)
    {
        throw "$Description must contain the exact schema property allowlist."
    }
    foreach ($name in $Expected)
    {
        if (@($actual | Where-Object { $_ -ceq $name }).Count -ne 1)
        {
            throw "$Description is missing exact property '$name'."
        }
    }
    foreach ($name in $actual)
    {
        if ($Expected -cnotcontains $name) { throw "$Description contains unexpected property '$name'." }
    }
}

function Assert-NoDuplicateJsonProperties
{
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string]$Path)

    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object)
    {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject())
        {
            if (-not $names.Add($property.Name)) { throw "JSON '$Path' contains duplicate property '$($property.Name)'." }
            $child = $property.Value
            Assert-NoDuplicateJsonProperties -Element $child -Path "$Path.$($property.Name)"
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array)
    {
        $index = 0
        foreach ($item in $Element.EnumerateArray())
        {
            Assert-NoDuplicateJsonProperties -Element $item -Path "$Path[$index]"
            $index++
        }
    }
}

function Read-StrictJsonText
{
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Description)

    if ([string]::IsNullOrWhiteSpace($Text)) { throw "$Description is empty." }
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    try { $document = [System.Text.Json.JsonDocument]::Parse($Text, $options) }
    catch { throw "$Description is invalid strict JSON: $($_.Exception.Message)" }
    try
    {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object)
        {
            throw "$Description root must be a JSON object."
        }
        $root = $document.RootElement
        Assert-NoDuplicateJsonProperties -Element $root -Path '$'
    }
    finally { $document.Dispose() }

    try { return $Text | ConvertFrom-Json -Depth 64 }
    catch { throw "$Description could not be materialized: $($_.Exception.Message)" }
}

function Read-TextFromStream
{
    param(
        [Parameter(Mandatory)][IO.Stream]$Stream,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][long]$MaximumBytes,
        [switch]$AsciiOnly)

    if ($Stream.Length -gt $MaximumBytes) { throw "$Description exceeds the safe size limit." }
    if ($Stream.CanSeek) { $Stream.Position = 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $reader = [IO.StreamReader]::new($Stream, $encoding, $true, 4096, $true)
    try { $text = $reader.ReadToEnd() }
    finally
    {
        $reader.Dispose()
        if ($Stream.CanSeek) { $Stream.Position = 0 }
    }
    if ($AsciiOnly -and $text -match '[^\x00-\x7F]') { throw "$Description must be ASCII." }
    return $text
}

function Get-StreamSha256
{
    param([Parameter(Mandatory)][IO.Stream]$Stream)

    if ($Stream.CanSeek) { $Stream.Position = 0 }
    try { return (Get-FileHash -InputStream $Stream -Algorithm SHA256).Hash.ToLowerInvariant() }
    finally { if ($Stream.CanSeek) { $Stream.Position = 0 } }
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

function Get-AssetListSha256
{
    param([AllowEmptyCollection()][string[]]$Assets)
    Get-TextSha256 -Text (@(Get-OrdinalSortedUnique -Values $Assets) -join "`n")
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
                throw "Store submission paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-SafeTopLevelFileName
{
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        [IO.Path]::GetFileName($Name) -cne $Name -or [IO.Path]::IsPathRooted($Name))
    {
        throw "Store submission file name '$Name' is unsafe."
    }
}

function Read-ChecksumManifest
{
    param(
        [Parameter(Mandatory)][IO.Stream]$Stream,
        [Parameter(Mandatory)][string[]]$ExpectedNames)

    $text = Read-TextFromStream -Stream $Stream -Description $ChecksumFileName -MaximumBytes 64KB -AsciiOnly
    $lines = @($text -split '\r?\n' | Where-Object { $_.Length -gt 0 })
    if ($lines.Count -ne $ExpectedNames.Count) { throw "$ChecksumFileName must contain exactly $($ExpectedNames.Count) entries." }
    $hashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $lines)
    {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$')
        {
            throw "$ChecksumFileName contains a non-canonical entry."
        }
        $name = $Matches[2]
        Assert-SafeTopLevelFileName -Name $name
        if (-not $hashes.TryAdd($name, $Matches[1])) { throw "$ChecksumFileName contains duplicate or case-aliased file '$name'." }
    }
    foreach ($name in $ExpectedNames)
    {
        if (-not $hashes.ContainsKey($name)) { throw "$ChecksumFileName does not cover exact file '$name'." }
    }
    foreach ($name in $hashes.Keys)
    {
        if ($ExpectedNames -cnotcontains $name) { throw "$ChecksumFileName covers unexpected file '$name'." }
    }
    return $hashes
}

function Normalize-PackagePath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description)

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOf([char]0) -ge 0) { throw "$Description is empty or contains NUL." }
    $normalized = $Path.Replace('\', '/')
    if ($normalized.StartsWith('/', [StringComparison]::Ordinal) -or $normalized -match '^[A-Za-z]:')
    {
        throw "$Description is rooted."
    }
    $segments = @($normalized.Split('/'))
    if ($segments.Count -eq 0) { throw "$Description is empty." }
    foreach ($segment in $segments)
    {
        if ([string]::IsNullOrEmpty($segment) -or $segment -in @('.', '..') -or
            $segment -match '[<>:"\\|?*\x00-\x1F]' -or $segment.EndsWith(' ') -or $segment.EndsWith('.'))
        {
            throw "$Description contains an unsafe Windows path segment."
        }
        $baseName = ($segment -split '\.')[0]
        if ($baseName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$')
        {
            throw "$Description contains a reserved Windows path segment."
        }
    }
    return ($segments -join '/')
}

function Read-ZipEntryText
{
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][long]$MaximumBytes)

    if ($Entry.Length -gt $MaximumBytes) { throw "$Description exceeds the safe size limit." }
    $stream = $Entry.Open()
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true, 4096, $true)
    try
    {
        $text = $reader.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($text)) { throw "$Description is empty." }
        return $text
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Read-ZipEntryXml
{
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory)][string]$Description)

    if ($Entry.Length -gt $MaximumXmlBytes) { throw "$Description exceeds the safe size limit." }
    $stream = $Entry.Open()
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $MaximumXmlBytes
    $reader = $null
    try
    {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    catch { throw "$Description is invalid safe XML: $($_.Exception.Message)" }
    finally { if ($null -ne $reader) { $reader.Dispose() }; $stream.Dispose() }
}

function Assert-PngDimensions
{
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height)

    if ($Entry.Length -lt 24) { throw "PNG '$($Entry.FullName)' is truncated." }
    $stream = $Entry.Open()
    try
    {
        $header = [byte[]]::new(24)
        $offset = 0
        while ($offset -lt $header.Length)
        {
            $read = $stream.Read($header, $offset, $header.Length - $offset)
            if ($read -le 0) { throw "PNG '$($Entry.FullName)' is truncated." }
            $offset += $read
        }
    }
    finally { $stream.Dispose() }
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Length; $index++)
    {
        if ($header[$index] -ne $signature[$index]) { throw "Asset '$($Entry.FullName)' is not a PNG." }
    }
    $actualWidth = [int]((([int]$header[16]) -shl 24) -bor (([int]$header[17]) -shl 16) -bor (([int]$header[18]) -shl 8) -bor ([int]$header[19]))
    $actualHeight = [int]((([int]$header[20]) -shl 24) -bor (([int]$header[21]) -shl 16) -bor (([int]$header[22]) -shl 8) -bor ([int]$header[23]))
    if ($actualWidth -ne $Width -or $actualHeight -ne $Height)
    {
        throw "Asset '$($Entry.FullName)' dimensions must be ${Width}x${Height}; found ${actualWidth}x${actualHeight}."
    }
}

function Assert-StoreIdentity
{
    param([Parameter(Mandatory)][object]$Identity)

    Assert-ExactJsonProperties -Value $Identity -Description "Store identity" -Expected @(
        "schemaVersion", "configured", "reservedProductName", "identityName", "publisher",
        "publisherDisplayName", "packageFamilyName", "applicationId")
    Assert-TextEqual -Actual ([string]$Identity.schemaVersion) -Expected $ExpectedStoreIdentitySchema -Field "Store identity schemaVersion"
    Assert-JsonBoolean -Value $Identity.configured -Expected $true -Field "Store identity configured"
    foreach ($field in @("reservedProductName", "identityName", "publisher", "publisherDisplayName", "packageFamilyName", "applicationId"))
    {
        Assert-NonEmptyResolvedText -Value $Identity.$field -Field "Store identity $field" -MaximumLength 256
    }
    if ([string]$Identity.identityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw "Store identityName is invalid." }
    if ([string]$Identity.publisher -notmatch '^CN=.{1,253}$' -or
        [string]::Equals([string]$Identity.publisher, $DevelopmentPublisher, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Store publisher must be a resolved Partner Center CN and must not be the development publisher."
    }
    $expectedPublisherId = Get-PackagePublisherId -Publisher ([string]$Identity.publisher)
    $expectedPackageFamilyName = "$([string]$Identity.identityName)_$expectedPublisherId"
    if ([string]$Identity.packageFamilyName -notmatch '^[A-Za-z0-9.-]{3,50}_[a-hjkmnp-tv-z0-9]{13}$' -or
        -not [string]::Equals(
            [string]$Identity.packageFamilyName,
            $expectedPackageFamilyName,
            [StringComparison]::Ordinal))
    {
        throw "Store packageFamilyName is inconsistent with identityName and publisher."
    }
    Assert-TextEqual -Actual ([string]$Identity.applicationId) -Expected $ExpectedApplicationId -Field "Store applicationId"
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

function Assert-DependencyInventory
{
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)][string]$SemanticVersion)

    Assert-ExactJsonProperties -Value $Inventory -Description "Dependency inventory" -Expected @(
        "schemaVersion", "product", "semanticVersion", "generatedUtc", "packageCount",
        "redistributedPackageCount", "missingLicenseMetadataCount", "notice", "dependencies")
    Assert-TextEqual -Actual ([string]$Inventory.schemaVersion) -Expected $ExpectedInventorySchema -Field "Dependency inventory schemaVersion"
    Assert-TextEqual -Actual ([string]$Inventory.product) -Expected $ExpectedProduct -Field "Dependency inventory product"
    Assert-TextEqual -Actual ([string]$Inventory.semanticVersion) -Expected $SemanticVersion -Field "Dependency inventory semanticVersion"
    Assert-JsonInteger -Value $Inventory.packageCount -Field "Dependency packageCount" -Minimum 1
    Assert-JsonInteger -Value $Inventory.redistributedPackageCount -Field "Dependency redistributedPackageCount" -Minimum 1
    Assert-JsonInteger -Value $Inventory.missingLicenseMetadataCount -Field "Dependency missingLicenseMetadataCount"
    if ([long]$Inventory.missingLicenseMetadataCount -ne 0) { throw "Store dependency inventory contains incomplete license metadata." }
    $dependencies = @($Inventory.dependencies)
    if ($dependencies.Count -ne [long]$Inventory.packageCount) { throw "Dependency packageCount does not match dependencies." }
    $policyHash = (Get-FileHash -LiteralPath $NoticePolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-TextEqual -Actual ([string]$Inventory.notice.policySha256) -Expected $policyHash `
        -Field "Notice policy SHA-256" -IgnoreCase
    $policy = Read-StrictJsonText -Text (Get-Content -LiteralPath $NoticePolicyPath -Raw) -Description "Notice policy"
    Assert-TextEqual -Actual ([string]$policy.schemaVersion) -Expected "infra-005-third-party-notices-v1" -Field "Notice policy schema"
    if (@($policy.packages).Count -ne 43 -or [int]$policy.expectedRedistributedPackageCount -ne 33)
    {
        throw "Checked-in notice policy differs from the exact reviewed 43/33 graph."
    }
    $policyByIdentity = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($policy.packages))
    {
        $identity = "$($entry.id)/$($entry.version)"
        if (-not $policyByIdentity.TryAdd($identity, $entry)) { throw "Notice policy contains duplicate '$identity'." }
    }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $redistributedCount = 0
    foreach ($dependency in $dependencies)
    {
        Assert-ExactJsonProperties -Value $dependency -Description "Dependency '$($dependency.id)'" -Expected @(
            "id", "version", "direct", "acquisition", "contentHashSha512", "license", "redistributed",
            "redistributedAssets", "copyright", "authors", "requireLicenseAcceptance", "repository", "notice")
        Assert-NonEmptyResolvedText -Value $dependency.id -Field "Dependency id"
        Assert-NonEmptyResolvedText -Value $dependency.version -Field "Dependency version"
        $identity = [string]$dependency.id + '/' + [string]$dependency.version
        if (-not $ids.Add($identity)) { throw "Dependency inventory contains a duplicate package identity." }
        if (-not $policyByIdentity.ContainsKey($identity)) { throw "Dependency '$identity' is absent from the checked-in notice policy." }
        $policyEntry = $policyByIdentity[$identity]
        Assert-TextEqual -Actual ([string]$dependency.contentHashSha512) -Expected ([string]$policyEntry.contentHashSha512) `
            -Field "Dependency '$identity' locked SHA-512"
        $licenseValues = @(@($dependency.license.expression, $dependency.license.file, $dependency.license.url) |
            Where-Object { $_ -is [string] -and -not [string]::IsNullOrWhiteSpace($_) })
        if ($licenseValues.Count -eq 0) { throw "Dependency '$($dependency.id)' has no complete license metadata." }
        Assert-JsonBoolean -Value $dependency.redistributed -Expected ([bool]$dependency.redistributed) -Field "Dependency redistributed"
        Assert-JsonBoolean -Value $dependency.requireLicenseAcceptance -Expected ([bool]$dependency.requireLicenseAcceptance) -Field "Dependency requireLicenseAcceptance"
        $assets = @($dependency.redistributedAssets)
        Assert-ExactJsonProperties -Value $dependency.notice -Description "Dependency notice" -Expected @(
            "strategy", "material", "assetListSha256", "payloadTreeSha256")
        Assert-NonEmptyResolvedText -Value $dependency.notice.strategy -Field "Dependency notice strategy"
        if ([string]$dependency.notice.assetListSha256 -notmatch '^[0-9a-f]{64}$') { throw "Dependency notice asset-list hash is invalid." }
        if ([string]$dependency.notice.payloadTreeSha256 -notmatch '^[0-9a-f]{64}$') { throw "Dependency notice payload-tree hash is invalid." }
        Assert-TextEqual -Actual ([string]$dependency.notice.assetListSha256) `
            -Expected ([string]$policyEntry.assetListSha256) -Field "Dependency '$identity' asset-list SHA-256" -IgnoreCase
        Assert-TextEqual -Actual (Get-AssetListSha256 -Assets @($assets)) `
            -Expected ([string]$policyEntry.assetListSha256) -Field "Dependency '$identity' computed asset-list SHA-256" -IgnoreCase
        Assert-TextEqual -Actual ([string]$dependency.notice.payloadTreeSha256) `
            -Expected ([string]$policyEntry.payloadTreeSha256) -Field "Dependency '$identity' payload-tree SHA-256" -IgnoreCase
        if ([bool]$dependency.redistributed -ne [bool]$policyEntry.redistributed)
        {
            throw "Dependency '$identity' redistribution flag differs from the checked-in notice policy."
        }
        if ([bool]$dependency.redistributed)
        {
            $redistributedCount++
            if ($assets.Count -eq 0 -or @($dependency.notice.material).Count -eq 0) { throw "Redistributed dependency '$($dependency.id)' has incomplete notice evidence." }
        }
        elseif ($assets.Count -ne 0 -or [string]$dependency.notice.strategy -cne 'excluded-no-payload')
        {
            throw "Excluded dependency '$($dependency.id)' unexpectedly declares payload or a notice strategy."
        }
    }
    if ($redistributedCount -ne [int]$Inventory.redistributedPackageCount) { throw "Redistributed dependency count does not match dependencies." }
    if ($dependencies.Count -ne 43 -or $redistributedCount -ne 33) { throw "Store dependency graph differs from the exact reviewed 43/33 notice policy." }
    Assert-ExactJsonProperties -Value $Inventory.notice -Description "Dependency notice manifest" -Expected @(
        "file", "sha256", "sizeBytes", "policyFile", "policySha256")
    Assert-TextEqual -Actual ([string]$Inventory.notice.file) -Expected "THIRD-PARTY-NOTICES.txt" -Field "Notice file"
    Assert-TextEqual -Actual ([string]$Inventory.notice.policyFile) -Expected "third-party-notice-policy.json" -Field "Notice policy file"
    foreach ($field in @("sha256", "policySha256")) { if ([string]$Inventory.notice.$field -notmatch '^[0-9a-f]{64}$') { throw "Notice $field is invalid." } }
    Assert-JsonInteger -Value $Inventory.notice.sizeBytes -Field "Notice sizeBytes" -Minimum 1
    [pscustomobject]@{ Count = $dependencies.Count; RedistributedCount = $redistributedCount; Policy = $policy }
}

function Assert-PackagedDependencyPayloadTrees
{
    param(
        [Parameter(Mandatory)][object]$Inventory,
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]$Entries)

    $policyByIdentity = @{}
    foreach ($entry in @($Policy.packages)) { $policyByIdentity["$($entry.id)/$($entry.version)"] = $entry }
    foreach ($dependency in @($Inventory.dependencies))
    {
        $identity = "$($dependency.id)/$($dependency.version)"
        $builder = [Text.StringBuilder]::new()
        foreach ($asset in @(Get-OrdinalSortedUnique -Values @($dependency.redistributedAssets)))
        {
            if (-not $Entries.ContainsKey($asset) -or $Entries[$asset].FullName -cne $asset)
            {
                throw "Attributed package asset '$asset' for '$identity' is absent from the MSIX."
            }
            $stream = $Entries[$asset].Open()
            try { $assetHash = Get-StreamSha256 -Stream $stream }
            finally { $stream.Dispose() }
            [void]$builder.Append($asset).Append("`n").Append($assetHash).Append("`n")
        }
        $treeHash = Get-TextSha256 -Text $builder.ToString()
        Assert-TextEqual -Actual $treeHash -Expected ([string]$policyByIdentity[$identity].payloadTreeSha256) `
            -Field "Packaged payload tree '$identity'" -IgnoreCase
    }

    $interOverride = @($Policy.overrides | Where-Object package -EQ 'Avalonia.Fonts.Inter/12.1.0') | Select-Object -First 1
    $interStream = $Entries['Avalonia.Fonts.Inter.dll'].Open()
    try { $interHash = Get-StreamSha256 -Stream $interStream }
    finally { $interStream.Dispose() }
    Assert-TextEqual -Actual $interHash -Expected ([string]$interOverride.payloadSha256) `
        -Field "Packaged Inter payload SHA-256" -IgnoreCase
}

function Assert-ReleaseManifest
{
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][object]$StoreIdentity,
        [Parameter(Mandatory)][string]$MsixFileName,
        [Parameter(Mandatory)][IO.Stream]$MsixLease,
        [Parameter(Mandatory)][string]$MsixHash,
        [Parameter(Mandatory)][string]$InventoryHash,
        [Parameter(Mandatory)][string]$StoreIdentityHash,
        [Parameter(Mandatory)][object]$DependencyInventoryResult,
        [Parameter(Mandatory)][object]$Inventory)

    Assert-ExactJsonProperties -Value $Manifest -Description "Release manifest" -Expected @(
        "schemaVersion", "product", "semanticVersion", "msixVersion", "runtimeIdentifier", "architecture",
        "configuration", "build", "package", "signing", "audioCodec", "supplyChain", "dataPersistence")
    Assert-TextEqual -Actual ([string]$Manifest.schemaVersion) -Expected $ExpectedReleaseSchema -Field "Release schemaVersion"
    Assert-TextEqual -Actual ([string]$Manifest.product) -Expected $ExpectedProduct -Field "Release product"
    Assert-TextEqual -Actual ([string]$Manifest.runtimeIdentifier) -Expected $ExpectedRuntimeIdentifier -Field "Release runtimeIdentifier"
    Assert-TextEqual -Actual ([string]$Manifest.architecture) -Expected $ExpectedArchitecture -Field "Release architecture" -IgnoreCase
    Assert-TextEqual -Actual ([string]$Manifest.configuration) -Expected "Release" -Field "Release configuration"
    Assert-NonEmptyResolvedText -Value $Manifest.semanticVersion -Field "Release semanticVersion"
    if ([string]$Manifest.semanticVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Release semanticVersion is invalid." }
    try { $msixVersion = [version]([string]$Manifest.msixVersion) } catch { throw "Release msixVersion is invalid." }
    if ($msixVersion.Revision -lt 0) { throw "Release msixVersion must have four numeric components." }

    Assert-ExactJsonProperties -Value $Manifest.build -Description "Release build" -Expected @(
        "createdUtc", "sourceRevision", "dotnetSdkVersion", "dotnetRuntimePackVersion",
        "windowsSdkBuildToolsVersion", "selfContained", "trimmed")
    foreach ($field in @("sourceRevision", "dotnetSdkVersion", "dotnetRuntimePackVersion", "windowsSdkBuildToolsVersion"))
    {
        Assert-NonEmptyResolvedText -Value $Manifest.build.$field -Field "Release build $field"
    }
    try { [void][DateTimeOffset]::Parse([string]$Manifest.build.createdUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) }
    catch { throw "Release build createdUtc is not a round-trip timestamp." }
    Assert-JsonBoolean -Value $Manifest.build.selfContained -Expected $true -Field "Release selfContained"
    Assert-JsonBoolean -Value $Manifest.build.trimmed -Expected $false -Field "Release trimmed"

    Assert-ExactJsonProperties -Value $Manifest.package -Description "Release package" -Expected @(
        "identityName", "publisher", "publisherDisplayName", "applicationId", "file", "sizeBytes", "sha256")
    Assert-TextEqual -Actual ([string]$Manifest.package.identityName) -Expected ([string]$StoreIdentity.identityName) -Field "Release package identityName"
    Assert-TextEqual -Actual ([string]$Manifest.package.publisher) -Expected ([string]$StoreIdentity.publisher) -Field "Release package publisher"
    Assert-TextEqual -Actual ([string]$Manifest.package.publisherDisplayName) -Expected ([string]$StoreIdentity.publisherDisplayName) -Field "Release package publisherDisplayName"
    Assert-TextEqual -Actual ([string]$Manifest.package.applicationId) -Expected ([string]$StoreIdentity.applicationId) -Field "Release package applicationId"
    Assert-TextEqual -Actual ([string]$Manifest.package.file) -Expected $MsixFileName -Field "Release package file"
    Assert-JsonInteger -Value $Manifest.package.sizeBytes -Field "Release package sizeBytes" -Minimum 1
    if ([long]$Manifest.package.sizeBytes -ne $MsixLease.Length) { throw "Release package sizeBytes does not match the leased MSIX." }
    Assert-TextEqual -Actual ([string]$Manifest.package.sha256) -Expected $MsixHash -Field "Release package SHA-256" -IgnoreCase

    Assert-ExactJsonProperties -Value $Manifest.signing -Description "Release signing" -Expected @(
        "mode", "publicEligible", "certificateSubject", "certificateThumbprint", "certificateNotAfterUtc",
        "timestampRequested", "timestampVerified", "trustStatus", "verified", "storeSigningRequired", "submissionEligible")
    Assert-TextEqual -Actual ([string]$Manifest.signing.mode) -Expected "store" -Field "Store signing mode"
    Assert-JsonBoolean -Value $Manifest.signing.publicEligible -Expected $false -Field "Store publicEligible"
    foreach ($field in @("certificateSubject", "certificateThumbprint", "certificateNotAfterUtc"))
    {
        if ($null -ne $Manifest.signing.$field) { throw "Store signing $field must be JSON null." }
    }
    Assert-JsonBoolean -Value $Manifest.signing.timestampRequested -Expected $false -Field "Store timestampRequested"
    Assert-JsonBoolean -Value $Manifest.signing.timestampVerified -Expected $false -Field "Store timestampVerified"
    Assert-TextEqual -Actual ([string]$Manifest.signing.trustStatus) -Expected "StoreSubmissionUnsigned" -Field "Store trustStatus"
    Assert-JsonBoolean -Value $Manifest.signing.verified -Expected $true -Field "Store verified"
    Assert-JsonBoolean -Value $Manifest.signing.storeSigningRequired -Expected $true -Field "Store storeSigningRequired"
    Assert-JsonBoolean -Value $Manifest.signing.submissionEligible -Expected $true -Field "Store submissionEligible"

    Assert-ExactJsonProperties -Value $Manifest.audioCodec -Description "Release audio codec" -Expected @(
        "primaryExtension", "container", "codec", "implementation", "managedWrapper", "sampleRateHz",
        "channels", "bitsPerSample", "bitRate", "bundledCodecBinaryCount", "patentProgramStatus", "evidenceUrls")
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.primaryExtension) -Expected ".mp3" -Field "Audio primary extension"
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.container) -Expected "MP3" -Field "Audio container"
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.codec) -Expected "MPEG-1 Layer III" -Field "Audio codec"
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.implementation) -Expected "Windows Media Foundation" -Field "Audio implementation"
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.managedWrapper) -Expected "NAudio" -Field "Audio managed wrapper"
    foreach ($field in @("sampleRateHz", "channels", "bitsPerSample", "bitRate", "bundledCodecBinaryCount"))
    {
        Assert-JsonInteger -Value $Manifest.audioCodec.$field -Field "Audio codec $field" -Minimum 0
    }
    if ([int]$Manifest.audioCodec.sampleRateHz -ne 48000 -or [int]$Manifest.audioCodec.channels -ne 2 -or
        [int]$Manifest.audioCodec.bitsPerSample -ne 16 -or [int]$Manifest.audioCodec.bitRate -ne 128000 -or
        [int]$Manifest.audioCodec.bundledCodecBinaryCount -ne 0)
    {
        throw "Release audio codec preset must be exact 48 kHz, stereo, 16-bit, 128 kbps with zero bundled codec binaries."
    }
    Assert-TextEqual -Actual ([string]$Manifest.audioCodec.patentProgramStatus) `
        -Expected "Fraunhofer MP3 licensing program terminated 2017-04-23" -Field "Audio patent-program status"
    $expectedEvidenceUrls = @(
        "https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder",
        "https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456",
        "https://www.iis.fraunhofer.de/en/ff/amm/consumer-electronics/mp3.html",
        "https://www.audioblog.iis.fraunhofer.com/mp3-software-patents-licenses")
    $actualEvidenceUrls = @($Manifest.audioCodec.evidenceUrls)
    if ($actualEvidenceUrls.Count -ne $expectedEvidenceUrls.Count) { throw "Release audio codec evidence URL count is invalid." }
    for ($index = 0; $index -lt $expectedEvidenceUrls.Count; $index++)
    {
        Assert-TextEqual -Actual ([string]$actualEvidenceUrls[$index]) -Expected $expectedEvidenceUrls[$index] `
            -Field "Audio codec evidence URL $($index + 1)"
    }
    Assert-ExactJsonProperties -Value $Manifest.supplyChain -Description "Release supply chain" -Expected @(
        "inventoryFile", "inventorySha256", "dependencyCount", "redistributedDependencyCount", "checksumFile",
        "thirdPartyNoticeFile", "thirdPartyNoticeSha256", "thirdPartyNoticeSizeBytes",
        "thirdPartyNoticePolicyFile", "thirdPartyNoticePolicySha256",
        "identityPolicySchemaVersion", "identityPolicySha256", "developmentCertificateFile",
        "developmentCertificateSha256", "storeIdentityFile", "storeIdentitySha256")
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.inventoryFile) -Expected $DependencyInventoryFileName -Field "Supply-chain inventoryFile"
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.inventorySha256) -Expected $InventoryHash -Field "Supply-chain inventory SHA-256" -IgnoreCase
    Assert-JsonInteger -Value $Manifest.supplyChain.dependencyCount -Field "Supply-chain dependencyCount" -Minimum 1
    if ([int]$Manifest.supplyChain.dependencyCount -ne $DependencyInventoryResult.Count) { throw "Supply-chain dependencyCount does not match inventory." }
    if ([int]$Manifest.supplyChain.redistributedDependencyCount -ne $DependencyInventoryResult.RedistributedCount) { throw "Supply-chain redistributedDependencyCount does not match inventory." }
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.thirdPartyNoticeFile) -Expected ([string]$Inventory.notice.file) -Field "Supply-chain notice file"
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.thirdPartyNoticeSha256) -Expected ([string]$Inventory.notice.sha256) -Field "Supply-chain notice SHA-256" -IgnoreCase
    if ([long]$Manifest.supplyChain.thirdPartyNoticeSizeBytes -ne [long]$Inventory.notice.sizeBytes) { throw "Supply-chain notice size differs from inventory." }
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.thirdPartyNoticePolicyFile) -Expected ([string]$Inventory.notice.policyFile) -Field "Supply-chain notice policy file"
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.thirdPartyNoticePolicySha256) -Expected ([string]$Inventory.notice.policySha256) -Field "Supply-chain notice policy SHA-256" -IgnoreCase
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.checksumFile) -Expected $ChecksumFileName -Field "Supply-chain checksumFile"
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.storeIdentityFile) -Expected $StoreIdentityFileName -Field "Supply-chain storeIdentityFile"
    Assert-TextEqual -Actual ([string]$Manifest.supplyChain.storeIdentitySha256) -Expected $StoreIdentityHash -Field "Supply-chain Store identity SHA-256" -IgnoreCase
    foreach ($field in @("developmentCertificateFile", "developmentCertificateSha256"))
    {
        if ($Manifest.supplyChain.PSObject.Properties.Name -contains $field -and $null -ne $Manifest.supplyChain.$field)
        {
            throw "Store supply chain must not declare $field."
        }
    }
    Assert-NonEmptyResolvedText -Value $Manifest.supplyChain.identityPolicySchemaVersion -Field "Supply-chain identity policy schemaVersion"
    if ([string]$Manifest.supplyChain.identityPolicySha256 -notmatch '^[0-9a-fA-F]{64}$')
    {
        throw "Supply-chain identity policy SHA-256 is invalid."
    }

    Assert-ExactJsonProperties -Value $Manifest.dataPersistence -Description "Release dataPersistence" -Expected @(
        "localAppDataRoot", "documentsRoot", "recordingsRoot", "fileSystemWriteVirtualization",
        "registryWriteVirtualization", "restrictedUnvirtualizedResourcesDeclared")
    Assert-ExactJsonProperties -Value $Manifest.dataPersistence.localAppDataRoot -Description "Release LocalAppData root" -Expected @(
        "knownFolder", "relativePath", "resolvedAtRuntimeBy")
    Assert-ExactJsonProperties -Value $Manifest.dataPersistence.documentsRoot -Description "Release Documents root" -Expected @(
        "knownFolder", "relativePath", "resolvedAtRuntimeBy")
    Assert-ExactJsonProperties -Value $Manifest.dataPersistence.recordingsRoot -Description "Release recordings root" -Expected @(
        "source", "defaultRelativePath", "customPathPreserved")
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.localAppDataRoot.knownFolder) -Expected "LocalApplicationData" -Field "Store LocalAppData known folder"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.localAppDataRoot.relativePath) -Expected "isTranscribe" -Field "Store LocalAppData relative path"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.localAppDataRoot.resolvedAtRuntimeBy) -Expected "Environment.SpecialFolder.LocalApplicationData" -Field "Store LocalAppData resolver"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.documentsRoot.knownFolder) -Expected "MyDocuments" -Field "Store Documents known folder"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.documentsRoot.relativePath) -Expected "isTranscribe" -Field "Store Documents relative path"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.documentsRoot.resolvedAtRuntimeBy) -Expected "Environment.SpecialFolder.MyDocuments" -Field "Store Documents resolver"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.recordingsRoot.source) -Expected "settings.recordingsFolder" -Field "Store recordings source"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.recordingsRoot.defaultRelativePath) -Expected "Recordings" -Field "Store recordings default path"
    Assert-JsonBoolean -Value $Manifest.dataPersistence.recordingsRoot.customPathPreserved -Expected $true -Field "Store custom recordings preservation"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.fileSystemWriteVirtualization) -Expected "fullTrustPassThrough" -Field "Store file-system persistence mode"
    Assert-TextEqual -Actual ([string]$Manifest.dataPersistence.registryWriteVirtualization) -Expected "fullTrustPassThrough" -Field "Store registry persistence mode"
    Assert-JsonBoolean -Value $Manifest.dataPersistence.restrictedUnvirtualizedResourcesDeclared -Expected $false `
        -Field "Store restrictedUnvirtualizedResourcesDeclared"

    return [pscustomobject]@{ SemanticVersion = [string]$Manifest.semanticVersion; MsixVersion = [string]$Manifest.msixVersion }
}

function Assert-MsixManifest
{
    param(
        [Parameter(Mandatory)][Xml.XmlDocument]$Manifest,
        [Parameter(Mandatory)][object]$ReleaseManifest,
        [Parameter(Mandatory)][object]$StoreIdentity,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]$Entries)

    if ($Manifest.OuterXml -match '(?i)(isTranscribe Development|\{\{|<[^!?/][^>]*>)')
    {
        # XML elements naturally contain angle brackets; only explicit template braces and development identity are forbidden here.
        if ($Manifest.OuterXml -match '(?i)(isTranscribe Development|\{\{|\}\})') { throw "AppxManifest contains a development identity or unresolved token." }
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($Manifest.NameTable)
    $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $namespaces.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
    $namespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
    $namespaces.AddNamespace("desktop6", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6")
    $namespaces.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")

    $identities = @($Manifest.SelectNodes("/f:Package/f:Identity", $namespaces))
    if ($identities.Count -ne 1) { throw "AppxManifest must contain one Identity." }
    $identity = $identities[0]
    Assert-TextEqual -Actual $identity.GetAttribute("Name") -Expected ([string]$StoreIdentity.identityName) -Field "MSIX identity name"
    Assert-TextEqual -Actual $identity.GetAttribute("Publisher") -Expected ([string]$StoreIdentity.publisher) -Field "MSIX publisher"
    Assert-TextEqual -Actual $identity.GetAttribute("Version") -Expected ([string]$ReleaseManifest.msixVersion) -Field "MSIX version"
    Assert-TextEqual -Actual $identity.GetAttribute("ProcessorArchitecture") -Expected $ExpectedArchitecture -Field "MSIX architecture" -IgnoreCase

    $targetFamilies = @($Manifest.SelectNodes("/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name='Windows.Desktop']", $namespaces))
    if ($targetFamilies.Count -ne 1) { throw "AppxManifest must contain one Windows.Desktop target family." }
    Assert-TextEqual -Actual $targetFamilies[0].GetAttribute("MinVersion") -Expected $ExpectedMinimumWindowsVersion -Field "MSIX minimum Windows version"

    $applications = @($Manifest.SelectNodes("/f:Package/f:Applications/f:Application", $namespaces))
    if ($applications.Count -ne 1) { throw "AppxManifest must contain one Application." }
    $application = $applications[0]
    Assert-TextEqual -Actual $application.GetAttribute("Id") -Expected ([string]$StoreIdentity.applicationId) -Field "MSIX application Id"
    Assert-TextEqual -Actual $application.GetAttribute("Executable") -Expected $ExpectedApplicationExecutable -Field "MSIX executable"
    Assert-TextEqual -Actual $application.GetAttribute("EntryPoint") -Expected "Windows.FullTrustApplication" -Field "MSIX entry point"
    Assert-TextEqual -Actual $application.GetAttribute("RuntimeBehavior", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected "packagedClassicApp" -Field "MSIX runtime behavior"
    Assert-TextEqual -Actual $application.GetAttribute("TrustLevel", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected "mediumIL" -Field "MSIX trust level"

    $capabilities = $Manifest.SelectSingleNode("/f:Package/f:Capabilities", $namespaces)
    if ($null -eq $capabilities) { throw "Store AppxManifest has no Capabilities declaration." }
    $capabilityElements = @($capabilities.ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
    if ($capabilityElements.Count -ne 2) { throw "Store AppxManifest capability exact allowlist requires two declarations." }
    $fullTrust = @($capabilityElements | Where-Object {
        $_.NamespaceURI -ceq "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" -and
        $_.LocalName -ceq "Capability" -and $_.GetAttribute("Name") -ceq "runFullTrust"
    })
    if ($fullTrust.Count -ne 1) { throw "Store AppxManifest must declare one runFullTrust capability." }
    $microphone = @($capabilityElements | Where-Object {
        $_.NamespaceURI -ceq "http://schemas.microsoft.com/appx/manifest/foundation/windows10" -and
        $_.LocalName -ceq "DeviceCapability" -and $_.GetAttribute("Name") -ceq "microphone"
    })
    if ($microphone.Count -ne 1) { throw "Store AppxManifest must declare one microphone capability." }
    if (@($capabilityElements | Where-Object { $_ -notin @($fullTrust[0], $microphone[0]) }).Count -ne 0)
    {
        throw "Store AppxManifest contains a capability outside the exact Store allowlist."
    }
    if (@($Manifest.SelectNodes("/f:Package/f:Properties/desktop6:FileSystemWriteVirtualization", $namespaces)).Count -ne 0 -or
        @($Manifest.SelectNodes("/f:Package/f:Properties/desktop6:RegistryWriteVirtualization", $namespaces)).Count -ne 0)
    {
        throw "Store AppxManifest must not declare desktop6 file-system or registry virtualization overrides."
    }
    if (@($Manifest.SelectNodes("/f:Package/*[local-name()='Extensions']", $namespaces)).Count -ne 0)
    {
        throw "Store AppxManifest exact extension allowlist permits no package-level Extensions."
    }

    $extensionContainers = @($application.SelectNodes("f:Extensions", $namespaces))
    if ($extensionContainers.Count -ne 1) { throw "Store AppxManifest must contain one application Extensions container." }
    $applicationExtensions = @($extensionContainers[0].ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
    if ($applicationExtensions.Count -ne 1) { throw "Store AppxManifest extension exact allowlist requires one application extension." }
    $startup = $applicationExtensions[0]
    if ($startup.NamespaceURI -cne "http://schemas.microsoft.com/appx/manifest/desktop/windows10" -or
        $startup.LocalName -cne "Extension" -or $startup.GetAttribute("Category") -cne "windows.startupTask")
    {
        throw "Store AppxManifest contains an application extension outside the exact Store allowlist."
    }
    Assert-TextEqual -Actual $startup.GetAttribute("Executable") -Expected $ExpectedApplicationExecutable -Field "StartupTask executable"
    Assert-TextEqual -Actual $startup.GetAttribute("EntryPoint") -Expected "Windows.FullTrustApplication" -Field "StartupTask entry point"
    Assert-TextEqual -Actual $startup.GetAttribute("Parameters", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10") `
        -Expected "--autostart" -Field "StartupTask parameters"
    $startupChildren = @($startup.ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
    $task = @($startup.SelectNodes("desktop:StartupTask", $namespaces))
    if ($startupChildren.Count -ne 1 -or $task.Count -ne 1 -or $startupChildren[0] -ne $task[0] -or
        $task[0].GetAttribute("TaskId") -cne "isTranscribeStartup" -or
        $task[0].GetAttribute("Enabled") -notmatch '^(?i:false)$')
    {
        throw "Store StartupTask declaration is invalid or enabled by default."
    }

    $packageDisplayName = $Manifest.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", $namespaces)
    $publisherDisplayName = $Manifest.SelectSingleNode("/f:Package/f:Properties/f:PublisherDisplayName", $namespaces)
    $visualElements = $application.SelectSingleNode("uap:VisualElements", $namespaces)
    if ($null -eq $packageDisplayName -or $null -eq $publisherDisplayName -or $null -eq $visualElements)
    {
        throw "Store AppxManifest display metadata is incomplete."
    }
    Assert-TextEqual -Actual $packageDisplayName.InnerText.Trim() -Expected ([string]$StoreIdentity.reservedProductName) -Field "MSIX reserved product name"
    Assert-TextEqual -Actual $publisherDisplayName.InnerText.Trim() -Expected ([string]$StoreIdentity.publisherDisplayName) -Field "MSIX publisher display name"
    Assert-TextEqual -Actual $visualElements.GetAttribute("DisplayName") -Expected ([string]$StoreIdentity.reservedProductName) -Field "MSIX visual display name"

    foreach ($asset in $ExpectedAssets.GetEnumerator())
    {
        if (-not $Entries.ContainsKey($asset.Key)) { throw "Required MSIX asset '$($asset.Key)' is missing." }
        Assert-PngDimensions -Entry $Entries[$asset.Key] -Width $asset.Value[0] -Height $asset.Value[1]
    }
}

function Assert-AppxBlockMap
{
    param(
        [Parameter(Mandatory)][Xml.XmlDocument]$BlockMap,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]$Entries)

    $namespaces = [Xml.XmlNamespaceManager]::new($BlockMap.NameTable)
    $namespaces.AddNamespace("b", "http://schemas.microsoft.com/appx/2010/blockmap")
    $root = $BlockMap.SelectSingleNode("/b:BlockMap", $namespaces)
    if ($null -eq $root -or $root.GetAttribute("HashMethod") -cne "http://www.w3.org/2001/04/xmlenc#sha256")
    {
        throw "AppxBlockMap is missing or does not use SHA-256."
    }
    $covered = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verifiedBlocks = 0
    $fileNodes = @($root.SelectNodes("b:File", $namespaces))
    foreach ($fileNode in $fileNodes)
    {
        $relative = Normalize-PackagePath -Path $fileNode.GetAttribute("Name") -Description "AppxBlockMap file name"
        if (-not $covered.Add($relative)) { throw "AppxBlockMap contains duplicate file '$relative'." }
        if (-not $Entries.ContainsKey($relative)) { throw "AppxBlockMap references missing file '$relative'." }
        $entry = $Entries[$relative]
        $declaredSize = 0L
        if (-not [long]::TryParse($fileNode.GetAttribute("Size"), [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$declaredSize) -or $declaredSize -ne $entry.Length)
        {
            throw "AppxBlockMap size mismatch for '$relative'."
        }
        $blocks = @($fileNode.SelectNodes("b:Block", $namespaces))
        $expectedBlockCount = [int][Math]::Ceiling($entry.Length / [double]$BlockSizeBytes)
        if ($blocks.Count -ne $expectedBlockCount) { throw "AppxBlockMap block count mismatch for '$relative'." }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        $consumed = 0L
        try
        {
            foreach ($block in $blocks)
            {
                $remaining = [int][Math]::Min($BlockSizeBytes, $entry.Length - $consumed)
                $buffer = [byte[]]::new($remaining)
                $offset = 0
                while ($offset -lt $buffer.Length)
                {
                    $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
                    if ($read -le 0) { throw "Unexpected end of '$relative' while verifying AppxBlockMap." }
                    $offset += $read
                    $consumed += $read
                }
                $actualHash = [Convert]::ToBase64String($sha.ComputeHash($buffer))
                Assert-TextEqual -Actual $block.GetAttribute("Hash") -Expected $actualHash -Field "AppxBlockMap hash for '$relative'"
                $verifiedBlocks++
            }
            if ($consumed -ne $entry.Length) { throw "AppxBlockMap did not consume all bytes of '$relative'." }
        }
        finally { $sha.Dispose(); $stream.Dispose() }
    }

    $reserved = @("AppxBlockMap.xml", "[Content_Types].xml", "AppxMetadata/CodeIntegrity.cat")
    foreach ($entry in $Entries.GetEnumerator())
    {
        if ($entry.Value.FullName.EndsWith('/', [StringComparison]::Ordinal)) { continue }
        if ($reserved -ccontains $entry.Key) { continue }
        if (-not $covered.Contains($entry.Key)) { throw "MSIX payload '$($entry.Key)' is absent from AppxBlockMap." }
    }
    return [pscustomobject]@{ FileCount = $fileNodes.Count; BlockCount = $verifiedBlocks }
}

function Get-ZipEntrySha256
{
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    $stream = $Entry.Open()
    try { return Get-StreamSha256 -Stream $stream }
    finally { $stream.Dispose() }
}

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance
function Assert-LocalTranscriptionPayload
{
    param(
        [Parameter(Mandatory)]
        [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]$Entries,
        [Parameter(Mandatory)][string]$NoticeText)

    $workerBaseName = "IsTranscribe.Transcription.Worker"
    if (-not $Entries.ContainsKey($LocalRuntimeInventoryPath))
    {
        $partialPayload = @($Entries.Keys | Where-Object {
            $_.StartsWith($workerBaseName, [StringComparison]::OrdinalIgnoreCase) -or
            $_ -ieq "native/runtime-manifest.v1.json" -or
            [IO.Path]::GetFileName($_).StartsWith("istranscribe_whisper_v1", [StringComparison]::OrdinalIgnoreCase)
        })
        if ($partialPayload.Count -gt 0)
        {
            throw "MSIX contains a partial local-transcription payload without its composition receipt."
        }
        return [pscustomobject]@{ Present = $false; WorkerFileCount = 0; NativeLibraryCount = 0 }
    }

    $inventoryEntry = $Entries[$LocalRuntimeInventoryPath]
    if ($inventoryEntry.FullName -cne $LocalRuntimeInventoryPath)
    {
        throw "The local-transcription composition receipt must use its exact canonical path."
    }
    $inventoryText = Read-ZipEntryText `
        -Entry $inventoryEntry `
        -Description "Local-transcription composition receipt" `
        -MaximumBytes $MaximumJsonBytes
    $inventory = Read-StrictJsonText -Text $inventoryText -Description "Local-transcription composition receipt"
    Assert-TextEqual `
        -Actual ([string]$inventory.schemaVersion) `
        -Expected "istranscribe-local-runtime-package-v1" `
        -Field "Local-transcription receipt schema"
    Assert-TextEqual `
        -Actual ([string]$inventory.rid) `
        -Expected $ExpectedRuntimeIdentifier `
        -Field "Local-transcription receipt RID"
    Assert-TextEqual `
        -Actual ([string]$inventory.worker.executable) `
        -Expected "$workerBaseName.exe" `
        -Field "Local-transcription worker executable"
    Assert-JsonBoolean `
        -Value $inventory.worker.selfContained `
        -Expected $true `
        -Field "Local-transcription worker selfContained"

    $requiredWorkerPaths = @(
        "$workerBaseName.exe",
        "$workerBaseName.dll",
        "$workerBaseName.deps.json",
        "$workerBaseName.runtimeconfig.json")
    $workerFiles = @($inventory.worker.files)
    if ($workerFiles.Count -lt $requiredWorkerPaths.Count)
    {
        throw "The local-transcription worker receipt is incomplete."
    }
    $workerPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($workerFile in $workerFiles)
    {
        $relativePath = Normalize-PackagePath `
            -Path ([string]$workerFile.path) `
            -Description "Local-transcription worker receipt path"
        if (-not $workerPathSet.Add($relativePath))
        {
            throw "The local-transcription worker receipt repeats '$relativePath'."
        }
        if ([string]$workerFile.disposition -cnotin @("worker", "shared-byte-identical"))
        {
            throw "The local-transcription worker disposition for '$relativePath' is invalid."
        }
        Assert-JsonInteger `
            -Value $workerFile.sizeBytes `
            -Field "Local-transcription worker '$relativePath' size"
        $expectedHash = [string]$workerFile.sha256
        if ($expectedHash -cnotmatch '^[0-9a-f]{64}$')
        {
            throw "The local-transcription worker hash for '$relativePath' is not canonical."
        }
        if (-not $Entries.ContainsKey($relativePath) -or $Entries[$relativePath].FullName -cne $relativePath)
        {
            throw "The local-transcription worker payload '$relativePath' is missing."
        }
        $entry = $Entries[$relativePath]
        if ($entry.Length -ne [long]$workerFile.sizeBytes -or
            (Get-ZipEntrySha256 -Entry $entry) -cne $expectedHash)
        {
            throw "worker_payload_mismatch: '$relativePath' differs from its composition receipt."
        }
    }
    foreach ($requiredPath in $requiredWorkerPaths)
    {
        if (-not $workerPathSet.Contains($requiredPath))
        {
            throw "The local-transcription worker receipt omits '$requiredPath'."
        }
    }
    if (-not $workerPathSet.Contains("hostfxr.dll"))
    {
        throw "The local-transcription worker receipt does not prove a self-contained Windows runtime."
    }

    $workerDepsText = Read-ZipEntryText `
        -Entry $Entries["$workerBaseName.deps.json"] `
        -Description "Local-transcription worker deps.json" `
        -MaximumBytes $MaximumJsonBytes
    $workerDeps = Read-StrictJsonText -Text $workerDepsText -Description "Local-transcription worker deps.json"
    if (-not ([string]$workerDeps.runtimeTarget.name).EndsWith(
            "/$ExpectedRuntimeIdentifier",
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The local-transcription worker deps.json does not target '$ExpectedRuntimeIdentifier'."
    }

    Assert-TextEqual `
        -Actual ([string]$inventory.native.runtimeManifestPath) `
        -Expected "native/runtime-manifest.v1.json" `
        -Field "Packaged native runtime manifest path"
    $manifestPath = [string]$inventory.native.runtimeManifestPath
    if (-not $Entries.ContainsKey($manifestPath) -or $Entries[$manifestPath].FullName -cne $manifestPath)
    {
        throw "The packaged native runtime manifest is missing."
    }
    $declaredManifestHash = [string]$inventory.native.runtimeManifestSha256
    Assert-TextEqual `
        -Actual $declaredManifestHash `
        -Expected $ExpectedLocalRuntimeManifestSha256 `
        -Field "Packaged native runtime manifest pin"
    if ((Get-ZipEntrySha256 -Entry $Entries[$manifestPath]) -cne $ExpectedLocalRuntimeManifestSha256)
    {
        throw "The packaged native runtime manifest bytes differ from the app-owned pin."
    }
    $manifestText = Read-ZipEntryText `
        -Entry $Entries[$manifestPath] `
        -Description "Packaged native runtime manifest" `
        -MaximumBytes $MaximumJsonBytes
    $manifest = Read-StrictJsonText -Text $manifestText -Description "Packaged native runtime manifest"
    Assert-TextEqual `
        -Actual ([string]$manifest.schemaVersion) `
        -Expected "istranscribe-whisper-native-v1" `
        -Field "Packaged native runtime manifest schema"
    Assert-TextEqual `
        -Actual ([string]$inventory.native.sourceCommit) `
        -Expected ([string]$manifest.source.commit) `
        -Field "Packaged native runtime source commit"
    Assert-TextEqual `
        -Actual ([string]$inventory.native.sourceArchiveSha256) `
        -Expected ([string]$manifest.source.archive.sha256) `
        -Field "Packaged native runtime source archive"

    $ridEntries = @($manifest.rids | Where-Object { [string]$_.rid -ceq $ExpectedRuntimeIdentifier })
    if ($ridEntries.Count -ne 1)
    {
        throw "The packaged native runtime manifest does not contain one exact '$ExpectedRuntimeIdentifier' entry."
    }
    $ridEntry = $ridEntries[0]
    $libraries = @($inventory.native.libraries)
    if (@($libraries | Where-Object { [string]$_.variant -ceq "cpu" }).Count -ne 1)
    {
        throw "The packaged native runtime does not contain one required CPU library."
    }
    $expectedNativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$expectedNativePaths.Add($LocalRuntimeInventoryPath)
    [void]$expectedNativePaths.Add($manifestPath)
    foreach ($library in $libraries)
    {
        $variant = [string]$library.variant
        if ($variant -cnotin @("cpu", "vulkan"))
        {
            throw "The packaged native runtime variant '$variant' is unsupported."
        }
        $manifestVariants = @($ridEntry.variants | Where-Object { [string]$_.id -ceq $variant })
        if ($manifestVariants.Count -ne 1)
        {
            throw "The packaged native runtime variant '$variant' is absent from the manifest."
        }
        $manifestVariant = $manifestVariants[0]
        Assert-JsonBoolean `
            -Value $library.required `
            -Expected ([bool]$manifestVariant.required) `
            -Field "Packaged native '$variant' required flag"
        $relativePath = Normalize-PackagePath `
            -Path ([string]$library.path) `
            -Description "Packaged native '$variant' path"
        $expectedPath = "native/$([string]$manifestVariant.outputFile)"
        Assert-TextEqual -Actual $relativePath -Expected $expectedPath -Field "Packaged native '$variant' path"
        if (-not $expectedNativePaths.Add($relativePath))
        {
            throw "The packaged native runtime repeats '$relativePath'."
        }
        Assert-JsonInteger -Value $library.sizeBytes -Field "Packaged native '$variant' size"
        $expectedHash = [string]$library.sha256
        if ($expectedHash -cnotmatch '^[0-9a-f]{64}$')
        {
            throw "The packaged native '$variant' hash is not canonical."
        }
        if (-not $Entries.ContainsKey($relativePath) -or $Entries[$relativePath].FullName -cne $relativePath)
        {
            throw "The packaged native '$variant' library is missing."
        }
        $entry = $Entries[$relativePath]
        if ($entry.Length -ne [long]$library.sizeBytes -or
            (Get-ZipEntrySha256 -Entry $entry) -cne $expectedHash)
        {
            throw "The packaged native '$variant' library differs from its inspected inventory."
        }
    }
    $actualNativePaths = @($Entries.Keys | Where-Object {
        $_.StartsWith("native/", [StringComparison]::Ordinal)
    } | Sort-Object)
    $expectedNativePathList = @($expectedNativePaths | Sort-Object)
    if (($actualNativePaths -join "`n") -cne ($expectedNativePathList -join "`n"))
    {
        throw "The packaged native directory contains an untracked or missing file."
    }

    $manifestLicenses = @{}
    foreach ($license in @($manifest.licenses))
    {
        $component = [string]$license.component
        if ($manifestLicenses.ContainsKey($component))
        {
            throw "The packaged native runtime manifest repeats license component '$component'."
        }
        $manifestLicenses[$component] = $license
    }
    $receiptLicenses = @($inventory.native.licenses)
    if ($receiptLicenses.Count -ne $manifestLicenses.Count)
    {
        throw "The packaged native runtime license inventory is incomplete."
    }
    foreach ($license in $receiptLicenses)
    {
        $component = [string]$license.component
        if (-not $manifestLicenses.ContainsKey($component))
        {
            throw "The packaged native license '$component' is absent from the manifest."
        }
        $expectedLicense = $manifestLicenses[$component]
        Assert-TextEqual -Actual ([string]$license.spdx) -Expected ([string]$expectedLicense.spdx) `
            -Field "Packaged native '$component' SPDX"
        Assert-TextEqual `
            -Actual ([string]$license.checkedInPath) `
            -Expected ([string]$expectedLicense.checkedInPath) `
            -Field "Packaged native '$component' checked-in license"
        Assert-TextEqual `
            -Actual ([string]$license.sha256) `
            -Expected ([string]$expectedLicense.sha256) `
            -Field "Packaged native '$component' license hash"
    }
    foreach ($requiredNoticeText in @(
        "whisper.cpp and ggml",
        "OpenAI Whisper",
        [string]$manifest.source.commit))
    {
        if (-not $NoticeText.Contains($requiredNoticeText, [StringComparison]::Ordinal))
        {
            throw "Packaged third-party notices omit native evidence '$requiredNoticeText'."
        }
    }

    foreach ($relativePath in $Entries.Keys)
    {
        $fileName = [IO.Path]::GetFileName($relativePath)
        if ($fileName -like "*.ggml" -or $fileName -like "*.gguf" -or
            $fileName -like "ggml-*.bin" -or $fileName -like "ggml_*.bin" -or
            $fileName -like "*whisper-cli*" -or $fileName -like "*whisper-server*" -or
            $fileName -like "*.py" -or $fileName -like "*python*" -or
            $fileName -like "*cuda*" -or $fileName -like "*rocm*" -or
            $fileName -like "*openvino*")
        {
            throw "MSIX contains forbidden local-transcription payload '$relativePath'."
        }
    }

    return [pscustomobject]@{
        Present = $true
        WorkerFileCount = $workerFiles.Count
        NativeLibraryCount = $libraries.Count
    }
}

function Assert-PackagedMp3Composition
{
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$PlatformAssemblyEntry)

    $entryStream = $PlatformAssemblyEntry.Open()
    $memory = [IO.MemoryStream]::new()
    try
    {
        $entryStream.CopyTo($memory)
        $bytes = $memory.ToArray()
    }
    finally
    {
        $memory.Dispose()
        $entryStream.Dispose()
    }
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [Text.Encoding]::Unicode.GetString($bytes)
    $unicodeOffsetOne = if ($bytes.Length -gt 1)
    {
        [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    }
    else { "" }
    foreach ($required in @(
        "WindowsMediaFoundationMp3Encoder",
        "windows-media-foundation-mp3",
        "mp3_encoder_unavailable"))
    {
        if (-not $ascii.Contains($required, [StringComparison]::Ordinal) -and
            -not $unicode.Contains($required, [StringComparison]::Ordinal) -and
            -not $unicodeOffsetOne.Contains($required, [StringComparison]::Ordinal))
        {
            throw "Packaged Windows runtime is missing required MP3 composition symbol '$required'."
        }
    }
    foreach ($forbidden in @(
        "WindowsMediaFoundationAacEncoder",
        "windows-media-foundation-aac",
        "aac_encoder_unavailable"))
    {
        if ($ascii.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase) -or
            $unicode.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase) -or
            $unicodeOffsetOne.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Packaged Windows runtime retains forbidden AAC composition symbol '$forbidden'."
        }
    }
}

$resolved = Resolve-Path -LiteralPath $StoreSubmissionDirectory -ErrorAction Stop
$rootItem = Get-Item -LiteralPath $resolved.Path -Force
if (-not $rootItem.PSIsContainer) { throw "StoreSubmissionDirectory must be a directory." }
$submissionRoot = [IO.Path]::GetFullPath($rootItem.FullName)
Assert-NoReparsePointInPath -Path $submissionRoot

$directories = @(Get-ChildItem -LiteralPath $submissionRoot -Force -Directory)
if ($directories.Count -ne 0) { throw "Store submission exact allowlist permits no top-level directories." }
$files = @(Get-ChildItem -LiteralPath $submissionRoot -Force -File)
foreach ($file in $files)
{
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Store submission contains a reparse point '$($file.Name)'." }
    Assert-SafeTopLevelFileName -Name $file.Name
}

$msixFiles = @($files | Where-Object { $_.Extension -ieq ".msix" })
if ($msixFiles.Count -ne 1) { throw "Store submission must contain exactly one MSIX." }
$msix = $msixFiles[0]
if ($msix.Name -notmatch '^isTranscribe-[0-9A-Za-z.-]+-store-win-x64\.msix$' -or $msix.Name -match '(?i)(-dev-|development|self[-_]?signed|test[-_]?only)')
{
    throw "Store MSIX file name must use the exact '-store-win-x64.msix' handoff form."
}

$expectedFiles = @(
    $msix.Name,
    $StoreMarkerFileName,
    $StoreIdentityFileName,
    $ReleaseManifestFileName,
    $DependencyInventoryFileName,
    $ChecksumFileName)
$actualNames = @($files | ForEach-Object Name)
if ($actualNames.Count -ne $expectedFiles.Count) { throw "Store submission exact file allowlist requires six files." }
foreach ($name in $expectedFiles)
{
    if (@($actualNames | Where-Object { $_ -ceq $name }).Count -ne 1) { throw "Store submission is missing exact allowlisted file '$name'." }
}
foreach ($name in $actualNames)
{
    if ($expectedFiles -cnotcontains $name) { throw "Store submission contains non-allowlisted file '$name'." }
}

$forbiddenTopLevelNames = @(
    "DEVELOPMENT_ONLY.txt", "isTranscribe-development-certificate.cer",
    ".pfx", ".p12", ".pkcs12", ".pem", ".key", ".pvk", ".snk", ".jks", ".keystore",
    ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".cab", ".nupkg")
foreach ($forbidden in $forbiddenTopLevelNames)
{
    if ($actualNames -icontains $forbidden -or @($actualNames | Where-Object { $_.EndsWith($forbidden, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0)
    {
        throw "Store submission contains forbidden private-key, certificate, development or archive material '$forbidden'."
    }
}

$leases = [Collections.Generic.Dictionary[string, IO.FileStream]]::new([StringComparer]::Ordinal)
$archive = $null
try
{
    $checksumLease = [IO.File]::Open((Join-Path $submissionRoot $ChecksumFileName), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $leases.Add($ChecksumFileName, $checksumLease)
    $checksummedNames = @($expectedFiles | Where-Object { $_ -cne $ChecksumFileName })
    $checksums = Read-ChecksumManifest -Stream $checksumLease -ExpectedNames $checksummedNames
    foreach ($name in $checksummedNames)
    {
        $lease = [IO.File]::Open((Join-Path $submissionRoot $name), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $leases.Add($name, $lease)
        $actualHash = Get-StreamSha256 -Stream $lease
        Assert-TextEqual -Actual $actualHash -Expected $checksums[$name] -Field "SHA-256 for '$name'" -IgnoreCase
    }

    $markerText = Read-TextFromStream -Stream $leases[$StoreMarkerFileName] -Description $StoreMarkerFileName -MaximumBytes 16KB -AsciiOnly
    $markerLines = @(($markerText -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd([char]"`n").Split("`n"))
    if ($markerLines.Count -ne $ExpectedMarkerLines.Count) { throw "Store-only marker must contain the exact four-line warning." }
    for ($index = 0; $index -lt $ExpectedMarkerLines.Count; $index++)
    {
        Assert-TextEqual -Actual $markerLines[$index] -Expected $ExpectedMarkerLines[$index] -Field "Store-only marker line $($index + 1)"
    }

    $storeIdentityText = Read-TextFromStream -Stream $leases[$StoreIdentityFileName] -Description $StoreIdentityFileName -MaximumBytes $MaximumJsonBytes
    $storeIdentity = Read-StrictJsonText -Text $storeIdentityText -Description "Store identity"
    Assert-StoreIdentity -Identity $storeIdentity

    $inventoryText = Read-TextFromStream -Stream $leases[$DependencyInventoryFileName] -Description $DependencyInventoryFileName -MaximumBytes $MaximumJsonBytes
    $inventory = Read-StrictJsonText -Text $inventoryText -Description "Dependency inventory"

    $releaseText = Read-TextFromStream -Stream $leases[$ReleaseManifestFileName] -Description $ReleaseManifestFileName -MaximumBytes $MaximumJsonBytes
    $releaseManifest = Read-StrictJsonText -Text $releaseText -Description "Release manifest"
    $dependencyInventoryResult = Assert-DependencyInventory -Inventory $inventory -SemanticVersion ([string]$releaseManifest.semanticVersion)
    $releaseResult = Assert-ReleaseManifest -Manifest $releaseManifest -StoreIdentity $storeIdentity `
        -MsixFileName $msix.Name -MsixLease $leases[$msix.Name] -MsixHash $checksums[$msix.Name] `
        -InventoryHash $checksums[$DependencyInventoryFileName] -StoreIdentityHash $checksums[$StoreIdentityFileName] `
        -DependencyInventoryResult $dependencyInventoryResult -Inventory $inventory
    $expectedMsixName = "isTranscribe-$($releaseResult.SemanticVersion)-store-win-x64.msix"
    Assert-TextEqual -Actual $msix.Name -Expected $expectedMsixName -Field "Store MSIX file name"

    $signature = Get-AuthenticodeSignature -LiteralPath $msix.FullName
    Assert-TextEqual -Actual ([string]$signature.Status) -Expected "NotSigned" -Field "Unsigned Store Authenticode status"
    if ($null -ne $signature.SignerCertificate -or $null -ne $signature.TimeStamperCertificate)
    {
        throw "Unsigned Store MSIX unexpectedly exposes a signer or timestamper certificate."
    }

    $msixLease = $leases[$msix.Name]
    $msixLease.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($msixLease, [IO.Compression.ZipArchiveMode]::Read, $true, [Text.Encoding]::UTF8)
    if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt $MaximumEntryCount) { throw "MSIX ZIP entry count is invalid." }
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    $expandedBytes = 0L
    foreach ($entry in $archive.Entries)
    {
        if ($entry.FullName.Contains('\')) { throw "MSIX ZIP entry '$($entry.FullName)' uses a non-canonical separator." }
        $isDirectory = $entry.FullName.EndsWith('/', [StringComparison]::Ordinal)
        $rawName = if ($isDirectory) { $entry.FullName.TrimEnd('/') } else { $entry.FullName }
        $normalized = Normalize-PackagePath -Path $rawName -Description "MSIX ZIP entry '$($entry.FullName)'"
        if (-not $entries.TryAdd($normalized, $entry)) { throw "MSIX ZIP contains duplicate or case-aliased entry '$normalized'." }
        if ($isDirectory -and $entry.Length -ne 0) { throw "MSIX ZIP directory '$normalized' contains data." }
        if (($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000)
        {
            throw "MSIX ZIP contains a reparse point or symbolic link '$normalized'."
        }
        if (-not $isDirectory)
        {
            if ($entry.Length -gt ($MaximumExpandedBytes - $expandedBytes))
            {
                throw "MSIX expanded payload exceeds the safe verification limit."
            }
            $expandedBytes += [long]$entry.Length
            $extension = [IO.Path]::GetExtension($normalized)
            if ($extension -in @(
                    ".pdb", ".ps1", ".cmd", ".bat", ".pfx", ".p12", ".pkcs12", ".pem", ".key",
                    ".pvk", ".snk", ".jks", ".keystore", ".zip", ".7z", ".rar", ".tar", ".gz",
                    ".tgz", ".cab", ".nupkg", ".cer", ".crt", ".der", ".p7b", ".p7c", ".p7x") -or
                [IO.Path]::GetFileName($normalized) -ieq "AppxSignature.p7x" -or
                [IO.Path]::GetFileNameWithoutExtension($normalized) -match '(?i)^(?:ffmpeg|ffprobe|lame|libmp3lame|faac|fdkaac|fdk-aac)(?:[-_.].*)?$' -or
                $normalized -match '(?i)(private[-_. ]?key|signing[-_. ]?secret)')
            {
                throw "MSIX contains forbidden private-key, archive, script, debug or bundled codec payload '$normalized'."
            }
        }
    }
    if ($entries.ContainsKey("AppxSignature.p7x")) { throw "Unsigned Store submission must not contain AppxSignature.p7x." }
    foreach ($required in @("[Content_Types].xml", "AppxBlockMap.xml", "AppxManifest.xml") + $RequiredRuntimeFiles)
    {
        if (-not $entries.ContainsKey($required) -or $entries[$required].FullName -cne $required) { throw "Required exact MSIX metadata/runtime payload '$required' is missing." }
    }
    Assert-PackagedDependencyPayloadTrees -Inventory $inventory -Policy $dependencyInventoryResult.Policy -Entries $entries
    $noticeEntry = $entries["THIRD-PARTY-NOTICES.txt"]
    if ($noticeEntry.Length -ne [long]$releaseManifest.supplyChain.thirdPartyNoticeSizeBytes) { throw "Packaged notice size differs from release evidence." }
    $noticeStream = $noticeEntry.Open()
    try { $noticeHash = Get-StreamSha256 -Stream $noticeStream }
    finally { $noticeStream.Dispose() }
    Assert-TextEqual -Actual $noticeHash -Expected ([string]$releaseManifest.supplyChain.thirdPartyNoticeSha256) `
        -Field "Packaged third-party notice SHA-256" -IgnoreCase
    $noticeTextStream = $noticeEntry.Open()
    $noticeMemory = [IO.MemoryStream]::new()
    try
    {
        $noticeTextStream.CopyTo($noticeMemory)
        $noticeBytes = $noticeMemory.ToArray()
    }
    finally { $noticeMemory.Dispose(); $noticeTextStream.Dispose() }
    if ($noticeBytes.Length -eq 0 -or $noticeBytes[-1] -ne 0x0A -or
        ($noticeBytes.Length -ge 3 -and $noticeBytes[0] -eq 0xEF -and $noticeBytes[1] -eq 0xBB -and $noticeBytes[2] -eq 0xBF))
    {
        throw "Packaged third-party notice must be UTF-8 without BOM and end with LF."
    }
    try { $noticeText = [Text.UTF8Encoding]::new($false, $true).GetString($noticeBytes) }
    catch { throw "Packaged third-party notice is not strict UTF-8." }
    if ($noticeText.Contains("`r", [StringComparison]::Ordinal) -or
        -not $noticeText.Contains("Microsoft.Windows.SDK.NET.Ref/10.0.26100.84", [StringComparison]::Ordinal) -or
        -not $noticeText.Contains("SIL OPEN FONT LICENSE Version 1.1", [StringComparison]::Ordinal))
    {
        throw "Packaged third-party notice is not deterministic LF text or lacks required SDK/Inter evidence."
    }
    $localTranscriptionResult = Assert-LocalTranscriptionPayload -Entries $entries -NoticeText $noticeText
    Assert-PackagedMp3Composition -PlatformAssemblyEntry $entries["IsTranscribe.Platform.Windows.dll"]

    foreach ($entry in $entries.Values)
    {
        if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) { continue }
        $stream = $entry.Open()
        try
        {
            $buffer = [byte[]]::new(81920)
            $readTotal = 0L
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $readTotal += $read }
            if ($readTotal -ne $entry.Length) { throw "MSIX ZIP entry '$($entry.FullName)' decompressed length mismatch." }
        }
        finally { $stream.Dispose() }
    }

    [void](Read-ZipEntryXml -Entry $entries["[Content_Types].xml"] -Description "MSIX content types")
    $manifestXml = Read-ZipEntryXml -Entry $entries["AppxManifest.xml"] -Description "AppxManifest"
    Assert-MsixManifest -Manifest $manifestXml -ReleaseManifest $releaseManifest -StoreIdentity $storeIdentity -Entries $entries
    $blockMapXml = Read-ZipEntryXml -Entry $entries["AppxBlockMap.xml"] -Description "AppxBlockMap"
    $blockMapResult = Assert-AppxBlockMap -BlockMap $blockMapXml -Entries $entries

    $depsText = Read-ZipEntryText -Entry $entries["IsTranscribe.Desktop.deps.json"] -Description "Packaged deps.json" -MaximumBytes $MaximumJsonBytes
    $deps = Read-StrictJsonText -Text $depsText -Description "Packaged deps.json"
    $runtimeTarget = [string]$deps.runtimeTarget.name
    if (-not $runtimeTarget.EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase)) { throw "Packaged deps.json does not target win-x64." }
    $expectedRuntimeLibrary = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$($releaseManifest.build.dotnetRuntimePackVersion)"
    $runtimeLibraries = @($deps.libraries.PSObject.Properties.Name | Where-Object {
        $_ -ceq $expectedRuntimeLibrary
    })
    if ($runtimeLibraries.Count -ne 1) { throw "Packaged deps.json does not prove the pinned self-contained win-x64 runtime pack." }
    $runtimeConfigText = Read-ZipEntryText -Entry $entries["IsTranscribe.Desktop.runtimeconfig.json"] -Description "Packaged runtimeconfig.json" -MaximumBytes $MaximumJsonBytes
    $runtimeConfig = Read-StrictJsonText -Text $runtimeConfigText -Description "Packaged runtimeconfig.json"
    Assert-NonEmptyResolvedText -Value $runtimeConfig.runtimeOptions.tfm -Field "Packaged runtimeconfig TFM"

    $result = [ordered]@{
        schemaVersion = "infra-005-store-submission-verification-v1"
        status = "passed"
        storeSubmissionDirectory = $submissionRoot
        semanticVersion = $releaseResult.SemanticVersion
        storeIdentity = [ordered]@{
            identityName = $storeIdentity.identityName
            publisher = $storeIdentity.publisher
            packageFamilyName = $storeIdentity.packageFamilyName
            applicationId = $storeIdentity.applicationId
        }
        msix = [ordered]@{
            file = $msix.Name
            sizeBytes = $msixLease.Length
            sha256 = $checksums[$msix.Name]
            authenticodeStatus = "NotSigned"
            zipEntryCount = $entries.Count
            expandedBytes = $expandedBytes
            blockMapFileCount = $blockMapResult.FileCount
            verifiedBlockCount = $blockMapResult.BlockCount
        }
        supplyChain = [ordered]@{
            checksummedFileCount = $checksums.Count
            dependencyCount = $dependencyInventoryResult.Count
            redistributedDependencyCount = $dependencyInventoryResult.RedistributedCount
            missingLicenseMetadataCount = 0
            storeSigningRequired = $true
            submissionEligible = $true
        }
    }
}
finally
{
    if ($null -ne $archive) { $archive.Dispose() }
    foreach ($lease in $leases.Values) { $lease.Dispose() }
}

$result | ConvertTo-Json -Depth 6 -Compress
