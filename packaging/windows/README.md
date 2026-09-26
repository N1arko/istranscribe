# Windows Release Tooling

`Build-WindowsRelease.ps1` creates the installable Windows x64 release described by
`INFRA-005.A`. The result is a signed, self-contained MSIX package for the Avalonia
`IsTranscribe.Desktop` entrypoint.

<!-- @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification -->
FEAT-016 release builds also require the inspected `win-x64` native output produced by
`eng/transcription/Build-WhisperNative.ps1`. Point the build at that output root before any
Store, development or production command:

```powershell
$env:ISTRANSCRIBE_WHISPER_NATIVE_OUTPUT_ROOT = 'C:\release-inputs\whisper-native'
```

The root contains `win-x64/cpu/istranscribe_whisper_v1.dll` and its matching
`inventory/win-x64-cpu.inventory.v1.json`; an inspected Vulkan pair is included when present.
The release builder publishes the worker separately for the same RID and .NET runtime,
requires byte-identical shared files, stages native libraries under `native/`, and verifies
the resulting inventory before creating the MSIX. The deterministic notice includes the
pinned whisper.cpp and OpenAI Whisper license material. Model weights remain outside the
package.

<!-- @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission -->
## Microsoft Store submission MSIX

The primary public Windows release is submitted through Microsoft Partner Center.
Microsoft Store signs the certified package, so this build mode has no local
code-signing certificate, PFX or timestamp dependency.

After reserving the product name, copy `store-identity.template.json` to a release
configuration outside the artifact directory. Replace every Partner Center
placeholder with the exact Package/Identity, publisher display name and package
family values, then set `configured` to `true`. The checked-in template remains
fail-closed.

```powershell
$env:ISTRANSCRIBE_STORE_IDENTITY_PATH = 'C:\release-inputs\isTranscribe-store-identity.json'
$env:ISTRANSCRIBE_SOURCE_REVISION = '<source revision>'

pwsh -File packaging/windows/Build-WindowsRelease.ps1 `
  -Version 2.0.0 `
  -SigningMode Store
```

The output goes to `artifacts/release/windows-x64/store/<version>/`. It contains an
unsigned `isTranscribe-<version>-store-win-x64.msix`, a sanitized identity snapshot,
the release evidence, and `STORE_SUBMISSION_ONLY.txt`. This directory is a Partner
Center handoff. The MSIX becomes user-distributable after Store certification and
Store signing.

The Store manifest keeps `runFullTrust` for the packaged desktop process and removes
the Store-restricted `unvirtualizedResources` virtualization override. Explain the
desktop audio capture use of `runFullTrust` on Partner Center's Submission options
page. The private flight must confirm `%LocalAppData%\isTranscribe` persistence on
the certified full-trust package before public publication.

Verify the handoff without installing it or changing certificate stores:

```powershell
pwsh -NoProfile -File packaging/windows/Test-WindowsStoreSubmissionArtifact.ps1 `
  -ReleaseDirectory artifacts/release/windows-x64/store/2.0.0
```

Generate the eight deterministic Store-listing screenshots from the production
Avalonia views. This capture-only mode is separate from the canonical visual-review
matrix and writes four RU plus four EN PNG files and a validation manifest:

```powershell
dotnet run --project tools/IsTranscribe.VisualReview/IsTranscribe.VisualReview.csproj `
  -c Release -- `
  --store-listing-output artifacts/release/windows-x64/store-assets/2.0.0
```

Copy `store-submission-metadata.template.json` to reviewed release inputs, resolve
every placeholder, set `configured` to `true`, and complete the Partner Center age
rating and review fields. Host a reviewed copy of `store/privacy-policy.template.md`
at the HTTPS URL recorded in the metadata. The same current-release privacy boundary
is available inside the app from Help → Privacy.

Verify the listing, privacy facts, screenshots and their binding to the exact MSIX:

```powershell
pwsh -NoProfile -File packaging/windows/Test-WindowsStoreSubmissionMetadata.ps1 `
  -MetadataPath C:\release-inputs\isTranscribe-store-metadata.json `
  -StoreReleaseDirectory artifacts/release/windows-x64/store/2.0.0 `
  -AssetsRoot artifacts/release/windows-x64/store-assets/2.0.0
```

The verifier is read-only. It rejects unconfigured metadata, unresolved placeholders,
test URLs, incomplete review gates, missing or undersized screenshots, and package
version/SHA-256 drift. `-AllowTestFixture` exists only for checked acceptance fixtures
whose metadata explicitly declares `fixture=true`; never use it for Partner Center.

<!-- @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing -->
## Development-signed MSIX

Use an existing code-signing certificate from the Windows certificate store:

```powershell
pwsh -File packaging/windows/Build-WindowsRelease.ps1 `
  -Version 2.0.0 `
  -SigningMode Development `
  -CertificateThumbprint $env:ISTRANSCRIBE_SIGNING_THUMBPRINT
```

For first-time local packaging, `-CreateDevelopmentCertificate` creates a test-only
certificate in the current user's Personal store. The development output includes
its public `.cer` file; it contains no private key. Development artifacts are marked
as ineligible for public distribution in `release-manifest.json`.

Windows requires a self-signed package certificate in the Local Machine Trusted
People store before App Installer can install the development MSIX. A tester with
administrator rights explicitly imports the included `.cer` file, then removes that
trust after uninstalling the development package. The private certificate in
`Cert:\CurrentUser\My` can also be removed by its thumbprint when the machine is no
longer used to build test packages. Public release installation never uses this
manual trust step.

## Production-signed MSIX

Production signing accepts either a certificate already installed in a Personal
certificate store or a PFX supplied by the release environment:

The stable package name, display name, development publisher and approved
production certificate subjects live in `release-identity-policy.json`. The initial
production allowlist is intentionally empty until the real trusted signing identity
is available. Adding or replacing a production subject requires a reviewed policy
change following the rollover instructions in that file. The package name stays
stable across certificate rollovers so Windows keeps one upgrade identity.

```powershell
$env:ISTRANSCRIBE_SIGNING_THUMBPRINT = '<certificate thumbprint>'
$env:ISTRANSCRIBE_SOURCE_REVISION = '<source revision>'

pwsh -File packaging/windows/Build-WindowsRelease.ps1 `
  -Version 2.0.0 `
  -SigningMode Production `
  -Publisher 'CN=Exact subject approved in release-identity-policy.json' `
  -PublisherDisplayName 'isTranscribe' `
  -TimestampUrl 'https://timestamp.example'
```

For PFX-based CI signing, set `ISTRANSCRIBE_SIGNING_PFX_PATH` to a file outside the
repository and place its password in `ISTRANSCRIBE_SIGNING_PFX_PASSWORD`. The script
imports the key temporarily into the current user's certificate store and removes
the imported certificate after packaging. Private material and passwords are not
written to release artifacts or logs.

Development and production builds use the same locked Desktop `win-x64` graph in
`src/IsTranscribe.App.Windows/packages.lock.json`. The self-contained .NET runtime pack
is pinned to `10.0.11` and recorded in the dependency inventory. Refresh the lock
only through an explicit reviewed restore with `--force-evaluate -r win-x64`.
Production builds also require a source revision and an RFC 3161 timestamp URL.
The release manifest pins the system Media Foundation MP3 implementation, exact
48 kHz stereo 128 kbps preset, zero bundled codec binaries and primary-source
evidence URLs.
The Windows SDK packaging tools are restored from the exact
`Microsoft.Windows.SDK.BuildTools` version in `WindowsSdkTools.csproj` and
`packages.lock.json`.

## Output

Production artifacts are promoted atomically to
`artifacts/release/windows-x64/public/<version>/`. Development artifacts use
`artifacts/release/windows-x64/development/<version>/` and include a
`DEVELOPMENT_ONLY.txt` marker.

- `isTranscribe-<version>-win-x64.msix` — signed native installer (`-dev` is
  included in the development filename);
- `release-manifest.json` — package, build, signature and persistence metadata;
- `dependency-license-inventory.json` — direct and transitive NuGet inventory;
- `SHA256SUMS.txt` — SHA-256 checksums for the distributable evidence set;
- `isTranscribe-development-certificate.cer` — public test certificate, present only
  in the development channel.

Release directories contain no PDB or local build-path metadata. Symbols remain an
internal build concern and are not part of the distributable release directory.

Verify a completed release with PowerShell 7 or newer:

```powershell
pwsh -NoProfile -File packaging/windows/Test-WindowsReleaseArtifact.ps1 `
  -ReleaseDirectory artifacts/release/windows-x64/development/2.0.0
```

The verifier performs locked BuildTools restore, WinVerifyTrust and SignTool checks,
validates every MSIX block hash, reconciles the packaged dependency graph with the
checked-in lock files, and prints one compact JSON result. It never installs the
package or writes to a certificate store.

Run the lifecycle preflight without installing either package:

```powershell
pwsh -NoProfile -File packaging/windows/Test-WindowsReleaseLifecycle.ps1 `
  -BaseMsixPath artifacts/release/windows-x64/development/2.0.0/isTranscribe-2.0.0-dev-win-x64.msix `
  -UpgradeMsixPath artifacts/release/windows-x64/development/2.0.1/isTranscribe-2.0.1-dev-win-x64.msix `
  -EvidencePath artifacts/acceptance/INFRA-005/windows-x64/lifecycle.json `
  -PreflightOnly
```

The mutating lifecycle mode refuses to run when the Windows user already has
isTranscribe data, package registration, a legacy Run-key entry, or an app process.
Use it only in a clean disposable Windows user or VM after explicitly trusting the
development certificate. Its running-upgrade step exercises PowerShell deployment;
the end-user App Installer UI, recording, process loopback, custom folders and
autostart remain separate live acceptance scenarios.

### Disposable Windows Sandbox fixture

On Windows Pro, Enterprise or Education with Windows Sandbox already available,
prepare a locked-down lifecycle fixture with the stock Windows PowerShell 5.1:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File packaging/windows/New-WindowsSandboxAcceptance.ps1
```

The generator creates
`artifacts/acceptance/INFRA-005/windows-x64/isTranscribe-installer-acceptance.wsb`
and an empty `sandbox-staging` directory. Pass `-Force` only to replace the generated
configuration file; the generator always refuses a non-empty staging directory.
The file contains absolute paths and is specific to the computer that generated it;
keep the repository and release artifacts on the actual Sandbox host and rerun the
generator there. Its JSON result reports the observed Windows edition, build,
architecture and Sandbox executable, including `hostLaunchPrerequisitesObserved`.

The `.wsb` maps development packages and installer tools read-only. One dedicated
staging directory is writable for two JSON evidence files. Networking, clipboard,
printer, microphone, camera and vGPU access are disabled, and Protected Client is
enabled. The configuration does not launch commands automatically or alter Windows
features and security settings.

Inside the disposable Sandbox, follow
`packaging/windows/windows-sandbox/README.txt`: manually trust the public development
certificate in Local Machine / Trusted People, then run
`Run-IsTranscribe-Installer-Acceptance.cmd`. The wrapper copies the two locked MSIX
files into Sandbox-local storage, checks their SHA-256 hashes and signer, runs a
non-mutating preflight, then exercises the full lifecycle. Review the staging JSON
before moving it into canonical acceptance evidence, and close Sandbox to discard
the installed package and temporary certificate trust.

The fixture requires Windows build 19041 or newer. Windows Sandbox is unavailable
on Windows Home. Microsoft documents its supported
editions, disposable behavior and mapped-folder security boundary in the
[Windows Sandbox overview](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)
and the
[`.wsb` configuration reference](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file).

### Portable clean-machine acceptance kit

Prepare the development-only handoff without copying the repository to the test
computer:

```powershell
pwsh -NoProfile -File packaging/windows/New-WindowsAcceptanceKit.ps1 -Force
```

The command re-verifies both pinned development MSIX packages and atomically writes
`artifacts/acceptance/INFRA-005/windows-x64/portable-kit/`. That directory contains
an extracted 26-file kit, a ZIP with the same exact files, an external `.zip.sha256`
digest and `acceptance-kit-artifact.json`. The builder reopens the ZIP and verifies
every entry before promotion. The kit is marked `TEST ONLY`, carries a public
development certificate with no private key and is ineligible for public release.

Transfer the ZIP and its separate digest through the agreed handoff channel. Verify
the digest before extraction, read `README-FIRST.txt`, and use a disposable clean
Windows x64 VM or user. An administrator explicitly places the bundled public
certificate in Local Machine / Trusted People. Run the acceptance commands from a
separate standard-user account that is not a member of local Administrators:

```text
Run-Clean-VM-Preflight.cmd
Run-Clean-VM-Lifecycle.cmd
```

The runtime verifies the exact file allowlist, package signatures, signer, hashes,
certificate properties and manifest identities with stock Windows PowerShell 5.1.
It holds read leases on local copies during each harness pass and cleans its exact
temporary files afterward. The clean-VM commands reject an elevated or
Administrators-member account before package mutation. Certificate trust,
elevation, Windows features and security settings are always manual host actions.

For a host with Windows Sandbox already available, open PowerShell in the extracted
kit root and generate the host-specific portable configuration beside the kit:

```powershell
powershell.exe -NoLogo -NoProfile `
  -File tools/New-WindowsSandboxConfiguration.ps1 -Force
```

The configuration maps the kit read-only and one dedicated evidence directory
read-write. Networking, clipboard, printer, microphone, camera and vGPU access are
disabled. The JSON files produced in `raw` and `validated-staging` remain staging
evidence until a reviewer confirms the live result and promotes it intentionally.
The portable run covers PowerShell package deployment. App Installer UI, recording,
process loopback, custom folders and autostart still require their documented live
acceptance passes.

Stable semantic versions map to an MSIX revision of `65535`. Supported prerelease
stages are `dev`, `alpha`, `beta` and `rc`, with a numeric sequence up to `9999`.

## Update and uninstall data policy

Opening a newer package with the same production identity performs the user-driven
in-place update. Windows rejects an older package version by default. Settings,
database, logs and recovery state remain in `%LocalAppData%\isTranscribe`.
Recordings remain in the configured recordings folder, which may be under Documents
or another user-selected location. Normal MSIX uninstall removes package binaries,
Start Menu registration and packaged autostart registration while preserving those
user files. Removing application data is a separate explicit user action.

The machine-readable manifest records `LocalApplicationData` and `MyDocuments` as
Windows known-folder identities resolved by the runtime. This remains accurate when
Documents is redirected or managed by OneDrive. The recordings folder continues to
come from the user's `settings.recordingsFolder` choice.

On packaged initialization after an unpackaged release, the saved autostart
preference is applied to the package `StartupTask`. The app-owned legacy
`HKCU Run\isTranscribe` value is removed after the target state is confirmed. The
saved preference, effective state and last observed packaged state are retained
separately. Windows user or policy constraints remain authoritative, and a later
unconstrained re-enable in Windows becomes the newer choice. A transient or
cancelled enable keeps the legacy registration only when it has the exact app
`--autostart` command shape and its executable still exists. Malformed, missing and
unrelated values are cleared and are never reported as effective autostart. Before
onboarding, migration only removes the stale legacy value and never enables startup
implicitly.

## Legacy development bundle

`Build-InstallerBundle.ps1`, `Install-IsTranscribe.cmd`,
`Install-IsTranscribe.ps1` and `Uninstall-IsTranscribe.ps1` form the historical
ZIP/PowerShell development contour. They publish the legacy WPF host and remain
available for migration diagnostics. These files are excluded from the public
release path and are not end-user installer artifacts.

Build the legacy bundle only when that development fixture is explicitly needed:

```powershell
pwsh -File packaging/windows/Build-InstallerBundle.ps1 -Version 0.1.0
```
