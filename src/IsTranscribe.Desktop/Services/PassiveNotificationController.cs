using Avalonia.Controls;
using Avalonia.Threading;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;
using IsTranscribe.Desktop.Views;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Emits one quiet app-owned notification for ready recordings and actionable failures.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#surfaces
/// </remarks>
public sealed class PassiveNotificationController : IAsyncDisposable
{
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(7);
    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _strings;
    private readonly Action _openApplication;
    private readonly Func<Window?> _placementOwner;
    private readonly TimeProvider _timeProvider;
    private readonly ISystemNotificationService? _systemNotifications;
    private readonly HashSet<(Guid SessionId, RuntimeCapabilityIssue Issue)>
        _notifiedRecordingCapabilityIssues = [];
    private ApplicationRuntimeSnapshot _previousSnapshot;
    private PassiveNotificationWindow? _window;
    private PassiveNotificationViewModel? _viewModel;
    private CancellationTokenSource? _lifetimeCancellation;
    private bool _started;
    private bool _disposed;

    public PassiveNotificationController(
        IApplicationRuntime runtime,
        ILocalizationService strings,
        Action openApplication,
        Func<Window?>? placementOwner = null,
        TimeProvider? timeProvider = null,
        ISystemNotificationService? systemNotifications = null)
    {
        _runtime = runtime;
        _strings = strings;
        _openApplication = openApplication;
        _placementOwner = placementOwner ?? (static () => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _systemNotifications = systemNotifications;
        _previousSnapshot = runtime.Snapshot;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _previousSnapshot = _runtime.Snapshot;
        _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
        if (!_previousSnapshot.UserSettings.Notifications)
        {
            return;
        }

        if (FindNewActionableRecordingCapabilityIssue(
                ApplicationRuntimeSnapshot.Initial,
                _previousSnapshot,
                _notifiedRecordingCapabilityIssues) is { } capabilityIssue)
        {
            ShowRecordingCapabilityFailure(capabilityIssue);
        }
        else if (_previousSnapshot.RecordingFinalization is
        { Stage: RecordingArtifactStage.Ready } finalization
            && _previousSnapshot.RecentRecordings.FirstOrDefault(recording =>
                recording.SessionId == finalization.SessionId
                && IsReady(recording)) is { } ready)
        {
            Show(
                "String.Notification.RecordingReady.Title",
                "String.Notification.RecordingReady.Body.Format",
                ready.SourceLabel,
                localizeMeetingSourceArgument: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_started)
        {
            _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
        }

        CloseCurrent();
        return ValueTask.CompletedTask;
    }

    internal void ApplySnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        if (!snapshot.UserSettings.Notifications)
        {
            _previousSnapshot = snapshot;
            CloseCurrent();
            return;
        }

        if (FindNewActionableRecordingCapabilityIssue(
                _previousSnapshot,
                snapshot,
                _notifiedRecordingCapabilityIssues) is { } capabilityIssue)
        {
            ShowRecordingCapabilityFailure(capabilityIssue);
        }
        else if (snapshot.Activity == ApplicationActivityState.AttentionRequired
            && _previousSnapshot.Activity != ApplicationActivityState.AttentionRequired)
        {
            ShowAttentionRequired(snapshot);
        }
        else if (FindNewReadyRecording(_previousSnapshot, snapshot) is { } ready)
        {
            Show(
                "String.Notification.RecordingReady.Title",
                "String.Notification.RecordingReady.Body.Format",
                ready.SourceLabel,
                localizeMeetingSourceArgument: true);
        }

        _previousSnapshot = snapshot;
    }

    private void ShowAttentionRequired(ApplicationRuntimeSnapshot snapshot)
    {
        var (titleKey, bodyKey) = GetAttentionRequiredNotificationKeys(snapshot);
        Show(titleKey, bodyKey);
    }

    private static (string TitleKey, string BodyKey) GetAttentionRequiredNotificationKeys(
        ApplicationRuntimeSnapshot snapshot) =>
        IsActionableCapabilityNotificationIssue(snapshot.Capability.Issue)
            ? GetRecordingCapabilityNotificationKeys(snapshot.Capability.Issue)
            : (
                "String.Notification.ActionRequired.Title",
                "String.Notification.ActionRequired.Body");

    private void ShowRecordingCapabilityFailure(RuntimeCapabilityIssue issue)
    {
        var (titleKey, bodyKey) = GetRecordingCapabilityNotificationKeys(issue);
        Show(titleKey, bodyKey);
    }

    private static (string TitleKey, string BodyKey) GetRecordingCapabilityNotificationKeys(
        RuntimeCapabilityIssue issue) => issue switch
        {
            RuntimeCapabilityIssue.NoActiveMicrophone
                or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
                or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable => (
                    "String.Notification.MicrophoneUnavailable.Title",
                    "String.Notification.MicrophoneUnavailable.Body"),
            RuntimeCapabilityIssue.NoActiveOutput
                or RuntimeCapabilityIssue.OutputCaptureUnavailable => (
                    "String.Notification.OutputUnavailable.Title",
                    "String.Notification.OutputUnavailable.Body"),
            RuntimeCapabilityIssue.PlatformBlocked
                or RuntimeCapabilityIssue.NoActiveAudioEndpoints => (
                    "String.Notification.ActionRequired.Title",
                    "String.Notification.ActionRequired.Body"),
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null)
        };

    private void Runtime_OnSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void Show(
        string titleKey,
        string bodyKey,
        string? bodyArgument = null,
        bool localizeMeetingSourceArgument = false)
    {
        CloseCurrent();
        var viewModel = new PassiveNotificationViewModel(
            _strings,
            titleKey,
            bodyKey,
            bodyArgument,
            localizeMeetingSourceArgument);
        if (_systemNotifications is not null)
        {
            try
            {
                _systemNotifications.ShowAsync(viewModel.Title, viewModel.Body, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                viewModel.Dispose();
                return;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
            {
                // Fall through to the existing app-owned notification surface.
            }
        }
        var window = new PassiveNotificationWindow(viewModel);
        _viewModel = viewModel;
        _window = window;
        viewModel.OpenRequested += ViewModel_OnOpenRequested;
        viewModel.DismissRequested += ViewModel_OnDismissRequested;
        window.Closed += Window_OnClosed;
        window.ShowNear(_placementOwner());

        _lifetimeCancellation = new CancellationTokenSource();
        _ = CloseAfterLifetimeAsync(window, _lifetimeCancellation.Token);
    }

    private async Task CloseAfterLifetimeAsync(PassiveNotificationWindow expected, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NotificationLifetime, _timeProvider, cancellationToken);
            if (ReferenceEquals(_window, expected))
            {
                CloseCurrent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ViewModel_OnOpenRequested(object? sender, EventArgs args)
    {
        CloseCurrent();
        _openApplication();
    }

    private void ViewModel_OnDismissRequested(object? sender, EventArgs args) => CloseCurrent();

    private void Window_OnClosed(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _window))
        {
            CloseCurrent(closeWindow: false);
        }
    }

    private void CloseCurrent(bool closeWindow = true)
    {
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;

        var window = _window;
        var viewModel = _viewModel;
        _window = null;
        _viewModel = null;
        if (viewModel is not null)
        {
            viewModel.OpenRequested -= ViewModel_OnOpenRequested;
            viewModel.DismissRequested -= ViewModel_OnDismissRequested;
        }

        if (window is not null)
        {
            window.Closed -= Window_OnClosed;
            if (closeWindow && window.IsVisible)
            {
                window.Close();
            }
        }

        viewModel?.Dispose();
    }

    private static RecentRecordingSnapshot? FindNewReadyRecording(
        ApplicationRuntimeSnapshot previous,
        ApplicationRuntimeSnapshot current)
    {
        var previousById = previous.RecentRecordings.ToDictionary(static item => item.SessionId);
        return current.RecentRecordings.FirstOrDefault(item =>
            IsReady(item)
            && (!previousById.TryGetValue(item.SessionId, out var previousItem)
                || !IsReady(previousItem)));
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
    private static RuntimeCapabilityIssue? FindNewActionableRecordingCapabilityIssue(
        ApplicationRuntimeSnapshot previous,
        ApplicationRuntimeSnapshot current,
        ISet<(Guid SessionId, RuntimeCapabilityIssue Issue)> notifiedIssues)
    {
        if (current.Activity != ApplicationActivityState.Recording
            || current.ActiveMeeting is not { } activeMeeting
            || !IsActionableCapabilityNotificationIssue(current.Capability.Issue))
        {
            return null;
        }

        var currentKey = (activeMeeting.SessionId, current.Capability.Issue);
        var previousKey = previous.Activity == ApplicationActivityState.Recording
                          && previous.ActiveMeeting is { } previousMeeting
                          && IsActionableCapabilityNotificationIssue(previous.Capability.Issue)
            ? (previousMeeting.SessionId, previous.Capability.Issue)
            : ((Guid SessionId, RuntimeCapabilityIssue Issue)?)null;
        if (previousKey == currentKey || !notifiedIssues.Add(currentKey))
        {
            return null;
        }

        return current.Capability.Issue;
    }

    private static bool IsActionableCapabilityNotificationIssue(RuntimeCapabilityIssue issue) =>
        issue is RuntimeCapabilityIssue.PlatformBlocked
            or RuntimeCapabilityIssue.NoActiveOutput
            or RuntimeCapabilityIssue.NoActiveMicrophone
            or RuntimeCapabilityIssue.NoActiveAudioEndpoints
            or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
            or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
            or RuntimeCapabilityIssue.OutputCaptureUnavailable;

    private static bool IsReady(RecentRecordingSnapshot recording) =>
        recording.State == RecentRecordingState.Ready
        || (recording.State == RecentRecordingState.Unspecified
            && !recording.RequiresAttention
            && !string.IsNullOrWhiteSpace(recording.PrimaryAudioPath));
}
