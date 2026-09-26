#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("osx-arm64", "win-x64")][string]$Rid,
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$CMakePath = "cmake",
    [string]$WindowsGenerator = "Visual Studio 17 2022",
    [string]$WindowsCCompiler,
    [string]$WindowsCxxCompiler,
    [string]$WindowsMakeProgram
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
# @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
if (($Rid -eq "osx-arm64" -and -not $IsMacOS) -or ($Rid -eq "win-x64" -and -not $IsWindows)) {
    throw "Build the speaker runtime on its target platform."
}
$buildPath = [IO.Path]::GetFullPath($BuildRoot)
$outputPath = [IO.Path]::GetFullPath((Join-Path $OutputRoot "speaker/$Rid"))
foreach ($path in @($buildPath, $outputPath)) {
    if ($path.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq [IO.Path]::GetPathRoot($path).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "A filesystem root cannot be an artifact directory."
    }
}
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../native/speaker"))
$configure = @("-S", $source, "-B", $buildPath, "-DCMAKE_BUILD_TYPE=Release")
if ($IsMacOS) { $configure += "-DCMAKE_OSX_ARCHITECTURES=arm64" }
elseif ($WindowsGenerator -ceq "Ninja") {
    foreach ($tool in @($WindowsCCompiler, $WindowsCxxCompiler, $WindowsMakeProgram)) {
        if ([string]::IsNullOrWhiteSpace($tool) -or -not [IO.Path]::IsPathRooted($tool) -or
            -not (Test-Path -LiteralPath $tool -PathType Leaf)) {
            throw "Ninja speaker builds require absolute existing compiler and make-program paths."
        }
    }
    $configure += @(
        "-G", "Ninja",
        "-DCMAKE_VS_PLATFORM_NAME=x64",
        "-DCMAKE_C_COMPILER=$WindowsCCompiler",
        "-DCMAKE_CXX_COMPILER=$WindowsCxxCompiler",
        "-DCMAKE_MAKE_PROGRAM=$WindowsMakeProgram")
}
elseif ($WindowsGenerator -match '^Visual Studio \d+ \d{4}$') {
    $configure += @("-G", $WindowsGenerator, "-A", "x64")
}
else { throw "WindowsGenerator must be a Visual Studio generator or exact 'Ninja'." }
& $CMakePath @configure
& $CMakePath --build $buildPath --config Release --target sherpa-onnx-c-api --parallel 4
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$names = if ($IsMacOS) { @("libsherpa-onnx-c-api.dylib", "libonnxruntime.1.17.1.dylib") }
    else { @("sherpa-onnx-c-api.dll", "onnxruntime.dll") }
$inventory = @()
foreach ($name in $names) {
    $matches = @(Get-ChildItem -LiteralPath $buildPath -Recurse -File -Filter $name | Where-Object { $_.FullName -notmatch "[/\\]CMakeFiles[/\\]|\.dSYM[/\\]" })
    if ($matches.Count -eq 0) { throw "Missing speaker runtime asset: $name" }
    $hashes = @($matches | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Sort-Object -Unique)
    if ($hashes.Count -ne 1) { throw "Ambiguous speaker runtime asset: $name" }
    $target = Join-Path $outputPath $name
    Copy-Item -LiteralPath $matches[0].FullName -Destination $target -Force
    $inventory += [ordered]@{ path = $name; sizeBytes = (Get-Item -LiteralPath $target).Length; sha256 = $hashes[0].ToLowerInvariant() }
}
$licenseSources = @(
    "speaker_upstream-src/LICENSE", "kaldi_native_fbank-src/LICENSE", "kaldi_decoder-src/LICENSE",
    "kaldifst-src/LICENSE", "openfst-src/COPYING", "simple-sentencepiece-src/LICENSE",
    "hclust_cpp-src/LICENSE", "kissfft-src/COPYING", "kissfft-src/LICENSES/BSD-3-Clause",
    "eigen-src/COPYING.MPL2", "onnxruntime-src/LICENSE", "onnxruntime-src/ThirdPartyNotices.txt",
    "cppjieba-src/LICENSE", "cppjieba-src/deps/limonp/LICENSE"
)
$licenses = @()
foreach ($relative in $licenseSources) {
    $licensePath = Join-Path (Join-Path $buildPath "_deps") $relative
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Missing speaker license material: $relative" }
    $name = $relative.Replace("/", "-") + ".txt"
    $target = Join-Path $outputPath "licenses/$name"
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $licensePath -Destination $target -Force
    $licenses += [ordered]@{ path = "licenses/$name"; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$receipt = [ordered]@{
    schema = "speaker-native/v1"; rid = $Rid; runtimeVersion = "sherpa-onnx/1.12.14"
    sourceCommit = "26aa2fa93210376a89de3a65a1a4dd320c37f5e9"
    sourceArchiveSha256 = "7c2daea812195ebef5f0799e68a907319e732b152176b0efa4e1dd7660df6572"
    ttsEnabled = $false; eigenMpl2Only = $true; libraries = $inventory; licenses = $licenses
}
[IO.File]::WriteAllText((Join-Path $outputPath "inventory.json"), ($receipt | ConvertTo-Json -Depth 10) + "`n")
$receipt
