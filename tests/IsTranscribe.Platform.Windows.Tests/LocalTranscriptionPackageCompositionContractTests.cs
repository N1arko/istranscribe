using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceptance
/// </summary>
public sealed class LocalTranscriptionPackageCompositionContractTests
{
    private const string ManifestSha256 =
        "fe37a0d085edaab7925c25f8e62514fe714dda4c66cfe3f2c48e2bea191d2feb";

    [Fact]
    public async Task PackageVerifierAcceptsExactReceiptThenRejectsWorkerMutation()
    {
        using var fixture = new SyntheticPackageFixture();
        fixture.Create();

        var accepted = await RunPowerShellAsync(
            fixture.VerifierPath,
            "-Rid",
            "win-x64",
            "-PackageRoot",
            fixture.PackageRoot);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("\"Present\":true", accepted.Output, StringComparison.Ordinal);

        await File.AppendAllTextAsync(
            Path.Combine(fixture.PackageRoot, "IsTranscribe.Transcription.Worker.dll"),
            "mutation");

        var rejected = await RunPowerShellAsync(
            fixture.VerifierPath,
            "-Rid",
            "win-x64",
            "-PackageRoot",
            fixture.PackageRoot);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("worker_payload_mismatch", rejected.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerRejectsANonidenticalSharedPublishFile()
    {
        using var fixture = new SyntheticPackageFixture();
        Directory.CreateDirectory(fixture.PackageRoot);
        Directory.CreateDirectory(fixture.WorkerRoot);
        Directory.CreateDirectory(fixture.NativeOutputRoot);
        foreach (var path in fixture.RequiredWorkerPaths)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.WorkerRoot, path), $"worker:{path}");
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.PackageRoot, "hostfxr.dll"), "application-hostfxr");

        var result = await RunPowerShellAsync(
            fixture.ComposerPath,
            "-Rid",
            "win-x64",
            "-ApplicationPublishRoot",
            fixture.PackageRoot,
            "-WorkerPublishRoot",
            fixture.WorkerRoot,
            "-NativeOutputRoot",
            fixture.NativeOutputRoot,
            "-ManifestPath",
            fixture.ManifestPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("publish_collision_mismatch", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityVerificationAcceptsAnAbsentContourAndRejectsAPartialOne()
    {
        using var fixture = new SyntheticPackageFixture();
        Directory.CreateDirectory(fixture.PackageRoot);

        var absent = await RunPowerShellAsync(
            fixture.VerifierPath,
            "-Rid",
            "win-x64",
            "-PackageRoot",
            fixture.PackageRoot,
            "-AllowAbsent");
        Assert.Equal(0, absent.ExitCode);
        Assert.Contains("\"Present\":false", absent.Output, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            Path.Combine(fixture.PackageRoot, "IsTranscribe.Transcription.Worker.dll"),
            "partial-worker");

        var partial = await RunPowerShellAsync(
            fixture.VerifierPath,
            "-Rid",
            "win-x64",
            "-PackageRoot",
            fixture.PackageRoot,
            "-AllowAbsent");
        Assert.NotEqual(0, partial.ExitCode);
        Assert.Contains("partial local-transcription payload", partial.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsReleasePublishesAndVerifiesTheIsolatedRuntimeContour()
    {
        var root = FindRepositoryRoot();
        var release = File.ReadAllText(Path.Combine(root, "packaging", "windows", "Build-WindowsRelease.ps1"));
        var notices = File.ReadAllText(Path.Combine(root, "packaging", "windows", "New-ThirdPartyNotices.ps1"));
        var noticeDeterminism = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "windows",
            "Test-ThirdPartyNoticeDeterminism.ps1"));
        var composer = File.ReadAllText(Path.Combine(root, "eng", "transcription", "Compose-LocalTranscriptionPayload.ps1"));
        var releaseVerifier = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "windows",
            "Test-WindowsReleaseArtifact.ps1"));
        var storeVerifier = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "windows",
            "Test-WindowsStoreSubmissionArtifact.ps1"));
        var workerProgram = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Transcription.Worker",
            "Program.cs"));
        var nativeFactory = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Transcription.Worker",
            "Runtime",
            "Native",
            "NativeWhisperBackend.cs"));
        var windowsDecoder = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Platform.Windows",
            "Audio",
            "Transcription",
            "WindowsMediaFoundationAudioChunkDecoder.cs"));
        var appProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.App.Windows",
            "IsTranscribe.App.Windows.csproj"));
        var workerProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Transcription.Worker",
            "IsTranscribe.Transcription.Worker.csproj"));

        Assert.Contains("src\\IsTranscribe.Transcription.Worker\\IsTranscribe.Transcription.Worker.csproj", release, StringComparison.Ordinal);
        Assert.Contains("$workerPublishRoot = Join-Path $stagingRoot \"worker-publish\"", release, StringComparison.Ordinal);
        Assert.Contains("\"--self-contained\", \"true\"", release, StringComparison.Ordinal);
        Assert.Contains("Compose-LocalTranscriptionPayload.ps1", release, StringComparison.Ordinal);
        Assert.Contains("Test-LocalTranscriptionPayload.ps1", release, StringComparison.Ordinal);
        Assert.Contains("-NativeRuntimeInventoryPath", release, StringComparison.Ordinal);
        Assert.Contains("Native license:", notices, StringComparison.Ordinal);
        Assert.Contains("-NativeRuntimeInventoryPath $nativeInventoryPath", noticeDeterminism, StringComparison.Ordinal);
        Assert.Contains("-NativeRuntimeManifestPath $nativeManifestPath", noticeDeterminism, StringComparison.Ordinal);
        Assert.Contains("shared-byte-identical", composer, StringComparison.Ordinal);
        Assert.Contains(ManifestSha256, composer, StringComparison.Ordinal);
        Assert.Contains("Test-LocalTranscriptionPayload.ps1", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalTranscriptionPayload", storeVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MFAudioFormat_AAC\"", releaseVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MFAudioFormat_AAC\"", storeVerifier, StringComparison.Ordinal);
        Assert.Contains("-Expected 43 -Field \"Exact notice-policy package count\"", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("exact reviewed 43/33 graph", storeVerifier, StringComparison.Ordinal);
        Assert.Contains("AudioSubtypes.MFAudioFormat_AAC", windowsDecoder, StringComparison.Ordinal);
        Assert.Contains(ManifestSha256, storeVerifier, StringComparison.Ordinal);
        Assert.Contains("CreateForAppBase(", workerProgram, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", workerProgram, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(applicationBaseDirectory, \"native\")", nativeFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Description=", release, StringComparison.Ordinal);
        Assert.Contains("<Description>Local meeting recorder</Description>", appProject, StringComparison.Ordinal);
        Assert.Contains("<Description>Isolated local transcription worker</Description>", workerProject, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string script, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            string.Concat(await standardOutput, Environment.NewLine, await standardError));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed class SyntheticPackageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-local-package-{Guid.NewGuid():N}");

        public string PackageRoot => Path.Combine(_root, "package");

        public string WorkerRoot => Path.Combine(_root, "worker");

        public string NativeOutputRoot => Path.Combine(_root, "native-output");

        public string RepositoryRoot { get; } = FindRepositoryRoot();

        public string ManifestPath => Path.Combine(
            RepositoryRoot,
            "native",
            "whisper",
            "runtime-manifest.v1.json");

        public string ComposerPath => Path.Combine(
            RepositoryRoot,
            "eng",
            "transcription",
            "Compose-LocalTranscriptionPayload.ps1");

        public string VerifierPath => Path.Combine(
            RepositoryRoot,
            "eng",
            "transcription",
            "Test-LocalTranscriptionPayload.ps1");

        public string[] RequiredWorkerPaths =>
        [
            "IsTranscribe.Transcription.Worker.exe",
            "IsTranscribe.Transcription.Worker.dll",
            "IsTranscribe.Transcription.Worker.deps.json",
            "IsTranscribe.Transcription.Worker.runtimeconfig.json",
            "hostfxr.dll"
        ];

        public void Create()
        {
            var nativeDirectory = Path.Combine(PackageRoot, "native");
            Directory.CreateDirectory(nativeDirectory);
            File.Copy(
                ManifestPath,
                Path.Combine(nativeDirectory, "runtime-manifest.v1.json"));
            Assert.Equal(ManifestSha256, Sha256(ManifestPath));

            var workerFiles = new List<object>();
            foreach (var relativePath in RequiredWorkerPaths)
            {
                var path = Path.Combine(PackageRoot, relativePath);
                var content = relativePath switch
                {
                    "IsTranscribe.Transcription.Worker.deps.json" =>
                        "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/win-x64\"}}",
                    "IsTranscribe.Transcription.Worker.runtimeconfig.json" =>
                        "{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}",
                    _ => $"worker:{relativePath}"
                };
                File.WriteAllText(path, content, new UTF8Encoding(false));
                workerFiles.Add(new
                {
                    path = relativePath,
                    sizeBytes = new FileInfo(path).Length,
                    sha256 = Sha256(path),
                    disposition = relativePath == "hostfxr.dll"
                        ? "shared-byte-identical"
                        : "worker"
                });
            }

            var nativeLibraryPath = Path.Combine(nativeDirectory, "istranscribe_whisper_v1.dll");
            File.WriteAllText(nativeLibraryPath, "synthetic-inspected-native", new UTF8Encoding(false));
            var speakerFiles = new List<object>();
            foreach (var relativePath in new[]
                     {
                         "onnxruntime.dll",
                         "sherpa-onnx-c-api.dll",
                         "speaker-licenses/SOURCE-AND-NOTICES.md",
                         "speaker-licenses/license-01.txt",
                         "speaker-licenses/license-02.txt",
                         "speaker-licenses/license-03.txt",
                         "speaker-licenses/license-04.txt",
                         "speaker-licenses/license-05.txt",
                         "speaker-licenses/license-06.txt",
                         "speaker-licenses/license-07.txt",
                         "speaker-licenses/license-08.txt",
                         "speaker-licenses/license-09.txt",
                         "speaker-licenses/license-10.txt",
                         "speaker-licenses/license-11.txt",
                         "speaker-licenses/license-12.txt",
                         "speaker-licenses/license-13.txt",
                         "speaker-licenses/license-14.txt"
                     })
            {
                var path = Path.Combine(PackageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"speaker:{relativePath}", new UTF8Encoding(false));
                speakerFiles.Add(new
                {
                    path = relativePath,
                    sizeBytes = new FileInfo(path).Length,
                    sha256 = Sha256(path)
                });
            }
            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            var manifest = manifestDocument.RootElement;
            var licenses = manifest.GetProperty("licenses")
                .EnumerateArray()
                .Select(static license => new
                {
                    component = license.GetProperty("component").GetString(),
                    spdx = license.GetProperty("spdx").GetString(),
                    checkedInPath = license.GetProperty("checkedInPath").GetString(),
                    sha256 = license.GetProperty("sha256").GetString()
                })
                .ToArray();
            var receipt = new
            {
                schemaVersion = "istranscribe-local-runtime-package-v1",
                spec = "spec://modules/app/FEAT-016-local-whisper-transcription#verification",
                rid = "win-x64",
                speaker = new
                {
                    runtimeVersion = "sherpa-onnx/1.12.14",
                    ttsEnabled = false,
                    eigenMpl2Only = true,
                    files = speakerFiles
                },
                worker = new
                {
                    executable = "IsTranscribe.Transcription.Worker.exe",
                    selfContained = true,
                    copiedFileCount = 4,
                    sharedByteIdenticalFileCount = 1,
                    files = workerFiles
                },
                native = new
                {
                    directory = "native",
                    runtimeManifestPath = "native/runtime-manifest.v1.json",
                    runtimeManifestSha256 = ManifestSha256,
                    sourceCommit = manifest.GetProperty("source").GetProperty("commit").GetString(),
                    sourceArchiveSha256 = manifest.GetProperty("source").GetProperty("archive").GetProperty("sha256").GetString(),
                    libraries = new[]
                    {
                        new
                        {
                            variant = "cpu",
                            required = true,
                            path = "native/istranscribe_whisper_v1.dll",
                            sizeBytes = new FileInfo(nativeLibraryPath).Length,
                            sha256 = Sha256(nativeLibraryPath),
                            architectures = new[] { "x64" },
                            directDependencies = new[] { "ADVAPI32.dll", "KERNEL32.dll" }
                        }
                    },
                    licenses
                }
            };
            File.WriteAllText(
                Path.Combine(nativeDirectory, "runtime-inventory.v1.json"),
                JsonSerializer.Serialize(receipt),
                new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
