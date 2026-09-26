using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Performs the mandatory full model-load probe in the same isolated worker/runtime used for jobs.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </remarks>
public sealed class LocalWhisperModelActivationVerifier : IWhisperModelActivationVerifier
{
    private readonly string _workerExecutablePath;
    private readonly string _backend;
    private readonly int _threadCount;
    private readonly LocalTranscriptionBackend _requestedBackend;
    private readonly ILocalWorkerClientFactory _workerClients;

    public LocalWhisperModelActivationVerifier(
        LocalTranscriptionRuntimeOptions options,
        ILocalWorkerClientFactory workerClients)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _workerExecutablePath = Path.GetFullPath(options.WorkerExecutablePath);
        _backend = LocalWhisperTranscriptionEngine.ToWorkerBackend(options.RequestedBackend);
        _requestedBackend = options.RequestedBackend;
        _threadCount = options.ThreadCount;
        _workerClients = workerClients ?? throw new ArgumentNullException(nameof(workerClients));
    }

    public async ValueTask VerifyCanLoadAsync(
        string payloadPath,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentNullException.ThrowIfNull(descriptor);
        await using var client = _workerClients.Create();
        var initialized = await client.StartAsync(_workerExecutablePath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(initialized.State, "initialized", StringComparison.Ordinal)
            || !string.Equals(
                initialized.WorkerVersion,
                LocalWorkerRuntimeContract.CurrentWorkerVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The packaged local worker identity is invalid.");
        }

        var ready = await client.ProbeAsync(
                new WorkerProbePayload(
                    Path.GetFullPath(payloadPath),
                    descriptor.Sha256,
                    _backend,
                    _threadCount),
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(ready.State, "probe_completed", StringComparison.Ordinal)
            || !string.Equals(
                ready.WorkerVersion,
                LocalWorkerRuntimeContract.CurrentWorkerVersion,
                StringComparison.Ordinal)
            || !IsAllowedResolvedBackend(_requestedBackend, ready.Backend))
        {
            throw new InvalidDataException("The local worker did not verify the complete model load.");
        }
    }

    private static bool IsAllowedResolvedBackend(
        LocalTranscriptionBackend requested,
        string? resolved) => requested switch
        {
            LocalTranscriptionBackend.Auto => resolved is "cpu" or "metal" or "vulkan",
            LocalTranscriptionBackend.Cpu => resolved is "cpu",
            LocalTranscriptionBackend.Metal => resolved is "metal" or "cpu",
            LocalTranscriptionBackend.Vulkan => resolved is "vulkan" or "cpu",
            _ => false,
        };
}
