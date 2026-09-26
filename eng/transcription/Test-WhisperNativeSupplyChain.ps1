#Requires -Version 7.4

[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\..\native\whisper\runtime-manifest.v1.json"),
    [string]$SourceArchivePath,
    [string]$NativeLibraryPath,
    [string]$NativeImportLibraryPath,
    [ValidateSet("cpu", "vulkan", "metal")][string]$NativeVariant,
    [string]$CCompilerPath = $(if ($IsMacOS) { "/usr/bin/clang" } else { "" }),
    [string]$CxxCompilerPath = $(if ($IsMacOS) { "/usr/bin/clang++" } else { "" })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
Import-Module (Join-Path $PSScriptRoot "WhisperSupplyChain.psm1") -Force

# @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
# @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification

function Assert-Throws
{
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Label)

    try
    {
        & $Action
        throw "Mutation '$Label' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -ceq "Mutation '$Label' was accepted.") { throw }
    }
}

function Assert-ExactSet
{
    param([Parameter(Mandatory)][string[]]$Actual, [Parameter(Mandatory)][string[]]$Expected, [Parameter(Mandatory)][string]$Label)

    $actualSorted = @(Get-OrdinalSortedUnique -Values $Actual)
    $expectedSorted = @(Get-OrdinalSortedUnique -Values $Expected)
    if (($actualSorted -join "`n") -cne ($expectedSorted -join "`n")) { throw "$Label is not exact." }
}

function Test-IsMsvcCompiler
{
    param([Parameter(Mandatory)][string]$Path)
    $IsWindows -and [IO.Path]::GetFileName($Path) -ieq "cl.exe"
}

$manifestEvidence = Read-WhisperRuntimeManifest -ManifestPath $ManifestPath
$manifest = $manifestEvidence.Document
$nativeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $manifestEvidence.Path))
$headerPath = Join-Path $nativeRoot ([string]$manifest.abi.header)
$bridgePath = Join-Path $nativeRoot "src/istranscribe_whisper_v1.cpp"
$cmakePath = Join-Path $nativeRoot "CMakeLists.txt"
$macExportsPath = Join-Path $nativeRoot "exports/macos.exports"
$windowsExportsPath = Join-Path $nativeRoot "exports/windows.def"
foreach ($requiredPath in @($headerPath, $bridgePath, $cmakePath, $macExportsPath, $windowsExportsPath))
{
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Required native source '$requiredPath' is missing." }
}

$header = Get-Content -LiteralPath $headerPath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$cmake = Get-Content -LiteralPath $cmakePath -Raw
if ($header -match '(?m)^\s*#\s*include\s*[<"](?:whisper|ggml)') { throw "Public ABI header exposes upstream headers." }
if ($header -notmatch '#\s*define\s+ISTRANSCRIBE_WHISPER_CALL\s+__cdecl') { throw "Windows ABI does not pin cdecl." }
if ($header -notmatch 'const uint8_t \* data;\s*\r?\n\s*uint64_t length;') { throw "UTF-8 pointer/length view is absent." }
if ($header -notmatch 'ISTRANSCRIBE_WHISPER_MAX_SAMPLES_V1 UINT64_C\(4800000\)') { throw "Five-minute PCM bound is absent." }

$publicStructs = [Regex]::Matches($header, 'typedef struct istranscribe_whisper_[a-z0-9_]+_v1\s*\{\s*uint32_t struct_size;', [Text.RegularExpressions.RegexOptions]::Singleline)
if ($publicStructs.Count -ne 8) { throw "Every eight public v1 data structs must start with uint32_t struct_size." }

$requiredSymbols = @($manifest.abi.requiredSymbols | ForEach-Object { [string]$_ })
foreach ($symbol in $requiredSymbols)
{
    if ([Regex]::Matches($header, "\b$([Regex]::Escape($symbol))\s*\(").Count -ne 1)
    {
        throw "ABI symbol '$symbol' is missing or duplicated in the public header."
    }
}

$macExports = @(Get-Content -LiteralPath $macExportsPath | Where-Object { $_ } | ForEach-Object {
    if (-not $_.StartsWith('_', [StringComparison]::Ordinal)) { throw "macOS export '$_' lacks the C symbol prefix." }
    $_.Substring(1)
})
$windowsExports = @(Get-Content -LiteralPath $windowsExportsPath | Where-Object {
    $_ -and $_ -cne "EXPORTS"
} | ForEach-Object { $_.Trim() })
Assert-ExactSet -Actual $macExports -Expected $requiredSymbols -Label "macOS export list"
Assert-ExactSet -Actual $windowsExports -Expected $requiredSymbols -Label "Windows export list"

foreach ($disabledOption in @($manifest.buildPolicy.forcedDisabledOptions | ForEach-Object { [string]$_ }))
{
    $pattern = "set\($([Regex]::Escape($disabledOption))\s+OFF\s+CACHE\s+BOOL"
    if ($cmake -notmatch $pattern) { throw "CMake does not fail-close '$disabledOption'." }
}
if ($cmake -match '(?i)\badd_executable\s*\(') { throw "Native product CMake must not build executables." }
if ($bridge -notmatch 'ISTRANSCRIBE_WHISPER_MAX_SAMPLES_V1' -or
    $bridge -notmatch 'temperature\s*=\s*0\.0f' -or
    $bridge -notmatch 'temperature_inc\s*=\s*0\.0f' -or
    $bridge -notmatch 'abort_callback' -or $bridge -notmatch 'progress_callback')
{
    throw "Native inference bridge omits deterministic bounds, cancellation, or progress."
}
if ($bridge -match 'cancelled\.store\(false' -or
    $bridge -notmatch 'whisper_full_lang_id' -or
    $bridge -notmatch 'istranscribe_whisper_context_get_info_v1' -or
    $cmake -notmatch 'ffile-prefix-map' -or
    $cmake -notmatch '/pathmap:')
{
    throw "Native bridge omits cancel-before-start, result metadata, or reproducible path handling."
}
if ($bridge -match '(?i)https?://|\bcurl\b|\bffmpeg\b|\bpython\b') { throw "Native bridge contains a network or external-tool path." }

$ridSet = @($manifest.rids | ForEach-Object { [string]$_.rid })
Assert-ExactSet -Actual $ridSet -Expected @("osx-arm64", "win-x64") -Label "RID set"
$macVariant = Get-WhisperRidVariant -Manifest $manifest -Rid "osx-arm64" -Variant "metal"
$cpuVariant = Get-WhisperRidVariant -Manifest $manifest -Rid "win-x64" -Variant "cpu"
$vulkanVariant = Get-WhisperRidVariant -Manifest $manifest -Rid "win-x64" -Variant "vulkan"
if (-not [bool]$macVariant.required -or -not [bool]$cpuVariant.required -or [bool]$vulkanVariant.required)
{
    throw "Required Metal/CPU and optional Vulkan variant policy differs from FEAT-016."
}
if ([string]$macVariant.cmakeCache.GGML_METAL -cne "ON" -or
    [string]$cpuVariant.cmakeCache.GGML_VULKAN -cne "OFF" -or
    [string]$vulkanVariant.cmakeCache.GGML_VULKAN -cne "ON")
{
    throw "RID acceleration flags differ from FEAT-016."
}

$payloadFiles = @(Get-ChildItem -LiteralPath $nativeRoot -Recurse -Force -File | Where-Object {
    $_.Extension -in @('.dll', '.dylib', '.so', '.exe', '.bin', '.gguf', '.py')
})
if ($payloadFiles.Count -ne 0) { throw "Checked-in native source contains a product binary, model, executable, or Python payload." }

foreach ($scriptPath in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File))
{
    $scriptText = Get-Content -LiteralPath $scriptPath.FullName -Raw
    if ($scriptText -match '(?im)^\s*(?:&\s*)?(?:curl|python(?:3)?|ffmpeg|whisper-cli)(?:\s|$)')
    {
        throw "Supply-chain script '$($scriptPath.Name)' invokes a forbidden external product tool."
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "istranscribe-whisper-contract-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try
{
    $mutatedManifest = Join-Path $temporaryRoot "mutated-manifest.json"
    $mutatedText = (Get-Content -LiteralPath $manifestEvidence.Path -Raw).Replace('"v1.9.1"', '"v1.9.2"')
    [IO.File]::WriteAllText($mutatedManifest, $mutatedText, [Text.UTF8Encoding]::new($false))
    Assert-Throws -Label "runtime-version" -Action { Read-WhisperRuntimeManifest -ManifestPath $mutatedManifest | Out-Null }

    if (-not [string]::IsNullOrWhiteSpace($CCompilerPath))
    {
        if (-not [IO.Path]::IsPathRooted($CCompilerPath) -or -not (Test-Path -LiteralPath $CCompilerPath -PathType Leaf))
        {
            throw "CCompilerPath must be an existing absolute file."
        }
        $cSmokePath = Join-Path $temporaryRoot "abi-smoke.c"
        [IO.File]::WriteAllText($cSmokePath, "#include `"istranscribe_whisper_v1.h`"`nint main(void) { return (int)istranscribe_whisper_abi_version_v1(); }`n", [Text.UTF8Encoding]::new($false))
        if (Test-IsMsvcCompiler -Path $CCompilerPath)
        {
            & $CCompilerPath /nologo /std:c11 /Zs /WX "/I$(Join-Path $nativeRoot "include")" $cSmokePath
        }
        else
        {
            & $CCompilerPath -std=c11 -fsyntax-only -Werror -I (Join-Path $nativeRoot "include") $cSmokePath
        }
        if ($LASTEXITCODE -ne 0) { throw "Public ABI header failed C11 syntax verification." }
    }
    if (-not [string]::IsNullOrWhiteSpace($CxxCompilerPath))
    {
        if (-not [IO.Path]::IsPathRooted($CxxCompilerPath) -or -not (Test-Path -LiteralPath $CxxCompilerPath -PathType Leaf))
        {
            throw "CxxCompilerPath must be an existing absolute file."
        }
        $cppSmokePath = Join-Path $temporaryRoot "abi-smoke.cpp"
        [IO.File]::WriteAllText($cppSmokePath, "#include `"istranscribe_whisper_v1.h`"`nint main() { return static_cast<int>(istranscribe_whisper_abi_version_v1()); }`n", [Text.UTF8Encoding]::new($false))
        if (Test-IsMsvcCompiler -Path $CxxCompilerPath)
        {
            & $CxxCompilerPath /nologo /std:c++17 /Zs /WX "/I$(Join-Path $nativeRoot "include")" $cppSmokePath
        }
        else
        {
            & $CxxCompilerPath -std=c++17 -fsyntax-only -Werror -I (Join-Path $nativeRoot "include") $cppSmokePath
        }
        if ($LASTEXITCODE -ne 0) { throw "Public ABI header failed C++17 syntax verification." }
    }

    if (-not [string]::IsNullOrWhiteSpace($NativeLibraryPath))
    {
        if (-not $IsMacOS -and -not $IsWindows) { throw "NativeLibraryPath execution requires macOS or Windows." }
        $nativeLibrary = [IO.Path]::GetFullPath($NativeLibraryPath)
        if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) { throw "NativeLibraryPath is missing." }
        if ([string]::IsNullOrWhiteSpace($CCompilerPath)) { throw "CCompilerPath is required for native ABI execution." }
        if ([string]::IsNullOrWhiteSpace($NativeVariant)) { throw "NativeVariant is required for native ABI execution." }
        if ($IsWindows)
        {
            if ([string]::IsNullOrWhiteSpace($NativeImportLibraryPath))
            {
                throw "NativeImportLibraryPath is required for Windows native ABI execution."
            }
            $nativeImportLibrary = [IO.Path]::GetFullPath($NativeImportLibraryPath)
            if (-not (Test-Path -LiteralPath $nativeImportLibrary -PathType Leaf))
            {
                throw "NativeImportLibraryPath is missing."
            }
        }

        $nativeSmokePath = Join-Path $temporaryRoot "native-abi-smoke.c"
        $nativeSmoke = @'
#include "istranscribe_whisper_v1.h"

#include <stddef.h>
#include <string.h>

static int probe_backend(istranscribe_whisper_backend_v1 backend) {
    istranscribe_whisper_backend_probe_v1 probe = {0};
    probe.struct_size = (uint32_t)sizeof(probe);
    if (istranscribe_whisper_probe_backend_v1(backend, &probe) != ISTRANSCRIBE_WHISPER_OK_V1) return 10;
    if (probe.abi_version != ISTRANSCRIBE_WHISPER_ABI_VERSION_V1 || probe.requested_backend != backend) return 11;
    if (probe.available != 1 || probe.device_name.length == 0 || probe.runtime_version.length == 0) return 12;
    return 0;
}

static int probe_unsupported(istranscribe_whisper_backend_v1 backend) {
    istranscribe_whisper_backend_probe_v1 probe = {0};
    probe.struct_size = (uint32_t)sizeof(probe);
    if (istranscribe_whisper_probe_backend_v1(backend, &probe) != ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1) return 20;
    if (probe.abi_version != ISTRANSCRIBE_WHISPER_ABI_VERSION_V1 || probe.requested_backend != backend) return 21;
    if (probe.available != 0 || probe.runtime_version.length == 0) return 22;
    return 0;
}

int main(void) {
    if (istranscribe_whisper_abi_version_v1() != ISTRANSCRIBE_WHISPER_ABI_VERSION_V1) return 1;
    int result = probe_backend(ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1);
    if (result != 0) return result;
#if defined(ITW_SMOKE_METAL)
    result = probe_backend(ISTRANSCRIBE_WHISPER_BACKEND_METAL_V1);
    if (result != 0) return result + 10;
    result = probe_unsupported(ISTRANSCRIBE_WHISPER_BACKEND_VULKAN_V1);
    if (result != 0) return result + 20;
#elif defined(ITW_SMOKE_VULKAN)
    result = probe_backend(ISTRANSCRIBE_WHISPER_BACKEND_VULKAN_V1);
    if (result != 0) return result + 10;
    result = probe_unsupported(ISTRANSCRIBE_WHISPER_BACKEND_METAL_V1);
    if (result != 0) return result + 20;
#else
    result = probe_unsupported(ISTRANSCRIBE_WHISPER_BACKEND_METAL_V1);
    if (result != 0) return result + 10;
    result = probe_unsupported(ISTRANSCRIBE_WHISPER_BACKEND_VULKAN_V1);
    if (result != 0) return result + 20;
#endif

    istranscribe_whisper_backend_probe_v1 invalid_probe = {0};
    invalid_probe.struct_size = (uint32_t)sizeof(invalid_probe);
    if (istranscribe_whisper_probe_backend_v1(UINT32_C(999), &invalid_probe) != ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1) return 30;
    istranscribe_whisper_error_v1 error = {0};
    error.struct_size = (uint32_t)sizeof(error);
    if (istranscribe_whisper_last_error_v1(NULL, &error) != ISTRANSCRIBE_WHISPER_OK_V1) return 31;
    if (error.code != ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1 || error.message.length == 0) return 32;

#if defined(_WIN32)
    static const char missing_model[] = "C:\\istranscribe\\acceptance\\missing-model.bin";
#else
    static const char missing_model[] = "/istranscribe/acceptance/missing-model.bin";
#endif
    istranscribe_whisper_context_options_v1 options = {0};
    options.struct_size = (uint32_t)sizeof(options);
    options.abi_version = ISTRANSCRIBE_WHISPER_ABI_VERSION_V1;
    options.backend = ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1;
    options.thread_count = 1;
    options.model_path.struct_size = (uint32_t)sizeof(options.model_path);
    options.model_path.data = (const uint8_t *)missing_model;
    options.model_path.length = (uint64_t)strlen(missing_model);
    istranscribe_whisper_context_v1 * context = NULL;
    if (istranscribe_whisper_context_create_v1(&options, &context) != ISTRANSCRIBE_WHISPER_MODEL_LOAD_FAILED_V1) return 33;
    if (context != NULL) return 34;
    error.struct_size = (uint32_t)sizeof(error);
    if (istranscribe_whisper_last_error_v1(NULL, &error) != ISTRANSCRIBE_WHISPER_OK_V1) return 35;
    if (error.code != ISTRANSCRIBE_WHISPER_MODEL_LOAD_FAILED_V1 || error.message.length == 0) return 36;
    return 0;
}
'@
        [IO.File]::WriteAllText($nativeSmokePath, $nativeSmoke.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
        $nativeSmokeExecutable = Join-Path $temporaryRoot $(if ($IsWindows) { "native-abi-smoke.exe" } else { "native-abi-smoke" })
        $nativeDirectory = Split-Path -Parent $nativeLibrary
        $variantDefinition = switch ($NativeVariant)
        {
            "metal" { "ITW_SMOKE_METAL" }
            "vulkan" { "ITW_SMOKE_VULKAN" }
            default { "ITW_SMOKE_CPU" }
        }
        if ($IsWindows)
        {
            if (-not (Test-IsMsvcCompiler -Path $CCompilerPath))
            {
                throw "Windows native ABI execution requires cl.exe."
            }
            & $CCompilerPath /nologo /std:c11 /WX "/D$variantDefinition" `
                "/I$(Join-Path $nativeRoot "include")" $nativeSmokePath $nativeImportLibrary `
                "/Fo:$(Join-Path $temporaryRoot "native-abi-smoke.obj")" `
                "/Fe:$nativeSmokeExecutable"
        }
        else
        {
            & $CCompilerPath -std=c11 -Werror "-D$variantDefinition" -I (Join-Path $nativeRoot "include") `
                $nativeSmokePath $nativeLibrary "-Wl,-rpath,$nativeDirectory" -o $nativeSmokeExecutable
        }
        if ($LASTEXITCODE -ne 0) { throw "Native ABI probe fixture failed to link." }
        $previousPath = $env:PATH
        try
        {
            if ($IsWindows) { $env:PATH = "$nativeDirectory;$previousPath" }
            & $nativeSmokeExecutable
            if ($LASTEXITCODE -ne 0) { throw "Native ABI probe fixture failed with exit code $LASTEXITCODE." }
        }
        finally
        {
            $env:PATH = $previousPath
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($SourceArchivePath))
    {
        $archive = [IO.Path]::GetFullPath($SourceArchivePath)
        if ((Get-LowerSha256 -Path $archive) -cne [string]$manifest.source.archive.sha256)
        {
            throw "Supplied source archive does not match the manifest."
        }
        $invalidArchive = Join-Path $temporaryRoot "invalid-source.tar.gz"
        [IO.File]::WriteAllBytes($invalidArchive, [byte[]]@(0x49, 0x54, 0x57, 0x00))
        Assert-Throws -Label "source-archive-sha256" -Action {
            & (Join-Path $PSScriptRoot "Fetch-WhisperSource.ps1") `
                -DestinationRoot (Join-Path $temporaryRoot "invalid-source") `
                -ManifestPath $manifestEvidence.Path `
                -ArchivePath $invalidArchive `
                -Offline | Out-Null
        }
        $extractionRoot = Join-Path $temporaryRoot "verified-source"
        $fetchResult = & (Join-Path $PSScriptRoot "Fetch-WhisperSource.ps1") `
            -DestinationRoot $extractionRoot `
            -ManifestPath $manifestEvidence.Path `
            -ArchivePath $archive `
            -Offline
        $verifiedTree = Assert-WhisperSourceTree `
            -SourceRoot $fetchResult.SourceRoot `
            -ReceiptPath $fetchResult.ReceiptPath `
            -Manifest $manifest `
            -ManifestSha256 $manifestEvidence.Sha256
        if ([string]$verifiedTree.treeSha256 -cne [string]$fetchResult.TreeSha256)
        {
            throw "Verified extraction tree hash is inconsistent."
        }

        $mutableSourceFile = Join-Path $fetchResult.SourceRoot "README.md"
        [IO.File]::AppendAllText($mutableSourceFile, "`nmutation`n", [Text.UTF8Encoding]::new($false))
        Assert-Throws -Label "source-tree" -Action {
            Assert-WhisperSourceTree -SourceRoot $fetchResult.SourceRoot -ReceiptPath $fetchResult.ReceiptPath -Manifest $manifest -ManifestSha256 $manifestEvidence.Sha256 | Out-Null
        }
    }
}
finally
{
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container)
    {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}

[pscustomobject]@{
    ManifestSha256 = $manifestEvidence.Sha256
    RequiredSymbols = $requiredSymbols.Count
    Rids = $ridSet.Count
    HeaderC11Verified = -not [string]::IsNullOrWhiteSpace($CCompilerPath)
    HeaderCxx17Verified = -not [string]::IsNullOrWhiteSpace($CxxCompilerPath)
    NativeAbiExecuted = -not [string]::IsNullOrWhiteSpace($NativeLibraryPath)
    SourceArchiveVerified = -not [string]::IsNullOrWhiteSpace($SourceArchivePath)
}
