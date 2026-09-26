#Requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repositoryRoot "isTranscribe.sln"
$artifactRoot = Join-Path $repositoryRoot "artifacts\infra-009\windows-verify"
$publishRoot = Join-Path $repositoryRoot "artifacts\infra-009\windows-publish"
$project = Join-Path $repositoryRoot "src\IsTranscribe.App.Windows\IsTranscribe.App.Windows.csproj"
$windowsTestProjects = @(
    "tests\IsTranscribe.Core.Tests\IsTranscribe.Core.Tests.csproj",
    "tests\IsTranscribe.Application.Tests\IsTranscribe.Application.Tests.csproj",
    "tests\IsTranscribe.Persistence.Tests\IsTranscribe.Persistence.Tests.csproj",
    "tests\IsTranscribe.Desktop.Tests\IsTranscribe.Desktop.Tests.csproj",
    "tests\IsTranscribe.Platform.ContractTests\IsTranscribe.Platform.ContractTests.csproj",
    "tests\IsTranscribe.Platform.Windows.Tests\IsTranscribe.Platform.Windows.Tests.csproj"
) | ForEach-Object { Join-Path $repositoryRoot $_ }

# @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#build-and-test-contour
dotnet restore $solution --artifacts-path $artifactRoot
dotnet restore $project -r win-x64 -p:RestoreLockedMode=true --artifacts-path $artifactRoot
dotnet build $solution -c Release --no-restore --nologo --artifacts-path $artifactRoot
foreach ($testProject in $windowsTestProjects)
{
    dotnet test $testProject -c Release --no-build --no-restore --nologo --artifacts-path $artifactRoot
}
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    --artifacts-path $artifactRoot -o $publishRoot

if (-not (Test-Path -LiteralPath (Join-Path $publishRoot "IsTranscribe.Desktop.exe") -PathType Leaf))
{
    throw "Stable Windows executable identity is missing."
}
