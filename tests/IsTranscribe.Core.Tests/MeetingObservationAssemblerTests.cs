using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Settings;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// </summary>
public sealed class MeetingObservationAssemblerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public void BrowserAudioWithoutMeetingWindowCreatesNoCandidate()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());

        var frames = assembler.Assemble(
            Audio("chrome.exe", rootProcessId: 42, activeAudio: true),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now);

        Assert.Empty(frames);
    }

    [Fact]
    public void DedicatedAppIdentityCreatesLowConfidenceFrame()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());

        var frame = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", rootProcessId: 42, activeAudio: false),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now));
        var score = new MeetingConfidenceScorer().Score(frame);

        Assert.Equal("zoom:desktop:42", frame.CandidateId);
        Assert.Equal(MeetingDecisionBand.Ignored, score.Band);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("AudioSessionStateActive")]
    public void PlatformActiveRenderStatesProduceSharedAudioEvidence(string sessionState)
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var audio = Audio("zoom.exe", rootProcessId: 42, activeAudio: true) with
        {
            Signals = [Audio("zoom.exe", rootProcessId: 42, activeAudio: true).Signals[0] with
            {
                SessionState = sessionState
            }]
        };

        var frame = Assert.Single(assembler.Assemble(
            audio,
            StrongZoomWindow(42),
            SpeechForRoot(42),
            [],
            Now));

        Assert.Contains(frame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSessionActive);
        Assert.Contains(frame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSpeech);
    }

    [Fact]
    public void DedicatedProcessRestartCreatesANewLogicalCandidateBoundary()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());

        var first = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", rootProcessId: 42, activeAudio: false),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now));
        var restarted = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", rootProcessId: 84, activeAudio: false),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now.AddSeconds(1)));

        Assert.Equal("zoom:desktop:42", first.CandidateId);
        Assert.Equal("zoom:desktop:84", restarted.CandidateId);
    }

    [Fact]
    public void ConcurrentInstancesOfOneProfileRemainSeparateCandidates()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var audio = Audio("zoom.exe", rootProcessId: 42, activeAudio: false) with
        {
            Processes =
            [
                new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 }),
                new ObservedProcessSnapshot(84, "zoom.exe", new HashSet<int> { 84 })
            ]
        };

        var frames = assembler.Assemble(
            audio,
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now);

        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, static frame => frame.CandidateId == "zoom:desktop:42");
        Assert.Contains(frames, static frame => frame.CandidateId == "zoom:desktop:84");
    }

    [Fact]
    public void SkippedMeetingDoesNotSuppressARestartedProcessBoundary()
    {
        var profiles = new MeetingProfileRegistry();
        var assembler = new MeetingObservationAssembler(profiles);
        var engine = new MeetingDetectionEngine();
        var firstWindows = StrongZoomWindow(rootProcessId: 42);
        var firstSpeech = SpeechForRoot(42);
        var first = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 42, activeAudio: true),
            firstWindows,
            firstSpeech,
            [],
            Now));
        var firstStable = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 42, activeAudio: true),
            firstWindows,
            firstSpeech,
            [],
            Now.AddSeconds(3)));
        engine.EvaluateBatch([first], Now);
        var firstPrompt = engine.EvaluateBatch([firstStable], Now.AddSeconds(3)).PromptCandidate!;
        Assert.True(engine.ResolvePrompt(
            firstPrompt.Frame.CandidateId,
            MeetingPromptResolution.Skip,
            Now.AddSeconds(3)));

        var restartedWindows = StrongZoomWindow(rootProcessId: 84);
        var restartedSpeech = SpeechForRoot(84);
        var restarted = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 84, activeAudio: true),
            restartedWindows,
            restartedSpeech,
            [],
            Now.AddSeconds(4)));
        var restartedStable = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 84, activeAudio: true),
            restartedWindows,
            restartedSpeech,
            [],
            Now.AddSeconds(7)));

        Assert.Null(engine.EvaluateBatch([restarted], Now.AddSeconds(4)).PromptCandidate);
        var restartedPrompt = engine.EvaluateBatch([restartedStable], Now.AddSeconds(7)).PromptCandidate;
        Assert.Equal("zoom:desktop:84", restartedPrompt?.Frame.CandidateId);
    }

    [Fact]
    public void BrowserWindowAndSustainedRemoteSpeechCreateAskEvidence()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var windows = new[]
        {
            new MeetingWindowEvidenceSnapshot(
                Now,
                "google-meet:browser",
                "google-meet",
                "Google Meet",
                MeetingCandidateContext.BrowserService,
                RootProcessId: 42,
                ProcessName: "chrome.exe",
                Evidence:
                [
                    new MeetingEvidenceFact(
                        MeetingEvidenceKind.MeetingWindow,
                        "google-meet.meeting-window",
                        ProviderId: "windows-window")
                ])
        };
        var speech = new MeetingSpeechActivitySnapshot(
            Now,
            new Dictionary<int, MeetingSpeechActivitySummary>
            {
                [42] = new(0.9, true, TimeSpan.FromSeconds(4), 0.6)
            },
            MeetingSpeechActivitySummary.Empty,
            new Dictionary<int, double>());

        assembler.Assemble(
            Audio("chrome.exe", rootProcessId: 42, activeAudio: true),
            windows,
            speech,
            [],
            Now);
        var frame = Assert.Single(assembler.Assemble(
            Audio("chrome.exe", rootProcessId: 42, activeAudio: true),
            windows,
            speech,
            [],
            Now.AddSeconds(3)));
        var score = new MeetingConfidenceScorer().Score(frame);

        Assert.Equal(MeetingDecisionBand.Ask, score.Band);
    }

    [Fact]
    public void IgnorePreferenceRemovesProfileCandidate()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var preferences = new[]
        {
            new MeetingApplicationPreference("zoom", "Zoom", MeetingApplicationPolicy.Ignore)
        };

        var frames = assembler.Assemble(
            Audio("zoom.exe", rootProcessId: 42, activeAudio: true),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            preferences,
            Now);

        Assert.Empty(frames);
    }

    [Fact]
    public void StableProcessEvidenceAppearsOnlyAfterThreeSeconds()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var first = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 42, activeAudio: true),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now));
        var stable = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 42, activeAudio: true),
            [],
            MeetingSpeechActivitySnapshot.Empty,
            [],
            Now.AddSeconds(3)));

        Assert.DoesNotContain(first.Evidence, static fact => fact.Kind == MeetingEvidenceKind.ProcessStable);
        Assert.Contains(stable.Evidence, static fact => fact.Kind == MeetingEvidenceKind.ProcessStable);
    }

    [Fact]
    public void UserDefinedDedicatedAppCanReachAskThroughConversationEvidence()
    {
        var custom = MeetingAppProfile.CreateUserDefined("my-call", "My Call", "my-call.exe");
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry([custom]));
        var speech = new MeetingSpeechActivitySnapshot(
            Now,
            new Dictionary<int, MeetingSpeechActivitySummary>
            {
                [42] = new(0.9, true, TimeSpan.FromSeconds(4), 0.7)
            },
            new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(4), 0.6),
            new Dictionary<int, double> { [42] = 1 });

        assembler.Assemble(
            Audio("my-call.exe", 42, activeAudio: true),
            [],
            speech,
            [],
            Now);
        var frame = Assert.Single(assembler.Assemble(
            Audio("my-call.exe", 42, activeAudio: true),
            [],
            speech,
            [],
            Now.AddSeconds(3)));
        var score = new MeetingConfidenceScorer().Score(frame);

        Assert.Equal(MeetingCandidateContext.UserDefinedApplication, frame.Context);
        Assert.Contains(frame.Evidence, static fact =>
            fact.Kind == MeetingEvidenceKind.UserDefinedApplicationIdentity);
        Assert.Equal(MeetingDecisionBand.Ask, score.Band);
    }

    [Fact]
    public void GlobalMicrophoneDoesNotBoostAnIdleDedicatedApplication()
    {
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry());
        var speech = new MeetingSpeechActivitySnapshot(
            Now,
            new Dictionary<int, MeetingSpeechActivitySummary>(),
            new MeetingSpeechActivitySummary(0.9, true, TimeSpan.FromSeconds(4), 0.8),
            new Dictionary<int, double> { [42] = 1 });

        var frame = Assert.Single(assembler.Assemble(
            Audio("zoom.exe", 42, activeAudio: false),
            [],
            speech,
            [],
            Now));

        Assert.DoesNotContain(frame.Evidence, static fact => fact.Kind is
            MeetingEvidenceKind.MicrophoneInUse or
            MeetingEvidenceKind.MicrophoneSpeech or
            MeetingEvidenceKind.ConversationalAlternation);
        Assert.Equal(MeetingDecisionBand.Ignored, new MeetingConfidenceScorer().Score(frame).Band);
    }

    [Fact]
    public void RootOnlyProfileDoesNotReceiveDescendantAudioOrSpeechEvidence()
    {
        var rootOnly = new MeetingAppProfile(
            "root-only",
            "Root only",
            ["root-call.exe"],
            [],
            [],
            [],
            new Dictionary<MeetingEvidenceKind, int>(),
            "generic-meeting",
            ProcessTreeBehavior: MeetingProcessTreeBehavior.RootOnly);
        var assembler = new MeetingObservationAssembler(new MeetingProfileRegistry([rootOnly]));
        var speech = SpeechForRoot(42);
        var descendantSpeech = speech with
        {
            RenderUsesDescendantProcessByRootProcessId = new Dictionary<int, bool> { [42] = true }
        };
        var rootSpeech = speech with
        {
            RenderUsesDescendantProcessByRootProcessId = new Dictionary<int, bool> { [42] = false }
        };
        var descendantAudio = Audio("root-call.exe", 42, activeAudio: false) with
        {
            Processes = [new ObservedProcessSnapshot(42, "root-call.exe", new HashSet<int> { 42, 501 })],
            Signals =
            [
                new AudioSignalSnapshot(
                    Now,
                    "root-call.exe",
                    42,
                    "AudioSessionStateActive",
                    -20,
                    "output",
                    IsWatchedProcess: true,
                    IsProcessTreeMatch: true)
            ]
        };
        var rootAudio = descendantAudio with
        {
            Signals = [descendantAudio.Signals[0] with { IsProcessTreeMatch = false }]
        };
        var mixedAudio = descendantAudio with
        {
            Signals =
            [
                descendantAudio.Signals[0] with
                {
                    SignalLevelDbfs = -40,
                    IsProcessTreeMatch = false
                },
                descendantAudio.Signals[0]
            ]
        };

        var descendantFrame = Assert.Single(assembler.Assemble(
            descendantAudio,
            [],
            descendantSpeech,
            [],
            Now));
        var rootFrame = Assert.Single(assembler.Assemble(
            rootAudio,
            [],
            rootSpeech,
            [],
            Now.AddSeconds(1)));
        var mixedFrame = Assert.Single(assembler.Assemble(
            mixedAudio,
            [],
            descendantSpeech,
            [],
            Now.AddSeconds(2)));

        Assert.DoesNotContain(descendantFrame.Evidence, static fact => fact.Kind is
            MeetingEvidenceKind.RenderSessionActive or MeetingEvidenceKind.RenderSpeech);
        Assert.Contains(rootFrame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSessionActive);
        Assert.Contains(rootFrame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSpeech);
        Assert.Contains(mixedFrame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSessionActive);
        Assert.DoesNotContain(mixedFrame.Evidence, static fact => fact.Kind == MeetingEvidenceKind.RenderSpeech);
    }

    private static AudioPlatformSnapshot Audio(
        string processName,
        int rootProcessId,
        bool activeAudio) => new(
            Now,
            new AudioPlatformCapabilities(true, true, "test"),
            [],
            [],
            [new ObservedProcessSnapshot(rootProcessId, processName, new HashSet<int> { rootProcessId })],
            activeAudio
                ?
                [
                    new AudioSignalSnapshot(
                        Now,
                        processName,
                        rootProcessId,
                        "AudioSessionStateActive",
                        SignalLevelDbfs: -24,
                        OutputDeviceId: "output",
                        IsWatchedProcess: true,
                        IsProcessTreeMatch: true)
                ]
                : []);

    private static IReadOnlyList<MeetingWindowEvidenceSnapshot> StrongZoomWindow(int rootProcessId) =>
    [
        new MeetingWindowEvidenceSnapshot(
            Now,
            $"zoom:desktop:{rootProcessId}",
            "zoom",
            "Zoom",
            MeetingCandidateContext.DedicatedApplication,
            rootProcessId,
            "zoom.exe",
            [
                new MeetingEvidenceFact(
                    MeetingEvidenceKind.MeetingControls,
                    "zoom.call-controls",
                    ProviderId: "fixture")
            ])
    ];

    private static MeetingSpeechActivitySnapshot SpeechForRoot(int rootProcessId) => new(
        Now,
        new Dictionary<int, MeetingSpeechActivitySummary>
        {
            [rootProcessId] = new(0.9, true, TimeSpan.FromSeconds(4), 0.7)
        },
        MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>());
}
