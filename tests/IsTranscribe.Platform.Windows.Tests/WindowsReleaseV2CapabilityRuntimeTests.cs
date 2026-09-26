using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows.Audio.Recording;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsReleaseV2CapabilityRuntimeTests
{
    [Fact]
    public async Task RecordingFallbackProjectsTypedMicrophoneIssueWithoutHidingControls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("recording-fallback");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);
            var sessionId = Guid.NewGuid();

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.Recording,
                new ActiveMeetingSnapshot(
                    sessionId,
                    "Zoom",
                    DateTimeOffset.UtcNow,
                    HasOutput: true,
                    HasMicrophone: false,
                    IsPaused: false),
                Finalization: null,
                AttentionMessage: null,
                RefreshRecentRecordings: false,
                CapabilityIssue: RuntimeCapabilityIssue.MicrophoneCaptureUnavailable));

            Assert.Equal(ApplicationActivityState.Recording, runtime.Snapshot.Activity);
            Assert.False(runtime.Snapshot.ActiveMeeting?.HasMicrophone);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                runtime.Snapshot.Capability.Issue);

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.Processing,
                ActiveMeeting: null,
                new RecordingFinalizationSnapshot(sessionId, RecordingArtifactStage.Processing),
                AttentionMessage: null,
                RefreshRecentRecordings: false));

            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                runtime.Snapshot.Capability.Issue);
            await runtime.SetServiceEnabledAsync(false, CancellationToken.None);
            Assert.Equal(ApplicationActivityState.Paused, runtime.Snapshot.Activity);
            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);

            await runtime.SetServiceEnabledAsync(true, CancellationToken.None);
            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                runtime.Snapshot.Capability.Issue);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);

            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.Recording,
                new ActiveMeetingSnapshot(
                    Guid.NewGuid(),
                    "Zoom",
                    DateTimeOffset.UtcNow,
                    HasOutput: true,
                    HasMicrophone: false,
                    IsPaused: false),
                Finalization: null,
                AttentionMessage: null,
                RefreshRecentRecordings: false,
                CapabilityIssue: RuntimeCapabilityIssue.MicrophoneCaptureUnavailable));
            runtime.ApplyRecordingSnapshot(new WindowsRecordingCoordinatorSnapshot(
                ApplicationActivityState.Recording,
                new ActiveMeetingSnapshot(
                    Guid.NewGuid(),
                    "Zoom",
                    DateTimeOffset.UtcNow,
                    HasOutput: true,
                    HasMicrophone: true,
                    IsPaused: false),
                Finalization: null,
                AttentionMessage: null,
                RefreshRecentRecordings: false));

            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedInitializationCleansUpAndCanBeRetriedWithoutDuplicateObservers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("initialization-retry");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true))
        {
            FailNextStart = true
        };
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateRuntime(root, audio);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime
                .InitializeAsync(CancellationToken.None)
                .AsTask());

            Assert.Equal(1, audio.StartCount);
            Assert.Equal(0, audio.SubscriberCount);
            Assert.Equal(ApplicationRuntimeSnapshot.Initial, runtime.Snapshot);

            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(2, audio.StartCount);
            Assert.Equal(2, audio.SubscriberCount);
            await runtime.InitializeAsync(CancellationToken.None);
            Assert.Equal(2, audio.StartCount);
            Assert.Equal(2, audio.SubscriberCount);
            var publicationCount = 0;
            runtime.SnapshotChanged += (_, _) => publicationCount++;
            audio.PublishCapability(CreateCapability(true, false));
            Assert.Equal(1, publicationCount);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);

            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            runtime = null;
            Assert.Equal(0, audio.SubscriberCount);
            Assert.Equal(1, audio.DisposeCount);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsAudioPlatformRefreshReassessesAndPublishesCapabilities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("windows-audio");
        var assessor = new MutableCapabilityAssessor
        {
            Current = new HostCapabilitySnapshot(
                HostCapabilityState.Degraded,
                ProcessLoopbackAvailable: false,
                Summary: "Device capture is available.")
        };
        try
        {
            await using var platform = new WindowsAudioPlatform(
                new BootstrapFileLogger(Path.Combine(root, "host.jsonl")),
                assessor);
            var published = new List<AudioPlatformSnapshot>();
            platform.SnapshotChanged += (_, snapshot) => published.Add(snapshot);

            await platform.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Equal(1, assessor.AssessmentCount);
            Assert.True(platform.Snapshot.Capabilities.IsSupported);
            Assert.False(platform.Snapshot.Capabilities.SupportsProcessOutputCapture);

            assessor.Current = new HostCapabilitySnapshot(
                HostCapabilityState.Blocked,
                ProcessLoopbackAvailable: false,
                Summary: "Audio capture is unavailable.",
                BlockingReason: "A supported Windows version is required.");
            await platform.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Equal(2, assessor.AssessmentCount);
            Assert.False(platform.Snapshot.Capabilities.IsSupported);
            Assert.Equal(
                "A supported Windows version is required.",
                platform.Snapshot.Capabilities.BlockingReason);
            Assert.Equal(2, published.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true, RuntimeCapabilityState.Full, ApplicationActivityState.Paused)]
    [InlineData(true, false, RuntimeCapabilityState.Degraded, ApplicationActivityState.Paused)]
    [InlineData(false, false, RuntimeCapabilityState.Blocked, ApplicationActivityState.Paused)]
    public async Task InitializeMapsPlatformCapabilityIntoRuntimeSnapshot(
        bool isSupported,
        bool supportsProcessOutput,
        RuntimeCapabilityState expectedCapability,
        ApplicationActivityState expectedActivity)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("initial");
        var capability = CreateCapability(isSupported, supportsProcessOutput);
        var audio = new RefreshableAudioPlatform(capability);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateRuntime(root, audio);

            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(expectedCapability, runtime.Snapshot.Capability.State);
            Assert.Equal(supportsProcessOutput, runtime.Snapshot.Capability.SupportsProcessOutputCapture);
            Assert.Equal(capability.Summary, runtime.Snapshot.Capability.Summary);
            Assert.Equal(expectedActivity, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.AttentionMessage);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, true, true, ApplicationActivityState.Listening)]
    [InlineData(true, true, false, false, ApplicationActivityState.AttentionRequired)]
    public async Task RefreshPublishesAndRecomputesIdleActivity(
        bool initialSupported,
        bool initialProcessOutput,
        bool refreshedSupported,
        bool refreshedProcessOutput,
        ApplicationActivityState expectedActivity)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("refresh");
        var audio = new RefreshableAudioPlatform(
            CreateCapability(initialSupported, initialProcessOutput));
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);
            var published = new List<ApplicationRuntimeSnapshot>();
            runtime.SnapshotChanged += (_, snapshot) => published.Add(snapshot);
            audio.NextCapability = CreateCapability(refreshedSupported, refreshedProcessOutput);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None);

            var expectedCapability = refreshedSupported
                ? refreshedProcessOutput
                    ? RuntimeCapabilityState.Full
                    : RuntimeCapabilityState.Degraded
                : RuntimeCapabilityState.Blocked;
            Assert.Equal(1, audio.RefreshCount);
            Assert.Equal(expectedCapability, runtime.Snapshot.Capability.State);
            Assert.Equal(expectedActivity, runtime.Snapshot.Activity);
            Assert.Contains(
                published,
                snapshot => snapshot.Capability.State == expectedCapability
                    && snapshot.Activity == expectedActivity);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CapabilityOnlyAudioSnapshotChangeRecomputesIdleState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("audio-event");
        var audio = new RefreshableAudioPlatform(CreateCapability(false, false));
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);
            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);

            audio.PublishCapability(CreateCapability(true, true));

            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(ApplicationActivityState.Listening, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.AttentionMessage);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeCapabilityExposesActiveEndpointReadiness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("endpoints");
        var audio = new RefreshableAudioPlatform(
            CreateCapability(true, true),
            [new AudioEndpointSnapshot("output", "Speakers", true, true)],
            [new AudioEndpointSnapshot("mic", "Microphone", true, false)]);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.True(runtime.Snapshot.Capability.HasActiveOutput);
            Assert.False(runtime.Snapshot.Capability.HasActiveMicrophone);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.NoActiveMicrophone,
                runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingExpectedMicrophoneRequiresAttentionAfterOnboarding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("missing-microphone");
        var audio = new RefreshableAudioPlatform(
            CreateCapability(true, true),
            [new AudioEndpointSnapshot("output", "Speakers", true, true)],
            []);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.NoActiveMicrophone,
                runtime.Snapshot.Capability.Issue);
            Assert.True(runtime.Snapshot.Capability.HasActiveOutput);
            Assert.False(runtime.Snapshot.Capability.HasActiveMicrophone);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingConfiguredMicrophoneDoesNotClaimFullCapability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("configured-microphone");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(
                root,
                onboardingCompleted: true,
                microphoneDeviceId: "missing-selected-mic",
                followSystemDefaultMicrophone: false);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
                runtime.Snapshot.Capability.Issue);
            Assert.True(runtime.Snapshot.Capability.HasActiveMicrophone);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DetectionMicrophoneHealthPublishesIssueAndClearsAfterRetrySucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("detection-microphone-health");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var speech = new MutableSpeechActivityProvider
        {
            MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Unavailable
        };
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = new WindowsReleaseV2Runtime(
                root,
                audio,
                MeetingDetectionMode.Live,
                (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
                    platform,
                    new EligibleBrowserMeetingWindowEvidenceProvider(),
                    speech,
                    profiles,
                    preferences,
                    new MeetingDetectionEngine(mode: mode),
                    new MeetingDetectionCoordinatorOptions(
                        ObservationCadence: TimeSpan.FromMilliseconds(25),
                        WindowEvidenceCadence: TimeSpan.FromSeconds(1),
                        PromptTimeout: TimeSpan.FromSeconds(12))),
                new TestAutostartService());
            await runtime.InitializeAsync(CancellationToken.None);

            await WaitForAsync(
                () => runtime.Snapshot.Activity == ApplicationActivityState.AttentionRequired
                    && runtime.Snapshot.Capability.Issue
                    == RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                TimeSpan.FromSeconds(3));

            await runtime.SetServiceEnabledAsync(false, CancellationToken.None);
            Assert.Equal(ApplicationActivityState.Paused, runtime.Snapshot.Activity);
            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);

            speech.MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Available;
            await runtime.SetServiceEnabledAsync(true, CancellationToken.None);
            await WaitForAsync(
                () => runtime.Snapshot.Activity == ApplicationActivityState.Listening
                    && runtime.Snapshot.Capability.State == RuntimeCapabilityState.Full
                    && runtime.Snapshot.Capability.Issue == RuntimeCapabilityIssue.None,
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshBeforeOnboardingPublishesUnavailableMicrophoneHealthBeforeReturning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("onboarding-microphone-unavailable");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var speech = new ControlledSpeechActivityProvider(MeetingSpeechCaptureHealth.Unavailable);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateProbeRuntime(root, audio, () => speech, TimeSpan.FromSeconds(1));
            await runtime.InitializeAsync(CancellationToken.None);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Equal(1, speech.ObserveCount);
            Assert.Equal(1, speech.DisposeCount);
            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.Equal(ApplicationActivityState.Paused, runtime.Snapshot.Activity);
            Assert.Equal(RuntimeCapabilityState.Degraded, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshBeforeOnboardingWaitsForAvailableMicrophoneHealth()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("onboarding-microphone-available");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var speech = new ControlledSpeechActivityProvider();
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateProbeRuntime(root, audio, () => speech, TimeSpan.FromSeconds(1));
            await runtime.InitializeAsync(CancellationToken.None);

            var refreshTask = runtime.RefreshCapabilitiesAsync(CancellationToken.None).AsTask();
            await speech.ObservationStarted.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(refreshTask.IsCompleted);

            speech.Complete(MeetingSpeechCaptureHealth.Available);
            await refreshTask;

            Assert.Equal(1, speech.DisposeCount);
            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TimedOutOnboardingMicrophoneHealthRefreshFailsOpenAndCanRetryCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("onboarding-microphone-timeout");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var timedOutSpeech = new ControlledSpeechActivityProvider();
        var retrySpeech = new ControlledSpeechActivityProvider(MeetingSpeechCaptureHealth.Available);
        var providers = new Queue<ControlledSpeechActivityProvider>([timedOutSpeech, retrySpeech]);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateProbeRuntime(
                root,
                audio,
                () => providers.Dequeue(),
                TimeSpan.FromMilliseconds(50));
            await runtime.InitializeAsync(CancellationToken.None);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
            await timedOutSpeech.Disposed.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.Equal(RuntimeCapabilityState.Full, runtime.Snapshot.Capability.State);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Empty(providers);
            Assert.Equal(1, timedOutSpeech.ObserveCount);
            Assert.Equal(1, timedOutSpeech.DisposeCount);
            Assert.Equal(1, retrySpeech.ObserveCount);
            Assert.Equal(1, retrySpeech.DisposeCount);
            Assert.Equal(RuntimeCapabilityIssue.None, runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledOnboardingMicrophoneHealthRefreshPropagatesAndCanRetryCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("onboarding-microphone-cancel");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var canceledSpeech = new ControlledSpeechActivityProvider();
        var retrySpeech = new ControlledSpeechActivityProvider(MeetingSpeechCaptureHealth.Unavailable);
        var providers = new Queue<ControlledSpeechActivityProvider>([canceledSpeech, retrySpeech]);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: false);
            runtime = CreateProbeRuntime(
                root,
                audio,
                () => providers.Dequeue(),
                TimeSpan.FromSeconds(5));
            await runtime.InitializeAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();

            var refreshTask = runtime.RefreshCapabilitiesAsync(cancellation.Token).AsTask();
            await canceledSpeech.ObservationStarted.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshTask);
            await canceledSpeech.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None);

            Assert.Empty(providers);
            Assert.Equal(1, canceledSpeech.DisposeCount);
            Assert.Equal(1, retrySpeech.DisposeCount);
            Assert.Equal(
                RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                runtime.Snapshot.Capability.Issue);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task RefreshBeforeOnboardingSkipsMicrophoneObservationWhenItCannotHelp(
        bool serviceEnabled,
        bool hasOutput,
        bool hasMicrophone,
        bool platformSupported)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("onboarding-microphone-skip");
        var audio = new RefreshableAudioPlatform(
            CreateCapability(platformSupported, platformSupported),
            hasOutput ? [new AudioEndpointSnapshot("output", "Speakers", true, true)] : [],
            hasMicrophone ? [new AudioEndpointSnapshot("mic", "Microphone", true, true)] : []);
        var coordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(
                root,
                onboardingCompleted: false,
                serviceEnabled: serviceEnabled);
            runtime = CreateProbeRuntime(
                root,
                audio,
                () =>
                {
                    Interlocked.Increment(ref coordinatorCount);
                    return new ControlledSpeechActivityProvider();
                },
                TimeSpan.FromSeconds(5));
            await runtime.InitializeAsync(CancellationToken.None);

            await runtime.RefreshCapabilitiesAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(0, Volatile.Read(ref coordinatorCount));
            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BlockedCapabilityKeepsDisabledServicePaused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("disabled");
        var audio = new RefreshableAudioPlatform(CreateCapability(false, false));
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(
                root,
                onboardingCompleted: true,
                serviceEnabled: false);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(RuntimeCapabilityState.Blocked, runtime.Snapshot.Capability.State);
            Assert.Equal(ApplicationActivityState.Paused, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.AttentionMessage);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAllActiveEndpointsBlocksEnabledService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("no-endpoints");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true), [], []);
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(RuntimeCapabilityState.Blocked, runtime.Snapshot.Capability.State);
            Assert.Equal(
                RuntimeCapabilityIssue.NoActiveAudioEndpoints,
                runtime.Snapshot.Capability.Issue);
            Assert.Equal(ApplicationActivityState.AttentionRequired, runtime.Snapshot.Activity);
            Assert.Null(runtime.Snapshot.AttentionMessage);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EndpointCapabilityTransitionsStopAndRestartDetectionLifecycle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("endpoint-lifecycle");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true), [], []);
        var coordinatorCount = 0;
        var disposedCoordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(
                root,
                audio,
                () => Interlocked.Increment(ref coordinatorCount),
                () => Interlocked.Increment(ref disposedCoordinatorCount));
            await runtime.InitializeAsync(CancellationToken.None);
            Assert.Equal(0, Volatile.Read(ref coordinatorCount));

            audio.PublishEndpoints(
                [new AudioEndpointSnapshot("output", "Speakers", true, true)],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)]);
            await WaitForAsync(
                () => Volatile.Read(ref coordinatorCount) == 1
                    && runtime.Snapshot.Activity == ApplicationActivityState.Listening,
                TimeSpan.FromSeconds(3),
                () => LifecycleDiagnostics(runtime, coordinatorCount, disposedCoordinatorCount));

            audio.PublishEndpoints([], []);
            await WaitForAsync(
                () => runtime.Snapshot.Activity == ApplicationActivityState.AttentionRequired
                    && Volatile.Read(ref disposedCoordinatorCount) == 1,
                TimeSpan.FromSeconds(3),
                () => LifecycleDiagnostics(runtime, coordinatorCount, disposedCoordinatorCount));

            audio.PublishEndpoints(
                [new AudioEndpointSnapshot("output", "Speakers", true, true)],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)]);
            await WaitForAsync(
                () => Volatile.Read(ref coordinatorCount) == 2
                    && runtime.Snapshot.Activity == ApplicationActivityState.Listening,
                TimeSpan.FromSeconds(3),
                () => LifecycleDiagnostics(runtime, coordinatorCount, disposedCoordinatorCount));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MatchingBlockedProjectionStillStopsAStaleDetectionLifecycle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("matching-blocked-projection");
        var audio = new RefreshableAudioPlatform(CreateCapability(true, true));
        var coordinatorCount = 0;
        var disposedCoordinatorCount = 0;
        WindowsReleaseV2Runtime? runtime = null;
        try
        {
            await WriteSettingsAsync(root, onboardingCompleted: true);
            runtime = CreateRuntime(
                root,
                audio,
                () => Interlocked.Increment(ref coordinatorCount),
                () => Interlocked.Increment(ref disposedCoordinatorCount));
            await runtime.InitializeAsync(CancellationToken.None);
            Assert.Equal(1, Volatile.Read(ref coordinatorCount));

            var blockedAudioCapability = CreateCapability(false, false);
            var blockedRuntimeCapability = new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Blocked,
                SupportsProcessOutputCapture: false,
                blockedAudioCapability.Summary,
                blockedAudioCapability.BlockingReason)
            {
                Issue = RuntimeCapabilityIssue.PlatformBlocked,
                HasActiveOutput = true,
                HasActiveMicrophone = true
            };
            var snapshotProperty = typeof(IsTranscribe.Application.ApplicationRuntime)
                .GetProperty(nameof(runtime.Snapshot));
            Assert.NotNull(snapshotProperty);
            snapshotProperty.SetValue(runtime, runtime.Snapshot with
            {
                Activity = ApplicationActivityState.Listening,
                Capability = blockedRuntimeCapability
            });

            audio.PublishCapability(blockedAudioCapability);

            await WaitForAsync(
                () => runtime.Snapshot.Activity == ApplicationActivityState.AttentionRequired
                    && runtime.Snapshot.Capability.State == RuntimeCapabilityState.Blocked
                    && Volatile.Read(ref disposedCoordinatorCount) == 1,
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static WindowsReleaseV2Runtime CreateRuntime(
        string root,
        IAudioPlatform audioPlatform,
        Action? onDetectionCoordinatorCreated = null,
        Action? onDetectionCoordinatorDisposed = null) => new(
        root,
        audioPlatform,
        MeetingDetectionMode.Live,
        (platform, profiles, preferences, mode) =>
        {
            onDetectionCoordinatorCreated?.Invoke();
            return CreateDetectionCoordinator(
                platform,
                profiles,
                preferences,
                mode,
                onDetectionCoordinatorDisposed);
        },
        new TestAutostartService());

    private static WindowsReleaseV2Runtime CreateProbeRuntime(
        string root,
        IAudioPlatform audioPlatform,
        Func<IMeetingSpeechActivityProvider> speechProviderFactory,
        TimeSpan timeout) => new(
        root,
        audioPlatform,
        MeetingDetectionMode.Live,
        (platform, profiles, preferences, mode) => new MeetingDetectionCoordinator(
            platform,
            new EmptyWindowEvidenceProvider(),
            speechProviderFactory(),
            profiles,
            preferences,
            new MeetingDetectionEngine(mode: mode)),
        new TestAutostartService(),
        onboardingMicrophoneHealthTimeout: timeout);

    private static MeetingDetectionCoordinator CreateDetectionCoordinator(
        IAudioPlatform platform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> preferences,
        MeetingDetectionMode mode,
        Action? onDisposed = null) => new(
        platform,
        new EmptyWindowEvidenceProvider(),
        new EmptySpeechActivityProvider(onDisposed),
        profiles,
        preferences,
        new MeetingDetectionEngine(mode: mode),
        new MeetingDetectionCoordinatorOptions(
            ObservationCadence: TimeSpan.FromMilliseconds(25),
            WindowEvidenceCadence: TimeSpan.FromSeconds(1),
            PromptTimeout: TimeSpan.FromSeconds(12)));

    private static AudioPlatformCapabilities CreateCapability(
        bool isSupported,
        bool supportsProcessOutput) => new(
        isSupported,
        supportsProcessOutput,
        isSupported
            ? supportsProcessOutput ? "Full capture is available." : "Device capture is available."
            : "Audio capture is unavailable.",
        isSupported ? null : "Windows audio capability is blocked.");

    private static string CreateRoot(string scenario)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-capability-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteSettingsAsync(
        string root,
        bool onboardingCompleted,
        bool serviceEnabled = true,
        string? microphoneDeviceId = null,
        bool followSystemDefaultMicrophone = true)
    {
        var store = new JsonApplicationSettingsStore(new LocalAppPaths("isTranscribe", root));
        await store.SaveAsync(
            ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with
                {
                    OnboardingCompleted = onboardingCompleted,
                    ServiceEnabled = serviceEnabled,
                    Autostart = false,
                    MicrophoneDeviceId = microphoneDeviceId,
                    FollowSystemDefaultMicrophone = followSystemDefaultMicrophone
                }),
            CancellationToken.None);
    }

    private static string LifecycleDiagnostics(
        WindowsReleaseV2Runtime runtime,
        int coordinatorCount,
        int disposedCoordinatorCount) =>
        $"created={Volatile.Read(ref coordinatorCount)}, "
        + $"disposed={Volatile.Read(ref disposedCoordinatorCount)}, "
        + $"activity={runtime.Snapshot.Activity}, "
        + $"capability={runtime.Snapshot.Capability.State}, "
        + $"issue={runtime.Snapshot.Capability.Issue}";

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string>? diagnostics = null)
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

        var details = diagnostics?.Invoke();
        throw new TimeoutException(string.IsNullOrWhiteSpace(details)
            ? "The runtime did not reach the expected state."
            : $"The runtime did not reach the expected state: {details}.");
    }

    private sealed class RefreshableAudioPlatform(
        AudioPlatformCapabilities initialCapability,
        IReadOnlyList<AudioEndpointSnapshot>? initialOutputs = null,
        IReadOnlyList<AudioEndpointSnapshot>? initialMicrophones = null) :
        IAudioPlatform,
        IAudioPlatformCapabilityRefresher
    {
        private EventHandler<AudioPlatformSnapshot>? _snapshotChanged;

        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged
        {
            add => _snapshotChanged += value;
            remove => _snapshotChanged -= value;
        }

        public AudioPlatformSnapshot Snapshot { get; private set; } = CreateSnapshot(
            initialCapability,
            initialOutputs ?? [new AudioEndpointSnapshot("output", "Speakers", true, true)],
            initialMicrophones ?? [new AudioEndpointSnapshot("mic", "Microphone", true, true)]);

        public AudioPlatformCapabilities? NextCapability { get; set; }

        public bool FailNextStart { get; set; }

        public int RefreshCount { get; private set; }

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int SubscriberCount => _snapshotChanged?.GetInvocationList().Length ?? 0;

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken)
        {
            StartCount++;
            if (FailNextStart)
            {
                FailNextStart = false;
                return ValueTask.FromException(new InvalidOperationException("Injected audio start failure."));
            }

            _snapshotChanged?.Invoke(this, Snapshot);
            return ValueTask.CompletedTask;
        }

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
        }

        public ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            if (NextCapability is { } next)
            {
                NextCapability = null;
                PublishCapability(next);
            }

            return ValueTask.CompletedTask;
        }

        public void PublishCapability(AudioPlatformCapabilities capability)
        {
            Snapshot = Snapshot with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Capabilities = capability
            };
            _snapshotChanged?.Invoke(this, Snapshot);
        }

        public void PublishEndpoints(
            IReadOnlyList<AudioEndpointSnapshot> outputs,
            IReadOnlyList<AudioEndpointSnapshot> microphones)
        {
            Snapshot = Snapshot with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                OutputDevices = outputs,
                Microphones = microphones
            };
            _snapshotChanged?.Invoke(this, Snapshot);
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IAudioCaptureSession>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private static AudioPlatformSnapshot CreateSnapshot(
            AudioPlatformCapabilities capability,
            IReadOnlyList<AudioEndpointSnapshot> outputs,
            IReadOnlyList<AudioEndpointSnapshot> microphones) => new(
            DateTimeOffset.UtcNow,
            capability,
            outputs,
            microphones,
            [],
            []);
    }

    private sealed class EmptyWindowEvidenceProvider : IMeetingWindowEvidenceProvider
    {
        public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            MeetingProfileRegistry profiles,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>([]);
    }

    /// <summary>
    /// Keeps the microphone-health scenario inside the candidate-gated capture lifecycle.
    /// @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    /// </summary>
    private sealed class EligibleBrowserMeetingWindowEvidenceProvider : IMeetingWindowEvidenceProvider
    {
        public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            MeetingProfileRegistry profiles,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>(
            [
                new MeetingWindowEvidenceSnapshot(
                    audioPlatform.ObservedAtUtc,
                    "google-meet:browser:42",
                    "google-meet",
                    "Google Meet",
                    MeetingCandidateContext.BrowserService,
                    42,
                    "chrome.exe",
                    [
                        new MeetingEvidenceFact(
                            MeetingEvidenceKind.MeetingControls,
                            "google-meet.call-controls",
                            ProviderId: "fixture")
                    ])
            ]);
    }

    private sealed class EmptySpeechActivityProvider(Action? onDisposed = null)
        : IMeetingSpeechActivityProvider
    {
        private int _disposed;

        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } =
            MeetingSpeechActivitySnapshot.Empty;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            Snapshot = MeetingSpeechActivitySnapshot.Empty with
            {
                ObservedAtUtc = audioPlatform.ObservedAtUtc
            };
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDisposed?.Invoke();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableSpeechActivityProvider : IMeetingSpeechActivityProvider
    {
        private int _microphoneCaptureHealth;

        public MeetingSpeechCaptureHealth MicrophoneCaptureHealth
        {
            get => (MeetingSpeechCaptureHealth)Volatile.Read(ref _microphoneCaptureHealth);
            set => Volatile.Write(ref _microphoneCaptureHealth, (int)value);
        }

        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } =
            MeetingSpeechActivitySnapshot.Empty;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            Snapshot = MeetingSpeechActivitySnapshot.Empty with
            {
                ObservedAtUtc = audioPlatform.ObservedAtUtc,
                MicrophoneCaptureHealth = MicrophoneCaptureHealth
            };
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledSpeechActivityProvider : IMeetingSpeechActivityProvider
    {
        private readonly TaskCompletionSource<MeetingSpeechCaptureHealth> _result = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _observationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _observeCount;
        private int _disposeCount;

        public ControlledSpeechActivityProvider(MeetingSpeechCaptureHealth? result = null)
        {
            if (result is { } health)
            {
                _result.SetResult(health);
            }
        }

        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } =
            MeetingSpeechActivitySnapshot.Empty;

        public Task ObservationStarted => _observationStarted.Task;

        public Task Disposed => _disposed.Task;

        public int ObserveCount => Volatile.Read(ref _observeCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _observeCount);
            _observationStarted.TrySetResult();
            var health = await _result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Snapshot = MeetingSpeechActivitySnapshot.Empty with
            {
                ObservedAtUtc = audioPlatform.ObservedAtUtc,
                MicrophoneCaptureHealth = health
            };
            return Snapshot;
        }

        public void Complete(MeetingSpeechCaptureHealth health) => _result.TrySetResult(health);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableCapabilityAssessor : IHostCapabilityAssessor
    {
        public required HostCapabilitySnapshot Current { get; set; }

        public int AssessmentCount { get; private set; }

        public HostCapabilitySnapshot Assess()
        {
            AssessmentCount++;
            return Current;
        }
    }
}
