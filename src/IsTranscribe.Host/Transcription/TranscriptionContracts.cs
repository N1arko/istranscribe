namespace IsTranscribe.Host.Transcription;

public sealed record TranscriptionRequest(
    string SessionId,
    string AudioPath,
    string Model,
    bool DiarizationEnabled,
    string? Language,
    int MinSpeakers,
    int MaxSpeakers);

public sealed record TranscriptionResponse(
    string Text,
    string RawJson,
    bool HasSpeakerMetadata);

public interface ITranscriptionProvider
{
    ValueTask<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, string apiKey, CancellationToken cancellationToken);
}

// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#fault-taxonomy.categories
public enum TranscriptionFailureKind
{
    Transient,
    Recoverable,
    Configuration,
    Terminal
}

public sealed class TranscriptionProviderException(
    string errorCode,
    string message,
    TranscriptionFailureKind kind,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public TranscriptionFailureKind Kind { get; } = kind;
}
