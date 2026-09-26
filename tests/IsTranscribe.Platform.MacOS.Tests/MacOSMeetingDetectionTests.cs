using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
public sealed class MacOSMeetingDetectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    public static IEnumerable<object[]> PositiveProfileCases()
    {
        yield return ["zoom.exe", "Zoom Meeting", "zoom", MeetingCandidateContext.DedicatedApplication];
        yield return ["ms-teams.exe", "Microsoft Teams meeting", "microsoft-teams", MeetingCandidateContext.DedicatedApplication];
        yield return ["zen.exe", "Meeting | Microsoft Teams", "microsoft-teams", MeetingCandidateContext.BrowserService];
        yield return ["zen.exe", "Planning — Google Meet", "google-meet", MeetingCandidateContext.BrowserService];
        yield return ["zen.exe", "Яндекс Телемост", "yandex-telemost", MeetingCandidateContext.BrowserService];
        yield return ["zen.exe", "Контур.Толк", "kontur-talk", MeetingCandidateContext.BrowserService];
        yield return ["zen.exe", "Acme Calls", "generic-browser", MeetingCandidateContext.BrowserService];
    }

    [Theory]
    [MemberData(nameof(PositiveProfileCases))]
    public void MacProfileFixturesProduceOneStableAskCandidate(
        string processName,
        string title,
        string profileId,
        MeetingCandidateContext expectedContext)
    {
        var profiles = new MeetingProfileRegistry();
        var audio = AudioSnapshot(processName, 42, active: true);
        var evidence = MacOSWindowEvidenceProvider.Match(
            audio,
            profiles,
            [new MacOSWindowProbe(42, 0xABC, processName, title, true, ["Unmute", "Leave"])],
            Now);
        var matched = Assert.Single(evidence, item => item.ProfileId == profileId);
        Assert.Equal(expectedContext, matched.Context);

        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();
        var first = Assert.Single(assembler.Assemble(audio, evidence, RemoteSpeakerSpeech(42), [], Now));
        var stable = Assert.Single(assembler.Assemble(
            audio,
            evidence,
            RemoteSpeakerSpeech(42),
            [],
            Now.AddSeconds(3)), item => item.ProfileId == profileId);

        Assert.Null(engine.EvaluateBatch([first], Now).PromptCandidate);
        var prompt = engine.EvaluateBatch([stable], Now.AddSeconds(3)).PromptCandidate;
        if (prompt is null)
        {
            var sustained = Assert.Single(assembler.Assemble(
                audio,
                evidence,
                RemoteSpeakerSpeech(42),
                [],
                Now.AddSeconds(6)), item => item.ProfileId == profileId);
            prompt = engine.EvaluateBatch([sustained], Now.AddSeconds(6)).PromptCandidate;
        }

        Assert.Equal(
            profileId,
            Assert.IsType<MeetingPromptCandidate>(prompt).Frame.ProfileId);
    }

    [Fact]
    public void MutedListenOnlyMacMeetingCanAskWithoutMicrophoneEvidence()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = AudioSnapshot("zoom.exe", 42, active: true, includeMicrophone: false);
        var evidence = MacOSWindowEvidenceProvider.Match(
            audio,
            profiles,
            [new MacOSWindowProbe(42, 0xABC, "zoom.exe", "Zoom Meeting", true, ["Unmute", "Leave"])],
            Now);
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();
        var first = Assert.Single(assembler.Assemble(audio, evidence, RemoteSpeakerSpeech(42), [], Now));
        var stable = Assert.Single(assembler.Assemble(
            audio,
            evidence,
            RemoteSpeakerSpeech(42),
            [],
            Now.AddSeconds(3)));

        Assert.Empty(audio.Microphones);
        Assert.Null(engine.EvaluateBatch([first], Now).PromptCandidate);
        var prompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch([stable], Now.AddSeconds(3)).PromptCandidate);
        Assert.DoesNotContain(prompt.Frame.Evidence, fact => fact.Kind is
            MeetingEvidenceKind.MicrophoneInUse or
            MeetingEvidenceKind.MicrophoneSpeech or
            MeetingEvidenceKind.ConversationalAlternation);
    }

    [Fact]
    public void DesktopWindowAndAccessibilityAreReducedToRuleIdentifiers()
    {
        var snapshot = AudioSnapshot("zoom.exe", 42, active: true);
        var windows = new[]
        {
            new MacOSWindowProbe(42, 77, "zoom.exe", "Private customer sync — Zoom Meeting", true,
                ["Mute", "Leave"])
        };

        var evidence = Assert.Single(MacOSWindowEvidenceProvider.Match(
            snapshot,
            new MeetingProfileRegistry(),
            windows,
            Now));

        Assert.Equal("zoom:desktop:42", evidence.CandidateId);
        Assert.Contains(evidence.Evidence, fact => fact.Kind == MeetingEvidenceKind.MeetingWindow);
        Assert.Contains(evidence.Evidence, fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
        Assert.Contains(evidence.Evidence, fact => fact.Kind == MeetingEvidenceKind.ForegroundWindow);
        Assert.DoesNotContain(evidence.Evidence, fact => fact.RuleId.Contains("customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SafariBrowserMeetingUsesStableWindowBoundaryAndPlaybackIsExcluded()
    {
        var snapshot = AudioSnapshot("safari.exe", 84, active: true);
        var windows = new[]
        {
            new MacOSWindowProbe(84, 0xABC, "safari.exe", "Weekly — Google Meet", false, ["Mute", "Leave"]),
            new MacOSWindowProbe(84, 0xDEF, "safari.exe", "YouTube", true, [])
        };

        var evidence = MacOSWindowEvidenceProvider.Match(snapshot, new MeetingProfileRegistry(), windows, Now);

        var meet = Assert.Single(evidence, item => item.ProfileId == "google-meet");
        Assert.Equal("browser:84:abc", meet.CandidateId);
        Assert.DoesNotContain(evidence, item => item.CandidateId.EndsWith(":def", StringComparison.Ordinal)
                                                 && item.Evidence.All(fact => fact.Kind != MeetingEvidenceKind.HardExclusion));
    }

    [Fact]
    public async Task SpeechProviderEmitsBoundedRenderAndMicrophoneSummariesWithoutArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-macos-vad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeCaptureFactory(Now);
            await using var provider = new MacOSMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            await provider.StartAsync(CancellationToken.None);
            var audio = AudioSnapshot("zoom.exe", 42, active: true);
            await provider.ApplyObservationDemandAsync(
                new MeetingSpeechObservationDemand([42], ObserveMicrophone: true),
                audio,
                CancellationToken.None);

            var observed = await provider.ObserveAsync(audio, CancellationToken.None);

            Assert.True(observed.RenderByRootProcessId[42].IsSustainedSpeech);
            Assert.True(observed.Microphone.IsSustainedSpeech);
            Assert.Equal(MeetingSpeechCaptureHealth.Available, observed.MicrophoneCaptureHealth);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), path => Path.GetExtension(path) is ".wav" or ".mp3" or ".m4a");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task IdleProviderDoesNotStartRenderOrMicrophoneCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-macos-idle-vad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeCaptureFactory(Now);
            await using var provider = new MacOSMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            await provider.StartAsync(CancellationToken.None);

            var observed = await provider.ObserveAsync(
                AudioSnapshot("zen.exe", 42, active: true),
                CancellationToken.None);

            Assert.Equal(0, factory.RenderStartCount);
            Assert.Equal(0, factory.MicrophoneStartCount);
            Assert.Empty(observed.RenderByRootProcessId);
            Assert.Equal(MeetingSpeechCaptureHealth.Unknown, observed.MicrophoneCaptureHealth);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task RemovingDemandDisposesAllDetectionCaptures()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-macos-demand-vad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeCaptureFactory(Now);
            await using var provider = new MacOSMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            await provider.StartAsync(CancellationToken.None);
            var audio = AudioSnapshot("zoom.exe", 42, active: true);
            await provider.ApplyObservationDemandAsync(
                new MeetingSpeechObservationDemand([42], ObserveMicrophone: true),
                audio,
                CancellationToken.None);

            await provider.ApplyObservationDemandAsync(
                MeetingSpeechObservationDemand.Empty,
                audio,
                CancellationToken.None);

            Assert.Equal(1, factory.RenderStartCount);
            Assert.Equal(1, factory.MicrophoneStartCount);
            Assert.All(factory.Captures, static capture => Assert.True(capture.IsDisposed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task ExplicitMicrophoneProbeClosesItsTemporaryCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-macos-probe-vad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeCaptureFactory(Now);
            await using var provider = new MacOSMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            await provider.StartAsync(CancellationToken.None);

            var health = await provider.ObserveMicrophoneCaptureHealthAsync(
                AudioSnapshot("zoom.exe", 42, active: false),
                CancellationToken.None);

            Assert.Equal(MeetingSpeechCaptureHealth.Available, health);
            Assert.Equal(0, factory.RenderStartCount);
            Assert.Equal(1, factory.MicrophoneStartCount);
            Assert.True(Assert.Single(factory.Captures).IsDisposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeWindowInventoryIsCallableOnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var native = new MacOSMeetingNative();
        var windows = native.EnumerateWindows(includeAccessibility: false);

        Assert.All(windows, window => Assert.True(window.ProcessId > 0));
    }

    [Fact]
    public async Task AccessibilityTraversalIsLimitedToObservedMeetingProcessFamilies()
    {
        var native = new ScopedAccessibilityNative("Call");
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.False(native.IncludeAccessibilityRequested);
        Assert.Equal([42], native.AccessibilityProcessIds);
        Assert.Single(evidence, item => item.ProfileId == "zoom");
    }

    [Fact]
    public async Task StrongWindowEvidenceSkipsAccessibilityTraversal()
    {
        var native = new ScopedAccessibilityNative("Zoom Meeting");
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Empty(native.AccessibilityProcessIds);
        Assert.Single(evidence, item => item.ProfileId == "zoom");
    }

    [Fact]
    public async Task AccessibilityTitleRestoresRedactedDesktopWindowWithoutControlTraversal()
    {
        var native = new ScopedAccessibilityNative(string.Empty, "Zoom Workplace\nZoom Meeting");
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Equal([42], native.AccessibilityTitleProcessIds);
        Assert.Empty(native.AccessibilityProcessIds);
        Assert.Contains(
            Assert.Single(evidence, item => item.ProfileId == "zoom").Evidence,
            fact => fact.RuleId == "zoom.meeting-window");
    }

    [Fact]
    public async Task AccessibilityTitleReplacesGenericDesktopWindowTitle()
    {
        var native = new ScopedAccessibilityNative("Zoom Workplace", "Zoom Workplace\nZoom Meeting");
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Equal([42], native.AccessibilityTitleProcessIds);
        Assert.Empty(native.AccessibilityProcessIds);
        Assert.Contains(
            Assert.Single(evidence, item => item.ProfileId == "zoom").Evidence,
            fact => fact.RuleId == "zoom.meeting-window");
    }

    [Fact]
    public async Task AccessibilityDocumentTitleRestoresRedactedZenMeetingWindow()
    {
        var native = new ScopedAccessibilityNative(
            "Zen Browser",
            "Zen Browser\nLocal Provider Smoke - Google Meet",
            meetingWindowOwnerName: "Zen");
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zen.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Equal([42], native.AccessibilityTitleProcessIds);
        Assert.Contains(
            Assert.Single(evidence, item => item.ProfileId == "google-meet").Evidence,
            fact => fact.RuleId == "google-meet.meeting-window");
    }

    [Fact]
    public async Task AccessibilityDocumentTitleIsAppliedToOnlyOneZenWindow()
    {
        var native = new ScopedAccessibilityNative(
            "Window",
            "Zen Browser\nLocal Provider Smoke - Google Meet",
            meetingWindowOwnerName: "Zen",
            includeDuplicateMeetingWindow: true);
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zen.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Equal([42], native.AccessibilityTitleProcessIds);
        Assert.Equal("google-meet", Assert.Single(evidence).ProfileId);
    }

    [Fact]
    public async Task AccessibilityQueriesUseAudioRootForHelperOwnedWindow()
    {
        var native = new ScopedAccessibilityNative(string.Empty, "Zoom Meeting", meetingWindowProcessId: 777);
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Equal([777, 42], native.AccessibilityTitleProcessIds);
        Assert.Single(evidence, item => item.ProfileId == "zoom");
    }

    [Fact]
    public async Task ZoomMeetingHostWindowProducesEvidenceWithoutAccessibilityTraversal()
    {
        var native = new ScopedAccessibilityNative(
            "Zoom Workplace",
            meetingWindowProcessId: 777,
            meetingWindowOwnerName: "Zoom",
            hasZoomMeetingHost: true);
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot("zoom.exe", 42, active: true, processTreeIds: new HashSet<int> { 42, 777 }),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        Assert.Empty(native.AccessibilityTitleProcessIds);
        Assert.Empty(native.AccessibilityProcessIds);
        Assert.Contains(
            Assert.Single(evidence, item => item.ProfileId == "zoom").Evidence,
            fact => fact.RuleId == "zoom.meeting-window");
    }

    [Fact]
    public async Task ZoomMeetingHostKeepsEvidenceWhenLayerZeroWindowMovesOffScreenOrToAnotherSpace()
    {
        var native = new ScopedAccessibilityNative(
            string.Empty,
            hasZoomMeetingHost: true,
            zoomMeetingHostProcessId: 777,
            includeMeetingWindow: false);
        var provider = new MacOSWindowEvidenceProvider(native);

        var evidence = await provider.ObserveAsync(
            AudioSnapshot(
                "zoom.exe",
                42,
                active: true,
                processTreeIds: new HashSet<int> { 42, 777 }),
            new MeetingProfileRegistry(),
            CancellationToken.None);

        var zoom = Assert.Single(evidence, item => item.ProfileId == "zoom");
        Assert.Equal("zoom:desktop:42", zoom.CandidateId);
        Assert.Contains(zoom.Evidence, fact => fact.RuleId == "zoom.meeting-window");
        Assert.Empty(native.AccessibilityTitleProcessIds);
        Assert.Empty(native.AccessibilityProcessIds);
    }

    [Theory]
    [InlineData("zoom.us", "us.zoom.xos", "zoom.exe")]
    [InlineData("Microsoft Teams Helper", "com.microsoft.teams2", "ms-teams.exe")]
    [InlineData("Google Chrome Helper", "com.google.Chrome.helper", "chrome.exe")]
    [InlineData("Safari", "com.apple.Safari", "safari.exe")]
    [InlineData("Zen", "app.zen-browser.zen", "zen.exe")]
    [InlineData("plugin-container", "app.zen-browser.zen", "zen.exe")]
    public void MacApplicationFamiliesNormalizeIntoSharedProfiles(string name, string bundleId, string expected)
    {
        var normalized = MacOSProcessIdentity.Normalize(name, bundleId);

        Assert.Equal(expected, normalized);
        Assert.NotEmpty(new MeetingProfileRegistry().MatchProcess(normalized));
    }

    [Fact]
    public void MacAndWindowsNormalizedEvidenceProduceIdenticalAskLifecycle()
    {
        var mac = BuildReplay("macos-window", "macos-accessibility");
        var windows = BuildReplay("windows-window", "windows-uia");

        var macResult = new MeetingDetectionReplayRunner().Run(mac);
        var windowsResult = new MeetingDetectionReplayRunner().Run(windows);

        Assert.Equal(
            windowsResult.Decisions.Select(ProjectDecision),
            macResult.Decisions.Select(ProjectDecision));
        Assert.Single(macResult.WouldPromptCandidates);
    }

    [Fact]
    public void BrowserPlaybackAndIdleDesktopRemainBelowAskEligibility()
    {
        var frames = Enumerable.Range(0, 8).Select(second => new MeetingDetectionReplayStep(
            Now.AddSeconds(second),
            [
                new MeetingObservationFrame(
                    Now.AddSeconds(second),
                    "browser:video",
                    "generic-browser",
                    "Browser",
                    MeetingCandidateContext.BrowserService,
                    84,
                    "safari.exe",
                    [
                        new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "generic-browser.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.9)
                    ],
                    new MeetingProfileRegistry().FindById("generic-browser")!.EvidenceWeightOverrides),
                new MeetingObservationFrame(
                    Now.AddSeconds(second),
                    "zoom:idle",
                    "zoom",
                    "Zoom",
                    MeetingCandidateContext.DedicatedApplication,
                    42,
                    "zoom.exe",
                    [
                        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")
                    ],
                    new MeetingProfileRegistry().FindById("zoom")!.EvidenceWeightOverrides)
            ])).ToArray();

        Assert.Empty(new MeetingDetectionReplayRunner().Run(frames).WouldPromptCandidates);
    }

    private static AudioPlatformSnapshot AudioSnapshot(
        string processName,
        int rootProcessId,
        bool active,
        bool includeMicrophone = true,
        IReadOnlySet<int>? processTreeIds = null) => new(
        Now,
        new AudioPlatformCapabilities(true, true, "test"),
        [new AudioEndpointSnapshot("output", "Output", true, true)],
        includeMicrophone ? [new AudioEndpointSnapshot("microphone", "Microphone", true, true)] : [],
        [new ObservedProcessSnapshot(rootProcessId, processName, processTreeIds ?? new HashSet<int> { rootProcessId })],
        [new AudioSignalSnapshot(Now, processName, rootProcessId, active ? "active" : "inactive", active ? -30 : -100, "output", true, false)]);

    private static MeetingSpeechActivitySnapshot RemoteSpeakerSpeech(int rootProcessId) => new(
        Now,
        new Dictionary<int, MeetingSpeechActivitySummary>
        {
            [rootProcessId] = new(0.9, true, TimeSpan.FromSeconds(4), 0.7)
        },
        MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>());

    private static MeetingDetectionReplayStep[] BuildReplay(string windowProvider, string controlsProvider)
    {
        var weights = new MeetingProfileRegistry().FindById("zoom")!.EvidenceWeightOverrides;
        return Enumerable.Range(0, 6).Select(second => new MeetingDetectionReplayStep(
            Now.AddSeconds(second),
            [new MeetingObservationFrame(
                Now.AddSeconds(second),
                "zoom:desktop:42",
                "zoom",
                "Zoom",
                MeetingCandidateContext.DedicatedApplication,
                42,
                "zoom.exe",
                [
                    new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity", ProviderId: "profile"),
                    new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active", ProviderId: "audio-session"),
                    new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.9, "speech-envelope"),
                    new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "zoom.meeting-window", 1, windowProvider),
                    new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, "zoom.call-controls", 1, controlsProvider),
                    new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable", ProviderId: "process")
                ],
                weights)])).ToArray();
    }

    private static (int[] Scores, bool Prompt) ProjectDecision(MeetingDetectionReplayDecision decision) =>
        (decision.Candidates.Select(static candidate => candidate.Score.Score).ToArray(), decision.WouldPrompt is not null);

    private sealed class FakeCaptureFactory(DateTimeOffset now) : IMacOSDetectionCaptureFactory
    {
        public List<FakeCapture> Captures { get; } = [];

        public int RenderStartCount { get; private set; }

        public int MicrophoneStartCount { get; private set; }

        public IMacOSDetectionCapture StartRender(int rootProcessId)
        {
            RenderStartCount++;
            return Track();
        }

        public IMacOSDetectionCapture StartMicrophone(string deviceId)
        {
            MicrophoneStartCount++;
            return Track();
        }

        private FakeCapture Track()
        {
            var capture = new FakeCapture(now);
            Captures.Add(capture);
            return capture;
        }
    }

    private sealed class ScopedAccessibilityNative : IMacOSMeetingNative
    {
        private readonly string _meetingWindowTitle;
        private readonly string _accessibleWindowTitle;
        private readonly int _meetingWindowProcessId;
        private readonly string _meetingWindowOwnerName;
        private readonly bool _hasZoomMeetingHost;
        private readonly bool _includeDuplicateMeetingWindow;
        private readonly bool _includeMeetingWindow;
        private readonly int _zoomMeetingHostProcessId;

        public ScopedAccessibilityNative(
            string meetingWindowTitle,
            string accessibleWindowTitle = "",
            int meetingWindowProcessId = 42,
            string meetingWindowOwnerName = "zoom.us",
            bool hasZoomMeetingHost = false,
            bool includeDuplicateMeetingWindow = false,
            bool includeMeetingWindow = true,
            int zoomMeetingHostProcessId = 42)
        {
            _meetingWindowTitle = meetingWindowTitle;
            _accessibleWindowTitle = accessibleWindowTitle;
            _meetingWindowProcessId = meetingWindowProcessId;
            _meetingWindowOwnerName = meetingWindowOwnerName;
            _hasZoomMeetingHost = hasZoomMeetingHost;
            _includeDuplicateMeetingWindow = includeDuplicateMeetingWindow;
            _includeMeetingWindow = includeMeetingWindow;
            _zoomMeetingHostProcessId = zoomMeetingHostProcessId;
        }

        public bool HasAccessibilityPermission => true;

        public bool HasScreenCapturePermission => true;

        public bool IncludeAccessibilityRequested { get; private set; }

        public List<int> AccessibilityProcessIds { get; } = [];

        public List<int> AccessibilityTitleProcessIds { get; } = [];

        public IReadOnlyList<MacOSNativeWindow> EnumerateWindows(bool includeAccessibility)
        {
            IncludeAccessibilityRequested |= includeAccessibility;
            var windows = new List<MacOSNativeWindow>();
            if (_includeMeetingWindow)
            {
                windows.Add(new MacOSNativeWindow(
                    _meetingWindowProcessId,
                    0xABC,
                    true,
                    _meetingWindowOwnerName,
                    _meetingWindowTitle,
                    []));
            }
            if (_includeDuplicateMeetingWindow)
            {
                windows.Add(new MacOSNativeWindow(
                    _meetingWindowProcessId,
                    0xABD,
                    true,
                    _meetingWindowOwnerName,
                    "Zen Browser",
                    []));
            }
            windows.Add(new MacOSNativeWindow(99, 0xDEF, false, "Unrelated", "Document", []));
            return windows;
        }

        public bool HasZoomMeetingHost(int rootProcessId) =>
            _hasZoomMeetingHost && rootProcessId == _zoomMeetingHostProcessId;

        public IReadOnlyList<string> ReadAccessibleControlNames(int processId)
        {
            AccessibilityProcessIds.Add(processId);
            return ["Mute", "Leave"];
        }

        public string ReadAccessibleWindowTitle(int processId)
        {
            AccessibilityTitleProcessIds.Add(processId);
            return _accessibleWindowTitle;
        }
    }

    private sealed class FakeCapture : IMacOSDetectionCapture
    {
        public FakeCapture(DateTimeOffset now)
        {
            Estimator = new SpeechActivityEstimator();
            const int sampleRate = 16_000;
            var samples = new float[sampleRate * 4];
            for (var index = 0; index < samples.Length; index++)
            {
                var time = (double)index / sampleRate;
                var syllable = time % 0.72;
                if (syllable > 0.57) continue;
                var fundamental = 128 + (32 * Math.Sin(2 * Math.PI * 0.7 * time));
                var envelope = 0.12 + (0.88 * Math.Pow(Math.Sin(Math.PI * syllable / 0.57), 2));
                var voiced = Math.Sin(2 * Math.PI * fundamental * time)
                             + (0.55 * Math.Sin(2 * Math.PI * fundamental * 2 * time))
                             + (0.30 * Math.Sin(2 * Math.PI * fundamental * 3 * time))
                             + (0.19 * Math.Sin(2 * Math.PI * fundamental * 5 * time))
                             + (0.14 * Math.Sin(2 * Math.PI * fundamental * 7 * time));
                samples[index] = (float)(0.16 * envelope * voiced);
            }
            Estimator.AppendMonoSamples(samples, sampleRate, now - TimeSpan.FromSeconds(4));
        }

        public bool IsActive => !IsDisposed;

        public bool IsDisposed { get; private set; }

        public SpeechActivityEstimator Estimator { get; }

        public void Dispose()
        {
            IsDisposed = true;
            Estimator.Reset();
        }
    }
}
