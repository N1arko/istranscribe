using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Worker.Runtime;

public sealed record LocalWorkerProbeResult(
    string Backend,
    long? AvailableMemoryBytes);

/// <summary>
/// Native-independent seam implemented by the pinned whisper bridge during integration.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public interface ILocalInferenceBackend
{
    ValueTask<LocalWorkerProbeResult> ProbeAsync(
        WorkerProbePayload request,
        CancellationToken cancellationToken);

    ValueTask<WorkerResultPayload> TranscribeAsync(
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload> progress,
        CancellationToken cancellationToken);
}

public sealed class LocalInferenceException : Exception
{
    public LocalInferenceException(
        string category,
        string stableCode,
        string safeMessage,
        bool retryable,
        string? backend = null)
        : base(safeMessage)
    {
        Category = category;
        StableCode = stableCode;
        Retryable = retryable;
        Backend = backend;
    }

    public string Category { get; }

    public string StableCode { get; }

    public bool Retryable { get; }

    public string? Backend { get; }

    public WorkerFailurePayload ToPayload() => new(
        Category,
        StableCode,
        Message,
        Retryable,
        Backend);
}

public interface IParentProcessMonitor
{
    Task WaitForExitAsync(int parentProcessId, CancellationToken cancellationToken);
}
