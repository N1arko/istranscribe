namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Controls bounded download retries and the free-space reserve kept by the model store.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download</remarks>
public sealed class WhisperModelStoreOptions
{
    public const long DefaultSafetyMarginBytes = 256L * 1024L * 1024L;

    public int MaximumDownloadAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan DownloadAttemptTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public long SafetyMarginBytes { get; init; } = DefaultSafetyMarginBytes;

    internal void Validate()
    {
        if (MaximumDownloadAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDownloadAttempts),
                "MaximumDownloadAttempts must be between 1 and 10.");
        }

        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }

        if (DownloadAttemptTimeout <= TimeSpan.Zero || DownloadAttemptTimeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(DownloadAttemptTimeout));
        }

        if (SafetyMarginBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyMarginBytes));
        }
    }
}

public enum ModelAcquisitionDisposition
{
    Downloaded,
    Resumed,
    DownloadedAfterServerRestart,
    ActivatedCachedPayload,
    ReusedInstalledPayload,
}

public enum ModelStoreError
{
    StorageNotWritable,
    InsufficientDiskSpace,
    StorageWriteFailed,
    TransportRejected,
    DownloadIncomplete,
    DownloadProtocolInvalid,
    PayloadSizeMismatch,
    PayloadHashMismatch,
    PayloadFormatMismatch,
    ActivationFailed,
    RetentionStateUnavailable,
}

/// <summary>
/// Stable failure surfaced by the model acquisition boundary without transport response bodies.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class ModelStoreException : Exception
{
    public ModelStoreException(ModelStoreError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public ModelStoreError Error { get; }
}

public sealed record ModelInstallation(
    string ModelId,
    string Sha256,
    string PayloadPath,
    long SizeBytes,
    string CatalogVersion,
    string FormatToken,
    DateTimeOffset ActivatedAtUtc);

public sealed record ModelAcquisitionResult(
    ModelInstallation Installation,
    ModelAcquisitionDisposition Disposition,
    long BytesReceivedFromNetwork);

public sealed record ModelRemovalResult(
    bool Removed,
    bool PayloadDeleted,
    bool PayloadRetainedByLease,
    string? RemovedSha256);

public sealed record ModelStoreCleanupResult(
    int RemovalTombstonesRecovered,
    IReadOnlyList<string> DeletedOrphanSha256,
    IReadOnlyList<string> RetainedOrphanSha256);

public sealed record ModelStorePaths(
    string PayloadPath,
    string ActiveManifestPath,
    string PartialPath,
    string PartialStatePath);

/// <summary>
/// Storage capability probe kept injectable so a failed disk check is deterministic and precedes network access.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download</remarks>
public interface IModelStorageProbe
{
    ValueTask EnsureWritableAsync(string directoryPath, CancellationToken cancellationToken);

    ValueTask<long> GetAvailableFreeSpaceAsync(string directoryPath, CancellationToken cancellationToken);
}

/// <summary>
/// Mandatory full-load compatibility gate run after size/hash/header validation and before a model
/// becomes active. Implementations must exercise the selected pinned native ABI and release its context.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// </remarks>
public interface IWhisperModelActivationVerifier
{
    /// <summary>
    /// Opens the complete model with the selected pinned runtime ABI and closes the native context.
    /// Returning successfully is the authorization for switching the durable active pointer.
    /// </summary>
    ValueTask VerifyCanLoadAsync(
        string payloadPath,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates deletion with durable transcription jobs. The lease must prevent a new durable
/// reference to the hash until it is disposed. Enqueue takes its usage/read lease before calling
/// store reads; deletion therefore takes this deletion/write lease before the store mutation gate.
/// The store has no permissive default implementation.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// </remarks>
public interface IModelPayloadRetentionLeaseProvider
{
    /// <summary>
    /// Held across active-model validation and the durable job insert. Once the insert commits,
    /// a later deletion lease observes the durable reference before this lease is released.
    /// </summary>
    ValueTask<IModelPayloadUsageLease> AcquireUsageLeaseAsync(
        string sha256,
        CancellationToken cancellationToken);

    ValueTask<IModelPayloadRetentionLease> AcquireDeletionLeaseAsync(
        string sha256,
        CancellationToken cancellationToken);
}

/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// </remarks>
public interface IModelPayloadUsageLease : IAsyncDisposable
{
}

/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// </remarks>
public interface IModelPayloadRetentionLease : IAsyncDisposable
{
    /// <summary>
    /// True when a queued/running/recoverable durable job still owns this exact model hash.
    /// </summary>
    bool IsRetained { get; }
}

/// <summary>
/// Narrow mutation seam used to verify crash-safe removal transitions.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#models</remarks>
public interface IModelStoreFileMutator
{
    bool FileExists(string path);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite = false);

    void DeleteFile(string path);
}
