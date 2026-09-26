using System.Security.Cryptography;
using System.Text.Json;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Application.Transcription;

/// <summary>
/// Executes the durable provider-neutral FIFO queue with one active job and one active upload.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed class TranscriptionQueueWorker : IAsyncDisposable
{
    private const long MaximumStagedTranscriptBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions TranscriptSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly TranscriptionJobRepository _repository;
    private readonly TranscriptionSessionContextReader _sessionContextReader;
    private readonly TranscriptionEngineRegistry _engines;
    private readonly AudioChunkPlanner _planner;
    private readonly IAudioChunkMaterializer _chunkMaterializer;
    private readonly TranscriptionChunkResultStore _resultStore;
    private readonly TranscriptMerger _merger;
    private readonly TranscriptArtifactMaterializer _artifactMaterializer;
    private readonly Func<ApplicationSettings> _settingsAccessor;
    private readonly string _workerRoot;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleDelay;
    private readonly ILocalModelOrphanCollector? _localModelOrphanCollector;
    private readonly SpeakerAwareChunkProcessor? _speakerProcessor;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _activeJobSync = new();
    private readonly object _localPreemptionSync = new();
    private readonly HashSet<string> _suspendedLocalJobIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopCancellation;
    private CancellationTokenSource? _activeJobCancellation;
    private TaskCompletionSource? _activeJobCompletion;
    private string? _activeJobId;
    private Task? _runTask;
    private int _localExecutionSuspended;

    internal Func<CancellationToken, ValueTask>? LocalAdmissionBarrierForTests { get; set; }

    public TranscriptionQueueWorker(
        TranscriptionJobRepository repository,
        TranscriptionSessionContextReader sessionContextReader,
        TranscriptionEngineRegistry engines,
        AudioChunkPlanner planner,
        IAudioChunkMaterializer chunkMaterializer,
        TranscriptionChunkResultStore resultStore,
        TranscriptMerger merger,
        TranscriptArtifactMaterializer artifactMaterializer,
        Func<ApplicationSettings> settingsAccessor,
        string workerRoot,
        TimeProvider? timeProvider = null,
        TimeSpan? idleDelay = null,
        ILocalModelOrphanCollector? localModelOrphanCollector = null,
        SpeakerAwareChunkProcessor? speakerProcessor = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessionContextReader = sessionContextReader ?? throw new ArgumentNullException(nameof(sessionContextReader));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _chunkMaterializer = chunkMaterializer ?? throw new ArgumentNullException(nameof(chunkMaterializer));
        _resultStore = resultStore ?? throw new ArgumentNullException(nameof(resultStore));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _artifactMaterializer = artifactMaterializer ?? throw new ArgumentNullException(nameof(artifactMaterializer));
        _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        _workerRoot = string.IsNullOrWhiteSpace(workerRoot)
            ? throw new ArgumentException("A transcription worker root is required.", nameof(workerRoot))
            : Path.GetFullPath(workerRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleDelay = idleDelay ?? TimeSpan.FromSeconds(2);
        _localModelOrphanCollector = localModelOrphanCollector;
        _speakerProcessor = speakerProcessor;
        if (_idleDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleDelay), "The worker idle delay must be positive.");
        }
    }

    public event EventHandler? StateChanged;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runTask is not null)
            {
                return;
            }

            Directory.CreateDirectory(_workerRoot);
            await _repository.RecoverInterruptedAsync(_timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            await RecoverArtifactPublicationsAsync(cancellationToken).ConfigureAwait(false);
            await CleanupTerminalWorkspacesAsync(cancellationToken).ConfigureAwait(false);
            await TryCollectRetainedLocalModelOrphansAsync(cancellationToken).ConfigureAwait(false);
            var stopCancellation = new CancellationTokenSource();
            _stopCancellation = stopCancellation;
            _runTask = Task.Run(() => RunAsync(stopCancellation.Token), CancellationToken.None);
            Signal();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Signal()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public void CancelActiveJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        lock (_activeJobSync)
        {
            if (string.Equals(_activeJobId, jobId.Trim(), StringComparison.Ordinal))
            {
                _activeJobCancellation?.Cancel();
            }
        }

        Signal();
    }

    /// <summary>
    /// Gives recording capture priority over local inference. The current isolated worker receives
    /// cancellation and its completed chunk checkpoints remain durable; remote jobs are unaffected.
    /// </summary>
    /// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
    public async ValueTask SuspendLocalExecutionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? jobId = null;
        var preemptionRegistered = false;
        var preemptionDispatched = false;
        Task? activeJobCompletion = null;
        try
        {
            CancellationTokenSource? activeCancellation;
            lock (_activeJobSync)
            {
                Volatile.Write(ref _localExecutionSuspended, 1);
                jobId = _activeJobId;
                activeCancellation = _activeJobCancellation;
                activeJobCompletion = _activeJobCompletion?.Task;
            }

            if (jobId is null || activeCancellation is null || activeJobCompletion is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var job = await _repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job?.ExecutionKind != TranscriptionExecutionKind.Local)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            lock (_activeJobSync)
            {
                if (!string.Equals(_activeJobId, jobId, StringComparison.Ordinal)
                    || !ReferenceEquals(_activeJobCancellation, activeCancellation)
                    || !ReferenceEquals(_activeJobCompletion?.Task, activeJobCompletion))
                {
                    return;
                }

                lock (_localPreemptionSync)
                {
                    _suspendedLocalJobIds.Add(jobId);
                    preemptionRegistered = true;
                }

                if (_engines.TryGet(job.EngineId, out var engine)
                    && engine is LocalWhisperTranscriptionEngine localEngine)
                {
                    localEngine.RequestPreemption(jobId);
                }

                activeCancellation.Cancel();
                preemptionDispatched = true;
            }

            Signal();
            await activeJobCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_activeJobSync)
            {
                Volatile.Write(ref _localExecutionSuspended, 0);
            }
            if (preemptionRegistered && !preemptionDispatched && jobId is not null)
            {
                lock (_localPreemptionSync)
                {
                    _suspendedLocalJobIds.Remove(jobId);
                }
            }

            Signal();
            throw;
        }
    }

    public async ValueTask ResumeLocalExecutionAsync(CancellationToken cancellationToken)
    {
        lock (_activeJobSync)
        {
            Volatile.Write(ref _localExecutionSuspended, 0);
        }
        string[] jobIds;
        lock (_localPreemptionSync)
        {
            jobIds = _suspendedLocalJobIds.ToArray();
        }

        foreach (var jobId in jobIds)
        {
            if (await _repository.RequeueAsync(jobId, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
            {
                lock (_localPreemptionSync)
                {
                    _suspendedLocalJobIds.Remove(jobId);
                }
            }
        }

        Signal();
    }

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        CancellationTokenSource? stopCancellation;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            runTask = _runTask;
            stopCancellation = _stopCancellation;
            _runTask = null;
            _stopCancellation = null;
            stopCancellation?.Cancel();
            lock (_activeJobSync)
            {
                _activeJobCancellation?.Cancel();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopCancellation?.IsCancellationRequested == true)
            {
            }
        }

        stopCancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RecoverArtifactPublicationsAsync(cancellationToken).ConfigureAwait(false);
                await TryCollectRetainedLocalModelOrphansAsync(cancellationToken).ConfigureAwait(false);
                await CleanupTerminalWorkspacesAsync(cancellationToken).ConfigureAwait(false);
                var job = await _repository
                    .ClaimNextDueAsync(_timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                if (job is null)
                {
                    await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (job.ExecutionKind == TranscriptionExecutionKind.Local
                    && LocalAdmissionBarrierForTests is { } admissionBarrier)
                {
                    await admissionBarrier(cancellationToken).ConfigureAwait(false);
                }

                using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var jobCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var deferLocalJob = false;
                lock (_activeJobSync)
                {
                    if (job.ExecutionKind == TranscriptionExecutionKind.Local
                        && Volatile.Read(ref _localExecutionSuspended) != 0)
                    {
                        deferLocalJob = true;
                    }
                    else
                    {
                        _activeJobId = job.Id;
                        _activeJobCancellation = jobCancellation;
                        _activeJobCompletion = jobCompletion;
                    }
                }

                if (deferLocalJob)
                {
                    await DeferSuspendedLocalJobAsync(job, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                OnStateChanged();
                try
                {
                    await ProcessJobAsync(job, jobCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
                {
                    // A user cancellation is already durable. Shutdown recovery requeues only
                    // transient states, so no result is published after either cancellation path.
                    await HandleLocalPreemptionAsync(job, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TranscriptArtifactRecoveryRequiredException)
                {
                    // The durable journal is reconciled at the top of the next iteration.
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await PauseForAttentionAsync(
                            job.Id,
                            "worker_failure",
                            "Transcription processing could not continue.",
                            currentChunkId: null,
                            engineRequestId: null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_activeJobSync)
                    {
                        if (ReferenceEquals(_activeJobCancellation, jobCancellation))
                        {
                            _activeJobCancellation = null;
                            _activeJobCompletion = null;
                            _activeJobId = null;
                        }
                    }

                    // ProcessJobAsync has returned and the isolated model context is gone. Recording
                    // priority can proceed while non-resource workspace/model cleanup continues.
                    jobCompletion.TrySetResult();
                    await CleanupTerminalWorkspaceAsync(job.Id, CancellationToken.None)
                        .ConfigureAwait(false);
                    OnStateChanged();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                OnStateChanged();
                await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask DeferSuspendedLocalJobAsync(
        TranscriptionJobRecord job,
        CancellationToken cancellationToken)
    {
        lock (_localPreemptionSync)
        {
            _suspendedLocalJobIds.Add(job.Id);
        }

        var now = _timeProvider.GetUtcNow();
        await _repository.ScheduleRetryAsync(
                job.Id,
                now.AddMinutes(5),
                "recording_in_progress",
                "Local transcription will continue after the active recording.",
                currentChunkId: null,
                engineRequestId: null,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        OnStateChanged();
    }

    private async ValueTask HandleLocalPreemptionAsync(
        TranscriptionJobRecord job,
        CancellationToken cancellationToken)
    {
        bool wasPreempted;
        lock (_localPreemptionSync)
        {
            wasPreempted = _suspendedLocalJobIds.Contains(job.Id);
        }

        if (!wasPreempted || job.ExecutionKind != TranscriptionExecutionKind.Local)
        {
            return;
        }

        var latest = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (latest is not null
            && !latest.CancellationRequested
            && latest.Status is TranscriptionJobStatus.Preparing
                or TranscriptionJobStatus.Uploading
                or TranscriptionJobStatus.Processing)
        {
            var now = _timeProvider.GetUtcNow();
            await _repository.ScheduleRetryAsync(
                    latest.Id,
                    Volatile.Read(ref _localExecutionSuspended) == 0 ? now : now.AddMinutes(5),
                    "recording_preempted",
                    "Local transcription paused for an active recording.",
                    latest.CurrentChunkId,
                    engineRequestId: null,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (_engines.TryGet(job.EngineId, out var engine)
            && engine is LocalWhisperTranscriptionEngine localEngine)
        {
            localEngine.ClearPreemption(job.Id);
        }

        if (Volatile.Read(ref _localExecutionSuspended) == 0)
        {
            if (await _repository.RequeueAsync(job.Id, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
            {
                lock (_localPreemptionSync)
                {
                    _suspendedLocalJobIds.Remove(job.Id);
                }
            }

            Signal();
        }

        OnStateChanged();
    }

    private async ValueTask ProcessJobAsync(
        TranscriptionJobRecord claimedJob,
        CancellationToken cancellationToken)
    {
        if (!_engines.TryGet(claimedJob.EngineId, out var engine) || engine is null)
        {
            await PauseForAttentionAsync(
                    claimedJob.Id,
                    "engine_unavailable",
                    "The selected transcription engine is unavailable.",
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (engine.Capabilities.ExecutionKind != claimedJob.ExecutionKind)
        {
            await PauseForAttentionAsync(
                    claimedJob.Id,
                    "engine_identity_mismatch",
                    "The saved transcription engine identity is invalid.",
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!File.Exists(claimedJob.InputAudioPath))
        {
            await PauseForAttentionAsync(
                    claimedJob.Id,
                    "input_missing",
                    "The source recording is unavailable.",
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var source = await ValidateSourceAsync(claimedJob, cancellationToken).ConfigureAwait(false);
        var jobDirectory = GetJobDirectory(claimedJob.Id);
        Directory.CreateDirectory(jobDirectory);
        var chunks = await _repository.ListChunksAsync(claimedJob.Id, cancellationToken)
            .ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            var manifest = _planner.Plan(source);
            await SaveManifestAsync(claimedJob, manifest, cancellationToken).ConfigureAwait(false);
            await _repository.ReplaceChunksAsync(
                    claimedJob.Id,
                    manifest.Chunks.Select(ToDefinition).ToArray(),
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            chunks = await _repository.ListChunksAsync(claimedJob.Id, cancellationToken)
                .ConfigureAwait(false);
            OnStateChanged();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentJob = await _repository.GetAsync(claimedJob.Id, cancellationToken).ConfigureAwait(false);
            if (currentJob is null || currentJob.CancellationRequested
                || currentJob.Status == TranscriptionJobStatus.Cancelled)
            {
                return;
            }

            chunks = await _repository.ListChunksAsync(claimedJob.Id, cancellationToken)
                .ConfigureAwait(false);
            var executable = chunks
                .Where(static chunk => chunk.Status != TranscriptionChunkStatus.Split)
                .OrderBy(static chunk => chunk.SequenceIndex)
                .ThenBy(static chunk => chunk.StartMilliseconds)
                .ToArray();
            var next = executable.FirstOrDefault(static chunk => chunk.Status != TranscriptionChunkStatus.Completed);
            if (next is null)
            {
                await FinalizeJobAsync(currentJob, source, executable, cancellationToken).ConfigureAwait(false);
                return;
            }

            var outcome = await ProcessChunkAsync(
                    currentJob,
                    source,
                    chunks,
                    executable,
                    next,
                    engine,
                    jobDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome == ChunkProcessingOutcome.Paused)
            {
                return;
            }
        }
    }

    private async ValueTask<ChunkProcessingOutcome> ProcessChunkAsync(
        TranscriptionJobRecord job,
        AudioSourceFingerprint source,
        IReadOnlyList<TranscriptionChunkRecord> persistedChunks,
        IReadOnlyList<TranscriptionChunkRecord> executableChunks,
        TranscriptionChunkRecord chunk,
        ITranscriptionEngine engine,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        var descriptor = ToDescriptor(source, chunk);
        MaterializedAudioChunk? materialized = null;
        var completedCount = executableChunks.Count(
            static candidate => candidate.Status == TranscriptionChunkStatus.Completed);
        var initialProgress = CalculateProgress(completedCount, executableChunks.Count, 0.01);
        try
        {
            if (!await _repository.TrySetExecutionStateAsync(
                    job.Id,
                    TranscriptionJobStatus.Preparing,
                    initialProgress,
                    chunk.Id,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                return ChunkProcessingOutcome.Paused;
            }

            OnStateChanged();
            materialized = await _chunkMaterializer.MaterializeAsync(
                    job,
                    descriptor,
                    Path.Combine(jobDirectory, "input"),
                    cancellationToken)
                .ConfigureAwait(false);
            var activeState = job.ExecutionKind == TranscriptionExecutionKind.Local
                ? TranscriptionJobStatus.Processing
                : TranscriptionJobStatus.Uploading;
            if (!await _repository.TrySetExecutionStateAsync(
                    job.Id,
                    activeState,
                    initialProgress,
                    chunk.Id,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                return ChunkProcessingOutcome.Paused;
            }

            OnStateChanged();
            if (materialized.SizeBytes > AudioChunkPlannerOptions.Default.MaximumRawBytes)
            {
                return await SplitOrPauseAsync(
                        job,
                        source,
                        persistedChunks,
                        chunk,
                        new TranscriptionError(
                            TranscriptionErrorCategory.PayloadTooLarge,
                            "local_payload_limit",
                            job.ExecutionKind == TranscriptionExecutionKind.Local
                                ? "The prepared local audio chunk exceeds its bounded memory boundary."
                                : "The materialized audio chunk exceeds the upload boundary.",
                            disposition: TranscriptionFailureDisposition.SplitInput),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var progress = new InlineProgress<TranscriptionProgress>(update =>
                ApplyEngineProgressSynchronously(
                    job.Id,
                    chunk.Id,
                    completedCount,
                    executableChunks.Count,
                    update));
            var request = new TranscriptionRequest(
                Guid.ParseExact(job.SessionId, "N"),
                materialized.Path,
                job.ModelId,
                string.Equals(job.RequestedLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : job.RequestedLanguage,
                descriptor.Start,
                descriptor.End,
                Guid.ParseExact(job.Id, "N"),
                chunk.Id,
                ReadRequireZeroDataRetention(job.EngineOptionsJson));
            // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition
            var result = _speakerProcessor is null
                ? await engine.TranscribeAsync(request, progress, cancellationToken).ConfigureAwait(false)
                : await _speakerProcessor.ProcessAsync(job, descriptor,
                    await _sessionContextReader.GetAsync(job.SessionId, cancellationToken).ConfigureAwait(false)
                        ?? throw new SpeakerProcessingException("input_missing"),
                    jobDirectory, engine, request, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (latest is null || latest.CancellationRequested)
            {
                return ChunkProcessingOutcome.Paused;
            }

            if (!result.Succeeded)
            {
                return await HandleEngineFailureAsync(
                        latest,
                        source,
                        persistedChunks,
                        chunk,
                        result.Error ?? new TranscriptionError(
                            TranscriptionErrorCategory.Processing,
                            "unknown_engine_failure",
                            "The transcription engine returned an incomplete failure.",
                            disposition: TranscriptionFailureDisposition.Terminal),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var stored = await _resultStore.WriteAsync(
                    Path.Combine(jobDirectory, "results"),
                    chunk.Id,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await _repository.TryMarkChunkCompletedAsync(
                    new TranscriptionChunkCompletion(
                        job.Id,
                        chunk.Id,
                        stored.Path,
                        stored.Sha256,
                        _timeProvider.GetUtcNow(),
                        stored.EngineRequestId,
                        stored.MetadataJson,
                        stored.UsageJson),
                    cancellationToken).ConfigureAwait(false))
            {
                return ChunkProcessingOutcome.Paused;
            }

            OnStateChanged();
            return ChunkProcessingOutcome.Continue;
        }
        catch (SpeakerProcessingException exception)
        {
            await PauseForAttentionAsync(job.Id, exception.Code,
                "Speaker-aware transcription needs attention. The recording and completed recognition checkpoints are retained.",
                chunk.Id, engineRequestId: null, cancellationToken).ConfigureAwait(false);
            return ChunkProcessingOutcome.Paused;
        }
        catch (NotSupportedException)
        {
            await PauseForAttentionAsync(
                    job.Id,
                    "unsupported_chunk_format",
                    "This recording format cannot be divided into bounded upload chunks.",
                    chunk.Id,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return ChunkProcessingOutcome.Paused;
        }
        catch (InvalidDataException)
        {
            await PauseForAttentionAsync(
                    job.Id,
                    "invalid_audio_chunk",
                    "A readable audio chunk could not be prepared.",
                    chunk.Id,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return ChunkProcessingOutcome.Paused;
        }
        finally
        {
            if (materialized is not null)
            {
                _chunkMaterializer.DeleteTemporary(materialized);
            }
        }
    }

    private async ValueTask<ChunkProcessingOutcome> HandleEngineFailureAsync(
        TranscriptionJobRecord job,
        AudioSourceFingerprint source,
        IReadOnlyList<TranscriptionChunkRecord> persistedChunks,
        TranscriptionChunkRecord chunk,
        TranscriptionError error,
        CancellationToken cancellationToken)
    {
        if (job.ExecutionKind == TranscriptionExecutionKind.Local
            && error.Disposition == TranscriptionFailureDisposition.TryAgain
            && error.Code is "low_power" or "energy_saver")
        {
            // OS power policy is a device state, not an inference failure. Keep the automatic
            // job deferred until that state changes without exhausting the transient engine
            // retry budget.
            // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
            var delay = error.SuggestedDelay is { } suggestedDelay && suggestedDelay > TimeSpan.Zero
                ? suggestedDelay
                : TimeSpan.FromMinutes(5);
            return await ScheduleRetryAsync(
                    job,
                    chunk,
                    error,
                    _timeProvider.GetUtcNow().Add(delay),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var completedTransientAttempts = chunk.AttemptCount;
        if (job.ExecutionKind == TranscriptionExecutionKind.Local)
        {
            var localAttempts = await _repository
                .ListLocalChunkAttemptsAsync(job.Id, cancellationToken)
                .ConfigureAwait(false);
            var durableTransientFailures = localAttempts.Count(attempt =>
                string.Equals(attempt.ChunkId, chunk.Id, StringComparison.Ordinal)
                && attempt.Status is LocalTranscriptionAttemptStatus.Crashed
                    or LocalTranscriptionAttemptStatus.Timeout
                    or LocalTranscriptionAttemptStatus.NativeFailure
                    or LocalTranscriptionAttemptStatus.ProtocolFailure);
            completedTransientAttempts = Math.Max(0, durableTransientFailures - 1);
        }

        var effectiveError = string.Equals(error.Code, "transport_timeout", StringComparison.Ordinal)
            && chunk.AttemptCount >= 1
            ? new TranscriptionError(
                error.Category,
                error.Code,
                "The provider timed out repeatedly for this audio chunk.",
                requestId: error.RequestId,
                disposition: TranscriptionFailureDisposition.SplitInput)
            : error;
        var decision = new TranscriptionRetryPolicy().Decide(
            effectiveError,
            completedTransientAttempts,
            _timeProvider.GetUtcNow());
        return decision.Action switch
        {
            TranscriptionFailureAction.SplitChunk => await SplitOrPauseAsync(
                    job,
                    source,
                    persistedChunks,
                    chunk,
                    effectiveError,
                    cancellationToken)
                .ConfigureAwait(false),
            TranscriptionFailureAction.ScheduleRetry => await ScheduleRetryAsync(
                    job,
                    chunk,
                    effectiveError,
                    decision.NextAttemptAt!.Value,
                    cancellationToken)
                .ConfigureAwait(false),
            TranscriptionFailureAction.RequireAttention => await RequireAttentionAsync(
                    job,
                    chunk,
                    effectiveError,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => await RequireAttentionAsync(
                    job,
                    chunk,
                    new TranscriptionError(
                        effectiveError.Category,
                        effectiveError.Code,
                        "The transcription request requires review before another attempt.",
                        requestId: effectiveError.RequestId,
                        disposition: TranscriptionFailureDisposition.AttentionRequired),
                    cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private async ValueTask<ChunkProcessingOutcome> SplitOrPauseAsync(
        TranscriptionJobRecord job,
        AudioSourceFingerprint source,
        IReadOnlyList<TranscriptionChunkRecord> persistedChunks,
        TranscriptionChunkRecord chunk,
        TranscriptionError error,
        CancellationToken cancellationToken)
    {
        var currentManifest = new AudioChunkManifest(
            AudioChunkPlanner.CurrentManifestVersion,
            source,
            persistedChunks
                .Where(static persisted => persisted.Status != TranscriptionChunkStatus.Split)
                .Select(persisted => ToDescriptor(source, persisted))
                .OrderBy(static descriptor => descriptor.SequenceIndex)
                .ToArray());
        AudioChunkManifest splitManifest;
        try
        {
            splitManifest = _planner.Split(currentManifest, chunk.Id);
        }
        catch (InvalidOperationException)
        {
            await PauseForAttentionAsync(
                    job.Id,
                    "minimum_chunk_rejected",
                    "The provider rejected an audio chunk at the minimum safe duration.",
                    chunk.Id,
                    error.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            return ChunkProcessingOutcome.Paused;
        }

        var children = splitManifest.Chunks
            .Where(candidate => string.Equals(candidate.ParentChunkId, chunk.Id, StringComparison.Ordinal))
            .Select(ToDefinition)
            .ToArray();
        await _repository.SplitChunkAsync(
                new TranscriptionChunkSplitRequest(
                    job.Id,
                    chunk.Id,
                    children,
                    _timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
        await SaveManifestAsync(job, splitManifest, cancellationToken).ConfigureAwait(false);
        OnStateChanged();
        return ChunkProcessingOutcome.Continue;
    }

    private async ValueTask<ChunkProcessingOutcome> ScheduleRetryAsync(
        TranscriptionJobRecord job,
        TranscriptionChunkRecord chunk,
        TranscriptionError error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await _repository.ScheduleRetryAsync(
                job.Id,
                nextAttemptAt,
                SanitizeCode(error.Code),
                SafeEngineMessage(error),
                chunk.Id,
                SanitizeRequestId(error.RequestId),
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        OnStateChanged();
        return ChunkProcessingOutcome.Paused;
    }

    private async ValueTask<ChunkProcessingOutcome> RequireAttentionAsync(
        TranscriptionJobRecord job,
        TranscriptionChunkRecord chunk,
        TranscriptionError error,
        CancellationToken cancellationToken)
    {
        await PauseForAttentionAsync(
                job.Id,
                SanitizeCode(error.Code),
                SafeEngineMessage(error),
                chunk.Id,
                SanitizeRequestId(error.RequestId),
                cancellationToken)
            .ConfigureAwait(false);
        return ChunkProcessingOutcome.Paused;
    }

    private async ValueTask FinalizeJobAsync(
        TranscriptionJobRecord job,
        AudioSourceFingerprint source,
        IReadOnlyList<TranscriptionChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        var completed = new List<CompletedTranscriptionChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Status != TranscriptionChunkStatus.Completed
                || chunk.ResultPath is null
                || chunk.ResultSha256 is null)
            {
                throw new InvalidDataException("A completed transcription manifest has a missing checkpoint.");
            }

            var result = await _resultStore
                .ReadAsync(chunk.ResultPath, chunk.ResultSha256, cancellationToken)
                .ConfigureAwait(false);
            completed.Add(new CompletedTranscriptionChunk(ToDescriptor(source, chunk), result));
        }

        var reconciled = _speakerProcessor is null ? completed
            : await _speakerProcessor.ReconcileAsync(completed, GetJobDirectory(job.Id), cancellationToken).ConfigureAwait(false);
        var merged = _merger.Merge(reconciled);
        if (_speakerProcessor is not null)
            merged = merged with { Segments = SpeakerTurnAssembler.GroupTurns(merged.Segments) };
        var session = await _sessionContextReader.GetAsync(job.SessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The meeting session disappeared before transcript publication.");
        if (!Guid.TryParseExact(session.SessionId, "N", out var sessionId))
        {
            throw new InvalidDataException("The meeting session identity is invalid.");
        }

        NormalizedTranscriptLocalExecution? localExecution = null;
        if (job.ExecutionKind == TranscriptionExecutionKind.Local)
        {
            var local = await _repository.GetLocalExecutionAsync(job.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The frozen local execution identity is unavailable.");
            localExecution = new NormalizedTranscriptLocalExecution(
                local.Execution.RuntimeVersion,
                local.Execution.ModelSha256,
                local.Execution.RequestedBackend.ToString().ToLowerInvariant(),
                local.Execution.ResolvedBackend?.ToString().ToLowerInvariant(),
                local.Execution.ThreadCount,
                local.Execution.NativeBundleManifestSha256,
                local.ProcessingDurationMilliseconds);
        }

        var document = NormalizedTranscriptDocument.Create(
            new TranscriptDocumentContext(
                job.Id,
                sessionId,
                session.DisplayTitle,
                session.StartedAtUtc,
                session.Duration,
                job.EngineId,
                job.ModelId,
                job.RequestedLanguage,
                source,
                localExecution,
                SpeakerAware: _speakerProcessor is not null,
                UiLanguage: _settingsAccessor().ReleaseV2?.Language ?? "en",
                SourceTracks: _speakerProcessor is null ? null : await SpeakerAwareChunkProcessor.GetSourcesAsync(
                    job, session, GetJobDirectory(job.Id), cancellationToken).ConfigureAwait(false),
                SourceRecognitions: _speakerProcessor is null ? null : await _speakerProcessor.ReadProvenanceAsync(
                    completed, GetJobDirectory(job.Id), cancellationToken).ConfigureAwait(false)),
            merged);
        var staged = await _artifactMaterializer
            .StageAsync(_settingsAccessor(), document, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var stagedAt = _timeProvider.GetUtcNow();
            if (!await _repository.StageCompletionAsync(
                    new TranscriptionArtifactStage(
                        job.Id,
                        staged.StagedMarkdownPath,
                        staged.StagedJsonPath,
                        staged.FinalMarkdownPath,
                        staged.FinalJsonPath,
                        staged.MarkdownSha256,
                        staged.JsonSha256,
                        stagedAt),
                    cancellationToken).ConfigureAwait(false))
            {
                var latest = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
                if (latest?.Status != TranscriptionJobStatus.Finalizing)
                {
                    _artifactMaterializer.DiscardStagedArtifacts(staged);
                    return;
                }
            }

            OnStateChanged();
            await PublishStagedAsync(
                    job.Id,
                    staged,
                    merged.DetectedLanguage,
                    merged.Usage,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            var latest = await _repository.GetAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            if (latest?.Status == TranscriptionJobStatus.Cancelled)
            {
                _artifactMaterializer.DiscardStagedArtifacts(staged);
            }

            throw;
        }
    }

    private async ValueTask PublishStagedAsync(
        string jobId,
        StagedTranscriptArtifacts staged,
        string? detectedLanguage,
        TranscriptionUsage? usage,
        CancellationToken cancellationToken)
    {
        var usageJson = usage is null ? null : JsonSerializer.Serialize(usage, SerializerOptions);
        await _artifactMaterializer.PublishAsync(
                staged,
                async (published, publishCancellationToken) =>
                {
                    var committed = await _repository.PublishCompletionAsync(
                            new TranscriptionArtifactPublication(
                                jobId,
                                published.FinalMarkdownPath,
                                published.FinalJsonPath,
                                _timeProvider.GetUtcNow(),
                                detectedLanguage,
                                usageJson),
                            publishCancellationToken)
                        .ConfigureAwait(false);
                    if (!committed)
                    {
                        var current = await _repository.GetAsync(jobId, publishCancellationToken)
                            .ConfigureAwait(false);
                        if (current?.Status != TranscriptionJobStatus.Completed)
                        {
                            throw new InvalidOperationException(
                                "The transcript publication database transaction was rejected.");
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        OnStateChanged();
    }

    private async ValueTask RecoverArtifactPublicationsAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsAccessor();
        foreach (var journalPath in _artifactMaterializer.FindPendingPublicationJournals(settings))
        {
            PendingTranscriptArtifactPublication? pending = null;
            TranscriptionJobRecord? job = null;
            try
            {
                pending = await _artifactMaterializer
                    .GetPendingPublicationAsync(journalPath, cancellationToken)
                    .ConfigureAwait(false);
                job = await _repository.GetAsync(pending.JobId, cancellationToken).ConfigureAwait(false);
                var databaseCommitted = job is
                {
                    Status: TranscriptionJobStatus.Completed,
                    ArtifactPublicationState: TranscriptionArtifactPublicationState.Promoted
                }
                    && string.Equals(job.TranscriptMarkdownPath, pending.FinalMarkdownPath, StringComparison.Ordinal)
                    && string.Equals(job.TranscriptJsonPath, pending.FinalJsonPath, StringComparison.Ordinal)
                    && string.Equals(job.StagedTranscriptMarkdownSha256, pending.MarkdownSha256, StringComparison.Ordinal)
                    && string.Equals(job.StagedTranscriptJsonSha256, pending.JsonSha256, StringComparison.Ordinal);
                await _artifactMaterializer.RecoverPublicationAsync(
                        journalPath,
                        databaseCommitted
                            ? TranscriptArtifactRecoveryAction.Complete
                            : TranscriptArtifactRecoveryAction.RollBack,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!databaseCommitted && job?.Status == TranscriptionJobStatus.Finalizing)
                {
                    var now = _timeProvider.GetUtcNow();
                    await _repository.ScheduleRetryAsync(
                            job.Id,
                            now,
                            "publication_interrupted",
                            "Transcript publication was interrupted and will be rebuilt.",
                            currentChunkId: null,
                            engineRequestId: null,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                OnStateChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await TryPausePublicationRecoveryAsync(
                        job?.Id ?? pending?.JobId,
                        "publication_journal_invalid",
                        "Transcript publication recovery requires review.",
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await _artifactMaterializer.QuarantinePublicationJournalAsync(
                            journalPath,
                            "journal-invalid",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // The item remains discoverable for a later recovery pass. Other jobs continue.
                }

                OnStateChanged();
            }
        }

        await RecoverCancelledStagedArtifactsAsync(cancellationToken).ConfigureAwait(false);

        var stagedJobs = await _repository.ListStagedJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in stagedJobs)
        {
            if (job.StagedTranscriptMarkdownPath is null
                || job.StagedTranscriptJsonPath is null
                || job.TranscriptMarkdownPath is null
                || job.TranscriptJsonPath is null
                || job.StagedTranscriptMarkdownSha256 is null
                || job.StagedTranscriptJsonSha256 is null)
            {
                await PauseForAttentionAsync(
                        job.Id,
                        "publication_checkpoint_invalid",
                        "Transcript publication metadata is incomplete.",
                        currentChunkId: null,
                        engineRequestId: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var staged = new StagedTranscriptArtifacts(
                        job.Id,
                        job.TranscriptMarkdownPath,
                        job.TranscriptJsonPath,
                        job.StagedTranscriptMarkdownPath,
                        job.StagedTranscriptJsonPath,
                        job.StagedTranscriptMarkdownSha256,
                        job.StagedTranscriptJsonSha256);
            try
            {
                var metadata = await ReadStagedDocumentMetadataAsync(job, cancellationToken).ConfigureAwait(false);
                await PublishStagedAsync(
                        job.Id,
                        staged,
                        metadata.DetectedLanguage,
                        metadata.Usage,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is not TranscriptArtifactRecoveryRequiredException)
                {
                    try
                    {
                        _artifactMaterializer.QuarantineStagedArtifacts(
                            staged,
                            "staged-invalid");
                    }
                    catch (Exception)
                    {
                        // The original staged files remain in place when quarantine cannot be copied.
                    }
                }

                await TryPausePublicationRecoveryAsync(
                        job.Id,
                        "publication_checkpoint_invalid",
                        "Transcript publication recovery requires review.",
                        cancellationToken)
                    .ConfigureAwait(false);
                OnStateChanged();
            }
        }
    }

    private async ValueTask RecoverCancelledStagedArtifactsAsync(
        CancellationToken cancellationToken)
    {
        var cancelledJobs = await _repository.ListCancelledStagedJobsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var job in cancelledJobs)
        {
            if (job.StagedTranscriptMarkdownPath is null
                || job.StagedTranscriptJsonPath is null
                || job.TranscriptMarkdownPath is null
                || job.TranscriptJsonPath is null
                || job.StagedTranscriptMarkdownSha256 is null
                || job.StagedTranscriptJsonSha256 is null)
            {
                continue;
            }

            var staged = new StagedTranscriptArtifacts(
                job.Id,
                job.TranscriptMarkdownPath,
                job.TranscriptJsonPath,
                job.StagedTranscriptMarkdownPath,
                job.StagedTranscriptJsonPath,
                job.StagedTranscriptMarkdownSha256,
                job.StagedTranscriptJsonSha256);
            try
            {
                if (!_artifactMaterializer.DiscardStagedArtifacts(staged))
                {
                    continue;
                }

                var cleanedAtUtc = _timeProvider.GetUtcNow();
                if (await _repository.CompleteCancelledStagedArtifactCleanupAsync(
                        new TranscriptionArtifactStage(
                            job.Id,
                            job.StagedTranscriptMarkdownPath,
                            job.StagedTranscriptJsonPath,
                            job.TranscriptMarkdownPath,
                            job.TranscriptJsonPath,
                            job.StagedTranscriptMarkdownSha256,
                            job.StagedTranscriptJsonSha256,
                            cleanedAtUtc),
                        cleanedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    OnStateChanged();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
            {
                // Persisted pointers remain available for a later fail-closed recovery pass.
            }
        }
    }

    private static async ValueTask<PublicationMetadata> ReadStagedDocumentMetadataAsync(
        TranscriptionJobRecord job,
        CancellationToken cancellationToken)
    {
        var path = File.Exists(job.StagedTranscriptJsonPath)
            ? job.StagedTranscriptJsonPath
            : job.TranscriptJsonPath;
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException("The staged normalized transcript is unavailable.", path);
        }

        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumStagedTranscriptBytes)
        {
            throw new InvalidDataException("The staged normalized transcript is too large.");
        }

        var document = await JsonSerializer.DeserializeAsync<NormalizedTranscriptDocument>(
                stream,
                TranscriptSerializerOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The staged normalized transcript is invalid.");
        return new PublicationMetadata(document.DetectedLanguage, document.Usage);
    }

    private async ValueTask TryPausePublicationRecoveryAsync(
        string? jobId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            await PauseForAttentionAsync(
                    jobId,
                    code,
                    message,
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Recovery isolation keeps unrelated queue items available.
        }
    }

    private async ValueTask CleanupTerminalWorkspacesAsync(CancellationToken cancellationToken)
    {
        var jobIds = await _repository.ListTerminalWorkspaceCleanupJobIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var jobId in jobIds)
        {
            await CleanupTerminalWorkspaceAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask CleanupTerminalWorkspaceAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await _repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job?.Status is not (TranscriptionJobStatus.Completed or TranscriptionJobStatus.Cancelled))
            {
                return;
            }

            var jobDirectory = GetJobDirectory(jobId);
            if (_speakerProcessor is not null)
            {
                var releasedRoot = await _repository.ReleaseSpeakerSourcesAsync(job.Id, (sessionId, root, primary) =>
                {
                    var expected = _artifactMaterializer.GetTempSessionPath(_settingsAccessor(), Guid.ParseExact(sessionId, "N"));
                    return TranscriptionSourceHandoff.ReleaseFiles(root, expected, Guid.ParseExact(sessionId, "N"), primary);
                }, cancellationToken).ConfigureAwait(false);
                if (releasedRoot is not null)
                {
                    // The source receipt is removed only after durable pointer cleanup commits.
                    File.Delete(Path.Combine(releasedRoot, TranscriptionSourceHandoff.FileName));
                    foreach (var directory in Directory.EnumerateDirectories(releasedRoot, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(static path => path.Length))
                        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    if (!Directory.EnumerateFileSystemEntries(releasedRoot).Any()) Directory.Delete(releasedRoot);
                }
            }
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }

            if (!Directory.Exists(jobDirectory))
            {
                await _repository.CompleteTerminalWorkspaceCleanupAsync(
                        jobId,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (job.ExecutionKind == TranscriptionExecutionKind.Local)
            {
                // @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
                // @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
                await TryCollectRetainedLocalModelOrphansAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            // Durable pointers remain, so a later pass retries cleanup without widening scope.
        }
    }

    private async ValueTask TryCollectRetainedLocalModelOrphansAsync(
        CancellationToken cancellationToken)
    {
        if (_localModelOrphanCollector is null)
        {
            return;
        }

        try
        {
            await _localModelOrphanCollector
                .CollectRetainedOrphansAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The collector owns a durable pending marker. Queue work continues and the next
            // iteration retries without relying on already-cleared workspace pointers.
        }
    }

    private void ApplyEngineProgressSynchronously(
        string jobId,
        string chunkId,
        int completedCount,
        int totalCount,
        TranscriptionProgress progress)
    {
        var status = progress.Stage switch
        {
            TranscriptionProgressStage.Processing or TranscriptionProgressStage.Transcribing =>
                TranscriptionJobStatus.Processing,
            TranscriptionProgressStage.Uploading => TranscriptionJobStatus.Uploading,
            _ => (TranscriptionJobStatus?)null
        };
        if (status is null)
        {
            return;
        }

        var localFraction = progress.Fraction ?? (status == TranscriptionJobStatus.Uploading ? 0.2 : 0.7);
        try
        {
            var changed = _repository.TrySetExecutionStateAsync(
                    jobId,
                    status.Value,
                    CalculateProgress(completedCount, totalCount, localFraction),
                    chunkId,
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (changed)
            {
                OnStateChanged();
            }
        }
        catch
        {
            // Persistence or cancellation will be observed immediately after the engine call.
        }
    }

    private async ValueTask<AudioSourceFingerprint> ValidateSourceAsync(
        TranscriptionJobRecord job,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(job.InputAudioPath);
        if (info.Length != job.InputSizeBytes)
        {
            await PauseForAttentionAsync(
                    job.Id,
                    "input_changed",
                    "The source recording changed after this job was queued.",
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException("The transcription input size changed.");
        }

        await using var stream = new FileStream(
            job.InputAudioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (!string.Equals(hash, job.InputSha256, StringComparison.Ordinal))
        {
            await PauseForAttentionAsync(
                    job.Id,
                    "input_changed",
                    "The source recording changed after this job was queued.",
                    currentChunkId: null,
                    engineRequestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException("The transcription input hash changed.");
        }

        var duration = job.InputDurationSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidDataException("The transcription input duration is unavailable.");
        return new AudioSourceFingerprint(
            hash,
            info.Length,
            duration,
            Path.GetExtension(job.InputAudioPath).TrimStart('.'))
            .Validate();
    }

    private async ValueTask PauseForAttentionAsync(
        string jobId,
        string code,
        string message,
        string? currentChunkId,
        string? engineRequestId,
        CancellationToken cancellationToken)
    {
        await _repository.RequireAttentionAsync(
                jobId,
                SanitizeCode(code),
                SanitizeMessage(message),
                currentChunkId,
                SanitizeRequestId(engineRequestId),
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        OnStateChanged();
    }

    private async ValueTask SaveManifestAsync(
        TranscriptionJobRecord job,
        AudioChunkManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(job.ManifestPath)
            ? Path.Combine(GetJobDirectory(job.Id), "manifest.json")
            : job.ManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async ValueTask WaitForWorkAsync(CancellationToken cancellationToken)
    {
        _ = await _wake.WaitAsync(_idleDelay, cancellationToken).ConfigureAwait(false);
    }

    private string GetJobDirectory(string jobId) => Path.Combine(_workerRoot, NormalizeJobId(jobId));

    private static string NormalizeJobId(string jobId)
    {
        if (!Guid.TryParseExact(jobId, "N", out var parsed))
        {
            throw new InvalidDataException("The transcription job identity is invalid.");
        }

        return parsed.ToString("N");
    }

    private static AudioChunkDescriptor ToDescriptor(
        AudioSourceFingerprint source,
        TranscriptionChunkRecord chunk)
    {
        var start = TimeSpan.FromMilliseconds(chunk.StartMilliseconds);
        var end = TimeSpan.FromMilliseconds(chunk.EndMilliseconds);
        var estimatedBytes = Math.Min(
            source.SizeBytes,
            checked((long)Math.Ceiling(
                source.SizeBytes * ((end - start).TotalMilliseconds / source.Duration.TotalMilliseconds))));
        return new AudioChunkDescriptor(
            chunk.Id,
            chunk.SequenceIndex,
            start,
            end,
            TimeSpan.FromMilliseconds(chunk.OverlapMilliseconds),
            estimatedBytes,
            chunk.ParentChunkId,
            chunk.SplitDepth);
    }

    private static TranscriptionChunkDefinition ToDefinition(AudioChunkDescriptor chunk) => new(
        chunk.Id,
        chunk.SequenceIndex,
        checked((long)Math.Round(chunk.Start.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        checked((long)Math.Round(chunk.End.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        checked((long)Math.Round(chunk.Overlap.TotalMilliseconds, MidpointRounding.AwayFromZero)),
        chunk.ParentChunkId,
        chunk.SplitDepth);

    private static double CalculateProgress(int completedCount, int totalCount, double localFraction) =>
        totalCount <= 0
            ? 0
            : Math.Clamp((completedCount + Math.Clamp(localFraction, 0, 0.99)) / totalCount, 0, 0.99);

    private static string SanitizeCode(string code)
    {
        if (LooksSensitive(code))
        {
            return "transcription_error";
        }

        var normalized = new string((code ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(static character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-' or '.')
            .Take(96)
            .ToArray());
        return normalized.Length == 0 ? "transcription_error" : normalized;
    }

    private static string SanitizeMessage(string? message)
    {
        var normalized = string.Join(' ', (message ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length switch
        {
            0 => "Transcription requires attention.",
            > 512 => normalized[..512],
            _ => normalized
        };
    }

    private static string? SanitizeRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var normalized = requestId.Trim();
        return normalized.Length <= 128
               && !LooksSensitive(normalized)
               && normalized.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static string SafeEngineMessage(TranscriptionError error) => error.Category switch
    {
        TranscriptionErrorCategory.Authentication =>
            "The provider rejected the saved API key. Update the key and retry.",
        TranscriptionErrorCategory.PaymentRequired =>
            "The provider account needs available credit before retrying.",
        TranscriptionErrorCategory.PayloadTooLarge =>
            "The audio chunk exceeded the provider upload limit.",
        TranscriptionErrorCategory.RateLimited =>
            "The provider asked the app to wait before retrying.",
        TranscriptionErrorCategory.Network =>
            "The provider request could not be completed. The app will retry safely.",
        TranscriptionErrorCategory.EngineUnavailable =>
            "The transcription provider is temporarily unavailable.",
        TranscriptionErrorCategory.Configuration =>
            "Review the provider model, key and privacy settings before retrying.",
        TranscriptionErrorCategory.UnsupportedInput =>
            "The provider cannot process this recording format.",
        TranscriptionErrorCategory.ResourceUnavailable =>
            "The transcription engine needs an available local resource before retrying.",
        TranscriptionErrorCategory.InvalidRequest =>
            "The saved transcription request needs a settings review.",
        _ => "The provider could not complete this audio chunk. Review the job before retrying."
    };

    private static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
            || value.Contains("sk-or-v1-", StringComparison.OrdinalIgnoreCase)
            || value.Length > 512;
    }

    private static bool? ReadRequireZeroDataRetention(string? engineOptionsJson)
    {
        if (string.IsNullOrWhiteSpace(engineOptionsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(engineOptionsJson);
            return document.RootElement.TryGetProperty("requireZeroDataRetention", out var value)
                   && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnStateChanged()
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private enum ChunkProcessingOutcome
    {
        Continue,
        Paused
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> _report = report;

        public void Report(T value) => _report(value);
    }

    private sealed record PublicationMetadata(string? DetectedLanguage, TranscriptionUsage? Usage);
}
