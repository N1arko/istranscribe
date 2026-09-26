# macOS packaging boundary

`INFRA-009` owns the shared-build and composition boundary only. Run:

```powershell
pwsh -NoProfile -File eng/verify-macos-boundary.ps1
```

The command restores, builds and tests `isTranscribe.macos.slnf`. It produces no
`.app` or `.dmg`, requests no permissions and does not advertise recording or
meeting detection. Native capture, permissions, Keychain integration and DMG
packaging belong to `INFRA-010`.

@spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#build-test

## FEAT-016 local-runtime staging

The Apple Silicon application and local inference worker are published as separate
self-contained `osx-arm64` payloads. The staging command merges byte-identical shared runtime
files, places the inspected Metal library under `native/`, and writes a path-free runtime
inventory:

```powershell
pwsh -NoProfile -File packaging/macos/Publish-MacOSLocalRuntime.ps1 `
  -WhisperNativeOutputRoot <verified-native-output> `
  -OutputRoot <fresh-output-directory>
```

The command produces a verified flat runtime payload for composition testing. The `.app`
bundle, deterministic macOS-wide notices, signing-ready metadata and DMG remain in
`INFRA-010`.

@spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
@spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
@spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution

## Speaker-aware release

`eng/build-macos-release.sh <fresh-output-directory>` now publishes the application
and isolated worker together, builds the pinned Whisper and speaker native assets,
checks their composition, bundles licenses, signs the app and verifies the DMG.
It requires the repository-selected .NET SDK, PowerShell 7.4+, CMake and Xcode tools.
The published runtime is pinned to .NET 10.0.11, matching the local-runtime staging
script. It does not use Homebrew-built .NET native libraries or external Brotli.

For a previously verified native build, set `TRANSCRIPTION_NATIVE_OUTPUT_ROOT` to
its output directory. That directory must contain both the Whisper inventory and
`speaker/osx-arm64/inventory.json` from `Build-SpeakerNative.ps1`.
The same speaker inventory is required by `Publish-MacOSLocalRuntime.ps1`.

The runtime composition receipt describes bytes **before** Mach-O path rewriting
and signing. The final release manifest independently hashes every signed bundle
file. ASR and speaker model weights remain on-demand downloads and are absent
from the DMG. Speaker licenses are included in the common notice and in
`Contents/MacOS/speaker-licenses/`.

@spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization.implementation
