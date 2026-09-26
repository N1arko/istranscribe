using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows;
using IsTranscribe.Platform.Windows.Audio.Recording;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsReleaseV2DetectionRuntimeTests
{
    private const string SensitiveFixtureRule = "Private roadmap for Nikita";

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    [Fact]
    public async Task FirstRunStartsDetectionOnlyAfterOnboardingCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-first-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var audio = new FakeAudioPlatform();
        var autostart = new TestAutostartService();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                CreateDetectionCoordinator,
                autostart);

            await runtime.InitializeAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.True(runtime.Snapshot.UserSettings.ServiceEnabled);
            Assert.Equal(ApplicationActivityState.Paused, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);
            Assert.Empty(audio.LastWatchedProcessNames);

            await runtime.CompleteOnboardingAsync(
                runtime.Snapshot.UserSettings.ToUpdate(),
                CancellationToken.None);

            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));
            Assert.True(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.True(autostart.IsEnabled);
            Assert.Equal("zoom", prompt.ProfileId);
            Assert.Contains("zoom.exe", audio.LastWatchedProcessNames, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimePublishesAskPromptAndPersistsIgnoreWithoutStartingRecording()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-detection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    new FakeWindowProvider(),
                    new FakeSpeechProvider(),
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                        SuspectedEntryThreshold: 40,
                        SuspectedExitThreshold: 32,
                        AskEntryThreshold: 70,
                        AskExitThreshold: 65,
                        AskStabilityWindow: TimeSpan.Zero,
                        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                        StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(20),
                        WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                        PromptTimeout: TimeSpan.FromSeconds(12))));
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            Assert.Equal(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.Equal("zoom", prompt.ProfileId);
            Assert.Equal("Zoom", prompt.SourceLabel);
            Assert.Equal(0, audio.RecordingCaptureStartCount);

            await runtime.ResolveMeetingPromptAsync(
                prompt.CandidateId,
                MeetingPromptUserAction.IgnoreApplication,
                CancellationToken.None);
            await WaitForPromptClearedAsync(runtime, TimeSpan.FromSeconds(1));

            Assert.DoesNotContain("zoom.exe", audio.LastWatchedProcessNames, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, audio.RecordingCaptureStartCount);
            var runtimeIgnored = Assert.Single(
                runtime.Snapshot.UserSettings.Applications,
                application => string.Equals(
                    application.ProfileId,
                    "zoom",
                    StringComparison.Ordinal));
            Assert.Equal(MeetingApplicationPolicy.Ignore, runtimeIgnored.Policy);
            var paths = new LocalAppPaths("isTranscribe", root);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFilePath));
            var ignored = document.RootElement
                .GetProperty("release_v2")
                .GetProperty("applications")
                .EnumerateArray()
                .Single(application => string.Equals(
                    application.GetProperty("profile_id").GetString(),
                    "zoom",
                    StringComparison.Ordinal));
            Assert.Equal((int)MeetingApplicationPolicy.Ignore, ignored.GetProperty("policy").GetInt32());

            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            runtime = null;

            var decisionLine = File.ReadLines(paths.HostLogFilePath)
                .Last(line => line.Contains("\"event_code\":\"DETECTION_DECISION\"", StringComparison.Ordinal) &&
                              line.Contains("\"candidate_count\":2", StringComparison.Ordinal));
            Assert.DoesNotContain(SensitiveFixtureRule, decisionLine, StringComparison.Ordinal);
            using var decision = JsonDocument.Parse(decisionLine);
            var candidates = decision.RootElement
                .GetProperty("metadata")
                .GetProperty("candidates")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, candidates.Length);
            Assert.Contains(candidates, candidate =>
                candidate.GetProperty("profile_id").GetString() == "zoom");
            Assert.Contains(candidates, candidate =>
                candidate.GetProperty("profile_id").GetString() == "microsoft-teams" &&
                candidate.GetProperty("evidence").EnumerateArray().Any(fact =>
                    fact.GetProperty("rule_id").GetString() == "untrusted"));
            var replayRecords = File.ReadLines(paths.HostLogFilePath)
                .Where(static line => line.Contains(
                    "\"event_code\":\"DETECTION_REPLAY_FRAME\"",
                    StringComparison.Ordinal))
                .Select(static line =>
                {
                    using var replayLine = JsonDocument.Parse(line);
                    return replayLine.RootElement
                        .GetProperty("metadata")
                        .GetProperty("record")
                        .Deserialize<MeetingDetectionReplayRecord>()!;
                })
                .ToArray();
            Assert.NotEmpty(replayRecords);
            Assert.DoesNotContain(
                File.ReadLines(paths.HostLogFilePath),
                line => line.Contains(SensitiveFixtureRule, StringComparison.Ordinal));
            Assert.All(replayRecords, record =>
            {
                var recomputed = new MeetingConfidenceScorer().Score(record.ToFrame());
                Assert.Equal(record.RecordedScore, recomputed.Score);
                Assert.Equal(record.RecordedBand, recomputed.Band.ToString().ToLowerInvariant());
            });
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task ConcurrentSettingsSaveAndPromptIgnoreMergeInSerializedOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-settings-ignore-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var initialSettings = ApplicationSettings.Default.WithReleaseV2Projection(
            ReleaseV2Settings.Default with
            {
                OnboardingCompleted = true,
                ServiceEnabled = true,
                Autostart = false,
                Applications = []
            });
        var settingsStore = new BlockingSettingsStore(initialSettings);
        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                CreateDetectionCoordinator,
                new TestAutostartService(),
                settingsStore);
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));
            var staleSettingsUpdate = runtime.Snapshot.UserSettings.ToUpdate() with
            {
                Notifications = false,
                Applications = []
            };
            settingsStore.BlockNextSave();

            var settingsTask = runtime
                .UpdateSettingsAsync(staleSettingsUpdate, CancellationToken.None)
                .AsTask();
            await settingsStore.WaitUntilSaveBlockedAsync(TimeSpan.FromSeconds(3));
            var ignoreTask = runtime
                .ResolveMeetingPromptAsync(
                    prompt.CandidateId,
                    MeetingPromptUserAction.IgnoreApplication,
                    CancellationToken.None)
                .AsTask();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Assert.False(ignoreTask.IsCompleted);
            }
            finally
            {
                settingsStore.ReleaseBlockedSave();
            }

            await Task.WhenAll(settingsTask, ignoreTask).WaitAsync(TimeSpan.FromSeconds(5));

            var runtimeIgnore = Assert.Single(runtime.Snapshot.UserSettings.Applications);
            Assert.Equal("zoom", runtimeIgnore.ProfileId);
            Assert.Equal(MeetingApplicationPolicy.Ignore, runtimeIgnore.Policy);
            var persistedIgnore = Assert.Single(settingsStore.Settings.ReleaseV2!.Applications);
            Assert.Equal("zoom", persistedIgnore.ProfileId);
            Assert.Equal(MeetingApplicationPolicy.Ignore, persistedIgnore.Policy);
            Assert.Equal(1, settingsStore.MaxConcurrentSaves);
        }
        finally
        {
            settingsStore.ReleaseBlockedSave();
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task ReadyFinalizationSurvivesDetectionTicksThenReturnsToCoherentPrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-ready-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                CreateDetectionCoordinator,
                new TestAutostartService(),
                settingsStore: null,
                readyFinalizationHold: TimeSpan.FromMilliseconds(300));
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));
            var sessionId = Guid.NewGuid();

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.Ready,
                ActiveMeeting: null,
                new RecordingFinalizationSnapshot(
                    sessionId,
                    RecordingArtifactStage.Ready,
                    RecoverableAudioPath: Path.Combine(root, "meeting.mp3")),
                AttentionMessage: null,
                RefreshRecentRecordings: false));

            await Task.Delay(TimeSpan.FromMilliseconds(120));
            Assert.Equal(ApplicationActivityState.Ready, runtime.Snapshot.Activity);
            Assert.Equal(RecordingArtifactStage.Ready, runtime.Snapshot.RecordingFinalization?.Stage);

            await WaitForFinalizationClearedAsync(runtime, TimeSpan.FromSeconds(2));

            Assert.Equal(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.Equal(prompt.CandidateId, runtime.Snapshot.PendingMeetingPrompt?.CandidateId);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task GenericStartupAttentionSuppressesDetectionUntilAcknowledged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-generic-attention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var paths = new LocalAppPaths("isTranscribe", root);
        var sessionId = Guid.NewGuid();
        await using (var connection = await new SqliteDatabaseInitializer(paths)
                         .InitializeAsync(CancellationToken.None))
        {
            var repository = new MeetingSessionRepository(connection);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    sessionId,
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    "manual",
                    "mixed",
                    "out",
                    "mic") with
                {
                    Status = "recording"
                },
                CancellationToken.None);
        }

        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                CreateDetectionCoordinator,
                new TestAutostartService());
            await runtime.InitializeAsync(CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);
            Assert.Contains(
                runtime.Snapshot.RecentRecordings,
                recording => recording.SessionId == sessionId && recording.RequiresAttention);

            await runtime.AcknowledgeAttentionAsync(Guid.Empty, CancellationToken.None);

            Assert.NotEqual(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.AttentionMessage);
            Assert.Contains(
                runtime.Snapshot.RecentRecordings,
                recording => recording.SessionId == sessionId && recording.RequiresAttention);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task AttentionFinalizationSurvivesDetectionTicksUntilAcknowledgedOrRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-attention-ack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new LocalAppPaths("isTranscribe", root);
        var sessionId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 12, 22, 0, 0, TimeSpan.Zero);
        var recordingsRoot = Path.Combine(root, "recordings");
        var primaryPath = Path.Combine(recordingsRoot, sessionId.ToString("N"), "meeting.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        await File.WriteAllBytesAsync(primaryPath, [1, 2, 3]);
        await SeedRecentRecordingAsync(
            paths,
            sessionId,
            createdAt,
            recordingsRoot,
            primaryPath,
            status: "attention_required");
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                CreateDetectionCoordinator,
                new TestAutostartService());
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.AttentionRequired,
                ActiveMeeting: null,
                new RecordingFinalizationSnapshot(
                    sessionId,
                    RecordingArtifactStage.AttentionRequired,
                    RecoverableAudioPath: primaryPath),
                AttentionMessage: "The source audio is ready for recovery.",
                RefreshRecentRecordings: true));

            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Equal(
                RecordingArtifactStage.AttentionRequired,
                runtime.Snapshot.RecordingFinalization?.Stage);
            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);

            await runtime.AcknowledgeAttentionAsync(sessionId, CancellationToken.None);

            Assert.Null(runtime.Snapshot.RecordingFinalization);
            Assert.Null(runtime.Snapshot.AttentionMessage);
            var acknowledgedRecent = Assert.Single(
                runtime.Snapshot.RecentRecordings,
                recording => recording.SessionId == sessionId);
            Assert.True(acknowledgedRecent.RequiresAttention);
            Assert.Equal(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.Equal(prompt.CandidateId, runtime.Snapshot.PendingMeetingPrompt?.CandidateId);

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.AttentionRequired,
                ActiveMeeting: null,
                new RecordingFinalizationSnapshot(
                    sessionId,
                    RecordingArtifactStage.AttentionRequired,
                    RecoverableAudioPath: primaryPath),
                AttentionMessage: "The source audio is ready for recovery.",
                RefreshRecentRecordings: true));

            await runtime.RemoveRecentRecordingAsync(
                sessionId,
                deleteAudioFile: false,
                CancellationToken.None);

            Assert.Null(runtime.Snapshot.RecordingFinalization);
            Assert.Null(runtime.Snapshot.AttentionMessage);
            Assert.Equal(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.Equal(prompt.CandidateId, runtime.Snapshot.PendingMeetingPrompt?.CandidateId);
            Assert.DoesNotContain(
                runtime.Snapshot.RecentRecordings,
                recording => recording.SessionId == sessionId);
            Assert.True(File.Exists(primaryPath));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#verification
    /// </summary>
    [Fact]
    public async Task ConfirmedAskAndManualRecordingProduceReadyMp3ThroughReleaseRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-runtime-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(allowRecording: true);
        var httpActivities = new ConcurrentQueue<string>();
        using var httpActivityListener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(
                source.Name,
                "System.Net.Http",
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => httpActivities.Enqueue(activity.OperationName)
        };
        ActivitySource.AddActivityListener(httpActivityListener);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    new FakeWindowProvider(),
                    new FakeSpeechProvider(),
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                        SuspectedEntryThreshold: 40,
                        SuspectedExitThreshold: 32,
                        AskEntryThreshold: 70,
                        AskExitThreshold: 65,
                        AskStabilityWindow: TimeSpan.Zero,
                        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                        StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(20),
                        WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                        PromptTimeout: TimeSpan.FromSeconds(12))));
            await runtime.InitializeAsync(CancellationToken.None);
            runtime.SnapshotChanged += (_, _) =>
                throw new InvalidOperationException("Injected runtime observer failure.");
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            await runtime.ResolveMeetingPromptAsync(
                prompt.CandidateId,
                MeetingPromptUserAction.Record,
                CancellationToken.None);
            await WaitForActivityAsync(runtime, ApplicationActivityState.Recording, TimeSpan.FromSeconds(2));
            Assert.Single(audio.RecordingRequests);
            Assert.Contains(
                audio.RecordingRequests[0].Sources,
                static source => source.Kind == AudioCaptureSourceKind.ProcessOutput
                                 && source.RootProcessId == 42);

            await runtime.FinishRecordingAsync(CancellationToken.None);
            await WaitForReadyRecordingCountAsync(runtime, expectedCount: 1, TimeSpan.FromSeconds(5));

            await runtime.SetServiceEnabledAsync(false, CancellationToken.None);
            await runtime.StartManualRecordingAsync(CancellationToken.None);
            await WaitForActivityAsync(runtime, ApplicationActivityState.Recording, TimeSpan.FromSeconds(2));
            Assert.Equal(2, audio.RecordingRequests.Count);
            Assert.Contains(
                audio.RecordingRequests[1].Sources,
                static source => source.Kind == AudioCaptureSourceKind.DeviceLoopback);

            await runtime.FinishRecordingAsync(CancellationToken.None);
            await WaitForReadyRecordingCountAsync(runtime, expectedCount: 2, TimeSpan.FromSeconds(5));
            Assert.All(runtime.Snapshot.RecentRecordings.Take(2), recording =>
            {
                Assert.NotNull(recording.PrimaryAudioPath);
                Assert.EndsWith(".mp3", recording.PrimaryAudioPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(recording.PrimaryAudioPath));
                Assert.False(recording.RequiresAttention);
            });
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                static path => Path.GetExtension(path) is ".wav" or ".partial");

            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            runtime = null;
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var persisted = new MeetingSessionRepository(connection).ListRecent(limit: 10, offset: 0);
            Assert.Equal(2, persisted.Count);
            Assert.All(persisted, session =>
            {
                Assert.Equal("ready", session.RecordingStatus);
                Assert.Equal("not_started", session.TranscriptionStatus);
                Assert.NotNull(session.PrimaryAudioPath);
                Assert.False(session.SourceCleanupPending);
                Assert.Null(session.AudioOutputPath);
                Assert.Null(session.AudioMicPath);
                Assert.Null(session.AudioMixPath);
            });
            Assert.Empty(httpActivities);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://common/PROP-006-release-v2-product-canon#product-model.primary-loop
    /// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
    /// </summary>
    [Fact]
    public async Task ConfirmedAskRecordingFinishesAfterMeetingEligibilityStaysLost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-ask-auto-finish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(allowRecording: true);
        var meetingSignals = new SwitchableMeetingSignalProviders();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    meetingSignals,
                    meetingSignals,
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                        SuspectedEntryThreshold: 40,
                        SuspectedExitThreshold: 32,
                        AskEntryThreshold: 70,
                        AskExitThreshold: 65,
                        AskStabilityWindow: TimeSpan.Zero,
                        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                        StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(20),
                        WindowEvidenceCadence: TimeSpan.FromMilliseconds(20),
                        PromptTimeout: TimeSpan.FromSeconds(12))),
                activeMeetingLossDelay: TimeSpan.FromMilliseconds(120));
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            await runtime.ResolveMeetingPromptAsync(
                prompt.CandidateId,
                MeetingPromptUserAction.Record,
                CancellationToken.None);
            await WaitForActivityAsync(
                runtime,
                ApplicationActivityState.Recording,
                TimeSpan.FromSeconds(2));

            meetingSignals.EndMeeting();

            await WaitForReadyRecordingCountAsync(runtime, 1, TimeSpan.FromSeconds(5));
            Assert.Null(runtime.Snapshot.ActiveMeeting);
            var paths = new LocalAppPaths("isTranscribe", root);
            await WaitForLogTextAsync(
                paths.HostLogFilePath,
                "\"event_code\":\"DETECTION_ACTIVE_SESSION_AUTO_STOP\"",
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
    /// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity
    /// </summary>
    [Fact]
    public async Task ConfirmedAskRecordingContinuesAcrossPresentationWindowTransition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-ask-presentation-continuity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(allowRecording: true);
        var meetingSignals = new SwitchableMeetingSignalProviders();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    meetingSignals,
                    meetingSignals,
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                        SuspectedEntryThreshold: 40,
                        SuspectedExitThreshold: 32,
                        AskEntryThreshold: 70,
                        AskExitThreshold: 65,
                        AskStabilityWindow: TimeSpan.Zero,
                        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                        StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(20),
                        WindowEvidenceCadence: TimeSpan.FromMilliseconds(20),
                        PromptTimeout: TimeSpan.FromSeconds(12))),
                activeMeetingLossDelay: TimeSpan.FromMilliseconds(120));
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            await runtime.ResolveMeetingPromptAsync(
                prompt.CandidateId,
                MeetingPromptUserAction.Record,
                CancellationToken.None);
            await WaitForActivityAsync(
                runtime,
                ApplicationActivityState.Recording,
                TimeSpan.FromSeconds(2));

            meetingSignals.BeginPresentation();
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.Equal(ApplicationActivityState.Recording, runtime.Snapshot.Activity);
            Assert.NotNull(runtime.Snapshot.ActiveMeeting);
            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);

            meetingSignals.EndPresentation();
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.Equal(ApplicationActivityState.Recording, runtime.Snapshot.Activity);
            Assert.NotNull(runtime.Snapshot.ActiveMeeting);
            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);

            meetingSignals.EndMeeting();
            await WaitForReadyRecordingCountAsync(runtime, 1, TimeSpan.FromSeconds(5));
            Assert.Null(runtime.Snapshot.ActiveMeeting);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
    /// </summary>
    [Fact]
    public async Task AskRecordCancellationAfterCaptureStartReleasesDetectionSessionGate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-ask-start-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        using var startCancellation = new CancellationTokenSource();
        var audio = new FakeAudioPlatform(
            allowRecording: true,
            onRecordingCaptureStarted: startCancellation.Cancel);
        MeetingDetectionEngine? engine = null;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) =>
                {
                    engine = new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                        SuspectedEntryThreshold: 40,
                        SuspectedExitThreshold: 32,
                        AskEntryThreshold: 70,
                        AskExitThreshold: 65,
                        AskStabilityWindow: TimeSpan.Zero,
                        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                        StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode);
                    return new MeetingDetectionCoordinator(
                        platform,
                        new FakeWindowProvider(),
                        new FakeSpeechProvider(),
                        profiles,
                        preferences,
                        engine,
                        new MeetingDetectionCoordinatorOptions(
                            ObservationCadence: TimeSpan.FromMilliseconds(20),
                            WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                            PromptTimeout: TimeSpan.FromSeconds(12)));
                });
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime
                .ResolveMeetingPromptAsync(
                    prompt.CandidateId,
                    MeetingPromptUserAction.Record,
                    startCancellation.Token)
                .AsTask());

            Assert.NotNull(engine);
            var observedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            var gateProbe = engine.EvaluateBatch(
                [HighConfidenceFrame("zoom:gate-probe:99", observedAtUtc)],
                observedAtUtc);
            Assert.Equal("zoom:gate-probe:99", gateProbe.PromptCandidate?.Frame.CandidateId);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance
    /// </summary>
    [Fact]
    public async Task EnablingServiceDuringManualRecordingRestartsDetectionAfterFinish()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-enable-during-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(allowRecording: true);
        var detectionCoordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) =>
                {
                    Interlocked.Increment(ref detectionCoordinatorCount);
                    return new MeetingDetectionCoordinator(
                        platform,
                        new FakeWindowProvider(),
                        new FakeSpeechProvider(),
                        profiles,
                        preferences,
                        new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
                            SuspectedEntryThreshold: 40,
                            SuspectedExitThreshold: 32,
                            AskEntryThreshold: 70,
                            AskExitThreshold: 65,
                            AskStabilityWindow: TimeSpan.Zero,
                            SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                            StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
                        new MeetingDetectionCoordinatorOptions(
                            ObservationCadence: TimeSpan.FromMilliseconds(20),
                            WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                            PromptTimeout: TimeSpan.FromSeconds(12)));
                });
            await runtime.InitializeAsync(CancellationToken.None);
            await runtime.SetServiceEnabledAsync(false, CancellationToken.None);
            await runtime.StartManualRecordingAsync(CancellationToken.None);
            await WaitForActivityAsync(runtime, ApplicationActivityState.Recording, TimeSpan.FromSeconds(2));

            await runtime.SetServiceEnabledAsync(true, CancellationToken.None);
            Assert.True(runtime.Snapshot.ServiceEnabled);
            await runtime.FinishRecordingAsync(CancellationToken.None);

            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(5));
            Assert.Equal("zoom", prompt.ProfileId);
            Assert.Equal(2, Volatile.Read(ref detectionCoordinatorCount));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    /// </summary>
    [Fact]
    public async Task FailedManualStartCompletesGateAndRestartsDetectionOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-manual-start-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(allowRecording: false);
        var detectionCoordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) =>
                {
                    Interlocked.Increment(ref detectionCoordinatorCount);
                    return CreateDetectionCoordinator(platform, profiles, preferences, mode);
                });
            await runtime.InitializeAsync(CancellationToken.None);
            await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(3));

            await runtime.StartManualRecordingAsync(CancellationToken.None);

            var failedSession = Assert.IsType<RecordingFinalizationSnapshot>(
                runtime.Snapshot.RecordingFinalization);
            Assert.Equal(RecordingArtifactStage.AttentionRequired, failedSession.Stage);
            await runtime.AcknowledgeAttentionAsync(
                failedSession.SessionId,
                CancellationToken.None);

            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(5));
            Assert.Equal("zoom", prompt.ProfileId);
            Assert.Equal(2, Volatile.Read(ref detectionCoordinatorCount));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    /// </summary>
    [Fact]
    public async Task NaturalTerminalGateFailureIsObservedAndLogged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-terminal-gate-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform(
            allowRecording: true,
            completeRecordingBeforeReturn: true);
        var detectionCoordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) =>
                {
                    if (Interlocked.Increment(ref detectionCoordinatorCount) == 2)
                    {
                        throw new InvalidOperationException("Injected detection restart failure.");
                    }

                    return CreateDetectionCoordinator(platform, profiles, preferences, mode);
                });
            await runtime.InitializeAsync(CancellationToken.None);
            await runtime.StartManualRecordingAsync(CancellationToken.None);

            var paths = new LocalAppPaths("isTranscribe", root);
            await WaitForLogTextAsync(
                paths.HostLogFilePath,
                "Recording gate completion failed after terminal capture state.",
                TimeSpan.FromSeconds(8));
            var countAfterObservedFailure = Volatile.Read(ref detectionCoordinatorCount);
            Assert.True(countAfterObservedFailure >= 2);

            await runtime.StartManualRecordingAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(8));
            Assert.Equal("zoom", prompt.ProfileId);
            Assert.True(Volatile.Read(ref detectionCoordinatorCount) > countAfterObservedFailure);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeShadowModeLogsWouldPromptWithoutPublishingAUserPrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var audio = new FakeAudioPlatform();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Shadow,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    new FakeWindowProvider(),
                    new FakeSpeechProvider(),
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(
                        timing: new MeetingDetectionTimingPolicy(
                            SuspectedEntryThreshold: 40,
                            SuspectedExitThreshold: 32,
                            AskEntryThreshold: 70,
                            AskExitThreshold: 65,
                            AskStabilityWindow: TimeSpan.Zero,
                            SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
                            StaleCandidateRetention: TimeSpan.FromMinutes(10)),
                        mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(20),
                        WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                        PromptTimeout: TimeSpan.FromSeconds(12))));
            await runtime.InitializeAsync(CancellationToken.None);
            var paths = new LocalAppPaths("isTranscribe", root);
            await WaitForLogTextAsync(
                paths.HostLogFilePath,
                "\"shadow_would_prompt_candidate_id\":\"zoom:desktop:42\"",
                TimeSpan.FromSeconds(3));

            Assert.Null(runtime.Snapshot.PendingMeetingPrompt);
            Assert.NotEqual(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.Equal(0, audio.RecordingCaptureStartCount);

            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            runtime = null;
            var log = await File.ReadAllTextAsync(paths.HostLogFilePath);
            Assert.Contains(
                "\"shadow_would_prompt_candidate_id\":\"zoom:desktop:42\"",
                log,
                StringComparison.Ordinal);
            Assert.DoesNotContain("\"prompt_visible\":true", log, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                static path => Path.GetExtension(path) is ".wav" or ".ogg" or ".mp3" or ".m4a");
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await audio.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveIdleClientsDoNotCreatePromptOrAudioArtifact()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LIVE_RUNTIME"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var zoom = FindVisibleProcess("Zoom");
        using var teams = FindVisibleProcess("ms-teams");
        using var telemost = FindVisibleProcess("YandexTelemost");
        using var meet = System.Diagnostics.Process.GetProcessesByName("chrome")
            .FirstOrDefault(static process =>
                process.MainWindowTitle.Contains("Google Meet", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(zoom);
        Assert.NotNull(teams);
        Assert.NotNull(telemost);
        Assert.NotNull(meet);

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-idle-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var paths = new LocalAppPaths("isTranscribe", root);
        var observationSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("ISTRANSCRIBE_LIVE_IDLE_SECONDS"),
            out var configuredSeconds)
            ? Math.Clamp(configuredSeconds, 1, 60)
            : 12;
        try
        {
            var runtime = new WindowsReleaseV2Runtime(root);
            try
            {
                await runtime.InitializeAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(observationSeconds));

                Assert.Null(runtime.Snapshot.PendingMeetingPrompt);
                Assert.NotEqual(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            }
            finally
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                static path => Path.GetExtension(path) is ".wav" or ".ogg" or ".mp3" or ".m4a");
            var log = await File.ReadAllTextAsync(paths.HostLogFilePath);
            Assert.DoesNotContain("\"prompt_visible\":true", log, StringComparison.Ordinal);
            Assert.Contains("\"event_code\":\"V2_RUNTIME_STOPPED\"", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveActiveMeetingProducesAskWithinFifteenSeconds()
    {
        var expectedProfileId = Environment.GetEnvironmentVariable(
            "ISTRANSCRIBE_RUN_LIVE_ACTIVE_PROFILE");
        if (string.IsNullOrWhiteSpace(expectedProfileId))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-active-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await WriteOnboardedSettingsAsync(root);
        var runtime = new WindowsReleaseV2Runtime(root);
        try
        {
            await runtime.InitializeAsync(CancellationToken.None);
            var prompt = await WaitForPromptAsync(runtime, TimeSpan.FromSeconds(15));

            Assert.Equal(expectedProfileId, prompt.ProfileId);
            Assert.Equal(ApplicationActivityState.AwaitingConfirmation, runtime.Snapshot.Activity);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                static path => Path.GetExtension(path) is ".wav" or ".ogg" or ".mp3" or ".m4a");
        }
        finally
        {
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task RenameRecentRecordingAsyncPersistsDisplayTitleWithoutChangingAudioArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new LocalAppPaths("isTranscribe", root);
        var sessionId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var recordingsRoot = Path.Combine(root, "recordings");
        var primaryPath = Path.Combine(recordingsRoot, sessionId.ToString("N"), "meeting.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        await File.WriteAllBytesAsync(primaryPath, [1, 2, 3]);
        await SeedRecentRecordingAsync(paths, sessionId, createdAt, recordingsRoot, primaryPath);

        try
        {
            await using (var runtime = new WindowsReleaseV2Runtime(
                             root,
                             new FakeAudioPlatform(),
                             MeetingDetectionMode.Live))
            {
                await runtime.InitializeAsync(CancellationToken.None);
                var original = Assert.Single(
                    runtime.Snapshot.RecentRecordings,
                    item => item.SessionId == sessionId);
                Assert.Equal("Manual recording", original.SourceLabel);
                Assert.Equal("Manual recording", original.DisplayTitle);

                await runtime.RenameRecentRecordingAsync(
                    sessionId,
                    "  Weekly product sync  ",
                    CancellationToken.None);

                var renamed = Assert.Single(
                    runtime.Snapshot.RecentRecordings,
                    item => item.SessionId == sessionId);
                Assert.Equal("Manual recording", renamed.SourceLabel);
                Assert.Equal("Weekly product sync", renamed.DisplayTitle);
                Assert.Equal(primaryPath, renamed.PrimaryAudioPath);
                Assert.True(File.Exists(primaryPath));
            }

            await using var restartedRuntime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live);
            await restartedRuntime.InitializeAsync(CancellationToken.None);
            var persisted = Assert.Single(
                restartedRuntime.Snapshot.RecentRecordings,
                item => item.SessionId == sessionId);
            Assert.Equal("Weekly product sync", persisted.DisplayTitle);
            Assert.Equal(primaryPath, persisted.PrimaryAudioPath);
            Assert.True(File.Exists(primaryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task RemoveRecentRecordingAsync_KeepFileHidesHistoryAndRetainsAudio()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-remove-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new LocalAppPaths("isTranscribe", root);
        var sessionId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero);
        var recordingsRoot = Path.Combine(root, "recordings");
        var primaryPath = Path.Combine(recordingsRoot, sessionId.ToString("N"), "meeting.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        await File.WriteAllBytesAsync(primaryPath, [1, 2, 3]);
        await SeedRecentRecordingAsync(paths, sessionId, createdAt, recordingsRoot, primaryPath);

        var audio = new FakeAudioPlatform();
        var runtime = new WindowsReleaseV2Runtime(root, audio, MeetingDetectionMode.Live);
        try
        {
            await runtime.InitializeAsync(CancellationToken.None);
            Assert.Contains(runtime.Snapshot.RecentRecordings, item => item.SessionId == sessionId);

            await runtime.RemoveRecentRecordingAsync(
                sessionId,
                deleteAudioFile: false,
                CancellationToken.None);

            Assert.DoesNotContain(runtime.Snapshot.RecentRecordings, item => item.SessionId == sessionId);
            Assert.True(File.Exists(primaryPath));
        }
        finally
        {
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    [Fact]
    public async Task RemoveRecentRecordingAsync_UnsafeFileRemainsRecoverableAfterCheckpoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-remove-unsafe-{Guid.NewGuid():N}");
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"istranscribe-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
        var paths = new LocalAppPaths("isTranscribe", root);
        var sessionId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 12, 21, 0, 0, TimeSpan.Zero);
        var recordingsRoot = Path.Combine(root, "recordings");
        var outsidePath = Path.Combine(outsideRoot, "unrelated.wav");
        await File.WriteAllBytesAsync(outsidePath, [1, 2, 3]);
        await SeedRecentRecordingAsync(paths, sessionId, createdAt, recordingsRoot, outsidePath);

        var audio = new FakeAudioPlatform();
        var runtime = new WindowsReleaseV2Runtime(root, audio, MeetingDetectionMode.Live);
        try
        {
            await runtime.InitializeAsync(CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime
                .RemoveRecentRecordingAsync(
                    sessionId,
                    deleteAudioFile: true,
                    CancellationToken.None)
                .AsTask());

            Assert.True(File.Exists(outsidePath));
            var recoverable = Assert.Single(
                runtime.Snapshot.RecentRecordings,
                item => item.SessionId == sessionId);
            Assert.True(recoverable.RequiresAttention);
            Assert.Equal(RecentRecordingState.AttentionRequired, recoverable.State);
        }
        finally
        {
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    private static async Task SeedRecentRecordingAsync(
        LocalAppPaths paths,
        Guid sessionId,
        DateTimeOffset createdAt,
        string recordingsRoot,
        string primaryPath,
        string status = "ready",
        string sourceApp = "Manual recording")
    {
        var settingsStore = new JsonApplicationSettingsStore(paths);
        await settingsStore.SaveAsync(
            ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with
                {
                    OnboardingCompleted = false,
                    RecordingsFolder = recordingsRoot
                }),
            CancellationToken.None);
        await using var connection = await new SqliteDatabaseInitializer(paths)
            .InitializeAsync(CancellationToken.None);
        var repository = new MeetingSessionRepository(connection);
        await repository.UpsertAsync(
            MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out", "mic") with
            {
                Status = status,
                SourceApp = sourceApp,
                EndedAtUtc = createdAt.AddMinutes(5),
                DurationSeconds = 300,
                PrimaryAudioPath = primaryPath
            },
            CancellationToken.None);
    }

    private static MeetingObservationFrame HighConfidenceFrame(
        string candidateId,
        DateTimeOffset observedAtUtc) => new(
        observedAtUtc,
        candidateId,
        "zoom",
        "Zoom",
        MeetingCandidateContext.DedicatedApplication,
        RootProcessId: 99,
        ProcessName: "zoom.exe",
        [
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "zoom.meeting-window"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")
        ]);

    private static async Task WriteOnboardedSettingsAsync(string root)
    {
        var store = new JsonApplicationSettingsStore(new LocalAppPaths("isTranscribe", root));
        await store.SaveAsync(
            ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with { OnboardingCompleted = true }),
            CancellationToken.None);
    }

    private static MeetingDetectionCoordinator CreateDetectionCoordinator(
        IAudioPlatform platform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> preferences,
        MeetingDetectionMode mode) => new(
        platform,
        new FakeWindowProvider(),
        new FakeSpeechProvider(),
        profiles,
        preferences,
        new MeetingDetectionEngine(timing: new MeetingDetectionTimingPolicy(
            SuspectedEntryThreshold: 40,
            SuspectedExitThreshold: 32,
            AskEntryThreshold: 70,
            AskExitThreshold: 65,
            AskStabilityWindow: TimeSpan.Zero,
            SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
            StaleCandidateRetention: TimeSpan.FromMinutes(10)), mode: mode),
        new MeetingDetectionCoordinatorOptions(
            ObservationCadence: TimeSpan.FromMilliseconds(20),
            WindowEvidenceCadence: TimeSpan.FromSeconds(1),
            PromptTimeout: TimeSpan.FromSeconds(12)));

    private static async Task<MeetingPromptSnapshot> WaitForPromptAsync(
        WindowsReleaseV2Runtime runtime,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Snapshot.PendingMeetingPrompt is { } prompt)
            {
                return prompt;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("Detection runtime did not publish an Ask prompt.");
    }

    private static async Task WaitForPromptClearedAsync(
        WindowsReleaseV2Runtime runtime,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Snapshot.PendingMeetingPrompt is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("Detection runtime did not clear the resolved prompt.");
    }

    private static async Task WaitForFinalizationClearedAsync(
        WindowsReleaseV2Runtime runtime,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Snapshot.RecordingFinalization is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("Runtime did not clear the terminal recording finalization.");
    }

    private static async Task WaitForActivityAsync(
        WindowsReleaseV2Runtime runtime,
        ApplicationActivityState expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Snapshot.Activity == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException($"Runtime did not enter activity '{expected}'.");
    }

    private static async Task WaitForReadyRecordingCountAsync(
        WindowsReleaseV2Runtime runtime,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Snapshot.RecentRecordings.Count(recording =>
                    !recording.RequiresAttention
                    && !string.IsNullOrWhiteSpace(recording.PrimaryAudioPath)
                    && File.Exists(recording.PrimaryAudioPath)) >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("Runtime did not publish the expected ready recording count.");
    }

    private static async Task WaitForLogTextAsync(
        string path,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    if ((await ReadSharedLogTextAsync(path)).Contains(expected, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("Detection runtime did not write the expected diagnostic.");
    }

    private static async Task<string> ReadSharedLogTextAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static System.Diagnostics.Process? FindVisibleProcess(string processName) =>
        System.Diagnostics.Process.GetProcessesByName(processName)
            .FirstOrDefault(static process =>
                process.MainWindowHandle != IntPtr.Zero &&
                !string.IsNullOrWhiteSpace(process.MainWindowTitle));

    private sealed class FakeWindowProvider : IMeetingWindowEvidenceProvider
    {
        public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            MeetingProfileRegistry profiles,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>(
            [
                new MeetingWindowEvidenceSnapshot(
                    audioPlatform.ObservedAtUtc,
                    "zoom:desktop:42",
                    "zoom",
                    "Zoom",
                    MeetingCandidateContext.DedicatedApplication,
                    42,
                    "zoom.exe",
                    [
                        new MeetingEvidenceFact(
                            MeetingEvidenceKind.MeetingControls,
                            "zoom.call-controls",
                            ProviderId: "fixture")
                    ]),
                new MeetingWindowEvidenceSnapshot(
                    audioPlatform.ObservedAtUtc,
                    "microsoft-teams:desktop:42",
                    "microsoft-teams",
                    "Microsoft Teams",
                    MeetingCandidateContext.DedicatedApplication,
                    42,
                    "zoom.exe",
                    [
                        new MeetingEvidenceFact(
                            MeetingEvidenceKind.MeetingWindow,
                            SensitiveFixtureRule,
                            Strength: 0.1,
                            ProviderId: "fixture")
                    ])
            ]);
    }

    private sealed class FakeSpeechProvider : IMeetingSpeechActivityProvider
    {
        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } = MeetingSpeechActivitySnapshot.Empty;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            Snapshot = new MeetingSpeechActivitySnapshot(
                audioPlatform.ObservedAtUtc,
                new Dictionary<int, MeetingSpeechActivitySummary>
                {
                    [42] = new(0.8, true, TimeSpan.FromSeconds(4), 0.6)
                },
                MeetingSpeechActivitySummary.Empty,
                new Dictionary<int, double>());
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SwitchableMeetingSignalProviders :
        IMeetingWindowEvidenceProvider,
        IMeetingSpeechActivityProvider
    {
        private volatile bool _meetingActive = true;
        private volatile bool _presentationActive;

        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } =
            MeetingSpeechActivitySnapshot.Empty;

        public void EndMeeting() => _meetingActive = false;

        public void BeginPresentation() => _presentationActive = true;

        public void EndPresentation() => _presentationActive = false;

        ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>>
            IMeetingWindowEvidenceProvider.ObserveAsync(
                AudioPlatformSnapshot audioPlatform,
                MeetingProfileRegistry profiles,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>(
                _meetingActive && !_presentationActive
                    ?
                    [
                        new MeetingWindowEvidenceSnapshot(
                            audioPlatform.ObservedAtUtc,
                            "zoom:desktop:42",
                            "zoom",
                            "Zoom",
                            MeetingCandidateContext.DedicatedApplication,
                            42,
                            "zoom.exe",
                            [
                                new MeetingEvidenceFact(
                                    MeetingEvidenceKind.MeetingControls,
                                    "zoom.call-controls",
                                    ProviderId: "fixture")
                            ])
                    ]
                    : []);

        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            Snapshot = _meetingActive
                ? new MeetingSpeechActivitySnapshot(
                    audioPlatform.ObservedAtUtc,
                    new Dictionary<int, MeetingSpeechActivitySummary>
                    {
                        [42] = new(0.8, true, TimeSpan.FromSeconds(4), 0.6)
                    },
                    MeetingSpeechActivitySummary.Empty,
                    new Dictionary<int, double>())
                : MeetingSpeechActivitySnapshot.Empty;
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioPlatform(
        bool allowRecording = false,
        Action? onRecordingCaptureStarted = null,
        bool completeRecordingBeforeReturn = false) : IAudioPlatform
    {
        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

        public AudioPlatformSnapshot Snapshot { get; private set; } = new(
            DateTimeOffset.UtcNow,
            new AudioPlatformCapabilities(true, true, "fixture"),
            [new AudioEndpointSnapshot("output", "Output", true, true)],
            [new AudioEndpointSnapshot("mic", "Mic", true, true)],
            [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 })],
            [
                new AudioSignalSnapshot(
                    DateTimeOffset.UtcNow,
                    "zoom.exe",
                    42,
                    "AudioSessionStateActive",
                    -20,
                    "output",
                    true,
                    true)
            ]);

        public IReadOnlyCollection<string> LastWatchedProcessNames { get; private set; } = [];

        public int RecordingCaptureStartCount { get; private set; }

        public List<AudioCaptureRequest> RecordingRequests { get; } = [];

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken)
        {
            LastWatchedProcessNames = watchedProcessNames.ToArray();
            SnapshotChanged?.Invoke(this, Snapshot);
            return ValueTask.CompletedTask;
        }

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
            LastWatchedProcessNames = processNames.ToArray();
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken)
        {
            RecordingCaptureStartCount++;
            RecordingRequests.Add(request);
            if (allowRecording)
            {
                var captureSession = new FakeCaptureSession(request);
                if (completeRecordingBeforeReturn)
                {
                    captureSession.CompleteNaturally(raiseEvent: false);
                }

                onRecordingCaptureStarted?.Invoke();
                return ValueTask.FromResult<IAudioCaptureSession>(captureSession);
            }

            throw new InvalidOperationException("Recording capture must not start before confirmation.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSettingsStore(ApplicationSettings initialSettings) : IApplicationSettingsStore
    {
        private readonly object _sync = new();
        private ApplicationSettings _settings = initialSettings;
        private TaskCompletionSource<bool> _saveBlocked = CreateSignal();
        private TaskCompletionSource<bool> _releaseSave = CreateSignal();
        private int _blockNextSave;
        private int _activeSaves;
        private int _maxConcurrentSaves;

        public ApplicationSettings Settings
        {
            get
            {
                lock (_sync)
                {
                    return _settings;
                }
            }
        }

        public int MaxConcurrentSaves => Volatile.Read(ref _maxConcurrentSaves);

        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Settings);

        public async ValueTask SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken)
        {
            var activeSaves = Interlocked.Increment(ref _activeSaves);
            UpdateMaximum(ref _maxConcurrentSaves, activeSaves);
            try
            {
                if (Interlocked.Exchange(ref _blockNextSave, 0) == 1)
                {
                    _saveBlocked.TrySetResult(true);
                    await _releaseSave.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    _settings = settings;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeSaves);
            }
        }

        public void BlockNextSave()
        {
            _saveBlocked = CreateSignal();
            _releaseSave = CreateSignal();
            Interlocked.Exchange(ref _blockNextSave, 1);
        }

        public async Task WaitUntilSaveBlockedAsync(TimeSpan timeout) =>
            await _saveBlocked.Task.WaitAsync(timeout);

        public void ReleaseBlockedSave() => _releaseSave.TrySetResult(true);

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void UpdateMaximum(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FakeCaptureSession(AudioCaptureRequest request) : IAudioCaptureSession
    {
        private bool _stopped;

        public event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged;

        public AudioCaptureSessionSnapshot Snapshot { get; private set; } = new(
            request.SessionId,
            AudioCaptureState.Running,
            true,
            [],
            []);

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PromotePrebufferAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            if (_stopped)
            {
                return ValueTask.CompletedTask;
            }

            CompleteNaturally(raiseEvent: true);
            return ValueTask.CompletedTask;
        }

        public void CompleteNaturally(bool raiseEvent)
        {
            _stopped = true;
            var artifacts = new List<AudioCaptureArtifactSnapshot>();
            foreach (var source in request.Sources)
            {
                var microphone = source.Kind == AudioCaptureSourceKind.Microphone;
                var path = Path.Combine(
                    request.TempSessionDirectoryPath,
                    microphone ? "mic.wav" : "output.wav");
                WriteTone(path, microphone ? 880 : 440);
                artifacts.Add(new AudioCaptureArtifactSnapshot(
                    microphone ? AudioCaptureArtifactKind.Microphone : AudioCaptureArtifactKind.Output,
                    path,
                    new FileInfo(path).Length,
                    DateTimeOffset.UtcNow,
                    microphone ? TimeSpan.FromMilliseconds(15) : TimeSpan.Zero));
            }

            Snapshot = new AudioCaptureSessionSnapshot(
                request.SessionId,
                AudioCaptureState.Completed,
                true,
                artifacts,
                []);
            if (raiseEvent)
            {
                SnapshotChanged?.Invoke(this, Snapshot);
            }
        }

        public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

        private static void WriteTone(string path, double frequency)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var format = new WaveFormat(48_000, 16, 2);
            using var writer = new WaveFileWriter(path, format);
            for (var frame = 0; frame < 24_000; frame++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * frame / format.SampleRate) * 8_000);
                writer.WriteByte((byte)sample);
                writer.WriteByte((byte)(sample >> 8));
                writer.WriteByte((byte)sample);
                writer.WriteByte((byte)(sample >> 8));
            }
        }
    }
}
