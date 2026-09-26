using System.Reflection;
using System.Runtime.InteropServices;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#verification
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#future-activation
/// </summary>
public sealed class TranscriptionContractsTests
{
    [Fact]
    public void CapabilitiesDescribeLocalAndRemoteEnginesWithoutActivationPolicy()
    {
        var local = CreateCapabilities(
            TranscriptionExecutionKind.Local,
            requiresNetwork: false,
            "Audio remains on this device.",
            new TranscriptionResourceRequirements(
                MinimumSystemMemoryBytes: 4L * 1024 * 1024 * 1024,
                MinimumGpuMemoryBytes: 2L * 1024 * 1024 * 1024,
                MinimumFreeDiskBytes: 1L * 1024 * 1024 * 1024));
        var remote = CreateCapabilities(
            TranscriptionExecutionKind.Remote,
            requiresNetwork: true,
            "Audio is sent to the selected remote engine.",
            minimumResources: null);

        local.Validate();
        remote.Validate();

        Assert.Equal("example.engine", local.EngineId);
        Assert.Equal("Example engine", local.DisplayName);
        Assert.Equal(TranscriptionExecutionKind.Local, local.ExecutionKind);
        Assert.False(local.RequiresNetwork);
        Assert.Contains(
            new TranscriptionPlatformTarget(TranscriptionOperatingSystem.Windows, Architecture.X64),
            local.SupportedPlatforms);
        Assert.Contains(local.Models, model => model.Id == "balanced" && model.DisplayName == "Balanced");
        Assert.Equal(["en", "ru"], local.SupportedLanguageCodes);
        Assert.True(local.SupportsAutomaticLanguageDetection);
        Assert.True(local.SupportsDiarization);
        Assert.Equal(
            TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word,
            local.TimestampCapabilities);
        Assert.Equal(4L * 1024 * 1024 * 1024, local.MinimumResources?.MinimumSystemMemoryBytes);
        Assert.Equal(TranscriptionExecutionKind.Remote, remote.ExecutionKind);
        Assert.True(remote.RequiresNetwork);
        Assert.Null(remote.MinimumResources);
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
    [Fact]
    public void DynamicModelCatalogMayStartEmptyOnlyWhenDiscoveryIsDeclared()
    {
        var dynamicCatalog = CreateCapabilities(
            TranscriptionExecutionKind.Remote,
            requiresNetwork: true,
            "Audio is sent to the selected remote engine.",
            minimumResources: null) with
        {
            Models = [],
            SupportsModelDiscovery = true
        };

        dynamicCatalog.Validate();
        Assert.Throws<ArgumentException>(() =>
            (dynamicCatalog with { SupportsModelDiscovery = false }).Validate());
    }

    [Fact]
    public void RequestValidationRequiresSessionAndPrimaryAudioArtifact()
    {
        var request = new TranscriptionRequest(
            Guid.NewGuid(),
            @"C:\Recordings\meeting.mp3",
            ModelId: "whisper",
            Language: "ru",
            SourceStart: TimeSpan.FromSeconds(10),
            SourceEnd: TimeSpan.FromSeconds(20),
            JobId: Guid.NewGuid(),
            ChunkId: "chunk-1");

        request.Validate();

        Assert.Throws<ArgumentException>(() => (request with { SessionId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (request with { PrimaryAudioArtifactPath = "  " }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { ModelId = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { Language = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { JobId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { ChunkId = " " }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (request with { SourceEnd = request.SourceStart }).Validate());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ProgressRejectsFractionsOutsideFiniteUnitInterval(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TranscriptionProgress(TranscriptionProgressStage.Transcribing, fraction));
    }

    [Fact]
    public void ResultFactoriesKeepCompletedAndFailedStatesCoherent()
    {
        var word = new TranscriptionWord(
            "Hello",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            0.98);
        var segment = new TranscriptionSegment(
            "Hello",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            "speaker-1",
            [word]);
        var metadata = new TranscriptionResultMetadata(
            ResolvedModelId: "whisper",
            RequestId: "request-1",
            Usage: new TranscriptionUsage(AudioSeconds: 1),
            SourceStart: TimeSpan.FromSeconds(1),
            SourceEnd: TimeSpan.FromSeconds(2),
            AudioDuration: TimeSpan.FromSeconds(1));
        var completed = TranscriptionResult.Completed("Hello", "en", [segment], metadata);
        var error = new TranscriptionError(
            TranscriptionErrorCategory.EngineUnavailable,
            "engine_unavailable",
            "The engine is unavailable.",
            suggestedDelay: TimeSpan.FromMinutes(1),
            requestId: "request-2",
            disposition: TranscriptionFailureDisposition.TryAgain);
        var failed = TranscriptionResult.Failed(error);

        Assert.True(completed.Succeeded);
        Assert.Equal(TranscriptionResultStatus.Completed, completed.Status);
        Assert.Equal("Hello", completed.Text);
        Assert.Equal("en", completed.DetectedLanguage);
        Assert.Equal([segment], completed.Segments);
        Assert.Equal([word], completed.Segments[0].Words);
        Assert.Same(metadata, completed.Metadata);
        Assert.Null(completed.Error);

        Assert.False(failed.Succeeded);
        Assert.Equal(TranscriptionResultStatus.Failed, failed.Status);
        Assert.Null(failed.Text);
        Assert.Null(failed.DetectedLanguage);
        Assert.Empty(failed.Segments);
        Assert.Same(error, failed.Error);
        Assert.Equal(TranscriptionFailureDisposition.TryAgain, failed.Error?.Disposition);
        Assert.Throws<ArgumentNullException>(() => TranscriptionResult.Completed(null!));
        Assert.Throws<ArgumentNullException>(() => TranscriptionResult.Failed(null!));
        Assert.Throws<ArgumentException>(() => new TranscriptionSegment(
            "invalid",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task EngineContractReportsProgressAndHonorsCancellation()
    {
        var completedEngine = new FakeTranscriptionEngine(releaseImmediately: true);
        var completedProgress = new ProgressCollector<TranscriptionProgress>();
        var request = new TranscriptionRequest(Guid.NewGuid(), @"C:\Recordings\meeting.mp3");

        var completed = await completedEngine.TranscribeAsync(
            request,
            completedProgress,
            CancellationToken.None);

        Assert.True(completed.Succeeded);
        Assert.Equal(
            [
                TranscriptionProgressStage.Preparing,
                TranscriptionProgressStage.Transcribing,
                TranscriptionProgressStage.Finalizing
            ],
            completedProgress.Values.Select(static item => item.Stage));
        Assert.Equal([0d, 0.5d, 1d], completedProgress.Values.Select(static item => item.Fraction));

        var canceledEngine = new FakeTranscriptionEngine(releaseImmediately: false);
        using var cancellation = new CancellationTokenSource();
        var canceledTask = canceledEngine
            .TranscribeAsync(request, progress: null, cancellation.Token)
            .AsTask();
        await canceledEngine.Started.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask);
    }

    [Fact]
    public void PublicTranscriptionSurfaceHasNoProviderSpecificOrNetworkTypes()
    {
        var assembly = typeof(ITranscriptionEngine).Assembly;
        var transcriptionTypes = assembly
            .GetExportedTypes()
            .Where(static type => string.Equals(
                type.Namespace,
                "IsTranscribe.Core.Transcription",
                StringComparison.Ordinal))
            .ToArray();
        var publicSurface = string.Join(
            '\n',
            transcriptionTypes.SelectMany(static type =>
                new[] { type.FullName ?? type.Name }
                    .Concat(type
                        .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Select(member => $"{type.FullName}.{member}"))
                    .Concat(type
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .SelectMany(static method => method.GetParameters())
                        .Select(parameter =>
                            $"{type.FullName}.{parameter.Member.Name}.{parameter.Name}:{parameter.ParameterType}"))
                    .Concat(type
                        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                        .SelectMany(static constructor => constructor.GetParameters())
                        .Select(parameter =>
                            $"{type.FullName}.ctor.{parameter.Name}:{parameter.ParameterType}"))));

        Assert.NotEmpty(transcriptionTypes);
        foreach (var forbidden in new[]
                 {
                     "Fireworks",
                     "HttpClient",
                     "HttpRequest",
                     "HttpResponse",
                     "ApiKey",
                     "Endpoint",
                     "RawJson",
                     "Retry"
                 })
        {
            Assert.DoesNotContain(forbidden, publicSurface, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "System.Net.Http", StringComparison.Ordinal)
                || string.Equals(reference.Name, "IsTranscribe.Host", StringComparison.Ordinal)
                || string.Equals(reference.Name, "IsTranscribe.Persistence", StringComparison.Ordinal));
    }

    private static TranscriptionEngineCapabilities CreateCapabilities(
        TranscriptionExecutionKind executionKind,
        bool requiresNetwork,
        string privacyDisclosure,
        TranscriptionResourceRequirements? minimumResources) => new(
        EngineId: "example.engine",
        DisplayName: "Example engine",
        ExecutionKind: executionKind,
        RequiresNetwork: requiresNetwork,
        PrivacyDisclosure: privacyDisclosure,
        SupportedPlatforms:
        [
            new TranscriptionPlatformTarget(TranscriptionOperatingSystem.Windows, Architecture.X64),
            new TranscriptionPlatformTarget(TranscriptionOperatingSystem.MacOS, Architecture.Arm64)
        ],
        Models: [new TranscriptionModelCapability("balanced", "Balanced")],
        SupportedLanguageCodes: ["en", "ru"],
        SupportsAutomaticLanguageDetection: true,
        SupportsDiarization: true,
        TimestampCapabilities:
            TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word,
        MinimumResources: minimumResources);

    private sealed class FakeTranscriptionEngine : ITranscriptionEngine
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTranscriptionEngine(bool releaseImmediately)
        {
            if (releaseImmediately)
            {
                _release.SetResult();
            }
        }

        public TranscriptionEngineCapabilities Capabilities { get; } = CreateCapabilities(
            TranscriptionExecutionKind.Local,
            requiresNetwork: false,
            "Audio remains on this device.",
            minimumResources: null);

        public Task Started => _started.Task;

        public async ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            request.Validate();
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Preparing, 0));
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Transcribing, 0.5));
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Finalizing, 1));
            return TranscriptionResult.Completed("Transcript", "en");
        }
    }

    private sealed class ProgressCollector<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
