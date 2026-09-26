using System.Runtime.InteropServices;

namespace IsTranscribe.Core.Transcription;

/// <summary>
/// Inactive provider-neutral boundary for a future transcription engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#future-activation
/// @spec spec://common/PROP-006-release-v2-product-canon#transcription-seam
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#target-structure
/// </remarks>
public interface ITranscriptionEngine
{
    TranscriptionEngineCapabilities Capabilities { get; }

    ValueTask<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability implemented by engines whose model catalog can change at runtime.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public interface ITranscriptionModelDiscovery
{
    ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverModelsAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Declared identity, privacy boundary and supported workload of one engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// </remarks>
public sealed record TranscriptionEngineCapabilities(
    string EngineId,
    string DisplayName,
    TranscriptionExecutionKind ExecutionKind,
    bool RequiresNetwork,
    string PrivacyDisclosure,
    IReadOnlyList<TranscriptionPlatformTarget> SupportedPlatforms,
    IReadOnlyList<TranscriptionModelCapability> Models,
    IReadOnlyList<string> SupportedLanguageCodes,
    bool SupportsAutomaticLanguageDetection,
    bool SupportsDiarization,
    TranscriptionTimestampCapabilities TimestampCapabilities,
    TranscriptionResourceRequirements? MinimumResources = null,
    bool SupportsModelDiscovery = false)
{
    private const TranscriptionTimestampCapabilities SupportedTimestampFlags =
        TranscriptionTimestampCapabilities.Segment | TranscriptionTimestampCapabilities.Word;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EngineId))
        {
            throw new ArgumentException("An engine id is required.", nameof(EngineId));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("An engine display name is required.", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(PrivacyDisclosure))
        {
            throw new ArgumentException("A privacy disclosure is required.", nameof(PrivacyDisclosure));
        }

        if (!Enum.IsDefined(ExecutionKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExecutionKind),
                ExecutionKind,
                "Unknown execution kind.");
        }

        ArgumentNullException.ThrowIfNull(SupportedPlatforms);
        ArgumentNullException.ThrowIfNull(Models);
        ArgumentNullException.ThrowIfNull(SupportedLanguageCodes);
        if (SupportedPlatforms.Count == 0)
        {
            throw new ArgumentException(
                "At least one supported platform is required.",
                nameof(SupportedPlatforms));
        }

        if (Models.Count == 0 && !SupportsModelDiscovery)
        {
            throw new ArgumentException("At least one model capability is required.", nameof(Models));
        }

        foreach (var platform in SupportedPlatforms)
        {
            ArgumentNullException.ThrowIfNull(platform);
            if (!Enum.IsDefined(platform.OperatingSystem))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SupportedPlatforms),
                    platform.OperatingSystem,
                    "Unknown operating system capability.");
            }

            if (!Enum.IsDefined(platform.Architecture))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SupportedPlatforms),
                    platform.Architecture,
                    "Unknown architecture capability.");
            }
        }

        foreach (var model in Models)
        {
            ArgumentNullException.ThrowIfNull(model);
            model.Validate();
        }

        if (SupportedLanguageCodes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Supported language codes cannot contain blank values.",
                nameof(SupportedLanguageCodes));
        }

        if ((TimestampCapabilities & ~SupportedTimestampFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TimestampCapabilities),
                TimestampCapabilities,
                "Unknown timestamp capabilities were declared.");
        }

        MinimumResources?.Validate();
    }
}

public enum TranscriptionExecutionKind
{
    Local,
    Remote
}

public enum TranscriptionOperatingSystem
{
    Windows,
    MacOS,
    Linux
}

public sealed record TranscriptionPlatformTarget(
    TranscriptionOperatingSystem OperatingSystem,
    Architecture Architecture);

public sealed record TranscriptionModelCapability(
    string Id,
    string DisplayName,
    bool IsRecommended = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("A model id is required.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("A model display name is required.", nameof(DisplayName));
        }
    }
}

[Flags]
public enum TranscriptionTimestampCapabilities
{
    None = 0,
    Segment = 1,
    Word = 2
}

public sealed record TranscriptionResourceRequirements(
    long? MinimumSystemMemoryBytes = null,
    long? MinimumGpuMemoryBytes = null,
    long? MinimumFreeDiskBytes = null)
{
    public void Validate()
    {
        ValidateNonNegative(MinimumSystemMemoryBytes, nameof(MinimumSystemMemoryBytes));
        ValidateNonNegative(MinimumGpuMemoryBytes, nameof(MinimumGpuMemoryBytes));
        ValidateNonNegative(MinimumFreeDiskBytes, nameof(MinimumFreeDiskBytes));
    }

    private static void ValidateNonNegative(long? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A minimum resource requirement cannot be negative.");
        }
    }
}

/// <summary>
/// Minimal identity and finalized primary audio artifact submitted to an engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// </remarks>
public sealed record TranscriptionRequest(
    Guid SessionId,
    string PrimaryAudioArtifactPath,
    string? ModelId = null,
    string? Language = null,
    TimeSpan? SourceStart = null,
    TimeSpan? SourceEnd = null,
    Guid? JobId = null,
    string? ChunkId = null,
    bool? RequireZeroDataRetention = null)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty)
        {
            throw new ArgumentException("A session id is required.", nameof(SessionId));
        }

        if (string.IsNullOrWhiteSpace(PrimaryAudioArtifactPath))
        {
            throw new ArgumentException(
                "A primary audio artifact path is required.",
                nameof(PrimaryAudioArtifactPath));
        }

        if (ModelId is not null && string.IsNullOrWhiteSpace(ModelId))
        {
            throw new ArgumentException("A model id cannot be blank.", nameof(ModelId));
        }

        if (Language is not null && string.IsNullOrWhiteSpace(Language))
        {
            throw new ArgumentException("A language cannot be blank.", nameof(Language));
        }

        if (SourceStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SourceStart),
                SourceStart,
                "A source range cannot start before the audio artifact.");
        }

        if (SourceEnd < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SourceEnd),
                SourceEnd,
                "A source range cannot end before the audio artifact.");
        }

        if (SourceStart is { } start && SourceEnd is { } end && end <= start)
        {
            throw new ArgumentException(
                "A source range end must follow its start.",
                nameof(SourceEnd));
        }

        if (JobId == Guid.Empty)
        {
            throw new ArgumentException("A job id cannot be empty when supplied.", nameof(JobId));
        }

        if (ChunkId is not null && string.IsNullOrWhiteSpace(ChunkId))
        {
            throw new ArgumentException("A chunk id cannot be blank.", nameof(ChunkId));
        }
    }
}

/// <summary>
/// Provider-neutral progress reported by a future engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// </remarks>
public sealed record TranscriptionProgress
{
    public TranscriptionProgress(TranscriptionProgressStage stage, double? fraction = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown progress stage.");
        }

        if (fraction is { } value && (!double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fraction),
                fraction,
                "Progress must be between zero and one, or null when indeterminate.");
        }

        Stage = stage;
        Fraction = fraction;
    }

    public TranscriptionProgressStage Stage { get; }

    public double? Fraction { get; }
}

public enum TranscriptionProgressStage
{
    Preparing,
    Uploading,
    Processing,
    Transcribing,
    Finalizing
}

/// <summary>
/// Structured completed or failed outcome without provider response payloads.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#extension-contract
/// </remarks>
public sealed record TranscriptionResult
{
    private TranscriptionResult(
        TranscriptionResultStatus status,
        string? text,
        string? detectedLanguage,
        IReadOnlyList<TranscriptionSegment> segments,
        TranscriptionResultMetadata? metadata,
        TranscriptionError? error)
    {
        Status = status;
        Text = text;
        DetectedLanguage = detectedLanguage;
        Segments = segments;
        Metadata = metadata;
        Error = error;
    }

    public TranscriptionResultStatus Status { get; }

    public bool Succeeded => Status == TranscriptionResultStatus.Completed;

    public string? Text { get; }

    public string? DetectedLanguage { get; }

    public IReadOnlyList<TranscriptionSegment> Segments { get; }

    public TranscriptionResultMetadata? Metadata { get; }

    public TranscriptionError? Error { get; }

    public static TranscriptionResult Completed(
        string text,
        string? detectedLanguage = null,
        IReadOnlyList<TranscriptionSegment>? segments = null,
        TranscriptionResultMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var segmentSnapshot = segments?.ToArray() ?? [];
        foreach (var segment in segmentSnapshot)
        {
            ArgumentNullException.ThrowIfNull(segment);
        }

        metadata?.Validate();

        return new TranscriptionResult(
            TranscriptionResultStatus.Completed,
            text,
            detectedLanguage,
            segmentSnapshot,
            metadata,
            error: null);
    }

    public static TranscriptionResult Failed(TranscriptionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new TranscriptionResult(
            TranscriptionResultStatus.Failed,
            text: null,
            detectedLanguage: null,
            segments: [],
            metadata: null,
            error);
    }
}

public enum TranscriptionResultStatus
{
    Completed,
    Failed
}

/// <summary>
/// Bounded provider-neutral metadata used for diagnostics and normalized artifacts.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </remarks>
public sealed record TranscriptionResultMetadata(
    string? ResolvedModelId = null,
    string? RequestId = null,
    TranscriptionUsage? Usage = null,
    TimeSpan? SourceStart = null,
    TimeSpan? SourceEnd = null,
    TimeSpan? AudioDuration = null)
{
    public void Validate()
    {
        if (ResolvedModelId is not null && string.IsNullOrWhiteSpace(ResolvedModelId))
        {
            throw new ArgumentException("A resolved model id cannot be blank.", nameof(ResolvedModelId));
        }

        if (RequestId is not null && string.IsNullOrWhiteSpace(RequestId))
        {
            throw new ArgumentException("A request id cannot be blank.", nameof(RequestId));
        }

        Usage?.Validate();
        ValidateNonNegative(SourceStart, nameof(SourceStart));
        ValidateNonNegative(SourceEnd, nameof(SourceEnd));
        ValidateNonNegative(AudioDuration, nameof(AudioDuration));
        if (SourceStart is { } start && SourceEnd is { } end && end <= start)
        {
            throw new ArgumentException("A source range end must follow its start.", nameof(SourceEnd));
        }
    }

    private static void ValidateNonNegative(TimeSpan? value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A result time value cannot be negative.");
        }
    }
}

public sealed record TranscriptionUsage(
    double? AudioSeconds = null,
    long? InputUnits = null,
    long? OutputUnits = null,
    decimal? ReportedCost = null,
    string? Currency = null)
{
    public void Validate()
    {
        if (AudioSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AudioSeconds),
                AudioSeconds,
                "Audio usage must be finite and non-negative.");
        }

        if (InputUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InputUnits),
                InputUnits,
                "Input usage cannot be negative.");
        }

        if (OutputUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutputUnits),
                OutputUnits,
                "Output usage cannot be negative.");
        }

        if (ReportedCost < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReportedCost),
                ReportedCost,
                "Reported cost cannot be negative.");
        }

        if (Currency is not null && string.IsNullOrWhiteSpace(Currency))
        {
            throw new ArgumentException("A currency cannot be blank.", nameof(Currency));
        }
    }
}

public sealed record TranscriptionSegment
{
    public TranscriptionSegment(
        string text,
        TimeSpan? start = null,
        TimeSpan? end = null,
        string? speakerLabel = null,
        IReadOnlyList<TranscriptionWord>? words = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Segment text is required.", nameof(text));
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "Segment start cannot be negative.");
        }

        if (end < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "Segment end cannot be negative.");
        }

        if (start is { } startValue && end is { } endValue && endValue < startValue)
        {
            throw new ArgumentException("Segment end cannot precede its start.", nameof(end));
        }

        Text = text;
        Start = start;
        End = end;
        SpeakerLabel = speakerLabel;
        Words = words?.ToArray() ?? [];
        foreach (var word in Words)
        {
            ArgumentNullException.ThrowIfNull(word);
            word.Validate();
        }
    }

    public string Text { get; }

    public TimeSpan? Start { get; }

    public TimeSpan? End { get; }

    public string? SpeakerLabel { get; }

    public IReadOnlyList<TranscriptionWord> Words { get; }
}

/// <summary>
/// Optional normalized word timing emitted by an engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record TranscriptionWord(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double? Confidence = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("Word text is required.", nameof(Text));
        }

        if (Start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Start), Start, "Word start cannot be negative.");
        }

        if (End < Start)
        {
            throw new ArgumentException("Word end cannot precede its start.", nameof(End));
        }

        if (Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Confidence),
                Confidence,
                "Word confidence must be between zero and one.");
        }
    }
}

public sealed record TranscriptionError
{
    public TranscriptionError(
        TranscriptionErrorCategory category,
        string code,
        string message,
        TimeSpan? suggestedDelay = null,
        string? requestId = null,
        TranscriptionFailureDisposition disposition = TranscriptionFailureDisposition.Terminal)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown error category.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("An error code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error message is required.", nameof(message));
        }

        if (suggestedDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suggestedDelay),
                suggestedDelay,
                "A suggested delay cannot be negative.");
        }

        if (requestId is not null && string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("A request id cannot be blank.", nameof(requestId));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unknown failure disposition.");
        }

        Category = category;
        Code = code;
        Message = message;
        SuggestedDelay = suggestedDelay;
        RequestId = requestId;
        Disposition = disposition;
    }

    public TranscriptionErrorCategory Category { get; }

    public string Code { get; }

    public string Message { get; }

    public TimeSpan? SuggestedDelay { get; }

    public string? RequestId { get; }

    public TranscriptionFailureDisposition Disposition { get; }
}

public enum TranscriptionFailureDisposition
{
    Terminal,
    TryAgain,
    SplitInput,
    AttentionRequired
}

public enum TranscriptionErrorCategory
{
    InvalidRequest,
    UnsupportedInput,
    EngineUnavailable,
    ResourceUnavailable,
    Configuration,
    Authentication,
    PaymentRequired,
    PayloadTooLarge,
    RateLimited,
    Network,
    Processing
}
