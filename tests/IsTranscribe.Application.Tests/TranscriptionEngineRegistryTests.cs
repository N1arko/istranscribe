using System.Runtime.InteropServices;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </summary>
public sealed class TranscriptionEngineRegistryTests
{
    [Fact]
    public void ConstructionValidatesCapabilitiesAndRejectsDuplicateEngineIds()
    {
        var invalid = new RecordingEngine(CreateCapabilities("remote.invalid", "Invalid") with
        {
            PrivacyDisclosure = " "
        });

        Assert.Throws<ArgumentException>(() => new TranscriptionEngineRegistry([invalid]));

        var first = new RecordingEngine(CreateCapabilities("remote.duplicate", "First"));
        var second = new RecordingEngine(CreateCapabilities("remote.duplicate", "Second"));
        var duplicate = Assert.Throws<ArgumentException>(() =>
            new TranscriptionEngineRegistry([first, second]));

        Assert.Contains("remote.duplicate", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(0, first.TranscribeCallCount);
        Assert.Equal(0, second.TranscribeCallCount);
    }

    [Fact]
    public void LookupIsNormalizedOrderedAndDoesNotInvokeAnEngine()
    {
        var zulu = new RecordingEngine(CreateCapabilities("remote.zulu", "Zulu"));
        var alpha = new RecordingEngine(CreateCapabilities("remote.alpha", "Alpha"));

        var registry = new TranscriptionEngineRegistry([zulu, alpha]);

        Assert.Equal(["remote.alpha", "remote.zulu"], registry.Engines
            .Select(static engine => engine.Capabilities.EngineId));
        Assert.Same(alpha, registry.GetRequired(" REMOTE.ALPHA "));
        Assert.True(registry.TryGet(" remote.zulu ", out var resolved));
        Assert.Same(zulu, resolved);
        Assert.False(registry.TryGet("remote.missing", out var missing));
        Assert.Null(missing);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("remote.missing"));
        Assert.Equal(0, alpha.TranscribeCallCount);
        Assert.Equal(0, zulu.TranscribeCallCount);
    }

    [Fact]
    public void CurrentPlatformCatalogReturnsOnlyTheMatchingTargetWithoutInvokingEngines()
    {
        var currentTarget = new TranscriptionPlatformTarget(
            GetCurrentOperatingSystem(),
            RuntimeInformation.ProcessArchitecture);
        var otherArchitecture = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? Architecture.Arm64
            : Architecture.X64;
        var supported = new RecordingEngine(CreateCapabilities(
            "remote.supported",
            "Supported",
            [currentTarget]));
        var unsupported = new RecordingEngine(CreateCapabilities(
            "remote.unsupported",
            "Unsupported",
            [new TranscriptionPlatformTarget(currentTarget.OperatingSystem, otherArchitecture)]));
        var registry = new TranscriptionEngineRegistry([unsupported, supported]);

        var available = registry.GetSupportedCurrentPlatform();

        Assert.Same(supported, Assert.Single(available));
        Assert.Equal(0, supported.TranscribeCallCount);
        Assert.Equal(0, unsupported.TranscribeCallCount);
    }

    private static TranscriptionEngineCapabilities CreateCapabilities(
        string engineId,
        string displayName,
        IReadOnlyList<TranscriptionPlatformTarget>? platforms = null) =>
        new(
            engineId,
            displayName,
            TranscriptionExecutionKind.Remote,
            RequiresNetwork: true,
            PrivacyDisclosure: "Synthetic disclosure.",
            SupportedPlatforms: platforms ??
            [
                new TranscriptionPlatformTarget(
                    GetCurrentOperatingSystem(),
                    RuntimeInformation.ProcessArchitecture)
            ],
            Models: [new TranscriptionModelCapability("model", "Model", IsRecommended: true)],
            SupportedLanguageCodes: [],
            SupportsAutomaticLanguageDetection: true,
            SupportsDiarization: false,
            TimestampCapabilities: TranscriptionTimestampCapabilities.Segment);

    private static TranscriptionOs GetCurrentOperatingSystem() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TranscriptionOs.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? TranscriptionOs.MacOS
                : TranscriptionOs.Linux;

    private sealed class RecordingEngine(TranscriptionEngineCapabilities capabilities)
        : ITranscriptionEngine
    {
        public int TranscribeCallCount { get; private set; }

        public TranscriptionEngineCapabilities Capabilities { get; } = capabilities;

        public ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            TranscribeCallCount++;
            throw new InvalidOperationException("Registry operations must not invoke an engine.");
        }
    }
}
