using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Freezes an installed model/runtime identity while its durable local job is committed.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public interface ILocalTranscriptionExecutionLeaseProvider
{
    ValueTask<LocalTranscriptionExecutionLease> AcquireExecutionLeaseAsync(
        string modelId,
        string inputSha256,
        string requestedLanguage,
        CancellationToken cancellationToken);
}

/// <summary>
/// Retries collection of inactive model payloads after durable jobs release their frozen hashes.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public interface ILocalModelOrphanCollector
{
    ValueTask CollectRetainedOrphansAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Holds the model deletion/read ordering gate until the caller has committed the durable job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed class LocalTranscriptionExecutionLease : IAsyncDisposable
{
    private readonly IModelPayloadUsageLease _usageLease;

    internal LocalTranscriptionExecutionLease(
        LocalTranscriptionExecutionIdentity identity,
        IModelPayloadUsageLease usageLease)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _usageLease = usageLease ?? throw new ArgumentNullException(nameof(usageLease));
    }

    public LocalTranscriptionExecutionIdentity Identity { get; }

    public ValueTask DisposeAsync() => _usageLease.DisposeAsync();
}

/// <summary>
/// Application-owned local model lifecycle, frozen execution identity and diagnostics cache.
/// Model download is its only network-capable path.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class LocalTranscriptionServices :
    ILocalTranscriptionExecutionLeaseProvider,
    ILocalModelOrphanCollector,
    IAsyncDisposable
{
    private const int ChunkProfileVersion = 1;
    private const long RequiredWorkingDiskBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions IdentitySerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly object _stateSync = new();
    private readonly WhisperModelCatalog _catalog;
    private readonly WhisperModelStore _modelStore;
    private readonly LocalModelRetentionCoordinator _retention;
    private readonly LocalTranscriptionRuntimeOptions _options;
    private readonly HttpClient _modelDownloadHttpClient;
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<string, RuntimeLocalModelSnapshot> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _modelOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeLocalTranscriptionDiagnosticsSnapshot> _diagnostics = new(StringComparer.Ordinal);
    private readonly LocalPowerOverrideStore _powerOverrides = new();
    private TaskCompletionSource _modelOperationsDrained = CompletedOperationsDrain();
    private bool _orphanCollectionPending;
    private long _orphanCollectionGeneration;
    private bool _disposed;

    private LocalTranscriptionServices(
        WhisperModelCatalog catalog,
        WhisperModelStore modelStore,
        LocalModelRetentionCoordinator retention,
        LocalTranscriptionRuntimeOptions options,
        HttpClient modelDownloadHttpClient,
        bool ownsHttpClient)
    {
        _catalog = catalog;
        _modelStore = modelStore;
        _retention = retention;
        _options = options;
        _modelDownloadHttpClient = modelDownloadHttpClient;
        _ownsHttpClient = ownsHttpClient;
        foreach (var descriptor in _catalog.Models)
        {
            _models.Add(descriptor.Id, CreateSnapshot(descriptor, installation: null));
        }
    }

    public event EventHandler? StateChanged;

    public WhisperModelCatalog Catalog => _catalog;

    public ILocalTranscriptionResourcePolicy ResourcePolicy => _options.ResourcePolicy;

    internal LocalPowerOverrideStore PowerOverrides => _powerOverrides;

    public static LocalTranscriptionServices Create(
        string defaultModelStoreRoot,
        TranscriptionJobRepository repository,
        LocalTranscriptionRuntimeOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModelStoreRoot);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var workerClients = options.WorkerClientFactory ?? new CoordinatorLocalWorkerClientFactory();
        var retention = new LocalModelRetentionCoordinator(repository);
        var verifier = new LocalWhisperModelActivationVerifier(options, workerClients);
        var ownsHttpClient = options.ModelDownloadHttpClient is null;
        var httpClient = options.ModelDownloadHttpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var store = new WhisperModelStore(
            options.ModelStoreRoot ?? Path.GetFullPath(defaultModelStoreRoot),
            httpClient,
            verifier,
            retention,
            timeProvider: timeProvider);
        return new LocalTranscriptionServices(
            WhisperModelCatalog.LoadEmbedded(),
            store,
            retention,
            options,
            httpClient,
            ownsHttpClient);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var collectionGeneration = ReadOrphanCollectionGeneration();
        try
        {
            var cleanup = await _modelStore
                .RecoverRemovalsAndCollectOrphansAsync(cancellationToken)
                .ConfigureAwait(false);
            CompleteOrphanCollection(collectionGeneration, cleanup.RetainedOrphanSha256.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            MarkOrphanCollectionPending();

            foreach (var descriptor in _catalog.Models)
            {
                SetFailedModelWithoutNotification(descriptor, "model_download_activation");
            }

            OnStateChanged();
            return;
        }

        foreach (var descriptor in _catalog.Models)
        {
            try
            {
                var installation = await _modelStore
                    .GetActiveInstallationAsync(descriptor, cancellationToken)
                    .ConfigureAwait(false);
                SetModel(CreateSnapshot(descriptor, installation));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetFailedModelWithoutNotification(descriptor, "model_download_activation");
            }
        }

        OnStateChanged();
    }

    public IReadOnlyList<RuntimeLocalModelSnapshot> GetModelSnapshots()
    {
        lock (_stateSync)
        {
            return _catalog.Models.Select(model => _models[model.Id]).ToArray();
        }
    }

    public RuntimeLocalResourceSnapshot GetResourceSnapshot(string? selectedModelId)
    {
        var descriptor = selectedModelId is null
            ? _catalog.RecommendedModel
            : _catalog.Models.FirstOrDefault(model => string.Equals(
                model.Id,
                selectedModelId,
                StringComparison.Ordinal)) ?? _catalog.RecommendedModel;
        var state = ResourcePolicy.CurrentState;
        return new RuntimeLocalResourceSnapshot(
            state.IsLowPowerMode,
            AutomaticDeferred: state.IsLowPowerMode,
            state.AvailableMemoryBytes,
            descriptor.ExpectedMemoryBytes,
            state.AvailableDiskBytes,
            checked(descriptor.DownloadSizeBytes + WhisperModelStoreOptions.DefaultSafetyMarginBytes),
            state.StableBlockCode)
        {
            InstalledMemoryBytes = state.InstalledMemoryBytes
        };
    }

    public async ValueTask InstallAsync(string modelId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var descriptor = _catalog.GetRequired(modelId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterOperation(descriptor.Id, linked);
        try
        {
            SetModel(CreateSnapshot(descriptor, installation: null) with
            {
                State = RuntimeLocalModelState.Downloading,
                Progress = 0,
            });
            OnStateChanged();
            long publishedBytes = 0;
            var publicationStep = Math.Max(1, descriptor.DownloadSizeBytes / 100);
            var progress = new InlineModelProgress(bytes =>
            {
                if (!ShouldPublishModelProgress(
                        bytes,
                        descriptor.DownloadSizeBytes,
                        publicationStep,
                        ref publishedBytes))
                {
                    return;
                }

                var fraction = Math.Clamp((double)bytes / descriptor.DownloadSizeBytes, 0, 1);
                SetModel(CreateSnapshot(descriptor, installation: null) with
                {
                    State = fraction >= 1
                        ? RuntimeLocalModelState.Verifying
                        : RuntimeLocalModelState.Downloading,
                    Progress = fraction,
                });
                OnStateChanged();
            });
            var result = await _modelStore
                .AcquireAsync(descriptor, progress, linked.Token)
                .ConfigureAwait(false);
            MarkOrphanCollectionPending();

            SetModel(CreateSnapshot(descriptor, result.Installation));
            OnStateChanged();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetFailedModel(descriptor, "model_download_cancelled");
            throw;
        }
        catch (ModelStoreException exception)
        {
            SetFailedModel(descriptor, MapModelStoreError(exception.Error));
            throw new TranscriptionCommandException(
                MapModelStoreError(exception.Error),
                "The local transcription model could not be installed.");
        }
        finally
        {
            UnregisterOperation(descriptor.Id, linked);
        }
    }

    public void CancelInstall(string modelId)
    {
        ThrowIfDisposed();
        _ = _catalog.GetRequired(modelId);
        CancellationTokenSource? operation;
        lock (_stateSync)
        {
            _modelOperations.TryGetValue(modelId, out operation);
        }

        operation?.Cancel();
    }

    public async ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var descriptor = _catalog.GetRequired(modelId);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterOperation(descriptor.Id, operation);
        try
        {
            var current = GetModel(descriptor.Id);
            SetModel(current with { State = RuntimeLocalModelState.Removing, StableErrorCode = null });
            OnStateChanged();
            var result = await _modelStore.RemoveActiveAsync(descriptor, operation.Token).ConfigureAwait(false);
            if (result.PayloadRetainedByLease)
            {
                MarkOrphanCollectionPending();
            }

            SetModel(CreateSnapshot(descriptor, installation: null) with
            {
                StableErrorCode = result.PayloadRetainedByLease ? "model_in_use" : null,
            });
            OnStateChanged();
        }
        catch (ModelStoreException exception)
        {
            var code = exception.Error == ModelStoreError.RetentionStateUnavailable
                ? "model_in_use"
                : MapModelStoreError(exception.Error);
            SetFailedModel(descriptor, code);
            throw new TranscriptionCommandException(code, "The local transcription model could not be removed.");
        }
        finally
        {
            UnregisterOperation(descriptor.Id, operation);
        }
    }

    public async ValueTask<LocalTranscriptionExecutionLease> AcquireExecutionLeaseAsync(
        string modelId,
        string inputSha256,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var descriptor = _catalog.GetRequired(modelId);
        var usageLease = await _retention
            .AcquireUsageLeaseAsync(descriptor.Sha256, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var installation = await _modelStore
                .GetActiveInstallationAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
            if (installation is null)
            {
                throw new TranscriptionCommandException(
                    "model_missing",
                    "Install and verify the selected local transcription model first.");
            }

            var inferenceParameters = JsonSerializer.Serialize(
                new LocalInferenceIdentity(
                    Task: "transcribe",
                    Temperature: 0,
                    Language: requestedLanguage,
                    TimestampGranularity: "segment",
                    MaximumChunkMilliseconds: 300_000,
                    OverlapMilliseconds: 2_000),
                IdentitySerializerOptions);
            var runtime = _options.RuntimeIdentity;
            var runIdentity = ComputeRunIdentity(
                inputSha256,
                installation,
                runtime,
                _options.RequestedBackend,
                _options.ThreadCount,
                inferenceParameters);
            var identity = new LocalTranscriptionExecutionIdentity(
                ModelCatalogVersion: _catalog.SchemaVersion,
                ModelCatalogRevision: _catalog.UpstreamRevision,
                ModelFormat: installation.FormatToken,
                ModelPath: installation.PayloadPath,
                ModelSizeBytes: installation.SizeBytes,
                ModelSha256: installation.Sha256,
                RuntimeVersion: runtime.RuntimeVersion,
                RuntimeCommit: runtime.RuntimeCommit,
                RuntimeSourceArchiveSha256: runtime.RuntimeSourceArchiveSha256,
                NativeBundleManifestSha256: runtime.NativeBundleManifestSha256,
                BridgeAbiVersion: runtime.BridgeAbiVersion,
                WorkerProtocolVersion: runtime.WorkerProtocolVersion,
                RequestedBackend: _options.RequestedBackend,
                ResolvedBackend: null,
                ThreadCount: _options.ThreadCount,
                InferenceParametersJson: inferenceParameters,
                ChunkProfileVersion: ChunkProfileVersion,
                RunIdentitySha256: runIdentity);
            return new LocalTranscriptionExecutionLease(identity, usageLease);
        }
        catch
        {
            await usageLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void AuthorizeOneShotPowerOverride(string jobId) => _powerOverrides.Authorize(jobId);

    public void RevokeOneShotPowerOverride(string jobId) => _powerOverrides.Revoke(jobId);

    public bool TryGetDiagnostics(
        string jobId,
        out RuntimeLocalTranscriptionDiagnosticsSnapshot? diagnostics)
    {
        lock (_stateSync)
        {
            return _diagnostics.TryGetValue(jobId, out diagnostics);
        }
    }

    public async ValueTask RefreshDiagnosticsAsync(string jobId, TranscriptionJobRepository repository)
    {
        var local = await repository.GetLocalExecutionAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (local is null)
        {
            return;
        }

        var execution = local.Execution;
        var snapshot = new RuntimeLocalTranscriptionDiagnosticsSnapshot(
            execution.RequestedBackend.ToString().ToLowerInvariant(),
            execution.ResolvedBackend?.ToString().ToLowerInvariant(),
            execution.ThreadCount,
            execution.RuntimeVersion,
            execution.NativeBundleManifestSha256,
            execution.ModelSha256,
            local.ProcessingDurationMilliseconds);
        lock (_stateSync)
        {
            _diagnostics[jobId] = snapshot;
        }

        OnStateChanged();
    }

    public async ValueTask CollectRetainedOrphansAsync(CancellationToken cancellationToken)
    {
        long collectionGeneration;
        lock (_stateSync)
        {
            if (!_orphanCollectionPending)
            {
                return;
            }

            collectionGeneration = _orphanCollectionGeneration;
        }

        var cleanup = await _modelStore
            .RecoverRemovalsAndCollectOrphansAsync(cancellationToken)
            .ConfigureAwait(false);
        CompleteOrphanCollection(collectionGeneration, cleanup.RetainedOrphanSha256.Count > 0);
    }

    private long ReadOrphanCollectionGeneration()
    {
        lock (_stateSync)
        {
            return _orphanCollectionGeneration;
        }
    }

    private void MarkOrphanCollectionPending()
    {
        lock (_stateSync)
        {
            _orphanCollectionPending = true;
            _orphanCollectionGeneration = unchecked(_orphanCollectionGeneration + 1);
        }
    }

    private void CompleteOrphanCollection(long collectionGeneration, bool retainedOrphansRemain)
    {
        lock (_stateSync)
        {
            _orphanCollectionPending = retainedOrphansRemain
                || ShouldKeepOrphanCollectionPending(
                    collectionGeneration,
                    _orphanCollectionGeneration);
        }
    }

    internal static bool ShouldKeepOrphanCollectionPending(
        long collectionGeneration,
        long currentGeneration) => collectionGeneration != currentGeneration;

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource[] operations;
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operations = _modelOperations.Values.ToArray();
        }

        foreach (var operation in operations)
        {
            operation.Cancel();
        }

        Task operationsDrained;
        lock (_stateSync)
        {
            operationsDrained = _modelOperationsDrained.Task;
        }

        await operationsDrained.ConfigureAwait(false);

        await _modelStore.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttpClient)
        {
            _modelDownloadHttpClient.Dispose();
        }
    }

    private void RegisterOperation(string modelId, CancellationTokenSource operation)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_modelOperations.Count == 0)
            {
                _modelOperationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (!_modelOperations.TryAdd(modelId, operation))
            {
                throw new TranscriptionCommandException(
                    "model_operation_in_progress",
                    "A local model operation is already in progress.");
            }
        }
    }

    private void UnregisterOperation(string modelId, CancellationTokenSource operation)
    {
        lock (_stateSync)
        {
            if (_modelOperations.TryGetValue(modelId, out var current)
                && ReferenceEquals(current, operation))
            {
                _modelOperations.Remove(modelId);
                if (_modelOperations.Count == 0)
                {
                    _modelOperationsDrained.TrySetResult();
                }
            }
        }
    }

    private RuntimeLocalModelSnapshot GetModel(string modelId)
    {
        lock (_stateSync)
        {
            return _models[modelId];
        }
    }

    private void SetModel(RuntimeLocalModelSnapshot snapshot)
    {
        lock (_stateSync)
        {
            _models[snapshot.ModelId] = snapshot;
        }
    }

    private void SetFailedModel(WhisperModelDescriptor descriptor, string code)
    {
        SetFailedModelWithoutNotification(descriptor, code);
        OnStateChanged();
    }

    private void SetFailedModelWithoutNotification(WhisperModelDescriptor descriptor, string code) =>
        SetModel(CreateSnapshot(descriptor, installation: null) with
        {
            State = RuntimeLocalModelState.Failed,
            StableErrorCode = code,
        });

    internal static bool ShouldPublishModelProgress(
        long receivedBytes,
        long totalBytes,
        long publicationStep,
        ref long publishedBytes)
    {
        var bounded = Math.Clamp(receivedBytes, 0, totalBytes);
        while (true)
        {
            var observed = Volatile.Read(ref publishedBytes);
            if (bounded <= observed)
            {
                return false;
            }

            if (bounded < totalBytes && bounded - observed < publicationStep)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref publishedBytes, bounded, observed) == observed)
            {
                return true;
            }
        }
    }

    private static TaskCompletionSource CompletedOperationsDrain()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private static RuntimeLocalModelSnapshot CreateSnapshot(
        WhisperModelDescriptor descriptor,
        ModelInstallation? installation) => new(
        descriptor.Id,
        ToDisplayName(descriptor),
        descriptor.DownloadSizeBytes,
        installation?.SizeBytes,
        descriptor.Recommended,
        installation is null ? RuntimeLocalModelState.NotInstalled : RuntimeLocalModelState.Installed,
        installation is null ? 0 : 1,
        installation is not null,
        StableErrorCode: null);

    private static string ToDisplayName(WhisperModelDescriptor descriptor) => descriptor.Id == "large-v3-turbo" ? "Whisper large v3 turbo" : descriptor.UiPreset switch
    {
        "compact" => "Compact (base)",
        "balanced" => "Balanced (small)",
        "accurate" => "Accurate (medium)",
        _ => descriptor.Id,
    };

    private static string MapModelStoreError(ModelStoreError error) => error switch
    {
        ModelStoreError.StorageNotWritable
            or ModelStoreError.InsufficientDiskSpace
            or ModelStoreError.StorageWriteFailed => "model_download_storage",
        ModelStoreError.TransportRejected
            or ModelStoreError.DownloadIncomplete
            or ModelStoreError.DownloadProtocolInvalid => "model_download_network",
        ModelStoreError.PayloadSizeMismatch
            or ModelStoreError.PayloadHashMismatch
            or ModelStoreError.PayloadFormatMismatch => "model_download_integrity",
        ModelStoreError.ActivationFailed => "model_download_activation",
        ModelStoreError.RetentionStateUnavailable => "model_in_use",
        _ => "model_download_failed",
    };

    private static string ComputeRunIdentity(
        string inputSha256,
        ModelInstallation installation,
        LocalWhisperRuntimeIdentity runtime,
        LocalTranscriptionBackend backend,
        int threadCount,
        string inferenceParameters)
    {
        var canonical = string.Join(
            '\n',
            "local.whisper",
            NormalizeSha256(inputSha256),
            installation.ModelId,
            installation.Sha256,
            installation.CatalogVersion,
            installation.FormatToken,
            runtime.RuntimeVersion,
            runtime.RuntimeCommit,
            runtime.RuntimeSourceArchiveSha256,
            runtime.NativeBundleManifestSha256,
            runtime.BridgeAbiVersion.ToString(CultureInfo.InvariantCulture),
            runtime.WorkerProtocolVersion.ToString(CultureInfo.InvariantCulture),
            backend.ToString().ToLowerInvariant(),
            threadCount.ToString(CultureInfo.InvariantCulture),
            ChunkProfileVersion.ToString(CultureInfo.InvariantCulture),
            inferenceParameters);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A source SHA-256 is required.", nameof(value));
        }

        return normalized;
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record LocalInferenceIdentity(
        string Task,
        double Temperature,
        string Language,
        string TimestampGranularity,
        int MaximumChunkMilliseconds,
        int OverlapMilliseconds);

    private sealed class InlineModelProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
