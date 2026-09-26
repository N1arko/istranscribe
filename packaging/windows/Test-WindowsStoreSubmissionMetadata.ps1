#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MetadataPath,
    [Parameter(Mandatory)][string]$StoreReleaseDirectory,
    [string]$AssetsRoot,
    [switch]$AllowTestFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
# @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
$ExpectedSchema = "infra-005-store-submission-metadata-v1"
$ExpectedArtifactEvidenceSchema = "infra-005-store-submission-verification-v1"
$ExpectedLanguages = @("ru-RU", "en-US")
$ExpectedScreenshotNames = @("suspected.png", "recording.png", "ready.png", "ask.png")
$MaximumMetadataBytes = 4MB
$MaximumScreenshotBytes = 50MB

function Assert-Condition
{
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
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

function Assert-ExactProperties
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Description)

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) { throw "$Description must contain the exact property allowlist." }
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

function Read-StrictJsonLease
{
    param(
        [Parameter(Mandatory)][IO.FileStream]$Lease,
        [Parameter(Mandatory)][string]$Description,
        [long]$MaximumBytes = $MaximumMetadataBytes)

    if ($Lease.Length -le 0 -or $Lease.Length -gt $MaximumBytes) { throw "$Description size is outside the safe limit." }
    $Lease.Position = 0
    $reader = [IO.StreamReader]::new($Lease, [Text.UTF8Encoding]::new($false, $true), $true, 4096, $true)
    try { $text = $reader.ReadToEnd() }
    finally { $reader.Dispose(); $Lease.Position = 0 }
    return [pscustomobject]@{
        Text = $text
        Value = Read-StrictJsonText -Text $text -Description $Description
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
                throw "Store metadata paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-ResolvedText
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [int]$MaximumLength = 10000,
        [switch]$AllowMultiline,
        [switch]$Fixture)

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Value))
    {
        throw "$Field must be a non-empty JSON string."
    }
    $text = [string]$Value
    $invalidControlPattern = if ($AllowMultiline) { '[\x00-\x08\x0B\x0C\x0E-\x1F]' } else { '[\x00-\x1F]' }
    if ($text.Length -gt $MaximumLength -or $text -cne $text.Trim() -or $text -match $invalidControlPattern -or
        $text -match '(?i)(<[^>]+>|\{\{[^}]+\}\}|__[^_]+__|TODO|CHANGEME|REPLACE[_ -]?ME)')
    {
        throw "$Field contains invalid length, whitespace, control characters or an unresolved placeholder."
    }
    if (-not $Fixture -and ($text -match '(?i)\bTEST(?:[-_: ]|$)' -or $text -match '(?i)(?:^|\.)example\.invalid$'))
    {
        throw "$Field contains fixture-only TEST/example.invalid metadata."
    }
}

function Assert-StringArray
{
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [int]$MinimumCount = 0,
        [int]$MaximumCount = 100,
        [int]$MaximumItemLength = 200,
        [switch]$Fixture)

    if ($Value -isnot [System.Array]) { throw "$Field must be a JSON array." }
    $items = @($Value)
    if ($items.Count -lt $MinimumCount -or $items.Count -gt $MaximumCount)
    {
        throw "$Field must contain $MinimumCount..$MaximumCount items."
    }
    foreach ($item in $items)
    {
        Assert-ResolvedText -Value $item -Field "$Field item" -MaximumLength $MaximumItemLength -Fixture:$Fixture
    }
    return ,$items
}

function Assert-HttpsUrl
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Field,
        [switch]$Fixture)

    Assert-ResolvedText -Value $Value -Field $Field -MaximumLength 2048 -Fixture:$Fixture
    $uri = $null
    if (-not [Uri]::TryCreate([string]$Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo))
    {
        throw "$Field must be an absolute HTTPS URL without user information."
    }
    if (-not $Fixture -and $uri.Host -ieq 'example.invalid') { throw "$Field uses a fixture-only host." }
}

function Assert-SupportContact
{
    param([Parameter(Mandatory)][object]$Value, [switch]$Fixture)

    Assert-ResolvedText -Value $Value -Field "properties.supportContact" -MaximumLength 2048 -Fixture:$Fixture
    if ([string]$Value -match '^https://')
    {
        Assert-HttpsUrl -Value $Value -Field "properties.supportContact" -Fixture:$Fixture
        return
    }
    try { $address = [Net.Mail.MailAddress]::new([string]$Value) }
    catch { throw "properties.supportContact must be an HTTPS URL or email address." }
    if ($address.Address -cne [string]$Value -or (-not $Fixture -and $address.Host -ieq 'example.invalid'))
    {
        throw "properties.supportContact email is non-canonical or fixture-only."
    }
}

function Resolve-SafeScreenshot
{
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Field,
        [switch]$Fixture)

    Assert-ResolvedText -Value $Value -Field $Field -MaximumLength 240 -Fixture:$Fixture
    $relative = [string]$Value
    if ($relative.Contains('\') -or [IO.Path]::IsPathRooted($relative) -or $relative -notmatch '^[A-Za-z0-9._/-]+\.png$')
    {
        throw "$Field must be a safe relative PNG path with forward slashes."
    }
    $segments = @($relative.Split('/'))
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0)
    {
        throw "$Field contains an unsafe path segment."
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Field escapes AssetsRoot." }
    Assert-NoReparsePointInPath -Path $candidate
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "$Field does not resolve to a regular file." }
    $item = Get-Item -LiteralPath $candidate -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0 -or $item.Length -gt $MaximumScreenshotBytes)
    {
        throw "$Field must be a non-reparse PNG of at most 50 MB."
    }
    $stream = [IO.File]::Open($candidate, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try
    {
        if ($stream.Length -lt 24) { throw "$Field PNG is truncated." }
        $header = [byte[]]::new(24)
        $offset = 0
        while ($offset -lt $header.Length)
        {
            $read = $stream.Read($header, $offset, $header.Length - $offset)
            if ($read -le 0) { throw "$Field PNG is truncated." }
            $offset += $read
        }
    }
    finally { $stream.Dispose() }
    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Count; $index++)
    {
        if ($header[$index] -ne $signature[$index]) { throw "$Field is not a PNG." }
    }
    $width = [int]((([int]$header[16]) -shl 24) -bor (([int]$header[17]) -shl 16) -bor (([int]$header[18]) -shl 8) -bor ([int]$header[19]))
    $height = [int]((([int]$header[20]) -shl 24) -bor (([int]$header[21]) -shl 16) -bor (([int]$header[22]) -shl 8) -bor ([int]$header[23]))
    $landscape = $width -ge 1366 -and $height -ge 768
    $portrait = $width -ge 768 -and $height -ge 1366
    if (-not ($landscape -or $portrait)) { throw "$Field dimensions must be at least 1366x768 or 768x1366." }
    return [pscustomobject]@{ Path = $relative; Width = $width; Height = $height; SizeBytes = $item.Length }
}

function Get-FileSha256
{
    param([Parameter(Mandatory)][IO.FileStream]$Stream)
    $Stream.Position = 0
    try { return (Get-FileHash -InputStream $Stream -Algorithm SHA256).Hash.ToLowerInvariant() }
    finally { $Stream.Position = 0 }
}

$resolvedMetadata = Resolve-Path -LiteralPath $MetadataPath -ErrorAction Stop
$metadataItem = Get-Item -LiteralPath $resolvedMetadata.Path -Force
if ($metadataItem.PSIsContainer -or $metadataItem.Extension -cne '.json') { throw "MetadataPath must be a JSON file." }
$metadataFullPath = [IO.Path]::GetFullPath($metadataItem.FullName)
Assert-NoReparsePointInPath -Path $metadataFullPath

$assetsFullPath = $null

$releaseFullPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $StoreReleaseDirectory -ErrorAction Stop).Path)
if (-not (Test-Path -LiteralPath $releaseFullPath -PathType Container)) { throw "StoreReleaseDirectory must be a directory." }
Assert-NoReparsePointInPath -Path $releaseFullPath
$storeIdentityPath = Join-Path $releaseFullPath "store-identity.json"
Assert-NoReparsePointInPath -Path $storeIdentityPath
if (-not (Test-Path -LiteralPath $storeIdentityPath -PathType Leaf)) { throw "Store release identity is missing." }

$metadataLease = $null
$identityLease = $null
$msixLease = $null
try
{
    $metadataLease = [IO.File]::Open($metadataFullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $metadataRead = Read-StrictJsonLease -Lease $metadataLease -Description "Store submission metadata"
    $metadata = $metadataRead.Value

    Assert-ExactProperties -Value $metadata -Description "Store submission metadata" -Expected @(
        "schemaVersion", "configured", "fixture", "product", "package", "pricingAvailability", "properties",
        "privacy", "ageRatings", "systemRequirements", "listings", "submissionOptions", "review")
    Assert-TextEqual -Actual ([string]$metadata.schemaVersion) -Expected $ExpectedSchema -Field "Metadata schemaVersion"
    Assert-JsonBoolean -Value $metadata.configured -Expected $true -Field "Metadata configured"
    if ($metadata.fixture -isnot [bool]) { throw "Metadata fixture must be a JSON boolean." }
    $isFixture = [bool]$metadata.fixture
    if ($isFixture -and -not $AllowTestFixture) { throw "Fixture metadata requires explicit -AllowTestFixture." }
    if (-not $isFixture -and $AllowTestFixture) { throw "-AllowTestFixture may be used only with fixture=true metadata." }

    $assetsFullPath = if ([string]::IsNullOrWhiteSpace($AssetsRoot))
    {
        [IO.Path]::GetDirectoryName($metadataFullPath)
    }
    else
    {
        [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AssetsRoot -ErrorAction Stop).Path)
    }
    if (-not (Test-Path -LiteralPath $assetsFullPath -PathType Container)) { throw "AssetsRoot must be a directory." }
    Assert-NoReparsePointInPath -Path $assetsFullPath

    $identityLease = [IO.File]::Open($storeIdentityPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $storeIdentity = (Read-StrictJsonLease -Lease $identityLease -Description "Store release identity").Value

    $artifactVerifier = Join-Path $PSScriptRoot "Test-WindowsStoreSubmissionArtifact.ps1"
    Assert-NoReparsePointInPath -Path $artifactVerifier
    if (-not (Test-Path -LiteralPath $artifactVerifier -PathType Leaf)) { throw "Store artifact verifier is missing." }
    try { $artifactOutput = @(& $artifactVerifier -ReleaseDirectory $releaseFullPath) }
    catch { throw "Store artifact verification failed: $($_.Exception.Message)" }
    if ($artifactOutput.Count -ne 1) { throw "Store artifact verifier returned non-canonical output." }
    $artifactEvidence = Read-StrictJsonText -Text ([string]$artifactOutput[0]) -Description "Store artifact verification evidence"
    Assert-TextEqual -Actual ([string]$artifactEvidence.schemaVersion) -Expected $ExpectedArtifactEvidenceSchema -Field "Artifact evidence schemaVersion"
    Assert-TextEqual -Actual ([string]$artifactEvidence.status) -Expected "passed" -Field "Artifact evidence status"

    $msixName = [string]$artifactEvidence.msix.file
    if ($msixName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*-store-win-x64\.msix$' -or [IO.Path]::GetFileName($msixName) -cne $msixName)
    {
        throw "Artifact evidence MSIX file name is unsafe."
    }
    $msixPath = Join-Path $releaseFullPath $msixName
    Assert-NoReparsePointInPath -Path $msixPath
    $msixLease = [IO.File]::Open($msixPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Assert-TextEqual -Actual (Get-FileSha256 -Stream $msixLease) -Expected ([string]$artifactEvidence.msix.sha256) `
        -Field "Current Store MSIX SHA-256" -IgnoreCase

    Assert-ExactProperties -Value $metadata.product -Description "Metadata product" -Expected @("name", "publisherDisplayName")
    Assert-ResolvedText -Value $metadata.product.name -Field "product.name" -MaximumLength 256 -Fixture:$isFixture
    Assert-ResolvedText -Value $metadata.product.publisherDisplayName -Field "product.publisherDisplayName" -MaximumLength 256 -Fixture:$isFixture
    Assert-TextEqual -Actual ([string]$metadata.product.name) -Expected ([string]$storeIdentity.reservedProductName) -Field "Metadata/Store product name"
    Assert-TextEqual -Actual ([string]$metadata.product.publisherDisplayName) -Expected ([string]$storeIdentity.publisherDisplayName) -Field "Metadata/Store publisher display name"

    Assert-ExactProperties -Value $metadata.package -Description "Metadata package" -Expected @("semanticVersion", "sha256")
    Assert-TextEqual -Actual ([string]$metadata.package.semanticVersion) -Expected ([string]$artifactEvidence.semanticVersion) -Field "Metadata/artifact semanticVersion"
    if ([string]$metadata.package.sha256 -notmatch '^[0-9a-f]{64}$') { throw "package.sha256 must be canonical lowercase SHA-256." }
    Assert-TextEqual -Actual ([string]$metadata.package.sha256) -Expected ([string]$artifactEvidence.msix.sha256) -Field "Metadata/artifact SHA-256"

    Assert-ExactProperties -Value $metadata.pricingAvailability -Description "Metadata pricingAvailability" -Expected @(
        "basePrice", "markets", "audience", "discoverability", "publishingHold")
    foreach ($pair in @(
        @("basePrice", "Free"), @("markets", "all"), @("audience", "private"),
        @("discoverability", "direct-link"), @("publishingHold", "manual")))
    {
        Assert-TextEqual -Actual ([string]$metadata.pricingAvailability.($pair[0])) -Expected $pair[1] -Field "pricingAvailability.$($pair[0])"
    }

    Assert-ExactProperties -Value $metadata.properties -Description "Metadata properties" -Expected @(
        "category", "personalInformationAccessed", "privacyPolicyUrl", "websiteUrl", "supportContact")
    Assert-TextEqual -Actual ([string]$metadata.properties.category) -Expected "Productivity" -Field "properties.category"
    Assert-JsonBoolean -Value $metadata.properties.personalInformationAccessed -Expected $true -Field "properties.personalInformationAccessed"
    Assert-HttpsUrl -Value $metadata.properties.privacyPolicyUrl -Field "properties.privacyPolicyUrl" -Fixture:$isFixture
    Assert-HttpsUrl -Value $metadata.properties.websiteUrl -Field "properties.websiteUrl" -Fixture:$isFixture
    Assert-SupportContact -Value $metadata.properties.supportContact -Fixture:$isFixture

    Assert-ExactProperties -Value $metadata.privacy -Description "Metadata privacy" -Expected @(
        "microphoneAudio", "systemAudio", "localStorage", "transcriptionEnabled", "externalAudioTransfer",
        "telemetryEnabled", "retention", "deletion")
    foreach ($field in @("microphoneAudio", "systemAudio", "localStorage"))
    {
        Assert-JsonBoolean -Value $metadata.privacy.$field -Expected $true -Field "privacy.$field"
    }
    foreach ($field in @("transcriptionEnabled", "externalAudioTransfer"))
    {
        Assert-JsonBoolean -Value $metadata.privacy.$field -Expected $true -Field "privacy.$field"
    }
    Assert-JsonBoolean -Value $metadata.privacy.telemetryEnabled -Expected $false -Field "privacy.telemetryEnabled"
    Assert-ResolvedText -Value $metadata.privacy.retention -Field "privacy.retention" -MaximumLength 2000 -AllowMultiline -Fixture:$isFixture
    Assert-ResolvedText -Value $metadata.privacy.deletion -Field "privacy.deletion" -MaximumLength 2000 -AllowMultiline -Fixture:$isFixture

    Assert-ExactProperties -Value $metadata.ageRatings -Description "Metadata ageRatings" -Expected @("questionnaireCompleted", "reference")
    Assert-JsonBoolean -Value $metadata.ageRatings.questionnaireCompleted -Expected $true -Field "ageRatings.questionnaireCompleted"
    Assert-ResolvedText -Value $metadata.ageRatings.reference -Field "ageRatings.reference" -MaximumLength 512 -Fixture:$isFixture
    if (([string]$metadata.ageRatings.reference).Length -lt 8) { throw "ageRatings.reference is too short to be an actionable reference." }

    Assert-ExactProperties -Value $metadata.systemRequirements -Description "Metadata systemRequirements" -Expected @(
        "runtimeIdentifier", "minimumWindowsVersion", "architecture", "additionalMinimumHardware", "additionalRecommendedHardware")
    Assert-TextEqual -Actual ([string]$metadata.systemRequirements.runtimeIdentifier) -Expected "win-x64" -Field "systemRequirements.runtimeIdentifier"
    Assert-TextEqual -Actual ([string]$metadata.systemRequirements.minimumWindowsVersion) -Expected "10.0.19041.0" -Field "systemRequirements.minimumWindowsVersion"
    Assert-TextEqual -Actual ([string]$metadata.systemRequirements.architecture) -Expected "x64" -Field "systemRequirements.architecture" -IgnoreCase
    [void](Assert-StringArray -Value $metadata.systemRequirements.additionalMinimumHardware -Field "systemRequirements.additionalMinimumHardware" `
        -MaximumCount 11 -MaximumItemLength 200 -Fixture:$isFixture)
    [void](Assert-StringArray -Value $metadata.systemRequirements.additionalRecommendedHardware -Field "systemRequirements.additionalRecommendedHardware" `
        -MaximumCount 11 -MaximumItemLength 200 -Fixture:$isFixture)

    if ($metadata.listings -isnot [System.Array]) { throw "listings must be a JSON array." }
    $listings = @($metadata.listings)
    if ($listings.Count -ne 2) { throw "listings must contain exactly ru-RU and en-US." }
    $seenLanguages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allScreenshotPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verifiedScreenshots = [Collections.Generic.List[object]]::new()
    foreach ($listing in $listings)
    {
        Assert-ExactProperties -Value $listing -Description "Store listing" -Expected @(
            "language", "productName", "description", "shortDescription", "features", "keywords", "screenshots")
        if (-not $seenLanguages.Add([string]$listing.language) -or $ExpectedLanguages -cnotcontains [string]$listing.language)
        {
            throw "Listing language must be one unique ru-RU or en-US entry."
        }
        Assert-TextEqual -Actual ([string]$listing.productName) -Expected ([string]$metadata.product.name) -Field "Listing productName"
        Assert-ResolvedText -Value $listing.description -Field "$($listing.language) description" -MaximumLength 10000 -AllowMultiline -Fixture:$isFixture
        Assert-ResolvedText -Value $listing.shortDescription -Field "$($listing.language) shortDescription" -MaximumLength 270 -Fixture:$isFixture

        $features = Assert-StringArray -Value $listing.features -Field "$($listing.language) features" `
            -MinimumCount 1 -MaximumCount 20 -MaximumItemLength 200 -Fixture:$isFixture
        $featureSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($feature in $features)
        {
            if ([string]$feature -match '^\s*(?:[-*+•‣▪◦–—]|\d+[.)])\s*') { throw "Listing feature must not begin with a bullet or list number." }
            if (-not $featureSet.Add([string]$feature)) { throw "Listing features contain a duplicate." }
        }

        $keywords = Assert-StringArray -Value $listing.keywords -Field "$($listing.language) keywords" `
            -MinimumCount 1 -MaximumCount 7 -MaximumItemLength 40 -Fixture:$isFixture
        $keywordSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $keywordWords = 0
        foreach ($keyword in $keywords)
        {
            if ([string]$keyword -match '[,;]' -or -not $keywordSet.Add([string]$keyword)) { throw "Listing keywords must be unique individual entries." }
            $wordCount = [regex]::Matches([string]$keyword, "[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*").Count
            if ($wordCount -lt 1) { throw "Listing keyword contains no words." }
            $keywordWords += $wordCount
        }
        if ($keywordWords -gt 21) { throw "Listing keywords exceed the 21-word limit." }

        $screenshotPaths = Assert-StringArray -Value $listing.screenshots -Field "$($listing.language) screenshots" `
            -MinimumCount 4 -MaximumCount 4 -MaximumItemLength 240 -Fixture:$isFixture
        $screenshotSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expectedScreenshotPaths = @($ExpectedScreenshotNames | ForEach-Object { "$($listing.language)/$_" })
        foreach ($screenshotPath in $screenshotPaths)
        {
            if (-not $screenshotSet.Add([string]$screenshotPath) -or -not $allScreenshotPaths.Add([string]$screenshotPath))
            {
                throw "Listing screenshots contain a duplicate path."
            }
            if ($expectedScreenshotPaths -cnotcontains [string]$screenshotPath)
            {
                throw "Listing screenshots must reference the exact four localized capture states."
            }
            $screenshot = Resolve-SafeScreenshot -Value $screenshotPath -Root $assetsFullPath `
                -Field "$($listing.language) screenshot" -Fixture:$isFixture
            $verifiedScreenshots.Add($screenshot)
        }
    }
    foreach ($language in $ExpectedLanguages)
    {
        if (-not $seenLanguages.Contains($language)) { throw "Listing '$language' is missing." }
    }
    if ($allScreenshotPaths.Count -ne 8 -or $verifiedScreenshots.Count -ne 8)
    {
        throw "Store metadata must bind exactly eight unique localized screenshots."
    }

    Assert-ExactProperties -Value $metadata.submissionOptions -Description "Metadata submissionOptions" -Expected @(
        "notesForCertification", "restrictedCapabilities")
    Assert-ResolvedText -Value $metadata.submissionOptions.notesForCertification -Field "submissionOptions.notesForCertification" `
        -MaximumLength 10000 -AllowMultiline -Fixture:$isFixture
    if (([string]$metadata.submissionOptions.notesForCertification).Length -lt 80)
    {
        throw "Certification notes must provide actionable test instructions."
    }
    if ($metadata.submissionOptions.restrictedCapabilities -isnot [System.Array])
    {
        throw "submissionOptions.restrictedCapabilities must be a JSON array."
    }
    $capabilities = @($metadata.submissionOptions.restrictedCapabilities)
    if ($capabilities.Count -ne 1) { throw "Submission options must contain exactly one runFullTrust capability justification." }
    $capability = $capabilities[0]
    Assert-ExactProperties -Value $capability -Description "Restricted capability" -Expected @("name", "justification")
    Assert-TextEqual -Actual ([string]$capability.name) -Expected "runFullTrust" -Field "Restricted capability name"
    Assert-ResolvedText -Value $capability.justification -Field "runFullTrust justification" -MaximumLength 4000 -AllowMultiline -Fixture:$isFixture
    if ([string]$capability.justification -match '(?i)unvirtualizedResources' -or
        ([string]$capability.justification).Length -lt 80 -or
        [string]$capability.justification -notmatch '(?i)audio' -or
        [string]$capability.justification -notmatch '(?i)consent')
    {
        throw "runFullTrust justification must explain audio/consent usage and must not request unvirtualizedResources."
    }

    Assert-ExactProperties -Value $metadata.review -Description "Metadata review" -Expected @(
        "accountType", "partnerCenterProductId", "storeId", "privacyPolicyReviewed", "metadataReviewed", "reviewedBy", "reviewedUtc")
    if ([string]$metadata.review.accountType -cnotin @("Individual", "Company")) { throw "review.accountType must be Individual or Company." }
    foreach ($field in @("partnerCenterProductId", "storeId", "reviewedBy"))
    {
        Assert-ResolvedText -Value $metadata.review.$field -Field "review.$field" -MaximumLength 512 -Fixture:$isFixture
    }
    Assert-JsonBoolean -Value $metadata.review.privacyPolicyReviewed -Expected $true -Field "review.privacyPolicyReviewed"
    Assert-JsonBoolean -Value $metadata.review.metadataReviewed -Expected $true -Field "review.metadataReviewed"
    try { $reviewedUtc = [DateTimeOffset]::Parse([string]$metadata.review.reviewedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) }
    catch { throw "review.reviewedUtc must be a round-trip timestamp." }
    if ($reviewedUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or $reviewedUtc.Year -lt 2020)
    {
        throw "review.reviewedUtc is outside the credible review window."
    }

    $result = [ordered]@{
        schemaVersion = "infra-005-store-submission-metadata-verification-v1"
        status = "passed"
        metadataPath = $metadataFullPath
        fixture = $isFixture
        artifact = [ordered]@{
            semanticVersion = $artifactEvidence.semanticVersion
            msixFile = $artifactEvidence.msix.file
            sha256 = $artifactEvidence.msix.sha256
            productName = $metadata.product.name
        }
        pricingAvailability = [ordered]@{
            basePrice = "Free"
            markets = "all"
            audience = "private"
            discoverability = "direct-link"
            publishingHold = "manual"
        }
        listings = [ordered]@{
            languages = $ExpectedLanguages
            screenshotCount = $verifiedScreenshots.Count
        }
        restrictedCapabilities = @("runFullTrust")
        systemRequirements = [ordered]@{
            runtimeIdentifier = "win-x64"
            minimumWindowsVersion = "10.0.19041.0"
            architecture = "x64"
        }
    }
}
finally
{
    if ($null -ne $msixLease) { $msixLease.Dispose() }
    if ($null -ne $identityLease) { $identityLease.Dispose() }
    if ($null -ne $metadataLease) { $metadataLease.Dispose() }
}

$result | ConvertTo-Json -Depth 6 -Compress
