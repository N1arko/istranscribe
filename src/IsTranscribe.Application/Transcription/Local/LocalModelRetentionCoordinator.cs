using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Serializes active-pointer reads/durable inserts against model removal. A usage lease is held
/// until the local job row commits; a deletion lease observes durable retention while holding the
/// same gate.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed class LocalModelRetentionCoordinator : IModelPayloadRetentionLeaseProvider
{
    private readonly TranscriptionJobRepository _repository;
    private readonly SemaphoreSlim _orderingGate = new(1, 1);

    public LocalModelRetentionCoordinator(TranscriptionJobRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<IModelPayloadUsageLease> AcquireUsageLeaseAsync(
        string sha256,
        CancellationToken cancellationToken)
    {
        ValidateSha256(sha256);
        await _orderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new UsageLease(_orderingGate);
    }

    public async ValueTask<IModelPayloadRetentionLease> AcquireDeletionLeaseAsync(
        string sha256,
        CancellationToken cancellationToken)
    {
        ValidateSha256(sha256);
        await _orderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retained = await _repository
                .IsLocalModelRetainedAsync(sha256, cancellationToken)
                .ConfigureAwait(false);
            return new RetentionLease(_orderingGate, retained);
        }
        catch
        {
            _orderingGate.Release();
            throw;
        }
    }

    private static void ValidateSha256(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64
            || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A canonical lowercase model SHA-256 is required.", nameof(sha256));
        }
    }

    private sealed class UsageLease(SemaphoreSlim gate) : IModelPayloadUsageLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetentionLease(SemaphoreSlim gate, bool isRetained)
        : IModelPayloadRetentionLease
    {
        private int _disposed;

        public bool IsRetained { get; } = isRetained;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
