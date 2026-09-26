using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Presentation state for the compact meeting-recorder surface.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaximumRecentItems = 5;
    private readonly IApplicationRuntime _runtime;
    private readonly IPlatformShell _shell;
    private readonly ILocalizationService _strings;
    private readonly TimeProvider _timeProvider;
    private ApplicationRuntimeSnapshot _lastSnapshot = ApplicationRuntimeSnapshot.Initial;
    private ITimer? _elapsedTimer;
    private Guid? _activeMeetingSessionId;
    private TimeSpan _activeMeetingDuration;
    private DateTimeOffset? _activeMeetingDurationMeasuredAtUtc;
    private bool _activeMeetingTimerRunning;
    private bool _disposed;
    private bool _isBusy = true;
    private bool _initializationSucceeded;
    private bool _serviceEnabled;
    private ApplicationActivityState _activity = ApplicationActivityState.Paused;
    private string _stateKicker = string.Empty;
    private string _stateTitle = string.Empty;
    private string _stateDescription = string.Empty;
    private string? _activeSourceLabel;
    private string _elapsedLabel = string.Empty;
    private string? _attentionMessage;
    private string? _recoveryPath;
    private Guid _recoverySessionId;
    private double? _progress;
    private string? _transientAttentionKey;
    private bool _initializationFailed;

    public MainWindowViewModel(
        IApplicationRuntime runtime,
        IPlatformShell shell,
        ILocalizationService strings,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime;
        _shell = shell;
        _strings = strings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runtime.SnapshotChanged += HandleSnapshotChanged;
        _strings.LanguageChanged += HandleLanguageChanged;

        ToggleServiceCommand = new AsyncRelayCommand(ToggleServiceAsync, CanToggleService);
        StartManualRecordingCommand = new AsyncRelayCommand(StartManualRecordingAsync, CanStartManualRecording);
        PauseOrResumeCommand = new AsyncRelayCommand(PauseOrResumeAsync, CanPauseOrResume);
        FinishRecordingCommand = new AsyncRelayCommand(FinishRecordingAsync, CanFinishRecording);
        OpenRecoveryFolderCommand = new AsyncRelayCommand(OpenRecoveryFolderAsync, () => HasRecoveryAction);
        RetryCapabilitiesCommand = new AsyncRelayCommand(RetryCapabilitiesAsync, CanRetryCapabilities);
        RetryInitializationCommand = new RelayCommand(
            () => InitializationRetryRequested?.Invoke(this, EventArgs.Empty),
            () => ShowInitializationRetryAction);
        OpenDiagnosticsCommand = new AsyncRelayCommand(OpenDiagnosticsAsync);
        RequestDiscardCommand = new RelayCommand(
            () => DiscardRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy && ShowRecordingActions);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenHelpCommand = new RelayCommand(() => HelpRequested?.Invoke(this, EventArgs.Empty));

        ApplySnapshot(ApplicationRuntimeSnapshot.Initial);
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? HelpRequested;

    public event EventHandler? DiagnosticsRequested;

    public event EventHandler? InitializationRetryRequested;

    public event EventHandler? DiscardRequested;

    public event EventHandler<RecentRecordingRemovalRequestedEventArgs>? RecentRecordingRemovalRequested;

    public event EventHandler<RecentRecordingRenameRequestedEventArgs>? RecentRecordingRenameRequested;

    public event EventHandler<RecentRecordingTranscriptionRequestedEventArgs>? RecentRecordingTranscriptionRequested;

    public ObservableCollection<RecentRecordingItemViewModel> RecentRecordings { get; } = [];

    public IAsyncRelayCommand ToggleServiceCommand { get; }

    public IAsyncRelayCommand StartManualRecordingCommand { get; }

    public IAsyncRelayCommand PauseOrResumeCommand { get; }

    public IAsyncRelayCommand FinishRecordingCommand { get; }

    public IAsyncRelayCommand OpenRecoveryFolderCommand { get; }

    public IAsyncRelayCommand RetryCapabilitiesCommand { get; }

    public IRelayCommand RetryInitializationCommand { get; }

    public IAsyncRelayCommand OpenDiagnosticsCommand { get; }

    public IRelayCommand RequestDiscardCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    public IRelayCommand OpenHelpCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowBusyIndicator));
            OnPropertyChanged(nameof(ShowInitializationRetryAction));
            NotifyCommandStates();
        }
    }

    public bool ShowBusyIndicator => IsBusy || Activity == ApplicationActivityState.Processing;

    public bool InitializationSucceeded
    {
        get => _initializationSucceeded;
        private set
        {
            if (SetProperty(ref _initializationSucceeded, value))
            {
                OnPropertyChanged(nameof(ShowInitializationRetryAction));
                RetryInitializationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ServiceEnabled
    {
        get => _serviceEnabled;
        private set
        {
            if (!SetProperty(ref _serviceEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ServiceStatusLabel));
            OnPropertyChanged(nameof(ServiceActionLabel));
            OnPropertyChanged(nameof(ShowEnableServiceAction));
            NotifyCommandStates();
        }
    }

    public string ServiceStatusLabel => _strings.Get(
        ServiceEnabled ? "String.Service.Listening" : "String.Service.Paused");

    public string ServiceActionLabel => _strings.Get(
        ServiceEnabled ? "String.Action.DisableService" : "String.Action.EnableService");

    public ApplicationActivityState Activity
    {
        get => _activity;
        private set
        {
            if (!SetProperty(ref _activity, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowManualRecordingAction));
            OnPropertyChanged(nameof(ShowRecordingActions));
            OnPropertyChanged(nameof(ShowEnableServiceAction));
            OnPropertyChanged(nameof(ShowBusyIndicator));
            OnPropertyChanged(nameof(ShowStateKicker));
            NotifyCommandStates();
        }
    }

    public string StateKicker
    {
        get => _stateKicker;
        private set => SetProperty(ref _stateKicker, value);
    }

    public bool ShowStateKicker => Activity is not ApplicationActivityState.Listening
        and not ApplicationActivityState.Ready;

    public string StateTitle
    {
        get => _stateTitle;
        private set => SetProperty(ref _stateTitle, value);
    }

    public string StateDescription
    {
        get => _stateDescription;
        private set => SetProperty(ref _stateDescription, value);
    }

    public string? ActiveSourceLabel
    {
        get => _activeSourceLabel;
        private set
        {
            if (SetProperty(ref _activeSourceLabel, value))
            {
                OnPropertyChanged(nameof(HasActiveSource));
            }
        }
    }

    public bool HasActiveSource => !string.IsNullOrWhiteSpace(ActiveSourceLabel);

    public string ElapsedLabel
    {
        get => _elapsedLabel;
        private set => SetProperty(ref _elapsedLabel, value);
    }

    public bool ShowElapsed => Activity == ApplicationActivityState.Recording && _activeMeetingSessionId.HasValue;

    public string PauseOrResumeLabel => _strings.Get(
        _lastSnapshot.ActiveMeeting?.IsPaused == true ? "String.Action.Resume" : "String.Action.Pause");

    // @spec spec://modules/app/FEAT-013.A-floating-recording-widget#controls
    public bool IsActiveRecordingPaused =>
        Activity == ApplicationActivityState.Recording
        && _lastSnapshot.ActiveMeeting?.IsPaused == true;

    // @spec spec://modules/app/FEAT-013.A-floating-recording-widget#controls
    public bool IsActiveRecordingRunning =>
        Activity == ApplicationActivityState.Recording
        && _lastSnapshot.ActiveMeeting is { IsPaused: false };

    public string? AttentionMessage
    {
        get => _attentionMessage;
        private set
        {
            if (SetProperty(ref _attentionMessage, value))
            {
                OnPropertyChanged(nameof(HasAttention));
                OnPropertyChanged(nameof(ShowInitializationRetryAction));
                OnPropertyChanged(nameof(ShowDiagnosticsRecoveryAction));
                RetryInitializationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasAttention => !string.IsNullOrWhiteSpace(AttentionMessage);

    public bool HasRecoveryAction => !string.IsNullOrWhiteSpace(_recoveryPath);

    public bool HasCapabilityRetryAction => _lastSnapshot.ServiceEnabled
        && IsActionableCapability(_lastSnapshot.Capability);

    public bool ShowInitializationRetryAction => !InitializationSucceeded && !IsBusy && HasAttention;

    public bool ShowDiagnosticsRecoveryAction => HasAttention
        && !HasRecoveryAction
        && !HasCapabilityRetryAction;

    public double? Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(HasProgress));
            }
        }
    }

    public bool HasProgress => Progress.HasValue;

    public bool ShowManualRecordingAction =>
        Activity is ApplicationActivityState.Listening
            or ApplicationActivityState.Suspected;

    public bool ShowRecordingActions => Activity == ApplicationActivityState.Recording;

    public bool ShowEnableServiceAction => Activity == ApplicationActivityState.Paused && !ServiceEnabled;

    public bool HasRecentRecordings => RecentRecordings.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        InitializationSucceeded = false;
        _initializationFailed = false;
        _transientAttentionKey = null;
        AttentionMessage = null;
        try
        {
            await _runtime.InitializeAsync(cancellationToken);
            ApplySnapshot(_runtime.Snapshot);
            InitializationSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            InitializationSucceeded = false;
            _initializationFailed = true;
            ApplyInitializationFailure();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task ConfirmDiscardAsync() => ExecuteRuntimeActionAsync(_runtime.DiscardRecordingAsync);

    public Task ConfirmRecentRemovalAsync(Guid sessionId, bool deleteAudioFile) => ExecuteRuntimeActionAsync(
        cancellationToken => _runtime.RemoveRecentRecordingAsync(sessionId, deleteAudioFile, cancellationToken));

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    public Task ConfirmRecentRenameAsync(Guid sessionId, string displayTitle) => ExecuteRuntimeActionAsync(
        cancellationToken => _runtime.RenameRecentRecordingAsync(sessionId, displayTitle, cancellationToken),
        "String.Error.RenameRecording");

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
    public Task ConfirmRecentTranscriptionAsync(Guid sessionId, bool replaceExisting) =>
        ExecuteTranscriptionRuntimeActionAsync(
            cancellationToken => _runtime.TranscribeRecentRecordingAsync(
                sessionId,
                replaceExisting,
                cancellationToken));

    public void ReportRecordingActionFailure() =>
        ShowTransientAttention("String.Error.RecordingUnavailable");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.SnapshotChanged -= HandleSnapshotChanged;
        _strings.LanguageChanged -= HandleLanguageChanged;
        _elapsedTimer?.Dispose();
        foreach (var item in RecentRecordings)
        {
            item.Dispose();
        }
    }

    private bool CanToggleService() => !IsBusy && Activity != ApplicationActivityState.Recording;

    private bool CanStartManualRecording() => !IsBusy && ServiceEnabled && ShowManualRecordingAction;

    private bool CanPauseOrResume() => !IsBusy && ShowRecordingActions;

    private bool CanFinishRecording() => !IsBusy && ShowRecordingActions;

    private Task ToggleServiceAsync() => ExecuteRuntimeActionAsync(
        cancellationToken => _runtime.SetServiceEnabledAsync(!ServiceEnabled, cancellationToken));

    private Task StartManualRecordingAsync() => ExecuteRuntimeActionAsync(_runtime.StartManualRecordingAsync);

    private Task PauseOrResumeAsync() => ExecuteRuntimeActionAsync(_runtime.PauseOrResumeAsync);

    private Task FinishRecordingAsync() => ExecuteRuntimeActionAsync(_runtime.FinishRecordingAsync);

    private Task RetryCapabilitiesAsync() => ExecuteRuntimeActionAsync(_runtime.RefreshCapabilitiesAsync);

    private bool CanRetryCapabilities() => !IsBusy && HasCapabilityRetryAction;

    private async Task OpenRecoveryFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_recoveryPath))
        {
            return;
        }

        try
        {
            await _shell.OpenContainingFolderAsync(_recoveryPath, CancellationToken.None);
            if (_recoverySessionId != Guid.Empty)
            {
                await _runtime.AcknowledgeAttentionAsync(_recoverySessionId, CancellationToken.None);
            }
        }
        catch
        {
            ShowTransientAttention("String.Error.OpenRecovery");
        }
    }

    private async Task OpenDiagnosticsAsync()
    {
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        if (!InitializationSucceeded)
        {
            return;
        }

        try
        {
            await _runtime.AcknowledgeAttentionAsync(_recoverySessionId, CancellationToken.None);
        }
        catch
        {
            ShowTransientAttention("String.Error.Action");
        }
    }

    private async Task ExecuteRuntimeActionAsync(
        Func<CancellationToken, ValueTask> action,
        string failureResourceKey = "String.Error.Action")
    {
        IsBusy = true;
        _transientAttentionKey = null;
        AttentionMessage = null;
        try
        {
            await action(CancellationToken.None);
        }
        catch
        {
            ShowTransientAttention(failureResourceKey);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteTranscriptionRuntimeActionAsync(
        Func<CancellationToken, ValueTask> action)
    {
        _transientAttentionKey = null;
        AttentionMessage = null;
        try
        {
            await action(CancellationToken.None);
        }
        catch (TranscriptionCommandException exception)
        {
            ShowTransientAttention(MapTranscriptionCommandError(exception.Code));
        }
        catch
        {
            ShowTransientAttention("String.Transcription.Error.Action");
        }
    }

    private static string MapTranscriptionCommandError(string code) =>
        code switch
        {
            "engine_not_selected" => "String.Transcription.Error.ServiceRequired",
            "credential_required" => "String.Transcription.Error.KeyRequired",
            "remote_consent_required" => "String.Transcription.Error.ConsentRequired",
            "model_unavailable" or "model_discovery_required" => "String.Transcription.Error.ModelRequired",
            "model_missing" or "model_corrupt" => "String.Transcription.Error.LocalModelRequired",
            "diarization_assets_missing" => "String.Transcription.Error.SpeakerComponents",
            "diarization_runtime_missing" => "String.Transcription.Error.SpeakerRuntime",
            "timestamp_capability_missing" => "String.Transcription.Error.WordTiming",
            "insufficient_memory" => "String.Transcription.Error.LocalMemory",
            "insufficient_disk" => "String.Transcription.Error.LocalDisk",
            "backend_unavailable" or "backend_probe_failed" or "native_failure" or "native_crash" =>
                "String.Transcription.Error.LocalBackend",
            "low_power" or "energy_saver" => "String.Transcription.Error.LocalLowPower",
            "power_override_unavailable" => "String.Transcription.Error.LocalPowerOverride",
            "recording_not_ready" or "recording_unavailable" or "recording_empty" or "input_missing" =>
                "String.Transcription.Error.RecordingUnavailable",
            _ => "String.Transcription.Error.Action"
        };

    private void HandleSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
        => ApplySnapshotCore(_lastSnapshot, preserveTransientAttention: true);

    private void ApplySnapshot(ApplicationRuntimeSnapshot snapshot) =>
        ApplySnapshotCore(snapshot, preserveTransientAttention: false);

    private void ApplySnapshotCore(
        ApplicationRuntimeSnapshot snapshot,
        bool preserveTransientAttention)
    {
        if (!preserveTransientAttention)
        {
            _transientAttentionKey = null;
        }

        _lastSnapshot = snapshot;
        ServiceEnabled = snapshot.ServiceEnabled;
        Activity = snapshot.Activity;
        StateKicker = MapStateKicker(snapshot.Activity);
        StateTitle = _strings.Get($"String.State.{snapshot.Activity}.Title");
        StateDescription = MapStateDescription(snapshot);
        ActiveSourceLabel = snapshot.ActiveMeeting is { } activeMeeting
            ? MeetingSourceLabelLocalizer.Localize(_strings, activeMeeting.SourceLabel)
            : snapshot.PendingMeetingPrompt is { } pendingPrompt
                ? MeetingSourceLabelLocalizer.Localize(
                    _strings,
                    pendingPrompt.SourceLabel,
                    pendingPrompt.ProfileId)
                : null;
        UpdateElapsedTimer(snapshot.ActiveMeeting);
        Progress = snapshot.RecordingFinalization?.Progress;
        _recoveryPath = snapshot.RecordingFinalization?.RecoverableAudioPath;
        _recoverySessionId = snapshot.RecordingFinalization?.SessionId ?? Guid.Empty;
        OnPropertyChanged(nameof(HasRecoveryAction));
        OnPropertyChanged(nameof(HasCapabilityRetryAction));
        OnPropertyChanged(nameof(ShowDiagnosticsRecoveryAction));
        OpenRecoveryFolderCommand.NotifyCanExecuteChanged();
        RetryCapabilitiesCommand.NotifyCanExecuteChanged();
        AttentionMessage = _transientAttentionKey is null
            ? MapAttentionMessage(snapshot)
            : _strings.Get(_transientAttentionKey);

        if (_initializationFailed)
        {
            ApplyInitializationFailure();
        }

        OnPropertyChanged(nameof(ServiceStatusLabel));
        OnPropertyChanged(nameof(ServiceActionLabel));
        OnPropertyChanged(nameof(PauseOrResumeLabel));
        OnPropertyChanged(nameof(IsActiveRecordingPaused));
        OnPropertyChanged(nameof(IsActiveRecordingRunning));
        ReconcileRecentRecordings(snapshot.RecentRecordings);
        NotifyCommandStates();
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    private void UpdateElapsedTimer(ActiveMeetingSnapshot? activeMeeting)
    {
        var active = Activity == ApplicationActivityState.Recording ? activeMeeting : null;
        var nowUtc = _timeProvider.GetUtcNow();
        var hasMeasuredDuration = active?.ActiveDurationMeasuredAtUtc is not null;
        var duration = active is null
            ? TimeSpan.Zero
            : hasMeasuredDuration
                ? active.ActiveDuration
                : nowUtc - active.StartedAtUtc;
        var measuredAtUtc = active is null
            ? null
            : hasMeasuredDuration
                ? active.ActiveDurationMeasuredAtUtc
                : nowUtc;
        var timerShouldRun = active is { IsPaused: false };
        var visibilityChanged = _activeMeetingSessionId != active?.SessionId;
        var timerStateChanged = _activeMeetingTimerRunning != timerShouldRun;

        _activeMeetingSessionId = active?.SessionId;
        _activeMeetingDuration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        _activeMeetingDurationMeasuredAtUtc = measuredAtUtc;
        _activeMeetingTimerRunning = timerShouldRun;

        if (visibilityChanged)
        {
            OnPropertyChanged(nameof(ShowElapsed));
        }

        if (timerStateChanged || (!timerShouldRun && _elapsedTimer is not null))
        {
            _elapsedTimer?.Dispose();
            _elapsedTimer = null;
        }

        UpdateElapsedLabel();
        if (!timerShouldRun || _elapsedTimer is not null)
        {
            return;
        }

        _elapsedTimer = _timeProvider.CreateTimer(
            static state => ((MainWindowViewModel)state!).QueueElapsedRefresh(),
            this,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void QueueElapsedRefresh()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateElapsedLabel();
            return;
        }

        Dispatcher.UIThread.Post(UpdateElapsedLabel);
    }

    private void UpdateElapsedLabel()
    {
        if (!_activeMeetingSessionId.HasValue)
        {
            ElapsedLabel = string.Empty;
            return;
        }

        var elapsed = _activeMeetingDuration;
        if (_activeMeetingTimerRunning && _activeMeetingDurationMeasuredAtUtc is { } measuredAtUtc)
        {
            var sinceMeasurement = _timeProvider.GetUtcNow() - measuredAtUtc;
            if (sinceMeasurement > TimeSpan.Zero)
            {
                elapsed += sinceMeasurement;
            }
        }

        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        ElapsedLabel = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    }

    private string MapStateKicker(ApplicationActivityState state) => _strings.Get(state switch
    {
        ApplicationActivityState.Listening => "String.State.Kicker.Ready",
        ApplicationActivityState.Suspected => "String.State.Kicker.Working",
        ApplicationActivityState.AwaitingConfirmation => "String.State.Kicker.Decision",
        ApplicationActivityState.Recording => "String.State.Kicker.Recording",
        ApplicationActivityState.Processing => "String.State.Kicker.Working",
        ApplicationActivityState.Ready => "String.State.Kicker.Ready",
        ApplicationActivityState.AttentionRequired => "String.State.Kicker.Attention",
        ApplicationActivityState.Paused => "String.State.Kicker.Paused",
        _ => "String.State.Kicker.Ready"
    });

    private string MapStateDescription(ApplicationRuntimeSnapshot snapshot)
    {
        if (snapshot.RecordingFinalization is not { } finalization)
        {
            return _strings.Get($"String.State.{snapshot.Activity}.Description");
        }

        return _strings.Get(finalization.Stage switch
        {
            RecordingArtifactStage.Ready => "String.State.Ready.Description",
            RecordingArtifactStage.AttentionRequired => "String.State.AttentionRequired.Description",
            _ => "String.State.Processing.Description"
        });
    }

    private string? MapAttentionMessage(ApplicationRuntimeSnapshot snapshot)
    {
        var hasCapabilityAttention = snapshot.ServiceEnabled
            && IsActionableCapability(snapshot.Capability);
        if (!hasCapabilityAttention
            && snapshot.Activity != ApplicationActivityState.AttentionRequired
            && string.IsNullOrWhiteSpace(snapshot.AttentionMessage))
        {
            return null;
        }

        if (snapshot.RecordingFinalization?.Stage == RecordingArtifactStage.AttentionRequired)
        {
            return _strings.Get("String.Attention.Recovery");
        }

        if (hasCapabilityAttention)
        {
            var isRecording = snapshot.Activity == ApplicationActivityState.Recording;
            return _strings.Get(snapshot.Capability.Issue switch
            {
                RuntimeCapabilityIssue.NoActiveAudioEndpoints => "String.Setup.Capability.AudioMissing",
                RuntimeCapabilityIssue.NoActiveOutput => isRecording
                    ? "String.Attention.OutputNotRecording"
                    : "String.Setup.Capability.OutputMissing",
                RuntimeCapabilityIssue.OutputCaptureUnavailable => isRecording
                    ? "String.Attention.OutputNotRecording"
                    : "String.Setup.Capability.OutputCaptureUnavailable",
                RuntimeCapabilityIssue.NoActiveMicrophone => isRecording
                    ? "String.Attention.MicrophoneNotRecording"
                    : "String.Setup.Capability.MicrophoneMissing",
                RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable => isRecording
                    ? "String.Attention.ConfiguredMicrophoneNotRecording"
                    : "String.Setup.Capability.ConfiguredMicrophoneUnavailable",
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable => isRecording
                    ? "String.Attention.MicrophoneNotRecording"
                    : "String.Setup.Capability.MicrophoneCaptureUnavailable",
                _ => "String.Attention.Capability"
            });
        }

        return _strings.Get(snapshot.Activity == ApplicationActivityState.Recording
            ? "String.Attention.RecordingDegraded"
            : "String.Attention.ActionRequired");
    }

    private static bool IsActionableCapability(RuntimeCapabilitySnapshot capability) =>
        capability.State == RuntimeCapabilityState.Blocked
        || capability.Issue is RuntimeCapabilityIssue.NoActiveOutput
            or RuntimeCapabilityIssue.NoActiveMicrophone
            or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
            or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
            or RuntimeCapabilityIssue.OutputCaptureUnavailable;

    private void ReconcileRecentRecordings(IReadOnlyList<RecentRecordingSnapshot> snapshots)
    {
        var desired = snapshots.Take(MaximumRecentItems).ToArray();
        var desiredIds = desired.Select(static item => item.SessionId).ToHashSet();
        var transcriptionAvailable = IsTranscriptionConfigured(_lastSnapshot.UserSettings.Transcription);

        for (var index = RecentRecordings.Count - 1; index >= 0; index--)
        {
            if (desiredIds.Contains(RecentRecordings[index].SessionId))
            {
                continue;
            }

            RecentRecordings[index].Dispose();
            RecentRecordings.RemoveAt(index);
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var snapshot = desired[index];
            var currentIndex = IndexOf(snapshot.SessionId);
            if (currentIndex < 0)
            {
                RecentRecordings.Insert(
                    index,
                    new RecentRecordingItemViewModel(
                        snapshot,
                        _shell,
                        _strings,
                        ShowArtifactActionFailure,
                        RequestRecentRemoval,
                        RequestRecentRename,
                        RequestRecentTranscription,
                        CancelRecentTranscriptionAsync,
                        RetryRecentTranscriptionAsync,
                        ConfirmRecentLocalPowerOverrideAsync,
                        transcriptionAvailable));
                continue;
            }

            var item = RecentRecordings[currentIndex];
            item.Apply(snapshot, transcriptionAvailable);
            if (currentIndex != index)
            {
                RecentRecordings.Move(currentIndex, index);
            }
        }

        OnPropertyChanged(nameof(HasRecentRecordings));
    }

    private static bool IsTranscriptionConfigured(RuntimeTranscriptionSettingsSnapshot settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SelectedEngineId))
        {
            return false;
        }

        var engine = settings.Engines.FirstOrDefault(candidate => string.Equals(
            candidate.EngineId,
            settings.SelectedEngineId,
            StringComparison.Ordinal));
        if (engine is null)
        {
            return false;
        }

        if (engine.ExecutionKind == TranscriptionExecutionKind.Local)
        {
            return engine.LocalModels.Any(model => string.Equals(
                    model.ModelId,
                    engine.SelectedModelId,
                    StringComparison.Ordinal)
                && model.State == RuntimeLocalModelState.Installed
                && model.IsVerified);
        }

        var hasAvailableModel = engine.Models.Count > 0
            || engine.SupportsModelDiscovery
            && !string.IsNullOrWhiteSpace(engine.SelectedModelId);
        if (!hasAvailableModel)
        {
            return false;
        }

        return engine.HasCredential && engine.DisclosureAccepted;
    }

    private Task CancelRecentTranscriptionAsync(RecentRecordingItemViewModel item) =>
        ExecuteTranscriptionRuntimeActionAsync(
            cancellationToken => _runtime.CancelTranscriptionAsync(item.SessionId, cancellationToken));

    private Task RetryRecentTranscriptionAsync(RecentRecordingItemViewModel item) =>
        ExecuteTranscriptionRuntimeActionAsync(
            cancellationToken => _runtime.RetryTranscriptionAsync(item.SessionId, cancellationToken));

    private Task ConfirmRecentLocalPowerOverrideAsync(RecentRecordingItemViewModel item) =>
        ExecuteTranscriptionRuntimeActionAsync(
            cancellationToken => _runtime.ConfirmLocalTranscriptionPowerOverrideAsync(
                item.SessionId,
                cancellationToken));

    private int IndexOf(Guid sessionId)
    {
        for (var index = 0; index < RecentRecordings.Count; index++)
        {
            if (RecentRecordings[index].SessionId == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private void ShowArtifactActionFailure() =>
        ShowTransientAttention("String.Error.ArtifactUnavailable");

    private void ShowTransientAttention(string resourceKey)
    {
        _transientAttentionKey = resourceKey;
        AttentionMessage = _strings.Get(resourceKey);
    }

    private void ApplyInitializationFailure()
    {
        StateKicker = _strings.Get("String.State.Kicker.Attention");
        StateTitle = _strings.Get("String.State.AttentionRequired.Title");
        StateDescription = _strings.Get("String.Error.Initialize");
        AttentionMessage = _strings.Get("String.Error.Action");
    }

    private void RequestRecentRemoval(RecentRecordingItemViewModel item) =>
        RecentRecordingRemovalRequested?.Invoke(
            this,
            new RecentRecordingRemovalRequestedEventArgs(item.SessionId, item.HasDeletableAudioFile));

    private void RequestRecentRename(RecentRecordingItemViewModel item) =>
        RecentRecordingRenameRequested?.Invoke(
            this,
            new RecentRecordingRenameRequestedEventArgs(item.SessionId, item.SourceLabel));

    private void RequestRecentTranscription(RecentRecordingItemViewModel item) =>
        RecentRecordingTranscriptionRequested?.Invoke(
            this,
            new RecentRecordingTranscriptionRequestedEventArgs(
                item.SessionId,
                item.RequiresTranscriptReplacementConfirmation));

    private void NotifyCommandStates()
    {
        ToggleServiceCommand.NotifyCanExecuteChanged();
        StartManualRecordingCommand.NotifyCanExecuteChanged();
        PauseOrResumeCommand.NotifyCanExecuteChanged();
        FinishRecordingCommand.NotifyCanExecuteChanged();
        OpenRecoveryFolderCommand.NotifyCanExecuteChanged();
        RetryCapabilitiesCommand.NotifyCanExecuteChanged();
        RetryInitializationCommand.NotifyCanExecuteChanged();
        RequestDiscardCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// Stable row state for a local recording and its shell actions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// </remarks>
public sealed class RecentRecordingItemViewModel : ObservableObject, IDisposable
{
    private readonly IPlatformShell _shell;
    private readonly ILocalizationService _strings;
    private readonly Action _onActionFailed;
    private readonly Action<RecentRecordingItemViewModel> _onRemoveRequested;
    private readonly Action<RecentRecordingItemViewModel> _onRenameRequested;
    private readonly Action<RecentRecordingItemViewModel> _onTranscriptionRequested;
    private readonly Func<RecentRecordingItemViewModel, Task> _onCancelTranscription;
    private readonly Func<RecentRecordingItemViewModel, Task> _onRetryTranscription;
    private readonly Func<RecentRecordingItemViewModel, Task> _onConfirmLocalPowerOverride;
    private RecentRecordingSnapshot _snapshot;
    private string _sourceLabel = string.Empty;
    private string _timeLabel = string.Empty;
    private string _durationLabel = string.Empty;
    private string _statusLabel = string.Empty;
    private RecentRecordingState _state;
    private bool _transcriptionAvailable;
    private bool _isTranscriptionActionRunning;
    private bool _disposed;

    public RecentRecordingItemViewModel(
        RecentRecordingSnapshot snapshot,
        IPlatformShell shell,
        ILocalizationService strings,
        Action onActionFailed,
        Action<RecentRecordingItemViewModel> onRemoveRequested,
        Action<RecentRecordingItemViewModel> onRenameRequested,
        Action<RecentRecordingItemViewModel> onTranscriptionRequested,
        Func<RecentRecordingItemViewModel, Task> onCancelTranscription,
        Func<RecentRecordingItemViewModel, Task> onRetryTranscription,
        Func<RecentRecordingItemViewModel, Task> onConfirmLocalPowerOverride,
        bool transcriptionAvailable)
    {
        _snapshot = snapshot;
        _shell = shell;
        _strings = strings;
        _onActionFailed = onActionFailed;
        _onRemoveRequested = onRemoveRequested;
        _onRenameRequested = onRenameRequested;
        _onTranscriptionRequested = onTranscriptionRequested;
        _onCancelTranscription = onCancelTranscription;
        _onRetryTranscription = onRetryTranscription;
        _onConfirmLocalPowerOverride = onConfirmLocalPowerOverride;
        _transcriptionAvailable = transcriptionAvailable;
        _strings.LanguageChanged += HandleLanguageChanged;
        OpenRecordingCommand = new AsyncRelayCommand(OpenRecordingAsync, () => HasAudioPath);
        OpenTranscriptCommand = new AsyncRelayCommand(OpenTranscriptAsync, () => HasTranscriptPath);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => HasArtifactPath);
        RequestRemoveCommand = new RelayCommand(
            () => _onRemoveRequested(this),
            () => CanRemove);
        RequestRenameCommand = new RelayCommand(
            () => _onRenameRequested(this),
            () => CanRename);
        RequestTranscriptionCommand = new RelayCommand(
            () => _onTranscriptionRequested(this),
            () => CanTranscribe);
        CancelTranscriptionCommand = new AsyncRelayCommand(
            CancelTranscriptionAsync,
            () => CanCancelTranscription);
        RetryTranscriptionCommand = new AsyncRelayCommand(
            RetryTranscriptionAsync,
            () => CanRetryTranscription);
        ContinueLocalTranscriptionCommand = new AsyncRelayCommand(
            ContinueLocalTranscriptionAsync,
            () => CanContinueLocalTranscription);
        Apply(snapshot, transcriptionAvailable);
    }

    public Guid SessionId => _snapshot.SessionId;

    public IAsyncRelayCommand OpenRecordingCommand { get; }

    public IAsyncRelayCommand OpenTranscriptCommand { get; }

    public IAsyncRelayCommand OpenFolderCommand { get; }

    public IRelayCommand RequestRemoveCommand { get; }

    public IRelayCommand RequestRenameCommand { get; }

    public IRelayCommand RequestTranscriptionCommand { get; }

    public IAsyncRelayCommand CancelTranscriptionCommand { get; }

    public IAsyncRelayCommand RetryTranscriptionCommand { get; }

    public IAsyncRelayCommand ContinueLocalTranscriptionCommand { get; }

    public string SourceLabel
    {
        get => _sourceLabel;
        private set => SetProperty(ref _sourceLabel, value);
    }

    public string TimeLabel
    {
        get => _timeLabel;
        private set => SetProperty(ref _timeLabel, value);
    }

    public string DurationLabel
    {
        get => _durationLabel;
        private set => SetProperty(ref _durationLabel, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public bool HasAudioPath => (State is RecentRecordingState.Ready or RecentRecordingState.AttentionRequired)
        && !string.IsNullOrWhiteSpace(_snapshot.PrimaryAudioPath);

    public bool HasTranscriptPath => (State is RecentRecordingState.Ready or RecentRecordingState.AttentionRequired)
        && !string.IsNullOrWhiteSpace(PreferredTranscriptPath);

    public bool HasArtifactPath => HasAudioPath || HasTranscriptPath;

    public bool HasTranscriptionStatus => _snapshot.Transcription is not null;

    public string TranscriptionStatusLabel => MapTranscriptionStatus(_snapshot.Transcription);

    public bool HasTranscriptionProgress => _snapshot.Transcription?.State is (
        RuntimeTranscriptionJobState.Queued
        or RuntimeTranscriptionJobState.Preparing
        or RuntimeTranscriptionJobState.Uploading
        or RuntimeTranscriptionJobState.Processing);

    public double TranscriptionProgressPercent => Math.Clamp(
        (_snapshot.Transcription?.Progress ?? 0) * 100,
        0,
        100);

    public bool HasTranscriptionUsage => !string.IsNullOrWhiteSpace(TranscriptionUsageLabel);

    public string? TranscriptionUsageLabel => MapTranscriptionUsage(_snapshot.Transcription?.Usage);

    public bool RequiresAttention => State == RecentRecordingState.AttentionRequired;

    public bool CanRemove => State is (RecentRecordingState.Ready or RecentRecordingState.AttentionRequired)
        && !IsActiveTranscription;

    public bool CanRename => State is RecentRecordingState.Ready or RecentRecordingState.AttentionRequired;

    public bool CanTranscribe => !_isTranscriptionActionRunning
        && _transcriptionAvailable
        && HasAudioPath
        && _snapshot.Transcription?.State is (null
            or RuntimeTranscriptionJobState.NotStarted
            or RuntimeTranscriptionJobState.Completed
            or RuntimeTranscriptionJobState.Cancelled);

    public bool CanCancelTranscription => !_isTranscriptionActionRunning
        && _snapshot.Transcription?.State is (
            RuntimeTranscriptionJobState.Queued
            or RuntimeTranscriptionJobState.Preparing
            or RuntimeTranscriptionJobState.Uploading
            or RuntimeTranscriptionJobState.Processing
            or RuntimeTranscriptionJobState.RetryScheduled
            or RuntimeTranscriptionJobState.AttentionRequired);

    public bool CanRetryTranscription => !_isTranscriptionActionRunning
        && _snapshot.Transcription?.StableErrorCode != "low_power_override_required"
        && _snapshot.Transcription?.State is (
            RuntimeTranscriptionJobState.RetryScheduled
            or RuntimeTranscriptionJobState.AttentionRequired
            or RuntimeTranscriptionJobState.Failed);

    public bool CanContinueLocalTranscription => !_isTranscriptionActionRunning
        && _snapshot.Transcription is
        {
            EngineId: "local.whisper",
            State: RuntimeTranscriptionJobState.AttentionRequired,
            StableErrorCode: "low_power_override_required"
        };

    public bool RequiresTranscriptReplacementConfirmation =>
        !string.IsNullOrWhiteSpace(_snapshot.TranscriptMarkdownPath)
        || !string.IsNullOrWhiteSpace(_snapshot.TranscriptJsonPath)
        || !string.IsNullOrWhiteSpace(_snapshot.LegacyTranscriptMarkdownPath)
        || !string.IsNullOrWhiteSpace(_snapshot.LegacyTranscriptJsonPath);

    public string TranscriptionActionLabel => _strings.Get(RequiresTranscriptReplacementConfirmation
        ? "String.Transcription.Action.TranscribeAgain"
        : "String.Transcription.Action.Transcribe");

    private bool IsActiveTranscription => _snapshot.Transcription?.State is (
        RuntimeTranscriptionJobState.Queued
        or RuntimeTranscriptionJobState.Preparing
        or RuntimeTranscriptionJobState.Uploading
        or RuntimeTranscriptionJobState.Processing);

    public bool HasDeletableAudioFile => !string.IsNullOrWhiteSpace(_snapshot.PrimaryAudioPath);

    public RecentRecordingState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public void Apply(RecentRecordingSnapshot snapshot, bool transcriptionAvailable)
    {
        _snapshot = snapshot;
        _transcriptionAvailable = transcriptionAvailable;
        State = ResolveState(snapshot);
        SourceLabel = string.Equals(snapshot.DisplayTitle, snapshot.SourceLabel, StringComparison.Ordinal)
            ? MeetingSourceLabelLocalizer.Localize(_strings, snapshot.SourceLabel)
            : snapshot.DisplayTitle;
        TimeLabel = snapshot.StartedAtUtc == DateTimeOffset.MinValue
            ? _strings.Get("String.Recent.DateUnknown")
            : snapshot.StartedAtUtc.ToLocalTime().ToString("g", _strings.CurrentCulture);
        DurationLabel = snapshot.Duration <= TimeSpan.Zero
            ? _strings.Get("String.Recent.Duration.Unknown")
            : snapshot.Duration.ToString(snapshot.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        StatusLabel = _strings.Get(State switch
        {
            RecentRecordingState.Recording => "String.Recent.Status.Recording",
            RecentRecordingState.Ready => "String.Recent.Status.Ready",
            RecentRecordingState.AttentionRequired => "String.Recent.Status.AttentionRequired",
            _ => "String.Recent.Status.Processing"
        });

        OnPropertyChanged(nameof(HasAudioPath));
        OnPropertyChanged(nameof(HasTranscriptPath));
        OnPropertyChanged(nameof(HasArtifactPath));
        OnPropertyChanged(nameof(HasTranscriptionStatus));
        OnPropertyChanged(nameof(TranscriptionStatusLabel));
        OnPropertyChanged(nameof(HasTranscriptionProgress));
        OnPropertyChanged(nameof(TranscriptionProgressPercent));
        OnPropertyChanged(nameof(HasTranscriptionUsage));
        OnPropertyChanged(nameof(TranscriptionUsageLabel));
        OnPropertyChanged(nameof(RequiresAttention));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanTranscribe));
        OnPropertyChanged(nameof(CanCancelTranscription));
        OnPropertyChanged(nameof(CanRetryTranscription));
        OnPropertyChanged(nameof(CanContinueLocalTranscription));
        OnPropertyChanged(nameof(RequiresTranscriptReplacementConfirmation));
        OnPropertyChanged(nameof(TranscriptionActionLabel));
        OnPropertyChanged(nameof(HasDeletableAudioFile));
        OpenRecordingCommand.NotifyCanExecuteChanged();
        OpenTranscriptCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        RequestRemoveCommand.NotifyCanExecuteChanged();
        RequestRenameCommand.NotifyCanExecuteChanged();
        RequestTranscriptionCommand.NotifyCanExecuteChanged();
        CancelTranscriptionCommand.NotifyCanExecuteChanged();
        RetryTranscriptionCommand.NotifyCanExecuteChanged();
        ContinueLocalTranscriptionCommand.NotifyCanExecuteChanged();
    }

    private static RecentRecordingState ResolveState(RecentRecordingSnapshot snapshot)
    {
        if (snapshot.State != RecentRecordingState.Unspecified)
        {
            return snapshot.State;
        }

        if (snapshot.RequiresAttention)
        {
            return RecentRecordingState.AttentionRequired;
        }

        return string.IsNullOrWhiteSpace(snapshot.PrimaryAudioPath)
            ? RecentRecordingState.Processing
            : RecentRecordingState.Ready;
    }

    private string MapTranscriptionStatus(RuntimeTranscriptionJobSnapshot? transcription)
    {
        if (transcription is null)
        {
            return string.Empty;
        }

        return transcription.State switch
        {
            RuntimeTranscriptionJobState.NotStarted => _strings.Get("String.Transcription.State.NotStarted"),
            RuntimeTranscriptionJobState.Queued => _strings.Get("String.Transcription.State.Queued"),
            RuntimeTranscriptionJobState.Preparing => _strings.Get("String.Transcription.State.Preparing"),
            RuntimeTranscriptionJobState.Uploading => _strings.Format(
                "String.Transcription.State.Uploading.Format",
                TranscriptionProgressPercent),
            RuntimeTranscriptionJobState.Processing => _strings.Format(
                "String.Transcription.State.Processing.Format",
                TranscriptionProgressPercent),
            RuntimeTranscriptionJobState.Completed => _strings.Get("String.Transcription.State.Completed"),
            RuntimeTranscriptionJobState.RetryScheduled when IsLocalTranscription(transcription)
                && transcription.NextAttemptAtUtc is { } localNextAttempt =>
                _strings.Format(
                    "String.Transcription.State.Local.RetryAt.Format",
                    localNextAttempt.ToLocalTime().ToString("t", _strings.CurrentCulture)),
            RuntimeTranscriptionJobState.RetryScheduled when IsLocalTranscription(transcription) =>
                _strings.Get("String.Transcription.State.Local.RetryScheduled"),
            RuntimeTranscriptionJobState.RetryScheduled when transcription.NextAttemptAtUtc is { } nextAttempt =>
                _strings.Format(
                    "String.Transcription.State.RetryAt.Format",
                    nextAttempt.ToLocalTime().ToString("t", _strings.CurrentCulture)),
            RuntimeTranscriptionJobState.RetryScheduled =>
                _strings.Get("String.Transcription.State.RetryScheduled"),
            RuntimeTranscriptionJobState.AttentionRequired => MapTranscriptionAttention(transcription),
            RuntimeTranscriptionJobState.Cancelled => _strings.Get("String.Transcription.State.Cancelled"),
            RuntimeTranscriptionJobState.Failed => _strings.Get("String.Transcription.State.Failed"),
            _ => string.Empty
        };
    }

    private string MapTranscriptionAttention(RuntimeTranscriptionJobSnapshot transcription) =>
        transcription.StableErrorCode switch
        {
            "diarization_assets_missing" => _strings.Get("String.Transcription.Error.SpeakerComponents"),
            "diarization_runtime_missing" => _strings.Get("String.Transcription.Error.SpeakerRuntime"),
            "diarization_failed" or "diarization_timeout" or "diarization_result_invalid" =>
                _strings.Get("String.Transcription.Error.SpeakerProcessing"),
            "timestamp_capability_missing" => _strings.Get("String.Transcription.Error.WordTiming"),
            "invalid_key" or "missing_key" =>
                _strings.Get("String.Transcription.State.Attention.Key"),
            "insufficient_credit" =>
                _strings.Get("String.Transcription.State.Attention.Credit"),
            "invalid_engine_configuration" or "model_unavailable"
                when !IsLocalTranscription(transcription) =>
                _strings.Get("String.Transcription.State.Attention.Model"),
            "model_missing" or "model_corrupt" or "model_unavailable" =>
                _strings.Get("String.Transcription.State.Attention.LocalModel"),
            "insufficient_memory" =>
                _strings.Get("String.Transcription.State.Attention.Memory"),
            "insufficient_disk" =>
                _strings.Get("String.Transcription.State.Attention.Disk"),
            "backend_unavailable" or "backend_probe_failed" or "native_failure" or "native_crash" =>
                _strings.Get("String.Transcription.State.Attention.LocalBackend"),
            "low_power_override_required" =>
                _strings.Get("String.Transcription.State.Attention.LowPowerOverride"),
            "low_power" or "energy_saver" =>
                _strings.Get("String.Transcription.State.Attention.LowPower"),
            "zdr_route_unavailable" =>
                _strings.Get("String.Transcription.State.Attention.Zdr"),
            "input_missing" or "input_unreadable" or "unsupported_input" =>
                _strings.Get("String.Transcription.State.Attention.Recording"),
            _ => _strings.Get("String.Transcription.State.Attention.Settings")
        };

    private static bool IsLocalTranscription(RuntimeTranscriptionJobSnapshot transcription) =>
        string.Equals(transcription.EngineId, "local.whisper", StringComparison.Ordinal);

    private string? MapTranscriptionUsage(TranscriptionUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var duration = usage.AudioSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
            : null;
        var cost = usage.ReportedCost?.ToString("0.####", _strings.CurrentCulture);
        var currency = string.IsNullOrWhiteSpace(usage.Currency) ? null : usage.Currency.Trim();
        var reportedCost = cost is null
            ? null
            : currency is null
                ? cost
                : $"{cost} {currency}";
        if (duration is not null && cost is not null)
        {
            return _strings.Format(
                "String.Transcription.Usage.DurationAndCost.Format",
                duration,
                reportedCost!);
        }

        if (cost is not null)
        {
            return _strings.Format(
                "String.Transcription.Usage.Cost.Format",
                reportedCost!);
        }

        return duration is null
            ? null
            : _strings.Format("String.Transcription.Usage.Duration.Format", duration);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= HandleLanguageChanged;
    }

    private string? PreferredTranscriptPath =>
        !string.IsNullOrWhiteSpace(_snapshot.TranscriptMarkdownPath)
            ? _snapshot.TranscriptMarkdownPath
            : !string.IsNullOrWhiteSpace(_snapshot.TranscriptJsonPath)
                ? _snapshot.TranscriptJsonPath
                : !string.IsNullOrWhiteSpace(_snapshot.LegacyTranscriptMarkdownPath)
                    ? _snapshot.LegacyTranscriptMarkdownPath
                    : _snapshot.LegacyTranscriptJsonPath;

    private string? PreferredArtifactPath =>
        !string.IsNullOrWhiteSpace(_snapshot.PrimaryAudioPath)
            ? _snapshot.PrimaryAudioPath
            : PreferredTranscriptPath;

    private Task OpenRecordingAsync() => ExecuteShellActionAsync(
        _snapshot.PrimaryAudioPath,
        path => _shell.OpenFileAsync(path, CancellationToken.None));

    private Task OpenTranscriptAsync() => ExecuteShellActionAsync(
        PreferredTranscriptPath,
        path => _shell.OpenFileAsync(path, CancellationToken.None));

    private Task OpenFolderAsync() => ExecuteShellActionAsync(
        PreferredArtifactPath,
        path => _shell.OpenContainingFolderAsync(path, CancellationToken.None));

    private async Task CancelTranscriptionAsync()
    {
        _isTranscriptionActionRunning = true;
        NotifyTranscriptionActionStateChanged();
        try
        {
            await _onCancelTranscription(this);
        }
        finally
        {
            _isTranscriptionActionRunning = false;
            NotifyTranscriptionActionStateChanged();
        }
    }

    private async Task RetryTranscriptionAsync()
    {
        _isTranscriptionActionRunning = true;
        NotifyTranscriptionActionStateChanged();
        try
        {
            await _onRetryTranscription(this);
        }
        finally
        {
            _isTranscriptionActionRunning = false;
            NotifyTranscriptionActionStateChanged();
        }
    }

    private async Task ContinueLocalTranscriptionAsync()
    {
        _isTranscriptionActionRunning = true;
        NotifyTranscriptionActionStateChanged();
        try
        {
            await _onConfirmLocalPowerOverride(this);
        }
        finally
        {
            _isTranscriptionActionRunning = false;
            NotifyTranscriptionActionStateChanged();
        }
    }

    private void NotifyTranscriptionActionStateChanged()
    {
        OnPropertyChanged(nameof(CanTranscribe));
        OnPropertyChanged(nameof(CanCancelTranscription));
        OnPropertyChanged(nameof(CanRetryTranscription));
        OnPropertyChanged(nameof(CanContinueLocalTranscription));
        RequestTranscriptionCommand.NotifyCanExecuteChanged();
        CancelTranscriptionCommand.NotifyCanExecuteChanged();
        RetryTranscriptionCommand.NotifyCanExecuteChanged();
        ContinueLocalTranscriptionCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteShellActionAsync(string? path, Func<string, ValueTask> action)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _onActionFailed();
            return;
        }

        try
        {
            await action(path);
        }
        catch
        {
            _onActionFailed();
        }
    }

    private void HandleLanguageChanged(object? sender, EventArgs args) =>
        Apply(_snapshot, _transcriptionAvailable);
}

public sealed class RecentRecordingRemovalRequestedEventArgs(Guid sessionId, bool hasAudioFile) : EventArgs
{
    public Guid SessionId { get; } = sessionId;

    public bool HasAudioFile { get; } = hasAudioFile;
}

public sealed class RecentRecordingRenameRequestedEventArgs(Guid sessionId, string currentTitle) : EventArgs
{
    public Guid SessionId { get; } = sessionId;

    public string CurrentTitle { get; } = currentTitle;
}

public sealed class RecentRecordingTranscriptionRequestedEventArgs(
    Guid sessionId,
    bool replaceExisting) : EventArgs
{
    public Guid SessionId { get; } = sessionId;

    public bool ReplaceExisting { get; } = replaceExisting;
}
