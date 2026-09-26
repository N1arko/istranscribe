using System.Reflection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <summary>
/// Covers the ordinary one-surface setup through first locally saved recording flow.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#acceptance
/// </remarks>
public sealed class ReleaseV2UsabilityFlowTests
{
    [Fact]
    public async Task Setup_to_first_ready_recording_never_requires_advanced_settings()
    {
        var initialSettings = RuntimeUserSettingsSnapshot.Initial with
        {
            ServiceEnabled = true,
            Autostart = true,
            Notifications = true,
            Language = "ru",
            Theme = "system"
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Paused,
            serviceEnabled: true,
            userSettings: initialSettings));
        var strings = new FakeLocalizationService();
        using var setup = new SetupViewModel(runtime, strings, initialSettings);

        await setup.CompleteCommand.ExecuteAsync(null);

        var completedSettings = Assert.Single(runtime.CompletedOnboardingUpdates);
        Assert.True(completedSettings.ServiceEnabled);
        Assert.True(completedSettings.Autostart);
        Assert.NotEmpty(completedSettings.Applications);

        var listeningSettings = new RuntimeUserSettingsSnapshot(
            OnboardingCompleted: true,
            ServiceEnabled: completedSettings.ServiceEnabled,
            Autostart: completedSettings.Autostart,
            Notifications: completedSettings.Notifications,
            Language: completedSettings.Language,
            Theme: completedSettings.Theme,
            MicrophoneDeviceId: completedSettings.MicrophoneDeviceId,
            FollowSystemDefaultMicrophone: completedSettings.FollowSystemDefaultMicrophone,
            RecordingsFolder: completedSettings.RecordingsFolder,
            Applications: completedSettings.Applications);
        runtime.Publish(SnapshotFactory.Create(userSettings: listeningSettings));
        var shell = new FakeDesktopShell();
        using var main = new MainWindowViewModel(runtime, shell, strings);
        await main.InitializeAsync(CancellationToken.None);

        Assert.True(main.ShowManualRecordingAction);
        await main.StartManualRecordingCommand.ExecuteAsync(null);
        Assert.Equal(1, runtime.ManualRecordingCalls);

        var sessionId = Guid.NewGuid();
        var recordingSnapshot = SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: new ActiveMeetingSnapshot(
                sessionId,
                "Manual recording",
                DateTimeOffset.UtcNow,
                HasOutput: true,
                HasMicrophone: true,
                IsPaused: false),
            userSettings: listeningSettings);
        runtime.Publish(recordingSnapshot);
        ApplySnapshot(main, recordingSnapshot);
        Assert.True(main.ShowRecordingActions);

        await main.FinishRecordingCommand.ExecuteAsync(null);
        Assert.Equal(1, runtime.FinishRecordingCalls);
        var processingSnapshot = SnapshotFactory.Create(
            ApplicationActivityState.Processing,
            finalization: new RecordingFinalizationSnapshot(
                sessionId,
                RecordingArtifactStage.Processing,
                RecoverableAudioPath: null,
                Progress: 0.5),
            userSettings: listeningSettings);
        runtime.Publish(processingSnapshot);
        ApplySnapshot(main, processingSnapshot);
        Assert.True(main.HasProgress);

        const string audioPath = "C:\\Recordings\\first-meeting.mp3";
        var recent = new RecentRecordingSnapshot(
            sessionId,
            "Manual recording",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            audioPath,
            RequiresAttention: false)
        {
            State = RecentRecordingState.Ready
        };
        var readySnapshot = SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent],
            finalization: new RecordingFinalizationSnapshot(
                sessionId,
                RecordingArtifactStage.Ready,
                RecoverableAudioPath: null,
                Progress: 1),
            userSettings: listeningSettings);
        runtime.Publish(readySnapshot);
        ApplySnapshot(main, readySnapshot);

        var readyItem = Assert.Single(main.RecentRecordings);
        await readyItem.OpenRecordingCommand.ExecuteAsync(null);
        Assert.Equal([audioPath], shell.OpenedFiles);
    }

    private static void ApplySnapshot(
        MainWindowViewModel viewModel,
        ApplicationRuntimeSnapshot snapshot)
    {
        var apply = typeof(MainWindowViewModel).GetMethod(
            "ApplySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);
        apply.Invoke(viewModel, [snapshot]);
    }
}
