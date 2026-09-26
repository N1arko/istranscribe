using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Persistence.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
public sealed class TranscriptionJobRepositoryTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
    [Fact]
    public async Task SpeakerSourceReleaseWaitsForTerminalJobAndRetriesFailedFileCleanup()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        await database.RetainSpeakerSourcesAsync(session.Id);
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(CreateRemoteRequest(session, "speaker-cleanup"), CancellationToken.None);
        var calls = 0;
        Assert.Null(await repository.ReleaseSpeakerSourcesAsync("speaker-cleanup", (_, _, _) => { calls++; return true; }, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.True(await repository.CancelAsync("speaker-cleanup", Baseline.AddMinutes(1), CancellationToken.None));
        Assert.Null(await repository.ReleaseSpeakerSourcesAsync("speaker-cleanup", (_, _, _) => false, CancellationToken.None));
        Assert.True(await database.HasRetainedSpeakerSourcesAsync(session.Id));
        await Assert.ThrowsAsync<IOException>(() => repository.ReleaseSpeakerSourcesAsync("speaker-cleanup",
            (_, _, _) => throw new IOException("simulated crash before commit"), CancellationToken.None).AsTask());
        Assert.True(await database.HasRetainedSpeakerSourcesAsync(session.Id));
        Assert.NotNull(await repository.ReleaseSpeakerSourcesAsync("speaker-cleanup", (id, root, primary) =>
        {
            Assert.Equal(session.Id, id); Assert.Equal(session.PrimaryAudioPath, primary);
            Assert.False(string.IsNullOrWhiteSpace(root)); calls++; return true;
        }, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.False(await database.HasRetainedSpeakerSourcesAsync(session.Id));
        Assert.True(File.Exists(session.PrimaryAudioPath));
        Assert.Null(await repository.ReleaseSpeakerSourcesAsync("speaker-cleanup", (_, _, _) => throw new Exception("must be idempotent"), CancellationToken.None));
    }

    [Fact]
    public async Task OldTerminalJobCannotReleaseSourcesOwnedByNewJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        await database.RetainSpeakerSourcesAsync(session.Id);
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(CreateRemoteRequest(session, "speaker-old"), CancellationToken.None);
        await repository.CancelAsync("speaker-old", Baseline.AddMinutes(1), CancellationToken.None);
        await repository.EnqueueAsync(CreateRemoteRequest(session, "speaker-new"), CancellationToken.None);
        Assert.Null(await repository.ReleaseSpeakerSourcesAsync("speaker-old",
            (_, _, _) => throw new Exception("active job still owns sources"), CancellationToken.None));
        Assert.True(await database.HasRetainedSpeakerSourcesAsync(session.Id));
    }

    [Fact]
    public async Task EnqueueFreezesExecutionIdentityAndDeduplicatesAnActiveSessionJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var firstRequest = CreateRemoteRequest(
            session,
            jobId: "job-first",
            engineId: "remote.groq",
            modelId: "whisper-large-v3-turbo",
            inputSha256: Hash('a'),
            queuedAtUtc: Baseline);

        var first = await repository.EnqueueAsync(firstRequest, CancellationToken.None);
        Assert.True(first.Created);
        Assert.Equal("remote.groq", first.Job.EngineId);
        Assert.Equal("whisper-large-v3-turbo", first.Job.ModelId);
        Assert.Equal(Hash('a'), first.Job.InputSha256);
        Assert.Equal(TranscriptionExecutionKind.Remote, first.Job.ExecutionKind);
        Assert.Equal(TranscriptionJobStatus.Queued, first.Job.Status);

        var duplicate = await repository.EnqueueAsync(
            CreateRemoteRequest(
                session,
                jobId: "job-second",
                engineId: "remote.openrouter",
                modelId: "another/model",
                inputSha256: Hash('b'),
                queuedAtUtc: Baseline.AddMinutes(1)),
            CancellationToken.None);

        Assert.False(duplicate.Created);
        Assert.Equal("job-first", duplicate.Job.Id);
        Assert.Equal("remote.groq", duplicate.Job.EngineId);
        Assert.Equal("whisper-large-v3-turbo", duplicate.Job.ModelId);
        Assert.Equal(Hash('a'), duplicate.Job.InputSha256);

        var sessionState = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal("job-first", sessionState.CurrentTranscriptionJobId);
        Assert.Equal(session.OldMarkdownPath, sessionState.TranscriptMarkdownPath);
        Assert.Equal(session.OldJsonPath, sessionState.TranscriptJsonPath);

        var mismatchedInput = CreateRemoteRequest(
            await database.AddReadySessionAsync(),
            jobId: "job-mismatched-input",
            engineId: "remote.groq",
            modelId: "whisper-large-v3-turbo",
            inputSha256: Hash('c'),
            queuedAtUtc: Baseline.AddMinutes(2)) with
        {
            InputAudioPath = Path.Combine(database.Paths.RootDirectory, "different.mp3")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.EnqueueAsync(mismatchedInput, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EnqueueRejectsEngineExecutionAndRemoteConsentMismatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var invalidRequests = new[]
        {
            CreateRemoteRequest(session, "remote-as-local") with
            {
                ExecutionKind = TranscriptionExecutionKind.Local,
                RemoteConsentRevision = null,
                RemoteConsentAtUtc = null
            },
            CreateLocalRequest(session, "local-as-remote", Baseline) with
            {
                ExecutionKind = TranscriptionExecutionKind.Remote,
                RemoteConsentRevision = "remote-disclosure-v1",
                RemoteConsentAtUtc = Baseline
            },
            CreateLocalRequest(session, "unknown-engine", Baseline) with
            {
                EngineId = "custom.engine"
            },
            CreateLocalRequest(session, "local-without-frozen-runtime", Baseline) with
            {
                LocalExecution = null
            },
            CreateRemoteRequest(session, "remote-without-consent") with
            {
                RemoteConsentRevision = null,
                RemoteConsentAtUtc = null
            }
        };

        foreach (var request in invalidRequests)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => repository.EnqueueAsync(request, CancellationToken.None).AsTask());
            Assert.Null(await repository.GetAsync(request.JobId, CancellationToken.None));
        }

        Assert.Null((await database.ReadSessionStateAsync(session.Id)).CurrentTranscriptionJobId);
    }

    [Fact]
    public async Task SchemaRejectsAnOrphanCurrentChunkPointer()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(
            CreateRemoteRequest(session, "job-orphan-pointer"),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<SqliteException>(
            () => database.SetJobCurrentChunkAsync(
                    "job-orphan-pointer",
                    "missing-chunk")
                .AsTask());
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Null((await repository.GetAsync(
            "job-orphan-pointer",
            CancellationToken.None))!.CurrentChunkId);
    }

    [Fact]
    public async Task ConcurrentClaimIsGlobalSingleWorkerFifoAndSkipsFutureRetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstSession = await database.AddReadySessionAsync();
        var secondSession = await database.AddReadySessionAsync();
        var thirdSession = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(
            CreateRemoteRequest(firstSession, "job-1", queuedAtUtc: Baseline),
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateRemoteRequest(secondSession, "job-2", queuedAtUtc: Baseline.AddSeconds(1)),
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateRemoteRequest(thirdSession, "job-3", queuedAtUtc: Baseline.AddSeconds(2)),
            CancellationToken.None);

        var concurrentClaims = await Task.WhenAll(
            new TranscriptionJobRepository(database.Paths)
                .ClaimNextDueAsync(Baseline.AddMinutes(1), CancellationToken.None)
                .AsTask(),
            new TranscriptionJobRepository(database.Paths)
                .ClaimNextDueAsync(Baseline.AddMinutes(1), CancellationToken.None)
                .AsTask());

        var firstClaim = Assert.Single(concurrentClaims, static job => job is not null);
        Assert.Equal("job-1", firstClaim!.Id);
        Assert.Equal(TranscriptionJobStatus.Preparing, firstClaim.Status);
        Assert.Equal(1, firstClaim.AttemptCount);

        Assert.True(await repository.ScheduleRetryAsync(
            firstClaim.Id,
            Baseline.AddMinutes(20),
            "provider_rate_limited",
            "Retry later.",
            currentChunkId: null,
            engineRequestId: null,
            Baseline.AddMinutes(1),
            CancellationToken.None));

        var secondClaim = await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(2),
            CancellationToken.None);
        Assert.NotNull(secondClaim);
        Assert.Equal("job-2", secondClaim.Id);
        Assert.True(await repository.CancelAsync(
            secondClaim.Id,
            Baseline.AddMinutes(3),
            CancellationToken.None));

        var thirdClaim = await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(4),
            CancellationToken.None);
        Assert.NotNull(thirdClaim);
        Assert.Equal("job-3", thirdClaim.Id);
        Assert.True(await repository.CancelAsync(
            thirdClaim.Id,
            Baseline.AddMinutes(5),
            CancellationToken.None));

        Assert.Null(await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(19),
            CancellationToken.None));
        var retried = await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(20),
            CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal("job-1", retried.Id);
        Assert.Equal(2, retried.AttemptCount);
    }

    [Fact]
    public async Task ChunkManifestAndCheckpointResumeWithoutMaterializedLocalChunkArtifacts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var enqueued = await repository.EnqueueAsync(
            CreateLocalRequest(session, "job-resume", Baseline) with
            {
                TriggerKind = TranscriptionTriggerKind.Automatic
            },
            CancellationToken.None);
        Assert.True(enqueued.Created);
        Assert.Equal(TranscriptionTriggerKind.Automatic, enqueued.Job.TriggerKind);
        var localExecution = await repository.GetLocalExecutionAsync(
            enqueued.Job.Id,
            CancellationToken.None);
        Assert.NotNull(localExecution);
        Assert.Equal("small", enqueued.Job.ModelId);
        Assert.Equal("whisper.cpp.ggml-f16.v1", localExecution.Execution.ModelFormat);
        Assert.Equal(LocalTranscriptionBackend.Auto, localExecution.Execution.RequestedBackend);
        Assert.Equal(Hash('d'), localExecution.Execution.RunIdentitySha256);
        Assert.True(await repository.IsLocalModelRetainedAsync(
            localExecution.Execution.ModelSha256,
            CancellationToken.None));

        var claimed = await repository.ClaimNextDueAsync(Baseline, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(TranscriptionTriggerKind.Automatic, claimed.TriggerKind);

        var chunks = new[]
        {
            new TranscriptionChunkDefinition(
                Id: "chunk-0",
                SequenceIndex: 0,
                StartMilliseconds: 0,
                EndMilliseconds: 600_000,
                OverlapMilliseconds: 0,
                ArtifactPath: Path.Combine(database.Paths.TempDirectory, "chunk-0.mp3"),
                ArtifactFormat: "mp3",
                ArtifactSha256: Hash('d'),
                ArtifactSizeBytes: 1_000_000),
            new TranscriptionChunkDefinition(
                Id: "chunk-1",
                SequenceIndex: 1,
                StartMilliseconds: 598_000,
                EndMilliseconds: 1_000_000,
                OverlapMilliseconds: 2_000)
        };
        await repository.ReplaceChunksAsync(
            "job-resume",
            chunks,
            Baseline.AddSeconds(1),
            CancellationToken.None);

        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-resume",
            TranscriptionJobStatus.Uploading,
            0.2,
            "chunk-0",
            Baseline.AddSeconds(2),
            CancellationToken.None));
        var completion = new TranscriptionChunkCompletion(
            JobId: "job-resume",
            ChunkId: "chunk-0",
            ResultPath: Path.Combine(database.Paths.TempDirectory, "chunk-0.result.json"),
            ResultSha256: Hash('e'),
            CompletedAtUtc: Baseline.AddSeconds(3),
            EngineRequestId: "request-1",
            ResultMetadataJson: "{\"duration_ms\":600000}",
            UsageJson: "{\"seconds\":600}");
        Assert.True(await repository.TryMarkChunkCompletedAsync(completion, CancellationToken.None));
        Assert.False(await repository.TryMarkChunkCompletedAsync(completion, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.TryMarkChunkCompletedAsync(
                    completion with { ResultSha256 = Hash('f') },
                    CancellationToken.None)
                .AsTask());

        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-resume",
            TranscriptionJobStatus.Processing,
            0.5,
            "chunk-1",
            Baseline.AddSeconds(4),
            CancellationToken.None));
        var recovered = await repository.RecoverInterruptedAsync(
            Baseline.AddSeconds(5),
            CancellationToken.None);
        Assert.Equal(1, recovered.RequeuedJobCount);
        Assert.Equal(1, recovered.ResetChunkCount);

        var recoveredJob = await repository.GetAsync("job-resume", CancellationToken.None);
        Assert.NotNull(recoveredJob);
        Assert.Equal(TranscriptionJobStatus.Queued, recoveredJob.Status);
        var recoveredChunks = await repository.ListChunksAsync("job-resume", CancellationToken.None);
        Assert.Collection(
            recoveredChunks,
            first =>
            {
                Assert.Equal(TranscriptionChunkStatus.Completed, first.Status);
                Assert.Equal("request-1", first.EngineRequestId);
                Assert.Equal(Hash('e'), first.ResultSha256);
            },
            second =>
            {
                Assert.Equal(TranscriptionChunkStatus.Pending, second.Status);
                Assert.Equal(1, second.AttemptCount);
                Assert.Null(second.ArtifactPath);
                Assert.Null(second.ArtifactSha256);
                Assert.Null(second.ArtifactSizeBytes);
                Assert.Null(second.ResultPath);
            });

        await repository.ReplaceChunksAsync(
            "job-resume",
            chunks,
            Baseline.AddSeconds(6),
            CancellationToken.None);
        var resumed = await repository.ListChunksAsync("job-resume", CancellationToken.None);
        Assert.Equal(TranscriptionChunkStatus.Completed, resumed[0].Status);
        Assert.Equal(Hash('e'), resumed[0].ResultSha256);

        var changedCompletedChunk = chunks.ToArray();
        changedCompletedChunk[0] = changedCompletedChunk[0] with { EndMilliseconds = 599_000 };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceChunksAsync(
                    "job-resume",
                    changedCompletedChunk,
                    Baseline.AddSeconds(7),
                    CancellationToken.None)
                .AsTask());

        Assert.True(await repository.CancelAsync(
            "job-resume",
            Baseline.AddSeconds(8),
            CancellationToken.None));
        Assert.False(await repository.IsLocalModelRetainedAsync(
            localExecution.Execution.ModelSha256,
            CancellationToken.None));
    }

    [Fact]
    public async Task LocalAttemptLifecycleIsIdempotentAndAggregatesBoundedTechnicalTelemetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var request = CreateLocalRequest(session, "job-local-attempt", Baseline);
        request = request with
        {
            LocalExecution = request.LocalExecution! with
            {
                BackendHistoryJson = "[\"metal\"]"
            }
        };
        await PrepareLocalChunkAsync(
            repository,
            request,
            chunkId: "chunk-local-attempt");

        var start = new LocalTranscriptionChunkAttemptStart(
            JobId: request.JobId,
            ChunkId: "chunk-local-attempt",
            AttemptIndex: 0,
            RequestedBackend: LocalTranscriptionBackend.Auto,
            WorkerStartedAtUtc: Baseline.AddSeconds(2));
        var started = await repository.StartLocalChunkAttemptAsync(start, CancellationToken.None);
        Assert.True(started.Created);
        Assert.Equal(LocalTranscriptionAttemptStatus.Running, started.Attempt.Status);
        Assert.Null(started.Attempt.WorkerEndedAtUtc);

        var duplicateStart = await repository.StartLocalChunkAttemptAsync(
            start,
            CancellationToken.None);
        Assert.False(duplicateStart.Created);
        Assert.Equal(started.Attempt, duplicateStart.Attempt);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StartLocalChunkAttemptAsync(
                    start with { RequestedBackend = LocalTranscriptionBackend.Cpu },
                    CancellationToken.None)
                .AsTask());

        var chunksAfterStart = await repository.ListChunksAsync(
            request.JobId,
            CancellationToken.None);
        Assert.Equal(1, Assert.Single(chunksAfterStart).AttemptCount);

        var crash = new LocalTranscriptionChunkAttemptCompletion(
            JobId: request.JobId,
            ChunkId: start.ChunkId,
            AttemptIndex: start.AttemptIndex,
            WorkerStartedAtUtc: start.WorkerStartedAtUtc,
            WorkerEndedAtUtc: Baseline.AddSeconds(3),
            Status: LocalTranscriptionAttemptStatus.Crashed,
            ResolvedBackend: LocalTranscriptionBackend.Metal,
            DecodeDurationMilliseconds: 100,
            InferenceDurationMilliseconds: 200,
            PeakWorkingSetBytes: 1_000,
            StableFailureCategory: "worker_crashed");
        Assert.True(await repository.TryCompleteLocalChunkAttemptAsync(
            crash,
            CancellationToken.None));
        Assert.False(await repository.TryCompleteLocalChunkAttemptAsync(
            crash,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.TryCompleteLocalChunkAttemptAsync(
                    crash with { PeakWorkingSetBytes = 1_001 },
                    CancellationToken.None)
                .AsTask());

        var retryStart = start with
        {
            AttemptIndex = 1,
            RequestedBackend = LocalTranscriptionBackend.Cpu,
            WorkerStartedAtUtc = Baseline.AddSeconds(4)
        };
        Assert.True((await repository.StartLocalChunkAttemptAsync(
            retryStart,
            CancellationToken.None)).Created);
        var completed = new LocalTranscriptionChunkAttemptCompletion(
            JobId: request.JobId,
            ChunkId: retryStart.ChunkId,
            AttemptIndex: retryStart.AttemptIndex,
            WorkerStartedAtUtc: retryStart.WorkerStartedAtUtc,
            WorkerEndedAtUtc: Baseline.AddSeconds(5),
            Status: LocalTranscriptionAttemptStatus.Completed,
            ResolvedBackend: LocalTranscriptionBackend.Cpu,
            DecodeDurationMilliseconds: 50,
            InferenceDurationMilliseconds: 150,
            PeakWorkingSetBytes: 800);
        Assert.True(await repository.TryCompleteLocalChunkAttemptAsync(
            completed,
            CancellationToken.None));

        var attempts = await new TranscriptionJobRepository(database.Paths)
            .ListLocalChunkAttemptsAsync(request.JobId, CancellationToken.None);
        Assert.Collection(
            attempts,
            first =>
            {
                Assert.Equal(LocalTranscriptionAttemptStatus.Crashed, first.Status);
                Assert.Equal(LocalTranscriptionBackend.Metal, first.ResolvedBackend);
                Assert.Equal("worker_crashed", first.StableFailureCategory);
            },
            second =>
            {
                Assert.Equal(LocalTranscriptionAttemptStatus.Completed, second.Status);
                Assert.Equal(LocalTranscriptionBackend.Cpu, second.ResolvedBackend);
                Assert.Null(second.StableFailureCategory);
            });

        var aggregate = await repository.GetLocalExecutionAsync(
            request.JobId,
            CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(LocalTranscriptionBackend.Cpu, aggregate.Execution.ResolvedBackend);
        Assert.Equal("[\"metal\",\"metal\",\"cpu\"]", aggregate.Execution.BackendHistoryJson);
        Assert.Equal(1, aggregate.NativeCrashCount);
        Assert.Equal(LocalTranscriptionBackend.Metal, aggregate.LastCrashBackend);
        Assert.Equal(500, aggregate.ProcessingDurationMilliseconds);
        Assert.Equal(1_000, aggregate.PeakWorkingSetBytes);
        Assert.Equal(2, Assert.Single(await repository.ListChunksAsync(
            request.JobId,
            CancellationToken.None)).AttemptCount);
    }

    [Fact]
    public async Task LocalAttemptCasRejectsOutOfSequenceOpenAndLateWorkerResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var request = CreateLocalRequest(session, "job-local-cas", Baseline);
        await PrepareLocalChunkAsync(repository, request, chunkId: "chunk-local-cas");

        var firstStart = new LocalTranscriptionChunkAttemptStart(
            request.JobId,
            "chunk-local-cas",
            AttemptIndex: 0,
            LocalTranscriptionBackend.Auto,
            Baseline.AddSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StartLocalChunkAttemptAsync(
                    firstStart with { AttemptIndex = 1 },
                    CancellationToken.None)
                .AsTask());
        Assert.True((await repository.StartLocalChunkAttemptAsync(
            firstStart,
            CancellationToken.None)).Created);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StartLocalChunkAttemptAsync(
                    firstStart with
                    {
                        AttemptIndex = 1,
                        WorkerStartedAtUtc = Baseline.AddSeconds(3)
                    },
                    CancellationToken.None)
                .AsTask());

        var preempted = new LocalTranscriptionChunkAttemptCompletion(
            firstStart.JobId,
            firstStart.ChunkId,
            firstStart.AttemptIndex,
            firstStart.WorkerStartedAtUtc,
            Baseline.AddSeconds(3),
            LocalTranscriptionAttemptStatus.Preempted);
        Assert.False(await repository.TryCompleteLocalChunkAttemptAsync(
            preempted with { WorkerStartedAtUtc = Baseline.AddSeconds(2.5) },
            CancellationToken.None));
        Assert.Equal(
            LocalTranscriptionAttemptStatus.Running,
            Assert.Single(await repository.ListLocalChunkAttemptsAsync(
                request.JobId,
                CancellationToken.None)).Status);
        Assert.True(await repository.TryCompleteLocalChunkAttemptAsync(
            preempted,
            CancellationToken.None));

        var secondStart = firstStart with
        {
            AttemptIndex = 1,
            RequestedBackend = LocalTranscriptionBackend.Cpu,
            WorkerStartedAtUtc = Baseline.AddSeconds(4)
        };
        Assert.True((await repository.StartLocalChunkAttemptAsync(
            secondStart,
            CancellationToken.None)).Created);
        Assert.False(await repository.TryCompleteLocalChunkAttemptAsync(
            preempted,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.TryCompleteLocalChunkAttemptAsync(
                    preempted with { Status = LocalTranscriptionAttemptStatus.Cancelled },
                    CancellationToken.None)
                .AsTask());
        Assert.False(await repository.TryCompleteLocalChunkAttemptAsync(
            preempted with
            {
                AttemptIndex = 2,
                WorkerStartedAtUtc = Baseline.AddSeconds(5),
                WorkerEndedAtUtc = Baseline.AddSeconds(6)
            },
            CancellationToken.None));

        var attempts = await repository.ListLocalChunkAttemptsAsync(
            request.JobId,
            CancellationToken.None);
        Assert.Collection(
            attempts,
            first => Assert.Equal(LocalTranscriptionAttemptStatus.Preempted, first.Status),
            second => Assert.Equal(LocalTranscriptionAttemptStatus.Running, second.Status));
    }

    [Theory]
    [InlineData(LocalTranscriptionAttemptStatus.Completed)]
    [InlineData(LocalTranscriptionAttemptStatus.Cancelled)]
    [InlineData(LocalTranscriptionAttemptStatus.Preempted)]
    [InlineData(LocalTranscriptionAttemptStatus.Crashed)]
    [InlineData(LocalTranscriptionAttemptStatus.OutOfMemory)]
    [InlineData(LocalTranscriptionAttemptStatus.Timeout)]
    [InlineData(LocalTranscriptionAttemptStatus.DecodeFailure)]
    [InlineData(LocalTranscriptionAttemptStatus.NativeFailure)]
    [InlineData(LocalTranscriptionAttemptStatus.ProtocolFailure)]
    public async Task LocalAttemptPersistsEveryTerminalExitCategory(
        LocalTranscriptionAttemptStatus status)
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var request = CreateLocalRequest(session, $"job-exit-{status}", Baseline);
        await PrepareLocalChunkAsync(repository, request, chunkId: "chunk-exit");
        var start = new LocalTranscriptionChunkAttemptStart(
            request.JobId,
            "chunk-exit",
            AttemptIndex: 0,
            LocalTranscriptionBackend.Auto,
            Baseline.AddSeconds(2));
        await repository.StartLocalChunkAttemptAsync(start, CancellationToken.None);
        var failure = status is
            LocalTranscriptionAttemptStatus.Crashed
            or LocalTranscriptionAttemptStatus.OutOfMemory
            or LocalTranscriptionAttemptStatus.Timeout
            or LocalTranscriptionAttemptStatus.DecodeFailure
            or LocalTranscriptionAttemptStatus.NativeFailure
            or LocalTranscriptionAttemptStatus.ProtocolFailure;
        var completion = new LocalTranscriptionChunkAttemptCompletion(
            start.JobId,
            start.ChunkId,
            start.AttemptIndex,
            start.WorkerStartedAtUtc,
            Baseline.AddSeconds(3),
            status,
            ResolvedBackend: status == LocalTranscriptionAttemptStatus.Completed
                ? LocalTranscriptionBackend.Cpu
                : null,
            StableFailureCategory: failure ? "stable_failure" : null);

        Assert.True(await repository.TryCompleteLocalChunkAttemptAsync(
            completion,
            CancellationToken.None));
        Assert.Equal(
            status,
            Assert.Single(await repository.ListLocalChunkAttemptsAsync(
                request.JobId,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task LocalAttemptValidatesBoundedIdentityOutcomeAndTelemetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var request = CreateLocalRequest(session, "job-local-validation", Baseline);
        var oversizedHistory = $"[{string.Join(',', Enumerable.Repeat("\"cpu\"", 33))}]";
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.EnqueueAsync(
                    request with
                    {
                        LocalExecution = request.LocalExecution! with
                        {
                            BackendHistoryJson = oversizedHistory
                        }
                    },
                    CancellationToken.None)
                .AsTask());
        await PrepareLocalChunkAsync(repository, request, chunkId: "chunk-local-validation");
        var start = new LocalTranscriptionChunkAttemptStart(
            request.JobId,
            "chunk-local-validation",
            AttemptIndex: 0,
            LocalTranscriptionBackend.Auto,
            Baseline.AddSeconds(2));

        var invalidStarts = new[]
        {
            start with { AttemptIndex = -1 },
            start with { AttemptIndex = 1_000_001 },
            start with { ChunkId = new string('x', 257) },
            start with { RequestedBackend = (LocalTranscriptionBackend)999 }
        };
        foreach (var invalidStart in invalidStarts)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => repository.StartLocalChunkAttemptAsync(
                        invalidStart,
                        CancellationToken.None)
                    .AsTask());
        }

        await repository.StartLocalChunkAttemptAsync(start, CancellationToken.None);
        var valid = new LocalTranscriptionChunkAttemptCompletion(
            start.JobId,
            start.ChunkId,
            start.AttemptIndex,
            start.WorkerStartedAtUtc,
            Baseline.AddSeconds(3),
            LocalTranscriptionAttemptStatus.Completed,
            ResolvedBackend: LocalTranscriptionBackend.Cpu,
            DecodeDurationMilliseconds: 10,
            InferenceDurationMilliseconds: 20,
            PeakWorkingSetBytes: 30);
        var invalidCompletions = new[]
        {
            valid with { Status = LocalTranscriptionAttemptStatus.Running },
            valid with { ResolvedBackend = null },
            valid with { ResolvedBackend = LocalTranscriptionBackend.Auto },
            valid with { DecodeDurationMilliseconds = -1 },
            valid with { WorkerEndedAtUtc = Baseline.AddSeconds(1) },
            valid with
            {
                Status = LocalTranscriptionAttemptStatus.Crashed,
                StableFailureCategory = null
            },
            valid with { StableFailureCategory = "unexpected" },
            valid with
            {
                Status = LocalTranscriptionAttemptStatus.Crashed,
                StableFailureCategory = new string('x', 129)
            },
            valid with
            {
                Status = LocalTranscriptionAttemptStatus.Crashed,
                StableFailureCategory = "contains transcript"
            }
        };
        foreach (var invalidCompletion in invalidCompletions)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => repository.TryCompleteLocalChunkAttemptAsync(
                        invalidCompletion,
                        CancellationToken.None)
                    .AsTask());
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListLocalChunkAttemptsAsync(
                    request.JobId,
                    CancellationToken.None,
                    maximumCount: 0)
                .AsTask());

        Assert.Equal(
            LocalTranscriptionAttemptStatus.Running,
            Assert.Single(await repository.ListLocalChunkAttemptsAsync(
                request.JobId,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task LocalAttemptRequiresAnExistingLocalJobAndItsExecutableChunk()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        var remoteSession = await database.AddReadySessionAsync();
        var remoteRequest = CreateRemoteRequest(
            remoteSession,
            "job-remote-attempt",
            queuedAtUtc: Baseline);
        await repository.EnqueueAsync(remoteRequest, CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        await repository.ReplaceChunksAsync(
            remoteRequest.JobId,
            [CreateChunk("chunk-remote-attempt", 0, 0, 60_000)],
            Baseline.AddSeconds(1),
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            remoteRequest.JobId,
            TranscriptionJobStatus.Processing,
            0,
            "chunk-remote-attempt",
            Baseline.AddSeconds(1),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StartLocalChunkAttemptAsync(
                    new LocalTranscriptionChunkAttemptStart(
                        remoteRequest.JobId,
                        "chunk-remote-attempt",
                        0,
                        LocalTranscriptionBackend.Cpu,
                        Baseline.AddSeconds(2)),
                    CancellationToken.None)
                .AsTask());
        Assert.Empty(await repository.ListLocalChunkAttemptsAsync(
            remoteRequest.JobId,
            CancellationToken.None));
        Assert.True(await repository.CancelAsync(
            remoteRequest.JobId,
            Baseline.AddSeconds(3),
            CancellationToken.None));

        var localSession = await database.AddReadySessionAsync();
        var localRequest = CreateLocalRequest(
            localSession,
            "job-missing-local-chunk",
            Baseline.AddSeconds(4));
        await PrepareLocalChunkAsync(repository, localRequest, chunkId: "chunk-existing");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StartLocalChunkAttemptAsync(
                    new LocalTranscriptionChunkAttemptStart(
                        localRequest.JobId,
                        "chunk-missing",
                        0,
                        LocalTranscriptionBackend.Cpu,
                        Baseline.AddSeconds(5)),
                    CancellationToken.None)
                .AsTask());
        Assert.Empty(await repository.ListLocalChunkAttemptsAsync(
            localRequest.JobId,
            CancellationToken.None));
    }

    [Fact]
    public async Task DynamicSplitIsAtomicIdempotentAndSurvivesRestart()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(
            CreateRemoteRequest(session, "job-split", queuedAtUtc: Baseline),
            CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));

        var initialChunks = new[]
        {
            CreateChunk("chunk-parent", 0, 0, 600_000),
            CreateChunk("chunk-completed", 1, 598_000, 1_200_000) with
            {
                OverlapMilliseconds = 2_000
            }
        };
        await repository.ReplaceChunksAsync(
            "job-split",
            initialChunks,
            Baseline.AddSeconds(1),
            CancellationToken.None);

        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-split",
            TranscriptionJobStatus.Processing,
            0.25,
            "chunk-completed",
            Baseline.AddSeconds(2),
            CancellationToken.None));
        Assert.True(await repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                "job-split",
                "chunk-completed",
                Path.Combine(database.Paths.TempDirectory, "chunk-completed.result.json"),
                Hash('f'),
                Baseline.AddSeconds(3),
                EngineRequestId: "request-completed"),
            CancellationToken.None));
        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-split",
            TranscriptionJobStatus.Uploading,
            0.5,
            "chunk-parent",
            Baseline.AddSeconds(4),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceChunksAsync(
                    "job-split",
                    initialChunks,
                    Baseline.AddSeconds(4.5),
                    CancellationToken.None)
                .AsTask());
        var activeParent = Assert.Single(
            await repository.ListChunksAsync("job-split", CancellationToken.None),
            static chunk => chunk.Id == "chunk-parent");
        Assert.Equal(TranscriptionChunkStatus.Uploading, activeParent.Status);
        Assert.Equal(
            "chunk-parent",
            (await repository.GetAsync("job-split", CancellationToken.None))!.CurrentChunkId);

        var splitChildren = new[]
        {
            new TranscriptionChunkDefinition(
                Id: "chunk-left",
                SequenceIndex: 0,
                StartMilliseconds: 0,
                EndMilliseconds: 301_000,
                OverlapMilliseconds: 0,
                ParentChunkId: "chunk-parent",
                SplitDepth: 1),
            new TranscriptionChunkDefinition(
                Id: "chunk-right",
                SequenceIndex: 1,
                StartMilliseconds: 299_000,
                EndMilliseconds: 600_000,
                OverlapMilliseconds: 2_000,
                ParentChunkId: "chunk-parent",
                SplitDepth: 1)
        };
        var splitRequest = new TranscriptionChunkSplitRequest(
            "job-split",
            "chunk-parent",
            splitChildren,
            Baseline.AddSeconds(5));

        var persistedChildren = await repository.SplitChunkAsync(
            splitRequest,
            CancellationToken.None);
        Assert.Collection(
            persistedChildren,
            left =>
            {
                Assert.Equal("chunk-left", left.Id);
                Assert.Equal(0, left.SequenceIndex);
                Assert.Equal(TranscriptionChunkStatus.Pending, left.Status);
            },
            right =>
            {
                Assert.Equal("chunk-right", right.Id);
                Assert.Equal(1, right.SequenceIndex);
                Assert.Equal(TranscriptionChunkStatus.Pending, right.Status);
            });

        var afterSplit = await repository.ListChunksAsync("job-split", CancellationToken.None);
        Assert.Collection(
            afterSplit,
            left => Assert.Equal("chunk-left", left.Id),
            right => Assert.Equal("chunk-right", right.Id),
            completed =>
            {
                Assert.Equal("chunk-completed", completed.Id);
                Assert.Equal(TranscriptionChunkStatus.Completed, completed.Status);
                Assert.Equal("request-completed", completed.EngineRequestId);
                Assert.Equal(Hash('f'), completed.ResultSha256);
            },
            parent =>
            {
                Assert.Equal("chunk-parent", parent.Id);
                Assert.Equal(TranscriptionChunkStatus.Split, parent.Status);
            });
        var splitJob = await repository.GetAsync("job-split", CancellationToken.None);
        Assert.NotNull(splitJob);
        Assert.Equal(TranscriptionJobStatus.Preparing, splitJob.Status);
        Assert.Null(splitJob.CurrentChunkId);
        Assert.Equal(1d / 3d, splitJob.Progress, precision: 10);

        var repeated = await repository.SplitChunkAsync(splitRequest, CancellationToken.None);
        Assert.Equal(
            persistedChildren.Select(static chunk => chunk.Id),
            repeated.Select(static chunk => chunk.Id));
        Assert.Equal(4, (await repository.ListChunksAsync(
            "job-split",
            CancellationToken.None)).Count);

        var resumedManifest = new[]
        {
            splitChildren[0],
            splitChildren[1],
            initialChunks[1] with { SequenceIndex = 2 }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceChunksAsync(
                    "job-split",
                    [resumedManifest[0], resumedManifest[2]],
                    Baseline.AddSeconds(5.25),
                    CancellationToken.None)
                .AsTask());
        var changedSplitChild = resumedManifest.ToArray();
        changedSplitChild[1] = changedSplitChild[1] with
        {
            ArtifactPath = "changed-split-child.mp3"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceChunksAsync(
                    "job-split",
                    changedSplitChild,
                    Baseline.AddSeconds(5.5),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal(4, (await repository.ListChunksAsync(
            "job-split",
            CancellationToken.None)).Count);

        var restartedRepository = new TranscriptionJobRepository(database.Paths);
        var recovery = await restartedRepository.RecoverInterruptedAsync(
            Baseline.AddSeconds(6),
            CancellationToken.None);
        Assert.Equal(1, recovery.RequeuedJobCount);
        Assert.Equal(0, recovery.ResetChunkCount);

        await restartedRepository.ReplaceChunksAsync(
            "job-split",
            resumedManifest,
            Baseline.AddSeconds(7),
            CancellationToken.None);

        var afterResume = await restartedRepository.ListChunksAsync(
            "job-split",
            CancellationToken.None);
        Assert.Equal(4, afterResume.Count);
        Assert.Equal(TranscriptionChunkStatus.Split, afterResume[3].Status);
        Assert.Equal("chunk-parent", afterResume[3].Id);
        Assert.Equal(TranscriptionChunkStatus.Completed, afterResume[2].Status);
        Assert.Equal("request-completed", afterResume[2].EngineRequestId);
        Assert.Equal(Hash('f'), afterResume[2].ResultSha256);
    }

    [Fact]
    public async Task RetryAttentionManualRequeueAndCancelAreDurableStateTransitions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(
            CreateRemoteRequest(session, "job-state", queuedAtUtc: Baseline),
            CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        await repository.ReplaceChunksAsync(
            "job-state",
            [
                CreateChunk("chunk-state", 0, 0, 120_000),
                CreateChunk("chunk-other", 1, 120_000, 240_000)
            ],
            Baseline,
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-state",
            TranscriptionJobStatus.Uploading,
            0.25,
            "chunk-state",
            Baseline.AddSeconds(1),
            CancellationToken.None));
        Assert.False(await repository.TrySetExecutionStateAsync(
            "job-state",
            TranscriptionJobStatus.Uploading,
            0.3,
            "chunk-other",
            Baseline.AddSeconds(1.5),
            CancellationToken.None));
        Assert.False(await repository.ScheduleRetryAsync(
            "job-state",
            Baseline.AddMinutes(5),
            "wrong_chunk",
            "Wrong compare token.",
            currentChunkId: "chunk-other",
            engineRequestId: "wrong-request",
            Baseline.AddSeconds(1.75),
            CancellationToken.None));

        Assert.True(await repository.ScheduleRetryAsync(
            "job-state",
            Baseline.AddMinutes(5),
            "provider_busy",
            "Provider is busy.",
            currentChunkId: null,
            engineRequestId: "request-retry",
            Baseline.AddSeconds(2),
            CancellationToken.None));
        var retry = await repository.GetAsync("job-state", CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(TranscriptionJobStatus.RetryScheduled, retry.Status);
        Assert.Equal("provider_busy", retry.StableErrorCode);
        Assert.Equal(Baseline.AddMinutes(5), retry.NextAttemptAtUtc);
        Assert.Null(retry.CurrentChunkId);
        var retryChunks = await repository.ListChunksAsync("job-state", CancellationToken.None);
        Assert.Equal(TranscriptionChunkStatus.Pending, retryChunks[0].Status);
        Assert.Equal("request-retry", retryChunks[0].EngineRequestId);
        Assert.Equal(TranscriptionChunkStatus.Pending, retryChunks[1].Status);
        Assert.Null(await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(4),
            CancellationToken.None));

        Assert.NotNull(await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(5),
            CancellationToken.None));
        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-state",
            TranscriptionJobStatus.Processing,
            0.4,
            "chunk-state",
            Baseline.AddMinutes(5).AddSeconds(1),
            CancellationToken.None));
        Assert.True(await repository.RequireAttentionAsync(
            "job-state",
            "invalid_key",
            "Replace the provider key.",
            currentChunkId: null,
            engineRequestId: "request-attention",
            Baseline.AddMinutes(5).AddSeconds(2),
            CancellationToken.None));

        var attention = await repository.GetAsync("job-state", CancellationToken.None);
        Assert.NotNull(attention);
        Assert.Equal(TranscriptionJobStatus.AttentionRequired, attention.Status);
        Assert.Equal("invalid_key", attention.StableErrorCode);
        Assert.Null(attention.CurrentChunkId);
        Assert.Equal("request-attention", (await repository.ListChunksAsync(
            "job-state",
            CancellationToken.None))[0].EngineRequestId);
        Assert.True(await repository.RequeueAsync(
            "job-state",
            Baseline.AddMinutes(6),
            CancellationToken.None));
        var requeued = await repository.GetAsync("job-state", CancellationToken.None);
        Assert.NotNull(requeued);
        Assert.Equal(TranscriptionJobStatus.Queued, requeued.Status);
        Assert.Equal(0, requeued.AttemptCount);
        Assert.Null(requeued.StableErrorCode);

        Assert.NotNull(await repository.ClaimNextDueAsync(
            Baseline.AddMinutes(6),
            CancellationToken.None));
        Assert.True(await repository.CancelAsync(
            "job-state",
            Baseline.AddMinutes(7),
            CancellationToken.None));
        var cancelled = await repository.GetAsync("job-state", CancellationToken.None);
        Assert.NotNull(cancelled);
        Assert.Equal(TranscriptionJobStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.All(
            await repository.ListChunksAsync("job-state", CancellationToken.None),
            static chunk => Assert.Equal(TranscriptionChunkStatus.Cancelled, chunk.Status));
        Assert.False(await repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                "job-state",
                "chunk-state",
                Path.Combine(database.Paths.TempDirectory, "late.json"),
                Hash('a'),
                Baseline.AddMinutes(8)),
            CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationSupersedeAtomicallyRetiresOldJobAndPreservesPublishedArtifacts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var oldMarkdownPath = Path.Combine(database.Paths.RootDirectory, "published.transcript.md");
        var oldJsonPath = Path.Combine(database.Paths.RootDirectory, "published.transcript.json");
        var session = await database.AddReadySessionAsync(oldMarkdownPath, oldJsonPath);
        var repository = new TranscriptionJobRepository(database.Paths);
        await PrepareConfigurationAttentionAsync(
            repository,
            session,
            "job-config-old",
            "invalid_engine_configuration");
        var replacement = CreateRemoteRequest(
            session,
            "job-config-new",
            modelId: "whisper-large-v3",
            queuedAtUtc: Baseline.AddMinutes(1)) with
        {
            ReplaceExisting = true
        };

        var result = await repository.SupersedeConfigurationAsync(
            "job-config-old",
            replacement,
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("job-config-new", result.Job.Id);
        Assert.Equal(TranscriptionJobStatus.Queued, result.Job.Status);
        Assert.Equal("whisper-large-v3", result.Job.ModelId);
        Assert.Equal("job-config-old", result.Job.SupersedesJobId);
        Assert.Equal(TranscriptionTriggerKind.Manual, result.Job.TriggerKind);

        var superseded = await repository.GetAsync("job-config-old", CancellationToken.None);
        Assert.NotNull(superseded);
        Assert.Equal(TranscriptionJobStatus.Cancelled, superseded.Status);
        Assert.True(superseded.CancellationRequested);
        Assert.Equal("superseded", superseded.StableErrorCode);
        Assert.Equal("whisper-large-v3-turbo", superseded.ModelId);
        Assert.All(
            await repository.ListChunksAsync("job-config-old", CancellationToken.None),
            static chunk =>
            {
                Assert.Equal(TranscriptionChunkStatus.Cancelled, chunk.Status);
                Assert.Equal("superseded", chunk.StableErrorCode);
            });

        var sessionState = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal("job-config-new", sessionState.CurrentTranscriptionJobId);
        Assert.Equal(oldMarkdownPath, sessionState.TranscriptMarkdownPath);
        Assert.Equal(oldJsonPath, sessionState.TranscriptJsonPath);
        Assert.Equal("completed", sessionState.TranscriptionStatus);
    }

    [Fact]
    public async Task ConfigurationSupersedeRollsBackOldJobChunksAndPointerWhenInsertFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var oldMarkdownPath = Path.Combine(database.Paths.RootDirectory, "published.transcript.md");
        var oldJsonPath = Path.Combine(database.Paths.RootDirectory, "published.transcript.json");
        var session = await database.AddReadySessionAsync(oldMarkdownPath, oldJsonPath);
        var repository = new TranscriptionJobRepository(database.Paths);
        await PrepareConfigurationAttentionAsync(
            repository,
            session,
            "job-config-rollback",
            "zdr_route_unavailable");
        var rejectedReplacement = CreateRemoteRequest(
            session,
            "job-config-rejected",
            engineId: "remote.openrouter",
            modelId: "openai/whisper-v3",
            queuedAtUtc: Baseline.AddMinutes(1)) with
        {
            InputAudioPath = session.PrimaryAudioPath + ".changed",
            EngineOptionsJson = "{\"requireZeroDataRetention\":false}",
            PrivacyPolicyJson = "{\"requireZeroDataRetention\":false}",
            ReplaceExisting = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SupersedeConfigurationAsync(
                    "job-config-rollback",
                    rejectedReplacement,
                    CancellationToken.None)
                .AsTask());

        var original = await repository.GetAsync("job-config-rollback", CancellationToken.None);
        Assert.NotNull(original);
        Assert.Equal(TranscriptionJobStatus.AttentionRequired, original.Status);
        Assert.False(original.CancellationRequested);
        Assert.Equal("zdr_route_unavailable", original.StableErrorCode);
        Assert.All(
            await repository.ListChunksAsync("job-config-rollback", CancellationToken.None),
            static chunk => Assert.Equal(TranscriptionChunkStatus.Pending, chunk.Status));
        Assert.Null(await repository.GetAsync("job-config-rejected", CancellationToken.None));
        Assert.Equal(1, await database.CountJobsAsync());

        var sessionState = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal("job-config-rollback", sessionState.CurrentTranscriptionJobId);
        Assert.Equal(oldMarkdownPath, sessionState.TranscriptMarkdownPath);
        Assert.Equal(oldJsonPath, sessionState.TranscriptJsonPath);
    }

    [Fact]
    public async Task ConfigurationSupersedeIsIdempotentForTheSameFrozenReplacement()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await PrepareConfigurationAttentionAsync(
            repository,
            session,
            "job-config-idempotent-old",
            "invalid_engine_configuration");
        var replacement = CreateRemoteRequest(
            session,
            "job-config-idempotent-new",
            modelId: "whisper-large-v3",
            queuedAtUtc: Baseline.AddMinutes(1));

        var first = await repository.SupersedeConfigurationAsync(
            "job-config-idempotent-old",
            replacement,
            CancellationToken.None);
        var repeated = await repository.SupersedeConfigurationAsync(
            "job-config-idempotent-old",
            replacement,
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.Job, repeated.Job);
        Assert.Equal(2, await database.CountJobsAsync());
        Assert.Equal(
            "job-config-idempotent-new",
            (await database.ReadSessionStateAsync(session.Id)).CurrentTranscriptionJobId);
    }

    [Fact]
    public async Task TerminalWorkspaceCleanupClearsOnlyFilePointersAndKeepsBoundedDiagnostics()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync();
        var repository = new TranscriptionJobRepository(database.Paths);
        await repository.EnqueueAsync(
            CreateRemoteRequest(session, "job-workspace-cleanup") with
            {
                ManifestPath = Path.Combine(database.Paths.TempDirectory, "manifest.json")
            },
            CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        await repository.ReplaceChunksAsync(
            "job-workspace-cleanup",
            [
                new TranscriptionChunkDefinition(
                    "chunk-cleanup",
                    0,
                    0,
                    60_000,
                    0,
                    ArtifactPath: Path.Combine(database.Paths.TempDirectory, "chunk.mp3"),
                    ArtifactFormat: "mp3",
                    ArtifactSha256: Hash('a'),
                    ArtifactSizeBytes: 1_024)
            ],
            Baseline,
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-workspace-cleanup",
            TranscriptionJobStatus.Uploading,
            0.5,
            "chunk-cleanup",
            Baseline,
            CancellationToken.None));
        Assert.True(await repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                "job-workspace-cleanup",
                "chunk-cleanup",
                Path.Combine(database.Paths.TempDirectory, "chunk.result.json"),
                Hash('b'),
                Baseline,
                EngineRequestId: "request-cleanup",
                ResultMetadataJson: "{\"version\":1}",
                UsageJson: "{\"audioSeconds\":60}"),
            CancellationToken.None));

        Assert.False(await repository.CompleteTerminalWorkspaceCleanupAsync(
            "job-workspace-cleanup",
            Baseline.AddSeconds(1),
            CancellationToken.None));
        Assert.True(await repository.CancelAsync(
            "job-workspace-cleanup",
            Baseline.AddSeconds(2),
            CancellationToken.None));
        Assert.Contains(
            "job-workspace-cleanup",
            await repository.ListTerminalWorkspaceCleanupJobIdsAsync(CancellationToken.None));

        Assert.True(await repository.CompleteTerminalWorkspaceCleanupAsync(
            "job-workspace-cleanup",
            Baseline.AddSeconds(3),
            CancellationToken.None));

        var cleanedJob = await repository.GetAsync("job-workspace-cleanup", CancellationToken.None);
        Assert.NotNull(cleanedJob);
        Assert.Null(cleanedJob.ManifestPath);
        var cleanedChunk = Assert.Single(await repository.ListChunksAsync(
            "job-workspace-cleanup",
            CancellationToken.None));
        Assert.Null(cleanedChunk.ArtifactPath);
        Assert.Null(cleanedChunk.ArtifactFormat);
        Assert.Null(cleanedChunk.ArtifactSha256);
        Assert.Null(cleanedChunk.ArtifactSizeBytes);
        Assert.Null(cleanedChunk.ResultPath);
        Assert.Null(cleanedChunk.ResultSha256);
        Assert.Equal("request-cleanup", cleanedChunk.EngineRequestId);
        Assert.Equal("{\"version\":1}", cleanedChunk.ResultMetadataJson);
        Assert.Equal("{\"audioSeconds\":60}", cleanedChunk.UsageJson);
        Assert.DoesNotContain(
            "job-workspace-cleanup",
            await repository.ListTerminalWorkspaceCleanupJobIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StagingPreservesCanonicalPathsAndPublishCompletesJobAndSessionAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await database.AddReadySessionAsync(
            oldMarkdownPath: Path.Combine(database.Paths.RootDirectory, "old.transcript.md"),
            oldJsonPath: Path.Combine(database.Paths.RootDirectory, "old.transcript.json"));
        var repository = new TranscriptionJobRepository(database.Paths);
        var publicationChunks = new[]
        {
            new TranscriptionChunkDefinition(
                Id: "chunk-split-parent",
                SequenceIndex: 0,
                StartMilliseconds: 0,
                EndMilliseconds: 120_000,
                OverlapMilliseconds: 0,
                InitialStatus: TranscriptionChunkStatus.Split),
            CreateChunk("chunk-publish", 1, 0, 60_000) with
            {
                ParentChunkId = "chunk-split-parent",
                SplitDepth = 1
            }
        };
        await repository.EnqueueAsync(
            CreateRemoteRequest(
                session,
                "job-publish",
                modelId: "whisper-large-v3",
                queuedAtUtc: Baseline) with
            {
                RequestedLanguage = "en",
                ReplaceExisting = true
            },
            CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        await repository.ReplaceChunksAsync(
            "job-publish",
            publicationChunks,
            Baseline,
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            "job-publish",
            TranscriptionJobStatus.Processing,
            0.5,
            "chunk-publish",
            Baseline.AddMilliseconds(500),
            CancellationToken.None));
        Assert.True(await repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                "job-publish",
                "chunk-publish",
                Path.Combine(database.Paths.TempDirectory, "chunk-publish.result.json"),
                Hash('b'),
                Baseline.AddSeconds(1)),
            CancellationToken.None));

        var stage = new TranscriptionArtifactStage(
            "job-publish",
            Path.Combine(database.Paths.TempDirectory, "transcript.md.staged"),
            Path.Combine(database.Paths.TempDirectory, "transcript.json.staged"),
            Path.Combine(database.Paths.RootDirectory, "session.transcript.md"),
            Path.Combine(database.Paths.RootDirectory, "session.transcript.json"),
            Hash('c'),
            Hash('d'),
            Baseline.AddSeconds(2));
        Assert.True(await repository.StageCompletionAsync(stage, CancellationToken.None));
        Assert.False(await repository.StageCompletionAsync(stage, CancellationToken.None));
        Assert.Equal("job-publish", Assert.Single(
            await repository.ListStagedJobsAsync(CancellationToken.None)).Id);

        var beforePublish = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal("job-publish", beforePublish.CurrentTranscriptionJobId);
        Assert.Equal(session.OldMarkdownPath, beforePublish.TranscriptMarkdownPath);
        Assert.Equal(session.OldJsonPath, beforePublish.TranscriptJsonPath);

        Assert.True(await repository.ScheduleRetryAsync(
            "job-publish",
            Baseline.AddSeconds(2.5),
            "artifact_promotion_failed",
            "Rebuild the staged artifacts.",
            currentChunkId: null,
            engineRequestId: null,
            Baseline.AddSeconds(2.25),
            CancellationToken.None));
        var publicationRetry = await repository.GetAsync("job-publish", CancellationToken.None);
        Assert.NotNull(publicationRetry);
        Assert.Equal(TranscriptionJobStatus.RetryScheduled, publicationRetry.Status);
        Assert.Equal(
            TranscriptionArtifactPublicationState.None,
            publicationRetry.ArtifactPublicationState);
        Assert.Empty(await repository.ListStagedJobsAsync(CancellationToken.None));
        Assert.NotNull(await repository.ClaimNextDueAsync(
            Baseline.AddSeconds(2.5),
            CancellationToken.None));
        Assert.True(await repository.StageCompletionAsync(
            stage with { StagedAtUtc = Baseline.AddSeconds(2.75) },
            CancellationToken.None));

        var publication = new TranscriptionArtifactPublication(
            "job-publish",
            stage.FinalMarkdownPath,
            stage.FinalJsonPath,
            Baseline.AddSeconds(3),
            DetectedLanguage: "en",
            UsageJson: "{\"seconds\":60}");

        await database.SetCurrentJobPointerAsync(session.Id, jobId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.PublishCompletionAsync(publication, CancellationToken.None).AsTask());
        var rejectedPublication = await repository.GetAsync("job-publish", CancellationToken.None);
        Assert.NotNull(rejectedPublication);
        Assert.Equal(TranscriptionJobStatus.Finalizing, rejectedPublication.Status);
        var afterRejectedPublication = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal(session.OldMarkdownPath, afterRejectedPublication.TranscriptMarkdownPath);
        Assert.Equal(session.OldJsonPath, afterRejectedPublication.TranscriptJsonPath);

        await database.SetCurrentJobPointerAsync(session.Id, "job-publish");
        Assert.True(await repository.PublishCompletionAsync(publication, CancellationToken.None));
        Assert.False(await repository.PublishCompletionAsync(publication, CancellationToken.None));

        var completed = await repository.GetAsync("job-publish", CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(TranscriptionJobStatus.Completed, completed.Status);
        Assert.Equal(TranscriptionArtifactPublicationState.Promoted, completed.ArtifactPublicationState);
        Assert.Equal(publication.FinalMarkdownPath, completed.TranscriptMarkdownPath);
        Assert.Equal(publication.FinalJsonPath, completed.TranscriptJsonPath);
        Assert.Equal("en", completed.DetectedLanguage);
        Assert.Equal("{\"seconds\":60}", completed.UsageJson);
        Assert.Equal(1d, completed.Progress);

        var afterPublish = await database.ReadSessionStateAsync(session.Id);
        Assert.Equal("job-publish", afterPublish.CurrentTranscriptionJobId);
        Assert.Equal(publication.FinalMarkdownPath, afterPublish.TranscriptMarkdownPath);
        Assert.Equal(publication.FinalJsonPath, afterPublish.TranscriptJsonPath);
        Assert.Equal("completed", afterPublish.TranscriptionStatus);
        Assert.Equal("whisper-large-v3", afterPublish.TranscriptionModel);
        Assert.Equal("en", afterPublish.Language);
        Assert.False(await repository.CancelAsync(
            "job-publish",
            Baseline.AddSeconds(4),
            CancellationToken.None));

        var repeated = await repository.EnqueueAsync(
            CreateRemoteRequest(
                session,
                "job-publish-repeat",
                modelId: "whisper-large-v3",
                queuedAtUtc: Baseline.AddSeconds(5)) with
            {
                RequestedLanguage = "en",
                ReplaceExisting = true
            },
            CancellationToken.None);
        Assert.True(repeated.Created);
        await repository.ReplaceChunksAsync(
            "job-publish-repeat",
            publicationChunks,
            Baseline.AddSeconds(6),
            CancellationToken.None);

        Assert.Equal(2, (await repository.ListChunksAsync(
            "job-publish",
            CancellationToken.None)).Count);
        Assert.Equal(2, (await repository.ListChunksAsync(
            "job-publish-repeat",
            CancellationToken.None)).Count);
        Assert.Equal(
            "job-publish-repeat",
            (await database.ReadSessionStateAsync(session.Id)).CurrentTranscriptionJobId);
    }

    private static TranscriptionJobEnqueueRequest CreateRemoteRequest(
        TestSession session,
        string jobId,
        string engineId = "remote.groq",
        string modelId = "whisper-large-v3-turbo",
        string? inputSha256 = null,
        DateTimeOffset? queuedAtUtc = null) =>
        new(
            JobId: jobId,
            SessionId: session.Id,
            EngineId: engineId,
            ExecutionKind: TranscriptionExecutionKind.Remote,
            ModelId: modelId,
            InputAudioPath: session.PrimaryAudioPath,
            InputSha256: inputSha256 ?? Hash('1'),
            InputSizeBytes: 12_345,
            QueuedAtUtc: queuedAtUtc ?? Baseline,
            InputDurationSeconds: 600,
            EngineOptionsJson: "{\"timestamps\":\"segment\"}",
            RequestedLanguage: "ru",
            RemoteConsentRevision: "remote-disclosure-v1",
            RemoteConsentAtUtc: Baseline.AddDays(-1),
            PrivacyPolicyJson: "{\"zdr\":true}");

    private static async ValueTask PrepareConfigurationAttentionAsync(
        TranscriptionJobRepository repository,
        TestSession session,
        string jobId,
        string stableErrorCode)
    {
        var engineId = stableErrorCode == "zdr_route_unavailable"
            ? "remote.openrouter"
            : "remote.groq";
        var modelId = engineId == "remote.openrouter"
            ? "openai/whisper-v2"
            : "whisper-large-v3-turbo";
        var request = CreateRemoteRequest(
            session,
            jobId,
            engineId,
            modelId,
            queuedAtUtc: Baseline) with
        {
            EngineOptionsJson = engineId == "remote.openrouter"
                ? "{\"requireZeroDataRetention\":true}"
                : null,
            PrivacyPolicyJson = engineId == "remote.openrouter"
                ? "{\"requireZeroDataRetention\":true}"
                : "{\"groqAccountDataControlsRequired\":true}"
        };
        await repository.EnqueueAsync(request, CancellationToken.None);
        Assert.NotNull(await repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        await repository.ReplaceChunksAsync(
            jobId,
            [CreateChunk($"{jobId}-chunk", 0, 0, 60_000)],
            Baseline,
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            jobId,
            TranscriptionJobStatus.Uploading,
            0.5,
            $"{jobId}-chunk",
            Baseline.AddSeconds(1),
            CancellationToken.None));
        Assert.True(await repository.RequireAttentionAsync(
            jobId,
            stableErrorCode,
            "Update the frozen execution configuration.",
            currentChunkId: null,
            engineRequestId: "request-configuration",
            Baseline.AddSeconds(2),
            CancellationToken.None));
    }

    private static TranscriptionJobEnqueueRequest CreateLocalRequest(
        TestSession session,
        string jobId,
        DateTimeOffset queuedAtUtc) =>
        new(
            JobId: jobId,
            SessionId: session.Id,
            EngineId: "local.whisper",
            ExecutionKind: TranscriptionExecutionKind.Local,
            ModelId: "small",
            InputAudioPath: session.PrimaryAudioPath,
            InputSha256: Hash('1'),
            InputSizeBytes: 12_345,
            QueuedAtUtc: queuedAtUtc,
            InputDurationSeconds: 1_000,
            RequestedLanguage: "ru",
            LocalExecution: new LocalTranscriptionExecutionIdentity(
                ModelCatalogVersion: 1,
                ModelCatalogRevision: "5359861c739e955e79d9a303bcbc70fb988958b1",
                ModelFormat: "whisper.cpp.ggml-f16.v1",
                ModelPath: Path.Combine(
                    Path.GetDirectoryName(session.PrimaryAudioPath)!,
                    "small.ggml.bin"),
                ModelSizeBytes: 487_601_967,
                ModelSha256: Hash('a'),
                RuntimeVersion: "1.9.1",
                RuntimeCommit: "f049fff95a089aa9969deb009cdd4892b3e74916",
                RuntimeSourceArchiveSha256: Hash('b'),
                NativeBundleManifestSha256: Hash('c'),
                BridgeAbiVersion: 1,
                WorkerProtocolVersion: 1,
                RequestedBackend: LocalTranscriptionBackend.Auto,
                ResolvedBackend: null,
                ThreadCount: 4,
                InferenceParametersJson: "{\"temperature\":0,\"language\":\"ru\"}",
                ChunkProfileVersion: 1,
                RunIdentitySha256: Hash('d')));

    private static async ValueTask PrepareLocalChunkAsync(
        TranscriptionJobRepository repository,
        TranscriptionJobEnqueueRequest request,
        string chunkId)
    {
        Assert.True((await repository.EnqueueAsync(
            request,
            CancellationToken.None)).Created);
        Assert.Equal(
            request.JobId,
            (await repository.ClaimNextDueAsync(
                request.QueuedAtUtc,
                CancellationToken.None))!.Id);
        var preparedAtUtc = request.QueuedAtUtc.AddSeconds(1);
        await repository.ReplaceChunksAsync(
            request.JobId,
            [CreateChunk(chunkId, 0, 0, 60_000)],
            preparedAtUtc,
            CancellationToken.None);
        Assert.True(await repository.TrySetExecutionStateAsync(
            request.JobId,
            TranscriptionJobStatus.Processing,
            0,
            chunkId,
            preparedAtUtc,
            CancellationToken.None));
    }

    private static TranscriptionChunkDefinition CreateChunk(
        string id,
        int sequenceIndex,
        long startMilliseconds,
        long endMilliseconds) =>
        new(
            Id: id,
            SequenceIndex: sequenceIndex,
            StartMilliseconds: startMilliseconds,
            EndMilliseconds: endMilliseconds,
            OverlapMilliseconds: 0,
            ArtifactPath: $"{id}.mp3",
            ArtifactFormat: "mp3",
            ArtifactSha256: Hash((char)('0' + sequenceIndex)),
            ArtifactSizeBytes: 10_000);

    private static string Hash(char character) => new(character, 64);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;

        private TestDatabase(string root, LocalAppPaths paths, SqliteConnection connection)
        {
            _root = root;
            Paths = paths;
            _connection = connection;
        }

        public LocalAppPaths Paths { get; }

        public async ValueTask RetainSpeakerSourcesAsync(string sessionId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE meeting_session SET temp_session_path = $root, source_cleanup_pending = 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$root", Path.Combine(_root, "retained-sources"));
            command.Parameters.AddWithValue("$id", sessionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask<bool> HasRetainedSpeakerSourcesAsync(string sessionId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT source_cleanup_pending FROM meeting_session WHERE id = $id AND temp_session_path IS NOT NULL;";
            command.Parameters.AddWithValue("$id", sessionId);
            return await command.ExecuteScalarAsync() is long value && value == 1;
        }

        public static async ValueTask<TestDatabase> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"istranscribe-transcription-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            return new TestDatabase(root, paths, connection);
        }

        public async ValueTask<TestSession> AddReadySessionAsync(
            string? oldMarkdownPath = null,
            string? oldJsonPath = null)
        {
            var id = Guid.NewGuid();
            var sessionDirectory = Path.Combine(_root, "recordings", id.ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            var primaryAudioPath = Path.Combine(sessionDirectory, "meeting.mp3");
            await File.WriteAllBytesAsync(primaryAudioPath, [1, 2, 3, 4]);
            var record = MeetingSessionRecord.Create(
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
                TranscriptionStatus = oldMarkdownPath is null ? "not_started" : "completed",
                TranscriptionModel = oldMarkdownPath is null ? null : "legacy-model",
                Language = oldMarkdownPath is null ? null : "ru"
            };
            await new MeetingSessionRepository(_connection)
                .UpsertAsync(record, CancellationToken.None);
            return new TestSession(record.Id, primaryAudioPath, oldMarkdownPath, oldJsonPath);
        }

        public async ValueTask<SessionState> ReadSessionStateAsync(string sessionId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    current_transcription_job_id,
                    transcript_md_path,
                    transcript_json_path,
                    transcription_status,
                    transcription_model,
                    language
                FROM meeting_session
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            return new SessionState(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        public async ValueTask<long> CountJobsAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transcription_job;";
            return (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L);
        }

        public async ValueTask SetCurrentJobPointerAsync(string sessionId, string? jobId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE meeting_session
                SET current_transcription_job_id = $job_id
                WHERE id = $session_id;
                """;
            command.Parameters.AddWithValue("$job_id", jobId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$session_id", sessionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        public async ValueTask SetJobCurrentChunkAsync(string jobId, string? chunkId)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE transcription_job
                SET current_chunk_id = $chunk_id
                WHERE id = $job_id;
                """;
            command.Parameters.AddWithValue("$chunk_id", chunkId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$job_id", jobId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed record TestSession(
        string Id,
        string PrimaryAudioPath,
        string? OldMarkdownPath,
        string? OldJsonPath);

    private sealed record SessionState(
        string? CurrentTranscriptionJobId,
        string? TranscriptMarkdownPath,
        string? TranscriptJsonPath,
        string TranscriptionStatus,
        string? TranscriptionModel,
        string? Language);
}
