using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData(ApplicationActivityState.Listening, true, false, false)]
    [InlineData(ApplicationActivityState.Suspected, true, false, false)]
    [InlineData(ApplicationActivityState.AwaitingConfirmation, false, false, false)]
    [InlineData(ApplicationActivityState.Recording, false, true, false)]
    [InlineData(ApplicationActivityState.Processing, false, false, false)]
    [InlineData(ApplicationActivityState.Ready, false, false, false)]
    [InlineData(ApplicationActivityState.AttentionRequired, false, false, false)]
    [InlineData(ApplicationActivityState.Paused, false, false, true)]
    public async Task Canonical_state_exposes_only_its_contextual_actions(
        ApplicationActivityState activity,
        bool showManual,
        bool showRecording,
        bool showEnableService)
    {
        var serviceEnabled = activity != ApplicationActivityState.Paused;
        var snapshot = SnapshotFactory.Create(
            activity,
            serviceEnabled,
            activeMeeting: activity == ApplicationActivityState.Recording
                ? ActiveMeeting()
                : null,
            pendingPrompt: activity == ApplicationActivityState.AwaitingConfirmation
                ? Prompt("awaiting")
                : null);
        var runtime = new FakeApplicationRuntime(snapshot);
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(activity, viewModel.Activity);
        Assert.Equal(showManual, viewModel.ShowManualRecordingAction);
        Assert.Equal(showRecording, viewModel.ShowRecordingActions);
        Assert.Equal(showEnableService, viewModel.ShowEnableServiceAction);
        Assert.Equal(showManual, viewModel.StartManualRecordingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(ApplicationActivityState.Listening, false)]
    [InlineData(ApplicationActivityState.Ready, false)]
    [InlineData(ApplicationActivityState.Suspected, true)]
    [InlineData(ApplicationActivityState.AwaitingConfirmation, true)]
    [InlineData(ApplicationActivityState.Recording, true)]
    [InlineData(ApplicationActivityState.Processing, true)]
    [InlineData(ApplicationActivityState.AttentionRequired, true)]
    [InlineData(ApplicationActivityState.Paused, true)]
    public async Task Ready_states_hide_the_redundant_kicker(
        ApplicationActivityState activity,
        bool expected)
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            activity,
            serviceEnabled: activity != ApplicationActivityState.Paused));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(expected, viewModel.ShowStateKicker);
    }

    [Fact]
    public async Task Awaiting_confirmation_keeps_manual_recording_action_owned_by_prompt()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AwaitingConfirmation,
            pendingPrompt: Prompt("candidate-1")));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.ShowManualRecordingAction);
        Assert.False(viewModel.StartManualRecordingCommand.CanExecute(null));
        Assert.Equal(0, runtime.ManualRecordingCalls);
    }

    [Fact]
    public async Task Listening_primary_action_starts_manual_recording_once()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.StartManualRecordingCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.ManualRecordingCalls);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasAttention);
    }

    [Fact]
    public async Task Recording_actions_route_to_runtime_and_discard_requires_request_then_confirmation()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: ActiveMeeting()));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);
        var discardRequests = 0;
        viewModel.DiscardRequested += (_, _) => discardRequests++;

        await viewModel.PauseOrResumeCommand.ExecuteAsync(null);
        await viewModel.FinishRecordingCommand.ExecuteAsync(null);
        viewModel.RequestDiscardCommand.Execute(null);

        Assert.Equal(1, runtime.PauseOrResumeCalls);
        Assert.Equal(1, runtime.FinishRecordingCalls);
        Assert.Equal(1, discardRequests);
        Assert.Equal(0, runtime.DiscardRecordingCalls);

        await viewModel.ConfirmDiscardAsync();

        Assert.Equal(1, runtime.DiscardRecordingCalls);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task PausedRecordingKeepsElapsedTimeFrozenAndResumeContinuesFromIt()
    {
        var measuredAtUtc = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var timeProvider = new ManualTimeProvider(measuredAtUtc);
        var strings = new FakeLocalizationService();
        var pausedMeeting = ActiveMeeting() with
        {
            StartedAtUtc = measuredAtUtc.AddMinutes(-7),
            IsPaused = true,
            ActiveDuration = TimeSpan.FromMinutes(2),
            ActiveDurationMeasuredAtUtc = measuredAtUtc
        };
        var pausedRuntime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: pausedMeeting));

        using (var pausedViewModel = Create(
                   pausedRuntime,
                   strings: strings,
                   timeProvider: timeProvider))
        {
            await pausedViewModel.InitializeAsync(CancellationToken.None);

            Assert.Equal("2:00", pausedViewModel.ElapsedLabel);
            Assert.True(pausedViewModel.IsActiveRecordingPaused);
            Assert.False(pausedViewModel.IsActiveRecordingRunning);
            Assert.Equal(0, timeProvider.TimerCreationCount);

            timeProvider.Advance(TimeSpan.FromMinutes(5));
            strings.SetLanguage(UiLanguage.English);

            Assert.Equal("2:00", pausedViewModel.ElapsedLabel);
        }

        var resumedAtUtc = timeProvider.GetUtcNow();
        var resumedMeeting = pausedMeeting with
        {
            IsPaused = false,
            ActiveDurationMeasuredAtUtc = resumedAtUtc
        };
        var resumedRuntime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: resumedMeeting));

        using var resumedViewModel = Create(
            resumedRuntime,
            strings: strings,
            timeProvider: timeProvider);
        await resumedViewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal("2:00", resumedViewModel.ElapsedLabel);
        Assert.False(resumedViewModel.IsActiveRecordingPaused);
        Assert.True(resumedViewModel.IsActiveRecordingRunning);
        Assert.Equal(1, timeProvider.TimerCreationCount);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        strings.SetLanguage(UiLanguage.Russian);

        Assert.Equal("2:10", resumedViewModel.ElapsedLabel);
    }

    [Fact]
    public async Task Recent_recording_actions_keep_file_access_and_removal_policy_explicit()
    {
        var sessionId = Guid.NewGuid();
        const string audioPath = "C:\\Recordings\\meeting.mp3";
        var recent = new RecentRecordingSnapshot(
            sessionId,
            "Zoom",
            DateTimeOffset.UtcNow.AddMinutes(-12),
            TimeSpan.FromMinutes(10),
            audioPath,
            RequiresAttention: false);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent]));
        var shell = new FakeDesktopShell();
        using var viewModel = Create(runtime, shell);
        await viewModel.InitializeAsync(CancellationToken.None);
        RecentRecordingRemovalRequestedEventArgs? removalRequest = null;
        viewModel.RecentRecordingRemovalRequested += (_, args) => removalRequest = args;

        var item = Assert.Single(viewModel.RecentRecordings);
        await item.OpenRecordingCommand.ExecuteAsync(null);
        await item.OpenFolderCommand.ExecuteAsync(null);
        item.RequestRemoveCommand.Execute(null);

        Assert.Equal([audioPath], shell.OpenedFiles);
        Assert.Equal([audioPath], shell.OpenedFolders);
        Assert.NotNull(removalRequest);
        Assert.Equal(sessionId, removalRequest.SessionId);
        Assert.True(removalRequest.HasAudioFile);
        Assert.Empty(runtime.RemovedRecordings);

        await viewModel.ConfirmRecentRemovalAsync(sessionId, deleteAudioFile: false);
        await viewModel.ConfirmRecentRemovalAsync(sessionId, deleteAudioFile: true);

        Assert.Equal(
            [(sessionId, false), (sessionId, true)],
            runtime.RemovedRecordings);
    }

    [Fact]
    public async Task Recent_recording_rename_is_requested_then_saved_through_the_runtime()
    {
        var sessionId = Guid.NewGuid();
        var recent = new RecentRecordingSnapshot(
            sessionId,
            "Zoom",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(4),
            "C:\\Recordings\\meeting.mp3",
            RequiresAttention: false)
        {
            State = RecentRecordingState.Ready
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent]));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);
        RecentRecordingRenameRequestedEventArgs? request = null;
        viewModel.RecentRecordingRenameRequested += (_, args) => request = args;

        var item = Assert.Single(viewModel.RecentRecordings);
        item.RequestRenameCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal("Zoom", request.CurrentTitle);
        Assert.Empty(runtime.RenamedRecordings);

        await viewModel.ConfirmRecentRenameAsync(sessionId, "Weekly product sync");

        Assert.Equal([(sessionId, "Weekly product sync")], runtime.RenamedRecordings);
    }

    [Fact]
    public async Task Custom_recording_title_stays_user_owned_when_language_changes()
    {
        var recent = new RecentRecordingSnapshot(
            Guid.NewGuid(),
            "manual_recording",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(4),
            "C:\\Recordings\\meeting.mp3",
            RequiresAttention: false)
        {
            DisplayTitle = "Weekly product sync",
            State = RecentRecordingState.Ready
        };
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent]));
        using var viewModel = Create(runtime, strings: strings);
        await viewModel.InitializeAsync(CancellationToken.None);

        strings.SetLanguage(UiLanguage.English);

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.Equal("Weekly product sync", item.SourceLabel);
        Assert.True(item.CanRename);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    /// </summary>
    [Fact]
    public async Task Legacy_transcript_action_opens_markdown_and_its_folder_without_runtime_mutation()
    {
        var sessionId = Guid.NewGuid();
        const string transcriptPath = "C:\\Recordings\\meeting.md";
        var recent = new RecentRecordingSnapshot(
            sessionId,
            "Zoom",
            DateTimeOffset.UtcNow.AddMinutes(-12),
            TimeSpan.FromMinutes(10),
            PrimaryAudioPath: null,
            RequiresAttention: false)
        {
            State = RecentRecordingState.Ready,
            LegacyTranscriptMarkdownPath = transcriptPath
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent]));
        var shell = new FakeDesktopShell();
        using var viewModel = Create(runtime, shell);
        await viewModel.InitializeAsync(CancellationToken.None);

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.False(item.HasAudioPath);
        Assert.True(item.HasTranscriptPath);
        Assert.True(item.HasArtifactPath);
        await item.OpenTranscriptCommand.ExecuteAsync(null);
        await item.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([transcriptPath], shell.OpenedFiles);
        Assert.Equal([transcriptPath], shell.OpenedFolders);
        Assert.Empty(runtime.RemovedRecordings);
        Assert.Equal(0, runtime.ManualRecordingCalls);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    /// </summary>
    [Fact]
    public async Task Legacy_transcript_action_uses_json_when_markdown_is_absent()
    {
        const string transcriptPath = "C:\\Recordings\\meeting.json";
        var recent = new RecentRecordingSnapshot(
            Guid.NewGuid(),
            "Zoom",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            PrimaryAudioPath: null,
            RequiresAttention: false)
        {
            State = RecentRecordingState.Ready,
            LegacyTranscriptJsonPath = transcriptPath
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent]));
        var shell = new FakeDesktopShell();
        using var viewModel = Create(runtime, shell);
        await viewModel.InitializeAsync(CancellationToken.None);

        var item = Assert.Single(viewModel.RecentRecordings);
        await item.OpenTranscriptCommand.ExecuteAsync(null);

        Assert.Equal([transcriptPath], shell.OpenedFiles);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
    /// </summary>
    [Fact]
    public async Task Ready_recording_requests_transcription_before_the_runtime_command()
    {
        var sessionId = Guid.NewGuid();
        var recent = ReadyRecording(sessionId);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent],
            userSettings: SettingsWithConfiguredTranscription()));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);
        RecentRecordingTranscriptionRequestedEventArgs? request = null;
        viewModel.RecentRecordingTranscriptionRequested += (_, args) => request = args;

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.True(item.CanTranscribe);
        item.RequestTranscriptionCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal(sessionId, request.SessionId);
        Assert.False(request.ReplaceExisting);
        Assert.Empty(runtime.TranscriptionRequests);

        await viewModel.ConfirmRecentTranscriptionAsync(sessionId, replaceExisting: false);
        Assert.Equal([(sessionId, false)], runtime.TranscriptionRequests);
    }

    [Fact]
    public async Task Restart_snapshot_keeps_manual_transcription_available_with_persisted_discoverable_model()
    {
        var sessionId = Guid.NewGuid();
        var settings = SettingsWithConfiguredTranscription();
        var engine = Assert.Single(settings.Transcription.Engines);
        settings = settings with
        {
            Transcription = settings.Transcription with
            {
                Engines =
                [
                    engine with
                    {
                        SelectedModelId = "whisper-large-v3-turbo",
                        Models = [],
                        SupportsModelDiscovery = true
                    }
                ]
            }
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [ReadyRecording(sessionId)],
            userSettings: settings));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);
        RecentRecordingTranscriptionRequestedEventArgs? request = null;
        viewModel.RecentRecordingTranscriptionRequested += (_, args) => request = args;

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.True(item.CanTranscribe);
        item.RequestTranscriptionCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Empty(runtime.TranscriptionRequests);
        Assert.Empty(runtime.DiscoveredTranscriptionModelEngines);
    }

    [Fact]
    public async Task Completed_transcription_opens_the_current_artifact_and_requests_confirmed_replacement()
    {
        var sessionId = Guid.NewGuid();
        const string currentTranscript = "C:\\Recordings\\meeting.transcript.md";
        const string legacyTranscript = "C:\\Recordings\\legacy.md";
        var recent = ReadyRecording(sessionId) with
        {
            LegacyTranscriptMarkdownPath = legacyTranscript,
            TranscriptMarkdownPath = currentTranscript,
            Transcription = new RuntimeTranscriptionJobSnapshot(
                "job-completed",
                "remote.openrouter",
                "provider/whisper",
                RuntimeTranscriptionJobState.Completed,
                Progress: 1,
                CurrentChunkIndex: null,
                NextAttemptAtUtc: null,
                StableErrorCode: null,
                ErrorMessage: null,
                Usage: new TranscriptionUsage(
                    AudioSeconds: 600,
                    ReportedCost: 0.0123m,
                    Currency: "USD"))
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [recent],
            userSettings: SettingsWithConfiguredTranscription()));
        var shell = new FakeDesktopShell();
        using var viewModel = Create(runtime, shell);
        await viewModel.InitializeAsync(CancellationToken.None);
        RecentRecordingTranscriptionRequestedEventArgs? request = null;
        viewModel.RecentRecordingTranscriptionRequested += (_, args) => request = args;

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.True(item.HasTranscriptionStatus);
        Assert.True(item.HasTranscriptionUsage);
        Assert.Equal("String.Transcription.Usage.DurationAndCost.Format", item.TranscriptionUsageLabel);
        Assert.True(item.RequiresTranscriptReplacementConfirmation);
        await item.OpenTranscriptCommand.ExecuteAsync(null);
        item.RequestTranscriptionCommand.Execute(null);

        Assert.Equal([currentTranscript], shell.OpenedFiles);
        Assert.NotNull(request);
        Assert.True(request.ReplaceExisting);
    }

    [Fact]
    public async Task Active_and_attention_states_expose_progress_cancel_and_actionable_retry()
    {
        var sessionId = Guid.NewGuid();
        var active = ReadyRecording(sessionId) with
        {
            Transcription = new RuntimeTranscriptionJobSnapshot(
                "job-active",
                "remote.groq",
                "whisper-large-v3-turbo",
                RuntimeTranscriptionJobState.Uploading,
                Progress: 0.42,
                CurrentChunkIndex: 0,
                NextAttemptAtUtc: null,
                StableErrorCode: null,
                ErrorMessage: null)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [active],
            userSettings: SettingsWithConfiguredTranscription()));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);

        var activeItem = Assert.Single(viewModel.RecentRecordings);
        Assert.True(activeItem.HasTranscriptionProgress);
        Assert.Equal(42, activeItem.TranscriptionProgressPercent);
        Assert.True(activeItem.CanCancelTranscription);
        Assert.False(activeItem.CanRemove);
        await activeItem.CancelTranscriptionCommand.ExecuteAsync(null);
        Assert.Equal([sessionId], runtime.CancelledTranscriptions);

        var attention = active with
        {
            Transcription = active.Transcription with
            {
                State = RuntimeTranscriptionJobState.AttentionRequired,
                StableErrorCode = "invalid_key"
            }
        };
        runtime.Publish(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings: [attention],
            userSettings: SettingsWithConfiguredTranscription()));
        using var attentionViewModel = Create(runtime);
        await attentionViewModel.InitializeAsync(CancellationToken.None);

        var attentionItem = Assert.Single(attentionViewModel.RecentRecordings);
        Assert.True(attentionItem.CanRetryTranscription);
        Assert.Equal("String.Transcription.State.Attention.Key", attentionItem.TranscriptionStatusLabel);
        await attentionItem.RetryTranscriptionCommand.ExecuteAsync(null);
        Assert.Equal([sessionId], runtime.RetriedTranscriptions);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
    /// </summary>
    [Fact]
    public async Task Local_attention_states_name_the_resource_or_backend_step()
    {
        var sessionId = Guid.NewGuid();
        var recording = ReadyRecording(sessionId) with
        {
            Transcription = new RuntimeTranscriptionJobSnapshot(
                "job-local",
                "local.whisper",
                "small",
                RuntimeTranscriptionJobState.AttentionRequired,
                Progress: 0.4,
                CurrentChunkIndex: 1,
                NextAttemptAtUtc: null,
                StableErrorCode: "model_missing",
                ErrorMessage: null)
        };
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model_missing"] = "String.Transcription.State.Attention.LocalModel",
            ["insufficient_memory"] = "String.Transcription.State.Attention.Memory",
            ["insufficient_disk"] = "String.Transcription.State.Attention.Disk",
            ["backend_unavailable"] = "String.Transcription.State.Attention.LocalBackend",
            ["low_power"] = "String.Transcription.State.Attention.LowPower"
        };
        foreach (var (errorCode, resourceKey) in cases)
        {
            var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
                ApplicationActivityState.Ready,
                recentRecordings:
                [
                    recording with
                    {
                        Transcription = recording.Transcription! with { StableErrorCode = errorCode }
                    }
                ],
                userSettings: SettingsWithConfiguredTranscription()));
            using var viewModel = Create(runtime);
            await viewModel.InitializeAsync(CancellationToken.None);

            Assert.Equal(resourceKey, Assert.Single(viewModel.RecentRecordings).TranscriptionStatusLabel);
        }

        var powerRuntime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Ready,
            recentRecordings:
            [
                recording with
                {
                    Transcription = recording.Transcription! with
                    {
                        StableErrorCode = "low_power_override_required"
                    }
                }
            ],
            userSettings: SettingsWithConfiguredTranscription()));
        using var powerViewModel = Create(powerRuntime);
        await powerViewModel.InitializeAsync(CancellationToken.None);
        var powerWarningItem = Assert.Single(powerViewModel.RecentRecordings);
        Assert.True(powerWarningItem.CanContinueLocalTranscription);
        Assert.False(powerWarningItem.CanRetryTranscription);
        Assert.Equal(
            "String.Transcription.State.Attention.LowPowerOverride",
            powerWarningItem.TranscriptionStatusLabel);

        await powerWarningItem.ContinueLocalTranscriptionCommand.ExecuteAsync(null);

        Assert.Equal([sessionId], powerRuntime.ConfirmedLocalTranscriptionPowerOverrides);
    }

    [Fact]
    public async Task Runtime_action_failure_returns_surface_to_idle_and_shows_plain_attention()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);

        runtime.ActionFailure = new InvalidOperationException("test failure");
        await viewModel.StartManualRecordingCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasAttention);
        Assert.Equal("String.Error.Action", viewModel.AttentionMessage);
    }

    [Fact]
    public async Task Processing_artifact_path_is_not_exposed_as_ready_or_removable()
    {
        var recent = new RecentRecordingSnapshot(
            Guid.NewGuid(),
            "Zoom",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            "C:\\Temp\\session\\output.wav",
            RequiresAttention: false)
        {
            State = RecentRecordingState.Processing
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Processing,
            recentRecordings: [recent]));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        var item = Assert.Single(viewModel.RecentRecordings);
        Assert.False(item.HasAudioPath);
        Assert.False(item.CanRemove);
        Assert.False(item.OpenRecordingCommand.CanExecute(null));
        Assert.False(item.RequestRemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Recovery_action_opens_folder_and_acknowledges_matching_attention()
    {
        var sessionId = Guid.NewGuid();
        const string recoveryPath = "C:\\Recordings\\recovery.wav";
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            finalization: new RecordingFinalizationSnapshot(
                sessionId,
                RecordingArtifactStage.AttentionRequired,
                recoveryPath),
            attentionMessage: "recovery"));
        var shell = new FakeDesktopShell();
        using var viewModel = Create(runtime, shell);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.OpenRecoveryFolderCommand.ExecuteAsync(null);

        Assert.Equal([recoveryPath], shell.OpenedFolders);
        Assert.Equal([sessionId], runtime.AcknowledgedAttentionSessions);
    }

    [Fact]
    public async Task Blocked_capability_exposes_direct_recheck_action()
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Blocked,
            SupportsProcessOutputCapture: false,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.NoActiveAudioEndpoints
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            capability: capability,
            attentionMessage: "blocked"));
        using var viewModel = Create(runtime);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.HasCapabilityRetryAction);
        await viewModel.RetryCapabilitiesCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.RefreshCapabilitiesCalls);
    }

    [Fact]
    public async Task Microphone_capture_degradation_keeps_recording_controls_and_names_the_missing_voice()
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var meeting = ActiveMeeting() with { HasMicrophone = false };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: meeting,
            capability: capability));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.ShowRecordingActions);
        Assert.True(viewModel.HasAttention);
        Assert.True(viewModel.HasCapabilityRetryAction);
        Assert.Equal("String.Attention.MicrophoneNotRecording", viewModel.AttentionMessage);
    }

    [Theory]
    [InlineData(
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
        "String.Setup.Capability.ConfiguredMicrophoneUnavailable")]
    [InlineData(
        RuntimeCapabilityIssue.OutputCaptureUnavailable,
        "String.Setup.Capability.OutputCaptureUnavailable")]
    public async Task Actionable_degraded_capability_exposes_specific_idle_recovery(
        RuntimeCapabilityIssue issue,
        string expectedMessage)
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = issue,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.AttentionRequired,
            capability: capability));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.HasCapabilityRetryAction);
        Assert.Equal(expectedMessage, viewModel.AttentionMessage);
    }

    [Fact]
    public async Task Paused_service_keeps_capability_warning_quiet_until_reenabled()
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Blocked,
            SupportsProcessOutputCapture: false,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.NoActiveAudioEndpoints
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            ApplicationActivityState.Paused,
            serviceEnabled: false,
            capability: capability));
        using var viewModel = Create(runtime);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.HasAttention);
        Assert.False(viewModel.HasCapabilityRetryAction);
        Assert.True(viewModel.ShowEnableServiceAction);
    }

    [Fact]
    public async Task Language_switch_preserves_the_exact_transient_main_action_error()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = Create(runtime, strings: strings);
        await viewModel.InitializeAsync(CancellationToken.None);
        runtime.ActionFailure = new InvalidOperationException("action unavailable");

        await viewModel.StartManualRecordingCommand.ExecuteAsync(null);
        Assert.Equal("ru:String.Error.Action", viewModel.AttentionMessage);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.Error.Action", viewModel.AttentionMessage);
    }

    [Fact]
    public async Task Startup_failure_keeps_its_semantic_state_when_available_language_is_applied()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create())
        {
            ActionFailure = new InvalidOperationException("startup unavailable")
        };
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = Create(runtime, strings: strings);

        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal("ru:String.Error.Initialize", viewModel.StateDescription);
        Assert.Equal("ru:String.Error.Action", viewModel.AttentionMessage);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.State.AttentionRequired.Title", viewModel.StateTitle);
        Assert.Equal("en:String.Error.Initialize", viewModel.StateDescription);
        Assert.Equal("en:String.Error.Action", viewModel.AttentionMessage);
    }

    private static MainWindowViewModel Create(
        FakeApplicationRuntime runtime,
        FakeDesktopShell? shell = null,
        FakeLocalizationService? strings = null,
        TimeProvider? timeProvider = null) =>
        new(
            runtime,
            shell ?? new FakeDesktopShell(),
            strings ?? new FakeLocalizationService(),
            timeProvider);

    private static ActiveMeetingSnapshot ActiveMeeting() => new(
        Guid.NewGuid(),
        "Zoom",
        DateTimeOffset.UtcNow.AddMinutes(-2),
        HasOutput: true,
        HasMicrophone: true,
        IsPaused: false);

    private static MeetingPromptSnapshot Prompt(string candidateId) => new(
        candidateId,
        "zoom",
        "Zoom",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(20),
        ConfidenceScore: 80,
        DecisionReasons: ["application", "conversation"]);

    private static RecentRecordingSnapshot ReadyRecording(Guid sessionId) => new(
        sessionId,
        "Zoom",
        DateTimeOffset.UtcNow.AddMinutes(-12),
        TimeSpan.FromMinutes(10),
        "C:\\Recordings\\meeting.mp3",
        RequiresAttention: false)
    {
        State = RecentRecordingState.Ready
    };

    private static RuntimeUserSettingsSnapshot SettingsWithConfiguredTranscription()
    {
        var model = new TranscriptionModelCapability(
            "whisper-large-v3-turbo",
            "Whisper large v3 turbo",
            IsRecommended: true);
        return RuntimeUserSettingsSnapshot.Initial with
        {
            OnboardingCompleted = true,
            ServiceEnabled = true,
            Transcription = new RuntimeTranscriptionSettingsSnapshot(
                "remote.groq",
                AutomaticEnabled: false,
                Language: "auto",
                RequireZeroDataRetention: true,
                Engines:
                [
                    new RuntimeTranscriptionEngineSnapshot(
                        "remote.groq",
                        "Groq",
                        TranscriptionExecutionKind.Remote,
                        RequiresNetwork: true,
                        "Audio is sent to Groq.",
                        new Uri("https://example.test/groq-policy"),
                        HasCredential: true,
                        model.Id,
                        [model],
                        SupportsModelDiscovery: true,
                        DisclosureAccepted: true,
                        "groq-v1")
                ])
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public int TimerCreationCount { get; private set; }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimerCreationCount++;
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
