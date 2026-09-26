using System.Security.Cryptography;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;
using IsTranscribe.Transcription.Worker.Runtime.Native;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// Opt-in real-model gate for pinned macOS and Windows native payloads. Normal test runs
/// perform no download; acceptance supplies all four absolute paths explicitly.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
[Collection(WorkerLifecycleCollection.Name)]
public sealed class NativeWhisperRealModelAcceptanceTests
{
    private const string LibraryVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_LIBRARY";
    private const string ModelVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_MODEL";
    private const string RussianWaveVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_RU_WAV";
    private const string EnglishWaveVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_EN_WAV";
    private const string PortugueseWaveVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_PT_WAV";
    private const string BackendVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_BACKEND";
    private const string ExpectedBackendVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_EXPECTED_BACKEND";
    private const string WorkerVariable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_WORKER";
    private const string ModelSha256Variable = "ISTRANSCRIBE_WHISPER_ACCEPTANCE_MODEL_SHA256";
    private const string BaseModelSha256 =
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";

    [Fact]
    public async Task PinnedPublicModelTranscribesRussianAndEnglishOnSelectedNativeBackend()
    {
        var paths = ReadAcceptancePaths();
        if (paths is null)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var nativeDirectory = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(nativeDirectory);
        var (host, libraryName) = ResolveHost(paths.RequestedBackend, paths.ExpectedBackend);
        File.Copy(paths.Library, Path.Combine(nativeDirectory, libraryName));

        using var backend = new NativeWhisperBackend(
            nativeDirectory,
            host,
            SystemNativeLibraryLoader.Instance);
        var probe = await backend.ProbeAsync(
            new WorkerProbePayload(paths.Model, paths.ModelSha256, paths.RequestedBackend, MaximumThreads: 4),
            CancellationToken.None);

        Assert.Equal(paths.ExpectedBackend, probe.Backend);
        await AssertFixtureAsync(
            backend,
            paths.Model,
            paths.ModelSha256,
            paths.RussianWave,
            paths.RequestedBackend,
            paths.ExpectedBackend,
            "ru",
            "локал");
        await AssertFixtureAsync(
            backend,
            paths.Model,
            paths.ModelSha256,
            paths.EnglishWave,
            paths.RequestedBackend,
            paths.ExpectedBackend,
            "en",
            "transcri");
        var portuguese = Environment.GetEnvironmentVariable(PortugueseWaveVariable);
        if (!string.IsNullOrWhiteSpace(portuguese))
            await AssertFixtureAsync(backend, paths.Model, paths.ModelSha256, portuguese,
                paths.RequestedBackend, paths.ExpectedBackend, "pt", "local");
    }

    [Fact]
    public async Task PublishedIsolatedWorkerTranscribesPinnedModelOverAnonymousPipes()
    {
        var paths = ReadAcceptancePaths();
        var workerPath = ReadOptionalWorkerPath();
        if (paths is null || workerPath is null)
        {
            return;
        }

        await using var coordinator = new LocalWorkerCoordinator(
            new AnonymousPipeWorkerProcessLauncher(),
            LocalWorkerCoordinatorOptions.Default with
            {
                HandshakeTimeout = TimeSpan.FromSeconds(30),
            });
        var initial = await coordinator.StartAsync(workerPath, CancellationToken.None);
        var probe = await coordinator.ProbeAsync(
            new WorkerProbePayload(paths.Model, paths.ModelSha256, paths.RequestedBackend, MaximumThreads: 4),
            CancellationToken.None);

        Assert.Equal("initialized", initial.State);
        Assert.Equal("probe_completed", probe.State);
        Assert.Equal(paths.ExpectedBackend, probe.Backend);
        await AssertWorkerFixtureAsync(coordinator, paths, paths.RussianWave, "ru", "локал", 0);
        await AssertWorkerFixtureAsync(coordinator, paths, paths.EnglishWave, "en", "transcri", 1);
        await coordinator.ShutdownAsync(CancellationToken.None);
    }

    private static async Task AssertFixtureAsync(
        NativeWhisperBackend backend,
        string modelPath,
        string modelSha256,
        string wavePath,
        string requestedBackend,
        string expectedBackend,
        string expectedLanguage,
        string expectedTextFragment)
    {
        var wave = NormalizedWaveReader.Read(wavePath);
        await using var input = File.OpenRead(wavePath);
        var inputSha256 = Convert.ToHexString(await SHA256.HashDataAsync(
                input,
                CancellationToken.None))
            .ToLowerInvariant();
        var progress = new List<WorkerProgressPayload>();
        var result = await backend.TranscribeAsync(
            new WorkerStartPayload(
                wavePath,
                inputSha256,
                modelPath,
                modelSha256,
                "auto",
                requestedBackend,
                StartMilliseconds: 0,
                EndMilliseconds: wave.DurationMilliseconds,
                MaximumThreads: 4),
            new InlineProgress<WorkerProgressPayload>(progress.Add),
            CancellationToken.None);

        Assert.Equal(expectedLanguage, result.Language);
        Assert.Equal(expectedBackend, result.Backend);
        Assert.Equal("whisper.cpp-v1.9.1", result.RuntimeVersion);
        Assert.Equal(modelSha256, result.ModelSha256);
        Assert.NotEmpty(result.Segments);
        // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition
        Assert.All(result.Segments, static segment => Assert.True(
            segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 1,
            "Native output must expose word-sized timestamp items."));
        Assert.True(
            string.Join(' ', result.Segments.Select(static segment => segment.Text))
                .Contains(expectedTextFragment, StringComparison.OrdinalIgnoreCase),
            "The expected language marker was absent from the native result.");
        Assert.All(result.Segments, segment =>
        {
            Assert.InRange(segment.StartMilliseconds, 0, wave.DurationMilliseconds);
            Assert.InRange(segment.EndMilliseconds, segment.StartMilliseconds, wave.DurationMilliseconds);
        });
        Assert.Contains(progress, static update => update.Stage == "finalizing");
    }

    private static async Task AssertWorkerFixtureAsync(
        LocalWorkerCoordinator coordinator,
        AcceptancePaths paths,
        string wavePath,
        string expectedLanguage,
        string expectedTextFragment,
        int chunkIndex)
    {
        var wave = NormalizedWaveReader.Read(wavePath);
        await using var input = File.OpenRead(wavePath);
        var inputSha256 = Convert.ToHexString(await SHA256.HashDataAsync(
                input,
                CancellationToken.None))
            .ToLowerInvariant();
        var progress = new List<WorkerProgressPayload>();
        var result = await coordinator.TranscribeAsync(
            "acceptance-job",
            chunkIndex,
            new WorkerStartPayload(
                wavePath,
                inputSha256,
                paths.Model,
                paths.ModelSha256,
                "auto",
                paths.RequestedBackend,
                StartMilliseconds: 0,
                EndMilliseconds: wave.DurationMilliseconds,
                MaximumThreads: 4),
            new InlineProgress<WorkerProgressPayload>(progress.Add),
            CancellationToken.None);

        Assert.Equal(expectedLanguage, result.Language);
        Assert.Equal(paths.ExpectedBackend, result.Backend);
        Assert.NotEmpty(result.Segments);
        Assert.All(result.Segments, segment =>
        {
            Assert.False(string.IsNullOrWhiteSpace(segment.Text), "The worker returned a whitespace-only word.");
            Assert.InRange(segment.StartMilliseconds, 0, wave.DurationMilliseconds);
            Assert.InRange(segment.EndMilliseconds, segment.StartMilliseconds, wave.DurationMilliseconds);
        });
        Assert.True(
            string.Join(' ', result.Segments.Select(static segment => segment.Text))
                .Contains(expectedTextFragment, StringComparison.OrdinalIgnoreCase),
            "The expected language marker was absent from the isolated worker result.");
        Assert.Contains(progress, static update => update.Stage == "finalizing");
    }

    private static AcceptancePaths? ReadAcceptancePaths()
    {
        var values = new[]
        {
            Environment.GetEnvironmentVariable(LibraryVariable),
            Environment.GetEnvironmentVariable(ModelVariable),
            Environment.GetEnvironmentVariable(RussianWaveVariable),
            Environment.GetEnvironmentVariable(EnglishWaveVariable)
        };
        if (values.All(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        Assert.All(values, static value => Assert.False(string.IsNullOrWhiteSpace(value)));
        var absolute = values.Select(static value => Path.GetFullPath(value!)).ToArray();
        Assert.All(absolute, static path => Assert.True(File.Exists(path), path));
        var backend = Environment.GetEnvironmentVariable(BackendVariable);
        if (string.IsNullOrWhiteSpace(backend))
        {
            backend = OperatingSystem.IsWindows() ? "cpu" : "metal";
        }

        Assert.Contains(backend, new[] { "auto", "cpu", "vulkan", "metal" });
        var expectedBackend = Environment.GetEnvironmentVariable(ExpectedBackendVariable);
        if (string.IsNullOrWhiteSpace(expectedBackend))
        {
            expectedBackend = backend;
        }

        Assert.Contains(expectedBackend, new[] { "cpu", "vulkan", "metal" });
        var modelSha256 = Environment.GetEnvironmentVariable(ModelSha256Variable);
        if (string.IsNullOrWhiteSpace(modelSha256))
        {
            modelSha256 = BaseModelSha256;
        }

        modelSha256 = modelSha256.Trim().ToLowerInvariant();
        Assert.Equal(64, modelSha256.Length);
        Assert.True(modelSha256.All(Uri.IsHexDigit));
        return new AcceptancePaths(
            absolute[0],
            absolute[1],
            absolute[2],
            absolute[3],
            backend,
            expectedBackend,
            modelSha256);
    }

    private static string? ReadOptionalWorkerPath()
    {
        var value = Environment.GetEnvironmentVariable(WorkerVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var absolute = Path.GetFullPath(value);
        Assert.True(File.Exists(absolute), absolute);
        return absolute;
    }

    private static (NativeWhisperHost Host, string LibraryName) ResolveHost(
        string requestedBackend,
        string expectedBackend) =>
        OperatingSystem.IsWindows()
            ? expectedBackend switch
            {
                "cpu" => (NativeWhisperHost.WindowsX64, NativeWhisperBackend.WindowsCpuLibraryName),
                "vulkan" => (NativeWhisperHost.WindowsX64, NativeWhisperBackend.WindowsVulkanLibraryName),
                _ => throw new InvalidOperationException(
                    $"Windows acceptance does not support '{requestedBackend}' resolving to '{expectedBackend}'.")
            }
            : expectedBackend is "metal" or "cpu"
                ? (NativeWhisperHost.MacOsArm64, NativeWhisperBackend.MacOsLibraryName)
                : throw new InvalidOperationException(
                    $"macOS acceptance does not support '{requestedBackend}' resolving to '{expectedBackend}'.");

    private sealed record AcceptancePaths(
        string Library,
        string Model,
        string RussianWave,
        string EnglishWave,
        string RequestedBackend,
        string ExpectedBackend,
        string ModelSha256);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"istranscribe-native-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
