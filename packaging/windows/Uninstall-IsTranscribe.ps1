param(
    [switch]$RemoveAppData,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#uninstall-model.binaries
# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#uninstall-model.data
$productName = "isTranscribe"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\isTranscribe"
$startMenuShortcut = Join-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs") "$productName.lnk"
$uninstallKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\isTranscribe"
$appDataRoot = Join-Path $env:LOCALAPPDATA "isTranscribe"
$documentsRoot = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "isTranscribe"

$running = Get-Process -Name "IsTranscribe.App" -ErrorAction SilentlyContinue
if ($null -ne $running)
{
    throw "Please close isTranscribe before uninstalling."
}

if (Test-Path $startMenuShortcut)
{
    Remove-Item -Path $startMenuShortcut -Force
}

if (Test-Path $uninstallKeyPath)
{
    Remove-Item -Path $uninstallKeyPath -Recurse -Force
}

if (Test-Path $installRoot)
{
    Remove-Item -Path $installRoot -Recurse -Force
}

if ($RemoveAppData)
{
    if (Test-Path $appDataRoot)
    {
        Remove-Item -Path $appDataRoot -Recurse -Force
    }

    if (Test-Path $documentsRoot)
    {
        Remove-Item -Path $documentsRoot -Recurse -Force
    }
}

if (-not $Quiet)
{
    Write-Host "Uninstalled isTranscribe binaries."
    if ($RemoveAppData)
    {
        Write-Host "Removed app data from: $appDataRoot"
        Write-Host "Removed documents data from: $documentsRoot"
    }
    else
    {
        Write-Host "User data was preserved."
        Write-Host "App data root: $appDataRoot"
        Write-Host "Documents root: $documentsRoot"
    }
}
