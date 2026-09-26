using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Hash-addressed, resumable store for explicitly downloaded Whisper model payloads.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class WhisperModelStore : IAsyncDisposable
{
    private const int CopyBufferSize = 128 * 1024;
    private readonly string _rootPath;
    private readonly HttpClient _httpClient;
    private readonly WhisperModelStoreOptions _options;
    private readonly IModelStorageProbe _storageProbe;
    private readonly IWhisperModelActivationVerifier _activationVerifier;
    private readonly IModelPayloadRetentionLeaseProvider _retentionLeaseProvider;
    private readonly GgmlModelHeaderVerifier _headerVerifier;
    private readonly IModelStoreFileMutator _fileMutator;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public WhisperModelStore(
        string rootPath,
        HttpClient httpClient,
        IWhisperModelActivationVerifier activationVerifier,
        IModelPayloadRetentionLeaseProvider retentionLeaseProvider,
        WhisperModelStoreOptions? options = null,
        IModelStorageProbe? storageProbe = null,
        GgmlModelHeaderVerifier? headerVerifier = null,
        IModelStoreFileMutator? fileMutator = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(activationVerifier);
        ArgumentNullException.ThrowIfNull(retentionLeaseProvider);

        _rootPath = Path.GetFullPath(rootPath);
        _httpClient = httpClient;
        _options = options ?? new WhisperModelStoreOptions();
        _options.Validate();
        _storageProbe = storageProbe ?? new PhysicalModelStorageProbe();
        _activationVerifier = activationVerifier;
        _retentionLeaseProvider = retentionLeaseProvider;
        _headerVerifier = headerVerifier ?? new GgmlModelHeaderVerifier();
        _fileMutator = fileMutator ?? new PhysicalModelStoreFileMutator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RootPath => _rootPath;

    /// <summary>
    /// Resolves paths only from reviewed model id/hash tokens; no upstream filename participates in removal paths.
    /// </summary>
    /// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download</remarks>
    public ModelStorePaths GetPaths(WhisperModelDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);

        return new ModelStorePaths(
            Path.Combine(_rootPath, "blobs", "sha256", $"{descriptor.Sha256}.bin"),
            Path.Combine(_rootPath, "active", $"{descriptor.Id}.json"),
            Path.Combine(_rootPath, "downloads", $"{descriptor.Sha256}.partial"),
            Path.Combine(_rootPath, "downloads", $"{descriptor.Sha256}.partial.json"));
    }

    /// <summary>
    /// Validates the active pointer and payload locally. This method has no network path.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </remarks>
    public async Task<ModelInstallation?> GetActiveInstallationAsync(
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDescriptor(descriptor);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryGetValidActiveInstallationAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Performs the only network operation in the local engine: an explicit, resumable model acquisition.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </remarks>
    public async Task<ModelAcquisitionResult> AcquireAsync(
        WhisperModelDescriptor descriptor,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDescriptor(descriptor);
        var paths = GetPaths(descriptor);
        PayloadValidation observedValidation;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observedActive = await TryGetValidActiveInstallationAsync(
                descriptor,
                cancellationToken).ConfigureAwait(false);
            if (observedActive is not null)
            {
                progress?.Report(descriptor.DownloadSizeBytes);
                return new ModelAcquisitionResult(
                    observedActive,
                    ModelAcquisitionDisposition.ReusedInstalledPayload,
                    BytesReceivedFromNetwork: 0);
            }

            observedValidation = await ValidatePayloadAsync(
                paths.PayloadPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }

        IModelPayloadRetentionLease? repairLease = observedValidation is
            PayloadValidation.Missing or PayloadValidation.Valid
                ? null
                : await AcquireRetentionLeaseAsync(
                    descriptor.Sha256,
                    cancellationToken).ConfigureAwait(false);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await TryGetValidActiveInstallationAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                progress?.Report(descriptor.DownloadSizeBytes);
                return new ModelAcquisitionResult(
                    active,
                    ModelAcquisitionDisposition.ReusedInstalledPayload,
                    BytesReceivedFromNetwork: 0);
            }

            var cachedValidation = await ValidatePayloadAsync(
                paths.PayloadPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            if (cachedValidation == PayloadValidation.Valid)
            {
                await VerifyForActivationAsync(
                    paths.PayloadPath,
                    descriptor,
                    cancellationToken).ConfigureAwait(false);
                var cachedInstallation = await ActivateAsync(
                    paths,
                    descriptor,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(descriptor.DownloadSizeBytes);
                return new ModelAcquisitionResult(
                    cachedInstallation,
                    ModelAcquisitionDisposition.ActivatedCachedPayload,
                    BytesReceivedFromNetwork: 0);
            }

            if (cachedValidation != PayloadValidation.Missing)
            {
                if (repairLease is null)
                {
                    throw CreateIntegrityException(cachedValidation);
                }

                if (repairLease.IsRetained)
                {
                    throw new ModelStoreException(
                        ModelStoreError.RetentionStateUnavailable,
                        "A corrupt model payload is still retained by a durable transcription job.");
                }

                try
                {
                    _fileMutator.DeleteFile(paths.PayloadPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelStoreException(
                        ModelStoreError.StorageWriteFailed,
                        "The corrupt model payload could not be removed before repair.",
                        exception);
                }
            }

            if (repairLease is not null)
            {
                await repairLease.DisposeAsync().ConfigureAwait(false);
                repairLease = null;
            }

            await _storageProbe.EnsureWritableAsync(
                _rootPath,
                cancellationToken).ConfigureAwait(false);
            EnsureStoreDirectories(paths);
            await _storageProbe.EnsureWritableAsync(
                Path.GetDirectoryName(paths.PartialPath)!,
                cancellationToken).ConfigureAwait(false);

            var initialPartialLength = await PreparePartialAsync(
                paths,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSufficientSpaceAsync(
                Path.GetDirectoryName(paths.PartialPath)!,
                descriptor.DownloadSizeBytes - initialPartialLength,
                cancellationToken).ConfigureAwait(false);

            var transfer = await DownloadWithRetriesAsync(
                paths,
                descriptor,
                initialPartialLength,
                progress,
                cancellationToken).ConfigureAwait(false);

            var validation = await ValidatePayloadAsync(
                paths.PartialPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            if (validation != PayloadValidation.Valid)
            {
                DeletePartialArtifacts(paths);
                throw CreateIntegrityException(validation);
            }

            try
            {
                await _headerVerifier.VerifyAsync(
                    paths.PartialPath,
                    descriptor,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ModelStoreException exception) when (exception.Error == ModelStoreError.PayloadFormatMismatch)
            {
                DeletePartialArtifacts(paths);
                throw;
            }

            await PromoteAsync(paths, descriptor, cancellationToken).ConfigureAwait(false);
            await VerifyForActivationAsync(
                paths.PayloadPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            var installation = await ActivateAsync(paths, descriptor, cancellationToken).ConfigureAwait(false);
            progress?.Report(descriptor.DownloadSizeBytes);

            var disposition = transfer.ServerRestartedRange
                ? ModelAcquisitionDisposition.DownloadedAfterServerRestart
                : initialPartialLength > 0 || transfer.UsedRangeResume
                    ? ModelAcquisitionDisposition.Resumed
                    : ModelAcquisitionDisposition.Downloaded;
            return new ModelAcquisitionResult(installation, disposition, transfer.NetworkBytes);
        }
        finally
        {
            _mutationGate.Release();
            if (repairLease is not null)
            {
                await repairLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Removes only the active pointer and its exact hash-addressed payload.
    /// Durable job leases and other active pointers retain shared payload hashes.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async Task<ModelRemovalResult> RemoveActiveAsync(
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDescriptor(descriptor);
        await using var retentionLease = await AcquireRetentionLeaseAsync(
            descriptor.Sha256,
            cancellationToken).ConfigureAwait(false);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = GetPaths(descriptor);
            var manifest = await ReadActiveManifestAsync(paths.ActiveManifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest is null
                || !string.Equals(manifest.ModelId, descriptor.Id, StringComparison.Ordinal)
                || !string.Equals(manifest.Sha256, descriptor.Sha256, StringComparison.Ordinal))
            {
                return new ModelRemovalResult(false, false, false, null);
            }

            var tombstone = Path.Combine(
                Path.GetDirectoryName(paths.ActiveManifestPath)!,
                $".removing-{descriptor.Id}-{Guid.NewGuid():N}.json");
            try
            {
                _fileMutator.MoveFile(paths.ActiveManifestPath, tombstone);
            }
            catch (FileNotFoundException)
            {
                return new ModelRemovalResult(false, false, false, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ModelStoreException(
                    ModelStoreError.StorageWriteFailed,
                    "The active model pointer could not be removed.",
                    exception);
            }

            var payloadDeleted = false;
            var removalCommitted = false;
            try
            {
                var referencedByAnotherActive = await IsHashReferencedAsync(
                    manifest.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!retentionLease.IsRetained && !referencedByAnotherActive)
                {
                    var payloadPath = GetPayloadPath(manifest.Sha256);
                    payloadDeleted = _fileMutator.FileExists(payloadPath);
                    try
                    {
                        _fileMutator.DeleteFile(payloadPath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        if (_fileMutator.FileExists(payloadPath))
                        {
                            RestoreTombstone(tombstone, paths.ActiveManifestPath);
                        }

                        throw new ModelStoreException(
                            ModelStoreError.StorageWriteFailed,
                            "The active model payload could not be removed safely.",
                            exception);
                    }
                }

                removalCommitted = true;
                try
                {
                    _fileMutator.DeleteFile(tombstone);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelStoreException(
                        ModelStoreError.StorageWriteFailed,
                        "Model removal committed; its recovery tombstone still needs cleanup.",
                        exception);
                }

                return new ModelRemovalResult(
                    true,
                    payloadDeleted,
                    retentionLease.IsRetained,
                    manifest.Sha256);
            }
            catch (OperationCanceledException)
            {
                if (!removalCommitted)
                {
                    RestoreTombstone(tombstone, paths.ActiveManifestPath);
                }

                throw;
            }
            catch (ModelStoreException)
            {
                if (!removalCommitted && _fileMutator.FileExists(GetPayloadPath(manifest.Sha256)))
                {
                    RestoreTombstone(tombstone, paths.ActiveManifestPath);
                }

                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!removalCommitted && _fileMutator.FileExists(GetPayloadPath(manifest.Sha256)))
                {
                    RestoreTombstone(tombstone, paths.ActiveManifestPath);
                }

                throw new ModelStoreException(
                    ModelStoreError.StorageWriteFailed,
                    "The model removal transaction could not be completed safely.",
                    exception);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Completes interrupted removals and deletes only hash blobs held by neither an active pointer
    /// nor a durable job lease. A released retained orphan can be collected by a later call.
    /// Deletion always takes the retention lease before the store mutation gate, matching enqueue's
    /// usage-lease-before-read ordering.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async Task<ModelStoreCleanupResult> RecoverRemovalsAndCollectOrphansAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var recovered = await RecoverRemovalTombstonesAsync(cancellationToken).ConfigureAwait(false);
        var deleted = new List<string>();
        var retained = new List<string>();
        var blobDirectory = Path.Combine(_rootPath, "blobs", "sha256");
        if (!Directory.Exists(blobDirectory))
        {
            return new ModelStoreCleanupResult(recovered, deleted.AsReadOnly(), retained.AsReadOnly());
        }

        var candidates = Directory.EnumerateFiles(blobDirectory, "*.bin", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var payloadPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = Path.GetFileNameWithoutExtension(payloadPath);
            if (!IsLowerSha256(sha256))
            {
                continue;
            }

            await using var lease = await AcquireRetentionLeaseAsync(
                sha256,
                cancellationToken).ConfigureAwait(false);
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_fileMutator.FileExists(payloadPath)
                    || await IsHashReferencedAsync(sha256, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (lease.IsRetained)
                {
                    retained.Add(sha256);
                    continue;
                }

                try
                {
                    _fileMutator.DeleteFile(payloadPath);
                    deleted.Add(sha256);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelStoreException(
                        ModelStoreError.StorageWriteFailed,
                        "An unreferenced model payload could not be collected.",
                        exception);
                }
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        return new ModelStoreCleanupResult(recovered, deleted.AsReadOnly(), retained.AsReadOnly());
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _mutationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<ModelInstallation?> TryGetValidActiveInstallationAsync(
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var paths = GetPaths(descriptor);
        var manifest = await ReadActiveManifestAsync(paths.ActiveManifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest is null
            || manifest.SchemaVersion != 1
            || !string.Equals(manifest.ModelId, descriptor.Id, StringComparison.Ordinal)
            || !string.Equals(manifest.Sha256, descriptor.Sha256, StringComparison.Ordinal)
            || !string.Equals(manifest.CatalogVersion, descriptor.CatalogVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.FormatToken, descriptor.Format.Token, StringComparison.Ordinal))
        {
            return null;
        }

        if (await ValidatePayloadAsync(paths.PayloadPath, descriptor, cancellationToken).ConfigureAwait(false)
            != PayloadValidation.Valid)
        {
            return null;
        }

        await VerifyForActivationAsync(paths.PayloadPath, descriptor, cancellationToken).ConfigureAwait(false);
        return ToInstallation(manifest, paths.PayloadPath);
    }

    private async Task<long> PreparePartialAsync(
        ModelStorePaths paths,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var state = await ReadPartialStateAsync(paths.PartialStatePath, cancellationToken).ConfigureAwait(false);
        var stateMatches = state is not null
            && state.SchemaVersion == 1
            && string.Equals(state.ModelId, descriptor.Id, StringComparison.Ordinal)
            && string.Equals(state.Sha256, descriptor.Sha256, StringComparison.Ordinal)
            && state.ExpectedSizeBytes == descriptor.DownloadSizeBytes
            && string.Equals(state.DownloadUrl, descriptor.DownloadUri.AbsoluteUri, StringComparison.Ordinal)
            && string.Equals(state.FormatToken, descriptor.Format.Token, StringComparison.Ordinal);

        var partialLength = File.Exists(paths.PartialPath)
            ? new FileInfo(paths.PartialPath).Length
            : 0;
        if (!stateMatches || partialLength > descriptor.DownloadSizeBytes)
        {
            DeletePartialArtifacts(paths);
            partialLength = 0;
        }

        if (!File.Exists(paths.PartialPath))
        {
            await using var stream = new FileStream(
                paths.PartialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!stateMatches)
        {
            await WriteJsonAtomicallyAsync(
                paths.PartialStatePath,
                new PartialDownloadState(
                    SchemaVersion: 1,
                    descriptor.Id,
                    descriptor.Sha256,
                    descriptor.DownloadSizeBytes,
                    descriptor.DownloadUri.AbsoluteUri,
                    descriptor.Format.Token),
                ModelStoreError.StorageWriteFailed,
                cancellationToken).ConfigureAwait(false);
        }

        return partialLength;
    }

    private async Task EnsureSufficientSpaceAsync(
        string directoryPath,
        long bytesRemaining,
        CancellationToken cancellationToken)
    {
        long required;
        try
        {
            required = checked(Math.Max(0, bytesRemaining) + _options.SafetyMarginBytes);
        }
        catch (OverflowException exception)
        {
            throw new ModelStoreException(
                ModelStoreError.InsufficientDiskSpace,
                "The required model storage size is invalid.",
                exception);
        }

        var available = await _storageProbe.GetAvailableFreeSpaceAsync(
            directoryPath,
            cancellationToken).ConfigureAwait(false);
        if (available < required)
        {
            throw new ModelStoreException(
                ModelStoreError.InsufficientDiskSpace,
                $"The model download requires {required.ToString(CultureInfo.InvariantCulture)} free bytes including its safety margin.");
        }
    }

    private async Task<DownloadTransfer> DownloadWithRetriesAsync(
        ModelStorePaths paths,
        WhisperModelDescriptor descriptor,
        long initialPartialLength,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        long networkBytes = 0;
        var serverRestartedRange = false;
        var usedRangeResume = false;
        for (var attempt = 1; attempt <= _options.MaximumDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? retryFailure = null;
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(_options.DownloadAttemptTimeout);
            try
            {
                var pass = await DownloadPassAsync(
                    paths.PartialPath,
                    descriptor,
                    progress,
                    attemptCancellation.Token).ConfigureAwait(false);
                networkBytes = checked(networkBytes + pass.NetworkBytes);
                serverRestartedRange |= pass.ServerRestartedRange;
                usedRangeResume |= pass.UsedRangeResume;
                if (pass.Completed)
                {
                    return new DownloadTransfer(networkBytes, serverRestartedRange, usedRangeResume);
                }
            }
            catch (RetryableDownloadException exception)
            {
                networkBytes = checked(networkBytes + exception.NetworkBytes);
                retryFailure = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                retryFailure = exception;
            }

            if (retryFailure is not null && attempt == _options.MaximumDownloadAttempts)
            {
                throw new ModelStoreException(
                    ModelStoreError.TransportRejected,
                    "The model download did not complete within the bounded full-attempt timeout/retry policy.",
                    retryFailure);
            }

            if (attempt < _options.MaximumDownloadAttempts && _options.RetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.RetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        var currentLength = File.Exists(paths.PartialPath)
            ? new FileInfo(paths.PartialPath).Length
            : initialPartialLength;
        throw new ModelStoreException(
            ModelStoreError.DownloadIncomplete,
            $"The model download stopped at {currentLength.ToString(CultureInfo.InvariantCulture)} of {descriptor.DownloadSizeBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
    }

    private async Task<DownloadPass> DownloadPassAsync(
        string partialPath,
        WhisperModelDescriptor descriptor,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var currentLength = new FileInfo(partialPath).Length;
        var requestedRange = currentLength > 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.DownloadUri);
        if (requestedRange)
        {
            request.Headers.Range = new RangeHeaderValue(currentLength, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetryableDownloadException("The model transport timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new RetryableDownloadException("The model transport failed.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (currentLength == descriptor.DownloadSizeBytes)
                {
                    progress?.Report(currentLength);
                    return new DownloadPass(
                        Completed: true,
                        NetworkBytes: 0,
                        ServerRestartedRange: false,
                        UsedRangeResume: true);
                }

                TruncatePartial(partialPath);
                return new DownloadPass(
                    Completed: false,
                    NetworkBytes: 0,
                    ServerRestartedRange: true,
                    UsedRangeResume: requestedRange);
            }

            if (IsTransient(response.StatusCode))
            {
                throw new RetryableDownloadException(
                    $"The model endpoint returned transient HTTP {(int)response.StatusCode}.");
            }

            var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
            var isFull = response.StatusCode == HttpStatusCode.OK;
            if (!isPartial && !isFull)
            {
                throw new ModelStoreException(
                    ModelStoreError.TransportRejected,
                    $"The model endpoint rejected the request with HTTP {(int)response.StatusCode}.");
            }

            var serverRestartedRange = currentLength > 0 && isFull;
            if (isPartial)
            {
                ValidateContentRange(
                    response.Content.Headers.ContentRange,
                    response.Content.Headers.ContentLength,
                    currentLength,
                    descriptor.DownloadSizeBytes);
            }
            else if (serverRestartedRange)
            {
                TruncatePartial(partialPath);
                currentLength = 0;
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0
                && declaredLength > descriptor.DownloadSizeBytes - currentLength)
            {
                throw new ModelStoreException(
                    ModelStoreError.DownloadProtocolInvalid,
                    "The model response exceeds the pinned payload size.");
            }

            Stream source;
            try
            {
                source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RetryableDownloadException("The model response stream timed out.", exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new RetryableDownloadException("The model response stream could not be opened.", exception);
            }

            await using (source.ConfigureAwait(false))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                destination.Position = currentLength;
                var buffer = new byte[CopyBufferSize];
                long networkBytes = 0;
                while (true)
                {
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, cancellationToken).AsTask()
                            .WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new RetryableDownloadException(
                            "The model response timed out.",
                            exception,
                            networkBytes);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or HttpRequestException)
                    {
                        throw new RetryableDownloadException(
                            "The model response was interrupted.",
                            exception,
                            networkBytes);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (destination.Position > descriptor.DownloadSizeBytes - read)
                    {
                        throw new ModelStoreException(
                            ModelStoreError.DownloadProtocolInvalid,
                            "The model response exceeded the pinned payload size while streaming.");
                    }

                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new ModelStoreException(
                            ModelStoreError.StorageWriteFailed,
                            "The model partial file could not be written.",
                            exception);
                    }

                    networkBytes += read;
                    progress?.Report(destination.Position);
                }

                try
                {
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelStoreException(
                        ModelStoreError.StorageWriteFailed,
                        "The model partial file could not be flushed.",
                        exception);
                }

                return new DownloadPass(
                    destination.Length == descriptor.DownloadSizeBytes,
                    networkBytes,
                    serverRestartedRange,
                    UsedRangeResume: requestedRange && !serverRestartedRange);
            }
        }
    }

    private static void ValidateContentRange(
        ContentRangeHeaderValue? contentRange,
        long? contentLength,
        long offset,
        long expectedSize)
    {
        if (contentRange is null
            || !contentRange.HasRange
            || contentRange.From != offset
            || contentRange.To is null
            || contentRange.To < contentRange.From
            || contentRange.To >= expectedSize
            || !contentRange.HasLength
            || contentRange.Length != expectedSize
            || contentLength is not null && contentLength != contentRange.To - contentRange.From + 1)
        {
            throw new ModelStoreException(
                ModelStoreError.DownloadProtocolInvalid,
                "The model endpoint returned an invalid Content-Range.");
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private async Task PromoteAsync(
        ModelStorePaths paths,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (File.Exists(paths.PayloadPath))
        {
            var existing = await ValidatePayloadAsync(
                paths.PayloadPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            if (existing == PayloadValidation.Valid)
            {
                File.Delete(paths.PartialPath);
                File.Delete(paths.PartialStatePath);
                return;
            }

            throw CreateIntegrityException(existing);
        }

        try
        {
            File.Move(paths.PartialPath, paths.PayloadPath);
            File.Delete(paths.PartialStatePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageWriteFailed,
                "The verified model could not be promoted atomically.",
                exception);
        }
    }

    private async Task<ModelInstallation> ActivateAsync(
        ModelStorePaths paths,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var manifest = new ActiveModelManifest(
            SchemaVersion: 1,
            descriptor.Id,
            descriptor.Sha256,
            descriptor.DownloadSizeBytes,
            descriptor.CatalogVersion,
            descriptor.Format.Token,
            _timeProvider.GetUtcNow());
        await WriteJsonAtomicallyAsync(
            paths.ActiveManifestPath,
            manifest,
            ModelStoreError.ActivationFailed,
            cancellationToken).ConfigureAwait(false);

        return ToInstallation(manifest, paths.PayloadPath);
    }

    private async Task VerifyForActivationAsync(
        string payloadPath,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await _headerVerifier.VerifyAsync(payloadPath, descriptor, cancellationToken).ConfigureAwait(false);
        try
        {
            await _activationVerifier.VerifyCanLoadAsync(
                payloadPath,
                descriptor,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModelStoreException exception) when (exception.Error == ModelStoreError.ActivationFailed)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelStoreException(
                ModelStoreError.ActivationFailed,
                "The model could not be fully loaded through the pinned runtime ABI.",
                exception);
        }
    }

    private async ValueTask<IModelPayloadRetentionLease> AcquireRetentionLeaseAsync(
        string sha256,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _retentionLeaseProvider.AcquireDeletionLeaseAsync(
                sha256,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The retention provider returned no deletion lease.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModelStoreException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelStoreException(
                ModelStoreError.RetentionStateUnavailable,
                "The durable model retention state could not be resolved.",
                exception);
        }
    }

    private async Task<int> RecoverRemovalTombstonesAsync(CancellationToken cancellationToken)
    {
        var activeDirectory = Path.Combine(_rootPath, "active");
        if (!Directory.Exists(activeDirectory))
        {
            return 0;
        }

        var recovered = 0;
        var tombstones = Directory.EnumerateFiles(
            activeDirectory,
            ".removing-*.json",
            SearchOption.TopDirectoryOnly).ToArray();
        foreach (var tombstonePath in tombstones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedManifest = await ReadActiveManifestAsync(
                tombstonePath,
                cancellationToken).ConfigureAwait(false);
            if (observedManifest is null || !IsLowerSha256(observedManifest.Sha256))
            {
                if (!_fileMutator.FileExists(tombstonePath))
                {
                    continue;
                }

                throw new ModelStoreException(
                    ModelStoreError.StorageWriteFailed,
                    "A model removal tombstone is corrupt; orphan deletion is paused for safe recovery.");
            }

            await using var lease = await AcquireRetentionLeaseAsync(
                observedManifest.Sha256,
                cancellationToken).ConfigureAwait(false);
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var manifest = await ReadActiveManifestAsync(
                    tombstonePath,
                    cancellationToken).ConfigureAwait(false);
                if (manifest is null
                    || !string.Equals(manifest.Sha256, observedManifest.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!lease.IsRetained
                    && !await IsHashReferencedAsync(manifest.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        _fileMutator.DeleteFile(GetPayloadPath(manifest.Sha256));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new ModelStoreException(
                            ModelStoreError.StorageWriteFailed,
                            "An interrupted model payload removal could not be recovered.",
                            exception);
                    }
                }

                try
                {
                    _fileMutator.DeleteFile(tombstonePath);
                    recovered++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelStoreException(
                        ModelStoreError.StorageWriteFailed,
                        "A committed model removal tombstone could not be cleaned up.",
                        exception);
                }
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        return recovered;
    }

    private async Task<bool> IsHashReferencedAsync(string sha256, CancellationToken cancellationToken)
    {
        var activeDirectory = Path.Combine(_rootPath, "active");
        if (!Directory.Exists(activeDirectory))
        {
            return false;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(activeDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(manifestPath).StartsWith(".removing-", StringComparison.Ordinal))
            {
                continue;
            }

            var manifest = await ReadActiveManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest is null || !IsLowerSha256(manifest.Sha256))
            {
                if (!_fileMutator.FileExists(manifestPath))
                {
                    continue;
                }

                throw new ModelStoreException(
                    ModelStoreError.StorageWriteFailed,
                    "An active model pointer is corrupt; payload deletion is paused for safe recovery.");
            }

            if (string.Equals(manifest.Sha256, sha256, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<PayloadValidation> ValidatePayloadAsync(
        string path,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return PayloadValidation.Missing;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (file.Length != descriptor.DownloadSizeBytes)
            {
                return PayloadValidation.SizeMismatch;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase)
                ? PayloadValidation.Valid
                : PayloadValidation.HashMismatch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageWriteFailed,
                "The model payload could not be validated.",
                exception);
        }
    }

    private static ModelStoreException CreateIntegrityException(PayloadValidation validation) => validation switch
    {
        PayloadValidation.SizeMismatch => new ModelStoreException(
            ModelStoreError.PayloadSizeMismatch,
            "The downloaded model size does not match the pinned catalog."),
        PayloadValidation.HashMismatch => new ModelStoreException(
            ModelStoreError.PayloadHashMismatch,
            "The downloaded model SHA-256 does not match the pinned catalog."),
        _ => new ModelStoreException(
            ModelStoreError.PayloadHashMismatch,
            "The downloaded model payload is missing after transfer."),
    };

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static Task<ActiveModelManifest?> ReadActiveManifestAsync(
        string path,
        CancellationToken cancellationToken) =>
        ReadJsonAsync<ActiveModelManifest>(path, cancellationToken);

    private static Task<PartialDownloadState?> ReadPartialStateAsync(
        string path,
        CancellationToken cancellationToken) =>
        ReadJsonAsync<PartialDownloadState>(path, cancellationToken);

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        ModelStoreError error,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            File.Delete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporaryPath);
            throw new ModelStoreException(error, "The model store metadata could not be committed atomically.", exception);
        }
    }

    private static void DeletePartialArtifacts(ModelStorePaths paths)
    {
        DeleteFileIfPresent(paths.PartialPath, ModelStoreError.StorageWriteFailed);
        DeleteFileIfPresent(paths.PartialStatePath, ModelStoreError.StorageWriteFailed);
    }

    private static void EnsureStoreDirectories(ModelStorePaths paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PartialPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ActiveManifestPath)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageNotWritable,
                "The app-owned model store directories could not be created.",
                exception);
        }
    }

    private static void DeleteFileIfPresent(string path, ModelStoreError error)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(error, "An invalid model store file could not be removed.", exception);
        }
    }

    private static void TruncatePartial(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageWriteFailed,
                "The partial model file could not be restarted.",
                exception);
        }
    }

    private string GetPayloadPath(string sha256)
    {
        if (!IsLowerSha256(sha256))
        {
            throw new InvalidOperationException("An active model pointer contains an invalid SHA-256.");
        }

        return Path.Combine(_rootPath, "blobs", "sha256", $"{sha256}.bin");
    }

    private void RestoreTombstone(string tombstone, string activePath)
    {
        try
        {
            if (_fileMutator.FileExists(tombstone) && !_fileMutator.FileExists(activePath))
            {
                _fileMutator.MoveFile(tombstone, activePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The tombstone remains app-owned and recoverable; the payload is never deleted after this failure.
        }
    }

    private static ModelInstallation ToInstallation(ActiveModelManifest manifest, string payloadPath) => new(
        manifest.ModelId,
        manifest.Sha256,
        payloadPath,
        manifest.SizeBytes,
        manifest.CatalogVersion,
        manifest.FormatToken,
        manifest.ActivatedAtUtc);

    private static void ValidateDescriptor(WhisperModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Regex.IsMatch(descriptor.Id, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)
            || !IsLowerSha256(descriptor.Sha256)
            || descriptor.DownloadSizeBytes <= sizeof(uint)
            || descriptor.ExpectedMemoryBytes <= 0
            || !descriptor.DownloadUri.IsAbsoluteUri
            || !descriptor.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(descriptor.CatalogVersion)
            || string.IsNullOrWhiteSpace(descriptor.Format.Token))
        {
            throw new ArgumentException("The model descriptor is not safe for acquisition.", nameof(descriptor));
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is not null
        && value.Length == 64
        && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private enum PayloadValidation
    {
        Missing,
        Valid,
        SizeMismatch,
        HashMismatch,
    }

    private sealed record PartialDownloadState(
        int SchemaVersion,
        string ModelId,
        string Sha256,
        long ExpectedSizeBytes,
        string DownloadUrl,
        string FormatToken);

    private sealed record ActiveModelManifest(
        int SchemaVersion,
        string ModelId,
        string Sha256,
        long SizeBytes,
        string CatalogVersion,
        string FormatToken,
        DateTimeOffset ActivatedAtUtc);

    private readonly record struct DownloadPass(
        bool Completed,
        long NetworkBytes,
        bool ServerRestartedRange,
        bool UsedRangeResume);

    private readonly record struct DownloadTransfer(
        long NetworkBytes,
        bool ServerRestartedRange,
        bool UsedRangeResume);

    private sealed class RetryableDownloadException : Exception
    {
        public RetryableDownloadException(
            string message,
            Exception? innerException = null,
            long networkBytes = 0)
            : base(message, innerException)
        {
            NetworkBytes = networkBytes;
        }

        public long NetworkBytes { get; }
    }
}
