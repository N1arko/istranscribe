using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Testable Application-side ownership boundary around one short-lived private worker session.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public interface ILocalWorkerClient : IAsyncDisposable
{
    Task<WorkerReadyPayload> StartAsync(string workerExecutablePath, CancellationToken cancellationToken);

    Task<WorkerReadyPayload> ProbeAsync(WorkerProbePayload request, CancellationToken cancellationToken);

    Task<WorkerResultPayload> TranscribeAsync(
        string jobId,
        int chunkIndex,
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates one isolated parent-side client for a short-lived local worker session.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#worker</remarks>
public interface ILocalWorkerClientFactory
{
    ILocalWorkerClient Create();
}

/// <summary>
/// Production client factory backed by inherited anonymous pipes and the bounded coordinator.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#worker</remarks>
public sealed class CoordinatorLocalWorkerClientFactory : ILocalWorkerClientFactory
{
    private readonly IWorkerProcessLauncher _launcher;
    private readonly LocalWorkerCoordinatorOptions _options;

    public CoordinatorLocalWorkerClientFactory(
        IWorkerProcessLauncher? launcher = null,
        LocalWorkerCoordinatorOptions? options = null)
    {
        _launcher = launcher ?? new AnonymousPipeWorkerProcessLauncher();
        _options = options ?? LocalWorkerCoordinatorOptions.Default;
    }

    public ILocalWorkerClient Create() => new CoordinatorLocalWorkerClient(
        new LocalWorkerCoordinator(_launcher, _options));

    private sealed class CoordinatorLocalWorkerClient(LocalWorkerCoordinator coordinator) : ILocalWorkerClient
    {
        public Task<WorkerReadyPayload> StartAsync(
            string workerExecutablePath,
            CancellationToken cancellationToken) =>
            coordinator.StartAsync(workerExecutablePath, cancellationToken);

        public Task<WorkerReadyPayload> ProbeAsync(
            WorkerProbePayload request,
            CancellationToken cancellationToken) =>
            coordinator.ProbeAsync(request, cancellationToken);

        public Task<WorkerResultPayload> TranscribeAsync(
            string jobId,
            int chunkIndex,
            WorkerStartPayload request,
            IProgress<WorkerProgressPayload>? progress,
            CancellationToken cancellationToken) =>
            coordinator.TranscribeAsync(jobId, chunkIndex, request, progress, cancellationToken);

        public ValueTask DisposeAsync() => coordinator.DisposeAsync();
    }
}
