param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "0.1.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#installer-model.artifact
# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#installer-model.prerequisites
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$appProject = Join-Path $repoRoot "src\IsTranscribe.App.Windows\IsTranscribe.App.Windows.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts\installer"
$bundleName = "isTranscribe-$RuntimeIdentifier-$Version"
$bundleRoot = Join-Path $artifactsRoot $bundleName
$payloadRoot = Join-Path $bundleRoot "payload"

if (Test-Path $bundleRoot)
{
    Remove-Item -Path $bundleRoot -Recurse -Force
}

New-Item -Path $payloadRoot -ItemType Directory -Force | Out-Null

Write-Host "Publishing self-contained payload..."
$publishArgs = @(
    "publish",
    $appProject,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:Version=$Version",
    "-o", $payloadRoot
)

dotnet @publishArgs
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$installerFiles = @(
    "Install-IsTranscribe.ps1",
    "Install-IsTranscribe.cmd",
    "Uninstall-IsTranscribe.ps1",
    "README.md"
)

foreach ($file in $installerFiles)
{
    Copy-Item -Path (Join-Path $PSScriptRoot $file) -Destination (Join-Path $bundleRoot $file) -Force
}

$manifestPath = Join-Path $bundleRoot "bundle-manifest.txt"
@(
    "Product=isTranscribe",
    "Version=$Version",
    "RuntimeIdentifier=$RuntimeIdentifier",
    "BuildUtc=$([DateTime]::UtcNow.ToString('O'))"
) | Set-Content -Path $manifestPath -Encoding UTF8

$zipPath = "$bundleRoot.zip"
if (Test-Path $zipPath)
{
    Remove-Item -Path $zipPath -Force
}
Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $zipPath -Force

Write-Host "Installer bundle created: $bundleRoot"
Write-Host "Zip created: $zipPath"
