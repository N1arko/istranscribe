using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Audio;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows.Audio.Finalization;
using NAudio.Wave;
using CoreCaptureMode = IsTranscribe.Core.Audio.AudioCaptureMode;
using CoreCaptureRequest = IsTranscribe.Core.Audio.AudioCaptureRequest;
using CoreCaptureSource = IsTranscribe.Core.Audio.AudioCaptureSource;
using CoreCaptureSourceKind = IsTranscribe.Core.Audio.AudioCaptureSourceKind;
using CoreArtifactKind = IsTranscribe.Core.Audio.AudioCaptureArtifactKind;
using HostArtifact = IsTranscribe.Host.Audio.Capture.AudioCaptureArtifact;
using HostArtifactKind = IsTranscribe.Host.Audio.Capture.AudioCaptureArtifactKind;
using HostCaptureMode = IsTranscribe.Host.Audio.Capture.AudioCaptureMode;
using HostCaptureRequest = IsTranscribe.Host.Audio.Capture.AudioCaptureRequest;
using HostCaptureSource = IsTranscribe.Host.Audio.Capture.AudioCaptureSourceRequest;

namespace IsTranscribe.Platform.Windows.Audio.Recording;

/// <summary>
/// Owns the one recording lifecycle shared by Ask-confirmed and manual sessions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#scope.in
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance
/// </remarks>
internal sealed class WindowsRecordingSessionCoordinator : IRecordingSessionCoordinator
{
    private static readonly string[] ActiveStatuses =
        ["recording", "paused", "stopping", "processing", "verifying", "promoting"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _handledCaptureFailures = new();
    private readonly IAudioPlatform _audioPlatform;
    private readonly MeetingSessionRepository _repository;
    private readonly ArtifactPathResolver _pathResolver;
    private readonly Func<ApplicationSettings> _settingsAccessor;
    private readonly IAudioArtifactPreparer _artifactPreparer;
    private readonly WindowsRecordingArtifactFinalizer _finalizer;
    private readonly BootstrapFileLogger _logger;
    private readonly TimeProvider _timeProvider;
    private ActiveRecording? _active;
    private bool _finalizing;
    private volatile bool _disposeRequested;
    private bool _disposed;

    public WindowsRecordingSessionCoordinator(
        IAudioPlatform audioPlatform,
        MeetingSessionRepository repository,
        ArtifactPathResolver pathResolver,
        Func<ApplicationSettings> settingsAccessor,
        IAudioArtifactPreparer artifactPreparer,
        WindowsRecordingArtifactFinalizer finalizer,
        BootstrapFileLogger logger,
        TimeProvider? timeProvider = null)
    {
        _audioPlatform = audioPlatform;
        _repository = repository;
        _pathResolver = pathResolver;
        _settingsAccessor = settingsAccessor;
        _artifactPreparer = artifactPreparer;
        _finalizer = finalizer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _audioPlatform.SnapshotChanged += HandleAudioPlatformSnapshotChanged;
    }

    public event EventHandler<RecordingCoordinatorSnapshot>? SnapshotChanged;

    public bool IsBusy => _active is not null || _finalizing;

    public Guid? ActiveSessionId => _active?.SessionId;

    public async ValueTask<bool> StartAskAsync(
        string sourceLabel,
        int rootProcessId,
        string processName,
        CancellationToken cancellationToken) =>
        await StartAsync(
            new RecordingStartContext(
                CoreCaptureMode.Ask,
                sourceLabel,
                rootProcessId,
                processName),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> StartManualAsync(CancellationToken cancellationToken) =>
        await StartAsync(
            new RecordingStartContext(
                CoreCaptureMode.Manual,
                "Manual recording",
                RootProcessId: null,
                ProcessName: null),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask PauseOrResumeAsync(CancellationToken cancellationToken)
    {
        var evaluateContinuity = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_disposeRequested)
            {
                return;
            }

            if (_active is not { } active)
            {
                return;
            }

            if (active.IsPaused)
            {
                await active.CaptureSession.ResumeAsync(cancellationToken).ConfigureAwait(false);
                var resumedAtUtc = _timeProvider.GetUtcNow();
                _repository.TryTransitionArtifactStage(
                    active.SessionId.ToString("N"),
                    ["paused"],
                    "recording",
                    0,
                    resumedAtUtc);
                _active = active with
                {
                    IsPaused = false,
                    ActiveSegmentStartedAtUtc = resumedAtUtc
                };
                evaluateContinuity = true;
            }
            else
            {
                await active.CaptureSession.PauseAsync(cancellationToken).ConfigureAwait(false);
                var pausedAtUtc = _timeProvider.GetUtcNow();
                _repository.TryTransitionArtifactStage(
                    active.SessionId.ToString("N"),
                    ["recording"],
                    "paused",
                    0,
                    pausedAtUtc);
                _active = active with
                {
                    IsPaused = true,
                    AccumulatedActiveDuration = CalculateActiveDuration(active, pausedAtUtc),
                    ActiveSegmentStartedAtUtc = null
                };
            }

            PublishRecording(_active);
        }
        finally
        {
            _gate.Release();
        }

        if (evaluateContinuity)
        {
            HandleAudioPlatformSnapshotChanged(this, _audioPlatform.Snapshot);
        }
    }

    public async ValueTask<RecordingArtifactFinalizationResult?> FinishAsync(
        CancellationToken cancellationToken)
    {
        ActiveRecording? finishingActive = null;
        IReadOnlyList<AudioCaptureArtifactSnapshot> recoverableArtifacts = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_active is not { } active || _finalizing)
            {
                return null;
            }

            _finalizing = true;
            _active = null;
            finishingActive = active;
            active.CaptureSession.SnapshotChanged -= HandleCaptureSnapshotChanged;
            var sessionId = active.SessionId.ToString("N");
            _repository.TryTransitionArtifactStage(
                sessionId,
                ["recording", "paused"],
                "stopping",
                0,
                DateTimeOffset.UtcNow);
            PublishStage(active.SessionId, RecordingArtifactStage.Stopping, recoverablePath: null);

            IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts;
            Exception? stopFailure = null;
            try
            {
                await active.CaptureSession.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
                _logger.Error(exception, $"Capture stop failed for session {active.SessionId:N}.");
            }

            artifacts = active.CompletedArtifacts
                .Concat(ApplyLegOffset(
                    active.CaptureSession.Snapshot.Artifacts,
                    active.CurrentLegBaseOffset))
                .ToArray();
            recoverableArtifacts = artifacts;
            await active.CaptureSession.DisposeAsync().ConfigureAwait(false);
            if (artifacts.Count == 0)
            {
                var errorMessage = stopFailure?.Message ?? "No readable capture source was finalized.";
                _repository.TryMarkArtifactAttentionRequired(
                    sessionId,
                    ActiveStatuses,
                    "capture_artifact_missing",
                    errorMessage,
                    DateTimeOffset.UtcNow);
                var missingResult = RecordingArtifactFinalizationResult.AttentionRequired(
                    "capture_artifact_missing",
                    errorMessage,
                    recoverableAudioPath: null);
                PublishAttention(active.SessionId, errorMessage, recoverablePath: null);
                return missingResult;
            }

            artifacts = await EnsurePreparedArtifactAsync(
                active.SessionId,
                active.TempSessionPath,
                artifacts,
                cancellationToken).ConfigureAwait(false);
            recoverableArtifacts = artifacts;

            var manifestPath = await RecordingSourceManifestStore.WriteAsync(
                active.TempSessionPath,
                active.SessionId,
                artifacts,
                cancellationToken).ConfigureAwait(false);
            var endedAtUtc = DateTimeOffset.UtcNow;
            var preparedDuration = GetPreparedSourceDuration(artifacts)
                ?? throw new InvalidDataException("Prepared recording source has no readable duration.");
            var durationSeconds = preparedDuration.TotalSeconds;
            var checkpoint = CreateCheckpoint(
                "processing",
                active.TempSessionPath,
                manifestPath,
                active.StagedPrimaryPath,
                artifacts,
                durationSeconds,
                artifactProgress: 0);
            if (!_repository.TryCheckpointArtifact(
                    sessionId,
                    ["stopping", "processing"],
                    checkpoint,
                    endedAtUtc))
            {
                throw new InvalidOperationException("Recording source checkpoint could not be persisted.");
            }

            var result = await FinalizeArtifactsAsync(
                active.SessionId,
                artifacts,
                active.TempSessionPath,
                manifestPath,
                active.StagedPrimaryPath,
                active.FinalPrimaryPath,
                durationSeconds,
                cancellationToken).ConfigureAwait(false);

            if (result.IsReady)
            {
                CleanupSources(
                    sessionId,
                    active.TempSessionPath,
                    active.FinalPrimaryPath,
                    artifacts,
                    manifestPath,
                    active.StagedPrimaryPath);
                PublishReady(active.SessionId, result.PrimaryAudioPath!);
            }
            else
            {
                _repository.TryMarkArtifactAttentionRequired(
                    sessionId,
                    ActiveStatuses.Append("attention_required").ToArray(),
                    result.ErrorCode ?? "artifact_finalization_failed",
                    result.ErrorMessage ?? "Recording artifact finalization requires attention.",
                    DateTimeOffset.UtcNow);
                PublishAttention(
                    active.SessionId,
                    result.ErrorMessage ?? "Исходное аудио сохранено для восстановления.",
                    result.RecoverableAudioPath);
            }

            return result;
        }
        catch (Exception exception) when (finishingActive is not null)
        {
            _logger.Error(
                exception,
                $"Recording finalization was interrupted for session {finishingActive.SessionId:N}.");
            try
            {
                await finishingActive.CaptureSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
                recoverableArtifacts = finishingActive.CompletedArtifacts
                    .Concat(ApplyLegOffset(
                        finishingActive.CaptureSession.Snapshot.Artifacts,
                        finishingActive.CurrentLegBaseOffset))
                    .ToArray();
                await finishingActive.CaptureSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                _logger.Error(
                    stopException,
                    $"Interrupted capture could not be closed cleanly for session {finishingActive.SessionId:N}.");
            }

            var errorCode = exception is OperationCanceledException
                ? "artifact_finalization_cancelled"
                : "artifact_finalization_failed";
            var errorMessage = exception.Message;
            _repository.TryMarkArtifactAttentionRequired(
                finishingActive.SessionId.ToString("N"),
                ActiveStatuses.Append("attention_required").ToArray(),
                errorCode,
                errorMessage,
                DateTimeOffset.UtcNow);
            var recoverablePath = SelectReadableArtifact(recoverableArtifacts);
            PublishAttention(finishingActive.SessionId, errorMessage, recoverablePath);
            return RecordingArtifactFinalizationResult.AttentionRequired(
                errorCode,
                errorMessage,
                recoverablePath);
        }
        finally
        {
            _finalizing = false;
            _gate.Release();
        }
    }

    async ValueTask<bool> IRecordingSessionCoordinator.FinishForRuntimeAsync(
        CancellationToken cancellationToken) =>
        await FinishAsync(cancellationToken).ConfigureAwait(false) is not null;

    // The durable `discarding` checkpoint is written before capture is stopped or files are
    // removed. A crash therefore leaves the temp path available as a recoverable recent row.
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public async ValueTask<bool> DiscardAsync(CancellationToken cancellationToken)
    {
        ActiveRecording? discardingActive = null;
        IReadOnlyList<AudioCaptureArtifactSnapshot> recoverableArtifacts = [];
        var discardCheckpointed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_disposeRequested || _active is not { } active || _finalizing)
            {
                return false;
            }

            var discardedAtUtc = DateTimeOffset.UtcNow;
            var durationSeconds = Math.Max(0, (discardedAtUtc - active.StartedAtUtc).TotalSeconds);
            var sessionId = active.SessionId.ToString("N");
            if (!_repository.TryBeginActiveDiscard(sessionId, discardedAtUtc, durationSeconds))
            {
                return false;
            }

            discardCheckpointed = true;
            _finalizing = true;
            _active = null;
            discardingActive = active;
            active.CaptureSession.SnapshotChanged -= HandleCaptureSnapshotChanged;
            PublishStage(active.SessionId, RecordingArtifactStage.Stopping, recoverablePath: null);

            try
            {
                await active.CaptureSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Warning(
                    $"Capture stop reported an error while discarding session {sessionId}: {exception.Message}");
            }

            recoverableArtifacts = active.CompletedArtifacts
                .Concat(ApplyLegOffset(
                    active.CaptureSession.Snapshot.Artifacts,
                    active.CurrentLegBaseOffset))
                .ToArray();
            await active.CaptureSession.DisposeAsync().ConfigureAwait(false);

            if (!IsCanonicalTempSessionPath(sessionId, NormalizeDirectory(active.TempSessionPath)))
            {
                throw new InvalidOperationException(
                    "The active recording temporary directory is outside its canonical app-owned path.");
            }

            RecordingArtifactDeletionService.DeleteDirectoryWithoutFollowingReparsePoints(
                active.TempSessionPath);
            if (!_repository.TryCompleteActiveDiscard(sessionId, DateTimeOffset.UtcNow))
            {
                throw new InvalidOperationException(
                    "The completed recording discard could not be checkpointed.");
            }

            PublishDiscarded();
            _logger.LogEvent(
                "Info",
                "RECORDING_DISCARDED",
                "A local recording was discarded after explicit user confirmation.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId
                });
            return true;
        }
        catch (Exception exception) when (discardCheckpointed && discardingActive is not null)
        {
            _logger.Error(
                exception,
                $"Recording discard requires recovery for session {discardingActive.SessionId:N}.");
            _repository.TryMarkActiveDiscardAttention(
                discardingActive.SessionId.ToString("N"),
                exception.Message,
                DateTimeOffset.UtcNow);
            PublishAttention(
                discardingActive.SessionId,
                exception.Message,
                SelectReadableArtifact(recoverableArtifacts));
            return false;
        }
        finally
        {
            _finalizing = false;
            _gate.Release();
        }
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _finalizing = true;
            foreach (var workItem in _repository.ListRecoverableArtifactWork())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(workItem.Id, "N", out var sessionId))
                    {
                        continue;
                    }

                    if (string.Equals(workItem.Status, "ready", StringComparison.Ordinal))
                    {
                        CleanupSources(
                            workItem.Id,
                            workItem.TempSessionPath,
                            workItem.PrimaryAudioPath,
                            BuildFallbackArtifacts(workItem),
                            workItem.SourceManifestPath,
                            workItem.StagedPrimaryPath);
                        continue;
                    }

                    var settings = _settingsAccessor();
                    var finalPath = _pathResolver.GetPrimaryAudioFilePath(
                        settings,
                        sessionId,
                        workItem.CreatedAtUtc,
                        workItem.SourceApp ?? "Recording");
                    var stagedPath = string.IsNullOrWhiteSpace(workItem.StagedPrimaryPath)
                        ? _pathResolver.GetStagedPrimaryAudioFilePath(
                            settings,
                            sessionId,
                            workItem.CreatedAtUtc,
                            workItem.SourceApp ?? "Recording")
                        : workItem.StagedPrimaryPath;
                    var tempSessionPath = workItem.TempSessionPath
                        ?? ResolveTempDirectory(workItem);
                    var artifacts = RecordingSourceManifestStore.Read(workItem.SourceManifestPath);
                    if (artifacts.Count == 0)
                    {
                        artifacts = BuildFallbackArtifacts(workItem);
                    }

                    artifacts = MergeArtifacts(
                        artifacts,
                        RecordingSourceManifestStore.DiscoverContinuityLegArtifacts(
                            tempSessionPath,
                            sessionId));

                    artifacts = await EnsurePreparedArtifactAsync(
                        sessionId,
                        tempSessionPath,
                        artifacts,
                        cancellationToken).ConfigureAwait(false);
                    var manifestPath = workItem.SourceManifestPath;
                    if (artifacts.Count > 0)
                    {
                        manifestPath = await RecordingSourceManifestStore.WriteAsync(
                            tempSessionPath,
                            sessionId,
                            artifacts,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var durationSeconds = GetPreparedSourceDuration(artifacts)?.TotalSeconds
                        ?? workItem.DurationSeconds;
                    if (!_repository.TryCheckpointArtifact(
                            workItem.Id,
                            [workItem.Status, "stopping", "processing", "verifying", "promoting"],
                            CreateCheckpoint(
                                "processing",
                                tempSessionPath,
                                manifestPath,
                                stagedPath,
                                artifacts,
                                durationSeconds,
                                artifactProgress: 0),
                            DateTimeOffset.UtcNow))
                    {
                        throw new InvalidOperationException(
                            $"Recovery checkpoint could not be persisted for session {workItem.Id}.");
                    }

                    PublishStage(sessionId, RecordingArtifactStage.Processing, SelectReadableArtifact(artifacts));

                    var result = await FinalizeArtifactsAsync(
                        sessionId,
                        artifacts,
                        tempSessionPath,
                        manifestPath,
                        stagedPath,
                        finalPath,
                        durationSeconds,
                        cancellationToken).ConfigureAwait(false);
                    // A shutdown-cancelled recovery keeps its last durable processing checkpoint so the
                    // next launch can claim it again. Permanent finalization failures remain terminal attention rows.
                    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression
                    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
                    if (!result.IsReady
                        && cancellationToken.IsCancellationRequested
                        && string.Equals(
                            result.ErrorCode,
                            "artifact_finalization_cancelled",
                            StringComparison.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (result.IsReady)
                    {
                        CleanupSources(
                            workItem.Id,
                            tempSessionPath,
                            result.PrimaryAudioPath,
                            artifacts,
                            manifestPath,
                            stagedPath);
                        PublishReady(sessionId, result.PrimaryAudioPath!);
                    }
                    else
                    {
                        _repository.TryMarkArtifactAttentionRequired(
                            workItem.Id,
                            ActiveStatuses.Append("attention_required").ToArray(),
                            result.ErrorCode ?? "artifact_recovery_failed",
                            result.ErrorMessage ?? "Recording artifact recovery requires attention.",
                            DateTimeOffset.UtcNow);
                        PublishAttention(sessionId, result.ErrorMessage, result.RecoverableAudioPath);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, $"Artifact recovery failed for session {workItem.Id}.");
                    _repository.TryMarkArtifactAttentionRequired(
                        workItem.Id,
                        ActiveStatuses.Append(workItem.Status).Append("attention_required").ToArray(),
                        "artifact_recovery_failed",
                        string.IsNullOrWhiteSpace(exception.Message)
                            ? "Recording artifact recovery failed."
                            : exception.Message,
                        DateTimeOffset.UtcNow);
                    if (Guid.TryParseExact(workItem.Id, "N", out var failedSessionId))
                    {
                        PublishAttention(
                            failedSessionId,
                            "Не удалось восстановить эту запись; остальные записи продолжают обрабатываться.",
                            SelectReadableArtifact(BuildFallbackArtifacts(workItem)));
                    }
                }
            }
        }
        finally
        {
            _finalizing = false;
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        var ownsDisposal = false;
        lock (_disposeGate)
        {
            if (!_disposeRequested)
            {
                _disposeRequested = true;
                ownsDisposal = true;
            }
        }

        return ownsDisposal
            ? new ValueTask(DisposeCoreAsync())
            : new ValueTask(_disposeCompletion.Task);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async Task DisposeCoreAsync()
    {
        try
        {
            await FinishAsync(CancellationToken.None).ConfigureAwait(false);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _disposed = true;
                _audioPlatform.SnapshotChanged -= HandleAudioPlatformSnapshotChanged;
            }
            finally
            {
                _gate.Release();
            }

            _gate.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async ValueTask<bool> StartAsync(
        RecordingStartContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_disposeRequested)
            {
                return false;
            }

            if (_active is not null || _finalizing)
            {
                return false;
            }

            var settings = _settingsAccessor();
            var startedAtUtc = _timeProvider.GetUtcNow();
            var sessionId = Guid.NewGuid();
            var tempSessionPath = _pathResolver.GetTempSessionDirectoryPath(settings, sessionId);
            var finalPrimaryPath = _pathResolver.GetPrimaryAudioFilePath(
                settings,
                sessionId,
                startedAtUtc,
                context.SourceLabel);
            var stagedPrimaryPath = _pathResolver.GetStagedPrimaryAudioFilePath(
                settings,
                sessionId,
                startedAtUtc,
                context.SourceLabel);
            var preferredSources = SelectSources(context, settings);
            if (preferredSources.Count == 0)
            {
                PublishAttention(
                    sessionId,
                    "Не найден доступный источник звука для записи.",
                    recoverablePath: null);
                return false;
            }

            Directory.CreateDirectory(tempSessionPath);
            var initialRecord = CreateRecordingRecord(
                context,
                sessionId,
                startedAtUtc,
                tempSessionPath,
                stagedPrimaryPath,
                preferredSources);
            await _repository.UpsertAsync(initialRecord, cancellationToken).ConfigureAwait(false);

            IAudioCaptureSession? captureSession = null;
            CoreCaptureRequest? captureRequest = null;
            Exception? lastFailure = null;
            foreach (var sources in BuildCaptureAttempts(preferredSources))
            {
                captureRequest = new CoreCaptureRequest(
                    sessionId,
                    context.Mode,
                    sources,
                    tempSessionPath,
                    PrebufferSeconds: 0,
                    CreateMixedArtifact: false);
                try
                {
                    captureSession = await _audioPlatform.StartCaptureAsync(
                        captureRequest,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    _logger.Warning(
                        $"Recording source plan failed for session {sessionId:N}: {exception.Message}");
                }
            }

            if (captureSession is null || captureRequest is null)
            {
                var message = lastFailure?.Message ?? "Audio capture could not be started.";
                _repository.TryMarkArtifactAttentionRequired(
                    sessionId.ToString("N"),
                    ["recording"],
                    "capture_start_failed",
                    message,
                    DateTimeOffset.UtcNow);
                PublishAttention(sessionId, message, recoverablePath: null);
                return false;
            }

            var actualRecord = CreateRecordingRecord(
                context,
                sessionId,
                startedAtUtc,
                tempSessionPath,
                stagedPrimaryPath,
                captureRequest.Sources);
            try
            {
                await _repository.UpsertAsync(actualRecord, cancellationToken).ConfigureAwait(false);
                _active = new ActiveRecording(
                    sessionId,
                    context,
                    startedAtUtc,
                    tempSessionPath,
                    stagedPrimaryPath,
                    finalPrimaryPath,
                    captureRequest,
                    captureSession,
                    IsPaused: false,
                    AccumulatedActiveDuration: TimeSpan.Zero,
                    ActiveSegmentStartedAtUtc: startedAtUtc,
                    LegIndex: 0,
                    CurrentLegBaseOffset: TimeSpan.Zero,
                    CompletedArtifacts: []);
            }
            catch
            {
                await StopAndDisposeUnownedCaptureAsync(captureSession).ConfigureAwait(false);
                _repository.TryMarkArtifactAttentionRequired(
                    sessionId.ToString("N"),
                    ["recording"],
                    "capture_handoff_failed",
                    "Capture started but its ownership checkpoint could not be persisted.",
                    DateTimeOffset.UtcNow);
                throw;
            }

            captureSession.SnapshotChanged += HandleCaptureSnapshotChanged;
            PublishRecording(_active);
            // The capture can reach a terminal state before ownership persistence and event subscription finish.
            // Re-reading after subscription closes that handoff window; duplicate terminal observations are idempotent.
            // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
            HandleCaptureSnapshotChanged(captureSession, captureSession.Snapshot);
            _logger.LogEvent(
                "Info",
                "RECORDING_CAPTURE_STARTED",
                "Local recording capture started.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId.ToString("N"),
                    ["mode"] = context.Mode.ToString().ToLowerInvariant(),
                    ["source_kinds"] = captureRequest.Sources.Select(static source => source.Kind.ToString()).ToArray(),
                    ["capability_issue"] = GetRecordingCapabilityIssue(_active)?.ToString()
                });
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecordingArtifactFinalizationResult> FinalizeArtifactsAsync(
        Guid sessionId,
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        string tempSessionPath,
        string? manifestPath,
        string stagedPrimaryPath,
        string finalPrimaryPath,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var expectedDuration = GetPreparedSourceDuration(artifacts)
            ?? (durationSeconds is > 0
                ? TimeSpan.FromSeconds(durationSeconds.Value)
                : throw new InvalidDataException("Expected prepared-source duration is unavailable."));
        return await _finalizer.FinalizeAsync(
            new RecordingArtifactFinalizationRequest(
                sessionId,
                artifacts,
                stagedPrimaryPath,
                finalPrimaryPath,
                expectedDuration),
            (stage, recoverablePath, progress, _) =>
            {
                var sessionKey = sessionId.ToString("N");
                var nowUtc = DateTimeOffset.UtcNow;
                var normalizedProgress = progress ?? 0;
                var changed = stage switch
                {
                    RecordingArtifactStage.Processing => _repository.TryCheckpointArtifact(
                        sessionKey,
                        ["stopping", "processing"],
                        CreateCheckpoint(
                            "processing",
                            tempSessionPath,
                            manifestPath,
                            stagedPrimaryPath,
                            artifacts,
                            durationSeconds,
                            normalizedProgress),
                        nowUtc),
                    RecordingArtifactStage.Verifying => _repository.TryTransitionArtifactStage(
                        sessionKey,
                        ["processing", "verifying"],
                        "verifying",
                        normalizedProgress,
                        nowUtc),
                    RecordingArtifactStage.Promoting => _repository.TryTransitionArtifactStage(
                        sessionKey,
                        ["verifying", "promoting"],
                        "promoting",
                        normalizedProgress,
                        nowUtc),
                    RecordingArtifactStage.Ready => _repository.TryMarkArtifactReady(
                        sessionKey,
                        ActiveStatuses.Append("ready").ToArray(),
                        finalPrimaryPath,
                        nowUtc),
                    RecordingArtifactStage.AttentionRequired => true,
                    RecordingArtifactStage.Stopping => true,
                    _ => false
                };
                if (!changed)
                {
                    throw new InvalidOperationException(
                        $"Artifact checkpoint '{stage}' could not be persisted for session {sessionId:N}.");
                }

                PublishStage(sessionId, stage, recoverablePath, progress);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AudioCaptureArtifactSnapshot>> EnsurePreparedArtifactAsync(
        Guid sessionId,
        string tempSessionPath,
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts.Any(static artifact =>
                artifact.Kind == CoreArtifactKind.Mixed && IsReadableFile(artifact.Path)))
        {
            return artifacts;
        }

        var raw = artifacts
            .Where(static artifact => artifact.Kind is CoreArtifactKind.Output or CoreArtifactKind.Microphone)
            .Where(static artifact => IsReadableFile(artifact.Path))
            .ToArray();
        if (raw.Length == 0)
        {
            return artifacts;
        }

        var hostArtifacts = raw.Select(static artifact => new HostArtifact(
            artifact.Kind == CoreArtifactKind.Microphone
                ? HostArtifactKind.Microphone
                : HostArtifactKind.Output,
            artifact.Path,
            artifact.BytesWritten,
            artifact.FinalizedAtUtc,
            artifact.RelativeStartOffset)).ToArray();
        var hostSources = raw.Select(static artifact =>
            artifact.Kind == CoreArtifactKind.Microphone
                ? HostCaptureSource.Microphone("recovery")
                : HostCaptureSource.DeviceLoopback("recovery")).ToArray();
        var prepared = await _artifactPreparer.PrepareAsync(
            new HostCaptureRequest(
                sessionId,
                HostCaptureMode.Manual,
                hostSources,
                tempSessionPath,
                PrebufferSeconds: 0,
                CreateMixedArtifact: true),
            hostArtifacts,
            cancellationToken).ConfigureAwait(false);
        if (prepared is null)
        {
            return artifacts;
        }

        return artifacts
            .Where(static artifact => artifact.Kind != CoreArtifactKind.Mixed)
            .Append(new AudioCaptureArtifactSnapshot(
                CoreArtifactKind.Mixed,
                prepared.Path,
                prepared.BytesWritten,
                prepared.FinalizedAtUtc,
                prepared.RelativeStartOffset))
            .ToArray();
    }

    private IReadOnlyList<CoreCaptureSource> SelectSources(
        RecordingStartContext context,
        ApplicationSettings settings)
    {
        var snapshot = _audioPlatform.Snapshot;
        var sources = new List<CoreCaptureSource>(capacity: 2);
        if (context.Mode == CoreCaptureMode.Ask
            && context.RootProcessId is > 0
            && snapshot.Capabilities.SupportsProcessOutputCapture)
        {
            sources.Add(CoreCaptureSource.ProcessOutput(
                context.RootProcessId.Value,
                context.ProcessName));
        }
        else
        {
            var outputDevice = ResolveOutputDevice(snapshot, context.RootProcessId);
            if (outputDevice is not null)
            {
                sources.Add(CoreCaptureSource.DeviceLoopback(outputDevice.Id));
            }
        }

        var microphone = ResolveMicrophone(snapshot, settings);
        if (microphone is not null)
        {
            sources.Add(CoreCaptureSource.Microphone(microphone.Id));
        }

        return sources;
    }

    private static AudioEndpointSnapshot? ResolveOutputDevice(
        AudioPlatformSnapshot snapshot,
        int? rootProcessId)
    {
        var signaledDeviceId = rootProcessId is > 0
            ? snapshot.Signals.FirstOrDefault(signal => signal.RootProcessId == rootProcessId)?.OutputDeviceId
            : null;
        return snapshot.OutputDevices.FirstOrDefault(device =>
                   device.IsActive
                   && string.Equals(device.Id, signaledDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive && device.IsDefault)
               ?? snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive);
    }

    private static AudioEndpointSnapshot? ResolveMicrophone(
        AudioPlatformSnapshot snapshot,
        ApplicationSettings settings)
    {
        var configuredId = settings.ReleaseV2?.MicrophoneDeviceId
            ?? settings.Devices.MicrophoneDeviceId;
        var followSystemDefault = settings.ReleaseV2?.FollowSystemDefaultMicrophone
            ?? settings.Devices.FollowSystemDefaultMic;
        if (!followSystemDefault && !string.IsNullOrWhiteSpace(configuredId))
        {
            return snapshot.Microphones.FirstOrDefault(device =>
                device.IsActive
                && string.Equals(device.Id, configuredId, StringComparison.OrdinalIgnoreCase));
        }

        return snapshot.Microphones.FirstOrDefault(static device => device.IsActive && device.IsDefault)
               ?? snapshot.Microphones.FirstOrDefault(static device => device.IsActive);
    }

    private static IEnumerable<IReadOnlyList<CoreCaptureSource>> BuildCaptureAttempts(
        IReadOnlyList<CoreCaptureSource> preferredSources)
    {
        yield return preferredSources;
        if (preferredSources.Count <= 1)
        {
            yield break;
        }

        foreach (var source in preferredSources.OrderBy(static source =>
                     source.Kind == CoreCaptureSourceKind.Microphone ? 1 : 0))
        {
            yield return [source];
        }
    }

    private static MeetingSessionRecord CreateRecordingRecord(
        RecordingStartContext context,
        Guid sessionId,
        DateTimeOffset startedAtUtc,
        string tempSessionPath,
        string stagedPrimaryPath,
        IReadOnlyList<CoreCaptureSource> sources)
    {
        var outputSource = sources.FirstOrDefault(static source =>
            source.Kind is CoreCaptureSourceKind.ProcessOutput or CoreCaptureSourceKind.DeviceLoopback);
        var microphoneSource = sources.FirstOrDefault(static source =>
            source.Kind == CoreCaptureSourceKind.Microphone);
        return MeetingSessionRecord.Create(
            sessionId,
            startedAtUtc,
            context.Mode.ToString().ToLowerInvariant(),
            string.Join(
                '+',
                sources.Select(static source => source.Kind.ToString().ToLowerInvariant())),
            outputSource?.DeviceId,
            microphoneSource?.DeviceId) with
        {
            SourceApp = context.SourceLabel,
            SourceProcessId = outputSource?.RootProcessId,
            TempSessionPath = tempSessionPath,
            StagedPrimaryPath = stagedPrimaryPath,
            AudioOutputPath = outputSource is null ? null : Path.Combine(tempSessionPath, "output.wav"),
            AudioMicPath = microphoneSource is null ? null : Path.Combine(tempSessionPath, "mic.wav"),
            TranscriptionStatus = "not_started"
        };
    }

    private static MeetingSessionArtifactCheckpoint CreateCheckpoint(
        string status,
        string? tempSessionPath,
        string? manifestPath,
        string? stagedPrimaryPath,
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        double? durationSeconds,
        double artifactProgress) =>
        new(
            status,
            tempSessionPath,
            manifestPath,
            stagedPrimaryPath,
            artifacts.FirstOrDefault(static artifact => artifact.Kind == CoreArtifactKind.Output)?.Path,
            artifacts.FirstOrDefault(static artifact => artifact.Kind == CoreArtifactKind.Microphone)?.Path,
            artifacts.FirstOrDefault(static artifact => artifact.Kind == CoreArtifactKind.Mixed)?.Path,
            durationSeconds,
            artifactProgress);

    private static IReadOnlyList<AudioCaptureArtifactSnapshot> BuildFallbackArtifacts(
        MeetingSessionArtifactWorkItem workItem)
    {
        var finalizedAtUtc = workItem.EndedAtUtc ?? workItem.StartedAtUtc ?? workItem.CreatedAtUtc;
        var artifacts = new List<AudioCaptureArtifactSnapshot>(capacity: 3);
        AddFallbackArtifact(artifacts, CoreArtifactKind.Output, workItem.AudioOutputPath, finalizedAtUtc);
        AddFallbackArtifact(artifacts, CoreArtifactKind.Microphone, workItem.AudioMicPath, finalizedAtUtc);
        AddFallbackArtifact(artifacts, CoreArtifactKind.Mixed, workItem.AudioMixPath, finalizedAtUtc);
        return artifacts;
    }

    private static IReadOnlyList<AudioCaptureArtifactSnapshot> ApplyLegOffset(
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        TimeSpan legBaseOffset) =>
        artifacts
            .Select(artifact => artifact with
            {
                RelativeStartOffset = artifact.RelativeStartOffset + legBaseOffset
            })
            .ToArray();

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private static IReadOnlyList<AudioCaptureArtifactSnapshot> MergeArtifacts(
        IReadOnlyList<AudioCaptureArtifactSnapshot> checkpointed,
        IReadOnlyList<AudioCaptureArtifactSnapshot> discovered) =>
        checkpointed
            .Concat(discovered)
            .DistinctBy(static artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void AddFallbackArtifact(
        ICollection<AudioCaptureArtifactSnapshot> artifacts,
        CoreArtifactKind kind,
        string? path,
        DateTimeOffset finalizedAtUtc)
    {
        if (!string.IsNullOrWhiteSpace(path) && IsReadableFile(path))
        {
            artifacts.Add(new AudioCaptureArtifactSnapshot(
                kind,
                path,
                new FileInfo(path).Length,
                finalizedAtUtc));
        }
    }

    private void CleanupSources(
        string sessionId,
        string? tempSessionPath,
        string? primaryAudioPath,
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts,
        string? manifestPath,
        string? stagedPrimaryPath)
    {
        // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
        if (!string.IsNullOrWhiteSpace(tempSessionPath)
            && (IsTranscribe.Application.Transcription.TranscriptionSourceHandoff.Exists(tempSessionPath)
                || _settingsAccessor().ReleaseV2?.Transcription?.Mode is "local" or "online"))
        {
            var retained = RecordingSourceManifestStore.Read(manifestPath);
            IsTranscribe.Application.Transcription.TranscriptionSourceHandoff.Create(
                tempSessionPath, Guid.ParseExact(sessionId, "N"), retained.Count > 0 ? retained : artifacts);
            return;
        }
        if (string.IsNullOrWhiteSpace(tempSessionPath))
        {
            _repository.TryCompleteSourceCleanup(sessionId, DateTimeOffset.UtcNow);
            return;
        }

        try
        {
            var normalizedTempRoot = Path.GetFullPath(tempSessionPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tempRootPrefix = normalizedTempRoot + Path.DirectorySeparatorChar;
            var primaryIsInsideTemp = !string.IsNullOrWhiteSpace(primaryAudioPath)
                && Path.GetFullPath(primaryAudioPath).StartsWith(
                    tempRootPrefix,
                    StringComparison.OrdinalIgnoreCase);
            var isCanonicalTempSession = IsCanonicalTempSessionPath(sessionId, normalizedTempRoot);
            if (Directory.Exists(normalizedTempRoot)
                && isCanonicalTempSession
                && !primaryIsInsideTemp)
            {
                Directory.Delete(normalizedTempRoot, recursive: true);
            }
            else
            {
                foreach (var path in artifacts.Select(static artifact => artifact.Path)
                             .Append(manifestPath)
                             .Append(stagedPrimaryPath)
                             .Where(static path => !string.IsNullOrWhiteSpace(path))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var fullPath = Path.GetFullPath(path!);
                    if (fullPath.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(fullPath, primaryAudioPath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }

                if (Directory.Exists(normalizedTempRoot)
                    && !Directory.EnumerateFileSystemEntries(normalizedTempRoot).Any())
                {
                    Directory.Delete(normalizedTempRoot, recursive: false);
                }
            }

            if (Directory.Exists(normalizedTempRoot))
            {
                _logger.Warning(
                    $"Recording source cleanup remains pending for session {sessionId}: temporary files remain.");
                return;
            }

            if (!_repository.TryCompleteSourceCleanup(sessionId, DateTimeOffset.UtcNow))
            {
                _logger.Warning(
                    $"Recording source cleanup checkpoint was not cleared for session {sessionId}.");
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(
                $"Recording source cleanup remains pending for session {sessionId}: {exception.Message}");
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    private bool IsCanonicalTempSessionPath(string sessionId, string normalizedTempSessionPath)
    {
        if (!Guid.TryParseExact(sessionId, "N", out var parsedSessionId))
        {
            return false;
        }

        try
        {
            var expectedPath = Path.GetFullPath(
                    _pathResolver.GetTempSessionDirectoryPath(_settingsAccessor(), parsedSessionId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(
                expectedPath,
                normalizedTempSessionPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void HandleAudioPlatformSnapshotChanged(object? sender, AudioPlatformSnapshot snapshot)
    {
        var active = _active;
        if (active is null || active.IsPaused || _finalizing || _disposeRequested)
        {
            return;
        }

        var desiredSources = SelectSources(active.Context, _settingsAccessor());
        if (CaptureSourcesAreEquivalent(active.CaptureRequest.Sources, desiredSources))
        {
            return;
        }

        _ = Task.Run(
            () => RebindChangedSourcesSafelyAsync(forceRebind: false),
            CancellationToken.None);
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
    private async Task RebindChangedSourcesAsync(bool forceRebind)
    {
        var shouldFinalize = false;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_active is not { } active
                || active.IsPaused
                || _finalizing
                || _disposeRequested)
            {
                return;
            }

            var desiredSources = SelectSources(active.Context, _settingsAccessor());
            if (!forceRebind
                && CaptureSourcesAreEquivalent(active.CaptureRequest.Sources, desiredSources))
            {
                return;
            }

            active.CaptureSession.SnapshotChanged -= HandleCaptureSnapshotChanged;
            try
            {
                await active.CaptureSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    $"Audio continuity leg could not stop cleanly for session {active.SessionId:N}.");
            }

            var completedArtifacts = active.CompletedArtifacts
                .Concat(ApplyLegOffset(
                    active.CaptureSession.Snapshot.Artifacts,
                    active.CurrentLegBaseOffset))
                .ToArray();
            await active.CaptureSession.DisposeAsync().ConfigureAwait(false);
            var nextLegIndex = active.LegIndex + 1;
            var nextLegBaseOffset = DateTimeOffset.UtcNow - active.StartedAtUtc;
            _active = active with
            {
                CaptureSession = new ClosedCaptureSession(active.SessionId),
                CompletedArtifacts = completedArtifacts,
                CurrentLegBaseOffset = nextLegBaseOffset,
                LegIndex = nextLegIndex
            };
            var manifestPath = await RecordingSourceManifestStore.WriteAsync(
                active.TempSessionPath,
                active.SessionId,
                completedArtifacts,
                CancellationToken.None).ConfigureAwait(false);
            if (!_repository.TryCheckpointArtifact(
                    active.SessionId.ToString("N"),
                    ["recording", "paused"],
                    CreateCheckpoint(
                        active.IsPaused ? "paused" : "recording",
                        active.TempSessionPath,
                        manifestPath,
                        active.StagedPrimaryPath,
                        completedArtifacts,
                        Math.Max(0, (DateTimeOffset.UtcNow - active.StartedAtUtc).TotalSeconds),
                        artifactProgress: 0),
                    DateTimeOffset.UtcNow))
            {
                throw new InvalidOperationException("Audio continuity checkpoint could not be persisted.");
            }

            var nextLegPath = Path.Combine(
                active.TempSessionPath,
                "legs",
                nextLegIndex.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            var continuityArtifactKinds = desiredSources
                .Select(static source => source.Kind == CoreCaptureSourceKind.Microphone
                    ? CoreArtifactKind.Microphone
                    : CoreArtifactKind.Output)
                .Distinct()
                .ToArray();
            if (continuityArtifactKinds.Length > 0)
            {
                await RecordingSourceManifestStore.WriteContinuityLegCheckpointAsync(
                    nextLegPath,
                    active.SessionId,
                    nextLegIndex,
                    nextLegBaseOffset,
                    continuityArtifactKinds,
                    CancellationToken.None).ConfigureAwait(false);
            }

            IAudioCaptureSession? nextCaptureSession = null;
            CoreCaptureRequest? nextRequest = null;
            foreach (var sources in BuildCaptureAttempts(desiredSources))
            {
                if (sources.Count == 0)
                {
                    continue;
                }

                var request = new CoreCaptureRequest(
                    active.SessionId,
                    active.Context.Mode,
                    sources,
                    nextLegPath,
                    PrebufferSeconds: 0,
                    CreateMixedArtifact: false);
                try
                {
                    nextCaptureSession = await _audioPlatform.StartCaptureAsync(
                        request,
                        CancellationToken.None).ConfigureAwait(false);
                    nextRequest = request;
                    break;
                }
                catch (Exception exception)
                {
                    _logger.Warning(
                        $"Audio continuity source plan failed for session {active.SessionId:N}: {exception.Message}");
                }
            }

            if (nextCaptureSession is null || nextRequest is null)
            {
                _active = active with
                {
                    CaptureSession = new ClosedCaptureSession(active.SessionId),
                    CompletedArtifacts = completedArtifacts,
                    CurrentLegBaseOffset = nextLegBaseOffset,
                    LegIndex = nextLegIndex
                };
                PublishRecording(
                    _active,
                    "Источник звука изменился; сохранённая часть встречи будет подготовлена сейчас.");
                shouldFinalize = true;
            }
            else
            {
                _active = active with
                {
                    CaptureSession = nextCaptureSession,
                    CaptureRequest = nextRequest,
                    CompletedArtifacts = completedArtifacts,
                    CurrentLegBaseOffset = nextLegBaseOffset,
                    LegIndex = nextLegIndex
                };
                nextCaptureSession.SnapshotChanged += HandleCaptureSnapshotChanged;
                PublishRecording(_active);
                // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
                HandleCaptureSnapshotChanged(nextCaptureSession, nextCaptureSession.Snapshot);
                _logger.LogEvent(
                    "Info",
                    "RECORDING_SOURCE_REBOUND",
                    "Recording continued with a new audio-source leg.",
                    metadata: new Dictionary<string, object?>
                    {
                        ["session_id"] = active.SessionId.ToString("N"),
                        ["leg_index"] = nextLegIndex,
                        ["source_kinds"] = nextRequest.Sources
                            .Select(static source => source.Kind.ToString())
                            .ToArray()
                    });
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Audio source continuity rebind failed.");
            if (_active is { } active)
            {
                PublishRecording(
                    active,
                    "Не удалось переключить источник звука; сохранённая часть записи останется доступна.");
                shouldFinalize = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (shouldFinalize)
        {
            await FinishAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool CaptureSourcesAreEquivalent(
        IReadOnlyList<CoreCaptureSource> left,
        IReadOnlyList<CoreCaptureSource> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var orderedLeft = left.OrderBy(static source => source.Kind).ToArray();
        var orderedRight = right.OrderBy(static source => source.Kind).ToArray();
        return orderedLeft.Zip(orderedRight).All(pair =>
            pair.First.Kind == pair.Second.Kind
            && pair.First.RootProcessId == pair.Second.RootProcessId
            && string.Equals(pair.First.DeviceId, pair.Second.DeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.ProcessName, pair.Second.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private void HandleCaptureSnapshotChanged(object? sender, AudioCaptureSessionSnapshot snapshot)
    {
        var active = _active;
        if (active is null || snapshot.SessionId != active.SessionId)
        {
            return;
        }

        if (snapshot.Failures.LastOrDefault() is { } failure)
        {
            var failureKey = string.Join(
                ':',
                snapshot.SessionId.ToString("N"),
                failure.OccurredAtUtc.UtcDateTime.Ticks,
                failure.SourceKind,
                failure.Code);
            if (_handledCaptureFailures.TryAdd(failureKey, 0))
            {
                PublishRecording(active, $"Один из источников звука недоступен: {failure.Message}");
                _ = Task.Run(
                    () => RebindChangedSourcesSafelyAsync(forceRebind: true),
                    CancellationToken.None);
            }
        }

        if (snapshot.State is AudioCaptureState.Completed or AudioCaptureState.Faulted && !_finalizing)
        {
            _ = Task.Run(FinishSafelyAsync, CancellationToken.None);
        }
    }

    private async Task RebindChangedSourcesSafelyAsync(bool forceRebind)
    {
        try
        {
            await RebindChangedSourcesAsync(forceRebind).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Background audio source continuity rebind failed.");
        }
    }

    private async Task FinishSafelyAsync()
    {
        try
        {
            await FinishAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Background recording finalization failed after terminal capture state.");
        }
    }

    private void PublishRecording(ActiveRecording? active, string? attentionMessage = null)
    {
        if (active is null)
        {
            return;
        }

        var capabilityIssue = GetRecordingCapabilityIssue(active);
        var measuredAtUtc = _timeProvider.GetUtcNow();

        PublishSnapshot(new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Recording,
            new ActiveMeetingSnapshot(
                active.SessionId,
                active.Context.SourceLabel,
                active.StartedAtUtc,
                active.CaptureRequest.Sources.Any(static source =>
                    source.Kind is CoreCaptureSourceKind.ProcessOutput or CoreCaptureSourceKind.DeviceLoopback),
                active.CaptureRequest.Sources.Any(static source =>
                    source.Kind == CoreCaptureSourceKind.Microphone),
                active.IsPaused)
            {
                ActiveDuration = CalculateActiveDuration(active, measuredAtUtc),
                ActiveDurationMeasuredAtUtc = measuredAtUtc
            },
            Finalization: null,
            AttentionMessage: attentionMessage,
            RefreshRecentRecordings: false,
            CapabilityIssue: capabilityIssue));
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    private static TimeSpan CalculateActiveDuration(ActiveRecording active, DateTimeOffset measuredAtUtc)
    {
        var duration = active.AccumulatedActiveDuration;
        if (!active.IsPaused && active.ActiveSegmentStartedAtUtc is { } segmentStartedAtUtc)
        {
            duration += measuredAtUtc - segmentStartedAtUtc;
        }

        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    private RuntimeCapabilityIssue? GetRecordingCapabilityIssue(ActiveRecording active)
    {
        var settings = _settingsAccessor();
        var audioPlatform = _audioPlatform.Snapshot;
        var configuredMicrophoneId = settings.ReleaseV2?.MicrophoneDeviceId
            ?? settings.Devices.MicrophoneDeviceId;
        var followsSystemMicrophone = settings.ReleaseV2?.FollowSystemDefaultMicrophone
            ?? settings.Devices.FollowSystemDefaultMic;
        if (!followsSystemMicrophone
            && !string.IsNullOrWhiteSpace(configuredMicrophoneId)
            && !audioPlatform.Microphones.Any(microphone =>
                microphone.IsActive
                && string.Equals(
                    microphone.Id,
                    configuredMicrophoneId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable;
        }

        var activeSources = active.CaptureRequest.Sources;
        var hasActiveMicrophone = activeSources.Any(static source =>
            source.Kind == CoreCaptureSourceKind.Microphone);
        if (!hasActiveMicrophone
            && !audioPlatform.Microphones.Any(static microphone => microphone.IsActive))
        {
            return RuntimeCapabilityIssue.NoActiveMicrophone;
        }

        var preferredSources = SelectSources(active.Context, settings);
        if (!hasActiveMicrophone
            && preferredSources.Any(static source => source.Kind == CoreCaptureSourceKind.Microphone))
        {
            return RuntimeCapabilityIssue.MicrophoneCaptureUnavailable;
        }

        var hasActiveOutput = activeSources.Any(IsOutputSource);
        if (!hasActiveOutput
            && !audioPlatform.OutputDevices.Any(static output => output.IsActive))
        {
            return RuntimeCapabilityIssue.NoActiveOutput;
        }

        return !hasActiveOutput && preferredSources.Any(IsOutputSource)
            ? RuntimeCapabilityIssue.OutputCaptureUnavailable
            : null;
    }

    private static bool IsOutputSource(CoreCaptureSource source) =>
        source.Kind is CoreCaptureSourceKind.ProcessOutput or CoreCaptureSourceKind.DeviceLoopback;

    private void PublishStage(
        Guid sessionId,
        RecordingArtifactStage stage,
        string? recoverablePath,
        double? progress = null) =>
        PublishSnapshot(new RecordingCoordinatorSnapshot(
            stage == RecordingArtifactStage.AttentionRequired
                ? ApplicationActivityState.AttentionRequired
                : stage == RecordingArtifactStage.Ready
                    ? ApplicationActivityState.Ready
                    : ApplicationActivityState.Processing,
            ActiveMeeting: null,
            new RecordingFinalizationSnapshot(sessionId, stage, recoverablePath, progress),
            AttentionMessage: null,
            RefreshRecentRecordings: stage is RecordingArtifactStage.Ready or RecordingArtifactStage.AttentionRequired));

    private void PublishReady(Guid sessionId, string primaryAudioPath) =>
        PublishSnapshot(new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Ready,
            ActiveMeeting: null,
            new RecordingFinalizationSnapshot(sessionId, RecordingArtifactStage.Ready, primaryAudioPath),
            AttentionMessage: null,
            RefreshRecentRecordings: true));

    private void PublishDiscarded() =>
        PublishSnapshot(new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Ready,
            ActiveMeeting: null,
            Finalization: null,
            AttentionMessage: null,
            RefreshRecentRecordings: true));

    private void PublishAttention(Guid sessionId, string? message, string? recoverablePath) =>
        PublishSnapshot(new RecordingCoordinatorSnapshot(
            ApplicationActivityState.AttentionRequired,
            ActiveMeeting: null,
            new RecordingFinalizationSnapshot(
                sessionId,
                RecordingArtifactStage.AttentionRequired,
                recoverablePath),
            AttentionMessage: message,
            RefreshRecentRecordings: true));

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private void PublishSnapshot(RecordingCoordinatorSnapshot snapshot)
    {
        var subscribers = SnapshotChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<RecordingCoordinatorSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Recording coordinator snapshot observer failed.");
            }
        }
    }

    private static string ResolveTempDirectory(MeetingSessionArtifactWorkItem workItem) =>
        new[] { workItem.AudioMixPath, workItem.AudioOutputPath, workItem.AudioMicPath }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetDirectoryName(path!))
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path))
        ?? throw new InvalidOperationException(
            $"Session {workItem.Id} has no recoverable temporary directory.");

    private static string? SelectReadableArtifact(IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts) =>
        artifacts.FirstOrDefault(static artifact => IsReadableFile(artifact.Path))?.Path;

    private static TimeSpan? GetPreparedSourceDuration(
        IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts)
    {
        var path = artifacts
            .OrderBy(static artifact => artifact.Kind switch
            {
                CoreArtifactKind.Mixed => 0,
                CoreArtifactKind.Output => 1,
                CoreArtifactKind.Microphone => 2,
                _ => 3
            })
            .Select(static artifact => artifact.Path)
            .FirstOrDefault(IsReadableFile);
        if (path is null)
        {
            return null;
        }

        try
        {
            using var reader = new WaveFileReader(path);
            return reader.TotalTime > TimeSpan.Zero ? reader.TotalTime : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsReadableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async Task StopAndDisposeUnownedCaptureAsync(IAudioCaptureSession captureSession)
    {
        try
        {
            await captureSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Unowned capture could not be stopped after a failed handoff.");
        }

        try
        {
            await captureSession.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Unowned capture could not be disposed after a failed handoff.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record RecordingStartContext(
        CoreCaptureMode Mode,
        string SourceLabel,
        int? RootProcessId,
        string? ProcessName);

    private sealed class ClosedCaptureSession : IAudioCaptureSession
    {
        public ClosedCaptureSession(Guid sessionId)
        {
            Snapshot = new AudioCaptureSessionSnapshot(
                sessionId,
                AudioCaptureState.Completed,
                IsPersistingAudio: true,
                Artifacts: [],
                Failures: []);
        }

        public event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AudioCaptureSessionSnapshot Snapshot { get; }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PromotePrebufferAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record ActiveRecording(
        Guid SessionId,
        RecordingStartContext Context,
        DateTimeOffset StartedAtUtc,
        string TempSessionPath,
        string StagedPrimaryPath,
        string FinalPrimaryPath,
        CoreCaptureRequest CaptureRequest,
        IAudioCaptureSession CaptureSession,
        bool IsPaused,
        TimeSpan AccumulatedActiveDuration,
        DateTimeOffset? ActiveSegmentStartedAtUtc,
        int LegIndex,
        TimeSpan CurrentLegBaseOffset,
        IReadOnlyList<AudioCaptureArtifactSnapshot> CompletedArtifacts);
}
