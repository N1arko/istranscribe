using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Synthetic application-level coverage for durable queue, checkpoint, split, cancellation,
/// publication and restart recovery behavior. Every engine response is produced in memory.
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
/// </summary>
public sealed class TranscriptionQueueWorkerEndToEndTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ShortWaveJobCheckpointsAndAtomicallyPublishesMarkdownAndJson()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromMilliseconds(250), sampleRate: 8_000);
        var engine = SyntheticEngine.Completing(
            request => Completed(request, "Synthetic short transcript.", "request-short"));
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        await fixture.WaitForPublicationCleanupAsync();
        await fixture.WaitForWorkspaceCleanupAsync(job.JobId);

        var chunk = Assert.Single(await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None));
        Assert.Equal(TranscriptionChunkStatus.Completed, chunk.Status);
        Assert.Null(chunk.ResultPath);
        Assert.Null(chunk.ResultSha256);
        Assert.Equal("request-short", chunk.EngineRequestId);
        Assert.NotNull(chunk.ResultMetadataJson);
        Assert.Equal(TranscriptionArtifactPublicationState.Promoted, completed.ArtifactPublicationState);
        Assert.NotNull(completed.TranscriptMarkdownPath);
        Assert.NotNull(completed.TranscriptJsonPath);
        Assert.True(File.Exists(completed.TranscriptMarkdownPath));
        Assert.True(File.Exists(completed.TranscriptJsonPath));
        Assert.Contains(
            "Synthetic short transcript.",
            await File.ReadAllTextAsync(completed.TranscriptMarkdownPath),
            StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(completed.TranscriptJsonPath)))
        {
            Assert.Equal("Synthetic short transcript.", json.RootElement.GetProperty("text").GetString());
            Assert.Single(json.RootElement.GetProperty("chunks").EnumerateArray());
            Assert.False(json.RootElement.TryGetProperty("local_execution", out _));
        }

        Assert.Empty(fixture.ArtifactMaterializer.FindPendingPublicationJournals(fixture.Settings));
        var partialArtifacts = Directory.EnumerateFiles(
            Path.GetDirectoryName(completed.TranscriptJsonPath)!,
            "*.partial",
            SearchOption.TopDirectoryOnly).ToArray();
        Assert.True(
            partialArtifacts.Length == 0,
            $"Unexpected partial transcript artifacts: {string.Join(", ", partialArtifacts)}");
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task RestartKeepsCompletedCheckpointAndDoesNotSendItsChunkAgain()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(4), sampleRate: 1_000);
        var secondCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngineCall = 0;
        var firstEngine = new SyntheticEngine(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref firstEngineCall) == 1)
            {
                return Completed(request, "First checkpoint.", "request-first");
            }

            secondCallStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        var planner = new AudioChunkPlanner(new AudioChunkPlannerOptions(
            MaximumDuration: TimeSpan.FromSeconds(2),
            MaximumRawBytes: 1_000_000,
            Overlap: TimeSpan.Zero,
            MinimumSplitDuration: TimeSpan.FromMilliseconds(500)));
        var firstWorker = fixture.CreateWorker(firstEngine, planner);

        await firstWorker.StartAsync(CancellationToken.None);
        await secondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstWorker.DisposeAsync();

        var interruptedChunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        var persistedCheckpoint = Assert.Single(
            interruptedChunks,
            static chunk => chunk.Status == TranscriptionChunkStatus.Completed);
        Assert.True(File.Exists(persistedCheckpoint.ResultPath));

        var restartedEngine = SyntheticEngine.Completing(
            request => Completed(request, "Second checkpoint.", "request-second"));
        await using var restartedWorker = fixture.CreateWorker(restartedEngine, planner);
        await restartedWorker.StartAsync(CancellationToken.None);

        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        await fixture.WaitForPublicationCleanupAsync();
        await fixture.WaitForWorkspaceCleanupAsync(job.JobId);
        var finalChunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        Assert.Equal(2, finalChunks.Count);
        Assert.All(finalChunks, static chunk => Assert.Equal(TranscriptionChunkStatus.Completed, chunk.Status));
        var restartedRequest = Assert.Single(restartedEngine.Requests);
        Assert.NotEqual(persistedCheckpoint.Id, restartedRequest.ChunkId);
        Assert.All(finalChunks, static chunk =>
        {
            Assert.Null(chunk.ResultPath);
            Assert.Null(chunk.ResultSha256);
        });
        Assert.True(File.Exists(completed.TranscriptJsonPath));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(completed.TranscriptJsonPath!));
        Assert.Equal(2, json.RootElement.GetProperty("chunks").GetArrayLength());
    }

    [Fact]
    public async Task PayloadTooLargeSplitsParentIntoPersistedChildrenAndCompletesThem()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromMinutes(4), sampleRate: 100);
        var call = 0;
        var engine = new SyntheticEngine((request, _) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                return ValueTask.FromResult(TranscriptionResult.Failed(new TranscriptionError(
                    TranscriptionErrorCategory.PayloadTooLarge,
                    "synthetic_413",
                    "The synthetic payload exceeded its boundary.",
                    requestId: "request-parent",
                    disposition: TranscriptionFailureDisposition.SplitInput)));
            }

            return ValueTask.FromResult(Completed(
                request,
                $"Child {request.ChunkId![..8]}.",
                $"request-child-{call}"));
        });
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        await fixture.WaitForPublicationCleanupAsync();
        await fixture.WaitForWorkspaceCleanupAsync(job.JobId);

        var chunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        Assert.Equal(3, chunks.Count);
        var parent = Assert.Single(chunks, static chunk => chunk.Status == TranscriptionChunkStatus.Split);
        var children = chunks
            .Where(static chunk => chunk.Status == TranscriptionChunkStatus.Completed)
            .OrderBy(static chunk => chunk.SequenceIndex)
            .ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, child =>
        {
            Assert.Equal(parent.Id, child.ParentChunkId);
            Assert.Equal(1, child.SplitDepth);
        });
        Assert.Equal(3, engine.Requests.Count);
        Assert.Equal(parent.Id, engine.Requests[0].ChunkId);
        Assert.Equal(
            children.Select(static child => child.Id).Order(StringComparer.Ordinal),
            engine.Requests.Skip(1).Select(static request => request.ChunkId!).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(completed.TranscriptMarkdownPath));
        Assert.True(File.Exists(completed.TranscriptJsonPath));
    }

    [Fact]
    public async Task CancellationDuringEngineCallNeverMaterializesReturnedResult()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var enteredEngine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEngine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engineReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new SyntheticEngine(async (request, _) =>
        {
            enteredEngine.TrySetResult();
            await releaseEngine.Task;
            engineReturned.TrySetResult();
            return Completed(request, "Result returned after cancellation.", "request-cancelled");
        });
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        await enteredEngine.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await fixture.Repository.CancelAsync(
            job.JobId,
            Baseline.AddSeconds(1),
            CancellationToken.None));
        worker.CancelActiveJob(job.JobId);
        releaseEngine.TrySetResult();
        await engineReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Cancelled);
        await fixture.WaitForWorkspaceCleanupAsync(job.JobId);
        await worker.DisposeAsync();

        var cancelled = await fixture.Repository.GetAsync(job.JobId, CancellationToken.None);
        Assert.NotNull(cancelled);
        Assert.True(cancelled.CancellationRequested);
        Assert.Null(cancelled.TranscriptMarkdownPath);
        Assert.Null(cancelled.TranscriptJsonPath);
        Assert.All(
            await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None),
            static chunk =>
            {
                Assert.Equal(TranscriptionChunkStatus.Cancelled, chunk.Status);
                Assert.Null(chunk.ResultPath);
            });
        var resultDirectory = Path.Combine(fixture.WorkerRoot, job.JobId, "results");
        Assert.False(Directory.Exists(resultDirectory));
        Assert.Empty(fixture.ArtifactMaterializer.FindPendingPublicationJournals(fixture.Settings));
    }

    [Fact]
    public async Task RecordingPrioritySuspensionLeavesActiveRemoteJobRunning()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = 0;
        var engine = new SyntheticEngine(async (request, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(
                () => Interlocked.Exchange(ref cancellationObserved, 1));
            entered.TrySetResult();
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return Completed(request, "Remote work continued.", "request-remote-priority");
        });
        await using var worker = fixture.CreateWorker(engine);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await worker.SuspendLocalExecutionAsync(CancellationToken.None);
            await Task.Delay(100);

            Assert.Equal(0, Volatile.Read(ref cancellationObserved));
            var active = await fixture.Repository.GetAsync(job.JobId, CancellationToken.None);
            Assert.NotNull(active);
            Assert.Equal(TranscriptionJobStatus.Processing, active.Status);
        }
        finally
        {
            release.TrySetResult();
        }

        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(0, Volatile.Read(ref cancellationObserved));
    }

    [Fact]
    public async Task CorruptPublicationJournalIsQuarantinedWithoutBlockingQueuedJob()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var recordingsFolder = Assert.IsType<string>(fixture.Settings.Storage.RecordingsFolder);
        var journalDirectory = Path.Combine(
            recordingsFolder,
            "2026",
            "08",
            "corrupt-journal");
        Directory.CreateDirectory(journalDirectory);
        var journalPath = Path.Combine(
            journalDirectory,
            "corrupt.transcript.json.publication-journal");
        await File.WriteAllTextAsync(journalPath, "{not-json");
        var engine = SyntheticEngine.Completing(
            request => Completed(request, "Queue continued after quarantine.", "request-after-quarantine"));
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        await fixture.WaitForWorkspaceCleanupAsync(job.JobId);

        Assert.True(File.Exists(completed.TranscriptJsonPath));
        Assert.False(File.Exists(journalPath));
        var quarantineDirectory = Path.Combine(journalDirectory, ".transcription-quarantine");
        var quarantined = Assert.Single(Directory.EnumerateFiles(
            quarantineDirectory,
            "*.quarantined",
            SearchOption.TopDirectoryOnly));
        Assert.Equal("{not-json", await File.ReadAllTextAsync(quarantined));
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task CorruptStagedJobMovesToAttentionWithoutBlockingNextQueuedJob()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var broken = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        Assert.NotNull(await fixture.Repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        var source = new AudioSourceFingerprint(
            broken.InputSha256,
            new FileInfo(broken.AudioPath).Length,
            broken.Duration,
            "wav");
        var descriptor = Assert.Single(new AudioChunkPlanner().Plan(source).Chunks);
        await fixture.Repository.ReplaceChunksAsync(
            broken.JobId,
            [ToDefinition(descriptor)],
            Baseline,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TrySetExecutionStateAsync(
            broken.JobId,
            TranscriptionJobStatus.Uploading,
            0.2,
            descriptor.Id,
            Baseline,
            CancellationToken.None));
        var result = Completed(
            new TranscriptionRequest(
                Guid.ParseExact(broken.SessionId, "N"),
                broken.AudioPath,
                "synthetic-model",
                "en",
                descriptor.Start,
                descriptor.End,
                Guid.ParseExact(broken.JobId, "N"),
                descriptor.Id),
            "Broken staged transcript.",
            "request-broken-stage");
        var stored = await new TranscriptionChunkResultStore().WriteAsync(
            Path.Combine(fixture.WorkerRoot, broken.JobId, "results"),
            descriptor.Id,
            result,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                broken.JobId,
                descriptor.Id,
                stored.Path,
                stored.Sha256,
                Baseline,
                stored.EngineRequestId,
                stored.MetadataJson,
                stored.UsageJson),
            CancellationToken.None));
        var merged = new TranscriptMerger().Merge(
            [new CompletedTranscriptionChunk(descriptor, result)]);
        var staged = await fixture.ArtifactMaterializer.StageAsync(
            fixture.Settings,
            NormalizedTranscriptDocument.Create(
                new TranscriptDocumentContext(
                    broken.JobId,
                    Guid.ParseExact(broken.SessionId, "N"),
                    "Broken staged recovery fixture",
                    Baseline,
                    broken.Duration,
                    TranscriptionEngineIds.Groq,
                    "synthetic-model",
                    "en",
                    source),
                merged),
            CancellationToken.None);
        Assert.True(await fixture.Repository.StageCompletionAsync(
            new TranscriptionArtifactStage(
                broken.JobId,
                staged.StagedMarkdownPath,
                staged.StagedJsonPath,
                staged.FinalMarkdownPath,
                staged.FinalJsonPath,
                staged.MarkdownSha256,
                staged.JsonSha256,
                Baseline),
            CancellationToken.None));
        await File.WriteAllTextAsync(staged.StagedJsonPath, "{invalid-staged-json");
        var queued = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var engine = SyntheticEngine.Completing(
            request => Completed(request, "Queue continued after staged quarantine.", "request-next-job"));
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var attention = await fixture.WaitForStatusAsync(
            broken.JobId,
            TranscriptionJobStatus.AttentionRequired);
        var completed = await fixture.WaitForStatusAsync(queued.JobId, TranscriptionJobStatus.Completed);
        await fixture.WaitForWorkspaceCleanupAsync(queued.JobId);

        Assert.Equal("publication_checkpoint_invalid", attention.StableErrorCode);
        Assert.True(File.Exists(completed.TranscriptJsonPath));
        var quarantineDirectory = Path.Combine(
            Path.GetDirectoryName(staged.StagedJsonPath)!,
            ".transcription-quarantine");
        Assert.Equal(2, Directory.EnumerateFiles(
            quarantineDirectory,
            "*.quarantined",
            SearchOption.TopDirectoryOnly).Count());
        Assert.Single(engine.Requests);
        Assert.Equal(queued.JobId, engine.Requests[0].JobId?.ToString("N"));
    }

    [Fact]
    public async Task StartupDeletesPersistedStageCancelledImmediatelyAfterCheckpoint()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        Assert.NotNull(await fixture.Repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
        var source = new AudioSourceFingerprint(
            job.InputSha256,
            new FileInfo(job.AudioPath).Length,
            job.Duration,
            "wav");
        var descriptor = Assert.Single(new AudioChunkPlanner().Plan(source).Chunks);
        await fixture.Repository.ReplaceChunksAsync(
            job.JobId,
            [ToDefinition(descriptor)],
            Baseline,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TrySetExecutionStateAsync(
            job.JobId,
            TranscriptionJobStatus.Uploading,
            0.2,
            descriptor.Id,
            Baseline,
            CancellationToken.None));
        var result = Completed(
            new TranscriptionRequest(
                Guid.ParseExact(job.SessionId, "N"),
                job.AudioPath,
                "synthetic-model",
                "en",
                descriptor.Start,
                descriptor.End,
                Guid.ParseExact(job.JobId, "N"),
                descriptor.Id),
            "Cancelled crash-window transcript.",
            "request-cancelled-stage");
        var stored = await new TranscriptionChunkResultStore().WriteAsync(
            Path.Combine(fixture.WorkerRoot, job.JobId, "results"),
            descriptor.Id,
            result,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                job.JobId,
                descriptor.Id,
                stored.Path,
                stored.Sha256,
                Baseline,
                stored.EngineRequestId,
                stored.MetadataJson,
                stored.UsageJson),
            CancellationToken.None));
        var merged = new TranscriptMerger().Merge(
            [new CompletedTranscriptionChunk(descriptor, result)]);
        var staged = await fixture.ArtifactMaterializer.StageAsync(
            fixture.Settings,
            NormalizedTranscriptDocument.Create(
                new TranscriptDocumentContext(
                    job.JobId,
                    Guid.ParseExact(job.SessionId, "N"),
                    "Cancelled staged recovery fixture",
                    Baseline,
                    job.Duration,
                    TranscriptionEngineIds.Groq,
                    "synthetic-model",
                    "en",
                    source),
                merged),
            CancellationToken.None);
        Assert.True(await fixture.Repository.StageCompletionAsync(
            new TranscriptionArtifactStage(
                job.JobId,
                staged.StagedMarkdownPath,
                staged.StagedJsonPath,
                staged.FinalMarkdownPath,
                staged.FinalJsonPath,
                staged.MarkdownSha256,
                staged.JsonSha256,
                Baseline),
            CancellationToken.None));
        Assert.True(await fixture.Repository.CancelAsync(
            job.JobId,
            Baseline.AddMilliseconds(1),
            CancellationToken.None));
        Assert.True(File.Exists(staged.StagedMarkdownPath));
        Assert.True(File.Exists(staged.StagedJsonPath));
        var cancelledCheckpoint = await fixture.Repository.GetAsync(job.JobId, CancellationToken.None);
        Assert.NotNull(cancelledCheckpoint);
        Assert.Equal(
            TranscriptionArtifactPublicationState.Staged,
            cancelledCheckpoint.ArtifactPublicationState);
        Assert.Equal(
            job.JobId,
            Assert.Single(await fixture.Repository.ListCancelledStagedJobsAsync(
                CancellationToken.None)).Id);
        Assert.False(await fixture.Repository.CompleteCancelledStagedArtifactCleanupAsync(
            new TranscriptionArtifactStage(
                job.JobId,
                staged.StagedMarkdownPath,
                staged.StagedJsonPath,
                staged.FinalMarkdownPath,
                $"{staged.FinalJsonPath}.different",
                staged.MarkdownSha256,
                staged.JsonSha256,
                Baseline),
            Baseline,
            CancellationToken.None));

        var engine = new SyntheticEngine((_, _) => throw new InvalidOperationException(
            "A cancelled staged job must not call its engine after restart."));
        await using var restartedWorker = fixture.CreateWorker(engine);
        await restartedWorker.StartAsync(CancellationToken.None);

        var recovered = await fixture.Repository.GetAsync(job.JobId, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(TranscriptionJobStatus.Cancelled, recovered.Status);
        Assert.Equal(TranscriptionArtifactPublicationState.None, recovered.ArtifactPublicationState);
        Assert.Null(recovered.StagedTranscriptMarkdownPath);
        Assert.Null(recovered.StagedTranscriptJsonPath);
        Assert.Null(recovered.StagedTranscriptMarkdownSha256);
        Assert.Null(recovered.StagedTranscriptJsonSha256);
        Assert.Null(recovered.TranscriptMarkdownPath);
        Assert.Null(recovered.TranscriptJsonPath);
        Assert.False(File.Exists(staged.StagedMarkdownPath));
        Assert.False(File.Exists(staged.StagedJsonPath));
        Assert.False(File.Exists(staged.FinalMarkdownPath));
        Assert.False(File.Exists(staged.FinalJsonPath));
        Assert.Empty(engine.Requests);
    }

    [Theory]
    [InlineData(
        TranscriptionFailureDisposition.TryAgain,
        TranscriptionJobStatus.RetryScheduled,
        "synthetic_transient")]
    [InlineData(
        TranscriptionFailureDisposition.AttentionRequired,
        TranscriptionJobStatus.AttentionRequired,
        "synthetic_attention")]
    public async Task EngineFailurePersistsRetryOrAttentionState(
        TranscriptionFailureDisposition disposition,
        TranscriptionJobStatus expectedStatus,
        string expectedCode)
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var engine = new SyntheticEngine((_, _) => ValueTask.FromResult(TranscriptionResult.Failed(
            new TranscriptionError(
                disposition == TranscriptionFailureDisposition.TryAgain
                    ? TranscriptionErrorCategory.Network
                    : TranscriptionErrorCategory.Authentication,
                expectedCode,
                "Synthetic bounded failure.",
                suggestedDelay: disposition == TranscriptionFailureDisposition.TryAgain
                    ? TimeSpan.FromMinutes(5)
                    : null,
                requestId: "request-failure",
                disposition: disposition))));
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var paused = await fixture.WaitForStatusAsync(job.JobId, expectedStatus);

        Assert.Equal(expectedCode, paused.StableErrorCode);
        Assert.Equal(
            expectedStatus == TranscriptionJobStatus.RetryScheduled
                ? Baseline.AddMinutes(5)
                : null,
            paused.NextAttemptAtUtc);
        Assert.Single(engine.Requests);
        var chunk = Assert.Single(await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None));
        Assert.Equal(TranscriptionChunkStatus.Pending, chunk.Status);
        Assert.Equal(expectedCode, chunk.StableErrorCode);
        Assert.Null(chunk.ResultPath);
        Assert.Null(paused.TranscriptMarkdownPath);
        Assert.Null(paused.TranscriptJsonPath);
    }

    [Fact]
    public async Task StartupCompletesJournalWhenDatabaseCommittedInCrashWindow()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(1), sampleRate: 1_000);
        var claimed = await fixture.Repository.ClaimNextDueAsync(Baseline, CancellationToken.None);
        Assert.NotNull(claimed);
        var source = new AudioSourceFingerprint(
            job.InputSha256,
            new FileInfo(job.AudioPath).Length,
            job.Duration,
            "wav");
        var descriptor = Assert.Single(new AudioChunkPlanner().Plan(source).Chunks);
        await fixture.Repository.ReplaceChunksAsync(
            job.JobId,
            [ToDefinition(descriptor)],
            Baseline,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TrySetExecutionStateAsync(
            job.JobId,
            TranscriptionJobStatus.Uploading,
            0.2,
            descriptor.Id,
            Baseline,
            CancellationToken.None));
        var result = Completed(
            new TranscriptionRequest(
                Guid.ParseExact(job.SessionId, "N"),
                job.AudioPath,
                "synthetic-model",
                "en",
                descriptor.Start,
                descriptor.End,
                Guid.ParseExact(job.JobId, "N"),
                descriptor.Id),
            "Crash window transcript.",
            "request-crash-window");
        var resultStore = new TranscriptionChunkResultStore();
        var stored = await resultStore.WriteAsync(
            Path.Combine(fixture.WorkerRoot, job.JobId, "results"),
            descriptor.Id,
            result,
            CancellationToken.None);
        Assert.True(await fixture.Repository.TryMarkChunkCompletedAsync(
            new TranscriptionChunkCompletion(
                job.JobId,
                descriptor.Id,
                stored.Path,
                stored.Sha256,
                Baseline,
                stored.EngineRequestId,
                stored.MetadataJson,
                stored.UsageJson),
            CancellationToken.None));
        var merged = new TranscriptMerger().Merge(
            [new CompletedTranscriptionChunk(descriptor, result)]);
        var document = NormalizedTranscriptDocument.Create(
            new TranscriptDocumentContext(
                job.JobId,
                Guid.ParseExact(job.SessionId, "N"),
                "Synthetic recovery fixture",
                Baseline,
                job.Duration,
                TranscriptionEngineIds.Groq,
                "synthetic-model",
                "en",
                source),
            merged);
        var staged = await fixture.ArtifactMaterializer.StageAsync(
            fixture.Settings,
            document,
            CancellationToken.None);
        Assert.True(await fixture.Repository.StageCompletionAsync(
            new TranscriptionArtifactStage(
                job.JobId,
                staged.StagedMarkdownPath,
                staged.StagedJsonPath,
                staged.FinalMarkdownPath,
                staged.FinalJsonPath,
                staged.MarkdownSha256,
                staged.JsonSha256,
                Baseline),
            CancellationToken.None));
        var pending = await fixture.ArtifactMaterializer.BeginPublicationAsync(
            staged,
            CancellationToken.None);
        Assert.True(await fixture.Repository.PublishCompletionAsync(
            new TranscriptionArtifactPublication(
                job.JobId,
                pending.FinalMarkdownPath,
                pending.FinalJsonPath,
                Baseline,
                "en",
                stored.UsageJson),
            CancellationToken.None));

        var crashWindow = await fixture.ArtifactMaterializer.GetPendingPublicationAsync(
            pending.JournalPath,
            CancellationToken.None);
        Assert.Equal(TranscriptArtifactPublicationPhase.Promoted, crashWindow.Phase);
        Assert.Equal(TranscriptionJobStatus.Completed, (await fixture.Repository.GetAsync(
            job.JobId,
            CancellationToken.None))!.Status);
        var engine = new SyntheticEngine((_, _) => throw new InvalidOperationException(
            "A completed crash-window job must not call its engine."));
        await using var restartedWorker = fixture.CreateWorker(engine);

        await restartedWorker.StartAsync(CancellationToken.None);

        Assert.Empty(fixture.ArtifactMaterializer.FindPendingPublicationJournals(fixture.Settings));
        Assert.True(File.Exists(pending.FinalMarkdownPath));
        Assert.True(File.Exists(pending.FinalJsonPath));
        Assert.Contains(
            "Crash window transcript.",
            await File.ReadAllTextAsync(pending.FinalMarkdownPath),
            StringComparison.Ordinal);
        Assert.Empty(engine.Requests);
    }

    private static TranscriptionResult Completed(
        TranscriptionRequest request,
        string text,
        string requestId)
    {
        var start = request.SourceStart ?? TimeSpan.Zero;
        var end = request.SourceEnd ?? start.Add(TimeSpan.FromSeconds(1));
        var duration = end - start;
        return TranscriptionResult.Completed(
            text,
            "en",
            [new TranscriptionSegment(text, TimeSpan.Zero, duration)],
            new TranscriptionResultMetadata(
                ResolvedModelId: request.ModelId,
                RequestId: requestId,
                Usage: new TranscriptionUsage(AudioSeconds: duration.TotalSeconds),
                SourceStart: start,
                SourceEnd: end,
                AudioDuration: duration));
    }

    private static TranscriptionChunkDefinition ToDefinition(AudioChunkDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.SequenceIndex,
        checked((long)Math.Round(descriptor.Start.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        checked((long)Math.Round(descriptor.End.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        checked((long)Math.Round(descriptor.Overlap.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        descriptor.ParentChunkId,
        descriptor.SplitDepth);

    private sealed class SyntheticEngine(
        Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> handler)
        : ITranscriptionEngine
    {
        private readonly Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> _handler =
            handler;
        private readonly ConcurrentQueue<TranscriptionRequest> _requests = new();

        public TranscriptionEngineCapabilities Capabilities { get; } = new(
            TranscriptionEngineIds.Groq,
            "Synthetic Groq",
            TranscriptionExecutionKind.Remote,
            RequiresNetwork: true,
            "Synthetic in-memory engine; no data leaves the test process.",
            [new TranscriptionPlatformTarget(TranscriptionOperatingSystem.Linux, Architecture.X64)],
            [new TranscriptionModelCapability("synthetic-model", "Synthetic model", IsRecommended: true)],
            ["en"],
            SupportsAutomaticLanguageDetection: true,
            SupportsDiarization: false,
            TranscriptionTimestampCapabilities.Segment);

        public IReadOnlyList<TranscriptionRequest> Requests => _requests.ToArray();

        public static SyntheticEngine Completing(Func<TranscriptionRequest, TranscriptionResult> resultFactory) =>
            new((request, _) => ValueTask.FromResult(resultFactory(request)));

        public ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            request.Validate();
            _requests.Enqueue(request);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Uploading, 0.5));
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Processing, 0.8));
            return _handler(request, cancellationToken);
        }
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly FixedTimeProvider _timeProvider = new(Baseline);

        private WorkerFixture(
            string root,
            LocalAppPaths paths,
            SqliteConnection connection,
            ApplicationSettings settings)
        {
            _root = root;
            Paths = paths;
            _connection = connection;
            Settings = settings;
            Repository = new TranscriptionJobRepository(paths);
            ArtifactMaterializer = new TranscriptArtifactMaterializer(new ArtifactPathResolver(paths));
            WorkerRoot = Path.Combine(paths.TempDirectory, "transcription-worker");
        }

        public LocalAppPaths Paths { get; }

        public ApplicationSettings Settings { get; }

        public TranscriptionJobRepository Repository { get; }

        public TranscriptArtifactMaterializer ArtifactMaterializer { get; }

        public string WorkerRoot { get; }

        public static async ValueTask<WorkerFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"istranscribe-worker-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var settings = ApplicationSettings.Default with
            {
                Storage = ApplicationSettings.Default.Storage with
                {
                    RecordingsFolder = Path.Combine(root, "recordings"),
                    FailedTempFolder = paths.TempDirectory
                }
            };
            return new WorkerFixture(root, paths, connection, settings);
        }

        public async ValueTask<SyntheticJob> AddRemoteJobAsync(TimeSpan duration, int sampleRate)
        {
            var sessionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var fixtureDirectory = Path.Combine(_root, "fixtures");
            Directory.CreateDirectory(fixtureDirectory);
            var audioPath = Path.Combine(fixtureDirectory, $"{sessionId:N}.wav");
            await WriteWaveAsync(audioPath, duration, sampleRate);
            var inputSha256 = await Sha256Async(audioPath);
            var session = MeetingSessionRecord.Create(
                sessionId,
                Baseline,
                "manual",
                "mixed",
                "synthetic-output",
                "synthetic-microphone") with
            {
                Status = "ready",
                SourceApp = "Synthetic recovery fixture",
                EndedAtUtc = Baseline.Add(duration),
                PrimaryAudioPath = audioPath,
                DurationSeconds = duration.TotalSeconds,
                UpdatedAtUtc = Baseline.Add(duration)
            };
            await new MeetingSessionRepository(_connection)
                .UpsertAsync(session, CancellationToken.None);
            var enqueue = await Repository.EnqueueAsync(
                new TranscriptionJobEnqueueRequest(
                    jobId.ToString("N"),
                    sessionId.ToString("N"),
                    TranscriptionEngineIds.Groq,
                    TranscriptionExecutionKind.Remote,
                    "synthetic-model",
                    audioPath,
                    inputSha256,
                    new FileInfo(audioPath).Length,
                    Baseline,
                    duration.TotalSeconds,
                    EngineOptionsJson: "{\"requireZeroDataRetention\":true}",
                    RequestedLanguage: "en",
                    RemoteConsentRevision: "synthetic-disclosure-v1",
                    RemoteConsentAtUtc: Baseline.AddMinutes(-1),
                    PrivacyPolicyJson: "{\"synthetic\":true}"),
                CancellationToken.None);
            Assert.True(enqueue.Created);
            return new SyntheticJob(
                jobId.ToString("N"),
                sessionId.ToString("N"),
                audioPath,
                inputSha256,
                duration);
        }

        public TranscriptionQueueWorker CreateWorker(
            ITranscriptionEngine engine,
            AudioChunkPlanner? planner = null) =>
            new(
                Repository,
                new TranscriptionSessionContextReader(Paths),
                new TranscriptionEngineRegistry([engine]),
                planner ?? new AudioChunkPlanner(),
                new ManagedAudioChunkMaterializer(),
                new TranscriptionChunkResultStore(),
                new TranscriptMerger(),
                ArtifactMaterializer,
                () => Settings,
                WorkerRoot,
                _timeProvider,
                TimeSpan.FromMilliseconds(10));

        public async ValueTask<TranscriptionJobRecord> WaitForStatusAsync(
            string jobId,
            TranscriptionJobStatus expectedStatus)
        {
            var timeout = Stopwatch.StartNew();
            TranscriptionJobRecord? last = null;
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                last = await Repository.GetAsync(jobId, CancellationToken.None);
                if (last?.Status == expectedStatus)
                {
                    return last;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException(
                $"Job '{jobId}' did not reach {expectedStatus}; last status was {last?.Status}.");
        }

        public async ValueTask WaitForPublicationCleanupAsync()
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (ArtifactMaterializer.FindPendingPublicationJournals(Settings).Count == 0)
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("Transcript publication journal cleanup did not finish.");
        }

        public async ValueTask WaitForWorkspaceCleanupAsync(string jobId)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                var job = await Repository.GetAsync(jobId, CancellationToken.None);
                var chunks = await Repository.ListChunksAsync(jobId, CancellationToken.None);
                var jobDirectory = Path.Combine(WorkerRoot, jobId);
                if (job?.ManifestPath is null
                    && chunks.All(static chunk => chunk.ResultPath is null
                        && chunk.ResultSha256 is null
                        && chunk.ArtifactPath is null)
                    && !Directory.Exists(jobDirectory))
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Job '{jobId}' worker workspace cleanup did not finish.");
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        private static async ValueTask WriteWaveAsync(
            string path,
            TimeSpan duration,
            int sampleRate)
        {
            const short channelCount = 1;
            const short bitsPerSample = 16;
            const short blockAlign = channelCount * (bitsPerSample / 8);
            var sampleCount = checked((int)Math.Ceiling(duration.TotalSeconds * sampleRate));
            var dataSize = checked(sampleCount * blockAlign);
            var payload = new byte[44 + dataSize];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(payload, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)(36 + dataSize)));
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(payload, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), checked((ushort)channelCount));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), checked((uint)sampleRate));
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(28, 4),
                checked((uint)(sampleRate * blockAlign)));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(32, 2), checked((ushort)blockAlign));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(34, 2), checked((ushort)bitsPerSample));
            Encoding.ASCII.GetBytes("data").CopyTo(payload, 36);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40, 4), checked((uint)dataSize));
            await File.WriteAllBytesAsync(path, payload);
        }

        private static async ValueTask<string> Sha256Async(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SyntheticJob(
        string JobId,
        string SessionId,
        string AudioPath,
        string InputSha256,
        TimeSpan Duration);
}
