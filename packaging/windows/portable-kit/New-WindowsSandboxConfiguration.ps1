#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$KitRoot,
    [string]$OutputPath,
    [string]$EvidenceDirectory,
    [switch]$Launch,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
function Test-PathInsideOrEqual
{
    param([string]$Path, [string]$ParentPath)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    return [string]::Equals($fullPath, $fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePointInPath
{
    param([string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current))
    {
        if (Test-Path -LiteralPath $current)
        {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            {
                throw "Sandbox host paths must not traverse reparse points: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

if ([string]::IsNullOrWhiteSpace($KitRoot)) { $KitRoot = Split-Path -Parent $PSScriptRoot }
$kitFullPath = [IO.Path]::GetFullPath($KitRoot)
if ($kitFullPath.StartsWith("\\", [StringComparison]::Ordinal)) { throw "Sandbox kit must be on a local host path." }
$kitParent = Split-Path -Parent $kitFullPath
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $kitParent "isTranscribe-INFRA-005-acceptance.wsb" }
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { $EvidenceDirectory = Join-Path $kitParent "isTranscribe-sandbox-evidence" }
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$evidenceFullPath = [IO.Path]::GetFullPath($EvidenceDirectory)
foreach ($localHostPath in @($kitFullPath, $outputFullPath, $evidenceFullPath))
{
    if ($localHostPath.StartsWith("\\", [StringComparison]::Ordinal))
    {
        throw "Sandbox kit, configuration and evidence must use local host paths."
    }
}
if ([IO.Path]::GetExtension($outputFullPath) -ine ".wsb") { throw "Sandbox output must use .wsb." }
foreach ($path in @($kitFullPath, $outputFullPath, $evidenceFullPath)) { Assert-NoReparsePointInPath -Path $path }
if ((Test-PathInsideOrEqual -Path $outputFullPath -ParentPath $kitFullPath) -or
    (Test-PathInsideOrEqual -Path $evidenceFullPath -ParentPath $kitFullPath) -or
    (Test-PathInsideOrEqual -Path $kitFullPath -ParentPath $evidenceFullPath) -or
    (Test-PathInsideOrEqual -Path $outputFullPath -ParentPath $evidenceFullPath))
{
    throw "Sandbox kit, configuration and evidence paths must be separate."
}
if ((Test-Path -LiteralPath $outputFullPath) -and -not $Force) { throw "Sandbox configuration exists; pass -Force to replace only that file." }
[void][IO.Directory]::CreateDirectory($evidenceFullPath)
Assert-NoReparsePointInPath -Path $evidenceFullPath
if (@(Get-ChildItem -LiteralPath $evidenceFullPath -Force).Count -ne 0) { throw "Sandbox evidence directory must be empty." }

$verification = & (Join-Path $kitFullPath "tools\Test-AcceptanceKit.ps1") -KitDirectory $kitFullPath | Out-String | ConvertFrom-Json
if ($verification.status -cne "passed") { throw "Acceptance kit verification failed before Sandbox generation." }

$document = [Xml.XmlDocument]::new()
[void]$document.AppendChild($document.CreateXmlDeclaration("1.0", "utf-8", $null))
$configuration = $document.CreateElement("Configuration")
[void]$document.AppendChild($configuration)
function Add-Element
{
    param([Xml.XmlNode]$Parent, [string]$Name, [string]$Value)
    $element = $document.CreateElement($Name)
    $element.InnerText = $Value
    [void]$Parent.AppendChild($element)
}
function Add-Mapping
{
    param([Xml.XmlNode]$Parent, [string]$HostFolder, [string]$SandboxFolder, [bool]$ReadOnly)
    $mapping = $document.CreateElement("MappedFolder")
    Add-Element -Parent $mapping -Name "HostFolder" -Value $HostFolder
    Add-Element -Parent $mapping -Name "SandboxFolder" -Value $SandboxFolder
    Add-Element -Parent $mapping -Name "ReadOnly" -Value $ReadOnly.ToString().ToLowerInvariant()
    [void]$Parent.AppendChild($mapping)
}
Add-Element -Parent $configuration -Name "vGPU" -Value "Disable"
Add-Element -Parent $configuration -Name "Networking" -Value "Disable"
$mappedFolders = $document.CreateElement("MappedFolders")
[void]$configuration.AppendChild($mappedFolders)
Add-Mapping -Parent $mappedFolders -HostFolder $kitFullPath -SandboxFolder "C:\isTranscribeAcceptance\kit" -ReadOnly $true
Add-Mapping -Parent $mappedFolders -HostFolder $evidenceFullPath -SandboxFolder "C:\isTranscribeAcceptance\evidence" -ReadOnly $false
Add-Element -Parent $configuration -Name "AudioInput" -Value "Disable"
Add-Element -Parent $configuration -Name "VideoInput" -Value "Disable"
Add-Element -Parent $configuration -Name "ProtectedClient" -Value "Enable"
Add-Element -Parent $configuration -Name "PrinterRedirection" -Value "Disable"
Add-Element -Parent $configuration -Name "ClipboardRedirection" -Value "Disable"
Add-Element -Parent $configuration -Name "MemoryInMB" -Value "4096"

$outputDirectory = Split-Path -Parent $outputFullPath
[void][IO.Directory]::CreateDirectory($outputDirectory)
$temporary = Join-Path $outputDirectory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($outputFullPath)), [guid]::NewGuid())
$backup = $null
try
{
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($temporary, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    if (Test-Path -LiteralPath $outputFullPath)
    {
        $backup = Join-Path $outputDirectory (".{0}.{1}.bak" -f ([IO.Path]::GetFileName($outputFullPath)), [guid]::NewGuid())
        [IO.File]::Replace($temporary, $outputFullPath, $backup, $true)
        Remove-Item -LiteralPath $backup -Force
        $backup = $null
    }
    else { [IO.File]::Move($temporary, $outputFullPath) }
}
finally
{
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if ($null -ne $backup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Force }
}

$edition = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
$editionId = if ($null -eq $edition) { $null } else { [string]$edition.EditionID }
$sandboxExecutable = Join-Path $env:WINDIR "System32\WindowsSandbox.exe"
$hostReady = [Environment]::Is64BitOperatingSystem -and [Environment]::OSVersion.Version.Build -ge 19041 -and
    (Test-Path -LiteralPath $sandboxExecutable -PathType Leaf)
if ($Launch)
{
    if (-not $hostReady) { throw "Sandbox configuration was prepared, but this host cannot launch Windows Sandbox." }
    Start-Process -FilePath $outputFullPath
}
[ordered]@{
    schemaVersion = "infra-005-portable-sandbox-preparation-v1"
    status = if ($hostReady) { "prepared" } else { "prepared_host_launch_unsupported" }
    configurationPath = $outputFullPath
    evidenceDirectory = $evidenceFullPath
    hostPathSpecific = $true
    editionId = $editionId
    hostLaunchPrerequisitesObserved = $hostReady
    securitySettingsModified = $false
    launched = [bool]$Launch -and $hostReady
} | ConvertTo-Json -Depth 3
