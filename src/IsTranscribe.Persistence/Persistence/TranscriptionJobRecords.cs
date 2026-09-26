using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Host.Persistence;

/// <summary>
/// Durable provider-neutral lifecycle states shared by remote and local transcription engines.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public enum TranscriptionJobStatus
{
    Queued,
    Preparing,
    Uploading,
    Processing,
    Finalizing,
    RetryScheduled,
    AttentionRequired,
    Completed,
    Cancelled,
    Failed
}

public enum TranscriptionChunkStatus
{
    Pending,
    Preparing,
    Uploading,
    Processing,
    Completed,
    Split,
    Cancelled,
    Failed
}

public enum TranscriptionArtifactPublicationState
{
    None,
    Staged,
    Promoted
}

/// <summary>
/// Records whether the user started a job directly or opted in for post-finalization enqueue.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// </remarks>
public enum TranscriptionTriggerKind
{
    Manual,
    Automatic
}

/// <summary>
/// Stable local execution backends persisted with the frozen run identity.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public enum LocalTranscriptionBackend
{
    Auto,
    Cpu,
    Metal,
    Vulkan
}

/// <summary>
/// Durable lifecycle of one isolated local worker attempt. Running is represented by an open
/// database row; every other value is a terminal worker outcome.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public enum LocalTranscriptionAttemptStatus
{
    Running,
    Completed,
    Cancelled,
    Preempted,
    Crashed,
    OutOfMemory,
    Timeout,
    DecodeFailure,
    NativeFailure,
    ProtocolFailure
}

/// <summary>
/// Stable engine identities whose execution and consent class is persisted with every job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public static class TranscriptionEngineIds
{
    public const string Groq = "remote.groq";

    public const string OpenRouter = "remote.openrouter";

    public const string LocalWhisper = "local.whisper";

    public static TranscriptionExecutionKind GetRequiredExecutionKind(string engineId) =>
        engineId switch
        {
            Groq or OpenRouter => TranscriptionExecutionKind.Remote,
            LocalWhisper => TranscriptionExecutionKind.Local,
            _ => throw new ArgumentException(
                $"Unknown transcription engine id '{engineId}'.",
                nameof(engineId))
        };
}

/// <summary>
/// Immutable execution identity and input fingerprint captured when a job enters the queue.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </remarks>
public sealed record TranscriptionJobEnqueueRequest(
    string JobId,
    string SessionId,
    string EngineId,
    TranscriptionExecutionKind ExecutionKind,
    string ModelId,
    string InputAudioPath,
    string InputSha256,
    long InputSizeBytes,
    DateTimeOffset QueuedAtUtc,
    double? InputDurationSeconds = null,
    string? EngineOptionsJson = null,
    string? RequestedLanguage = null,
    string? RemoteConsentRevision = null,
    DateTimeOffset? RemoteConsentAtUtc = null,
    string? PrivacyPolicyJson = null,
    int ManifestVersion = 1,
    string? ManifestPath = null,
    bool ReplaceExisting = false,
    TranscriptionTriggerKind TriggerKind = TranscriptionTriggerKind.Manual,
    string? SupersedesJobId = null,
    LocalTranscriptionExecutionIdentity? LocalExecution = null);

public sealed record TranscriptionEnqueueResult(
    TranscriptionJobRecord Job,
    bool Created);

public sealed record TranscriptionJobRecord(
    string Id,
    string SessionId,
    string EngineId,
    TranscriptionExecutionKind ExecutionKind,
    string ModelId,
    string? EngineOptionsJson,
    string? RequestedLanguage,
    string? DetectedLanguage,
    string InputAudioPath,
    string InputSha256,
    long InputSizeBytes,
    double? InputDurationSeconds,
    TranscriptionJobStatus Status,
    double Progress,
    int? CurrentChunkIndex,
    string? CurrentChunkId,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? StableErrorCode,
    string? ErrorMessage,
    string? TranscriptMarkdownPath,
    string? TranscriptJsonPath,
    string? UsageJson,
    string? RemoteConsentRevision,
    DateTimeOffset? RemoteConsentAtUtc,
    string? PrivacyPolicyJson,
    int ManifestVersion,
    string? ManifestPath,
    TranscriptionArtifactPublicationState ArtifactPublicationState,
    string? StagedTranscriptMarkdownPath,
    string? StagedTranscriptJsonPath,
    string? StagedTranscriptMarkdownSha256,
    string? StagedTranscriptJsonSha256,
    bool ReplaceExisting,
    bool CancellationRequested,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    TranscriptionTriggerKind TriggerKind = TranscriptionTriggerKind.Manual,
    string? SupersedesJobId = null);

/// <summary>
/// Immutable model/runtime identity captured atomically with a local job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed record LocalTranscriptionExecutionIdentity(
    int ModelCatalogVersion,
    string ModelCatalogRevision,
    string ModelFormat,
    string ModelPath,
    long ModelSizeBytes,
    string ModelSha256,
    string RuntimeVersion,
    string RuntimeCommit,
    string RuntimeSourceArchiveSha256,
    string NativeBundleManifestSha256,
    int BridgeAbiVersion,
    int WorkerProtocolVersion,
    LocalTranscriptionBackend RequestedBackend,
    LocalTranscriptionBackend? ResolvedBackend,
    int ThreadCount,
    string InferenceParametersJson,
    int ChunkProfileVersion,
    string RunIdentitySha256,
    string? BackendHistoryJson = null,
    string? PolicyDeferReason = null);

public sealed record LocalTranscriptionJobRecord(
    string JobId,
    LocalTranscriptionExecutionIdentity Execution,
    int NativeCrashCount,
    LocalTranscriptionBackend? LastCrashBackend,
    long? ProcessingDurationMilliseconds,
    long? PeakWorkingSetBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Compare-and-set identity written immediately before one worker begins processing a chunk.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed record LocalTranscriptionChunkAttemptStart(
    string JobId,
    string ChunkId,
    int AttemptIndex,
    LocalTranscriptionBackend RequestedBackend,
    DateTimeOffset WorkerStartedAtUtc);

public sealed record LocalTranscriptionChunkAttemptStartResult(
    LocalTranscriptionChunkAttemptRecord Attempt,
    bool Created);

/// <summary>
/// Terminal technical telemetry for the exact open attempt identified by its start timestamp.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed record LocalTranscriptionChunkAttemptCompletion(
    string JobId,
    string ChunkId,
    int AttemptIndex,
    DateTimeOffset WorkerStartedAtUtc,
    DateTimeOffset WorkerEndedAtUtc,
    LocalTranscriptionAttemptStatus Status,
    LocalTranscriptionBackend? ResolvedBackend = null,
    long? DecodeDurationMilliseconds = null,
    long? InferenceDurationMilliseconds = null,
    long? PeakWorkingSetBytes = null,
    string? StableFailureCategory = null);

public sealed record LocalTranscriptionChunkAttemptRecord(
    string JobId,
    string ChunkId,
    int AttemptIndex,
    LocalTranscriptionBackend RequestedBackend,
    LocalTranscriptionBackend? ResolvedBackend,
    DateTimeOffset WorkerStartedAtUtc,
    DateTimeOffset? WorkerEndedAtUtc,
    LocalTranscriptionAttemptStatus Status,
    long? DecodeDurationMilliseconds,
    long? InferenceDurationMilliseconds,
    long? PeakWorkingSetBytes,
    string? StableFailureCategory,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Deterministic chunk identity and optional materialized input artifact.
/// </summary>
/// <remarks>
/// Nullable artifact fields allow a local engine to decode the requested time range directly
/// from the source recording.
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record TranscriptionChunkDefinition(
    string Id,
    int SequenceIndex,
    long StartMilliseconds,
    long EndMilliseconds,
    long OverlapMilliseconds,
    string? ParentChunkId = null,
    int SplitDepth = 0,
    TranscriptionChunkStatus InitialStatus = TranscriptionChunkStatus.Pending,
    string? ArtifactPath = null,
    string? ArtifactFormat = null,
    string? ArtifactSha256 = null,
    long? ArtifactSizeBytes = null);

/// <summary>
/// Atomic replacement of one provider-rejected chunk by its two deterministic children.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// </remarks>
public sealed record TranscriptionChunkSplitRequest(
    string JobId,
    string ParentChunkId,
    IReadOnlyList<TranscriptionChunkDefinition> Children,
    DateTimeOffset SplitAtUtc);

public sealed record TranscriptionChunkRecord(
    string Id,
    string JobId,
    int SequenceIndex,
    string? ParentChunkId,
    int SplitDepth,
    long StartMilliseconds,
    long EndMilliseconds,
    long OverlapMilliseconds,
    string? ArtifactPath,
    string? ArtifactFormat,
    string? ArtifactSha256,
    long? ArtifactSizeBytes,
    TranscriptionChunkStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? EngineRequestId,
    string? ResultPath,
    string? ResultSha256,
    string? ResultMetadataJson,
    string? UsageJson,
    string? StableErrorCode,
    string? ErrorMessage);

public sealed record TranscriptionChunkCompletion(
    string JobId,
    string ChunkId,
    string ResultPath,
    string ResultSha256,
    DateTimeOffset CompletedAtUtc,
    string? EngineRequestId = null,
    string? ResultMetadataJson = null,
    string? UsageJson = null);

public sealed record TranscriptionArtifactStage(
    string JobId,
    string StagedMarkdownPath,
    string StagedJsonPath,
    string FinalMarkdownPath,
    string FinalJsonPath,
    string StagedMarkdownSha256,
    string StagedJsonSha256,
    DateTimeOffset StagedAtUtc);

public sealed record TranscriptionArtifactPublication(
    string JobId,
    string FinalMarkdownPath,
    string FinalJsonPath,
    DateTimeOffset PublishedAtUtc,
    string? DetectedLanguage = null,
    string? UsageJson = null);

public sealed record TranscriptionStartupRecoveryResult(
    int RequeuedJobCount,
    int ResetChunkCount);
