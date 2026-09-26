using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Runtime;

/// <summary>
/// Platform-neutral state consumed by the Avalonia shell.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed record ApplicationRuntimeSnapshot(
    ApplicationActivityState Activity,
    bool ServiceEnabled,
    string Theme,
    ActiveMeetingSnapshot? ActiveMeeting,
    IReadOnlyList<RecentRecordingSnapshot> RecentRecordings,
    string? AttentionMessage,
    MeetingPromptSnapshot? PendingMeetingPrompt = null,
    RecordingFinalizationSnapshot? RecordingFinalization = null)
{
    public RuntimeCapabilitySnapshot Capability { get; init; } = RuntimeCapabilitySnapshot.Unknown;

    public RuntimeUserSettingsSnapshot UserSettings { get; init; } = RuntimeUserSettingsSnapshot.Initial;

    public IReadOnlyList<RuntimeMicrophoneSnapshot> AvailableMicrophones { get; init; } = [];

    public static ApplicationRuntimeSnapshot Initial { get; } = new(
        ApplicationActivityState.Paused,
        ServiceEnabled: false,
        Theme: "system",
        ActiveMeeting: null,
        RecentRecordings: [],
        AttentionMessage: null,
        PendingMeetingPrompt: null,
        RecordingFinalization: null);
}

/// <summary>
/// Platform-neutral readiness of the capture contour exposed to first-run and diagnostics surfaces.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </remarks>
public sealed record RuntimeCapabilitySnapshot(
    RuntimeCapabilityState State,
    bool SupportsProcessOutputCapture,
    string Summary,
    string? BlockingReason = null)
{
    public RuntimeCapabilityIssue Issue { get; init; }

    public bool HasActiveOutput { get; init; }

    public bool HasActiveMicrophone { get; init; }

    public static RuntimeCapabilitySnapshot Unknown { get; } = new(
        RuntimeCapabilityState.Unknown,
        SupportsProcessOutputCapture: false,
        Summary: string.Empty);
}

public enum RuntimeCapabilityState
{
    Unknown,
    Full,
    Degraded,
    Blocked
}

public enum RuntimeCapabilityIssue
{
    None,
    PlatformBlocked,
    ProcessOutputCaptureUnavailable,
    NoActiveOutput,
    NoActiveMicrophone,
    NoActiveAudioEndpoints,
    ConfiguredMicrophoneUnavailable,
    MicrophoneCaptureUnavailable,
    OutputCaptureUnavailable
}

public sealed record MeetingPromptSnapshot(
    string CandidateId,
    string ProfileId,
    string SourceLabel,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int ConfidenceScore,
    IReadOnlyList<string> DecisionReasons);

public enum MeetingPromptUserAction
{
    Record,
    Skip,
    IgnoreApplication
}

public sealed record ActiveMeetingSnapshot(
    Guid SessionId,
    string SourceLabel,
    DateTimeOffset StartedAtUtc,
    bool HasOutput,
    bool HasMicrophone,
    bool IsPaused)
{
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    public TimeSpan ActiveDuration { get; init; }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    public DateTimeOffset? ActiveDurationMeasuredAtUtc { get; init; }
}

public sealed record RecentRecordingSnapshot(
    Guid SessionId,
    string SourceLabel,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    string? PrimaryAudioPath,
    bool RequiresAttention)
{
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    public string DisplayTitle { get; init; } = SourceLabel;

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    public RecentRecordingState State { get; init; } = RecentRecordingState.Unspecified;

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    public string? LegacyTranscriptMarkdownPath { get; init; }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    public string? LegacyTranscriptJsonPath { get; init; }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
    public RuntimeTranscriptionJobSnapshot? Transcription { get; init; }

    // Canonical artifacts published by the current provider-neutral job. Legacy values remain
    // available through the properties above for rollback and old recordings.
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public string? TranscriptMarkdownPath { get; init; }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public string? TranscriptJsonPath { get; init; }
}

/// <summary>
/// Privacy-bounded status and usage projection for the current transcription job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed record RuntimeTranscriptionJobSnapshot(
    string JobId,
    string EngineId,
    string ModelId,
    RuntimeTranscriptionJobState State,
    double Progress,
    int? CurrentChunkIndex,
    DateTimeOffset? NextAttemptAtUtc,
    string? StableErrorCode,
    string? ErrorMessage,
    TranscriptionUsage? Usage = null)
{
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
    public int ChunkCount { get; init; }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    public string? ProviderRequestId { get; init; }

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    public RuntimeLocalTranscriptionDiagnosticsSnapshot? LocalDiagnostics { get; init; }
}

/// <summary>
/// Privacy-bounded local runtime identity and aggregate timings for diagnostics.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed record RuntimeLocalTranscriptionDiagnosticsSnapshot(
    string RequestedBackend,
    string? ResolvedBackend,
    int ThreadCount,
    string RuntimeVersion,
    string NativeBundleManifestSha256,
    string ModelSha256,
    long? ProcessingDurationMilliseconds);

public enum RuntimeTranscriptionJobState
{
    NotStarted,
    Queued,
    Preparing,
    Uploading,
    Processing,
    Completed,
    RetryScheduled,
    AttentionRequired,
    Cancelled,
    Failed
}

public enum RecentRecordingState
{
    Unspecified,
    Recording,
    Processing,
    Ready,
    AttentionRequired
}

/// <summary>
/// Recoverable recording-artifact state surfaced while capture is being finalized.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed record RecordingFinalizationSnapshot(
    Guid SessionId,
    RecordingArtifactStage Stage,
    string? RecoverableAudioPath = null,
    double? Progress = null);

public enum RecordingArtifactStage
{
    Stopping,
    Processing,
    Verifying,
    Promoting,
    Ready,
    AttentionRequired
}
