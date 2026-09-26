using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </summary>
public sealed class MeetingDetectionReplayRunnerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public void CuratedNegativeReplayProducesNoAskDecision()
    {
        var steps = new List<MeetingDetectionReplayStep>();
        for (var second = 0; second <= 8; second++)
        {
            steps.Add(new MeetingDetectionReplayStep(
                Start.AddSeconds(second),
                [
                    Frame(
                        "browser:youtube",
                        "google-meet",
                        MeetingCandidateContext.BrowserService,
                        new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "browser.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.9)),
                    Frame(
                        "zoom:idle",
                        "zoom",
                        MeetingCandidateContext.DedicatedApplication,
                        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"))
                ]));
        }

        var replay = new MeetingDetectionReplayRunner().Run(steps);

        Assert.Empty(replay.WouldPromptCandidates);
    }

    [Fact]
    public void StableMeetingReplayProducesOneShadowPrompt()
    {
        var steps = Enumerable.Range(0, 6)
            .Select(second => new MeetingDetectionReplayStep(
                Start.AddSeconds(second),
                [
                    Frame(
                        "zoom:desktop",
                        "zoom",
                        MeetingCandidateContext.DedicatedApplication,
                        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.8),
                        new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, "zoom.controls"))
                ]))
            .ToArray();

        var replay = new MeetingDetectionReplayRunner().Run(steps);

        var prompt = Assert.Single(replay.WouldPromptCandidates);
        Assert.Equal("zoom:desktop", prompt.Frame.CandidateId);
    }

    [Fact]
    public void TypedDiagnosticRecordsRoundTripThroughTheProductionScorerAndReplay()
    {
        var scorer = new MeetingConfidenceScorer();
        var template = new MeetingDetectionReplayRecord(
            MeetingDetectionReplayRecord.CurrentSchemaVersion,
            Start,
            "zoom:typed-replay",
            "zoom",
            "dedicated_application",
            [
                new("known_application_identity", "zoom.identity", 1, "profile"),
                new("render_session_active", "audio.active", 1, "audio-session"),
                new("render_speech", "speech.render", 0.8, "speech-envelope"),
                new("meeting_controls", "zoom.call-controls", 1, "windows-uia")
            ],
            new Dictionary<string, int>(),
            RecordedScore: 0,
            RecordedBand: "ignored",
            Suppressed: false);
        var recomputed = scorer.Score(template.ToFrame());
        var records = Enumerable.Range(0, 5)
            .Select(second => template with
            {
                ObservedAtUtc = Start.AddSeconds(second),
                RecordedScore = recomputed.Score,
                RecordedBand = "ask"
            })
            .ToArray();

        Assert.All(records, record =>
        {
            var score = scorer.Score(record.ToFrame());
            Assert.Equal(record.RecordedScore, score.Score);
            Assert.Equal(record.RecordedBand, score.Band.ToString().ToLowerInvariant());
        });
        var replay = new MeetingDetectionReplayRunner().RunRecords(records);

        Assert.Single(replay.WouldPromptCandidates);
    }

    [Theory]
    [InlineData("zoom", MeetingCandidateContext.DedicatedApplication)]
    [InlineData("microsoft-teams", MeetingCandidateContext.DedicatedApplication)]
    [InlineData("google-meet", MeetingCandidateContext.BrowserService)]
    [InlineData("yandex-telemost", MeetingCandidateContext.DedicatedApplication)]
    [InlineData("kontur-talk", MeetingCandidateContext.DedicatedApplication)]
    public void EveryInitialProfileHasAPositiveDeterministicFixture(
        string profileId,
        MeetingCandidateContext context)
    {
        var identityKind = context == MeetingCandidateContext.BrowserService
            ? MeetingEvidenceKind.BrowserServiceIdentity
            : MeetingEvidenceKind.KnownApplicationIdentity;
        var steps = Enumerable.Range(0, 5)
            .Select(second => new MeetingDetectionReplayStep(
                Start.AddSeconds(second),
                [
                    Frame(
                        $"{profileId}:fixture",
                        profileId,
                        context,
                        new MeetingEvidenceFact(identityKind, $"{profileId}.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.8),
                        new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, $"{profileId}.call-controls"))
                ]))
            .ToArray();

        var replay = new MeetingDetectionReplayRunner().Run(steps);

        var prompt = Assert.Single(replay.WouldPromptCandidates);
        Assert.Equal(profileId, prompt.Frame.ProfileId);
    }

    [Fact]
    public void SoundShorterThanThreeSecondsNeverProducesPrompt()
    {
        var highEvidence = Frame(
            "zoom:short-sound",
            "zoom",
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, "zoom.call-controls"));
        MeetingDetectionReplayStep[] steps =
        [
            new(Start, [highEvidence]),
            new(Start.AddSeconds(1), [highEvidence]),
            new(Start.AddSeconds(2.9), [highEvidence]),
            new(Start.AddSeconds(3), [])
        ];

        var replay = new MeetingDetectionReplayRunner().Run(steps);

        Assert.Empty(replay.WouldPromptCandidates);
    }

    [Fact]
    public void EightHourMixedBackgroundReplayProducesZeroFalsePrompts()
    {
        const int stepSeconds = 5;
        var stepCount = (8 * 60 * 60 / stepSeconds) + 1;
        var steps = Enumerable.Range(0, stepCount)
            .Select(index => new MeetingDetectionReplayStep(
                Start.AddSeconds(index * stepSeconds),
                [
                    Frame(
                        "browser:background-media",
                        "google-meet",
                        MeetingCandidateContext.BrowserService,
                        new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "browser.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "speech.render", 0.9),
                        new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "google-meet.brand-window", 0.15),
                        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")),
                    Frame(
                        "zoom:idle",
                        "zoom",
                        MeetingCandidateContext.DedicatedApplication,
                        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")),
                    Frame(
                        "teams:notification",
                        "microsoft-teams",
                        MeetingCandidateContext.DedicatedApplication,
                        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "teams.identity"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
                        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"))
                ]))
            .ToArray();

        var replay = new MeetingDetectionReplayRunner().Run(steps);

        Assert.Empty(replay.WouldPromptCandidates);
        Assert.Equal(stepCount, replay.Decisions.Count);
    }

    private static MeetingObservationFrame Frame(
        string candidateId,
        string profileId,
        MeetingCandidateContext context,
        params MeetingEvidenceFact[] evidence) => new(
            Start,
            candidateId,
            profileId,
            profileId,
            context,
            RootProcessId: 42,
            ProcessName: context == MeetingCandidateContext.BrowserService ? "chrome.exe" : "zoom.exe",
            evidence);
}
