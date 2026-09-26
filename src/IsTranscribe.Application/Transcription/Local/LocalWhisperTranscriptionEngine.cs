using System.Security.Cryptography;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Provider-neutral local engine adapter. Every chunk runs in a short-lived private worker and all
/// technical attempt outcomes are checkpointed without transcript/audio content.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class LocalWhisperTranscriptionEngine : ITranscriptionEngine
{
    public const string EngineId = "local.whisper";
    private const long MaximumChunkMilliseconds = 5 * 60 * 1000;
    private const long RequiredWorkingDiskBytes = 128L * 1024 * 1024;

    private readonly TranscriptionJobRepository _repository;
    private readonly LocalTranscriptionServices _services;
    private readonly LocalTranscriptionRuntimeOptions _options;
    private readonly ILocalWorkerClientFactory _workerClients;
    private readonly TimeProvider _timeProvider;
    private readonly object _preemptionSync = new();
    private readonly HashSet<string> _preemptedJobIds = new(StringComparer.Ordinal);

    public LocalWhisperTranscriptionEngine(
        TranscriptionJobRepository repository,
        LocalTranscriptionServices services,
        LocalTranscriptionRuntimeOptions options,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _workerClients = options.WorkerClientFactory ?? new CoordinatorLocalWorkerClientFactory();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Capabilities = BuildCapabilities(services.Catalog);
    }

    public TranscriptionEngineCapabilities Capabilities { get; }

    internal void RequestPreemption(string jobId)
    {
        lock (_preemptionSync)
        {
            _preemptedJobIds.Add(jobId);
        }
    }

    internal void ClearPreemption(string jobId)
    {
        lock (_preemptionSync)
        {
            _preemptedJobIds.Remove(jobId);
        }
    }

    public async ValueTask<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.JobId is not { } jobGuid || request.ChunkId is null)
        {
            return Fail(
                TranscriptionErrorCategory.InvalidRequest,
                "local_identity_missing",
                "A durable local job and chunk identity are required.");
        }

        var jobId = jobGuid.ToString("N");
        var job = await _repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        var local = await _repository.GetLocalExecutionAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || local is null
            || job.ExecutionKind != TranscriptionExecutionKind.Local
            || !string.Equals(job.EngineId, EngineId, StringComparison.Ordinal)
            || !string.Equals(job.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            return Fail(
                TranscriptionErrorCategory.Configuration,
                "local_identity_invalid",
                "The frozen local transcription identity is unavailable.");
        }

        if (!File.Exists(local.Execution.ModelPath))
        {
            return Fail(
                TranscriptionErrorCategory.ResourceUnavailable,
                "model_missing",
                "The local transcription model is unavailable.",
                TranscriptionFailureDisposition.AttentionRequired);
        }

        var modelInfo = new FileInfo(local.Execution.ModelPath);
        if (modelInfo.Length != local.Execution.ModelSizeBytes)
        {
            return Fail(
                TranscriptionErrorCategory.ResourceUnavailable,
                "model_corrupt",
                "The local transcription model no longer matches its verified identity.",
                TranscriptionFailureDisposition.AttentionRequired);
        }

        if (!await VerifyModelIntegrityAsync(job.Id, local.Execution, modelInfo, cancellationToken)
            .ConfigureAwait(false))
        {
            return Fail(
                TranscriptionErrorCategory.ResourceUnavailable,
                "model_corrupt",
                "The local transcription model no longer matches its verified checksum.",
                TranscriptionFailureDisposition.AttentionRequired);
        }

        if (local.NativeCrashCount >= 2)
        {
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "native_crash",
                "The isolated local runtime failed repeatedly and requires review.",
                TranscriptionFailureDisposition.AttentionRequired);
        }

        var descriptor = _services.Catalog.GetRequiredForFrozenRun(job.ModelId);
        var hasPowerOverride = job.TriggerKind == TranscriptionTriggerKind.Manual
            && _services.PowerOverrides.Consume(job.Id);
        var assessment = await _services.ResourcePolicy.AssessAsync(
                new LocalTranscriptionResourceRequest(
                    job.Id,
                    job.SessionId,
                    job.TriggerKind,
                    descriptor.ExpectedMemoryBytes,
                    RequiredWorkingDiskBytes,
                    hasPowerOverride),
                cancellationToken)
            .ConfigureAwait(false);
        var resourceFailure = ValidateResourceAssessment(
            assessment,
            descriptor.ExpectedMemoryBytes,
            RequiredWorkingDiskBytes,
            job.TriggerKind,
            hasPowerOverride);
        if (resourceFailure is not null)
        {
            return TranscriptionResult.Failed(resourceFailure);
        }

        var sourceStart = request.SourceStart ?? TimeSpan.Zero;
        var sourceEnd = request.SourceEnd;
        if (sourceEnd is null
            || sourceEnd <= sourceStart
            || sourceEnd.Value - sourceStart > TimeSpan.FromMilliseconds(MaximumChunkMilliseconds))
        {
            return Fail(
                TranscriptionErrorCategory.InvalidRequest,
                "chunk_duration_invalid",
                "A local transcription chunk must be bounded to five minutes.");
        }

        var chunks = await _repository.ListChunksAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var chunk = chunks.SingleOrDefault(candidate => string.Equals(
            candidate.Id,
            request.ChunkId,
            StringComparison.Ordinal));
        if (chunk is null)
        {
            return Fail(
                TranscriptionErrorCategory.InvalidRequest,
                "chunk_identity_invalid",
                "The durable local transcription chunk is unavailable.");
        }

        var attempts = await _repository.ListLocalChunkAttemptsAsync(job.Id, cancellationToken)
            .ConfigureAwait(false);
        var chunkAttempts = attempts
            .Where(candidate => string.Equals(candidate.ChunkId, chunk.Id, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.AttemptIndex)
            .ToArray();
        var stale = chunkAttempts.LastOrDefault(static attempt =>
            attempt.Status == LocalTranscriptionAttemptStatus.Running);
        if (stale is not null)
        {
            var interruptedAt = _timeProvider.GetUtcNow();
            if (interruptedAt < stale.WorkerStartedAtUtc)
            {
                interruptedAt = stale.WorkerStartedAtUtc;
            }

            await _repository.TryCompleteLocalChunkAttemptAsync(
                    new LocalTranscriptionChunkAttemptCompletion(
                        stale.JobId,
                        stale.ChunkId,
                        stale.AttemptIndex,
                        stale.WorkerStartedAtUtc,
                        interruptedAt,
                        LocalTranscriptionAttemptStatus.Cancelled,
                        ResolvedBackend: null,
                        StableFailureCategory: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var attemptIndex = chunkAttempts.Length == 0
            ? 0
            : checked(chunkAttempts.Max(static attempt => attempt.AttemptIndex) + 1);
        var requestedBackend = local.NativeCrashCount > 0
            && local.Execution.RequestedBackend != LocalTranscriptionBackend.Cpu
                ? LocalTranscriptionBackend.Cpu
                : local.Execution.RequestedBackend;
        var workerStartedAt = _timeProvider.GetUtcNow();
        var attempt = await _repository.StartLocalChunkAttemptAsync(
                new LocalTranscriptionChunkAttemptStart(
                    job.Id,
                    chunk.Id,
                    attemptIndex,
                    requestedBackend,
                    workerStartedAt),
                cancellationToken)
            .ConfigureAwait(false);
        long peakWorkingSet = 0;
        try
        {
            var inputSha256 = await HashFileAsync(request.PrimaryAudioArtifactPath, cancellationToken)
                .ConfigureAwait(false);
            await using var client = _workerClients.Create();
            var started = await client.StartAsync(
                    Path.GetFullPath(_options.WorkerExecutablePath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(started.State, "initialized", StringComparison.Ordinal)
                || !string.Equals(
                    started.WorkerVersion,
                    LocalWorkerRuntimeContract.CurrentWorkerVersion,
                    StringComparison.Ordinal))
            {
                throw new WorkerProtocolException(
                    WorkerProtocolError.InvalidSchema,
                    "The local worker readiness identity is invalid.");
            }

            var workerProgress = new InlineWorkerProgress(update =>
            {
                peakWorkingSet = Math.Max(peakWorkingSet, Math.Max(0, update.WorkingSetBytes));
                progress?.Report(ToProgress(update));
            });
            var result = await client.TranscribeAsync(
                    job.Id,
                    chunk.SequenceIndex,
                    new WorkerStartPayload(
                        Path.GetFullPath(request.PrimaryAudioArtifactPath),
                        inputSha256,
                        local.Execution.ModelPath,
                        local.Execution.ModelSha256,
                        request.Language ?? "auto",
                        ToWorkerBackend(requestedBackend),
                        checked((long)Math.Round(sourceStart.TotalMilliseconds)),
                        checked((long)Math.Round(sourceEnd.Value.TotalMilliseconds)),
                        local.Execution.ThreadCount),
                    workerProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateWorkerResult(
                result,
                local.Execution,
                requestedBackend,
                sourceEnd.Value - sourceStart);
            var resolvedBackend = ParseWorkerBackend(result.Backend);
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.Completed,
                    resolvedBackend,
                    result.ProcessingMilliseconds,
                    peakWorkingSet,
                    stableFailureCategory: null)
                .ConfigureAwait(false);
            await _services.RefreshDiagnosticsAsync(job.Id, _repository).ConfigureAwait(false);
            var segments = result.Segments.Select(segment => new TranscriptionSegment(
                    segment.Text,
                    TimeSpan.FromMilliseconds(segment.StartMilliseconds),
                    TimeSpan.FromMilliseconds(segment.EndMilliseconds),
                    words: [new TranscriptionWord(segment.Text,
                        TimeSpan.FromMilliseconds(segment.StartMilliseconds),
                        TimeSpan.FromMilliseconds(segment.EndMilliseconds))]))
                .ToArray();
            return TranscriptionResult.Completed(
                string.Join(' ', segments.Select(static segment => segment.Text)).Trim(),
                result.Language,
                segments,
                new TranscriptionResultMetadata(
                    ResolvedModelId: job.ModelId,
                    SourceStart: sourceStart,
                    SourceEnd: sourceEnd,
                    AudioDuration: sourceEnd - sourceStart));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var wasPreempted = ConsumePreemption(job.Id);
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    wasPreempted
                        ? LocalTranscriptionAttemptStatus.Preempted
                        : LocalTranscriptionAttemptStatus.Cancelled,
                    resolvedBackend: null,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    stableFailureCategory: null)
                .ConfigureAwait(false);
            throw;
        }
        catch (WorkerRemoteFailureException exception)
        {
            var failure = exception.Failure;
            var status = MapAttemptStatus(failure);
            var backend = TryParseWorkerBackend(failure.Backend);
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    status,
                    backend,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    StableToken(failure.StableCode))
                .ConfigureAwait(false);
            await _services.RefreshDiagnosticsAsync(job.Id, _repository).ConfigureAwait(false);
            return TranscriptionResult.Failed(MapWorkerFailure(failure));
        }
        catch (TimeoutException)
        {
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.Timeout,
                    resolvedBackend: null,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    "worker_timeout")
                .ConfigureAwait(false);
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "worker_timeout",
                "The isolated local worker exceeded its bounded execution time.",
                TranscriptionFailureDisposition.TryAgain);
        }
        catch (WorkerProtocolException)
        {
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.ProtocolFailure,
                    resolvedBackend: null,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    "worker_protocol_failure")
                .ConfigureAwait(false);
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "worker_protocol_failure",
                "The isolated local worker protocol failed validation.",
                TranscriptionFailureDisposition.TryAgain);
        }
        catch (Exception exception) when (exception is WorkerProcessExitedException or IOException)
        {
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.Crashed,
                    resolvedBackend: requestedBackend == LocalTranscriptionBackend.Auto
                        ? null
                        : requestedBackend,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    "native_crash")
                .ConfigureAwait(false);
            await _services.RefreshDiagnosticsAsync(job.Id, _repository).ConfigureAwait(false);
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "native_crash",
                "The isolated local runtime stopped unexpectedly.",
                TranscriptionFailureDisposition.TryAgain);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.Crashed,
                    resolvedBackend: null,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    "backend_unavailable")
                .ConfigureAwait(false);
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "backend_unavailable",
                "The packaged local transcription worker could not be started.",
                TranscriptionFailureDisposition.AttentionRequired);
        }
        catch (Exception)
        {
            await CompleteAttemptAsync(
                    attempt.Attempt,
                    LocalTranscriptionAttemptStatus.NativeFailure,
                    resolvedBackend: null,
                    inferenceDurationMilliseconds: null,
                    peakWorkingSet,
                    "native_failure")
                .ConfigureAwait(false);
            return Fail(
                TranscriptionErrorCategory.EngineUnavailable,
                "native_failure",
                "The isolated local runtime failed while processing this chunk.",
                TranscriptionFailureDisposition.TryAgain);
        }
    }

    internal static string ToWorkerBackend(LocalTranscriptionBackend backend) => backend switch
    {
        LocalTranscriptionBackend.Auto => "auto",
        LocalTranscriptionBackend.Cpu => "cpu",
        LocalTranscriptionBackend.Metal => "metal",
        LocalTranscriptionBackend.Vulkan => "vulkan",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static TranscriptionEngineCapabilities BuildCapabilities(WhisperModelCatalog catalog) => new(
        EngineId,
        "On-device Whisper",
        TranscriptionExecutionKind.Local,
        RequiresNetwork: false,
        PrivacyDisclosure: "Audio and transcript text stay on this device during transcription.",
        SupportedPlatforms:
        [
            new TranscriptionPlatformTarget(TranscriptionOs.Windows, System.Runtime.InteropServices.Architecture.X64),
            new TranscriptionPlatformTarget(TranscriptionOs.MacOS, System.Runtime.InteropServices.Architecture.Arm64),
        ],
        Models: catalog.Models.Select(model => new TranscriptionModelCapability(
                model.Id,
                model.Id == "large-v3-turbo" ? "Whisper large v3 turbo" : model.UiPreset switch
                {
                    "compact" => "Compact (base)",
                    "balanced" => "Balanced (small)",
                    "accurate" => "Accurate (medium)",
                    _ => model.Id,
                },
                model.Recommended))
            .ToArray(),
        SupportedLanguageCodes: [],
        SupportsAutomaticLanguageDetection: true,
        SupportsDiarization: false,
        TimestampCapabilities: TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word,
        MinimumResources: new TranscriptionResourceRequirements(
            MinimumSystemMemoryBytes: catalog.Models.Min(static model => model.ExpectedMemoryBytes),
            MinimumFreeDiskBytes: RequiredWorkingDiskBytes));

    private static TranscriptionError? ValidateResourceAssessment(
        LocalTranscriptionResourceAssessment assessment,
        long requiredMemory,
        long requiredDisk,
        TranscriptionTriggerKind trigger,
        bool hasOverride)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (assessment.State.InstalledMemoryBytes is null
            || assessment.State.AvailableMemoryBytes is null
            || assessment.State.AvailableDiskBytes is null)
        {
            return new TranscriptionError(
                TranscriptionErrorCategory.ResourceUnavailable,
                "resource_state_unavailable",
                "Installed memory, available memory and disk availability could not be verified for local transcription.",
                disposition: TranscriptionFailureDisposition.AttentionRequired);
        }

        if (assessment.State.InstalledMemoryBytes is { } installedMemory
            && installedMemory < requiredMemory)
        {
            return new TranscriptionError(
                TranscriptionErrorCategory.ResourceUnavailable,
                "insufficient_memory",
                "Installed memory is below the selected local model requirement.",
                disposition: TranscriptionFailureDisposition.AttentionRequired);
        }

        if (assessment.State.AvailableMemoryBytes is { } availableMemory
            && availableMemory < requiredMemory)
        {
            return new TranscriptionError(
                TranscriptionErrorCategory.ResourceUnavailable,
                "insufficient_memory",
                "Available memory is below the selected local model requirement.",
                disposition: TranscriptionFailureDisposition.AttentionRequired);
        }

        if (assessment.State.AvailableDiskBytes is { } availableDisk && availableDisk < requiredDisk)
        {
            return new TranscriptionError(
                TranscriptionErrorCategory.ResourceUnavailable,
                "insufficient_disk",
                "Available disk space is below the local transcription working requirement.",
                disposition: TranscriptionFailureDisposition.AttentionRequired);
        }

        if (assessment.Disposition == LocalTranscriptionResourceDisposition.Allowed)
        {
            return null;
        }

        var code = StableToken(assessment.StableCode ?? assessment.State.StableBlockCode ?? "resource_unavailable");
        var message = string.IsNullOrWhiteSpace(assessment.SafeMessage)
            ? "Local transcription is waiting for device resources."
            : assessment.SafeMessage;
        if (assessment.Disposition == LocalTranscriptionResourceDisposition.Deferred
            && trigger == TranscriptionTriggerKind.Automatic)
        {
            return new TranscriptionError(
                TranscriptionErrorCategory.ResourceUnavailable,
                code,
                message,
                suggestedDelay: TimeSpan.FromMinutes(5),
                disposition: TranscriptionFailureDisposition.TryAgain);
        }

        if (assessment.CanUseOneShotManualOverride && !hasOverride)
        {
            code = "low_power_override_required";
        }

        return new TranscriptionError(
            TranscriptionErrorCategory.ResourceUnavailable,
            code,
            message,
            disposition: TranscriptionFailureDisposition.AttentionRequired);
    }

    private async ValueTask CompleteAttemptAsync(
        LocalTranscriptionChunkAttemptRecord attempt,
        LocalTranscriptionAttemptStatus status,
        LocalTranscriptionBackend? resolvedBackend,
        long? inferenceDurationMilliseconds,
        long peakWorkingSet,
        string? stableFailureCategory)
    {
        var endedAt = _timeProvider.GetUtcNow();
        if (endedAt < attempt.WorkerStartedAtUtc)
        {
            endedAt = attempt.WorkerStartedAtUtc;
        }

        await _repository.TryCompleteLocalChunkAttemptAsync(
                new LocalTranscriptionChunkAttemptCompletion(
                    attempt.JobId,
                    attempt.ChunkId,
                    attempt.AttemptIndex,
                    attempt.WorkerStartedAtUtc,
                    endedAt,
                    status,
                    resolvedBackend,
                    DecodeDurationMilliseconds: null,
                    InferenceDurationMilliseconds: inferenceDurationMilliseconds,
                    PeakWorkingSetBytes: peakWorkingSet > 0 ? peakWorkingSet : null,
                    StableFailureCategory: stableFailureCategory),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static void ValidateWorkerResult(
        WorkerResultPayload result,
        LocalTranscriptionExecutionIdentity identity,
        LocalTranscriptionBackend requestedBackend,
        TimeSpan duration)
    {
        if (!string.Equals(result.ModelSha256, identity.ModelSha256, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result.RuntimeVersion)
            || !RuntimeMatches(result.RuntimeVersion, identity.RuntimeVersion)
            || !IsAllowedResolvedBackend(requestedBackend, result.Backend)
            || result.ProcessingMilliseconds < 0)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidSchema,
                "The local worker result identity does not match the frozen run.");
        }

        var durationMilliseconds = checked((long)Math.Round(duration.TotalMilliseconds));
        foreach (var segment in result.Segments)
        {
            if (segment.StartMilliseconds < 0
                || segment.EndMilliseconds < segment.StartMilliseconds
                || segment.EndMilliseconds > durationMilliseconds
                || string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new WorkerProtocolException(
                    WorkerProtocolError.InvalidSchema,
                    "The local worker returned an invalid segment range.");
            }
        }
    }

    private static bool RuntimeMatches(string actual, string expected)
    {
        static string Normalize(string value) => value.Trim()
            .Replace("whisper.cpp-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimStart('v');
        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);
    }

    private static bool IsAllowedResolvedBackend(LocalTranscriptionBackend requested, string? resolved) =>
        requested switch
        {
            LocalTranscriptionBackend.Auto => resolved is "cpu" or "metal" or "vulkan",
            LocalTranscriptionBackend.Cpu => resolved is "cpu",
            LocalTranscriptionBackend.Metal => resolved is "metal" or "cpu",
            LocalTranscriptionBackend.Vulkan => resolved is "vulkan" or "cpu",
            _ => false,
        };

    private static LocalTranscriptionBackend ParseWorkerBackend(string value) => value switch
    {
        "cpu" => LocalTranscriptionBackend.Cpu,
        "metal" => LocalTranscriptionBackend.Metal,
        "vulkan" => LocalTranscriptionBackend.Vulkan,
        _ => throw new WorkerProtocolException(
            WorkerProtocolError.InvalidSchema,
            "The local worker returned an unknown backend."),
    };

    private static LocalTranscriptionBackend? TryParseWorkerBackend(string? value) => value switch
    {
        "cpu" => LocalTranscriptionBackend.Cpu,
        "metal" => LocalTranscriptionBackend.Metal,
        "vulkan" => LocalTranscriptionBackend.Vulkan,
        _ => null,
    };

    private static LocalTranscriptionAttemptStatus MapAttemptStatus(WorkerFailurePayload failure)
    {
        var category = failure.Category.ToLowerInvariant();
        var code = failure.StableCode.ToLowerInvariant();
        if (category.Contains("memory", StringComparison.Ordinal)
            || code.Contains("memory", StringComparison.Ordinal))
        {
            return LocalTranscriptionAttemptStatus.OutOfMemory;
        }

        if (category.Contains("decode", StringComparison.Ordinal)
            || code.Contains("wav", StringComparison.Ordinal)
            || code.Contains("audio", StringComparison.Ordinal))
        {
            return LocalTranscriptionAttemptStatus.DecodeFailure;
        }

        return LocalTranscriptionAttemptStatus.NativeFailure;
    }

    private static TranscriptionError MapWorkerFailure(WorkerFailurePayload failure)
    {
        var code = StableToken(failure.StableCode);
        var attention = !failure.Retryable
            || code is "model_missing" or "model_corrupt" or "model_load_failed"
            || failure.Category.Contains("memory", StringComparison.OrdinalIgnoreCase);
        var publicCode = code == "model_load_failed" ? "model_corrupt" : code;
        return new TranscriptionError(
            failure.Category.Contains("memory", StringComparison.OrdinalIgnoreCase)
                ? TranscriptionErrorCategory.ResourceUnavailable
                : TranscriptionErrorCategory.EngineUnavailable,
            publicCode,
            string.IsNullOrWhiteSpace(failure.SafeMessage)
                ? "The local transcription runtime could not process this chunk."
                : failure.SafeMessage,
            disposition: attention
                ? TranscriptionFailureDisposition.AttentionRequired
                : TranscriptionFailureDisposition.TryAgain);
    }

    private static TranscriptionProgress ToProgress(WorkerProgressPayload update)
    {
        var stage = update.Stage switch
        {
            "preparing" => TranscriptionProgressStage.Preparing,
            "finalizing" => TranscriptionProgressStage.Finalizing,
            _ => TranscriptionProgressStage.Transcribing,
        };
        double? fraction = update.TotalMilliseconds <= 0
            ? null
            : Math.Clamp((double)update.CompletedMilliseconds / update.TotalMilliseconds, 0, 1);
        return new TranscriptionProgress(stage, fraction);
    }

    private static TranscriptionResult Fail(
        TranscriptionErrorCategory category,
        string code,
        string message,
        TranscriptionFailureDisposition disposition = TranscriptionFailureDisposition.AttentionRequired) =>
        TranscriptionResult.Failed(new TranscriptionError(category, code, message, disposition: disposition));

    private static string StableToken(string value)
    {
        var token = new string(value.Trim().ToLowerInvariant()
            .Select(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '_')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(token) ? "local_worker_failure" : token;
    }

    private static async ValueTask<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private async ValueTask<bool> VerifyModelIntegrityAsync(
        string jobId,
        LocalTranscriptionExecutionIdentity identity,
        FileInfo modelInfo,
        CancellationToken cancellationToken)
    {
        _ = jobId;
        modelInfo.Refresh();
        if (!modelInfo.Exists || modelInfo.Length != identity.ModelSizeBytes)
        {
            return false;
        }

        var actualSha256 = await HashFileAsync(identity.ModelPath, cancellationToken).ConfigureAwait(false);
        modelInfo.Refresh();
        return modelInfo.Exists
            && modelInfo.Length == identity.ModelSizeBytes
            && string.Equals(actualSha256, identity.ModelSha256, StringComparison.Ordinal);
    }

    private bool ConsumePreemption(string jobId)
    {
        lock (_preemptionSync)
        {
            return _preemptedJobIds.Remove(jobId);
        }
    }

    private sealed class InlineWorkerProgress(Action<WorkerProgressPayload> report)
        : IProgress<WorkerProgressPayload>
    {
        public void Report(WorkerProgressPayload value) => report(value);
    }

}
