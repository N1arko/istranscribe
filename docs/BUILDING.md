# Building isTranscribe

[English overview](../README.md) · [Русское описание](../README.ru.md)

The .NET SDK selection lives in [`global.json`](../global.json). Package versions are pinned in the project files and lock files. Builds restore dependencies from NuGet; the native build scripts fetch pinned upstream sources.

## Windows: desktop development

Use Windows x64 with Git and the selected .NET 10 SDK. From PowerShell in the repository root:

```powershell
dotnet build src/IsTranscribe.App.Windows/IsTranscribe.App.Windows.csproj -c Release -r win-x64
dotnet run --project src/IsTranscribe.App.Windows/IsTranscribe.App.Windows.csproj -c Release -r win-x64 --no-build
```

This is the Avalonia entrypoint. `src/IsTranscribe.App` contains the earlier WPF host.

Local transcription also needs the separate worker and native Whisper/speaker libraries. The package workflow below assembles those files. Models are installed by the app after the user enables transcription.

## Windows: full development MSIX

Prerequisites:

- PowerShell 7.4 or later;
- Visual Studio 2022 Build Tools with the C++ desktop workload and Windows SDK;
- CMake 3.24 or later, available on `PATH`;
- the .NET SDK selected by the repository;
- the Vulkan SDK if building the optional Vulkan backend.

Run these commands in a Visual Studio Developer PowerShell session so `dumpbin` is available. Short output paths help keep native builds and MSIX staging within Windows path limits. Use dedicated build folders and check free disk space first: native dependencies and intermediate .NET outputs occupy several gigabytes.

```powershell
$cmake = (Get-Command cmake -ErrorAction Stop).Source
$dumpbin = (Get-Command dumpbin -ErrorAction Stop).Source
$source = & ./eng/transcription/Fetch-WhisperSource.ps1 -DestinationRoot C:\it-build\whisper-source

& ./eng/transcription/Build-WhisperNative.ps1 `
  -Rid win-x64 `
  -SourceRoot $source.SourceRoot `
  -SourceReceiptPath $source.ReceiptPath `
  -BuildRoot C:\it-build\whisper-build `
  -OutputRoot C:\it-build\native-output `
  -CMakePath $cmake `
  -DumpbinPath $dumpbin

& ./eng/transcription/Build-SpeakerNative.ps1 `
  -Rid win-x64 `
  -BuildRoot C:\it-build\speaker-build `
  -OutputRoot C:\it-build\native-output `
  -CMakePath $cmake

& ./packaging/windows/Build-WindowsRelease.ps1 `
  -Version 2.0.0-dev.1 `
  -SigningMode Development `
  -CreateDevelopmentCertificate `
  -SourceRevision (git rev-parse HEAD) `
  -WhisperNativeOutputRoot C:\it-build\native-output `
  -OutputRoot C:\it-release
```

To include Vulkan, install its SDK and add `-EnableVulkan` to the Whisper build command. CPU remains included. Both native scripts also accept explicit Ninja/compiler paths for alternate toolchains; their parameter lists document those options.

The MSIX builder restores its pinned Windows SDK packaging tools, publishes the app and worker, includes the native libraries and dependency notices, and signs with a local development certificate. A development package requires certificate trust on the machine where it is installed. Signing and installation details are in the [Windows packaging guide](../packaging/windows/README.md#development-signed-msix).

## macOS on Apple Silicon

The current platform target is macOS 14.2 or later. Build on a Mac with the selected .NET SDK, PowerShell 7.4+, CMake, `jq`, and Xcode command-line tools.

For the application and worker bundle:

```sh
VERSION=2.0.0 sh eng/build-macos-release.sh "$PWD/artifacts/release/local-macos"
```

The script recreates the output directory, so use a dedicated path. It builds the native libraries, packages the app and worker, and produces a development DMG. Model weights are separate downloads. Permissions, signing, and reuse of native outputs are covered by the [macOS packaging guide](../packaging/macos/README.md).

## Data during development

Development launches use the platform's normal isTranscribe data location. Recordings remain in the configured folder. Build outputs belong in `bin`, `obj`, or dedicated artifact folders; model caches and application data should be kept out of cleanup commands.
