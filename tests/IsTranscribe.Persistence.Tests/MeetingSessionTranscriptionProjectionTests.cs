using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Persistence.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </summary>
public sealed class MeetingSessionTranscriptionProjectionTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveCurrentJobSurvivesRestartAndKeepsThePublishedTranscriptFallback()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var session = await AddReadySessionAsync(paths);
            var jobs = new TranscriptionJobRepository(paths);
            await jobs.EnqueueAsync(
                CreateRemoteJob(session, "job-active"),
                CancellationToken.None);
            Assert.NotNull(await jobs.ClaimNextDueAsync(Baseline, CancellationToken.None));
            await jobs.ReplaceChunksAsync(
                "job-active",
                [
                    CreateChunk("chunk-first", 0, 0, 300_000),
                    CreateChunk("chunk-active", 1, 298_000, 600_000, overlapMilliseconds: 2_000)
                ],
                Baseline,
                CancellationToken.None);
            Assert.True(await jobs.TrySetExecutionStateAsync(
                "job-active",
                TranscriptionJobStatus.Processing,
                0,
                "chunk-first",
                Baseline.AddSeconds(1),
                CancellationToken.None));
            Assert.True(await jobs.TryMarkChunkCompletedAsync(
                new TranscriptionChunkCompletion(
                    "job-active",
                    "chunk-first",
                    Path.Combine(paths.TempDirectory, "chunk-first.result.json"),
                    Hash('b'),
                    Baseline.AddSeconds(2),
                    EngineRequestId: "provider-request:restart-1"),
                CancellationToken.None));
            Assert.True(await jobs.TrySetExecutionStateAsync(
                "job-active",
                TranscriptionJobStatus.Processing,
                0.625,
                "chunk-active",
                Baseline.AddSeconds(3),
                CancellationToken.None));

            await using var restartedConnection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var recent = Assert.Single(
                new MeetingSessionRepository(restartedConnection).ListRecent(limit: 10, offset: 0));

            Assert.Equal("processing", recent.TranscriptionStatus);
            Assert.Equal(session.OldMarkdownPath, recent.TranscriptMarkdownPath);
            Assert.Equal(session.OldJsonPath, recent.TranscriptJsonPath);

            var current = Assert.IsType<CurrentTranscriptionJobListItem>(recent.CurrentTranscription);
            Assert.Equal("job-active", current.JobId);
            Assert.Equal("remote.openrouter", current.EngineId);
            Assert.Equal(TranscriptionExecutionKind.Remote, current.ExecutionKind);
            Assert.Equal("provider/whisper", current.ModelId);
            Assert.Equal(TranscriptionJobStatus.Processing, current.Status);
            Assert.Equal(0.625, current.Progress);
            Assert.Equal(1, current.CurrentChunkIndex);
            Assert.Equal(2, current.ChunkCount);
            Assert.Equal("provider-request:restart-1", current.ProviderRequestId);
            Assert.Equal(TranscriptionArtifactPublicationState.None, current.ArtifactPublicationState);
            Assert.Null(current.TranscriptMarkdownPath);
            Assert.Null(current.TranscriptJsonPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentJobProjectionDropsARequestIdThatLooksLikeASecret()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var session = await AddReadySessionAsync(paths);
            var jobs = new TranscriptionJobRepository(paths);
            await jobs.EnqueueAsync(
                CreateRemoteJob(session, "job-sensitive-request"),
                CancellationToken.None);
            Assert.NotNull(await jobs.ClaimNextDueAsync(Baseline, CancellationToken.None));
            await jobs.ReplaceChunksAsync(
                "job-sensitive-request",
                [CreateChunk("chunk-sensitive")],
                Baseline,
                CancellationToken.None);
            Assert.True(await jobs.TrySetExecutionStateAsync(
                "job-sensitive-request",
                TranscriptionJobStatus.Processing,
                0.25,
                "chunk-sensitive",
                Baseline.AddSeconds(1),
                CancellationToken.None));
            Assert.True(await jobs.RequireAttentionAsync(
                "job-sensitive-request",
                "invalid_key",
                "synthetic error",
                "chunk-sensitive",
                "gsk_synthetic-secret",
                Baseline.AddSeconds(2),
                CancellationToken.None));

            await using var restartedConnection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var current = Assert.IsType<CurrentTranscriptionJobListItem>(Assert.Single(
                new MeetingSessionRepository(restartedConnection).ListRecent(10, 0)).CurrentTranscription);

            Assert.Equal(1, current.ChunkCount);
            Assert.Null(current.ProviderRequestId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDiagnosticsSurviveRestartFromDurableJobProjection()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var session = await AddReadySessionAsync(paths);
            var jobs = new TranscriptionJobRepository(paths);
            var localIdentity = new LocalTranscriptionExecutionIdentity(
                ModelCatalogVersion: 1,
                ModelCatalogRevision: "revision",
                ModelFormat: "whisper.cpp.ggml-f16.v1",
                ModelPath: Path.Combine(root, "models", "small.bin"),
                ModelSizeBytes: 1024,
                ModelSha256: Hash('e'),
                RuntimeVersion: "1.9.1",
                RuntimeCommit: "runtime-commit",
                RuntimeSourceArchiveSha256: Hash('f'),
                NativeBundleManifestSha256: Hash('1'),
                BridgeAbiVersion: 1,
                WorkerProtocolVersion: 1,
                RequestedBackend: LocalTranscriptionBackend.Metal,
                ResolvedBackend: null,
                ThreadCount: 6,
                InferenceParametersJson: "{\"language\":\"auto\"}",
                ChunkProfileVersion: 1,
                RunIdentitySha256: Hash('2'));
            await jobs.EnqueueAsync(
                new TranscriptionJobEnqueueRequest(
                    JobId: "job-local-diagnostics",
                    SessionId: session.Id,
                    EngineId: "local.whisper",
                    ExecutionKind: TranscriptionExecutionKind.Local,
                    ModelId: "small",
                    InputAudioPath: session.PrimaryAudioPath,
                    InputSha256: Hash('a'),
                    InputSizeBytes: 4,
                    QueuedAtUtc: Baseline,
                    InputDurationSeconds: 600,
                    RequestedLanguage: "auto",
                    LocalExecution: localIdentity),
                CancellationToken.None);

            await using var restartedConnection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var current = Assert.IsType<CurrentTranscriptionJobListItem>(Assert.Single(
                new MeetingSessionRepository(restartedConnection).ListRecent(10, 0)).CurrentTranscription);

            var diagnostics = Assert.IsType<CurrentLocalTranscriptionDiagnosticsListItem>(
                current.LocalDiagnostics);
            Assert.Equal("metal", diagnostics.RequestedBackend);
            Assert.Null(diagnostics.ResolvedBackend);
            Assert.Equal(6, diagnostics.ThreadCount);
            Assert.Equal("1.9.1", diagnostics.RuntimeVersion);
            Assert.Equal(localIdentity.NativeBundleManifestSha256, diagnostics.NativeBundleManifestSha256);
            Assert.Equal(localIdentity.ModelSha256, diagnostics.ModelSha256);
            Assert.Null(diagnostics.ProcessingDurationMilliseconds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedPublicationAtomicallyReplacesTheLegacyProjection()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var session = await AddReadySessionAsync(paths);
            var jobs = new TranscriptionJobRepository(paths);
            await jobs.EnqueueAsync(
                CreateRemoteJob(session, "job-published") with { ReplaceExisting = true },
                CancellationToken.None);
            Assert.NotNull(await jobs.ClaimNextDueAsync(Baseline, CancellationToken.None));
            await jobs.ReplaceChunksAsync(
                "job-published",
                [CreateChunk("chunk-published")],
                Baseline,
                CancellationToken.None);
            Assert.True(await jobs.TrySetExecutionStateAsync(
                "job-published",
                TranscriptionJobStatus.Processing,
                0.5,
                "chunk-published",
                Baseline.AddSeconds(1),
                CancellationToken.None));
            Assert.True(await jobs.TryMarkChunkCompletedAsync(
                new TranscriptionChunkCompletion(
                    "job-published",
                    "chunk-published",
                    Path.Combine(paths.TempDirectory, "chunk.result.json"),
                    Hash('b'),
                    Baseline.AddSeconds(2)),
                CancellationToken.None));

            var stage = new TranscriptionArtifactStage(
                "job-published",
                Path.Combine(paths.TempDirectory, "transcript.md.staged"),
                Path.Combine(paths.TempDirectory, "transcript.json.staged"),
                Path.Combine(paths.RootDirectory, "session.transcript.md"),
                Path.Combine(paths.RootDirectory, "session.transcript.json"),
                Hash('c'),
                Hash('d'),
                Baseline.AddSeconds(3));
            Assert.True(await jobs.StageCompletionAsync(stage, CancellationToken.None));

            await using (var beforePublishConnection = await new SqliteDatabaseInitializer(paths)
                             .InitializeAsync(CancellationToken.None))
            {
                var beforePublish = Assert.Single(
                    new MeetingSessionRepository(beforePublishConnection).ListRecent(limit: 10, offset: 0));
                Assert.Equal("finalizing", beforePublish.TranscriptionStatus);
                Assert.Equal(session.OldMarkdownPath, beforePublish.TranscriptMarkdownPath);
                Assert.Equal(session.OldJsonPath, beforePublish.TranscriptJsonPath);
                Assert.Equal(
                    TranscriptionArtifactPublicationState.Staged,
                    beforePublish.CurrentTranscription?.ArtifactPublicationState);
                Assert.Null(beforePublish.CurrentTranscription?.TranscriptMarkdownPath);
                Assert.Null(beforePublish.CurrentTranscription?.TranscriptJsonPath);
            }

            Assert.True(await jobs.PublishCompletionAsync(
                new TranscriptionArtifactPublication(
                    "job-published",
                    stage.FinalMarkdownPath,
                    stage.FinalJsonPath,
                    Baseline.AddSeconds(4),
                    DetectedLanguage: "en"),
                CancellationToken.None));

            await using var restartedConnection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var completed = Assert.Single(
                new MeetingSessionRepository(restartedConnection).ListRecent(limit: 10, offset: 0));
            Assert.Equal("completed", completed.TranscriptionStatus);
            Assert.Equal(stage.FinalMarkdownPath, completed.TranscriptMarkdownPath);
            Assert.Equal(stage.FinalJsonPath, completed.TranscriptJsonPath);

            var current = Assert.IsType<CurrentTranscriptionJobListItem>(completed.CurrentTranscription);
            Assert.Equal(TranscriptionJobStatus.Completed, current.Status);
            Assert.Equal(1, current.Progress);
            Assert.Equal(TranscriptionArtifactPublicationState.Promoted, current.ArtifactPublicationState);
            Assert.Equal(stage.FinalMarkdownPath, current.TranscriptMarkdownPath);
            Assert.Equal(stage.FinalJsonPath, current.TranscriptJsonPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-session-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async ValueTask<TestSession> AddReadySessionAsync(LocalAppPaths paths)
    {
        var id = Guid.NewGuid();
        var primaryAudioPath = Path.Combine(paths.RootDirectory, "recordings", $"{id:N}.mp3");
        var oldMarkdownPath = Path.Combine(paths.RootDirectory, "transcripts", "previous.md");
        var oldJsonPath = Path.Combine(paths.RootDirectory, "transcripts", "previous.json");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryAudioPath)!);
        await File.WriteAllBytesAsync(primaryAudioPath, [1, 2, 3, 4]);

        await using var connection = await new SqliteDatabaseInitializer(paths)
            .InitializeAsync(CancellationToken.None);
        var session = MeetingSessionRecord.Create(
            id,
            Baseline,
            "manual",
            "mixed",
            "output-device",
            "microphone-device") with
        {
            Status = "ready",
            EndedAtUtc = Baseline.AddMinutes(10),
            PrimaryAudioPath = primaryAudioPath,
            DurationSeconds = 600,
            TranscriptMarkdownPath = oldMarkdownPath,
            TranscriptJsonPath = oldJsonPath,
            TranscriptionStatus = "completed",
            TranscriptionModel = "legacy-model",
            Language = "ru"
        };
        await new MeetingSessionRepository(connection)
            .UpsertAsync(session, CancellationToken.None);
        return new TestSession(session.Id, primaryAudioPath, oldMarkdownPath, oldJsonPath);
    }

    private static TranscriptionJobEnqueueRequest CreateRemoteJob(TestSession session, string jobId) =>
        new(
            JobId: jobId,
            SessionId: session.Id,
            EngineId: "remote.openrouter",
            ExecutionKind: TranscriptionExecutionKind.Remote,
            ModelId: "provider/whisper",
            InputAudioPath: session.PrimaryAudioPath,
            InputSha256: Hash('a'),
            InputSizeBytes: 4,
            QueuedAtUtc: Baseline,
            InputDurationSeconds: 600,
            RequestedLanguage: "auto",
            RemoteConsentRevision: "remote-disclosure-v1",
            RemoteConsentAtUtc: Baseline.AddDays(-1),
            PrivacyPolicyJson: "{\"zdr\":true}");

    private static TranscriptionChunkDefinition CreateChunk(
        string id,
        int sequenceIndex = 0,
        long startMilliseconds = 0,
        long endMilliseconds = 600_000,
        long overlapMilliseconds = 0) =>
        new(
            Id: id,
            SequenceIndex: sequenceIndex,
            StartMilliseconds: startMilliseconds,
            EndMilliseconds: endMilliseconds,
            OverlapMilliseconds: overlapMilliseconds,
            ArtifactPath: $"{id}.mp3",
            ArtifactFormat: "mp3",
            ArtifactSha256: Hash('e'),
            ArtifactSizeBytes: 4);

    private static string Hash(char character) => new(character, 64);

    private sealed record TestSession(
        string Id,
        string PrimaryAudioPath,
        string OldMarkdownPath,
        string OldJsonPath);
}
