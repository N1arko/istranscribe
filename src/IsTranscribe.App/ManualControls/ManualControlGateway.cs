using System.IO;
using System.Runtime.Versioning;
using IsTranscribe.App.AutomaticRecording;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.App.ManualControls;

public enum RecordingActivityState
{
    OnboardingBlocked,
    Idle,
    AwaitingConfirmation,
    Recording,
    Paused,
    Stopping
}

public enum RecordingSourceAvailability
{
    NotIncluded,
    Active,
    Unavailable
}

public sealed record CurrentRecordingSnapshot(
    Guid SessionId,
    string Mode,
    string SourceType,
    string SourceSummary,
    string? SourceApp,
    string? OutputDeviceName,
    string? MicrophoneDeviceName,
    RecordingSourceAvailability OutputAvailability,
    RecordingSourceAvailability MicrophoneAvailability,
    DateTimeOffset StartedAtUtc,
    TimeSpan RecordedDuration,
    string StatusText,
    string? BlockingReason);

public sealed record PendingRecordingConfirmationSnapshot(
    Guid SessionId,
    string Mode,
    string PromptKind,
    string SourceSummary,
    string? SourceApp,
    DateTimeOffset StartedAtUtc);

public sealed record ManualControlSnapshot(
    RecordingActivityState RecordingState,
    bool PrivacyPauseEnabled,
    bool CanStartForceRecord,
    bool CanPause,
    bool CanResume,
    bool CanStop,
    bool CanDiscard,
    string? BlockingReason,
    CurrentRecordingSnapshot? CurrentRecording,
    PendingRecordingConfirmationSnapshot? PendingConfirmation)
{
    public static ManualControlSnapshot Default { get; } = new(
        RecordingActivityState.OnboardingBlocked,
        PrivacyPauseEnabled: false,
        CanStartForceRecord: false,
        CanPause: false,
        CanResume: false,
        CanStop: false,
        CanDiscard: false,
        BlockingReason: "Onboarding is incomplete.",
        CurrentRecording: null,
        PendingConfirmation: null);
}

 public sealed record ResolvedCapturePlan(
     IReadOnlyList<AudioCaptureSourceRequest> Sources,
     bool CreateMixedArtifact,
     string SourceType,
     string MergeSourceFamily,
     string SourceSummary,
     string? BlockingReason,
     string? OutputDeviceId,
     string? OutputDeviceName,
    string? MicrophoneDeviceId,
    string? MicrophoneDeviceName)
{
    public bool HasOutput => OutputDeviceId is not null;

    public bool HasMicrophone => MicrophoneDeviceId is not null;
}

public sealed record AutomaticRecordingStartRequest(
    string Mode,
    string? SourceApp,
    int? SourceProcessId,
    ResolvedCapturePlan Plan);

public sealed record PreparedAutomaticRecordingHandle(
    Guid SessionId,
    string Mode,
    string PromptKind,
    string SourceSummary,
    string? SourceApp,
    DateTimeOffset StartedAtUtc);

[SupportedOSPlatform("windows")]
public sealed class ManualControlGateway
{
    private const string ManualPauseBlocker = "manual_pause";
    private const string PrivacyPauseBlocker = "privacy_pause";
    private const string MergeWindowBlocker = "merge_window";
    private static readonly TimeSpan DeviceGraceWindow = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly BootstrapFileLogger _logger;
    private readonly IAudioRecorderEngine _recorderEngine;
    private readonly IAudioDeviceManager _deviceManager;
    private readonly IAutomaticPromptService? _promptService;
    private readonly ArtifactPathResolver _artifactPathResolver;
    private readonly AudioCompressor _audioCompressor;
    private readonly TimeProvider _timeProvider;
    private readonly LocalAppPaths _paths;
    private readonly System.Threading.Timer _ticker;
    private ApplicationSettings _settings = ApplicationSettings.Default;
    private HostCapabilitySnapshot _capability = new(HostCapabilityState.Blocked, false, "Capability state is unavailable.");
    private MeetingSessionRepository? _sessionRepository;
    private ManualControlSnapshot _snapshot = ManualControlSnapshot.Default;
    private ActiveRecordingSession? _activeSession;
    private PreparedAutomaticRecordingSession? _pendingAutomaticRecording;
    private CancellationTokenSource? _deviceContinuityPromptCts;
    private bool _privacyPauseEnabled;

    public ManualControlGateway(
        BootstrapFileLogger logger,
        IAudioRecorderEngine recorderEngine,
        IAudioDeviceManager deviceManager,
        IAutomaticPromptService? promptService,
        ArtifactPathResolver artifactPathResolver,
        AudioCompressor audioCompressor,
        LocalAppPaths paths,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _recorderEngine = recorderEngine;
        _deviceManager = deviceManager;
        _promptService = promptService;
        _artifactPathResolver = artifactPathResolver;
        _audioCompressor = audioCompressor;
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ticker = new System.Threading.Timer(_ => PublishSnapshot(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        PublishSnapshot();
    }

    public event EventHandler<ManualControlSnapshot>? SnapshotChanged;

    public ManualControlSnapshot Snapshot => _snapshot;

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#command-model
    public void UpdateHostContext(ApplicationSettings settings, HostCapabilitySnapshot capability, MeetingSessionRepository? sessionRepository = null)
    {
        _settings = settings;
        _capability = capability;
        if (sessionRepository is not null)
        {
            _sessionRepository = sessionRepository;
        }

        PublishSnapshot();
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.sources
    public ResolvedCapturePlan ResolveAutomaticRecordingPlan(int rootProcessId)
    {
        var outputSnapshot = ResolveRenderDevice();
        var micSnapshot = ResolveCaptureDevice();
        var requestedSources = _settings.Recording.DefaultSourcesAuto
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var wantsProcessOutput = requestedSources.Contains("process_output", StringComparer.OrdinalIgnoreCase);
        var wantsDeviceLoopback = requestedSources.Contains("device_loopback", StringComparer.OrdinalIgnoreCase);
        var wantsMicrophone = requestedSources.Contains("mic", StringComparer.OrdinalIgnoreCase);

        var sources = new List<AudioCaptureSourceRequest>();
        if (wantsProcessOutput && _capability.ProcessLoopbackAvailable && rootProcessId > 0)
        {
            sources.Add(AudioCaptureSourceRequest.ProcessOutput(rootProcessId));
        }
        else if (wantsDeviceLoopback && outputSnapshot is not null)
        {
            sources.Add(AudioCaptureSourceRequest.DeviceLoopback(outputSnapshot.Id));
        }

        if (wantsMicrophone && micSnapshot is not null)
        {
            sources.Add(AudioCaptureSourceRequest.Microphone(micSnapshot.Id));
        }

        return BuildResolvedPlan(
            requestedSources,
            sources,
            outputSnapshot?.Id,
            outputSnapshot?.FriendlyName,
            micSnapshot?.Id,
            micSnapshot?.FriendlyName,
            "No automatic capture sources are currently available.");
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#force-record.preconditions
    public async Task StartForceRecordAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolvedPlan = ResolveForceRecordPlan();
            if (!CanStartForceRecord(resolvedPlan, out var blockingReason))
            {
                _logger.Warning($"Force Record start rejected. Reason={blockingReason}");
                PublishSnapshot(blockingReason);
                return;
            }

            var repository = _sessionRepository
                ?? throw new InvalidOperationException("Meeting session repository is unavailable.");

            var startRequest = new AutomaticRecordingStartRequest("manual", null, null, resolvedPlan);
            _activeSession = await StartRecordingSessionLockedAsync(
                startRequest,
                AudioCaptureMode.Manual,
                repository).ConfigureAwait(false);

            UpdateTickerState();
            PublishSnapshot();
            _logger.Info($"Force Record started. SessionId={_activeSession.SessionId:N}, Sources={string.Join(",", resolvedPlan.Sources.Select(static source => source.Kind))}.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.capture
    public async Task<PreparedAutomaticRecordingHandle?> PrepareAutomaticRecordingAsync(AutomaticRecordingStartRequest request, string promptKind)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanPrepareAutomaticRecording(request.Plan, out var blockingReason))
            {
                PublishSnapshot(blockingReason);
                return null;
            }

            var createdAtUtc = _timeProvider.GetUtcNow();
            var sessionId = Guid.NewGuid();
            var tempRoot = _artifactPathResolver.GetTempSessionDirectoryPath(_settings, sessionId);
            var legDirectory = GetLegDirectoryPath(tempRoot, 1);
            Directory.CreateDirectory(legDirectory);

            AudioRecorderSession recorderSession;
            try
            {
                recorderSession = await _recorderEngine.StartAsync(
                    new AudioCaptureRequest(
                        sessionId,
                        AudioCaptureMode.Ask,
                        request.Plan.Sources,
                        legDirectory,
                        _settings.Recording.PrebufferSeconds,
                        request.Plan.CreateMixedArtifact),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Automatic prompt preparation failed for session {sessionId:N}.");
                TryDeleteDirectory(tempRoot);
                PublishSnapshot(exception.Message);
                return null;
            }

            _pendingAutomaticRecording = new PreparedAutomaticRecordingSession(
                sessionId,
                request.Mode,
                promptKind,
                request.SourceApp,
                request.SourceProcessId,
                createdAtUtc,
                tempRoot,
                legDirectory,
                request.Plan,
                recorderSession);

            PublishSnapshot();
            return new PreparedAutomaticRecordingHandle(
                sessionId,
                request.Mode,
                promptKind,
                request.Plan.SourceSummary,
                request.SourceApp,
                createdAtUtc);
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.when
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.fields
    public async Task<bool> ActivatePreparedAutomaticRecordingAsync(Guid sessionId)
    {
        PreparedAutomaticRecordingSession? preparedSession;
        MeetingSessionRepository repository;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            preparedSession = _pendingAutomaticRecording;
            if (preparedSession is null || preparedSession.SessionId != sessionId)
            {
                PublishSnapshot();
                return false;
            }

            repository = _sessionRepository
                ?? throw new InvalidOperationException("Meeting session repository is unavailable.");

            var sessionRecord = CreateSessionRecord(
                preparedSession.SessionId,
                preparedSession.CreatedAtUtc,
                preparedSession.Mode,
                preparedSession.SourceApp,
                preparedSession.SourceProcessId,
                preparedSession.Plan);

            await repository.UpsertAsync(sessionRecord, CancellationToken.None).ConfigureAwait(false);

            try
            {
                await preparedSession.RecorderSession.PromotePrebufferAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Automatic prompt promotion failed for session {preparedSession.SessionId:N}.");
                await repository.UpsertAsync(
                    sessionRecord with
                    {
                        EndedAtUtc = _timeProvider.GetUtcNow(),
                        Status = "failed",
                        ErrorCode = "automatic_prompt_promotion_failed",
                        ErrorMessage = exception.Message
                    },
                    CancellationToken.None).ConfigureAwait(false);

                _pendingAutomaticRecording = null;
                PublishSnapshot(exception.Message);
                _ = CleanupPreparedRecordingAsync(preparedSession);
                return false;
            }

            preparedSession.RecorderSession.SnapshotChanged += HandleRecorderSnapshotChanged;
            _activeSession = new ActiveRecordingSession(
                preparedSession.SessionId,
                sessionRecord,
                preparedSession.TempRootDirectory,
                preparedSession.CreatedAtUtc,
                preparedSession.Plan,
                _settings.Recording.PrivacyPausePolicy,
                preparedSession.RecorderSession,
                preparedSession.CurrentLegDirectory,
                1);

            _pendingAutomaticRecording = null;
            UpdateTickerState();
            PublishSnapshot();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.cancellation
    public async Task DismissPreparedAutomaticRecordingAsync(Guid sessionId, string? transientBlockingReason = null)
    {
        PreparedAutomaticRecordingSession? preparedSession = null;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingAutomaticRecording is not null && _pendingAutomaticRecording.SessionId == sessionId)
            {
                preparedSession = _pendingAutomaticRecording;
                _pendingAutomaticRecording = null;
            }

            PublishSnapshot(transientBlockingReason);
        }
        finally
        {
            _mutex.Release();
        }

        if (preparedSession is not null)
        {
            await CleanupPreparedRecordingAsync(preparedSession).ConfigureAwait(false);
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.when
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.fields
    public async Task<bool> StartAutomaticRecordingAsync(AutomaticRecordingStartRequest request)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanPrepareAutomaticRecording(request.Plan, out var blockingReason))
            {
                PublishSnapshot(blockingReason);
                return false;
            }

            var repository = _sessionRepository
                ?? throw new InvalidOperationException("Meeting session repository is unavailable.");

            _activeSession = await StartRecordingSessionLockedAsync(
                request,
                AudioCaptureMode.Auto,
                repository).ConfigureAwait(false);

            UpdateTickerState();
            PublishSnapshot();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.merge
    public async Task<bool> EnterAutomaticMergePendingAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSession is null
                || _activeSession.IsFinalizing
                || _activeSession.Record.Mode == "manual"
                || _activeSession.IsMergePending)
            {
                PublishSnapshot();
                return false;
            }

            _activeSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
            await FinalizeCurrentLegLockedAsync(_activeSession, stopRecorder: true).ConfigureAwait(false);
            _activeSession.EnterMergePending(_timeProvider.GetUtcNow());
            await PersistIntermediateStatusAsync(_activeSession, "paused").ConfigureAwait(false);
            UpdateTickerState();
            PublishSnapshot();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.merge
    public async Task<bool> ResumeAutomaticRecordingAsync(AutomaticRecordingStartRequest request)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSession is null
                || _activeSession.IsFinalizing
                || !_activeSession.IsMergePending
                || !string.Equals(_activeSession.Record.Mode, request.Mode, StringComparison.OrdinalIgnoreCase))
            {
                PublishSnapshot();
                return false;
            }

            if (!string.Equals(_activeSession.CurrentPlan.MergeSourceFamily, request.Plan.MergeSourceFamily, StringComparison.OrdinalIgnoreCase))
            {
                PublishSnapshot("The meeting returned with a different source family, so merge resume is not allowed.");
                return false;
            }

            if (request.Plan.Sources.Count == 0)
            {
                PublishSnapshot(request.Plan.BlockingReason ?? "No capture sources are available.");
                return false;
            }

            var nextLegIndex = _activeSession.CurrentLegIndex + 1;
            var nextLegDirectory = GetLegDirectoryPath(_activeSession.TempRootDirectory, nextLegIndex);
            Directory.CreateDirectory(nextLegDirectory);

            AudioRecorderSession recorderSession;
            try
            {
                recorderSession = await _recorderEngine.StartAsync(
                    new AudioCaptureRequest(
                        _activeSession.SessionId,
                        AudioCaptureMode.Auto,
                        request.Plan.Sources,
                        nextLegDirectory,
                        0,
                        request.Plan.CreateMixedArtifact),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Automatic merge resume failed for session {_activeSession.SessionId:N}.");
                PublishSnapshot(exception.Message);
                return false;
            }

            recorderSession.SnapshotChanged += HandleRecorderSnapshotChanged;
            _activeSession.ResumeFromMergePending(recorderSession, nextLegDirectory, nextLegIndex, request.Plan, _timeProvider.GetUtcNow());
            await PersistIntermediateStatusAsync(_activeSession, "recording").ConfigureAwait(false);
            UpdateTickerState();
            PublishSnapshot();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public bool IsMergePending => _activeSession is { IsMergePending: true, IsFinalizing: false };

    public DateTimeOffset? MergePendingStartedAtUtc => _activeSession?.MergePendingStartedAtUtc;

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.stop
    public Task StopCurrentRecordingAsync() => FinalizeActiveSessionAsync(discard: false, explicitError: null);

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.discard
    public Task DiscardCurrentRecordingAsync() => FinalizeActiveSessionAsync(discard: true, explicitError: null);

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.pause
    public async Task PauseCurrentRecordingAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSession is null || _activeSession.IsFinalizing)
            {
                PublishSnapshot();
                return;
            }

            if (!_activeSession.PauseBlockers.Add(ManualPauseBlocker))
            {
                PublishSnapshot();
                return;
            }

            if (_activeSession.LiveRecorderSession is not null && !_activeSession.IsMergePending)
            {
                _activeSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
                await _activeSession.LiveRecorderSession.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistIntermediateStatusAsync(_activeSession, "paused").ConfigureAwait(false);
            }

            UpdateTickerState();
            PublishSnapshot();
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#actions.pause
    public async Task ResumeCurrentRecordingAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSession is null || _activeSession.IsFinalizing)
            {
                PublishSnapshot();
                return;
            }

            if (!_activeSession.PauseBlockers.Remove(ManualPauseBlocker))
            {
                PublishSnapshot();
                return;
            }

            if (_activeSession.LiveRecorderSession is not null && !_activeSession.IsMergePending && _activeSession.PauseBlockers.Count == 0)
            {
                _activeSession.ResumeAt(_timeProvider.GetUtcNow());
                await _activeSession.LiveRecorderSession.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistIntermediateStatusAsync(_activeSession, "recording").ConfigureAwait(false);
            }

            UpdateTickerState();
            PublishSnapshot();
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#privacy-and-quit.privacy
    public async Task TogglePrivacyPauseAsync()
    {
        PreparedAutomaticRecordingSession? preparedSessionToCancel = null;
        Task? finalizeTask = null;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _privacyPauseEnabled = !_privacyPauseEnabled;
            _logger.Info($"Privacy Pause toggled. Enabled={_privacyPauseEnabled}.");

            if (_privacyPauseEnabled && _pendingAutomaticRecording is not null)
            {
                preparedSessionToCancel = _pendingAutomaticRecording;
                _pendingAutomaticRecording = null;
            }

            if (_activeSession is null || _activeSession.IsFinalizing)
            {
                UpdateTickerState();
                PublishSnapshot();
                return;
            }

            if (_privacyPauseEnabled)
            {
                if (string.Equals(_activeSession.PrivacyPausePolicy, "finish", StringComparison.OrdinalIgnoreCase))
                {
                    var session = await BeginFinalizationLockedAsync().ConfigureAwait(false);
                    if (session is not null)
                    {
                        finalizeTask = FinalizePreparedSessionAsync(session, discard: false, explicitError: null);
                    }
                }
                else if (_activeSession.PauseBlockers.Add(PrivacyPauseBlocker) && _activeSession.LiveRecorderSession is not null && !_activeSession.IsMergePending)
                {
                    _activeSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
                    await _activeSession.LiveRecorderSession.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                    await PersistIntermediateStatusAsync(_activeSession, "paused").ConfigureAwait(false);
                }
            }
            else if (_activeSession.PauseBlockers.Remove(PrivacyPauseBlocker)
                     && _activeSession.LiveRecorderSession is not null
                     && !_activeSession.IsMergePending
                     && _activeSession.PauseBlockers.Count == 0)
            {
                _activeSession.ResumeAt(_timeProvider.GetUtcNow());
                await _activeSession.LiveRecorderSession.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistIntermediateStatusAsync(_activeSession, "recording").ConfigureAwait(false);
            }

            if (finalizeTask is null)
            {
                UpdateTickerState();
                PublishSnapshot();
            }
        }
        finally
        {
            _mutex.Release();
        }

        if (preparedSessionToCancel is not null)
        {
            await CleanupPreparedRecordingAsync(preparedSessionToCancel).ConfigureAwait(false);
        }

        if (finalizeTask is not null)
        {
            await finalizeTask.ConfigureAwait(false);
        }
    }

    // @spec spec://modules/app/FEAT-007-device-aware-recording-continuity#change-matrix
    // @spec spec://modules/app/FEAT-007-device-aware-recording-continuity#transient-loss
    // @spec spec://modules/app/FEAT-007-device-aware-recording-continuity#diagnostics
    public async Task HandleDeviceInventoryChangedAsync(CancellationToken cancellationToken = default)
    {
        DeviceContinuityDecisionContext? decisionContext;
        bool hasDeviceLoss;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            decisionContext = BuildDeviceContinuityDecisionContextLocked();
            if (decisionContext is null)
            {
                return;
            }

            hasDeviceLoss = (decisionContext.OutputRole is not null && !decisionContext.OutputRole.CurrentAvailable)
                || (decisionContext.MicRole is not null && !decisionContext.MicRole.CurrentAvailable);
        }
        finally
        {
            _mutex.Release();
        }

        // @spec spec://modules/app/FEAT-007-device-aware-recording-continuity#transient-loss.window
        if (hasDeviceLoss)
        {
            await Task.Delay(DeviceGraceWindow, _timeProvider, cancellationToken).ConfigureAwait(false);

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                decisionContext = BuildDeviceContinuityDecisionContextLocked();
                if (decisionContext is null)
                {
                    return;
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!decisionContext.RequiresPrompt)
            {
                await ApplyDeviceContinuityDecisionLockedAsync(
                    decisionContext,
                    decisionContext.DefaultDecision,
                    fallbackApplied: false,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            _mutex.Release();
        }

        var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deviceContinuityPromptCts = promptCts;
        AutomaticPromptDecision selectedDecision;
        try
        {
            selectedDecision = await RequestDeviceContinuityDecisionAsync(decisionContext, promptCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _deviceContinuityPromptCts = null;
            promptCts.Dispose();
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyDeviceContinuityDecisionLockedAsync(
                decisionContext,
                selectedDecision,
                fallbackApplied: selectedDecision == decisionContext.DefaultDecision,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task HandleSourceDeviceDriftAsync(SourceDeviceDrift drift, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSession is null
                || _activeSession.IsFinalizing
                || _activeSession.IsMergePending
                || _activeSession.LiveRecorderSession is null)
            {
                return;
            }

            // Only relevant for device_loopback captures — process_output captures at process level regardless of device.
            var hasDeviceLoopback = _activeSession.CurrentPlan.Sources
                .Any(static source => source.Kind == AudioCaptureSourceKind.DeviceLoopback);
            if (!hasDeviceLoopback)
            {
                return;
            }

            // Only react if the drifting process matches the active recording's source process.
            if (_activeSession.Record.SourceProcessId is not null
                && _activeSession.Record.SourceProcessId != drift.RootProcessId)
            {
                return;
            }

            // Only react if the current capture device matches the previous render device (the one the source moved away from).
            if (!string.Equals(_activeSession.CurrentPlan.OutputDeviceId, drift.PreviousRenderDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.Info($"Source device drift: session {_activeSession.SessionId:N} capture device {drift.PreviousRenderDeviceId} → source app moved to {drift.CurrentRenderDeviceId}.");

            var nextPlan = ResolvePlanForCurrentMode(_activeSession);
            var switched = await TrySeamlessSwitchLockedAsync(nextPlan, cancellationToken).ConfigureAwait(false);
            _logger.Info(switched
                ? $"Source device drift: seamless switch completed for session {_activeSession.SessionId:N}."
                : $"Source device drift: seamless switch failed for session {_activeSession.SessionId:N}.");
            PublishSnapshot();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task FinalizeActiveSessionAsync(bool discard, string? explicitError)
    {
        // @spec spec://modules/app/FEAT-007-device-aware-recording-continuity#prompt-cancel-on-stop
        _deviceContinuityPromptCts?.Cancel();

        ActiveRecordingSession? session;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            session = await BeginFinalizationLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        if (session is not null)
        {
            await FinalizePreparedSessionAsync(session, discard, explicitError).ConfigureAwait(false);
        }
    }

    private async Task<ActiveRecordingSession?> BeginFinalizationLockedAsync()
    {
        var session = _activeSession;
        if (session is null || session.IsFinalizing)
        {
            PublishSnapshot();
            return null;
        }

        session.IsFinalizing = true;
        session.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
        await PersistIntermediateStatusAsync(session, "stopping").ConfigureAwait(false);
        UpdateTickerState();
        PublishSnapshot();
        return session;
    }

    private async Task FinalizePreparedSessionAsync(ActiveRecordingSession session, bool discard, string? explicitError)
    {
        try
        {
            await FinalizeCurrentLegLockedAsync(session, stopRecorder: true).ConfigureAwait(false);
            await PersistFinalStateAsync(session, discard, explicitError).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Recording finalization failed. SessionId={session.SessionId:N}.");
        }
        finally
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }

                UpdateTickerState();
                PublishSnapshot();
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    private async Task PersistFinalStateAsync(ActiveRecordingSession session, bool discard, string? explicitError)
    {
        var repository = _sessionRepository
            ?? throw new InvalidOperationException("Meeting session repository is unavailable.");

        var endedAtUtc = _timeProvider.GetUtcNow();

        if (discard)
        {
            var recordedDuration = session.GetRecordedDuration(endedAtUtc);
            TryDeleteDirectory(session.TempRootDirectory);
            await repository.UpsertAsync(
                session.Record with
                {
                    EndedAtUtc = endedAtUtc,
                    Status = "discarded",
                    DurationSeconds = recordedDuration.TotalSeconds,
                    UserDiscarded = true
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.Info($"Recording discarded. SessionId={session.SessionId:N}.");
            return;
        }

        var finalDirectory = _artifactPathResolver.GetSessionDirectoryPath(_settings, session.SessionId, session.CreatedAtUtc);
        Directory.CreateDirectory(finalDirectory);

        string? outputPath = null;
        string? micPath = null;
        string? mixPath = null;

        foreach (var group in session.CompletedLegs
                     .SelectMany(static leg => leg.Artifacts)
                     .Where(static artifact => !string.IsNullOrWhiteSpace(artifact.Path))
                     .GroupBy(static artifact => artifact.Kind))
        {
            var existingPaths = group
                .Select(static artifact => artifact.Path)
                .Where(File.Exists)
                .ToArray();

            if (existingPaths.Length == 0)
            {
                continue;
            }

            var extension = Path.GetExtension(existingPaths[0]);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".wav";
            }

            var destinationPath = Path.Combine(finalDirectory, $"{GetArtifactBaseName(group.Key)}{extension}");
            if (existingPaths.Length == 1)
            {
                MoveArtifactBestEffort(existingPaths[0], destinationPath);
            }
            else
            {
                WaveArtifactConcatenator.Concatenate(existingPaths, destinationPath);
            }

            switch (group.Key)
            {
                case AudioCaptureArtifactKind.Output:
                    outputPath = destinationPath;
                    break;
                case AudioCaptureArtifactKind.Microphone:
                    micPath = destinationPath;
                    break;
                case AudioCaptureArtifactKind.Mixed:
                    mixPath = destinationPath;
                    break;
            }
        }

        TryDeleteDirectory(session.TempRootDirectory);

        if (_settings.Storage.AudioCompressionEnabled)
        {
            outputPath = await TryCompressArtifactAsync(outputPath, isMicrophone: false).ConfigureAwait(false);
            micPath = await TryCompressArtifactAsync(micPath, isMicrophone: true).ConfigureAwait(false);
            mixPath = await TryCompressArtifactAsync(mixPath, isMicrophone: false).ConfigureAwait(false);
        }

        var saved = outputPath is not null || micPath is not null || mixPath is not null;
        var totalDuration = session.GetRecordedDuration(endedAtUtc);
        await repository.UpsertAsync(
            session.Record with
            {
                EndedAtUtc = endedAtUtc,
                Status = saved ? "saved" : "failed",
                TranscriptionStatus = saved ? "queued" : "not_started",
                QueuedAtUtc = saved ? endedAtUtc : null,
                UpdatedAtUtc = endedAtUtc,
                AudioOutputPath = outputPath,
                AudioMicPath = micPath,
                AudioMixPath = mixPath,
                DurationSeconds = totalDuration.TotalSeconds,
                ErrorCode = explicitError is null ? null : "recording_finalize_failed",
                ErrorMessage = explicitError,
                UserDiscarded = false
            },
            CancellationToken.None).ConfigureAwait(false);

        _logger.Info($"Recording finalized. SessionId={session.SessionId:N}, Saved={saved}.");
    }

    private DeviceContinuityDecisionContext? BuildDeviceContinuityDecisionContextLocked()
    {
        if (_activeSession is null
            || _activeSession.IsFinalizing
            || _activeSession.IsMergePending
            || _activeSession.LiveRecorderSession is null)
        {
            return null;
        }

        var outputRole = EvaluateDeviceRoleChange(
            _activeSession.CurrentPlan.OutputDeviceId,
            _settings.Devices.FollowSystemDefaultOutput,
            _settings.Devices.OutputChangePolicy,
            deviceRole: "output");
        var micRole = EvaluateDeviceRoleChange(
            _activeSession.CurrentPlan.MicrophoneDeviceId,
            _settings.Devices.FollowSystemDefaultMic,
            _settings.Devices.MicChangePolicy,
            deviceRole: "mic");

        if (outputRole is null && micRole is null)
        {
            return null;
        }

        var policy = CombinePolicy(
            CombinePolicy(outputRole?.Policy, micRole?.Policy),
            NormalizePolicy(_settings.Devices.ActiveRecordingDevicePolicy));

        var nextPlan = ResolvePlanForCurrentMode(_activeSession);
        var defaultDecision = (outputRole?.FollowSystemDefault == true || micRole?.FollowSystemDefault == true)
            ? AutomaticPromptDecision.SwitchNow
            : AutomaticPromptDecision.FinishAndStartNew;

        return new DeviceContinuityDecisionContext(
            _activeSession.SessionId,
            _activeSession.Record.Mode,
            _activeSession.Record.SourceApp,
            _activeSession.Record.SourceProcessId,
            _activeSession.CurrentPlan,
            nextPlan,
            outputRole,
            micRole,
            policy,
            RequiresPrompt: string.Equals(policy, "ask", StringComparison.OrdinalIgnoreCase),
            defaultDecision);
    }

    private DeviceRoleChange? EvaluateDeviceRoleChange(
        string? currentDeviceId,
        bool followSystemDefault,
        string rolePolicy,
        string deviceRole)
    {
        if (string.IsNullOrWhiteSpace(currentDeviceId))
        {
            return null;
        }

        var activeDevices = string.Equals(deviceRole, "output", StringComparison.OrdinalIgnoreCase)
            ? _deviceManager.CurrentSnapshot.RenderDevices.Where(static device => device.IsActive).ToArray()
            : _deviceManager.CurrentSnapshot.CaptureDevices.Where(static device => device.IsActive).ToArray();

        var currentAvailable = activeDevices.Any(device => string.Equals(device.Id, currentDeviceId, StringComparison.OrdinalIgnoreCase));
        var selectedDevice = string.Equals(deviceRole, "output", StringComparison.OrdinalIgnoreCase)
            ? ResolveRenderDevice()
            : ResolveCaptureDevice();
        var nextDeviceId = selectedDevice?.Id;

        if (currentAvailable && string.Equals(currentDeviceId, nextDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var eventType = !currentAvailable
            ? "DEVICE_DISCONNECTED"
            : followSystemDefault
                ? "DEFAULT_DEVICE_CHANGED"
                : "BETTER_MATCH_CONNECTED";

        var currentDevice = activeDevices.FirstOrDefault(device => string.Equals(device.Id, currentDeviceId, StringComparison.OrdinalIgnoreCase));

        return new DeviceRoleChange(
            deviceRole,
            currentDeviceId,
            nextDeviceId,
            currentDevice?.FriendlyName,
            selectedDevice?.FriendlyName,
            followSystemDefault,
            NormalizePolicy(rolePolicy),
            eventType,
            currentAvailable);
    }

    private async Task<AutomaticPromptDecision> RequestDeviceContinuityDecisionAsync(
        DeviceContinuityDecisionContext context,
        CancellationToken cancellationToken)
    {
        if (_promptService is null)
        {
            return context.DefaultDecision;
        }

        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var followDefaultPrompt = context.OutputRole?.FollowSystemDefault == true || context.MicRole?.FollowSystemDefault == true;
        var promptKind = followDefaultPrompt
            ? "device_continuity_follow_default"
            : "device_continuity_pinned";

        var sourceLabel = context.SourceApp ?? context.CurrentPlan.SourceSummary;
        var details = BuildDevicePromptDetails(context);

        var decision = await _promptService.ShowAsync(
            new AutomaticPromptRequest(
                promptKind,
                sourceLabel,
                details,
                TimeoutSeconds: 8),
            promptCancellation.Token).ConfigureAwait(false);

        return decision == AutomaticPromptDecision.Cancelled
            ? context.DefaultDecision
            : decision;
    }

    private async Task ApplyDeviceContinuityDecisionLockedAsync(
        DeviceContinuityDecisionContext context,
        AutomaticPromptDecision decision,
        bool fallbackApplied,
        CancellationToken cancellationToken)
    {
        if (_activeSession is null || _activeSession.SessionId != context.SessionId || _activeSession.IsFinalizing)
        {
            return;
        }

        if (decision == AutomaticPromptDecision.KeepCurrentIfPossible)
        {
            var keepPossible = (context.OutputRole is null || context.OutputRole.CurrentAvailable)
                && (context.MicRole is null || context.MicRole.CurrentAvailable);
            if (keepPossible)
            {
                LogDeviceDecision(context, "kept_current", fallbackApplied);
                return;
            }

            decision = context.NextPlan.Sources.Count > 0
                ? AutomaticPromptDecision.SwitchNow
                : AutomaticPromptDecision.FinishAndStartNew;
        }

        var switched = false;
        var restarted = false;

        if (decision == AutomaticPromptDecision.SwitchNow)
        {
            switched = await TrySeamlessSwitchLockedAsync(context.NextPlan, cancellationToken).ConfigureAwait(false);
            if (!switched)
            {
                restarted = await EndAndRestartLockedAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            restarted = await EndAndRestartLockedAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var decisionResult = switched
            ? "switched"
            : restarted
                ? "ended_and_restarted"
                : "no_available_device";
        LogDeviceDecision(context, decisionResult, fallbackApplied);
        PublishSnapshot();
    }

    private async Task<bool> TrySeamlessSwitchLockedAsync(ResolvedCapturePlan nextPlan, CancellationToken cancellationToken)
    {
        if (_activeSession is null || _activeSession.LiveRecorderSession is null || nextPlan.Sources.Count == 0)
        {
            return false;
        }

        var keepPaused = _activeSession.PauseBlockers.Count > 0;
        _activeSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
        await FinalizeCurrentLegLockedAsync(_activeSession, stopRecorder: true).ConfigureAwait(false);

        var nextLegIndex = _activeSession.CurrentLegIndex + 1;
        var nextLegDirectory = GetLegDirectoryPath(_activeSession.TempRootDirectory, nextLegIndex);
        Directory.CreateDirectory(nextLegDirectory);

        try
        {
            var recorderSession = await _recorderEngine.StartAsync(
                new AudioCaptureRequest(
                    _activeSession.SessionId,
                    ResolveCaptureMode(_activeSession.Record.Mode),
                    nextPlan.Sources,
                    nextLegDirectory,
                    PrebufferSeconds: 0,
                    nextPlan.CreateMixedArtifact),
                cancellationToken).ConfigureAwait(false);

            if (keepPaused)
            {
                await recorderSession.PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            recorderSession.SnapshotChanged += HandleRecorderSnapshotChanged;
            _activeSession.ReplaceLiveRecorder(
                recorderSession,
                nextLegDirectory,
                nextLegIndex,
                nextPlan,
                _timeProvider.GetUtcNow(),
                keepPaused);

            await PersistIntermediateStatusAsync(_activeSession, keepPaused ? "paused" : "recording").ConfigureAwait(false);
            UpdateTickerState();
            return true;
        }
        catch (Exception exception)
        {
            _logger.Warning($"Device continuity seamless switch failed for session {_activeSession.SessionId:N}. {exception.Message}");
            return false;
        }
    }

    private async Task<bool> EndAndRestartLockedAsync(DeviceContinuityDecisionContext context, CancellationToken cancellationToken)
    {
        if (_activeSession is null)
        {
            return false;
        }

        var endedSession = _activeSession;
        endedSession.IsFinalizing = true;
        endedSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
        await PersistIntermediateStatusAsync(endedSession, "stopping").ConfigureAwait(false);
        await FinalizeCurrentLegLockedAsync(endedSession, stopRecorder: true).ConfigureAwait(false);
        await PersistFinalStateAsync(endedSession, discard: false, explicitError: "device_continuity_boundary").ConfigureAwait(false);
        _activeSession = null;

        if (context.NextPlan.Sources.Count == 0)
        {
            UpdateTickerState();
            return false;
        }

        var repository = _sessionRepository
            ?? throw new InvalidOperationException("Meeting session repository is unavailable.");

        var startedSession = await StartRecordingSessionLockedAsync(
            new AutomaticRecordingStartRequest(
                context.Mode,
                context.SourceApp,
                context.SourceProcessId,
                context.NextPlan),
            ResolveCaptureMode(context.Mode),
            repository).ConfigureAwait(false);

        _activeSession = startedSession;
        UpdateTickerState();
        return true;
    }

    private ResolvedCapturePlan ResolvePlanForCurrentMode(ActiveRecordingSession session) =>
        string.Equals(session.Record.Mode, "manual", StringComparison.OrdinalIgnoreCase)
            ? ResolveForceRecordPlan()
            : ResolveAutomaticRecordingPlan(session.Record.SourceProcessId ?? 0);

    private static AudioCaptureMode ResolveCaptureMode(string mode) =>
        string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase)
            ? AudioCaptureMode.Manual
            : AudioCaptureMode.Auto;

    private static string NormalizePolicy(string policy)
    {
        if (string.Equals(policy, "end_and_start_new", StringComparison.OrdinalIgnoreCase))
        {
            return "end_and_start_new";
        }

        if (string.Equals(policy, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return "ask";
        }

        return "seamless_switch";
    }

    private static string CombinePolicy(string? first, string? second)
    {
        if (string.Equals(first, "end_and_start_new", StringComparison.OrdinalIgnoreCase)
            || string.Equals(second, "end_and_start_new", StringComparison.OrdinalIgnoreCase))
        {
            return "end_and_start_new";
        }

        if (string.Equals(first, "ask", StringComparison.OrdinalIgnoreCase)
            || string.Equals(second, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return "ask";
        }

        return "seamless_switch";
    }

    private static string BuildDevicePromptDetails(DeviceContinuityDecisionContext context)
    {
        var roles = new List<string>();
        if (context.OutputRole is not null)
        {
            var oldLabel = context.OutputRole.OldFriendlyName ?? context.OutputRole.OldDeviceId;
            var newLabel = context.OutputRole.NewFriendlyName ?? context.OutputRole.NewDeviceId ?? "none";
            roles.Add($"Output: {oldLabel} → {newLabel}");
        }

        if (context.MicRole is not null)
        {
            var oldLabel = context.MicRole.OldFriendlyName ?? context.MicRole.OldDeviceId;
            var newLabel = context.MicRole.NewFriendlyName ?? context.MicRole.NewDeviceId ?? "none";
            roles.Add($"Mic: {oldLabel} → {newLabel}");
        }

        return string.Join("; ", roles);
    }

    private void LogDeviceDecision(DeviceContinuityDecisionContext context, string decisionResult, bool fallbackApplied)
    {
        var roles = new[] { context.OutputRole, context.MicRole }.Where(static role => role is not null).Cast<DeviceRoleChange>().ToArray();
        foreach (var role in roles)
        {
            _logger.LogEvent(
                "Info",
                "DEVICE_CONTINUITY",
                $"Device continuity decision applied for {role.DeviceRole}.",
                sessionId: context.SessionId.ToString("N"),
                metadata: new Dictionary<string, object?>
                {
                    ["event_type"] = role.EventType,
                    ["device_role"] = role.DeviceRole,
                    ["old_device_id"] = role.OldDeviceId,
                    ["new_device_id"] = role.NewDeviceId,
                    ["policy_applied"] = context.Policy,
                    ["decision_result"] = decisionResult,
                    ["fallback_applied"] = fallbackApplied
                });
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.merge-boundaries
    private void HandleRecorderSnapshotChanged(object? sender, AudioRecorderSessionSnapshot snapshot)
    {
        PublishSnapshot();

        if (snapshot.State is not AudioRecorderState.Completed and not AudioRecorderState.Faulted)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var finalizeAsStop = false;

            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_activeSession is null
                    || _activeSession.IsFinalizing
                    || !ReferenceEquals(_activeSession.LiveRecorderSession, sender))
                {
                    return;
                }

                if (string.Equals(_activeSession.Record.Mode, "manual", StringComparison.OrdinalIgnoreCase)
                    || _settings.Recording.MergeWindowSeconds <= 0)
                {
                    finalizeAsStop = true;
                }
                else
                {
                    _activeSession.RecordAccumulatedDuration(_timeProvider.GetUtcNow());
                    await FinalizeCurrentLegLockedAsync(_activeSession, stopRecorder: false).ConfigureAwait(false);
                    _activeSession.EnterMergePending(_timeProvider.GetUtcNow());
                    await PersistIntermediateStatusAsync(_activeSession, "paused").ConfigureAwait(false);
                    UpdateTickerState();
                    PublishSnapshot();
                }
            }
            finally
            {
                _mutex.Release();
            }

            if (finalizeAsStop)
            {
                await FinalizeActiveSessionAsync(discard: false, explicitError: null).ConfigureAwait(false);
            }
        });
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#coordinator-model.gating
    private bool CanPrepareAutomaticRecording(ResolvedCapturePlan plan, out string? blockingReason)
    {
        if (!_settings.OnboardingCompleted)
        {
            blockingReason = "Finish onboarding before starting a recording.";
            return false;
        }

        if (_sessionRepository is null)
        {
            blockingReason = "Meeting session storage is unavailable.";
            return false;
        }

        if (_privacyPauseEnabled)
        {
            blockingReason = "Privacy Pause is active.";
            return false;
        }

        if (_pendingAutomaticRecording is not null)
        {
            blockingReason = "Another recording confirmation is already pending.";
            return false;
        }

        if (_activeSession is not null)
        {
            blockingReason = "A recording is already active.";
            return false;
        }

        if (plan.Sources.Count == 0)
        {
            blockingReason = plan.BlockingReason ?? "No capture sources are available.";
            return false;
        }

        blockingReason = null;
        return true;
    }

    private bool CanStartForceRecord(out string? blockingReason) =>
        CanStartForceRecord(resolvedPlan: null, out blockingReason);

    private bool CanStartForceRecord(ResolvedCapturePlan? resolvedPlan, out string? blockingReason)
    {
        if (!_settings.OnboardingCompleted)
        {
            blockingReason = "Finish onboarding before starting a recording.";
            return false;
        }

        if (_sessionRepository is null)
        {
            blockingReason = "Meeting session storage is unavailable.";
            return false;
        }

        if (_privacyPauseEnabled)
        {
            blockingReason = "Privacy Pause is active.";
            return false;
        }

        if (_pendingAutomaticRecording is not null)
        {
            blockingReason = "A meeting is waiting for confirmation.";
            return false;
        }

        if (_activeSession is not null)
        {
            blockingReason = "A recording is already active.";
            return false;
        }

        var plan = resolvedPlan ?? ResolveForceRecordPlan();
        if (plan.Sources.Count == 0)
        {
            blockingReason = plan.BlockingReason ?? "No capture sources are available.";
            return false;
        }

        blockingReason = null;
        return true;
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#force-record.sources
    private ResolvedCapturePlan ResolveForceRecordPlan()
    {
        var outputSnapshot = ResolveRenderDevice();
        var micSnapshot = ResolveCaptureDevice();
        var requestedSources = _settings.Recording.DefaultSourcesForce
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = new List<AudioCaptureSourceRequest>();
        if (requestedSources.Contains("device_loopback", StringComparer.OrdinalIgnoreCase) && outputSnapshot is not null)
        {
            sources.Add(AudioCaptureSourceRequest.DeviceLoopback(outputSnapshot.Id));
        }

        if (requestedSources.Contains("mic", StringComparer.OrdinalIgnoreCase) && micSnapshot is not null)
        {
            sources.Add(AudioCaptureSourceRequest.Microphone(micSnapshot.Id));
        }

        return BuildResolvedPlan(
            requestedSources,
            sources,
            outputSnapshot?.Id,
            outputSnapshot?.FriendlyName,
            micSnapshot?.Id,
            micSnapshot?.FriendlyName,
            requestedSources.Length == 0
                ? "Enable at least one Force Record source in Settings."
                : "No selected Force Record devices are currently available.");
    }

    private AudioDeviceSnapshot? ResolveRenderDevice()
    {
        var renderDevices = _deviceManager.CurrentSnapshot.RenderDevices.Where(static device => device.IsActive).ToArray();
        if (renderDevices.Length == 0)
        {
            return null;
        }

        if (_settings.Devices.FollowSystemDefaultOutput)
        {
            return renderDevices.FirstOrDefault(static device => device.IsDefault) ?? renderDevices.FirstOrDefault();
        }

        return renderDevices.FirstOrDefault(device => string.Equals(device.Id, _settings.Devices.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
    }

    private AudioDeviceSnapshot? ResolveCaptureDevice()
    {
        var captureDevices = _deviceManager.CurrentSnapshot.CaptureDevices.Where(static device => device.IsActive).ToArray();
        if (captureDevices.Length == 0)
        {
            return null;
        }

        if (_settings.Devices.FollowSystemDefaultMic)
        {
            return captureDevices.FirstOrDefault(static device => device.IsDefault) ?? captureDevices.FirstOrDefault();
        }

        return captureDevices.FirstOrDefault(device => string.Equals(device.Id, _settings.Devices.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase));
    }

    private void PublishSnapshot(string? transientBlockingReason = null)
    {
        var snapshot = BuildSnapshot(transientBlockingReason);
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private async Task PersistIntermediateStatusAsync(ActiveRecordingSession session, string status)
    {
        if (_sessionRepository is null)
        {
            return;
        }

        session.Record = session.Record with { Status = status };
        await _sessionRepository.UpsertAsync(session.Record, CancellationToken.None).ConfigureAwait(false);
    }

    private ManualControlSnapshot BuildSnapshot(string? transientBlockingReason)
    {
        if (_activeSession is not null)
        {
            var state = _activeSession.IsFinalizing
                ? RecordingActivityState.Stopping
                : _activeSession.IsPausedLike
                    ? RecordingActivityState.Paused
                    : RecordingActivityState.Recording;

            var liveRecorderSnapshot = _activeSession.LiveRecorderSession?.Snapshot;
            var outputAvailability = !_activeSession.CurrentPlan.HasOutput
                ? RecordingSourceAvailability.NotIncluded
                : liveRecorderSnapshot?.Faults.Any(static fault => fault.SourceKind == AudioCaptureSourceKind.DeviceLoopback) == true
                    ? RecordingSourceAvailability.Unavailable
                    : RecordingSourceAvailability.Active;

            var microphoneAvailability = !_activeSession.CurrentPlan.HasMicrophone
                ? RecordingSourceAvailability.NotIncluded
                : liveRecorderSnapshot?.Faults.Any(static fault => fault.SourceKind == AudioCaptureSourceKind.Microphone) == true
                    ? RecordingSourceAvailability.Unavailable
                    : RecordingSourceAvailability.Active;

            return new ManualControlSnapshot(
                state,
                _privacyPauseEnabled,
                CanStartForceRecord: false,
                CanPause: _activeSession.CanUserPause,
                CanResume: _activeSession.CanUserResume,
                CanStop: state is RecordingActivityState.Recording or RecordingActivityState.Paused,
                CanDiscard: state is RecordingActivityState.Recording or RecordingActivityState.Paused,
                BlockingReason: liveRecorderSnapshot?.Faults.LastOrDefault()?.Message,
                CurrentRecording: new CurrentRecordingSnapshot(
                    _activeSession.SessionId,
                    _activeSession.Record.Mode,
                    _activeSession.CurrentPlan.SourceType,
                    _activeSession.CurrentPlan.SourceSummary,
                    _activeSession.Record.SourceApp,
                    _activeSession.CurrentPlan.OutputDeviceName,
                    _activeSession.CurrentPlan.MicrophoneDeviceName,
                    outputAvailability,
                    microphoneAvailability,
                    _activeSession.CreatedAtUtc,
                    _activeSession.GetRecordedDuration(_timeProvider.GetUtcNow()),
                    state switch
                    {
                        RecordingActivityState.Paused when _activeSession.IsMergePending => "Waiting for the meeting to resume",
                        RecordingActivityState.Paused => "Paused",
                        RecordingActivityState.Stopping => "Stopping",
                        _ => "Recording"
                    },
                    _activeSession.GetBlockingReason(_privacyPauseEnabled)),
                PendingConfirmation: null);
        }

        if (_pendingAutomaticRecording is not null)
        {
            return new ManualControlSnapshot(
                RecordingActivityState.AwaitingConfirmation,
                _privacyPauseEnabled,
                CanStartForceRecord: false,
                CanPause: false,
                CanResume: false,
                CanStop: false,
                CanDiscard: false,
                transientBlockingReason ?? $"Awaiting confirmation for {_pendingAutomaticRecording.SourceApp ?? _pendingAutomaticRecording.Plan.SourceSummary}.",
                CurrentRecording: null,
                PendingConfirmation: new PendingRecordingConfirmationSnapshot(
                    _pendingAutomaticRecording.SessionId,
                    _pendingAutomaticRecording.Mode,
                    _pendingAutomaticRecording.PromptKind,
                    _pendingAutomaticRecording.Plan.SourceSummary,
                    _pendingAutomaticRecording.SourceApp,
                    _pendingAutomaticRecording.CreatedAtUtc));
        }

        if (!_settings.OnboardingCompleted)
        {
            return new ManualControlSnapshot(
                RecordingActivityState.OnboardingBlocked,
                _privacyPauseEnabled,
                CanStartForceRecord: false,
                CanPause: false,
                CanResume: false,
                CanStop: false,
                CanDiscard: false,
                transientBlockingReason ?? "Finish onboarding before starting a recording.",
                CurrentRecording: null,
                PendingConfirmation: null);
        }

        var canStart = CanStartForceRecord(out var blockingReason);
        return new ManualControlSnapshot(
            RecordingActivityState.Idle,
            _privacyPauseEnabled,
            CanStartForceRecord: canStart,
            CanPause: false,
            CanResume: false,
            CanStop: false,
            CanDiscard: false,
            transientBlockingReason ?? blockingReason,
            CurrentRecording: null,
            PendingConfirmation: null);
    }

    private void UpdateTickerState()
    {
        var shouldTick = _activeSession is { IsFinalizing: false, IsPausedLike: false };
        _ticker.Change(shouldTick ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, shouldTick ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan);
    }

    private async Task<ActiveRecordingSession> StartRecordingSessionLockedAsync(
        AutomaticRecordingStartRequest request,
        AudioCaptureMode captureMode,
        MeetingSessionRepository repository)
    {
        var createdAtUtc = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var tempRoot = _artifactPathResolver.GetTempSessionDirectoryPath(_settings, sessionId);
        var legDirectory = GetLegDirectoryPath(tempRoot, 1);
        Directory.CreateDirectory(legDirectory);

        var sessionRecord = CreateSessionRecord(
            sessionId,
            createdAtUtc,
            request.Mode,
            request.SourceApp,
            request.SourceProcessId,
            request.Plan);

        await repository.UpsertAsync(sessionRecord, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var recorderSession = await _recorderEngine.StartAsync(
                new AudioCaptureRequest(
                    sessionId,
                    captureMode,
                    request.Plan.Sources,
                    legDirectory,
                    PrebufferSeconds: 0,
                    request.Plan.CreateMixedArtifact),
                CancellationToken.None).ConfigureAwait(false);

            recorderSession.SnapshotChanged += HandleRecorderSnapshotChanged;
            return new ActiveRecordingSession(
                sessionId,
                sessionRecord,
                tempRoot,
                createdAtUtc,
                request.Plan,
                _settings.Recording.PrivacyPausePolicy,
                recorderSession,
                legDirectory,
                1);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Recording start failed for session {sessionId:N}.");
            await repository.UpsertAsync(
                sessionRecord with
                {
                    EndedAtUtc = _timeProvider.GetUtcNow(),
                    Status = "failed",
                    ErrorCode = request.Mode == "manual" ? "force_record_start_failed" : "automatic_recording_start_failed",
                    ErrorMessage = exception.Message
                },
                CancellationToken.None).ConfigureAwait(false);

            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private async Task FinalizeCurrentLegLockedAsync(ActiveRecordingSession session, bool stopRecorder)
    {
        var recorderSession = session.LiveRecorderSession;
        if (recorderSession is null)
        {
            return;
        }

        recorderSession.SnapshotChanged -= HandleRecorderSnapshotChanged;

        try
        {
            if (stopRecorder)
            {
                await recorderSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            session.AddCompletedLeg(recorderSession.Snapshot.Artifacts, session.CurrentLegDirectory ?? string.Empty);
            await recorderSession.DisposeAsync().ConfigureAwait(false);
            session.ClearLiveRecorder();
        }
    }

    private static async Task CleanupPreparedRecordingAsync(PreparedAutomaticRecordingSession preparedSession)
    {
        try
        {
            await preparedSession.RecorderSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            await preparedSession.RecorderSession.DisposeAsync().ConfigureAwait(false);
            TryDeleteDirectory(preparedSession.TempRootDirectory);
        }
    }

    private MeetingSessionRecord CreateSessionRecord(
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string mode,
        string? sourceApp,
        int? sourceProcessId,
        ResolvedCapturePlan plan) =>
        MeetingSessionRecord.Create(
            sessionId,
            createdAtUtc,
            mode,
            plan.SourceType,
            plan.OutputDeviceId,
            plan.MicrophoneDeviceId) with
        {
            SourceApp = sourceApp,
            SourceProcessId = sourceProcessId,
            TranscriptionModel = _settings.Transcription.Model,
            DiarizationEnabled = _settings.Transcription.Diarization,
            Language = _settings.Transcription.Language
        };

     private static ResolvedCapturePlan BuildResolvedPlan(
         IReadOnlyCollection<string> requestedSources,
         IReadOnlyList<AudioCaptureSourceRequest> sources,
         string? outputDeviceId,
        string? outputDeviceName,
        string? microphoneDeviceId,
         string? microphoneDeviceName,
         string emptyPlanReason)
     {
         // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.sources
         var sourceType = sources.Count switch
         {
             0 => "mixed",
             1 when sources[0].Kind == AudioCaptureSourceKind.ProcessOutput => "process",
            1 when sources[0].Kind == AudioCaptureSourceKind.DeviceLoopback => "device",
            1 when sources[0].Kind == AudioCaptureSourceKind.Microphone => "mic",
            _ => "mixed"
        };

        var sourceSummary = sources.Count switch
        {
            0 => "No sources resolved",
            1 when sources[0].Kind == AudioCaptureSourceKind.ProcessOutput => "Process output",
            1 when sources[0].Kind == AudioCaptureSourceKind.DeviceLoopback => "Output",
            1 when sources[0].Kind == AudioCaptureSourceKind.Microphone => "Microphone",
             _ when sources.Any(static source => source.Kind == AudioCaptureSourceKind.ProcessOutput) && sources.Any(static source => source.Kind == AudioCaptureSourceKind.Microphone) => "Process output + Microphone",
             _ => "Output + Microphone"
         };

         var mergeSourceFamily =
             sources.Any(static source => source.Kind == AudioCaptureSourceKind.ProcessOutput)
                 ? sources.Any(static source => source.Kind == AudioCaptureSourceKind.Microphone) ? "process+mic" : "process"
                 : sources.Any(static source => source.Kind == AudioCaptureSourceKind.DeviceLoopback)
                     ? sources.Any(static source => source.Kind == AudioCaptureSourceKind.Microphone) ? "device+mic" : "device"
                     : sources.Any(static source => source.Kind == AudioCaptureSourceKind.Microphone)
                         ? "mic"
                         : "none";

         return new ResolvedCapturePlan(
             sources,
             CreateMixedArtifact: false,
             sourceType,
             mergeSourceFamily,
             sourceSummary,
             BlockingReason: sources.Count > 0 ? null : emptyPlanReason,
             outputDeviceId,
            outputDeviceName,
            microphoneDeviceId,
            microphoneDeviceName);
    }

    private static string GetLegDirectoryPath(string tempRootDirectory, int legIndex) =>
        Path.Combine(tempRootDirectory, $"leg-{legIndex:000}");

    private static string GetArtifactBaseName(AudioCaptureArtifactKind kind) => kind switch
    {
        AudioCaptureArtifactKind.Output => "output",
        AudioCaptureArtifactKind.Microphone => "microphone",
        AudioCaptureArtifactKind.Mixed => "mix",
        _ => "artifact"
    };

    private static void MoveArtifactBestEffort(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch (IOException)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            File.Delete(sourcePath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<string?> TryCompressArtifactAsync(string? wavPath, bool isMicrophone)
    {
        if (wavPath is null || !File.Exists(wavPath))
        {
            return wavPath;
        }

        var compressed = await _audioCompressor.CompressToOpusAsync(wavPath, isMicrophone, CancellationToken.None).ConfigureAwait(false);
        if (compressed is null)
        {
            return wavPath; // Compression failed; keep WAV.
        }

        if (!_settings.Storage.KeepRawAfterSuccess)
        {
            try { File.Delete(wavPath); } catch { }
        }

        return compressed;
    }

    private sealed class PreparedAutomaticRecordingSession(
        Guid sessionId,
        string mode,
        string promptKind,
        string? sourceApp,
        int? sourceProcessId,
        DateTimeOffset createdAtUtc,
        string tempRootDirectory,
        string currentLegDirectory,
        ResolvedCapturePlan plan,
        AudioRecorderSession recorderSession)
    {
        public Guid SessionId { get; } = sessionId;

        public string Mode { get; } = mode;

        public string PromptKind { get; } = promptKind;

        public string? SourceApp { get; } = sourceApp;

        public int? SourceProcessId { get; } = sourceProcessId;

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public string TempRootDirectory { get; } = tempRootDirectory;

        public string CurrentLegDirectory { get; } = currentLegDirectory;

        public ResolvedCapturePlan Plan { get; } = plan;

        public AudioRecorderSession RecorderSession { get; } = recorderSession;
    }

    private sealed class ActiveRecordingSession(
        Guid sessionId,
        MeetingSessionRecord record,
        string tempRootDirectory,
        DateTimeOffset createdAtUtc,
        ResolvedCapturePlan currentPlan,
        string privacyPausePolicy,
        AudioRecorderSession liveRecorderSession,
        string currentLegDirectory,
        int currentLegIndex)
    {
        private TimeSpan _recordedDuration = TimeSpan.Zero;
        private DateTimeOffset? _lastResumedAtUtc = createdAtUtc;

        public Guid SessionId { get; } = sessionId;

        public MeetingSessionRecord Record { get; set; } = record;

        public string TempRootDirectory { get; } = tempRootDirectory;

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public ResolvedCapturePlan CurrentPlan { get; private set; } = currentPlan;

        public string PrivacyPausePolicy { get; } = privacyPausePolicy;

        public HashSet<string> PauseBlockers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CompletedCaptureLeg> CompletedLegs { get; } = [];

        public AudioRecorderSession? LiveRecorderSession { get; private set; } = liveRecorderSession;

        public string? CurrentLegDirectory { get; private set; } = currentLegDirectory;

        public int CurrentLegIndex { get; private set; } = currentLegIndex;

        public bool IsFinalizing { get; set; }

        public bool IsMergePending { get; private set; }

        public DateTimeOffset? MergePendingStartedAtUtc { get; private set; }

        public bool IsPausedLike => IsMergePending || PauseBlockers.Count > 0;

        public bool CanUserPause =>
            LiveRecorderSession is not null
            && !IsFinalizing
            && !IsMergePending
            && PauseBlockers.Count == 0;

        public bool CanUserResume =>
            !IsFinalizing
            && !IsMergePending
            && LiveRecorderSession is not null
            && PauseBlockers.Contains(ManualPauseBlocker);

        public TimeSpan GetRecordedDuration(DateTimeOffset now) =>
            PauseBlockers.Count == 0 && !IsFinalizing && !IsMergePending && _lastResumedAtUtc is DateTimeOffset resumedAtUtc
                ? _recordedDuration + (now - resumedAtUtc)
                : _recordedDuration;

        public void RecordAccumulatedDuration(DateTimeOffset now)
        {
            if (_lastResumedAtUtc is DateTimeOffset resumedAtUtc)
            {
                _recordedDuration += now - resumedAtUtc;
                _lastResumedAtUtc = null;
            }
        }

        public void ResumeAt(DateTimeOffset now)
        {
            _lastResumedAtUtc = now;
        }

        public void EnterMergePending(DateTimeOffset now)
        {
            RecordAccumulatedDuration(now);
            PauseBlockers.Add(MergeWindowBlocker);
            IsMergePending = true;
            MergePendingStartedAtUtc = now;
        }

        public void ResumeFromMergePending(
            AudioRecorderSession recorderSession,
            string legDirectory,
            int legIndex,
            ResolvedCapturePlan nextPlan,
            DateTimeOffset resumedAtUtc)
        {
            LiveRecorderSession = recorderSession;
            CurrentLegDirectory = legDirectory;
            CurrentLegIndex = legIndex;
            CurrentPlan = nextPlan;
            IsMergePending = false;
            MergePendingStartedAtUtc = null;
            PauseBlockers.Remove(MergeWindowBlocker);
            ResumeAt(resumedAtUtc);
        }

        public void AddCompletedLeg(IReadOnlyList<AudioCaptureArtifact> artifacts, string legDirectory)
        {
            if (artifacts.Count == 0)
            {
                return;
            }

            CompletedLegs.Add(new CompletedCaptureLeg(legDirectory, artifacts.ToArray()));
        }

        public void ClearLiveRecorder()
        {
            LiveRecorderSession = null;
            CurrentLegDirectory = null;
        }

        public void ReplaceLiveRecorder(
            AudioRecorderSession recorderSession,
            string legDirectory,
            int legIndex,
            ResolvedCapturePlan nextPlan,
            DateTimeOffset switchedAtUtc,
            bool keepPaused)
        {
            LiveRecorderSession = recorderSession;
            CurrentLegDirectory = legDirectory;
            CurrentLegIndex = legIndex;
            CurrentPlan = nextPlan;
            if (keepPaused)
            {
                _lastResumedAtUtc = null;
            }
            else
            {
                ResumeAt(switchedAtUtc);
            }
        }

        public string? GetBlockingReason(bool privacyPauseEnabled)
        {
            if (IsMergePending && PauseBlockers.Contains(MergeWindowBlocker))
            {
                return "Waiting to see if the meeting resumes.";
            }

            if (PauseBlockers.Contains(ManualPauseBlocker))
            {
                return "Paused manually.";
            }

            if (privacyPauseEnabled && PauseBlockers.Contains(PrivacyPauseBlocker))
            {
                return "Privacy Pause is active.";
            }

            return null;
        }
    }

    private sealed record CompletedCaptureLeg(
        string LegDirectory,
        IReadOnlyList<AudioCaptureArtifact> Artifacts);

    private sealed record DeviceRoleChange(
        string DeviceRole,
        string OldDeviceId,
        string? NewDeviceId,
        string? OldFriendlyName,
        string? NewFriendlyName,
        bool FollowSystemDefault,
        string Policy,
        string EventType,
        bool CurrentAvailable);

    private sealed record DeviceContinuityDecisionContext(
        Guid SessionId,
        string Mode,
        string? SourceApp,
        int? SourceProcessId,
        ResolvedCapturePlan CurrentPlan,
        ResolvedCapturePlan NextPlan,
        DeviceRoleChange? OutputRole,
        DeviceRoleChange? MicRole,
        string Policy,
        bool RequiresPrompt,
        AutomaticPromptDecision DefaultDecision);
}
