using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </summary>
public sealed class ApplicationRuntimeTranscriptionProjectionTests
{
    [Fact]
    public void CurrentJobProjectionCarriesProviderNeutralDiagnosticsMetadata()
    {
        var snapshot = ApplicationRuntime.ToRuntimeTranscriptionJob(
            CreateCurrentJob("provider-request:abc-123"));

        Assert.Equal("remote.openrouter", snapshot.EngineId);
        Assert.Equal("provider/whisper", snapshot.ModelId);
        Assert.Equal(2, snapshot.CurrentChunkIndex);
        Assert.Equal(4, snapshot.ChunkCount);
        Assert.Equal("provider-request:abc-123", snapshot.ProviderRequestId);
    }

    [Fact]
    public void CurrentJobProjectionDropsSensitiveUnboundedOrMalformedProviderRequestIds()
    {
        var requestIds = new[]
        {
            "gsk_synthetic-secret",
            new string('x', 129),
            "request id with spaces"
        };

        Assert.All(
            requestIds,
            requestId => Assert.Null(ApplicationRuntime
                .ToRuntimeTranscriptionJob(CreateCurrentJob(requestId))
                .ProviderRequestId));
    }

    [Fact]
    public void ProviderUsageProjectionReadsPersistedWebJson()
    {
        const string json =
            """
            {"inputUnits":12,"outputUnits":34,"audioSeconds":9.2,"reportedCost":0.0042,"currency":"USD"}
            """;

        var usage = ApplicationRuntime.ParseTranscriptionUsage(json);

        Assert.NotNull(usage);
        Assert.Equal(12, usage.InputUnits);
        Assert.Equal(34, usage.OutputUnits);
        Assert.Equal(9.2, usage.AudioSeconds);
        Assert.Equal(0.0042m, usage.ReportedCost);
        Assert.Equal("USD", usage.Currency);
    }

    [Fact]
    public void LocalDiagnosticsProjectionUsesDurableRuntimeIdentity()
    {
        var job = CreateCurrentJob(providerRequestId: string.Empty) with
        {
            EngineId = "local.whisper",
            ExecutionKind = TranscriptionExecutionKind.Local,
            ModelId = "small",
            LocalDiagnostics = new CurrentLocalTranscriptionDiagnosticsListItem(
                RequestedBackend: "metal",
                ResolvedBackend: "cpu",
                ThreadCount: 6,
                RuntimeVersion: "1.9.1",
                NativeBundleManifestSha256: new string('a', 64),
                ModelSha256: new string('b', 64),
                ProcessingDurationMilliseconds: 3210)
        };

        var diagnostics = ApplicationRuntime.ToRuntimeTranscriptionJob(job).LocalDiagnostics;

        Assert.NotNull(diagnostics);
        Assert.Equal("metal", diagnostics.RequestedBackend);
        Assert.Equal("cpu", diagnostics.ResolvedBackend);
        Assert.Equal(6, diagnostics.ThreadCount);
        Assert.Equal(3210, diagnostics.ProcessingDurationMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"audioSeconds\":-1}")]
    public void ProviderUsageProjectionRejectsMissingOrInvalidPayload(string json)
    {
        Assert.Null(ApplicationRuntime.ParseTranscriptionUsage(json));
    }

    private static CurrentTranscriptionJobListItem CreateCurrentJob(string providerRequestId) => new(
        JobId: "job-diagnostics",
        EngineId: "remote.openrouter",
        ExecutionKind: TranscriptionExecutionKind.Remote,
        ModelId: "provider/whisper",
        Status: TranscriptionJobStatus.Processing,
        Progress: 0.5,
        CurrentChunkIndex: 2,
        NextAttemptAtUtc: null,
        StableErrorCode: null,
        ErrorMessage: "bounded operational message",
        ArtifactPublicationState: TranscriptionArtifactPublicationState.None,
        TranscriptMarkdownPath: null,
        TranscriptJsonPath: null,
        UsageJson: null,
        ChunkCount: 4,
        ProviderRequestId: providerRequestId);
}
