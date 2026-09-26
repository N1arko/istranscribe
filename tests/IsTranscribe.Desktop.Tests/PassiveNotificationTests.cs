using System.Reflection;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#surfaces
/// </remarks>
public sealed class PassiveNotificationTests
{
    [Fact]
    public async Task Startup_attention_snapshot_does_not_repeat_an_actionable_notification()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            capability: Capability(RuntimeCapabilityIssue.MicrophoneCaptureUnavailable)));
        var notifications = new RecordingSystemNotificationService();
        await using var controller = new PassiveNotificationController(
            runtime,
            new FakeLocalizationService(),
            static () => { },
            systemNotifications: notifications);

        controller.Start();

        Assert.Empty(notifications.Messages);
    }

    [Fact]
    public void Processing_to_ready_transition_is_detected_once()
    {
        var sessionId = Guid.NewGuid();
        var processing = Recording(sessionId, audioPath: null, requiresAttention: false);
        var ready = processing with { PrimaryAudioPath = "C:\\Recordings\\ready.mp3" };
        var previous = SnapshotFactory.Create(recentRecordings: [processing]);
        var current = SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [ready]);

        var detected = FindNewReadyRecording(previous, current);

        Assert.Equal(ready, detected);
        Assert.Null(FindNewReadyRecording(current, current));
    }

    [Fact]
    public void Actionable_failure_is_not_misreported_as_ready_recording()
    {
        var failed = Recording(
            Guid.NewGuid(),
            audioPath: "C:\\Recordings\\recovery.wav",
            requiresAttention: true);
        var current = SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            recentRecordings: [failed]);

        Assert.Null(FindNewReadyRecording(SnapshotFactory.Create(), current));
    }

    [Fact]
    public void New_ready_item_is_detected_when_it_enters_recent_history()
    {
        var ready = Recording(
            Guid.NewGuid(),
            audioPath: "C:\\Recordings\\new.mp3",
            requiresAttention: false);
        var current = SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [ready]);

        Assert.Equal(ready, FindNewReadyRecording(SnapshotFactory.Create(), current));
    }

    [Fact]
    public void Recording_capability_failure_is_notified_once_per_session_and_issue()
    {
        var sessionId = Guid.NewGuid();
        var notifiedIssues = new HashSet<(Guid SessionId, RuntimeCapabilityIssue Issue)>();
        var healthy = RecordingSnapshot(sessionId, RuntimeCapabilityIssue.None);
        var microphoneFailure = RecordingSnapshot(
            sessionId,
            RuntimeCapabilityIssue.MicrophoneCaptureUnavailable);

        Assert.Equal(
            RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
            FindNewRecordingCapabilityIssue(healthy, microphoneFailure, notifiedIssues));
        Assert.Null(FindNewRecordingCapabilityIssue(
            microphoneFailure,
            microphoneFailure,
            notifiedIssues));
        Assert.Null(FindNewRecordingCapabilityIssue(
            microphoneFailure,
            healthy,
            notifiedIssues));
        Assert.Null(FindNewRecordingCapabilityIssue(
            healthy,
            microphoneFailure,
            notifiedIssues));

        var outputFailure = RecordingSnapshot(
            sessionId,
            RuntimeCapabilityIssue.OutputCaptureUnavailable);
        Assert.Equal(
            RuntimeCapabilityIssue.OutputCaptureUnavailable,
            FindNewRecordingCapabilityIssue(healthy, outputFailure, notifiedIssues));

        var nextSessionFailure = RecordingSnapshot(
            Guid.NewGuid(),
            RuntimeCapabilityIssue.MicrophoneCaptureUnavailable);
        Assert.Equal(
            RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
            FindNewRecordingCapabilityIssue(outputFailure, nextSessionFailure, notifiedIssues));
    }

    [Theory]
    [InlineData(RuntimeCapabilityIssue.PlatformBlocked)]
    [InlineData(RuntimeCapabilityIssue.NoActiveOutput)]
    [InlineData(RuntimeCapabilityIssue.NoActiveMicrophone)]
    [InlineData(RuntimeCapabilityIssue.NoActiveAudioEndpoints)]
    [InlineData(RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable)]
    [InlineData(RuntimeCapabilityIssue.MicrophoneCaptureUnavailable)]
    [InlineData(RuntimeCapabilityIssue.OutputCaptureUnavailable)]
    public void Recording_actionable_issue_transition_is_detected(RuntimeCapabilityIssue issue)
    {
        var sessionId = Guid.NewGuid();
        var notifiedIssues = new HashSet<(Guid SessionId, RuntimeCapabilityIssue Issue)>();

        Assert.Equal(
            issue,
            FindNewRecordingCapabilityIssue(
                RecordingSnapshot(sessionId, RuntimeCapabilityIssue.None),
                RecordingSnapshot(sessionId, issue),
                notifiedIssues));
    }

    [Theory]
    [InlineData(RuntimeCapabilityIssue.None)]
    [InlineData(RuntimeCapabilityIssue.ProcessOutputCaptureUnavailable)]
    public void Recording_non_actionable_issue_does_not_emit_failure(
        RuntimeCapabilityIssue issue)
    {
        var sessionId = Guid.NewGuid();
        var notifiedIssues = new HashSet<(Guid SessionId, RuntimeCapabilityIssue Issue)>();

        Assert.Null(FindNewRecordingCapabilityIssue(
            RecordingSnapshot(sessionId, RuntimeCapabilityIssue.None),
            RecordingSnapshot(sessionId, issue),
            notifiedIssues));
        Assert.Empty(notifiedIssues);
    }

    [Theory]
    [InlineData(
        RuntimeCapabilityIssue.NoActiveMicrophone,
        "String.Notification.MicrophoneUnavailable.Title",
        "String.Notification.MicrophoneUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
        "String.Notification.MicrophoneUnavailable.Title",
        "String.Notification.MicrophoneUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
        "String.Notification.MicrophoneUnavailable.Title",
        "String.Notification.MicrophoneUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.NoActiveOutput,
        "String.Notification.OutputUnavailable.Title",
        "String.Notification.OutputUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.OutputCaptureUnavailable,
        "String.Notification.OutputUnavailable.Title",
        "String.Notification.OutputUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.PlatformBlocked,
        "String.Notification.ActionRequired.Title",
        "String.Notification.ActionRequired.Body")]
    [InlineData(
        RuntimeCapabilityIssue.NoActiveAudioEndpoints,
        "String.Notification.ActionRequired.Title",
        "String.Notification.ActionRequired.Body")]
    public void Recording_source_issue_uses_source_specific_copy(
        RuntimeCapabilityIssue issue,
        string expectedTitleKey,
        string expectedBodyKey)
    {
        var keys = GetRecordingCapabilityNotificationKeys(issue);

        Assert.Equal(expectedTitleKey, keys.TitleKey);
        Assert.Equal(expectedBodyKey, keys.BodyKey);
    }

    [Fact]
    public void Source_issue_outside_recording_does_not_emit_recording_failure()
    {
        var sessionId = Guid.NewGuid();
        var notifiedIssues = new HashSet<(Guid SessionId, RuntimeCapabilityIssue Issue)>();
        var current = RecordingSnapshot(
            sessionId,
            RuntimeCapabilityIssue.MicrophoneCaptureUnavailable) with
        {
            Activity = ApplicationActivityState.AttentionRequired
        };

        Assert.Null(FindNewRecordingCapabilityIssue(
            RecordingSnapshot(sessionId, RuntimeCapabilityIssue.None),
            current,
            notifiedIssues));
        Assert.Empty(notifiedIssues);
    }

    [Theory]
    [InlineData(
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
        "String.Notification.MicrophoneUnavailable.Title",
        "String.Notification.MicrophoneUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
        "String.Notification.MicrophoneUnavailable.Title",
        "String.Notification.MicrophoneUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.OutputCaptureUnavailable,
        "String.Notification.OutputUnavailable.Title",
        "String.Notification.OutputUnavailable.Body")]
    [InlineData(
        RuntimeCapabilityIssue.None,
        "String.Notification.ActionRequired.Title",
        "String.Notification.ActionRequired.Body")]
    public void Attention_required_uses_capability_specific_copy_when_available(
        RuntimeCapabilityIssue issue,
        string expectedTitleKey,
        string expectedBodyKey)
    {
        var snapshot = SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            capability: Capability(issue));

        var keys = GetAttentionRequiredNotificationKeys(snapshot);

        Assert.Equal(expectedTitleKey, keys.TitleKey);
        Assert.Equal(expectedBodyKey, keys.BodyKey);
    }

    [Fact]
    public void Passive_view_model_exposes_open_and_dismiss_actions()
    {
        var strings = new FakeLocalizationService();
        using var viewModel = new PassiveNotificationViewModel(
            strings,
            "String.Notification.RecordingReady.Title",
            "String.Notification.RecordingReady.Body.Format",
            "Zoom");
        var opened = 0;
        var dismissed = 0;
        viewModel.OpenRequested += (_, _) => opened++;
        viewModel.DismissRequested += (_, _) => dismissed++;

        viewModel.OpenCommand.Execute(null);
        viewModel.DismissCommand.Execute(null);

        Assert.Equal(1, opened);
        Assert.Equal(1, dismissed);
        Assert.Equal("Zoom", viewModel.Body);
    }

    [Fact]
    public void Open_notification_relocalizes_a_known_raw_meeting_source()
    {
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new PassiveNotificationViewModel(
            strings,
            "String.Notification.RecordingReady.Title",
            "String.Notification.RecordingReady.Body.Format",
            "Яндекс Телемост",
            localizeMeetingSourceArgument: true);

        Assert.Equal("ru:String.App.YandexTelemost", viewModel.Body);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.App.YandexTelemost", viewModel.Body);
    }

    [Fact]
    public void Open_notification_preserves_an_unknown_source_verbatim()
    {
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new PassiveNotificationViewModel(
            strings,
            "String.Notification.RecordingReady.Title",
            "String.Notification.RecordingReady.Body.Format",
            "Internal Call",
            localizeMeetingSourceArgument: true);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("Internal Call", viewModel.Body);
    }

    private static RecentRecordingSnapshot? FindNewReadyRecording(
        ApplicationRuntimeSnapshot previous,
        ApplicationRuntimeSnapshot current)
    {
        var method = typeof(PassiveNotificationController).GetMethod(
            "FindNewReadyRecording",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [previous, current]) as RecentRecordingSnapshot;
    }

    private static RuntimeCapabilityIssue? FindNewRecordingCapabilityIssue(
        ApplicationRuntimeSnapshot previous,
        ApplicationRuntimeSnapshot current,
        ISet<(Guid SessionId, RuntimeCapabilityIssue Issue)> notifiedIssues)
    {
        var method = typeof(PassiveNotificationController).GetMethod(
            "FindNewActionableRecordingCapabilityIssue",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [previous, current, notifiedIssues]) is RuntimeCapabilityIssue issue
            ? issue
            : null;
    }

    private static (string TitleKey, string BodyKey) GetRecordingCapabilityNotificationKeys(
        RuntimeCapabilityIssue issue)
    {
        var method = typeof(PassiveNotificationController).GetMethod(
            "GetRecordingCapabilityNotificationKeys",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<(string TitleKey, string BodyKey)>(method.Invoke(null, [issue]));
    }

    private static (string TitleKey, string BodyKey) GetAttentionRequiredNotificationKeys(
        ApplicationRuntimeSnapshot snapshot)
    {
        var method = typeof(PassiveNotificationController).GetMethod(
            "GetAttentionRequiredNotificationKeys",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<(string TitleKey, string BodyKey)>(method.Invoke(null, [snapshot]));
    }

    private static ApplicationRuntimeSnapshot RecordingSnapshot(
        Guid sessionId,
        RuntimeCapabilityIssue issue) => SnapshotFactory.Create(
        ApplicationActivityState.Recording,
        activeMeeting: new ActiveMeetingSnapshot(
            sessionId,
            "Zoom",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            HasOutput: true,
            HasMicrophone: true,
            IsPaused: false),
        capability: Capability(issue));

    private static RuntimeCapabilitySnapshot Capability(RuntimeCapabilityIssue issue) =>
        new(
            issue == RuntimeCapabilityIssue.None
                ? RuntimeCapabilityState.Full
                : RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = issue,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };

    private static RecentRecordingSnapshot Recording(
        Guid sessionId,
        string? audioPath,
        bool requiresAttention) => new(
        sessionId,
        "Zoom",
        DateTimeOffset.UtcNow.AddMinutes(-5),
        TimeSpan.FromMinutes(4),
        audioPath,
        requiresAttention);

    private sealed class RecordingSystemNotificationService : ISystemNotificationService
    {
        public List<(string Title, string Message)> Messages { get; } = [];

        public ValueTask ShowAsync(
            string title,
            string message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add((title, message));
            return ValueTask.CompletedTask;
        }
    }
}
