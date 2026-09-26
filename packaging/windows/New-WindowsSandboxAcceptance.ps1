[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$OutputPath,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
# @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
function Test-PathInsideOrEqual
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    return [string]::Equals($fullPath, $fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith(
            $fullParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PathsDoNotOverlap
{
    param(
        [Parameter(Mandatory)][string]$FirstPath,
        [Parameter(Mandatory)][string]$SecondPath
    )

    if ((Test-PathInsideOrEqual -Path $FirstPath -ParentPath $SecondPath) -or
        (Test-PathInsideOrEqual -Path $SecondPath -ParentPath $FirstPath))
    {
        throw "Windows Sandbox host mappings and output paths must not overlap: '$FirstPath' and '$SecondPath'."
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
                throw "Windows Sandbox paths must not traverse a reparse point: $current"
            }
        }

        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent)
        {
            break
        }
        $current = $parent.FullName
    }
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot))
{
    $RepositoryRoot = Join-Path $PSScriptRoot "..\.."
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot "specs\BOARD.md") -PathType Leaf))
{
    throw "RepositoryRoot does not identify the isTranscribe repository."
}

$windowsVersionKey = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
    -ErrorAction SilentlyContinue
$editionId = if ($null -eq $windowsVersionKey) { $null } else { [string]$windowsVersionKey.EditionID }
$osBuild = [Environment]::OSVersion.Version.Build
$supportedEdition = -not [string]::IsNullOrWhiteSpace($editionId) -and
    $editionId -match '^(Professional|Enterprise|Education|CloudEdition)'
$minimumBuildSatisfied = $osBuild -ge 19041
$sandboxExecutable = Join-Path $env:WINDIR "System32\WindowsSandbox.exe"
$sandboxExecutablePresent = Test-Path -LiteralPath $sandboxExecutable -PathType Leaf
$hostLaunchPrerequisitesObserved =
    [Environment]::Is64BitOperatingSystem -and
    $supportedEdition -and
    $minimumBuildSatisfied -and
    $sandboxExecutablePresent

$releaseDirectory = Join-Path $RepositoryRoot "artifacts\release\windows-x64\development"
$installerToolsDirectory = Join-Path $RepositoryRoot "packaging\windows"
$acceptanceDirectory = Join-Path $RepositoryRoot "artifacts\acceptance\INFRA-005\windows-x64"
$stagingDirectory = Join-Path $acceptanceDirectory "sandbox-staging"
if ([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path $acceptanceDirectory "isTranscribe-installer-acceptance.wsb"
}
elseif (-not [IO.Path]::IsPathRooted($OutputPath))
{
    $OutputPath = Join-Path $RepositoryRoot $OutputPath
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

if ([IO.Path]::GetExtension($OutputPath) -ine ".wsb")
{
    throw "OutputPath must use the .wsb extension."
}
if (-not (Test-PathInsideOrEqual -Path $releaseDirectory -ParentPath $RepositoryRoot) -or
    -not (Test-PathInsideOrEqual -Path $installerToolsDirectory -ParentPath $RepositoryRoot) -or
    -not (Test-PathInsideOrEqual -Path $stagingDirectory -ParentPath $acceptanceDirectory) -or
    -not (Test-PathInsideOrEqual -Path $OutputPath -ParentPath $acceptanceDirectory))
{
    throw "Sandbox inputs, staging and configuration must stay within their expected repository roots."
}
Assert-PathsDoNotOverlap -FirstPath $releaseDirectory -SecondPath $installerToolsDirectory
Assert-PathsDoNotOverlap -FirstPath $releaseDirectory -SecondPath $stagingDirectory
Assert-PathsDoNotOverlap -FirstPath $installerToolsDirectory -SecondPath $stagingDirectory
Assert-PathsDoNotOverlap -FirstPath $OutputPath -SecondPath $releaseDirectory
Assert-PathsDoNotOverlap -FirstPath $OutputPath -SecondPath $installerToolsDirectory
Assert-PathsDoNotOverlap -FirstPath $OutputPath -SecondPath $stagingDirectory
foreach ($hostPath in @(
    $RepositoryRoot,
    $releaseDirectory,
    $installerToolsDirectory,
    $acceptanceDirectory,
    $stagingDirectory,
    $OutputPath))
{
    Assert-NoReparsePointInPath -Path $hostPath
}

$requiredFiles = @(
    (Join-Path $releaseDirectory "2.0.0\isTranscribe-2.0.0-dev-win-x64.msix"),
    (Join-Path $releaseDirectory "2.0.1\isTranscribe-2.0.1-dev-win-x64.msix"),
    (Join-Path $releaseDirectory "2.0.1\isTranscribe-development-certificate.cer"),
    (Join-Path $installerToolsDirectory "Test-WindowsReleaseLifecycle.ps1"),
    (Join-Path $installerToolsDirectory "windows-sandbox\Invoke-WindowsSandboxAcceptance.ps1"),
    (Join-Path $installerToolsDirectory "windows-sandbox\Run-IsTranscribe-Installer-Acceptance.cmd"),
    (Join-Path $installerToolsDirectory "windows-sandbox\README.txt")
)
foreach ($requiredFile in $requiredFiles)
{
    Assert-NoReparsePointInPath -Path $requiredFile
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf))
    {
        throw "Required acceptance input is missing: $requiredFile"
    }
}

[void][IO.Directory]::CreateDirectory($acceptanceDirectory)
[void][IO.Directory]::CreateDirectory($stagingDirectory)
Assert-NoReparsePointInPath -Path $acceptanceDirectory
Assert-NoReparsePointInPath -Path $stagingDirectory
if (@(Get-ChildItem -LiteralPath $stagingDirectory -Force).Count -ne 0)
{
    throw "The Sandbox staging directory must be empty before a new disposable run: $stagingDirectory"
}

$expectedLifecycleHarnessSha256 = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F"
$lifecycleHarnessPath = Join-Path $installerToolsDirectory "Test-WindowsReleaseLifecycle.ps1"
$actualLifecycleHarnessSha256 =
    (Get-FileHash -LiteralPath $lifecycleHarnessPath -Algorithm SHA256).Hash
if (-not [string]::Equals(
    $actualLifecycleHarnessSha256,
    $expectedLifecycleHarnessSha256,
    [StringComparison]::OrdinalIgnoreCase))
{
    throw "The lifecycle harness changed. Review it and update the Sandbox fixture hash pin intentionally."
}

$outputDirectory = Split-Path -Parent $OutputPath
[void][IO.Directory]::CreateDirectory($outputDirectory)
if ((Test-Path -LiteralPath $OutputPath) -and -not $Force)
{
    throw "The Sandbox configuration already exists. Pass -Force to replace only that file: $OutputPath"
}

$document = [Xml.XmlDocument]::new()
[void]$document.AppendChild($document.CreateXmlDeclaration("1.0", "utf-8", $null))
$configuration = $document.CreateElement("Configuration")
[void]$document.AppendChild($configuration)

function Add-ConfigurationElement
{
    param(
        [Parameter(Mandatory)][Xml.XmlNode]$Parent,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $element = $document.CreateElement($Name)
    $element.InnerText = $Value
    [void]$Parent.AppendChild($element)
}

function Add-MappedFolder
{
    param(
        [Parameter(Mandatory)][Xml.XmlNode]$Parent,
        [Parameter(Mandatory)][string]$HostFolder,
        [Parameter(Mandatory)][string]$SandboxFolder,
        [Parameter(Mandatory)][bool]$ReadOnly
    )

    if (-not [IO.Path]::IsPathRooted($HostFolder))
    {
        throw "Windows Sandbox mappings require absolute host paths."
    }

    $mapping = $document.CreateElement("MappedFolder")
    Add-ConfigurationElement -Parent $mapping -Name "HostFolder" -Value ([IO.Path]::GetFullPath($HostFolder))
    Add-ConfigurationElement -Parent $mapping -Name "SandboxFolder" -Value $SandboxFolder
    Add-ConfigurationElement -Parent $mapping -Name "ReadOnly" -Value $ReadOnly.ToString().ToLowerInvariant()
    [void]$Parent.AppendChild($mapping)
}

Add-ConfigurationElement -Parent $configuration -Name "vGPU" -Value "Disable"
Add-ConfigurationElement -Parent $configuration -Name "Networking" -Value "Disable"
$mappedFolders = $document.CreateElement("MappedFolders")
[void]$configuration.AppendChild($mappedFolders)
Add-MappedFolder -Parent $mappedFolders `
    -HostFolder $releaseDirectory `
    -SandboxFolder "C:\isTranscribeAcceptance\packages" `
    -ReadOnly $true
Add-MappedFolder -Parent $mappedFolders `
    -HostFolder $installerToolsDirectory `
    -SandboxFolder "C:\isTranscribeAcceptance\tools" `
    -ReadOnly $true
Add-MappedFolder -Parent $mappedFolders `
    -HostFolder $stagingDirectory `
    -SandboxFolder "C:\isTranscribeAcceptance\evidence" `
    -ReadOnly $false
Add-ConfigurationElement -Parent $configuration -Name "AudioInput" -Value "Disable"
Add-ConfigurationElement -Parent $configuration -Name "VideoInput" -Value "Disable"
Add-ConfigurationElement -Parent $configuration -Name "ProtectedClient" -Value "Enable"
Add-ConfigurationElement -Parent $configuration -Name "PrinterRedirection" -Value "Disable"
Add-ConfigurationElement -Parent $configuration -Name "ClipboardRedirection" -Value "Disable"
Add-ConfigurationElement -Parent $configuration -Name "MemoryInMB" -Value "4096"

$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$temporaryPath = Join-Path $outputDirectory (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($OutputPath)), [guid]::NewGuid())
$backupPath = $null
try
{
    $writer = [Xml.XmlWriter]::Create($temporaryPath, $settings)
    try
    {
        $document.Save($writer)
    }
    finally
    {
        $writer.Dispose()
    }

    if (Test-Path -LiteralPath $OutputPath)
    {
        $backupPath = Join-Path $outputDirectory (".{0}.{1}.bak" -f ([IO.Path]::GetFileName($OutputPath)), [guid]::NewGuid())
        [IO.File]::Replace($temporaryPath, $OutputPath, $backupPath, $true)
        Remove-Item -LiteralPath $backupPath -Force
        $backupPath = $null
    }
    else
    {
        [IO.File]::Move($temporaryPath, $OutputPath)
    }
}
finally
{
    if (Test-Path -LiteralPath $temporaryPath)
    {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    if ($null -ne $backupPath -and (Test-Path -LiteralPath $backupPath))
    {
        Remove-Item -LiteralPath $backupPath -Force
    }
}

[ordered]@{
    status = if ($hostLaunchPrerequisitesObserved) {
        "prepared"
    } else {
        "prepared_host_launch_unsupported"
    }
    configurationPath = $OutputPath
    configurationIsHostPathSpecific = $true
    rerunGeneratorOnLaunchHost = $true
    stagingDirectory = $stagingDirectory
    writableMappingCount = 1
    securitySettingsModified = $false
    sandboxLaunched = $false
    host = [ordered]@{
        editionId = $editionId
        osBuild = $osBuild
        is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        supportedEdition = $supportedEdition
        minimumBuild19041Satisfied = $minimumBuildSatisfied
        sandboxExecutablePresent = $sandboxExecutablePresent
        launchPrerequisitesObserved = $hostLaunchPrerequisitesObserved
    }
} | ConvertTo-Json -Depth 3
