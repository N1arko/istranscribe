namespace IsTranscribe.Transcription.Local.Protocol;

/// <summary>
/// Stable constants for the private parent/worker transport.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
public static class WorkerProtocol
{
    public const uint CurrentVersion = 1;
    public const int FramePrefixBytes = sizeof(uint);
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int MaximumStringBytes = 512 * 1024;
    public const int MaximumSegmentsPerChunk = 20_000;
}

public enum WorkerMessageKind
{
    Hello,
    Ready,
    Probe,
    Start,
    Progress,
    Result,
    Failure,
    Cancel,
    Shutdown,
}

public interface IWorkerPayload;

public sealed record WorkerEnvelope(
    uint ProtocolVersion,
    WorkerMessageKind Kind,
    string CorrelationId,
    string JobId,
    int ChunkIndex,
    long Sequence,
    IWorkerPayload Payload);

public sealed record WorkerHelloPayload(
    int ParentProcessId,
    string ClientVersion) : IWorkerPayload;

public sealed record WorkerReadyPayload(
    string State,
    string WorkerVersion,
    string? Backend,
    long? AvailableMemoryBytes) : IWorkerPayload;

public sealed record WorkerProbePayload(
    string ModelPath,
    string ModelSha256,
    string RequestedBackend,
    int MaximumThreads) : IWorkerPayload;

public sealed record WorkerStartPayload(
    string InputPath,
    string InputSha256,
    string ModelPath,
    string ModelSha256,
    string Language,
    string Backend,
    long StartMilliseconds,
    long EndMilliseconds,
    int MaximumThreads) : IWorkerPayload;

public sealed record WorkerProgressPayload(
    string Stage,
    long CompletedMilliseconds,
    long TotalMilliseconds,
    long WorkingSetBytes,
    long CpuMilliseconds) : IWorkerPayload;

public sealed record WorkerSegmentPayload(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

public sealed record WorkerResultPayload(
    string Language,
    long ProcessingMilliseconds,
    string RuntimeVersion,
    string ModelSha256,
    string Backend,
    IReadOnlyList<WorkerSegmentPayload> Segments) : IWorkerPayload;

public sealed record WorkerFailurePayload(
    string Category,
    string StableCode,
    string SafeMessage,
    bool Retryable,
    string? Backend) : IWorkerPayload;

public sealed record WorkerCancelPayload(string Reason) : IWorkerPayload;

public sealed record WorkerShutdownPayload(string Reason) : IWorkerPayload;

public enum WorkerProtocolError
{
    UnexpectedEndOfStream,
    EmptyFrame,
    FrameTooLarge,
    InvalidUtf8,
    InvalidJson,
    InvalidSchema,
    UnsupportedVersion,
    InvalidSequence,
    InvalidOrder,
    CorrelationMismatch,
    JobMismatch,
    ChunkMismatch,
}

public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(WorkerProtocolError error, string message)
        : base(message)
    {
        Error = error;
    }

    public WorkerProtocolException(WorkerProtocolError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public WorkerProtocolError Error { get; }
}
