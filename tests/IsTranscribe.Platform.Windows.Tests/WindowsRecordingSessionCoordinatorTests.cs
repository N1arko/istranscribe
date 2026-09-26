using IsTranscribe.Core.Audio;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Recording;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows.Audio.Finalization;
using IsTranscribe.Platform.Windows.Audio.Recording;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance
/// </summary>
public sealed class WindowsRecordingSessionCoordinatorTests
{
    [Fact]
    public async Task ManualAndAskUseOneCaptureAndFinalizationPath()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(
                    new FakeEncoder(TimeSpan.FromMilliseconds(100)),
                    logger),
                logger);
            var snapshots = new List<WindowsRecordingCoordinatorSnapshot>();
            coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            await coordinator.PauseOrResumeAsync(CancellationToken.None);
            await coordinator.PauseOrResumeAsync(CancellationToken.None);
            var manualFinish = coordinator.FinishAsync(CancellationToken.None).AsTask();
            Assert.False(manualFinish.IsCompleted);
            var manualResult = await manualFinish;

            Assert.NotNull(manualResult);
            Assert.True(manualResult.IsReady);
            Assert.True(File.Exists(manualResult.PrimaryAudioPath));

            Assert.True(await coordinator.StartAskAsync(
                "Zoom",
                rootProcessId: 42,
                processName: "zoom.exe",
                CancellationToken.None));
            var askResult = await coordinator.FinishAsync(CancellationToken.None);

            Assert.NotNull(askResult);
            Assert.True(askResult.IsReady);
            Assert.True(File.Exists(askResult.PrimaryAudioPath));
            Assert.Equal(2, audioPlatform.Requests.Count);
            Assert.Contains(
                audioPlatform.Requests[0].Sources,
                static source => source.Kind == AudioCaptureSourceKind.DeviceLoopback);
            Assert.Contains(
                audioPlatform.Requests[1].Sources,
                static source => source.Kind == AudioCaptureSourceKind.ProcessOutput
                                 && source.RootProcessId == 42);
            Assert.All(audioPlatform.Requests, request =>
                Assert.Contains(
                    request.Sources,
                    static source => source.Kind == AudioCaptureSourceKind.Microphone));

            var recent = repository.ListRecent(limit: 10, offset: 0);
            Assert.Equal(2, recent.Count);
            Assert.All(recent, session =>
            {
                Assert.Equal("ready", session.RecordingStatus);
                Assert.Equal("not_started", session.TranscriptionStatus);
                Assert.NotNull(session.PrimaryAudioPath);
                Assert.True(File.Exists(session.PrimaryAudioPath));
                Assert.False(session.SourceCleanupPending);
                Assert.Null(session.AudioOutputPath);
                Assert.Null(session.AudioMicPath);
                Assert.Null(session.AudioMixPath);
            });
            Assert.Contains(snapshots, static snapshot =>
                snapshot.Finalization?.Stage == RecordingArtifactStage.Processing);
            Assert.Contains(snapshots, static snapshot =>
                snapshot.Finalization is
                {
                    Stage: RecordingArtifactStage.Processing,
                    Progress: >= 0.5
                });
            Assert.Contains(snapshots, static snapshot =>
                snapshot.Finalization?.Stage == RecordingArtifactStage.Verifying);
            Assert.Contains(snapshots, static snapshot =>
                snapshot.Finalization?.Stage == RecordingArtifactStage.Promoting);
            Assert.Contains(snapshots, static snapshot =>
                snapshot.Finalization?.Stage == RecordingArtifactStage.Ready);

            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task PauseFreezesActiveDurationAndResumeContinuesFromTheFrozenValue()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var timeProvider = new ManualTimeProvider(
                DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger,
                timeProvider);
            var snapshots = new List<WindowsRecordingCoordinatorSnapshot>();
            coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            timeProvider.Advance(TimeSpan.FromSeconds(12));
            await coordinator.PauseOrResumeAsync(CancellationToken.None);

            var paused = snapshots.Last(static snapshot => snapshot.ActiveMeeting?.IsPaused == true)
                .ActiveMeeting!;
            Assert.Equal(TimeSpan.FromSeconds(12), paused.ActiveDuration);
            Assert.Equal(timeProvider.GetUtcNow(), paused.ActiveDurationMeasuredAtUtc);

            timeProvider.Advance(TimeSpan.FromSeconds(30));
            await coordinator.PauseOrResumeAsync(CancellationToken.None);

            var resumed = snapshots.Last(static snapshot => snapshot.ActiveMeeting?.IsPaused == false)
                .ActiveMeeting!;
            Assert.Equal(TimeSpan.FromSeconds(12), resumed.ActiveDuration);
            Assert.Equal(timeProvider.GetUtcNow(), resumed.ActiveDurationMeasuredAtUtc);

            timeProvider.Advance(TimeSpan.FromSeconds(8));
            await coordinator.PauseOrResumeAsync(CancellationToken.None);

            var pausedAgain = snapshots.Last(static snapshot => snapshot.ActiveMeeting?.IsPaused == true)
                .ActiveMeeting!;
            Assert.Equal(TimeSpan.FromSeconds(20), pausedAgain.ActiveDuration);

            Assert.True(await coordinator.DiscardAsync(CancellationToken.None));
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task MicrophoneCaptureFailureContinuesOutputWithExplicitCapabilityIssue()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform
            {
                RejectRequest = request => request.Sources.Any(static source =>
                    source.Kind == AudioCaptureSourceKind.Microphone)
            };
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            var snapshots = new List<WindowsRecordingCoordinatorSnapshot>();
            coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));

            Assert.Equal(2, audioPlatform.Requests.Count);
            Assert.Contains(
                audioPlatform.Requests[0].Sources,
                static source => source.Kind == AudioCaptureSourceKind.Microphone);
            Assert.DoesNotContain(
                audioPlatform.Requests[1].Sources,
                static source => source.Kind == AudioCaptureSourceKind.Microphone);
            var recording = snapshots.Last(static snapshot =>
                snapshot.Activity == ApplicationActivityState.Recording);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                recording.CapabilityIssue);
            Assert.True(recording.ActiveMeeting?.HasOutput);
            Assert.False(recording.ActiveMeeting?.HasMicrophone);

            await coordinator.FinishAsync(CancellationToken.None);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task MissingConfiguredMicrophoneIsNotSilentlyReplaced()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings"),
                    FollowSystemDefaultMicrophone = false,
                    MicrophoneDeviceId = "missing-selected-mic"
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            WindowsRecordingCoordinatorSnapshot? recording = null;
            coordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.Activity == ApplicationActivityState.Recording)
                {
                    recording = snapshot;
                }
            };

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));

            var request = Assert.Single(audioPlatform.Requests);
            Assert.DoesNotContain(
                request.Sources,
                static source => source.Kind == AudioCaptureSourceKind.Microphone);
            Assert.Equal(
                RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
                recording?.CapabilityIssue);
            Assert.False(recording?.ActiveMeeting?.HasMicrophone);

            await coordinator.FinishAsync(CancellationToken.None);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverPendingAsync_RebuildsSourcesAndReconcilesAlreadyPromotedFile()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var pathResolver = new ArtifactPathResolver(paths);
            var encoder = new FakeEncoder();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                pathResolver,
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(encoder, logger),
                logger);

            var malformedId = Guid.NewGuid();
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    malformedId,
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    "ask",
                    "process_output",
                    null,
                    null) with
                {
                    Status = "processing",
                    SourceApp = "Повреждённая запись"
                },
                CancellationToken.None);

            var processingId = Guid.NewGuid();
            var processingCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var processingTemp = pathResolver.GetTempSessionDirectoryPath(settings, processingId);
            var processingOutput = Path.Combine(processingTemp, "output.wav");
            var processingMic = Path.Combine(processingTemp, "mic.wav");
            WriteTone(processingOutput, 440);
            WriteTone(processingMic, 880);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    processingId,
                    processingCreatedAt,
                    "manual",
                    "device_loopback+microphone",
                    "output",
                    "mic") with
                {
                    Status = "processing",
                    SourceApp = "Ручная запись",
                    TempSessionPath = processingTemp,
                    StagedPrimaryPath = pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        processingId,
                        processingCreatedAt,
                        "Ручная запись"),
                    AudioOutputPath = processingOutput,
                    AudioMicPath = processingMic
                },
                CancellationToken.None);

            var promotingId = Guid.NewGuid();
            var promotingCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var promotingTemp = pathResolver.GetTempSessionDirectoryPath(settings, promotingId);
            var promotingOutput = Path.Combine(promotingTemp, "output.wav");
            WriteTone(promotingOutput, 550);
            var promotedFinal = pathResolver.GetPrimaryAudioFilePath(
                settings,
                promotingId,
                promotingCreatedAt,
                "Zoom");
            Directory.CreateDirectory(Path.GetDirectoryName(promotedFinal)!);
            await File.WriteAllBytesAsync(promotedFinal, FakeEncoder.EncodedBytes);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    promotingId,
                    promotingCreatedAt,
                    "ask",
                    "process_output",
                    null,
                    null) with
                {
                    Status = "promoting",
                    SourceApp = "Zoom",
                    TempSessionPath = promotingTemp,
                    StagedPrimaryPath = pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        promotingId,
                        promotingCreatedAt,
                        "Zoom"),
                    AudioOutputPath = promotingOutput
                },
                CancellationToken.None);

            await coordinator.RecoverPendingAsync(CancellationToken.None);

            var recovered = repository.ListRecent(limit: 10, offset: 0);
            Assert.Equal(3, recovered.Count);
            var ready = recovered.Where(static session => session.RecordingStatus == "ready").ToArray();
            Assert.Equal(2, ready.Length);
            Assert.All(ready, session =>
            {
                Assert.NotNull(session.PrimaryAudioPath);
                Assert.True(File.Exists(session.PrimaryAudioPath));
                Assert.False(session.SourceCleanupPending);
            });
            var attention = Assert.Single(recovered, static session =>
                session.RecordingStatus == "attention_required");
            Assert.Equal(malformedId.ToString("N"), attention.Id);
            Assert.Equal("artifact_recovery_failed", attention.ArtifactErrorCode);
            Assert.Equal(1, encoder.EncodeCount);
            Assert.False(File.Exists(processingOutput));
            Assert.False(File.Exists(processingMic));
            Assert.False(File.Exists(promotingOutput));
            Assert.True(File.Exists(promotedFinal));

            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    [Fact]
    public async Task ContinuityFallbackKeepsMicrophoneCaptureIssueVisible()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            var snapshots = new List<WindowsRecordingCoordinatorSnapshot>();
            coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);
            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            audioPlatform.RejectRequest = request =>
                request.TempSessionDirectoryPath.Contains("legs", StringComparison.OrdinalIgnoreCase)
                && request.Sources.Any(static source =>
                    source.Kind == AudioCaptureSourceKind.Microphone);

            audioPlatform.SwitchDefaultDevices("output-2", "mic-2");
            await WaitUntilAsync(
                () => audioPlatform.Requests.Count >= 3,
                TimeSpan.FromSeconds(3));

            var fallbackRequest = audioPlatform.Requests.Last();
            Assert.DoesNotContain(
                fallbackRequest.Sources,
                static source => source.Kind == AudioCaptureSourceKind.Microphone);
            var degradedRecording = snapshots.Last(static snapshot =>
                snapshot.Activity == ApplicationActivityState.Recording);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                degradedRecording.CapabilityIssue);
            Assert.False(degradedRecording.ActiveMeeting?.HasMicrophone);

            await coordinator.FinishAsync(CancellationToken.None);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    /// </summary>
    [Fact]
    public async Task ShutdownCancelledRecoveryResumesNextLaunchWithoutRetryingPermanentAttention()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var pathResolver = new ArtifactPathResolver(paths);
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var cancelledSessionId = Guid.NewGuid();
            var cancelledCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
            var cancelledTempPath = pathResolver.GetTempSessionDirectoryPath(settings, cancelledSessionId);
            var cancelledSourcePath = Path.Combine(cancelledTempPath, "output.wav");
            WriteTone(cancelledSourcePath, 440);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    cancelledSessionId,
                    cancelledCreatedAtUtc,
                    "manual",
                    "device_loopback",
                    "output",
                    null) with
                {
                    Status = "processing",
                    SourceApp = "Прерванное восстановление",
                    TempSessionPath = cancelledTempPath,
                    StagedPrimaryPath = pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        cancelledSessionId,
                        cancelledCreatedAtUtc,
                        "Прерванное восстановление"),
                    AudioOutputPath = cancelledSourcePath
                },
                CancellationToken.None);

            var permanentSessionId = Guid.NewGuid();
            var permanentCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            var permanentTempPath = pathResolver.GetTempSessionDirectoryPath(settings, permanentSessionId);
            var permanentSourcePath = Path.Combine(permanentTempPath, "output.wav");
            WriteTone(permanentSourcePath, 880);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    permanentSessionId,
                    permanentCreatedAtUtc,
                    "manual",
                    "device_loopback",
                    "output",
                    null) with
                {
                    Status = "attention_required",
                    SourceApp = "Постоянная ошибка",
                    TempSessionPath = permanentTempPath,
                    StagedPrimaryPath = pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        permanentSessionId,
                        permanentCreatedAtUtc,
                        "Постоянная ошибка"),
                    AudioOutputPath = permanentSourcePath,
                    SourceCleanupPending = true,
                    ArtifactErrorCode = "mp3_encoder_unavailable",
                    ArtifactErrorMessage = "Injected permanent encoder failure."
                },
                CancellationToken.None);

            var cancellingEncoder = new CancellationEncoder();
            var firstCoordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                pathResolver,
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(cancellingEncoder, logger),
                logger);
            using (var cancellation = new CancellationTokenSource())
            {
                var recoveryTask = firstCoordinator.RecoverPendingAsync(cancellation.Token);
                await cancellingEncoder.Started.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recoveryTask);
            }

            var interrupted = repository.ListRecent(limit: 10, offset: 0)
                .Single(session => session.Id == cancelledSessionId.ToString("N"));
            Assert.Equal("processing", interrupted.RecordingStatus);
            Assert.Null(interrupted.ArtifactErrorCode);
            Assert.True(File.Exists(cancelledSourcePath));
            await firstCoordinator.DisposeAsync();

            var successfulEncoder = new FakeEncoder();
            var secondCoordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                pathResolver,
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(successfulEncoder, logger),
                logger);
            await secondCoordinator.RecoverPendingAsync(CancellationToken.None);

            var afterRestart = repository.ListRecent(limit: 10, offset: 0);
            var recovered = afterRestart.Single(session => session.Id == cancelledSessionId.ToString("N"));
            Assert.Equal("ready", recovered.RecordingStatus);
            Assert.NotNull(recovered.PrimaryAudioPath);
            Assert.True(File.Exists(recovered.PrimaryAudioPath));
            var permanent = afterRestart.Single(session => session.Id == permanentSessionId.ToString("N"));
            Assert.Equal("attention_required", permanent.RecordingStatus);
            Assert.Equal("mp3_encoder_unavailable", permanent.ArtifactErrorCode);
            Assert.True(File.Exists(permanentSourcePath));
            Assert.Equal(1, successfulEncoder.EncodeCount);
            await secondCoordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultDeviceChangeCreatesANewTimelineLegInsideTheSameSession()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            var sessionId = coordinator.ActiveSessionId;
            audioPlatform.SwitchDefaultDevices("output-2", "mic-2");
            await WaitUntilAsync(
                () => audioPlatform.Requests.Count >= 2,
                TimeSpan.FromSeconds(3));

            Assert.Equal(sessionId, audioPlatform.Requests[1].SessionId);
            Assert.Contains(audioPlatform.Requests[1].Sources, static source =>
                source.Kind == AudioCaptureSourceKind.DeviceLoopback
                && source.DeviceId == "output-2");
            Assert.Contains(audioPlatform.Requests[1].Sources, static source =>
                source.Kind == AudioCaptureSourceKind.Microphone
                && source.DeviceId == "mic-2");
            Assert.Contains(
                Path.Combine("legs", "0001"),
                audioPlatform.Requests[1].TempSessionDirectoryPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                audioPlatform.Requests[1].TempSessionDirectoryPath,
                RecordingSourceManifestStore.ContinuityLegCheckpointFileName)));

            var result = await coordinator.FinishAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.IsReady);
            Assert.True(File.Exists(result.PrimaryAudioPath));
            var persisted = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", persisted.RecordingStatus);
            Assert.Equal("not_started", persisted.TranscriptionStatus);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    /// </summary>
    [Fact]
    public async Task RecoveryDiscoversClosedContinuityLegMissingFromPreviousManifest()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var pathResolver = new ArtifactPathResolver(paths);
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                pathResolver,
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            var sessionId = Guid.NewGuid();
            var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            var tempSessionPath = pathResolver.GetTempSessionDirectoryPath(settings, sessionId);
            var firstLegPath = Path.Combine(tempSessionPath, "output.wav");
            WriteTone(firstLegPath, 440);
            var firstLeg = new AudioCaptureArtifactSnapshot(
                AudioCaptureArtifactKind.Output,
                firstLegPath,
                new FileInfo(firstLegPath).Length,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero);
            var oldManifestPath = await RecordingSourceManifestStore.WriteAsync(
                tempSessionPath,
                sessionId,
                [firstLeg],
                CancellationToken.None);
            var secondLegDirectory = Path.Combine(tempSessionPath, "legs", "0001");
            await RecordingSourceManifestStore.WriteContinuityLegCheckpointAsync(
                secondLegDirectory,
                sessionId,
                legIndex: 1,
                relativeStartOffset: TimeSpan.FromSeconds(1.25),
                [AudioCaptureArtifactKind.Output],
                CancellationToken.None);
            var secondLegPath = Path.Combine(secondLegDirectory, "output.wav");
            WriteTone(secondLegPath, 660);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    sessionId,
                    createdAtUtc,
                    "manual",
                    "device_loopback",
                    "output",
                    null) with
                {
                    Status = "recording",
                    SourceApp = "Ручная запись",
                    TempSessionPath = tempSessionPath,
                    SourceManifestPath = oldManifestPath,
                    StagedPrimaryPath = pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        sessionId,
                        createdAtUtc,
                        "Ручная запись"),
                    AudioOutputPath = firstLegPath
                },
                CancellationToken.None);

            await coordinator.RecoverPendingAsync(CancellationToken.None);

            var recovered = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", recovered.RecordingStatus);
            Assert.True(recovered.DurationSeconds >= 2.2);
            Assert.NotNull(recovered.PrimaryAudioPath);
            Assert.True(File.Exists(recovered.PrimaryAudioPath));
            Assert.False(Directory.Exists(tempSessionPath));
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeviceLossRebindKeepsHealthyMicrophoneLegAndFinalizesSession()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            audioPlatform.RejectRequest = request => request.Sources.Any(static source =>
                source.Kind == AudioCaptureSourceKind.DeviceLoopback);

            audioPlatform.Sessions[0].RaiseFailure(AudioCaptureSourceKind.DeviceLoopback);
            await WaitUntilAsync(() => audioPlatform.Sessions.Count >= 2, TimeSpan.FromSeconds(3));

            var fallbackRequest = audioPlatform.Requests.Last();
            var fallbackSource = Assert.Single(fallbackRequest.Sources);
            Assert.Equal(AudioCaptureSourceKind.Microphone, fallbackSource.Kind);
            var result = await coordinator.FinishAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.IsReady);
            Assert.True(File.Exists(result.PrimaryAudioPath));
            var persisted = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", persisted.RecordingStatus);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LockedOrphanKeepsCleanupCheckpointUntilNextRecoveryPass()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            var tempSessionPath = audioPlatform.Requests[0].TempSessionDirectoryPath;
            Directory.CreateDirectory(tempSessionPath);
            var orphanPath = Path.Combine(tempSessionPath, "orphan.partial");

            await using (var lockedOrphan = new FileStream(
                             orphanPath,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.None))
            {
                await lockedOrphan.WriteAsync(new byte[] { 1, 2, 3, 4 });
                var result = await coordinator.FinishAsync(CancellationToken.None);

                Assert.NotNull(result);
                Assert.True(result.IsReady);
                var pending = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
                Assert.True(pending.SourceCleanupPending);
                Assert.Equal(tempSessionPath, pending.TempSessionPath);
                Assert.True(File.Exists(orphanPath));
            }

            await coordinator.RecoverPendingAsync(CancellationToken.None);

            var cleaned = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.False(cleaned.SourceCleanupPending);
            Assert.Null(cleaned.TempSessionPath);
            Assert.False(Directory.Exists(tempSessionPath));
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    /// </summary>
    [Fact]
    public async Task RecoveryCleanupDeletesTrackedFilesWithoutRecursivelyDeletingUntrustedDirectory()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            var sessionId = Guid.NewGuid();
            var createdAtUtc = DateTimeOffset.UtcNow;
            var untrustedDirectory = Path.Combine(root, "user-owned-directory");
            var trackedSourcePath = Path.Combine(untrustedDirectory, "tracked-source.wav");
            var sentinelPath = Path.Combine(untrustedDirectory, "keep-me.txt");
            WriteTone(trackedSourcePath, 440);
            await File.WriteAllTextAsync(sentinelPath, "user data");
            var primaryPath = Path.Combine(root, "recordings", "ready.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            await File.WriteAllBytesAsync(primaryPath, FakeEncoder.EncodedBytes);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    sessionId,
                    createdAtUtc,
                    "manual",
                    "device_loopback",
                    "output",
                    null) with
                {
                    Status = "ready",
                    SourceApp = "Ручная запись",
                    AudioOutputPath = trackedSourcePath,
                    PrimaryAudioPath = primaryPath,
                    TempSessionPath = untrustedDirectory,
                    SourceCleanupPending = true
                },
                CancellationToken.None);

            await coordinator.RecoverPendingAsync(CancellationToken.None);

            Assert.False(File.Exists(trackedSourcePath));
            Assert.True(File.Exists(sentinelPath));
            Assert.True(Directory.Exists(untrustedDirectory));
            var pending = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.True(pending.SourceCleanupPending);
            Assert.Equal(untrustedDirectory, pending.TempSessionPath);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledPostStartHandoffStopsAndDisposesUnownedCapture()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            using var cancellation = new CancellationTokenSource();
            audioPlatform.AfterCaptureStarted = cancellation.Cancel;
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                coordinator.StartManualAsync(cancellation.Token).AsTask());

            var capture = Assert.Single(audioPlatform.Sessions);
            Assert.True(capture.WasStopped);
            Assert.True(capture.WasDisposed);
            Assert.False(coordinator.IsBusy);
            var session = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("attention_required", session.RecordingStatus);
            Assert.Equal("capture_handoff_failed", session.ArtifactErrorCode);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalCaptureSnapshotDuringOwnershipHandoffStillFinalizes()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform
            {
                CompleteCaptureBeforeReturn = true
            };
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.Finalization?.Stage == RecordingArtifactStage.Ready)
                {
                    ready.TrySetResult();
                }
            };

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var capture = Assert.Single(audioPlatform.Sessions);
            Assert.True(capture.WasStopped);
            Assert.True(capture.WasDisposed);
            var persisted = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", persisted.RecordingStatus);
            Assert.NotNull(persisted.PrimaryAudioPath);
            Assert.True(File.Exists(persisted.PrimaryAudioPath));
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ThrowingSnapshotObserverCannotAbortReadyArtifactFinalization()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var coordinator = new WindowsRecordingSessionCoordinator(
                new FakeAudioPlatform(),
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(new FakeEncoder(), logger),
                logger);
            coordinator.SnapshotChanged += (_, _) =>
                throw new InvalidOperationException("Injected recording observer failure.");

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            var result = await coordinator.FinishAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.IsReady);
            Assert.True(File.Exists(result.PrimaryAudioPath));
            var persisted = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", persisted.RecordingStatus);
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeWaitsForInFlightFinalizationBeforeReleasingCoordinatorResources()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => ApplicationSettings.Default,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(
                    new FakeEncoder(TimeSpan.FromMilliseconds(300)),
                    logger),
                logger);
            coordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.Finalization?.Stage == RecordingArtifactStage.Processing)
                {
                    processing.TrySetResult();
                }
            };
            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            var finishTask = coordinator.FinishAsync(CancellationToken.None).AsTask();
            await processing.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var disposeTask = coordinator.DisposeAsync().AsTask();
            Assert.False(disposeTask.IsCompleted);
            await Task.WhenAll(finishTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5));

            var finishResult = await finishTask;
            Assert.True(finishResult?.IsReady == true);
            var session = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("ready", session.RecordingStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    [Fact]
    public async Task DiscardAsync_CheckpointsStopsAndDeletesOnlyTheActiveTempSession()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var audioPlatform = new FakeAudioPlatform();
            var encoder = new FakeEncoder();
            var logger = new BootstrapFileLogger(paths.HostLogFilePath);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "recordings")
                }
            };
            var coordinator = new WindowsRecordingSessionCoordinator(
                audioPlatform,
                repository,
                new ArtifactPathResolver(paths),
                () => settings,
                new IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder(),
                new WindowsRecordingArtifactFinalizer(encoder, logger),
                logger);

            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            var activeSessionId = Assert.IsType<Guid>(coordinator.ActiveSessionId);
            var tempSessionPath = new ArtifactPathResolver(paths)
                .GetTempSessionDirectoryPath(settings, activeSessionId);
            Assert.True(Directory.Exists(tempSessionPath));

            Assert.True(await coordinator.DiscardAsync(CancellationToken.None));

            Assert.Null(coordinator.ActiveSessionId);
            Assert.False(Directory.Exists(tempSessionPath));
            Assert.Equal(0, encoder.EncodeCount);
            var discarded = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("discarded", discarded.RecordingStatus);
            Assert.Null(discarded.PrimaryAudioPath);
            Assert.Null(discarded.TempSessionPath);
            Assert.Null(discarded.AudioOutputPath);
            Assert.Null(discarded.AudioMicPath);
            Assert.True(audioPlatform.Sessions[0].WasStopped);
            Assert.True(audioPlatform.Sessions[0].WasDisposed);

            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public void RecordingArtifactDeletionService_RejectsDatabasePathOutsideOwnedRoots()
    {
        var root = CreateRoot();
        var outsideRoot = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var recordingsRoot = Path.Combine(root, "recordings");
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = recordingsRoot
                }
            };
            var outsideFile = Path.Combine(outsideRoot, "unrelated.wav");
            File.WriteAllBytes(outsideFile, [1, 2, 3]);
            var service = new RecordingArtifactDeletionService(
                paths,
                new ArtifactPathResolver(paths),
                () => settings);
            var workItem = new RecentRecordingRemovalWorkItem(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                "ready",
                AudioOutputPath: outsideFile,
                AudioMicPath: null,
                AudioMixPath: null,
                PrimaryAudioPath: null,
                TempSessionPath: null,
                SourceManifestPath: null,
                StagedPrimaryPath: null);

            Assert.Throws<InvalidOperationException>(() => service.DeleteRecentArtifacts(workItem));
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public void RecordingArtifactDeletionService_AllowsPersistedSessionAfterFolderSettingChanges()
    {
        var root = CreateRoot();
        var formerRecordingsRoot = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = Path.Combine(root, "current-recordings")
                }
            };
            var sessionId = Guid.NewGuid();
            var formerSessionDirectory = Path.Combine(
                formerRecordingsRoot,
                sessionId.ToString("N"));
            var recordingPath = Path.Combine(formerSessionDirectory, "meeting.m4a");
            Directory.CreateDirectory(formerSessionDirectory);
            File.WriteAllBytes(recordingPath, [1, 2, 3]);
            var service = new RecordingArtifactDeletionService(
                paths,
                new ArtifactPathResolver(paths),
                () => settings);

            service.DeleteRecentArtifacts(new RecentRecordingRemovalWorkItem(
                sessionId.ToString("N"),
                DateTimeOffset.UtcNow,
                "ready",
                AudioOutputPath: null,
                AudioMicPath: null,
                AudioMixPath: null,
                PrimaryAudioPath: recordingPath,
                TempSessionPath: null,
                SourceManifestPath: null,
                StagedPrimaryPath: null));

            Assert.False(File.Exists(recordingPath));
            Assert.False(Directory.Exists(formerSessionDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(formerRecordingsRoot))
            {
                Directory.Delete(formerRecordingsRoot, recursive: true);
            }
        }
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    [Fact]
    public void RecordingArtifactDeletionServiceLeavesLegacyTranscriptsInSharedSessionDirectory()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var recordingsRoot = Path.Combine(root, "recordings");
            var settings = ApplicationSettings.Default with
            {
                ReleaseV2 = ReleaseV2Settings.Default with
                {
                    RecordingsFolder = recordingsRoot
                }
            };
            var sessionId = Guid.NewGuid();
            var sessionDirectory = Path.Combine(recordingsRoot, sessionId.ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            var audioPath = Path.Combine(sessionDirectory, "meeting.m4a");
            var markdownPath = Path.Combine(sessionDirectory, "meeting.md");
            var jsonPath = Path.Combine(sessionDirectory, "meeting.json");
            File.WriteAllBytes(audioPath, [1, 2, 3]);
            File.WriteAllText(markdownPath, "# Legacy transcript");
            File.WriteAllText(jsonPath, "{\"text\":\"Legacy transcript\"}");
            var service = new RecordingArtifactDeletionService(
                paths,
                new ArtifactPathResolver(paths),
                () => settings);

            service.DeleteRecentArtifacts(new RecentRecordingRemovalWorkItem(
                sessionId.ToString("N"),
                DateTimeOffset.UtcNow,
                "ready",
                AudioOutputPath: null,
                AudioMicPath: null,
                AudioMixPath: null,
                PrimaryAudioPath: audioPath,
                TempSessionPath: null,
                SourceManifestPath: null,
                StagedPrimaryPath: null));

            Assert.False(File.Exists(audioPath));
            Assert.True(File.Exists(markdownPath));
            Assert.True(File.Exists(jsonPath));
            Assert.True(Directory.Exists(sessionDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("The expected recording state was not observed.");
    }

    private static void WriteTone(string path, double frequency)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = new WaveFormat(48_000, 16, 2);
        using var writer = new WaveFileWriter(path, format);
        for (var frame = 0; frame < 48_000; frame++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequency * frame / format.SampleRate) * 8_000);
            writer.WriteByte((byte)sample);
            writer.WriteByte((byte)(sample >> 8));
            writer.WriteByte((byte)sample);
            writer.WriteByte((byte)(sample >> 8));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class FakeAudioPlatform : IAudioPlatform
    {
        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

        public AudioPlatformSnapshot Snapshot { get; private set; } = new(
            DateTimeOffset.UtcNow,
            new AudioPlatformCapabilities(true, true, "fixture"),
            [new AudioEndpointSnapshot("output", "Output", true, true)],
            [new AudioEndpointSnapshot("mic", "Microphone", true, true)],
            [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 })],
            [new AudioSignalSnapshot(
                DateTimeOffset.UtcNow,
                "zoom.exe",
                42,
                "active",
                -20,
                "output",
                true,
                true)]);

        public List<AudioCaptureRequest> Requests { get; } = [];

        public List<FakeCaptureSession> Sessions { get; } = [];

        public Action? AfterCaptureStarted { get; set; }

        public bool CompleteCaptureBeforeReturn { get; set; }

        public Func<AudioCaptureRequest, bool>? RejectRequest { get; set; }

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken)
        {
            SnapshotChanged?.Invoke(this, Snapshot);
            return ValueTask.CompletedTask;
        }

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
        }

        public void SwitchDefaultDevices(string outputDeviceId, string microphoneDeviceId)
        {
            Snapshot = Snapshot with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                OutputDevices =
                [
                    new AudioEndpointSnapshot("output", "Output", false, true),
                    new AudioEndpointSnapshot(outputDeviceId, "New output", true, true)
                ],
                Microphones =
                [
                    new AudioEndpointSnapshot("mic", "Microphone", false, true),
                    new AudioEndpointSnapshot(microphoneDeviceId, "New microphone", true, true)
                ]
            };
            SnapshotChanged?.Invoke(this, Snapshot);
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (RejectRequest?.Invoke(request) == true)
            {
                throw new IOException("Injected capture source failure.");
            }

            var session = new FakeCaptureSession(request);
            Sessions.Add(session);
            if (CompleteCaptureBeforeReturn)
            {
                session.CompleteNaturally(raiseEvent: false);
            }

            AfterCaptureStarted?.Invoke();
            return ValueTask.FromResult<IAudioCaptureSession>(session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCaptureSession(AudioCaptureRequest request) : IAudioCaptureSession
    {
        private bool _stopped;

        public bool WasStopped => _stopped;

        public bool WasDisposed { get; private set; }

        public event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged;

        public AudioCaptureSessionSnapshot Snapshot { get; private set; } = new(
            request.SessionId,
            AudioCaptureState.Running,
            true,
            [],
            []);

        public ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

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
                var isMicrophone = source.Kind == AudioCaptureSourceKind.Microphone;
                var path = Path.Combine(
                    request.TempSessionDirectoryPath,
                    isMicrophone ? "mic.wav" : "output.wav");
                WindowsRecordingSessionCoordinatorTests.WriteTone(path, isMicrophone ? 880 : 440);
                artifacts.Add(new AudioCaptureArtifactSnapshot(
                    isMicrophone ? AudioCaptureArtifactKind.Microphone : AudioCaptureArtifactKind.Output,
                    path,
                    new FileInfo(path).Length,
                    DateTimeOffset.UtcNow,
                    isMicrophone ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero));
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

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            WasDisposed = true;
        }

        public void RaiseFailure(AudioCaptureSourceKind sourceKind)
        {
            var failure = new AudioCaptureFailureSnapshot(
                DateTimeOffset.UtcNow,
                "DeviceLost",
                sourceKind,
                "device_lost",
                "Injected device loss.",
                ArtifactPath: null);
            Snapshot = Snapshot with
            {
                Failures = Snapshot.Failures.Append(failure).ToArray()
            };
            SnapshotChanged?.Invoke(this, Snapshot);
        }

    }

    private sealed class FakeEncoder(TimeSpan? encodeDelay = null) : IAudioArtifactEncoder
    {
        public static readonly byte[] EncodedBytes = [0x66, 0x74, 0x79, 0x70, 1, 2, 3, 4];
        private TimeSpan _encodedDuration = TimeSpan.FromSeconds(1);

        public int EncodeCount { get; private set; }

        public AudioArtifactEncoderAvailability ProbeAvailability() => new(
            true,
            "fixture",
            48_000,
            2,
            16,
            128_000,
            null,
            null);

        public async Task<AudioArtifactEncodeResult> EncodeAsync(
            AudioArtifactEncodeRequest request,
            IProgress<AudioArtifactEncodingProgress>? progress,
            CancellationToken cancellationToken)
        {
            EncodeCount++;
            progress?.Report(new AudioArtifactEncodingProgress(1, 2, 0.5));
            if (encodeDelay is { Ticks: > 0 })
            {
                await Task.Delay(encodeDelay.Value, cancellationToken);
            }

            using (var sourceReader = new WaveFileReader(request.SourceWavePath))
            {
                _encodedDuration = sourceReader.TotalTime;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.PartialOutputPath)!);
            await File.WriteAllBytesAsync(request.PartialOutputPath, EncodedBytes, cancellationToken);
            return new AudioArtifactEncodeResult(
                true,
                request.PartialOutputPath,
                EncodedBytes.Length,
                _encodedDuration,
                48_000,
                2,
                16,
                128_000,
                null,
                null);
        }

        public AudioArtifactReadabilityProbe ProbeReadability(string artifactPath)
        {
            var readable = File.Exists(artifactPath)
                           && File.ReadAllBytes(artifactPath).SequenceEqual(EncodedBytes);
            return new AudioArtifactReadabilityProbe(
                readable,
                readable ? EncodedBytes.Length : 0,
                readable ? _encodedDuration : null,
                readable ? 48_000 : null,
                readable ? 2 : null,
                readable ? 16 : null,
                readable ? null : "unreadable",
                readable ? null : "fixture unreadable");
        }
    }

    private sealed class CancellationEncoder : IAudioArtifactEncoder
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public AudioArtifactEncoderAvailability ProbeAvailability() => new(
            true,
            "fixture-cancellation",
            48_000,
            2,
            16,
            128_000,
            null,
            null);

        public async Task<AudioArtifactEncodeResult> EncodeAsync(
            AudioArtifactEncodeRequest request,
            IProgress<AudioArtifactEncodingProgress>? progress,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Infinite cancellation fixture completed unexpectedly.");
        }

        public AudioArtifactReadabilityProbe ProbeReadability(string artifactPath) => new(
            false,
            0,
            null,
            null,
            null,
            null,
            "fixture_unreadable",
            "Cancellation fixture never produces an artifact.");
    }
}
