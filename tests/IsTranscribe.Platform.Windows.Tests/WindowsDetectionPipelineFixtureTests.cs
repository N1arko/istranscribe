using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Exercises profile data through the real privacy-reduced Windows matcher and Core detection pipeline.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#acceptance
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDetectionPipelineFixtureTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    public static IEnumerable<object[]> PositiveProfileCases()
    {
        yield return
        [
            "zoom.exe", "Zoom Meeting", "zoom", MeetingCandidateContext.DedicatedApplication,
            "zoom.meeting-window", "zoom.call-controls"
        ];
        yield return
        [
            "ms-teams.exe", "Microsoft Teams meeting", "microsoft-teams",
            MeetingCandidateContext.DedicatedApplication,
            "microsoft-teams.meeting-window", "teams.call-controls"
        ];
        yield return
        [
            "chrome.exe", "Meeting | Microsoft Teams", "microsoft-teams",
            MeetingCandidateContext.BrowserService,
            "microsoft-teams.meeting-window", "teams.call-controls"
        ];
        yield return
        [
            "zen.exe", "Meeting | Microsoft Teams", "microsoft-teams",
            MeetingCandidateContext.BrowserService,
            "microsoft-teams.meeting-window", "teams.call-controls"
        ];
        yield return
        [
            "chrome.exe", "Planning — Google Meet", "google-meet", MeetingCandidateContext.BrowserService,
            "google-meet.meeting-window", "google-meet.call-controls"
        ];
        yield return
        [
            "zen.exe", "Planning — Google Meet", "google-meet", MeetingCandidateContext.BrowserService,
            "google-meet.meeting-window", "google-meet.call-controls"
        ];
        yield return
        [
            "yandextelemost.exe", "Яндекс Телемост", "yandex-telemost",
            MeetingCandidateContext.DedicatedApplication,
            "yandex-telemost.meeting-window", "telemost.call-controls"
        ];
        yield return
        [
            "chrome.exe", "Яндекс Телемост", "yandex-telemost", MeetingCandidateContext.BrowserService,
            "yandex-telemost.meeting-window", "telemost.call-controls"
        ];
        yield return
        [
            "zen.exe", "Яндекс Телемост", "yandex-telemost", MeetingCandidateContext.BrowserService,
            "yandex-telemost.meeting-window", "telemost.call-controls"
        ];
        yield return
        [
            "tolk.exe", "Контур.Толк", "kontur-talk", MeetingCandidateContext.DedicatedApplication,
            "kontur-talk.meeting-window", "kontur-talk.call-controls"
        ];
        yield return
        [
            "chrome.exe", "Контур.Толк", "kontur-talk", MeetingCandidateContext.BrowserService,
            "kontur-talk.meeting-window", "kontur-talk.call-controls"
        ];
        yield return
        [
            "zen.exe", "Контур.Толк", "kontur-talk", MeetingCandidateContext.BrowserService,
            "kontur-talk.meeting-window", "kontur-talk.call-controls"
        ];
    }

    [Theory]
    [MemberData(nameof(PositiveProfileCases))]
    public void InitialProfileRulesProduceOneStableAskCandidate(
        string processName,
        string windowTitle,
        string profileId,
        MeetingCandidateContext expectedContext,
        string expectedWindowRule,
        string expectedControlRule)
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, processName, new HashSet<int> { 42, 501 })],
            [Signal(processName, 42, -24)]);
        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, windowTitle, true, ["Unmute", "Leave"])],
            Start);
        var matched = Assert.Single(windowEvidence, evidence => evidence.ProfileId == profileId);
        Assert.Equal(expectedContext, matched.Context);
        Assert.Contains(matched.Evidence, fact => fact.RuleId == expectedWindowRule);
        Assert.Contains(matched.Evidence, fact => fact.RuleId == expectedControlRule);
        Assert.DoesNotContain(windowEvidence, static evidence => evidence.ProfileId == "generic-browser");

        var assembler = new MeetingObservationAssembler(profiles);
        var speech = SpeechForRoot(42);
        var first = Assert.Single(assembler.Assemble(audio, windowEvidence, speech, [], Start));
        var stable = Assert.Single(assembler.Assemble(audio, windowEvidence, speech, [], Start.AddSeconds(3)));
        var engine = new MeetingDetectionEngine();

        Assert.Null(engine.EvaluateBatch([first], Start).PromptCandidate);
        var result = engine.EvaluateBatch([stable], Start.AddSeconds(3));

        Assert.Equal(profileId, Assert.IsType<MeetingPromptCandidate>(result.PromptCandidate).Frame.ProfileId);
    }

    [Fact]
    public void CurrentRussianZoomMeetingSelectorsProduceStrongEvidence()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42, 501 })],
            []);

        var evidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "Zoom Конференция", true, ["Включить звук", "Завершение"])],
            Start);

        var zoom = Assert.Single(evidence, static item => item.ProfileId == "zoom");
        Assert.Contains(zoom.Evidence, static fact => fact.RuleId == "zoom.meeting-window");
        Assert.Contains(zoom.Evidence, static fact => fact.RuleId == "zoom.call-controls");
    }

    [Fact]
    public void ZenFirefoxStyleAccessibleControlNamesProduceStrongMeetEvidence()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "zen.exe", new HashSet<int> { 42, 501 })],
            [Signal("zen.exe", 42, -24)]);

        var evidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [
                new WindowsWindowProbeSnapshot(
                    501,
                    "Planning — Google Meet",
                    true,
                    ["Turn off microphone (Ctrl+D)", "Leave call"])
            ],
            Start);

        var meet = Assert.Single(evidence, static item => item.ProfileId == "google-meet");
        Assert.Equal("zen.exe", meet.ProcessName);
        Assert.Contains(meet.Evidence, static fact => fact.RuleId == "google-meet.call-controls");
    }

    [Fact]
    public void CurrentRussianTeamsMeetingSelectorsProduceStrongEvidence()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "ms-teams.exe", new HashSet<int> { 42, 501 })],
            []);

        var evidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [
                new WindowsWindowProbeSnapshot(
                    501,
                    "Собрание с организатором Nikita Arkhipov | Microsoft Teams",
                    true,
                    ["Включить микрофон", "Выйти"])
            ],
            Start);

        var teams = Assert.Single(evidence, static item => item.ProfileId == "microsoft-teams");
        Assert.Contains(teams.Evidence, static fact => fact.RuleId == "microsoft-teams.meeting-window");
        Assert.Contains(teams.Evidence, static fact => fact.RuleId == "teams.call-controls");
    }

    [Fact]
    public void MutedListenOnlyMeetingWithChangingRemoteSpeakersCanAsk()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42, 501 })],
            [Signal("zoom.exe", 42, -26)]);
        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(
                501,
                "Zoom Meeting",
                true,
                ["Unmute", "Leave"],
                WindowId: 1001)],
            Start);
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();

        var first = Assert.Single(assembler.Assemble(
            audio,
            windowEvidence,
            RemoteSpeakerSpeech(42, 0.62),
            [],
            Start));
        var second = Assert.Single(assembler.Assemble(
            audio,
            windowEvidence,
            RemoteSpeakerSpeech(42, 0.91),
            [],
            Start.AddSeconds(3)));

        Assert.Empty(audio.Microphones);
        Assert.Null(engine.EvaluateBatch([first], Start).PromptCandidate);
        var prompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch([second], Start.AddSeconds(3)).PromptCandidate);
        Assert.Equal("zoom", prompt.Frame.ProfileId);
        Assert.DoesNotContain(prompt.Frame.Evidence, static fact => fact.Kind is
            MeetingEvidenceKind.MicrophoneInUse or
            MeetingEvidenceKind.MicrophoneSpeech or
            MeetingEvidenceKind.ConversationalAlternation);
    }

    [Fact]
    public void BrowserMediaWindowIsHardExcludedInsideTheDetectionPipeline()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);
        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "YouTube - Google Chrome", true, ["Mute", "Full screen"])],
            Start);

        var frames = new MeetingObservationAssembler(profiles).Assemble(
            audio,
            windowEvidence,
            SpeechForRoot(42),
            [],
            Start);

        var candidate = Assert.Single(frames);
        var score = new MeetingConfidenceScorer().Score(candidate);
        Assert.Equal("generic-browser", candidate.ProfileId);
        Assert.True(score.IsHardExcluded);
        Assert.Equal(0, score.Score);
    }

    [Fact]
    public void UnknownBrowserMeetingCanReachAskThroughControlsAndAttributedSpeech()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);
        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "Acme Calls", true, ["Unmute", "Leave"])],
            Start);
        var matched = Assert.Single(windowEvidence);
        Assert.Equal("generic-browser", matched.ProfileId);
        Assert.Contains(matched.Evidence, static fact =>
            fact.RuleId == "generic-browser.call-controls" &&
            fact.Kind == MeetingEvidenceKind.MeetingControls);

        var assembler = new MeetingObservationAssembler(profiles);
        var speech = SpeechForRoot(42);
        var first = Assert.Single(assembler.Assemble(audio, windowEvidence, speech, [], Start));
        var stable = Assert.Single(assembler.Assemble(audio, windowEvidence, speech, [], Start.AddSeconds(3)));
        var askStable = Assert.Single(assembler.Assemble(audio, windowEvidence, speech, [], Start.AddSeconds(6)));
        var engine = new MeetingDetectionEngine();

        Assert.Null(engine.EvaluateBatch([first], Start).PromptCandidate);
        Assert.Null(engine.EvaluateBatch([stable], Start.AddSeconds(3)).PromptCandidate);
        var prompt = engine.EvaluateBatch([askStable], Start.AddSeconds(6)).PromptCandidate;

        Assert.Equal("generic-browser", Assert.IsType<MeetingPromptCandidate>(prompt).Frame.ProfileId);
    }

    [Fact]
    public void GenericBrowserControlsWithoutActiveRenderAudioCreateNoCandidate()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            []);

        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "Acme Calls", true, ["Unmute", "Leave"])],
            Start);

        Assert.Empty(windowEvidence);
    }

    [Fact]
    public void GenericBrowserIgnoresControlLikePreviewAndFeedbackLabels()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);

        var windowEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(
                501,
                "Acme product preview",
                true,
                ["Unmute preview", "Leave feedback"],
                WindowId: 1001)],
            Start);

        Assert.Empty(windowEvidence);
    }

    [Fact]
    public void RootOnlyProfileDoesNotInspectDescendantWindows()
    {
        var rootOnly = new MeetingAppProfile(
            "root-only",
            "Root only",
            ["rootcall.exe"],
            [],
            [new MeetingWindowEvidenceRule("root-only.window", ["Root Call"], [])],
            [],
            new Dictionary<MeetingEvidenceKind, int>(),
            "generic-meeting",
            ProcessTreeBehavior: MeetingProcessTreeBehavior.RootOnly);
        var profiles = new MeetingProfileRegistry([rootOnly]);
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "rootcall.exe", new HashSet<int> { 42, 501 })],
            []);

        var childOnly = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(501, "Root Call", true, [], WindowId: 1001)],
            Start);
        var rootWindow = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(42, "Root Call", true, [], WindowId: 1002)],
            Start);

        Assert.Empty(childOnly);
        Assert.Equal("root-only", Assert.Single(rootWindow).ProfileId);
    }

    [Fact]
    public void SkippedBrowserWindowStaysSuppressedWhenItsServiceBecomesKnown()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();

        var genericEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(
                501,
                "Acme Calls",
                true,
                ["Unmute", "Leave"],
                WindowId: 1001)],
            Start);
        var genericCandidateId = Assert.Single(genericEvidence).CandidateId;

        var first = Assert.Single(assembler.Assemble(audio, genericEvidence, SpeechForRoot(42), [], Start));
        var stable = Assert.Single(assembler.Assemble(
            audio,
            genericEvidence,
            SpeechForRoot(42),
            [],
            Start.AddSeconds(3)));
        var askStable = Assert.Single(assembler.Assemble(
            audio,
            genericEvidence,
            SpeechForRoot(42),
            [],
            Start.AddSeconds(6)));
        Assert.Null(engine.EvaluateBatch([first], Start).PromptCandidate);
        Assert.Null(engine.EvaluateBatch([stable], Start.AddSeconds(3)).PromptCandidate);
        var prompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch([askStable], Start.AddSeconds(6)).PromptCandidate);
        Assert.True(engine.ResolvePrompt(prompt.Frame.CandidateId, MeetingPromptResolution.Skip, Start.AddSeconds(6)));

        var recognizedEvidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [new WindowsWindowProbeSnapshot(
                501,
                "Daily - Google Meet",
                true,
                ["Unmute", "Leave"],
                WindowId: 1001)],
            Start.AddSeconds(7));
        var recognized = Assert.Single(recognizedEvidence);
        Assert.Equal("google-meet", recognized.ProfileId);
        Assert.Equal(genericCandidateId, recognized.CandidateId);

        var recognizedFrame = Assert.Single(assembler.Assemble(
            audio,
            recognizedEvidence,
            SpeechForRoot(42),
            [],
            Start.AddSeconds(7)));
        var result = engine.EvaluateBatch([recognizedFrame], Start.AddSeconds(7));

        Assert.Null(result.PromptCandidate);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.IsSuppressed);
        Assert.Equal("google-meet", candidate.Frame.ProfileId);
    }

    [Fact]
    public void WeakKnownLandingDoesNotHideAnUnknownMeetingInAnotherBrowserWindow()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);

        var evidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [
                new WindowsWindowProbeSnapshot(
                    501,
                    "Google Meet",
                    true,
                    ["New meeting", "Join"],
                    WindowId: 1001),
                new WindowsWindowProbeSnapshot(
                    501,
                    "Acme Calls",
                    false,
                    ["Unmute", "Leave"],
                    WindowId: 1002)
            ],
            Start);

        var meetLanding = Assert.Single(evidence, static item => item.ProfileId == "google-meet");
        var fallbackMeeting = Assert.Single(evidence, static item => item.ProfileId == "generic-browser");
        Assert.NotEqual(meetLanding.CandidateId, fallbackMeeting.CandidateId);
        Assert.Contains(fallbackMeeting.Evidence, static fact =>
            fact.Kind == MeetingEvidenceKind.MeetingControls);
    }

    [Fact]
    public void MediaWindowDoesNotHardExcludeAMeetingInAnotherBrowserWindow()
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, "chrome.exe", new HashSet<int> { 42, 501 })],
            [Signal("chrome.exe", 42, -18)]);

        var evidence = WindowsWindowEvidenceMatcher.Match(
            audio,
            profiles,
            [
                new WindowsWindowProbeSnapshot(
                    501,
                    "YouTube - Google Chrome",
                    false,
                    ["Mute", "Full screen"],
                    WindowId: 2001),
                new WindowsWindowProbeSnapshot(
                    501,
                    "Acme Calls",
                    true,
                    ["Unmute", "Leave"],
                    WindowId: 2002)
            ],
            Start);

        var fallbackCandidates = evidence
            .Where(static item => item.ProfileId == "generic-browser")
            .ToArray();
        Assert.Equal(2, fallbackCandidates.Length);
        var excludedMedia = Assert.Single(fallbackCandidates, static item =>
            item.Evidence.Any(fact => fact.Kind == MeetingEvidenceKind.HardExclusion));
        var meeting = Assert.Single(fallbackCandidates, static item =>
            item.Evidence.Any(fact => fact.Kind == MeetingEvidenceKind.MeetingControls));
        Assert.NotEqual(excludedMedia.CandidateId, meeting.CandidateId);

        var assembler = new MeetingObservationAssembler(profiles);
        assembler.Assemble(audio, evidence, SpeechForRoot(42), [], Start);
        var stableFrames = assembler.Assemble(audio, evidence, SpeechForRoot(42), [], Start.AddSeconds(3));
        var scores = stableFrames.ToDictionary(
            static frame => frame.CandidateId,
            frame => new MeetingConfidenceScorer().Score(frame));
        Assert.True(scores[excludedMedia.CandidateId].IsHardExcluded);
        Assert.Equal(MeetingDecisionBand.Ask, scores[meeting.CandidateId].Band);
    }

    [Fact]
    public void DetectionSourcesContainNoManagedNetworkCalls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repositoryRoot, "src", "IsTranscribe.Core", "Detection"),
            Path.Combine(repositoryRoot, "src", "IsTranscribe.Platform.Windows")
        };
        var forbidden = new[] { "System.Net.", "HttpClient", "HttpRequestMessage", "WebRequest" };

        var violations = sourceRoots
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                  && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .Where(candidate => forbidden.Any(token => candidate.Source.Contains(token, StringComparison.Ordinal)))
            .Select(candidate => Path.GetRelativePath(repositoryRoot, candidate.Path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("vlc.exe")]
    [InlineData("spotify.exe")]
    [InlineData("game.exe")]
    [InlineData("explorer.exe")]
    [InlineData("updater.exe")]
    public void UnmatchedMediaSystemAndUpdaterProcessesCreateNoCandidate(string processName)
    {
        var profiles = new MeetingProfileRegistry();
        var audio = Platform(
            [new ObservedProcessSnapshot(42, processName, new HashSet<int> { 42 })],
            [Signal(processName, 42, -12)]);

        var frames = new MeetingObservationAssembler(profiles).Assemble(
            audio,
            [],
            SpeechForRoot(42),
            [],
            Start);

        Assert.Empty(frames);
    }

    [Fact]
    public void EightHourProductionPathBackgroundWorkloadProducesNoPrompt()
    {
        var profiles = new MeetingProfileRegistry();
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();
        const int stepSeconds = 5;
        var stepCount = (8 * 60 * 60 / stepSeconds) + 1;
        var candidateObservationCount = 0;
        var hardExclusionCount = 0;
        var emptyObservationCount = 0;
        var observedProfiles = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < stepCount; index++)
        {
            var nowUtc = Start.AddSeconds(index * stepSeconds);
            var slice = BackgroundSlice(index, profiles, nowUtc);
            var frames = assembler.Assemble(
                slice.Audio,
                slice.WindowEvidence,
                slice.Speech,
                [],
                nowUtc);
            var result = engine.EvaluateBatch(frames, nowUtc);

            Assert.Null(result.PromptCandidate);
            Assert.All(result.Candidates, static candidate => Assert.True(candidate.Score.Score < 70));
            candidateObservationCount += result.Candidates.Count;
            hardExclusionCount += result.Candidates.Count(static candidate => candidate.Score.IsHardExcluded);
            if (result.Candidates.Count == 0)
            {
                emptyObservationCount++;
            }

            foreach (var candidate in result.Candidates)
            {
                observedProfiles.Add(candidate.Frame.ProfileId);
            }
        }

        Assert.True(candidateObservationCount > 0);
        Assert.True(hardExclusionCount > 0);
        Assert.True(emptyObservationCount > 0);
        Assert.Contains("generic-browser", observedProfiles);
        Assert.Contains("google-meet", observedProfiles);
        Assert.Contains("zoom", observedProfiles);
        Assert.Contains("microsoft-teams", observedProfiles);
    }

    private static BackgroundWorkloadSlice BackgroundSlice(
        int index,
        MeetingProfileRegistry profiles,
        DateTimeOffset nowUtc)
    {
        var kind = index % 6;
        var processName = kind switch
        {
            1 => "zoom.exe",
            3 => "ms-teams.exe",
            5 => "chrome.exe",
            _ => "chrome.exe"
        };
        var rootProcessId = kind switch
        {
            1 => 84,
            3 => 126,
            _ => 42
        };
        var windowProcessId = rootProcessId + 1_000;
        var audio = Platform(
            [new ObservedProcessSnapshot(
                rootProcessId,
                processName,
                new HashSet<int> { rootProcessId, windowProcessId })],
            kind == 4 ? [] : [Signal(processName, rootProcessId, -20)]);
        var windows = kind switch
        {
            0 => new[]
            {
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "YouTube - Google Chrome",
                    true,
                    ["Mute", "Full screen"],
                    WindowId: 2_000 + kind)
            },
            1 =>
            [
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "Zoom Workplace",
                    false,
                    [],
                    WindowId: 2_000 + kind)
            ],
            2 =>
            [
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "Voice message - Chrome",
                    true,
                    ["Play"],
                    WindowId: 2_000 + kind)
            ],
            3 =>
            [
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "Microsoft Teams",
                    false,
                    [],
                    WindowId: 2_000 + kind)
            ],
            4 =>
            [
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "System notification - Chrome",
                    true,
                    ["Dismiss"],
                    WindowId: 2_000 + kind)
            ],
            _ =>
            [
                new WindowsWindowProbeSnapshot(
                    windowProcessId,
                    "Google Meet",
                    true,
                    ["New meeting", "Join"],
                    WindowId: 2_000 + kind)
            ]
        };
        var evidence = WindowsWindowEvidenceMatcher.Match(audio, profiles, windows, nowUtc);
        var speech = kind is 0 or 1 or 3 or 5
            ? RemoteSpeakerSpeech(rootProcessId, kind == 5 ? 0.95 : 0.75)
            : MeetingSpeechActivitySnapshot.Empty;
        return new BackgroundWorkloadSlice(audio, evidence, speech);
    }

    private static AudioPlatformSnapshot Platform(
        IReadOnlyList<ObservedProcessSnapshot> processes,
        IReadOnlyList<AudioSignalSnapshot> signals) => new(
            Start,
            new AudioPlatformCapabilities(true, true, "fixture"),
            [new AudioEndpointSnapshot("output", "Output", true, true)],
            [],
            processes,
            signals);

    private static AudioSignalSnapshot Signal(
        string processName,
        int rootProcessId,
        double levelDbfs) => new(
            Start,
            processName,
            rootProcessId,
            "AudioSessionStateActive",
            levelDbfs,
            "output",
            IsWatchedProcess: true,
            IsProcessTreeMatch: true);

    private static MeetingSpeechActivitySnapshot SpeechForRoot(int rootProcessId) => new(
        Start,
        new Dictionary<int, MeetingSpeechActivitySummary>
        {
            [rootProcessId] = new(0.9, true, TimeSpan.FromSeconds(4), 0.7)
        },
        MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>());

    private static MeetingSpeechActivitySnapshot RemoteSpeakerSpeech(
        int rootProcessId,
        double probability) => new(
            Start,
            new Dictionary<int, MeetingSpeechActivitySummary>
            {
                [rootProcessId] = new(probability, true, TimeSpan.FromSeconds(4), 0.7)
            },
            MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>());

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output path.");
    }

    private sealed record BackgroundWorkloadSlice(
        AudioPlatformSnapshot Audio,
        IReadOnlyList<MeetingWindowEvidenceSnapshot> WindowEvidence,
        MeetingSpeechActivitySnapshot Speech);
}
