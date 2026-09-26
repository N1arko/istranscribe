param(
    [string]$BundleRoot = $PSScriptRoot,
    [string]$Version = "0.1.0",
    [switch]$LaunchAfterInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#installer-model.location
# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#shell-integration.start-menu
# @spec spec://modules/platform/INFRA-005-windows-installer-and-shell-integration#shell-integration.apps-features
$productName = "isTranscribe"
$publisher = "isTranscribe OSS"
$payloadRoot = Join-Path $BundleRoot "payload"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\isTranscribe"
$startMenuPrograms = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuPrograms "$productName.lnk"
$exePath = Join-Path $installRoot "IsTranscribe.App.exe"
$uninstallScriptSource = Join-Path $BundleRoot "Uninstall-IsTranscribe.ps1"
$uninstallScriptInstalled = Join-Path $installRoot "Uninstall-IsTranscribe.ps1"
$uninstallKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\isTranscribe"

if (-not (Test-Path $payloadRoot))
{
    throw "Payload folder not found: $payloadRoot"
}

if (-not (Test-Path (Join-Path $payloadRoot "IsTranscribe.App.exe")))
{
    throw "IsTranscribe.App.exe not found in payload."
}

$running = Get-Process -Name "IsTranscribe.App" -ErrorAction SilentlyContinue
if ($null -ne $running)
{
    throw "Please close isTranscribe before installing an upgrade."
}

if (Test-Path $installRoot)
{
    Remove-Item -Path $installRoot -Recurse -Force
}
New-Item -Path $installRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $payloadRoot "*") -Destination $installRoot -Recurse -Force
Copy-Item -Path $uninstallScriptSource -Destination $uninstallScriptInstalled -Force

if (-not (Test-Path $startMenuPrograms))
{
    New-Item -Path $startMenuPrograms -ItemType Directory -Force | Out-Null
}

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installRoot
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = "isTranscribe"
$shortcut.Save()

$windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
$uninstallCommand = "`"$windowsPowerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScriptInstalled`""
$quietUninstallCommand = "$uninstallCommand -Quiet"

$sizeKb = [int]((Get-ChildItem -Path $installRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)

New-Item -Path $uninstallKeyPath -Force | Out-Null
Set-ItemProperty -Path $uninstallKeyPath -Name "DisplayName" -Value $productName
Set-ItemProperty -Path $uninstallKeyPath -Name "DisplayVersion" -Value $Version
Set-ItemProperty -Path $uninstallKeyPath -Name "Publisher" -Value $publisher
Set-ItemProperty -Path $uninstallKeyPath -Name "InstallLocation" -Value $installRoot
Set-ItemProperty -Path $uninstallKeyPath -Name "DisplayIcon" -Value $exePath
Set-ItemProperty -Path $uninstallKeyPath -Name "UninstallString" -Value $uninstallCommand
Set-ItemProperty -Path $uninstallKeyPath -Name "QuietUninstallString" -Value $quietUninstallCommand
Set-ItemProperty -Path $uninstallKeyPath -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKeyPath -Name "NoRepair" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKeyPath -Name "EstimatedSize" -Value $sizeKb -Type DWord

Write-Host "Installed isTranscribe to: $installRoot"
Write-Host "Start Menu shortcut: $shortcutPath"
Write-Host "Apps & Features uninstall key: $uninstallKeyPath"

if ($LaunchAfterInstall)
{
    Start-Process -FilePath $exePath | Out-Null
}
