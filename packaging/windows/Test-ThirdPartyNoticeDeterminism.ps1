#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StoreSubmissionDirectory,
    [string]$EvidenceOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#verification

function Write-Utf8NoBom
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent))
    {
        [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($parent)) | Out-Null
    }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path), $Text, [Text.UTF8Encoding]::new($false))
}

function Write-Json
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object]$Value)
    Write-Utf8NoBom -Path $Path -Text ($Value | ConvertTo-Json -Depth 20)
}

function Get-LowerSha256
{
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-BaseInventory
{
    param([Parameter(Mandatory)][object]$Inventory)

    [ordered]@{
        schemaVersion = "infra-005-dependencies-v1"
        product = [string]$Inventory.product
        semanticVersion = [string]$Inventory.semanticVersion
        generatedUtc = [string]$Inventory.generatedUtc
        packageCount = [int]$Inventory.packageCount
        missingLicenseMetadataCount = [int]$Inventory.missingLicenseMetadataCount
        dependencies = @($Inventory.dependencies)
    }
}

function Invoke-Generator
{
    param(
        [Parameter(Mandatory)][string]$CaseRoot,
        [Parameter(Mandatory)][string]$BaseInventoryPath,
        [Parameter(Mandatory)][string]$PublishedDepsPath,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$PolicyPath)

    [IO.Directory]::CreateDirectory($CaseRoot) | Out-Null
    $nativeInventoryPath = Join-Path $PublishRoot "native\runtime-inventory.v1.json"
    $nativeManifestPath = Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"
    if (-not (Test-Path -LiteralPath $nativeInventoryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $nativeManifestPath -PathType Leaf))
    {
        throw "Determinism verification requires the packaged native runtime inventory and checked-in manifest."
    }
    & (Join-Path $PSScriptRoot "New-ThirdPartyNotices.ps1") `
        -BaseInventoryPath $BaseInventoryPath `
        -PublishedDepsPath $PublishedDepsPath `
        -PublishRoot $PublishRoot `
        -PolicyPath $PolicyPath `
        -NativeRuntimeInventoryPath $nativeInventoryPath `
        -NativeRuntimeManifestPath $nativeManifestPath `
        -InventoryOutputPath (Join-Path $CaseRoot "dependency-license-inventory.json") `
        -NoticeOutputPath (Join-Path $CaseRoot "THIRD-PARTY-NOTICES.txt") `
        -ResultOutputPath (Join-Path $CaseRoot "result.json") | Out-Null
}

function Assert-FailsClosed
{
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Mutation,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][object]$BaseInventory,
        [Parameter(Mandatory)][string]$PublishedDepsPath,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$NoticesSource)

    $caseRoot = Join-Path $Root "mutation-$Name"
    $noticesRoot = Join-Path $caseRoot "notices"
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    Copy-Item -LiteralPath $NoticesSource -Destination $noticesRoot -Recurse
    $inventory = $BaseInventory | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $policyPath = Join-Path $noticesRoot "third-party-notice-policy.json"
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    & $Mutation $inventory $policy $noticesRoot
    $basePath = Join-Path $caseRoot "base-inventory.json"
    Write-Json -Path $basePath -Value $inventory
    Write-Json -Path $policyPath -Value $policy

    try
    {
        Invoke-Generator `
            -CaseRoot (Join-Path $caseRoot "output") `
            -BaseInventoryPath $basePath `
            -PublishedDepsPath $PublishedDepsPath `
            -PublishRoot $PublishRoot `
            -PolicyPath $policyPath
    }
    catch
    {
        return [pscustomobject]@{ name = $Name; status = "passed"; rejectedWith = $_.Exception.Message }
    }
    throw "Mutation '$Name' did not fail closed."
}

$resolvedRelease = Resolve-Path -LiteralPath $StoreSubmissionDirectory -ErrorAction Stop
$releaseItem = Get-Item -LiteralPath $resolvedRelease.Path -Force
if (-not $releaseItem.PSIsContainer) { throw "StoreSubmissionDirectory must be a directory." }
$msixFiles = @(Get-ChildItem -LiteralPath $releaseItem.FullName -Filter "*.msix" -File)
if ($msixFiles.Count -ne 1) { throw "Store submission must contain exactly one MSIX." }
$inventoryPath = Join-Path $releaseItem.FullName "dependency-license-inventory.json"
$inventoryV2 = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if ([string]$inventoryV2.schemaVersion -cne "infra-005-dependencies-v2") { throw "Release inventory must be v2." }

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempParent ("isTranscribe-notice-test-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try
{
    $publishRoot = Join-Path $tempRoot "publish"
    [IO.Compression.ZipFile]::ExtractToDirectory($msixFiles[0].FullName, $publishRoot)
    $publishedDepsPath = Join-Path $publishRoot "IsTranscribe.Desktop.deps.json"
    $baseInventory = New-BaseInventory -Inventory $inventoryV2
    $baseInventoryPath = Join-Path $tempRoot "base-inventory.json"
    Write-Json -Path $baseInventoryPath -Value $baseInventory
    $noticesSource = Join-Path $PSScriptRoot "notices"
    $policyPath = Join-Path $noticesSource "third-party-notice-policy.json"

    $firstRoot = Join-Path $tempRoot "first"
    $secondRoot = Join-Path $tempRoot "second"
    Invoke-Generator -CaseRoot $firstRoot -BaseInventoryPath $baseInventoryPath `
        -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -PolicyPath $policyPath
    Invoke-Generator -CaseRoot $secondRoot -BaseInventoryPath $baseInventoryPath `
        -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -PolicyPath $policyPath

    $firstNotice = Join-Path $firstRoot "THIRD-PARTY-NOTICES.txt"
    $secondNotice = Join-Path $secondRoot "THIRD-PARTY-NOTICES.txt"
    $firstInventory = Join-Path $firstRoot "dependency-license-inventory.json"
    $secondInventory = Join-Path $secondRoot "dependency-license-inventory.json"
    $noticeHash = Get-LowerSha256 -Path $firstNotice
    $inventoryHash = Get-LowerSha256 -Path $firstInventory
    if ($noticeHash -cne (Get-LowerSha256 -Path $secondNotice) -or
        $inventoryHash -cne (Get-LowerSha256 -Path $secondInventory))
    {
        throw "Identical inputs did not produce byte-identical notice evidence."
    }
    if ($noticeHash -cne [string]$inventoryV2.notice.sha256)
    {
        throw "Regenerated notice differs from the packaged release notice."
    }

    $mutationResults = @(
        Assert-FailsClosed -Name "package-hash" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation { param($inventory, $policy, $notices) $inventory.dependencies[0].contentHashSha512 = "sha512-AAAAAAAA" }
        Assert-FailsClosed -Name "asset-list" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation { param($inventory, $policy, $notices) $policy.packages[0].assetListSha256 = ("0" * 64) }
        Assert-FailsClosed -Name "notice-source" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation { param($inventory, $policy, $notices) [IO.File]::AppendAllText((Join-Path $notices "licenses\MIT.txt"), "changed") }
        Assert-FailsClosed -Name "license-expression" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation { param($inventory, $policy, $notices) $policy.canonicalLicenses.PSObject.Properties.Remove("MIT") }
        Assert-FailsClosed -Name "override" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation { param($inventory, $policy, $notices) [IO.File]::AppendAllText((Join-Path $notices "overrides\Avalonia.Fonts.Inter-12.1.0.txt"), "changed") }
        Assert-FailsClosed -Name "exclusion" -Root $tempRoot -BaseInventory $baseInventory `
            -PublishedDepsPath $publishedDepsPath -PublishRoot $publishRoot -NoticesSource $noticesSource `
            -Mutation {
                param($inventory, $policy, $notices)
                $redistributed = @($policy.packages | Where-Object redistributed | Select-Object -First 1)
                $redistributed[0].redistributed = $false
            }
    )

    $result = [ordered]@{
        schemaVersion = "infra-005-third-party-notice-verification-v1"
        status = "passed"
        semanticVersion = [string]$inventoryV2.semanticVersion
        packageCount = [int]$inventoryV2.packageCount
        redistributedPackageCount = [int]$inventoryV2.redistributedPackageCount
        noticeSha256 = $noticeHash
        inventorySha256 = $inventoryHash
        deterministicRuns = 2
        mutationCount = $mutationResults.Count
        mutations = $mutationResults
    }
    $json = $result | ConvertTo-Json -Depth 8
    if (-not [string]::IsNullOrWhiteSpace($EvidenceOutputPath))
    {
        Write-Utf8NoBom -Path $EvidenceOutputPath -Text $json
    }
    $result | ConvertTo-Json -Depth 8 -Compress
}
finally
{
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if ($resolvedTempRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTempRoot).StartsWith("isTranscribe-notice-test-", [StringComparison]::Ordinal))
    {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
