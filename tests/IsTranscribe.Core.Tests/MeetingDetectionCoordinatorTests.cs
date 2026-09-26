using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
public sealed class MeetingDetectionCoordinatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public async Task StableEvidenceRaisesOnePromptWithoutStartingRecordingCapture()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var coordinator = CreateCoordinator(audio);

        var first = await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);
        var repeated = await coordinator.EvaluateOnceAsync(Start.AddSeconds(4), CancellationToken.None);

        Assert.Null(first.PendingPrompt);
        Assert.NotNull(stable.PendingPrompt);
        Assert.Equal(
            stable.PendingPrompt!.Candidate.Frame.CandidateId,
            repeated.PendingPrompt!.Candidate.Frame.CandidateId);
        Assert.Equal(stable.PendingPrompt.CreatedAtUtc, repeated.PendingPrompt.CreatedAtUtc);
        Assert.Equal(stable.PendingPrompt.ExpiresAtUtc, repeated.PendingPrompt.ExpiresAtUtc);
        Assert.Equal(0, audio.RecordingCaptureStartCount);
    }

    [Fact]
    public async Task SkipSuppressesTheCurrentCandidate()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var coordinator = CreateCoordinator(audio);
        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);

        var resolved = await coordinator.ResolvePromptAsync(
            stable.PendingPrompt!.Candidate.Frame.CandidateId,
            MeetingPromptResolution.Skip,
            Start.AddSeconds(4),
            CancellationToken.None);
        var next = await coordinator.EvaluateOnceAsync(Start.AddSeconds(5), CancellationToken.None);

        Assert.True(resolved);
        Assert.Null(next.PendingPrompt);
    }

    [Fact]
    public async Task PromptTimeoutResolvesSafelyToSuppression()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var coordinator = CreateCoordinator(audio);
        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);

        var timedOut = await coordinator.EvaluateOnceAsync(
            stable.PendingPrompt!.ExpiresAtUtc,
            CancellationToken.None);

        Assert.Null(timedOut.PendingPrompt);
        Assert.Contains(timedOut.Candidates, static candidate => candidate.IsSuppressed);
        Assert.Equal(0, audio.RecordingCaptureStartCount);
    }

    [Fact]
    public async Task ProviderFailureIsReportedAsPrivacySafeDegradedSignal()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var speech = new FakeSpeechProvider(throwOnObserve: true);
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new FakeWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);

        var snapshot = await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);

        Assert.Contains("speech_activity", snapshot.DegradedSignals);
        Assert.Null(snapshot.PendingPrompt);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task BrowserPlaybackWithoutMeetingEvidenceKeepsAudioObservationClosed()
    {
        await using var audio = new FakeAudioPlatform(BrowserAudio());
        await using var speech = new FakeSpeechProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new EmptyWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);

        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);

        Assert.Empty(speech.LastDemand.RenderRootProcessIds);
        Assert.False(speech.LastDemand.ObserveMicrophone);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task BrowserMeetingEvidenceOpensOnlyTheEligibleRoot()
    {
        await using var audio = new FakeAudioPlatform(BrowserAudio());
        await using var speech = new FakeSpeechProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new RefiningBrowserWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);

        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);

        Assert.Equal([42], speech.LastDemand.RenderRootProcessIds);
        Assert.True(speech.LastDemand.ObserveMicrophone);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task SkipReleasesObservationOnTheNextEvaluation()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var speech = new FakeSpeechProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new FakeWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);
        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);

        await coordinator.ResolvePromptAsync(
            stable.PendingPrompt!.Candidate.Frame.CandidateId,
            MeetingPromptResolution.Skip,
            Start.AddSeconds(4),
            CancellationToken.None);
        await coordinator.EvaluateOnceAsync(Start.AddSeconds(5), CancellationToken.None);

        Assert.Empty(speech.LastDemand.RenderRootProcessIds);
        Assert.False(speech.LastDemand.ObserveMicrophone);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task StoppingCoordinatorReleasesObservationImmediately()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var speech = new FakeSpeechProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new FakeWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);
        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        Assert.True(speech.LastDemand.ObserveMicrophone);

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Empty(speech.LastDemand.RenderRootProcessIds);
        Assert.False(speech.LastDemand.ObserveMicrophone);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task ConfirmedRecordingHoldsObservationUntilTheActiveSessionEnds()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var speech = new FakeSpeechProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new FakeWindowProvider(),
            speech,
            new MeetingProfileRegistry(),
            []);
        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);
        var candidateId = stable.PendingPrompt!.Candidate.Frame.CandidateId;

        Assert.True(await coordinator.ResolvePromptAsync(
            candidateId,
            MeetingPromptResolution.Record,
            Start.AddSeconds(4),
            CancellationToken.None));
        await coordinator.EvaluateOnceAsync(Start.AddSeconds(5), CancellationToken.None);

        Assert.Equal([42], speech.LastDemand.RenderRootProcessIds);
        Assert.True(speech.LastDemand.ObserveMicrophone);

        Assert.True(await coordinator.EndActiveSessionAsync(
            candidateId,
            Start.AddSeconds(6),
            CancellationToken.None));
        await coordinator.EvaluateOnceAsync(Start.AddSeconds(7), CancellationToken.None);

        Assert.Empty(speech.LastDemand.RenderRootProcessIds);
        Assert.False(speech.LastDemand.ObserveMicrophone);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task LostBrowserMeetingEvidenceReleasesObservationAtTheWindowCadence()
    {
        await using var audio = new FakeAudioPlatform(BrowserAudio());
        await using var speech = new FakeSpeechProvider();
        var windows = new RefiningBrowserWindowProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            windows,
            speech,
            new MeetingProfileRegistry(),
            []);

        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        Assert.Equal([42], speech.LastDemand.RenderRootProcessIds);

        windows.Hide();
        await coordinator.EvaluateOnceAsync(Start.AddMilliseconds(500), CancellationToken.None);
        Assert.Equal([42], speech.LastDemand.RenderRootProcessIds);

        await coordinator.EvaluateOnceAsync(Start.AddSeconds(1), CancellationToken.None);
        Assert.Empty(speech.LastDemand.RenderRootProcessIds);
        Assert.False(speech.LastDemand.ObserveMicrophone);
    }

    [Fact]
    public async Task ShadowModePublishesWouldPromptWithoutCreatingALivePrompt()
    {
        await using var audio = new FakeAudioPlatform(ZoomAudio());
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            new FakeWindowProvider(),
            new FakeSpeechProvider(),
            new MeetingProfileRegistry(),
            [],
            new MeetingDetectionEngine(mode: MeetingDetectionMode.Shadow));

        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        var stable = await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);

        Assert.Null(stable.PendingPrompt);
        Assert.Equal("zoom:desktop:42", stable.ShadowWouldPromptCandidate?.Frame.CandidateId);
        Assert.Equal(0, audio.RecordingCaptureStartCount);
    }

    [Fact]
    public async Task OpenPromptRefreshesWhenTheSameBrowserWindowGetsAKnownProfile()
    {
        await using var audio = new FakeAudioPlatform(BrowserAudio());
        var windows = new RefiningBrowserWindowProvider();
        await using var coordinator = new MeetingDetectionCoordinator(
            audio,
            windows,
            new FakeSpeechProvider(includeConversation: true),
            new MeetingProfileRegistry(),
            []);

        await coordinator.EvaluateOnceAsync(Start, CancellationToken.None);
        await coordinator.EvaluateOnceAsync(Start.AddSeconds(3), CancellationToken.None);
        var prompted = await coordinator.EvaluateOnceAsync(Start.AddSeconds(6), CancellationToken.None);
        var original = Assert.IsType<MeetingPendingPrompt>(prompted.PendingPrompt);
        Assert.Equal("generic-browser", original.Candidate.Frame.ProfileId);

        windows.RecognizeGoogleMeet();
        var refreshed = await coordinator.EvaluateOnceAsync(Start.AddSeconds(7), CancellationToken.None);
        var pending = Assert.IsType<MeetingPendingPrompt>(refreshed.PendingPrompt);

        Assert.Equal(original.Candidate.Frame.CandidateId, pending.Candidate.Frame.CandidateId);
        Assert.Equal("google-meet", pending.Candidate.Frame.ProfileId);
        Assert.Equal(original.CreatedAtUtc, pending.CreatedAtUtc);
        Assert.Equal(original.ExpiresAtUtc, pending.ExpiresAtUtc);
    }

    private static MeetingDetectionCoordinator CreateCoordinator(FakeAudioPlatform audio) => new(
        audio,
        new FakeWindowProvider(),
        new FakeSpeechProvider(),
        new MeetingProfileRegistry(),
        []);

    private static AudioPlatformSnapshot ZoomAudio() => new(
        Start,
        new AudioPlatformCapabilities(true, true, "test"),
        [new AudioEndpointSnapshot("output", "Output", true, true)],
        [new AudioEndpointSnapshot("mic", "Mic", true, true)],
        [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 })],
        [
            new AudioSignalSnapshot(
                Start,
                "zoom.exe",
                42,
                "AudioSessionStateActive",
                -20,
                "output",
                true,
                true)
        ]);

    private static AudioPlatformSnapshot BrowserAudio() => new(
        Start,
        new AudioPlatformCapabilities(true, true, "test"),
        [new AudioEndpointSnapshot("output", "Output", true, true)],
        [new AudioEndpointSnapshot("mic", "Mic", true, true)],
        [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42 })],
        [
            new AudioSignalSnapshot(
                Start,
                "chrome.exe",
                42,
                "AudioSessionStateActive",
                -20,
                "output",
                true,
                true)
        ]);

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
                            ProviderId: "test")
                    ])
            ]);
    }

    private sealed class FakeSpeechProvider(
        bool throwOnObserve = false,
        bool includeConversation = false) : IMeetingSpeechActivityProvider
    {
        public MeetingSpeechActivitySnapshot Snapshot { get; private set; } = MeetingSpeechActivitySnapshot.Empty;

        public MeetingSpeechObservationDemand LastDemand { get; private set; } = MeetingSpeechObservationDemand.Empty;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ApplyObservationDemandAsync(
            MeetingSpeechObservationDemand demand,
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            LastDemand = demand;
            return ValueTask.CompletedTask;
        }

        public ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            CancellationToken cancellationToken)
        {
            if (throwOnObserve)
            {
                throw new InvalidOperationException("fixture failure containing no user data");
            }

            Snapshot = new MeetingSpeechActivitySnapshot(
                audioPlatform.ObservedAtUtc,
                new Dictionary<int, MeetingSpeechActivitySummary>
                {
                    [42] = new(0.8, true, TimeSpan.FromSeconds(4), 0.6)
                },
                includeConversation
                    ? new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(4), 0.6)
                    : MeetingSpeechActivitySummary.Empty,
                includeConversation
                    ? new Dictionary<int, double> { [42] = 0.6 }
                    : new Dictionary<int, double>());
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyWindowProvider : IMeetingWindowEvidenceProvider
    {
        public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            MeetingProfileRegistry profiles,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>([]);
    }

    private sealed class RefiningBrowserWindowProvider : IMeetingWindowEvidenceProvider
    {
        private string _profileId = "generic-browser";
        private bool _isVisible = true;

        public void RecognizeGoogleMeet() => _profileId = "google-meet";

        public void Hide() => _isVisible = false;

        public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
            AudioPlatformSnapshot audioPlatform,
            MeetingProfileRegistry profiles,
            CancellationToken cancellationToken)
        {
            if (!_isVisible)
            {
                return ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>([]);
            }

            var isKnown = string.Equals(_profileId, "google-meet", StringComparison.Ordinal);
            return ValueTask.FromResult<IReadOnlyList<MeetingWindowEvidenceSnapshot>>(
            [
                new MeetingWindowEvidenceSnapshot(
                    audioPlatform.ObservedAtUtc,
                    "browser:42:3e9",
                    _profileId,
                    isKnown ? "Google Meet" : "Другая встреча в браузере",
                    MeetingCandidateContext.BrowserService,
                    42,
                    "chrome.exe",
                    [
                        new MeetingEvidenceFact(
                            MeetingEvidenceKind.MeetingControls,
                            isKnown ? "google-meet.call-controls" : "generic-browser.call-controls",
                            ProviderId: "test")
                    ])
            ]);
        }
    }

    private sealed class FakeAudioPlatform(AudioPlatformSnapshot snapshot) : IAudioPlatform
    {
        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AudioPlatformSnapshot Snapshot { get; } = snapshot;

        public int RecordingCaptureStartCount { get; private set; }

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken)
        {
            RecordingCaptureStartCount++;
            throw new InvalidOperationException("Recording capture is forbidden in detection tests.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
