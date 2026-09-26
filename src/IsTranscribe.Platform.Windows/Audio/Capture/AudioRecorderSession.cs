using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio.Capture;

public sealed class AudioRecorderSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly BootstrapFileLogger _logger;
    private readonly AudioCaptureRequest _request;
    private readonly IReadOnlyList<IAudioCaptureAdapter> _adapters;
    private readonly IAudioArtifactPreparer _artifactPreparer;
    private readonly List<AudioCaptureArtifact> _artifacts = [];
    private readonly List<AudioCaptureFault> _faults = [];
    private readonly List<AudioCaptureStopEvent> _stopEvents = [];
    private TaskCompletionSource? _promotionCompletion;
    private TaskCompletionSource? _stopCompletion;
    private bool _disposed;
    private bool _naturalCompletionStarted;
    private bool _stopCompleted;
    private bool _stopStarted;

    internal AudioRecorderSession(
        BootstrapFileLogger logger,
        AudioCaptureRequest request,
        IReadOnlyList<IAudioCaptureAdapter> adapters,
        IAudioArtifactPreparer artifactPreparer,
        bool isPersistingAudio)
    {
        _logger = logger;
        _request = request;
        _adapters = adapters;
        _artifactPreparer = artifactPreparer;
        Snapshot = new AudioRecorderSessionSnapshot(
            request.SessionId,
            AudioRecorderState.Running,
            isPersistingAudio,
            Array.Empty<AudioCaptureArtifact>(),
            Array.Empty<AudioCaptureFault>(),
            Array.Empty<AudioCaptureStopEvent>());

        foreach (var adapter in _adapters)
        {
            adapter.Faulted += HandleAdapterFaulted;
            adapter.Stopped += HandleAdapterStopped;
        }
    }

    public event EventHandler<AudioRecorderSessionSnapshot>? SnapshotChanged;

    public AudioRecorderSessionSnapshot Snapshot { get; private set; }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.pause
    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        foreach (var adapter in _adapters)
        {
            await adapter.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.pause
    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        foreach (var adapter in _adapters)
        {
            await adapter.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#prebuffer.promotion
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public async ValueTask PromotePrebufferAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        Task promotionTask;
        TaskCompletionSource? startedCompletion = null;
        lock (_gate)
        {
            if (Snapshot.IsPersistingAudio || _stopCompletion is not null)
            {
                return;
            }

            if (_promotionCompletion is null)
            {
                _promotionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startedCompletion = _promotionCompletion;
            }

            promotionTask = _promotionCompletion.Task;
        }

        if (startedCompletion is not null)
        {
            _ = RunPromotionCoreAsync(startedCompletion);
        }

        await promotionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        Task stopTask;
        TaskCompletionSource? startedCompletion = null;
        lock (_gate)
        {
            if (_stopCompletion is null)
            {
                _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopStarted = true;
                startedCompletion = _stopCompletion;
            }

            stopTask = _stopCompletion.Task;
        }

        if (startedCompletion is not null)
        {
            _ = RunStopCoreAsync(startedCompletion);
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunStopCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task StopCoreAsync()
    {
        UpdateSnapshot(AudioRecorderState.Stopping, Snapshot.IsPersistingAudio, _artifacts, _faults);
        var promotionTask = GetPromotionTask();
        if (promotionTask is not null)
        {
            try
            {
                await promotionTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not PrebufferPromotionUnavailableException)
                {
                    RegisterPromotionFailure(exception);
                }
            }
        }

        // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
        // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
        var adapterStopTasks = new Task[_adapters.Count];
        for (var index = 0; index < _adapters.Count; index++)
        {
            var adapterIndex = index;
            var adapter = _adapters[index];
            adapterStopTasks[index] = Task.Run(() => StopAdapterAsync(adapterIndex, adapter));
        }

        await Task.WhenAll(adapterStopTasks).ConfigureAwait(false);

        // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
        try
        {
            if (_request.CreateMixedArtifact)
            {
                var mixedArtifact = await _artifactPreparer
                    .PrepareAsync(_request, GetArtifactsSnapshot(), CancellationToken.None)
                    .ConfigureAwait(false);
                if (mixedArtifact is not null)
                {
                    RegisterArtifact(mixedArtifact);
                }
            }
        }
        catch (Exception exception)
        {
            var fault = new AudioCaptureFault(
                DateTimeOffset.UtcNow,
                AudioCaptureFailureKind.IoFailure,
                SourceKind: null,
                Code: "mixed_artifact_build_failed",
                Message: exception.Message,
                ArtifactPath: Path.Combine(_request.TempSessionDirectoryPath, "mix.wav"));

            lock (_gate)
            {
                _faults.Add(fault);
            }

            _logger.Error(exception, $"Mixed artifact build failed for session {_request.SessionId:N}.");
        }

        AudioRecorderState terminalState;
        lock (_gate)
        {
            _stopCompleted = true;
            terminalState = _artifacts.Count > 0 ? AudioRecorderState.Completed : AudioRecorderState.Faulted;
        }

        UpdateSnapshot(terminalState, Snapshot.IsPersistingAudio, _artifacts, _faults);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async Task StopAdapterAsync(int adapterIndex, IAudioCaptureAdapter adapter)
    {
        AudioCaptureArtifact? artifact = null;
        Exception? finalFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                artifact = await adapter.StopAsync(CancellationToken.None).ConfigureAwait(false);
                finalFailure = null;
                break;
            }
            catch (Exception exception)
            {
                finalFailure = exception;
                if (attempt == 1)
                {
                    _logger.Warning(
                        $"Audio capture adapter stop will retry. Session={_request.SessionId:N}, SourceIndex={adapterIndex}, Message={exception.Message}");
                }
            }
        }

        if (finalFailure is not null)
        {
            RegisterStopFailure(adapterIndex, adapter, finalFailure);
        }

        RegisterArtifact(artifact ?? adapter.FinalArtifact);
        RegisterStopEvent(adapter.StopEvent);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async Task RunPromotionCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            var promotionTasks = _adapters
                .Select((adapter, index) => Task.Run(() => PromoteAdapterAsync(index, adapter)))
                .ToArray();
            var attempts = await Task.WhenAll(promotionTasks).ConfigureAwait(false);
            foreach (var attempt in attempts.Where(static attempt =>
                         attempt.Outcome == AudioCapturePromotionOutcome.SourceUnavailable))
            {
                RegisterPromotionUnavailable(attempt);
            }

            if (!attempts.Any(static attempt =>
                    attempt.Outcome is AudioCapturePromotionOutcome.Promoted
                        or AudioCapturePromotionOutcome.AlreadyPersisting))
            {
                UpdateSnapshot(Snapshot.State, isPersistingAudio: false, _artifacts, _faults);
                throw new PrebufferPromotionUnavailableException();
            }

            UpdateSnapshot(Snapshot.State, isPersistingAudio: true, _artifacts, _faults);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task<PromotionAttempt> PromoteAdapterAsync(
        int adapterIndex,
        IAudioCaptureAdapter adapter)
    {
        try
        {
            var outcome = await adapter.PromoteAsync(CancellationToken.None).ConfigureAwait(false);
            return new PromotionAttempt(adapterIndex, outcome, Exception: null);
        }
        catch (Exception exception)
        {
            return new PromotionAttempt(
                adapterIndex,
                AudioCapturePromotionOutcome.SourceUnavailable,
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;

        foreach (var adapter in _adapters)
        {
            adapter.Faulted -= HandleAdapterFaulted;
            adapter.Stopped -= HandleAdapterStopped;
            await adapter.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void HandleAdapterFaulted(object? sender, AudioCaptureFault fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
        }

        UpdateSnapshot(Snapshot.State, Snapshot.IsPersistingAudio, _artifacts, _faults);
    }

    private void HandleAdapterStopped(object? sender, AudioCaptureStopEvent stopEvent)
    {
        RegisterStopEvent(stopEvent);
        if (sender is IAudioCaptureAdapter adapter)
        {
            RegisterArtifact(adapter.FinalArtifact);
        }

        lock (_gate)
        {
            if (_stopStarted || _stopCompleted || _naturalCompletionStarted)
            {
                return;
            }

            if (_adapters.Any(adapter => adapter.IsActive))
            {
                return;
            }

            _naturalCompletionStarted = true;
        }

        _ = FinalizeNaturalCompletionAsync();
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private void UpdateSnapshot(
        AudioRecorderState state,
        bool isPersistingAudio,
        IReadOnlyCollection<AudioCaptureArtifact> artifacts,
        IReadOnlyCollection<AudioCaptureFault> faults)
    {
        AudioRecorderSessionSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new AudioRecorderSessionSnapshot(
                _request.SessionId,
                state,
                isPersistingAudio,
                artifacts.ToArray(),
                faults.ToArray(),
                _stopEvents.ToArray());
            Snapshot = snapshot;
        }

        var subscribers = SnapshotChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<AudioRecorderSessionSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    $"Audio recorder snapshot observer failed for session {_request.SessionId:N}.");
            }
        }
    }

    private void RegisterArtifact(AudioCaptureArtifact? artifact)
    {
        if (artifact is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_artifacts.Any(existing => string.Equals(existing.Path, artifact.Path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _artifacts.Add(artifact);
        }
    }

    private void RegisterStopEvent(AudioCaptureStopEvent? stopEvent)
    {
        if (stopEvent is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopEvents.Any(existing =>
                existing.SourceKind == stopEvent.SourceKind &&
                existing.StopKind == stopEvent.StopKind &&
                string.Equals(existing.Code, stopEvent.Code, StringComparison.Ordinal)))
            {
                return;
            }

            _stopEvents.Add(stopEvent);
        }
    }

    private void RegisterStopFailure(int adapterIndex, IAudioCaptureAdapter adapter, Exception exception)
    {
        var sourceKind = adapterIndex < _request.Sources.Count
            ? _request.Sources[adapterIndex].Kind
            : adapter.StopEvent?.SourceKind;
        var fault = new AudioCaptureFault(
            DateTimeOffset.UtcNow,
            AudioCaptureFailureClassifier.Classify(exception),
            sourceKind,
            "capture_stop_failed",
            exception.Message,
            adapter.FinalArtifact?.Path);

        lock (_gate)
        {
            _faults.Add(fault);
        }

        _logger.Error(
            exception,
            $"Audio capture adapter stop failed for session {_request.SessionId:N}, source {sourceKind?.ToString() ?? "unknown"}.");
    }

    private IReadOnlyList<AudioCaptureArtifact> GetArtifactsSnapshot()
    {
        lock (_gate)
        {
            return _artifacts.ToArray();
        }
    }

    private Task? GetPromotionTask()
    {
        lock (_gate)
        {
            return _promotionCompletion?.Task;
        }
    }

    private void RegisterPromotionFailure(Exception exception)
    {
        var fault = new AudioCaptureFault(
            DateTimeOffset.UtcNow,
            AudioCaptureFailureClassifier.Classify(exception),
            SourceKind: null,
            Code: "prebuffer_promotion_failed",
            Message: exception.Message,
            ArtifactPath: _request.TempSessionDirectoryPath);

        lock (_gate)
        {
            _faults.Add(fault);
        }

        _logger.Error(exception, $"Audio prebuffer promotion failed for session {_request.SessionId:N}.");
    }

    private void RegisterPromotionUnavailable(PromotionAttempt attempt)
    {
        var sourceKind = attempt.AdapterIndex < _request.Sources.Count
            ? _request.Sources[attempt.AdapterIndex].Kind
            : (AudioCaptureSourceKind?)null;
        var artifactPath = sourceKind switch
        {
            AudioCaptureSourceKind.Microphone => Path.Combine(_request.TempSessionDirectoryPath, "mic.wav"),
            AudioCaptureSourceKind.ProcessOutput or AudioCaptureSourceKind.DeviceLoopback =>
                Path.Combine(_request.TempSessionDirectoryPath, "output.wav"),
            _ => _request.TempSessionDirectoryPath
        };
        var fault = new AudioCaptureFault(
            DateTimeOffset.UtcNow,
            attempt.Exception is null
                ? AudioCaptureFailureKind.UnexpectedRuntimeFailure
                : AudioCaptureFailureClassifier.Classify(attempt.Exception),
            sourceKind,
            attempt.Exception is null
                ? "prebuffer_source_unavailable"
                : "prebuffer_source_promotion_failed",
            attempt.Exception?.Message ?? "Capture source stopped before buffered audio could be promoted.",
            artifactPath);

        lock (_gate)
        {
            _faults.Add(fault);
        }

        if (attempt.Exception is not null)
        {
            _logger.Error(
                attempt.Exception,
                $"Audio prebuffer source promotion failed for session {_request.SessionId:N}, source {sourceKind?.ToString() ?? "unknown"}.");
        }
        else
        {
            _logger.Warning(
                $"Audio prebuffer source became unavailable before promotion. Session={_request.SessionId:N}, Source={sourceKind?.ToString() ?? "unknown"}.");
        }
    }

    private async Task FinalizeNaturalCompletionAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var fault = new AudioCaptureFault(
                DateTimeOffset.UtcNow,
                AudioCaptureFailureKind.UnexpectedRuntimeFailure,
                SourceKind: null,
                Code: "session_natural_completion_failed",
                Message: exception.Message);

            lock (_gate)
            {
                _faults.Add(fault);
            }

            _logger.Error(exception, $"Audio recorder natural completion failed for session {_request.SessionId:N}.");
            UpdateSnapshot(AudioRecorderState.Faulted, Snapshot.IsPersistingAudio, _artifacts, _faults);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record PromotionAttempt(
        int AdapterIndex,
        AudioCapturePromotionOutcome Outcome,
        Exception? Exception);

    private sealed class PrebufferPromotionUnavailableException()
        : InvalidOperationException("No capture source remained available for buffered-audio promotion.");
}
