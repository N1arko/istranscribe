using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// </summary>
public sealed class MeetingDetectionEngineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public void AskScoreNeedsThreeSecondsOfStability()
    {
        var engine = new MeetingDetectionEngine();

        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start).PromptCandidate);
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(2.9)).PromptCandidate);
        var result = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3));

        Assert.Equal("zoom:1", result.PromptCandidate?.Frame.CandidateId);
    }

    [Fact]
    public void HysteresisKeepsAskStabilityAcrossSmallScoreDrop()
    {
        var engine = new MeetingDetectionEngine();

        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        engine.EvaluateBatch([SustainFloorFrame("zoom:1")], Start.AddSeconds(1));
        var result = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3));

        Assert.NotNull(result.PromptCandidate);
    }

    [Fact]
    public void DropBelowAskExitResetsStabilityWindow()
    {
        var engine = new MeetingDetectionEngine();

        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(2));
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate);
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(5.9)).PromptCandidate);
        Assert.NotNull(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(6)).PromptCandidate);
    }

    [Fact]
    public void SkipSuppressesLogicalCandidateUntilTwentySecondsBelowEligibility()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate!;
        Assert.True(engine.ResolvePrompt(prompt.Frame.CandidateId, MeetingPromptResolution.Skip, Start.AddSeconds(3)));

        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(10)).PromptCandidate);
        engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(11));
        engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(30.9));
        engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(31));
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(31.1)).PromptCandidate);
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(34)).PromptCandidate);
        Assert.NotNull(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(34.1)).PromptCandidate);
    }

    [Fact]
    public void RepeatedSkipAcrossAnEightHourMeetingProducesOnePromptMaximum()
    {
        var engine = new MeetingDetectionEngine();
        var frame = HighConfidenceFrame("zoom:long-meeting");
        engine.EvaluateBatch([frame], Start);
        var prompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch([frame], Start.AddSeconds(3)).PromptCandidate);
        Assert.True(engine.ResolvePrompt(
            prompt.Frame.CandidateId,
            MeetingPromptResolution.Skip,
            Start.AddSeconds(3)));

        for (var minute = 1; minute <= 8 * 60; minute++)
        {
            var result = engine.EvaluateBatch([frame], Start.AddMinutes(minute));
            Assert.Null(result.PromptCandidate);
            Assert.True(Assert.Single(result.Candidates).IsSuppressed);
        }
    }

    [Theory]
    [InlineData(MeetingPromptResolution.Timeout)]
    [InlineData(MeetingPromptResolution.Closed)]
    public void SafePromptResolutionUsesSkipSuppression(MeetingPromptResolution resolution)
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate!;

        engine.ResolvePrompt(prompt.Frame.CandidateId, resolution, Start.AddSeconds(3));
        var next = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(30));

        var candidate = Assert.Single(next.Candidates);
        Assert.True(candidate.IsSuppressed);
        Assert.Null(next.PromptCandidate);
    }

    [Fact]
    public void MeetingUiBoundaryEndReleasesSkipForTheNextMeeting()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate!;
        Assert.True(engine.ResolvePrompt(prompt.Frame.CandidateId, MeetingPromptResolution.Skip, Start.AddSeconds(3)));

        var firstWeakObservation = engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(4));
        Assert.True(Assert.Single(firstWeakObservation.Candidates).IsSuppressed);
        var boundaryEnded = engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(6));
        Assert.False(Assert.Single(boundaryEnded.Candidates).IsSuppressed);
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(6.1)).PromptCandidate);
        Assert.NotNull(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(9.1)).PromptCandidate);
    }

    [Fact]
    public void OneTransientUiEvidenceDropKeepsSkipSuppression()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate!;
        engine.ResolvePrompt(prompt.Frame.CandidateId, MeetingPromptResolution.Skip, Start.AddSeconds(3));

        engine.EvaluateBatch([LowFrame("zoom:1")], Start.AddSeconds(4));
        var recovered = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(5));

        Assert.True(Assert.Single(recovered.Candidates).IsSuppressed);
        Assert.Null(recovered.PromptCandidate);
    }

    [Fact]
    public void CandidateEndedResolutionDoesNotSuppressANewBoundary()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate!;

        Assert.True(engine.ResolvePrompt(
            prompt.Frame.CandidateId,
            MeetingPromptResolution.CandidateEnded,
            Start.AddSeconds(4)));
        Assert.Null(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(5)).PromptCandidate);
        Assert.NotNull(engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(8)).PromptCandidate);
    }

    [Fact]
    public void OnlyOnePromptCanBeActiveAtATime()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch(
            [HighConfidenceFrame("zoom:1"), HighConfidenceFrame("teams:2", "microsoft-teams")],
            Start);

        var first = engine.EvaluateBatch(
            [HighConfidenceFrame("zoom:1"), HighConfidenceFrame("teams:2", "microsoft-teams")],
            Start.AddSeconds(3));
        var whileOpen = engine.EvaluateBatch(
            [HighConfidenceFrame("zoom:1"), HighConfidenceFrame("teams:2", "microsoft-teams")],
            Start.AddSeconds(4));

        Assert.NotNull(first.PromptCandidate);
        Assert.Null(whileOpen.PromptCandidate);
        Assert.NotNull(whileOpen.ActivePromptCandidateId);
    }

    [Fact]
    public void RecordedCandidateBlocksOtherPromptsUntilTheActiveSessionEnds()
    {
        var engine = new MeetingDetectionEngine();
        var frames = new[]
        {
            HighConfidenceFrame("zoom:1"),
            HighConfidenceFrame("teams:2", "microsoft-teams")
        };
        engine.EvaluateBatch(frames, Start);
        var firstPrompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch(frames, Start.AddSeconds(3)).PromptCandidate);
        Assert.True(engine.ResolvePrompt(
            firstPrompt.Frame.CandidateId,
            MeetingPromptResolution.Record,
            Start.AddSeconds(3)));

        var whileRecording = engine.EvaluateBatch(frames, Start.AddSeconds(4));

        Assert.Null(whileRecording.PromptCandidate);
        Assert.True(engine.EndActiveSession(firstPrompt.Frame.CandidateId, Start.AddSeconds(5)));
        var afterRecording = engine.EvaluateBatch(frames, Start.AddSeconds(5));
        var nextPrompt = Assert.IsType<MeetingPromptCandidate>(afterRecording.PromptCandidate);
        Assert.NotEqual(firstPrompt.Frame.CandidateId, nextPrompt.Frame.CandidateId);
    }

    [Fact]
    public void ActiveSessionKeepsSuppressionAcrossLongMeetingUiBoundaryLoss()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        var prompt = Assert.IsType<MeetingPromptCandidate>(
            engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3)).PromptCandidate);
        Assert.True(engine.ResolvePrompt(
            prompt.Frame.CandidateId,
            MeetingPromptResolution.Record,
            Start.AddSeconds(3)));

        engine.EvaluateBatch([PresentationFrame("zoom:1")], Start.AddSeconds(4));
        var duringPresentation = engine.EvaluateBatch(
            [PresentationFrame("zoom:1")],
            Start.AddSeconds(30));

        Assert.True(Assert.Single(duringPresentation.Candidates).IsSuppressed);
        var recovered = engine.EvaluateBatch(
            [HighConfidenceFrame("zoom:1")],
            Start.AddSeconds(31));
        Assert.True(Assert.Single(recovered.Candidates).IsSuppressed);
        Assert.Null(recovered.PromptCandidate);

        Assert.True(engine.EndActiveSession(prompt.Frame.CandidateId, Start.AddSeconds(32)));
        Assert.Null(engine.EvaluateBatch(
            [HighConfidenceFrame("zoom:1")],
            Start.AddSeconds(36)).PromptCandidate);
    }

    [Fact]
    public void MissingActiveCandidateRequestsPromptCancellation()
    {
        var engine = new MeetingDetectionEngine();
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3));

        var result = engine.EvaluateBatch([], Start.AddSeconds(4));

        Assert.True(result.ShouldCancelActivePrompt);
    }

    [Fact]
    public void ShadowModeReportsWouldPromptWithoutOpeningLivePrompt()
    {
        var engine = new MeetingDetectionEngine(mode: MeetingDetectionMode.Shadow);
        engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start);

        var result = engine.EvaluateBatch([HighConfidenceFrame("zoom:1")], Start.AddSeconds(3));

        Assert.Null(result.PromptCandidate);
        Assert.NotNull(result.ShadowPromptCandidate);
        Assert.Null(result.ActivePromptCandidateId);
    }

    private static MeetingObservationFrame HighConfidenceFrame(
        string candidateId,
        string profileId = "zoom") => Frame(
            candidateId,
            profileId,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, $"{profileId}.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, $"{profileId}.meeting-window"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"));

    private static MeetingObservationFrame SustainFloorFrame(string candidateId) => Frame(
        candidateId,
        "zoom",
        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render", 0.4),
        new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "zoom.meeting-window", 0.75),
        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"));

    private static MeetingObservationFrame LowFrame(string candidateId) => Frame(
        candidateId,
        "zoom",
        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"));

    private static MeetingObservationFrame PresentationFrame(string candidateId) => Frame(
        candidateId,
        "zoom",
        new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
        new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render", 0.6),
        new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"));

    private static MeetingObservationFrame Frame(
        string candidateId,
        string profileId,
        params MeetingEvidenceFact[] evidence) => new(
            Start,
            candidateId,
            profileId,
            profileId,
            MeetingCandidateContext.DedicatedApplication,
            RootProcessId: candidateId.GetHashCode(StringComparison.Ordinal),
            ProcessName: $"{profileId}.exe",
            evidence);
}
