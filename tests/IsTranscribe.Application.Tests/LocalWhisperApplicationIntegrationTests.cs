using System.Diagnostics;
using System.Security.Cryptography;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Worker;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Focused Application integration coverage for frozen local identity, resource and integrity
/// gates, isolated attempt telemetry and real recording-priority cancellation.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </summary>
public sealed partial class LocalWhisperApplicationIntegrationTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);

    // Explicit opt-in, app-owned temporary DB and public/synthetic audio only; no downloads.
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#verification
    [Fact]
    public async Task RealPublishedWorkerProducesSelfAndTwoRemoteSpeakersThroughDurableQueue()
    {
        var root = Environment.GetEnvironmentVariable("ISTRANSCRIBE_SPEAKER_ACCEPTANCE_ASSETS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var worker = Environment.GetEnvironmentVariable("ISTRANSCRIBE_WHISPER_ACCEPTANCE_WORKER");
        var model = Environment.GetEnvironmentVariable("ISTRANSCRIBE_WHISPER_ACCEPTANCE_MODEL");
        var output = Environment.GetEnvironmentVariable("ISTRANSCRIBE_SPEAKER_ACCEPTANCE_TWO_VOICES");
        var mic = Environment.GetEnvironmentVariable("ISTRANSCRIBE_WHISPER_ACCEPTANCE_PT_WAV");
        Assert.All(new[] { worker, model, output, mic }, static path => Assert.True(File.Exists(path)));
        var backend = Enum.Parse<LocalTranscriptionBackend>(Environment.GetEnvironmentVariable("ISTRANSCRIBE_WHISPER_ACCEPTANCE_BACKEND") ?? "metal", true);
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(durationSeconds: 16, prepare: false,
            requestedBackend: backend, realModelPath: model, realOutputWave: output, realMicrophoneWave: mic);
        using var denyNetwork = new DenyNetworkHandler();
        using var modelHttp = new HttpClient(denyNetwork);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), new CoordinatorLocalWorkerClientFactory(), modelHttp, worker, backend);
        var processor = new SpeakerAwareChunkProcessor(new SpeakerDiarizationClient(worker!, new DiarizationAssets(root)), new PassThroughChunkMaterializer());
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner(), speakerProcessor: processor);
        await queue.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed, timeoutSeconds: 180);
        using var artifact = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(completed.TranscriptJsonPath!));
        var document = artifact.RootElement;
        Assert.Equal("speaker-transcript/v1", document.GetProperty("schema").GetString());
        var ids = document.GetProperty("speakers").EnumerateArray().Select(static item => item.GetProperty("id").GetString()).ToArray();
        Assert.Contains("self", ids); Assert.Contains("remote:1", ids); Assert.Contains("remote:2", ids);
        Assert.DoesNotContain(ids, static id => id!.StartsWith("unknown:", StringComparison.Ordinal));
        var languages = document.GetProperty("source_recognitions").EnumerateArray()
            .Select(static item => item.GetProperty("detected_language").GetString()).ToArray();
        Assert.Contains("pt", languages); Assert.Contains("en", languages);
        Assert.True(document.GetProperty("turns").GetArrayLength() >= 3);
        Assert.Equal(0, denyNetwork.RequestCount);
        Assert.True(File.Exists(job.Request.PrimaryAudioArtifactPath));
        var attempts = await fixture.Repository.ListLocalChunkAttemptsAsync(job.JobId, CancellationToken.None);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, static attempt => Assert.Equal(LocalTranscriptionAttemptStatus.Completed, attempt.Status));
        var contextReader = new TranscriptionSessionContextReader(fixture.Paths);
        for (var i = 0; i < 100 && (await contextReader.GetAsync(completed.SessionId, CancellationToken.None))?.TempSessionPath is not null; i++)
            await Task.Delay(20);
        Assert.Null((await contextReader.GetAsync(completed.SessionId, CancellationToken.None))?.TempSessionPath);
    }

    [Fact]
    public void PinnedRuntimeIdentityRejectsDrift()
    {
        LocalWhisperRuntimeIdentity.PinnedV1.Validate();

        var exception = Assert.Throws<ArgumentException>(() =>
            (LocalWhisperRuntimeIdentity.PinnedV1 with { RuntimeVersion = "whisper.cpp-v1.9.2" })
            .Validate());

        Assert.Contains("FEAT-016 runtime pin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedWorkerAttemptPersistsRuntimeEvidenceAndDiagnostics()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync();
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        var result = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Synthetic local transcript.", result.Text);
        var attempt = Assert.Single(await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None));
        Assert.Equal(LocalTranscriptionAttemptStatus.Completed, attempt.Status);
        Assert.Equal(LocalTranscriptionBackend.Cpu, attempt.ResolvedBackend);
        Assert.Equal(321, attempt.InferenceDurationMilliseconds);
        var local = Assert.IsType<LocalTranscriptionJobRecord>(
            await fixture.Repository.GetLocalExecutionAsync(job.JobId, CancellationToken.None));
        Assert.Equal(LocalTranscriptionBackend.Cpu, local.Execution.ResolvedBackend);
        Assert.Equal(321, local.ProcessingDurationMilliseconds);
        Assert.True(harness.Services.TryGetDiagnostics(job.JobId, out var diagnostics));
        Assert.NotNull(diagnostics);
        Assert.Equal(LocalWhisperRuntimeIdentity.PinnedV1.RuntimeVersion, diagnostics.RuntimeVersion);
        Assert.Equal(job.ModelSha256, diagnostics.ModelSha256);
        Assert.Equal("cpu", diagnostics.ResolvedBackend);
        Assert.Equal(1, workers.CreateCount);
        Assert.Equal(1, workers.TranscribeCount);
    }

    [Fact]
    public async Task SameSizeModelMutationFailsIntegrityBeforeWorkerOrAttempt()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync();
        var mutated = job.ModelBytes.ToArray();
        mutated[0] ^= 0xff;
        await File.WriteAllBytesAsync(job.ModelPath, mutated);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        var result = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model_corrupt", result.Error?.Code);
        Assert.Equal(0, workers.CreateCount);
        Assert.Equal(0, workers.TranscribeCount);
        Assert.Empty(await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None));
    }

    [Fact]
    public async Task RestartClosesOpenAttemptAsInterruptedCancellationThenStartsNewAttempt()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync();
        await fixture.Repository.StartLocalChunkAttemptAsync(
            new LocalTranscriptionChunkAttemptStart(
                job.JobId,
                job.ChunkId,
                AttemptIndex: 0,
                LocalTranscriptionBackend.Cpu,
                Baseline.AddMinutes(-1)),
            CancellationToken.None);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        var result = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var attempts = await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(LocalTranscriptionAttemptStatus.Cancelled, attempts[0].Status);
        Assert.Null(attempts[0].StableFailureCategory);
        Assert.Equal(LocalTranscriptionAttemptStatus.Completed, attempts[1].Status);
    }

    [Fact]
    public async Task AcceleratorCrashRetriesSameChunkOnCpuSafeMode()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(
            requestedBackend: LocalTranscriptionBackend.Metal);
        var workers = RecordingWorkerFactory.CrashThenComplete(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        var first = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);
        var second = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal("native_crash", first.Error?.Code);
        Assert.True(second.Succeeded);
        Assert.Equal(["metal", "cpu"], workers.RequestedBackends);
        var local = await fixture.Repository.GetLocalExecutionAsync(
            job.JobId,
            CancellationToken.None);
        Assert.NotNull(local);
        Assert.Equal(1, local.NativeCrashCount);
        Assert.Equal(LocalTranscriptionBackend.Metal, local.LastCrashBackend);
        Assert.Equal(LocalTranscriptionBackend.Cpu, local.Execution.ResolvedBackend);
    }

    [Fact]
    public async Task RepeatedCpuSafeModeCrashRequiresAttentionWithoutThirdWorker()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(
            requestedBackend: LocalTranscriptionBackend.Metal);
        var workers = RecordingWorkerFactory.Crashing();
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        var first = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);
        var second = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);
        var third = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.False(third.Succeeded);
        Assert.Equal(TranscriptionFailureDisposition.AttentionRequired, third.Error?.Disposition);
        Assert.Equal(["metal", "cpu"], workers.RequestedBackends);
    }

    [Fact]
    public async Task AutomaticLowPowerJobDefersBeforeWorkerStart()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(TranscriptionTriggerKind.Automatic);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        var policy = new DeterministicLocalTranscriptionResourcePolicy(
            new LocalTranscriptionResourceAssessment(
                LocalTranscriptionResourceDisposition.Deferred,
                new LocalTranscriptionResourceState(
                    IsLowPowerMode: true,
                    AvailableMemoryBytes: 4L * 1024 * 1024 * 1024,
                    AvailableDiskBytes: 4L * 1024 * 1024 * 1024,
                    StableBlockCode: "low_power")
                {
                    InstalledMemoryBytes = 8L * 1024 * 1024 * 1024
                },
                StableCode: "low_power",
                SafeMessage: "Local transcription is deferred in low-power mode.",
                CanUseOneShotManualOverride: true));
        await using var harness = fixture.CreateEngine(policy, workers);

        var result = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.Equal("low_power", result.Error?.Code);
        Assert.Equal(TranscriptionFailureDisposition.TryAgain, result.Error?.Disposition);
        Assert.Equal(0, workers.CreateCount);
        Assert.Empty(await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None));
    }

    [Fact]
    public async Task AutomaticLowPowerDeferralDoesNotExhaustEngineRetryBudget()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(
            TranscriptionTriggerKind.Automatic,
            prepare: false);
        var workers = RecordingWorkerFactory.CrashThenComplete(job.ModelSha256);
        var policy = new MutableLocalTranscriptionResourcePolicy(
            new LocalTranscriptionResourceAssessment(
                LocalTranscriptionResourceDisposition.Deferred,
                new LocalTranscriptionResourceState(
                    IsLowPowerMode: true,
                    AvailableMemoryBytes: 4L * 1024 * 1024 * 1024,
                    AvailableDiskBytes: 4L * 1024 * 1024 * 1024,
                    StableBlockCode: "low_power")
                {
                    InstalledMemoryBytes = 8L * 1024 * 1024 * 1024
                },
                StableCode: "low_power",
                SafeMessage: "Local transcription is deferred in low-power mode."));
        await using var harness = fixture.CreateEngine(policy, workers);
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner());
        await queue.StartAsync(CancellationToken.None);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var deferred = await fixture.WaitForStatusAsync(
                job.JobId,
                TranscriptionJobStatus.RetryScheduled);
            Assert.Equal("low_power", deferred.StableErrorCode);
            Assert.Equal(0, workers.CreateCount);
            if (attempt < 4)
            {
                Assert.True(await fixture.Repository.RequeueAsync(
                    job.JobId,
                    Baseline,
                    CancellationToken.None));
                queue.Signal();
            }
        }

        policy.Assessment = await AllowedPolicy().AssessAsync(
                new LocalTranscriptionResourceRequest(
                    job.JobId,
                    "session",
                    TranscriptionTriggerKind.Automatic,
                    RequiredMemoryBytes: 1,
                    RequiredDiskBytes: 1,
                    HasOneShotPowerOverride: false),
                CancellationToken.None);
        Assert.True(await fixture.Repository.RequeueAsync(
            job.JobId,
            Baseline,
            CancellationToken.None));
        queue.Signal();
        var firstInferenceFailure = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.RetryScheduled);
        Assert.Equal("native_crash", firstInferenceFailure.StableErrorCode);
        Assert.True(await fixture.Repository.RequeueAsync(
            job.JobId,
            Baseline,
            CancellationToken.None));
        queue.Signal();

        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(2, workers.TranscribeCount);
    }

    [Fact]
    public async Task CancelledSuspensionDoesNotLeaveLocalQueuePaused()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(prepare: false);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.SuspendLocalExecutionAsync(cancellation.Token).AsTask());
        await queue.StartAsync(CancellationToken.None);

        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(1, workers.TranscribeCount);
    }

    [Fact]
    public async Task SuspensionAtomicallyClosesLocalAdmissionBeforeActiveRegistration()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(prepare: false);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner());
        var admissionReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queue.LocalAdmissionBarrierForTests = async cancellationToken =>
        {
            admissionReached.TrySetResult();
            await releaseAdmission.Task.WaitAsync(cancellationToken);
        };
        await queue.StartAsync(CancellationToken.None);
        await admissionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.SuspendLocalExecutionAsync(CancellationToken.None);
        releaseAdmission.TrySetResult();

        var deferred = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.RetryScheduled);
        Assert.Equal("recording_in_progress", deferred.StableErrorCode);
        Assert.Equal(0, workers.CreateCount);

        queue.LocalAdmissionBarrierForTests = null;
        await queue.ResumeLocalExecutionAsync(CancellationToken.None);
        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(1, workers.TranscribeCount);
    }

    [Fact]
    public async Task LocalSuspensionWaitsUntilTheActiveWorkerReleasesItsContext()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(prepare: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = RecordingWorkerFactory.PreemptThenComplete(
            job.ModelSha256,
            entered,
            cancellationObserved,
            release);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner());
        await queue.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var suspension = queue.SuspendLocalExecutionAsync(CancellationToken.None).AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(suspension.IsCompleted);

        release.TrySetResult();
        await suspension.WaitAsync(TimeSpan.FromSeconds(5));
        var preempted = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.RetryScheduled);
        Assert.Equal("recording_preempted", preempted.StableErrorCode);

        await queue.ResumeLocalExecutionAsync(CancellationToken.None);
        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(2, workers.TranscribeCount);
    }

    [Fact]
    public async Task TerminalLocalJobRetriesModelOrphanCollectionWithoutRestart()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(prepare: false);
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        var collector = new TerminalRetryingOrphanCollector(fixture.Repository, job.JobId);
        await using var queue = fixture.CreateQueueWorker(
            harness.Engine,
            new AudioChunkPlanner(),
            collector);

        await queue.StartAsync(CancellationToken.None);
        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        await collector.Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(collector.TerminalAttempts >= 2);
    }

    [Fact]
    public async Task CancelledSuspensionWaitKeepsTheDispatchedPreemptionRecoverable()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(prepare: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = RecordingWorkerFactory.PreemptThenComplete(
            job.ModelSha256,
            entered,
            cancellationObserved,
            release);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        await using var queue = fixture.CreateQueueWorker(harness.Engine, new AudioChunkPlanner());
        await queue.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var suspension = queue.SuspendLocalExecutionAsync(cancellation.Token).AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspension);
        release.TrySetResult();

        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);
        Assert.Equal(2, workers.TranscribeCount);
    }

    [Fact]
    public async Task LocalInitializationFailureDegradesModelSnapshotsWithoutThrowing()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.ModelStoreRoot, "active"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.ModelStoreRoot, "active", ".removing-corrupt.json"),
            "{not-json}");
        var workers = RecordingWorkerFactory.Completing(new string('a', 64));
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);

        await harness.Services.InitializeAsync(CancellationToken.None);

        Assert.All(
            harness.Services.GetModelSnapshots(),
            model =>
            {
                Assert.Equal(RuntimeLocalModelState.Failed, model.State);
                Assert.Equal("model_download_activation", model.StableErrorCode);
            });
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsBlockedModelDownload()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        using var handler = new BlockingDownloadHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var workers = RecordingWorkerFactory.Completing(new string('a', 64));
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers, client);
        var install = harness.Services.InstallAsync("large-v3-turbo", CancellationToken.None).AsTask();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = harness.Services.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ModelProgressPublicationIsBoundedToPercentChangesAndCompletion()
    {
        const long totalBytes = 1_500_000_000;
        var step = totalBytes / 100;
        long publishedBytes = 0;
        var publications = 0;
        for (long bytes = 0; bytes < totalBytes; bytes += 128 * 1024)
        {
            if (LocalTranscriptionServices.ShouldPublishModelProgress(
                    bytes,
                    totalBytes,
                    step,
                    ref publishedBytes))
            {
                publications++;
            }
        }

        Assert.True(LocalTranscriptionServices.ShouldPublishModelProgress(
            totalBytes,
            totalBytes,
            step,
            ref publishedBytes));
        Assert.InRange(publications + 1, 100, 101);
    }

    [Fact]
    public void ConcurrentModelMutationKeepsOrphanCollectionPendingAfterAnOlderScan()
    {
        Assert.False(LocalTranscriptionServices.ShouldKeepOrphanCollectionPending(
            collectionGeneration: 7,
            currentGeneration: 7));
        Assert.True(LocalTranscriptionServices.ShouldKeepOrphanCollectionPending(
            collectionGeneration: 7,
            currentGeneration: 8));
    }

    [Fact]
    public void PowerOverrideIsBoundToOneExactJobAndCanBeRevokedBeforeClaim()
    {
        var overrides = new LocalPowerOverrideStore();

        overrides.Authorize("job-a");
        Assert.False(overrides.Consume("job-b"));
        Assert.True(overrides.Consume("job-a"));
        Assert.False(overrides.Consume("job-a"));

        overrides.Authorize("job-a");
        overrides.Revoke("job-a");
        Assert.False(overrides.Consume("job-a"));
    }

    [Fact]
    public async Task RecordingPrioritySignalCancelsWorkerAndPersistsPreemptedAttempt()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = RecordingWorkerFactory.Blocking(entered);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers);
        using var cancellation = new CancellationTokenSource();
        var execution = harness.Engine.TranscribeAsync(
                job.Request,
                progress: null,
                cancellation.Token)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Engine.RequestPreemption(job.JobId);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var attempt = Assert.Single(await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None));
        Assert.Equal(LocalTranscriptionAttemptStatus.Preempted, attempt.Status);
        Assert.Null(attempt.StableFailureCategory);
    }

    [Fact]
    public async Task MissingResourceMeasurementsFailClosedBeforeWorkerStart()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync();
        var workers = RecordingWorkerFactory.Completing(job.ModelSha256);
        var policy = new DeterministicLocalTranscriptionResourcePolicy(
            new LocalTranscriptionResourceAssessment(
                LocalTranscriptionResourceDisposition.Allowed,
                new LocalTranscriptionResourceState(
                    IsLowPowerMode: false,
                    AvailableMemoryBytes: null,
                    AvailableDiskBytes: null)));
        await using var harness = fixture.CreateEngine(policy, workers);

        var result = await harness.Engine.TranscribeAsync(
            job.Request,
            progress: null,
            CancellationToken.None);

        Assert.Equal("resource_state_unavailable", result.Error?.Code);
        Assert.Equal(0, workers.CreateCount);
    }

    [Fact]
    public async Task QueueSuspensionPreemptsActiveLocalWorkerAndResumesFromCompletedCheckpoint()
    {
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(
            durationSeconds: 2,
            prepare: false);
        var enteredSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = RecordingWorkerFactory.CompleteThenBlockThenComplete(
            job.ModelSha256,
            enteredSecondChunk);
        using var denyNetwork = new DenyNetworkHandler();
        using var modelClient = new HttpClient(denyNetwork);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), workers, modelClient);
        var planner = new AudioChunkPlanner(new AudioChunkPlannerOptions(
            MaximumDuration: TimeSpan.FromSeconds(1),
            MaximumRawBytes: 1_000_000,
            Overlap: TimeSpan.Zero,
            MinimumSplitDuration: TimeSpan.FromMilliseconds(250)));
        await using var queue = fixture.CreateQueueWorker(harness.Engine, planner);
        await queue.StartAsync(CancellationToken.None);
        await enteredSecondChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var active = await fixture.Repository.GetAsync(job.JobId, CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal(TranscriptionJobStatus.Processing, active.Status);
        var beforePreemption = await fixture.Repository.ListChunksAsync(
            job.JobId,
            CancellationToken.None);
        Assert.Equal(2, beforePreemption.Count);
        Assert.Single(beforePreemption, static chunk =>
            chunk.Status == TranscriptionChunkStatus.Completed);

        await queue.SuspendLocalExecutionAsync(CancellationToken.None);
        var suspended = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.RetryScheduled);
        Assert.Equal("recording_preempted", suspended.StableErrorCode);
        var afterPreemption = await fixture.Repository.ListChunksAsync(
            job.JobId,
            CancellationToken.None);
        Assert.Single(afterPreemption, static chunk =>
            chunk.Status == TranscriptionChunkStatus.Completed);
        var preempted = Assert.Single(
            await fixture.Repository.ListLocalChunkAttemptsAsync(job.JobId, CancellationToken.None),
            static attempt => attempt.Status == LocalTranscriptionAttemptStatus.Preempted);
        Assert.Equal(afterPreemption[1].Id, preempted.ChunkId);

        await queue.ResumeLocalExecutionAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.Completed);

        var finalChunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        Assert.All(finalChunks, static chunk =>
            Assert.Equal(TranscriptionChunkStatus.Completed, chunk.Status));
        var attempts = await fixture.Repository.ListLocalChunkAttemptsAsync(
            job.JobId,
            CancellationToken.None);
        Assert.Equal(3, attempts.Count);
        Assert.Equal(2, attempts.Count(static attempt =>
            attempt.Status == LocalTranscriptionAttemptStatus.Completed));
        Assert.Equal(3, workers.TranscribeCount);
        Assert.NotNull(completed.TranscriptJsonPath);
        var artifactJson = await File.ReadAllTextAsync(completed.TranscriptJsonPath);
        using var artifact = System.Text.Json.JsonDocument.Parse(artifactJson);
        var localArtifact = artifact.RootElement.GetProperty("local_execution");
        Assert.Equal(
            LocalWhisperRuntimeIdentity.PinnedV1.RuntimeVersion,
            localArtifact.GetProperty("runtime_version").GetString());
        Assert.Equal(job.ModelSha256, localArtifact.GetProperty("model_sha256").GetString());
        Assert.Equal("cpu", localArtifact.GetProperty("resolved_backend").GetString());
        Assert.Equal(642, localArtifact.GetProperty("processing_duration_milliseconds").GetInt64());
        Assert.DoesNotContain("model_path", artifactJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worker_executable", artifactJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, denyNetwork.RequestCount);
    }

    private static DeterministicLocalTranscriptionResourcePolicy AllowedPolicy() => new(
        new LocalTranscriptionResourceAssessment(
            LocalTranscriptionResourceDisposition.Allowed,
            new LocalTranscriptionResourceState(
                IsLowPowerMode: false,
                AvailableMemoryBytes: 4L * 1024 * 1024 * 1024,
                AvailableDiskBytes: 4L * 1024 * 1024 * 1024)
            {
                InstalledMemoryBytes = 8L * 1024 * 1024 * 1024
            }));

    private sealed class LocalEngineHarness(
        LocalWhisperTranscriptionEngine engine,
        LocalTranscriptionServices services) : IAsyncDisposable
    {
        public LocalWhisperTranscriptionEngine Engine { get; } = engine;

        public LocalTranscriptionServices Services { get; } = services;

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class LocalEngineFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly FixedTimeProvider _timeProvider = new(Baseline);

        private LocalEngineFixture(
            string root,
            LocalAppPaths paths,
            SqliteConnection connection)
        {
            _root = root;
            Paths = paths;
            _connection = connection;
            Repository = new TranscriptionJobRepository(paths);
        }

        public LocalAppPaths Paths { get; }

        public TranscriptionJobRepository Repository { get; }

        public string ModelStoreRoot => Path.Combine(_root, "models");

        public static async ValueTask<LocalEngineFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"istranscribe-local-application-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            return new LocalEngineFixture(root, paths, connection);
        }

        public LocalEngineHarness CreateEngine(
            ILocalTranscriptionResourcePolicy resourcePolicy,
            ILocalWorkerClientFactory workers,
            HttpClient? modelDownloadHttpClient = null,
            string? workerPath = null,
            LocalTranscriptionBackend backend = LocalTranscriptionBackend.Cpu)
        {
            var options = new LocalTranscriptionRuntimeOptions(
                WorkerExecutablePath: workerPath ?? Path.Combine(_root, "transcription-worker", "fake-worker"),
                RequestedBackend: backend,
                ThreadCount: 4,
                ResourcePolicy: resourcePolicy,
                ModelStoreRoot: ModelStoreRoot,
                ModelDownloadHttpClient: modelDownloadHttpClient,
                WorkerClientFactory: workers);
            var services = LocalTranscriptionServices.Create(
                ModelStoreRoot,
                Repository,
                options,
                _timeProvider);
            return new LocalEngineHarness(
                new LocalWhisperTranscriptionEngine(
                    Repository,
                    services,
                    options,
                    _timeProvider),
                services);
        }

        public TranscriptionQueueWorker CreateQueueWorker(
            ITranscriptionEngine engine,
            AudioChunkPlanner planner,
            ILocalModelOrphanCollector? localModelOrphanCollector = null,
            SpeakerAwareChunkProcessor? speakerProcessor = null)
        {
            var settings = ApplicationSettings.Default with
            {
                Storage = ApplicationSettings.Default.Storage with
                {
                    RecordingsFolder = Path.Combine(_root, "recordings"),
                    FailedTempFolder = Paths.TempDirectory,
                }
            };
            return new TranscriptionQueueWorker(
                Repository,
                new TranscriptionSessionContextReader(Paths),
                new TranscriptionEngineRegistry([engine]),
                planner,
                new PassThroughChunkMaterializer(),
                new TranscriptionChunkResultStore(),
                new TranscriptMerger(),
                new TranscriptArtifactMaterializer(new ArtifactPathResolver(Paths)),
                () => settings,
                Path.Combine(Paths.TempDirectory, "transcription-worker"),
                _timeProvider,
                TimeSpan.FromMilliseconds(10),
                localModelOrphanCollector, speakerProcessor: speakerProcessor);
        }

        public async ValueTask<PreparedLocalJob> AddPreparedJobAsync(
            TranscriptionTriggerKind triggerKind = TranscriptionTriggerKind.Manual,
            double durationSeconds = 1,
            bool prepare = true,
            LocalTranscriptionBackend requestedBackend = LocalTranscriptionBackend.Cpu,
            string? realModelPath = null,
            string? realOutputWave = null,
            string? realMicrophoneWave = null,
            string? remoteEngineId = null,
            string? remoteModelId = null)
        {
            var sessionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var chunkId = Guid.NewGuid().ToString("N");
            var fixtureDirectory = Path.Combine(_root, "fixtures", jobId.ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            var audioPath = Path.Combine(fixtureDirectory, "recording.wav");
            await File.WriteAllBytesAsync(audioPath, [1, 2, 3, 4, 5, 6, 7, 8]);
            if (realOutputWave is not null) File.Copy(realOutputWave, audioPath, overwrite: true);
            var modelPath = realModelPath ?? Path.Combine(fixtureDirectory, "ggml-base.bin");
            byte[] modelBytes = [10, 20, 30, 40, 50, 60, 70, 80];
            if (realModelPath is null) await File.WriteAllBytesAsync(modelPath, modelBytes);
            var modelId = realModelPath is null ? "base" : "large-v3-turbo";
            var inputSha256 = await Sha256Async(audioPath);
            var modelSha256 = await Sha256Async(modelPath);
            var session = MeetingSessionRecord.Create(
                sessionId,
                Baseline.AddMinutes(-1),
                "manual",
                "mixed",
                "output-device",
                "microphone-device") with
            {
                Status = "ready",
                EndedAtUtc = Baseline,
                DurationSeconds = durationSeconds,
                PrimaryAudioPath = audioPath,
            };
            await new MeetingSessionRepository(_connection)
                .UpsertAsync(session, CancellationToken.None);
            if (realMicrophoneWave is not null)
            {
                var sources = Path.Combine(Paths.TempDirectory, "sessions", sessionId.ToString("N"));
                Directory.CreateDirectory(sources);
                var outputPath = Path.Combine(sources, "output.wav");
                var micPath = Path.Combine(sources, "microphone.wav");
                File.Copy(audioPath, outputPath); File.Copy(realMicrophoneWave, micPath);
                TranscriptionSourceHandoff.Create(sources, sessionId,
                    [new(IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Output, outputPath, new FileInfo(outputPath).Length, Baseline),
                     new(IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Microphone, micPath, new FileInfo(micPath).Length, Baseline)]);
                await new MeetingSessionRepository(_connection).UpsertAsync(session with
                {
                    TempSessionPath = sources,
                    SourceCleanupPending = true
                }, CancellationToken.None);
            }
            var runtime = LocalWhisperRuntimeIdentity.PinnedV1;
            var identity = new LocalTranscriptionExecutionIdentity(
                ModelCatalogVersion: realModelPath is null ? 1 : 2,
                ModelCatalogRevision: "5359861c739e955e79d9a303bcbc70fb988958b1",
                ModelFormat: "whisper.cpp.ggml-f16.v1",
                ModelPath: modelPath,
                ModelSizeBytes: new FileInfo(modelPath).Length,
                ModelSha256: modelSha256,
                RuntimeVersion: runtime.RuntimeVersion,
                RuntimeCommit: runtime.RuntimeCommit,
                RuntimeSourceArchiveSha256: runtime.RuntimeSourceArchiveSha256,
                NativeBundleManifestSha256: runtime.NativeBundleManifestSha256,
                BridgeAbiVersion: runtime.BridgeAbiVersion,
                WorkerProtocolVersion: runtime.WorkerProtocolVersion,
                RequestedBackend: requestedBackend,
                ResolvedBackend: null,
                ThreadCount: 4,
                InferenceParametersJson: "{\"language\":\"en\",\"temperature\":0}",
                ChunkProfileVersion: 1,
                RunIdentitySha256: Sha256("synthetic-local-run"));
            var enqueue = await Repository.EnqueueAsync(
                new TranscriptionJobEnqueueRequest(
                    JobId: jobId.ToString("N"),
                    SessionId: sessionId.ToString("N"),
                    EngineId: remoteEngineId ?? LocalWhisperTranscriptionEngine.EngineId,
                    ExecutionKind: remoteEngineId is null ? TranscriptionExecutionKind.Local : TranscriptionExecutionKind.Remote,
                    ModelId: remoteModelId ?? modelId,
                    InputAudioPath: audioPath,
                    InputSha256: inputSha256,
                    InputSizeBytes: new FileInfo(audioPath).Length,
                    QueuedAtUtc: Baseline,
                    InputDurationSeconds: durationSeconds,
                    RequestedLanguage: "en",
                    RemoteConsentRevision: remoteEngineId is null ? null : RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(remoteEngineId),
                    RemoteConsentAtUtc: remoteEngineId is null ? null : Baseline,
                    PrivacyPolicyJson: remoteEngineId is null ? null : RemoteTranscriptionDisclosureCatalog.BuildFrozenPolicyJson(remoteEngineId, true),
                    TriggerKind: triggerKind,
                    LocalExecution: remoteEngineId is null ? identity : null),
                CancellationToken.None);
            Assert.True(enqueue.Created);
            if (prepare)
            {
                Assert.NotNull(await Repository.ClaimNextDueAsync(Baseline, CancellationToken.None));
                await Repository.ReplaceChunksAsync(
                    jobId.ToString("N"),
                    [
                        new TranscriptionChunkDefinition(
                            chunkId,
                            SequenceIndex: 0,
                            StartMilliseconds: 0,
                            EndMilliseconds: checked((long)Math.Round(durationSeconds * 1_000)),
                            OverlapMilliseconds: 0)
                    ],
                    Baseline,
                    CancellationToken.None);
                Assert.True(await Repository.TrySetExecutionStateAsync(
                    jobId.ToString("N"),
                    TranscriptionJobStatus.Processing,
                    progress: 0,
                    chunkId,
                    Baseline,
                    CancellationToken.None));
            }

            return new PreparedLocalJob(
                jobId.ToString("N"),
                chunkId,
                modelPath,
                modelSha256,
                modelBytes,
                new TranscriptionRequest(
                    sessionId,
                    audioPath,
                    modelId,
                    "en",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(durationSeconds),
                    jobId,
                    chunkId));
        }

        public async ValueTask<TranscriptionJobRecord> WaitForStatusAsync(
            string jobId,
            TranscriptionJobStatus expected,
            int timeoutSeconds = 8,
            bool failOnAttention = false)
        {
            var timeout = Stopwatch.StartNew();
            TranscriptionJobRecord? last = null;
            while (timeout.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                last = await Repository.GetAsync(jobId, CancellationToken.None);
                if (failOnAttention && last?.Status is TranscriptionJobStatus.AttentionRequired or TranscriptionJobStatus.RetryScheduled)
                    throw new InvalidOperationException($"Acceptance job stopped at {last.Status}: {last.StableErrorCode}.");
                if (last?.Status == expected)
                {
                    return last;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException(
                $"Job '{jobId}' did not reach {expected}; last state was {last?.Status}, code {last?.StableErrorCode}.");
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static async ValueTask<string> Sha256Async(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }

        private static string Sha256(string value) => Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private sealed class RecordingWorkerFactory : ILocalWorkerClientFactory
    {
        private readonly Func<WorkerStartPayload, CancellationToken, Task<WorkerResultPayload>> _transcribe;

        private RecordingWorkerFactory(
            Func<WorkerStartPayload, CancellationToken, Task<WorkerResultPayload>> transcribe)
        {
            _transcribe = transcribe;
        }

        public int CreateCount { get; private set; }

        public int TranscribeCount { get; private set; }

        public List<string> RequestedBackends { get; } = [];

        public static RecordingWorkerFactory Completing(string modelSha256) => new((request, _) =>
            Task.FromResult(CompletedResult(modelSha256, request)));

        public static RecordingWorkerFactory CrashThenComplete(string modelSha256)
        {
            var call = 0;
            return new RecordingWorkerFactory((request, _) =>
                Interlocked.Increment(ref call) == 1
                    ? Task.FromException<WorkerResultPayload>(new WorkerProcessExitedException())
                    : Task.FromResult(CompletedResult(modelSha256, request)));
        }

        public static RecordingWorkerFactory Crashing() => new((_, _) =>
            Task.FromException<WorkerResultPayload>(new WorkerProcessExitedException()));

        public static RecordingWorkerFactory CompleteThenBlockThenComplete(
            string modelSha256,
            TaskCompletionSource enteredSecondChunk)
        {
            var call = 0;
            return new RecordingWorkerFactory(async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref call) == 2)
                {
                    enteredSecondChunk.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return CompletedResult(modelSha256, request);
            });
        }

        public static RecordingWorkerFactory Blocking(TaskCompletionSource entered) => new(
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking worker should be cancelled.");
            });

        public static RecordingWorkerFactory PreemptThenComplete(
            string modelSha256,
            TaskCompletionSource entered,
            TaskCompletionSource cancellationObserved,
            TaskCompletionSource release)
        {
            var call = 0;
            return new RecordingWorkerFactory(async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    using var registration = cancellationToken.Register(
                        () => cancellationObserved.TrySetResult());
                    entered.TrySetResult();
                    await release.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return CompletedResult(modelSha256, request);
            });
        }

        private static WorkerResultPayload CompletedResult(
            string modelSha256,
            WorkerStartPayload request) => new(
                Language: "en",
                ProcessingMilliseconds: 321,
                RuntimeVersion: LocalWhisperRuntimeIdentity.PinnedV1.RuntimeVersion,
                ModelSha256: modelSha256,
                Backend: "cpu",
                Segments:
                [
                    new WorkerSegmentPayload(
                        0,
                        request.EndMilliseconds - request.StartMilliseconds,
                        "Synthetic local transcript.")
                ]);

        public ILocalWorkerClient Create()
        {
            CreateCount++;
            return new RecordingWorkerClient(this);
        }

        private sealed class RecordingWorkerClient(RecordingWorkerFactory owner) : ILocalWorkerClient
        {
            public Task<WorkerReadyPayload> StartAsync(
                string workerExecutablePath,
                CancellationToken cancellationToken) => Task.FromResult(new WorkerReadyPayload(
                    State: "initialized",
                    WorkerVersion: LocalWorkerRuntimeContract.CurrentWorkerVersion,
                    Backend: null,
                    AvailableMemoryBytes: null));

            public Task<WorkerReadyPayload> ProbeAsync(
                WorkerProbePayload request,
                CancellationToken cancellationToken) => Task.FromResult(new WorkerReadyPayload(
                    State: "probe_completed",
                    WorkerVersion: LocalWorkerRuntimeContract.CurrentWorkerVersion,
                    Backend: "cpu",
                    AvailableMemoryBytes: 4L * 1024 * 1024 * 1024));

            public Task<WorkerResultPayload> TranscribeAsync(
                string jobId,
                int chunkIndex,
                WorkerStartPayload request,
                IProgress<WorkerProgressPayload>? progress,
                CancellationToken cancellationToken)
            {
                owner.TranscribeCount++;
                owner.RequestedBackends.Add(request.Backend);
                return owner._transcribe(request, cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PassThroughChunkMaterializer : IAudioChunkMaterializer
    {
        public ValueTask<MaterializedAudioChunk> MaterializeAsync(
            TranscriptionJobRecord job,
            AudioChunkDescriptor chunk,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MaterializedAudioChunk(
                job.InputAudioPath,
                "wav",
                job.InputSha256,
                job.InputSizeBytes,
                IsTemporary: false));
        }

        public void DeleteTemporary(MaterializedAudioChunk chunk)
        {
        }
    }

    private sealed class BlockingDownloadHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The synthetic model response must be cancelled.");
        }
    }

    private sealed class DenyNetworkHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            throw new InvalidOperationException("Local inference attempted a network request.");
        }
    }

    private sealed class TerminalRetryingOrphanCollector(
        TranscriptionJobRepository repository,
        string jobId) : ILocalModelOrphanCollector
    {
        private int _terminalAttempts;

        public int TerminalAttempts => Volatile.Read(ref _terminalAttempts);

        public TaskCompletionSource Succeeded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask CollectRetainedOrphansAsync(CancellationToken cancellationToken)
        {
            var job = await repository.GetAsync(jobId, cancellationToken);
            if (job?.Status is not (TranscriptionJobStatus.Completed or TranscriptionJobStatus.Cancelled))
            {
                return;
            }

            if (Interlocked.Increment(ref _terminalAttempts) == 1)
            {
                throw new IOException("Synthetic transient model-store cleanup failure.");
            }

            Succeeded.TrySetResult();
        }
    }

    private sealed class MutableLocalTranscriptionResourcePolicy(
        LocalTranscriptionResourceAssessment assessment) : ILocalTranscriptionResourcePolicy
    {
        public LocalTranscriptionResourceAssessment Assessment { get; set; } = assessment;

        public LocalTranscriptionResourceState CurrentState => Assessment.State;

        public ValueTask<LocalTranscriptionResourceAssessment> AssessAsync(
            LocalTranscriptionResourceRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Assessment);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record PreparedLocalJob(
        string JobId,
        string ChunkId,
        string ModelPath,
        string ModelSha256,
        byte[] ModelBytes,
        TranscriptionRequest Request);
}
