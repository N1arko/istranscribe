using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity
/// </summary>
public sealed class ActiveMeetingContinuityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-03T12:00:00Z");

    [Fact]
    public void SustainedRenderSpeechKeepsSessionActiveWhenMeetingWindowTemporarilyDisappears()
    {
        var candidate = Candidate(
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render", 0.6),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"));

        Assert.True(candidate.Score.Score < MeetingDetectionTimingPolicy.Default.AskExitThreshold);
        Assert.True(ApplicationRuntime.HasActiveMeetingContinuity(candidate));
    }

    [Fact]
    public void IdleMeetingProcessDoesNotKeepSessionActiveAfterMeetingEnds()
    {
        var candidate = Candidate(
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"));

        Assert.False(ApplicationRuntime.HasActiveMeetingContinuity(candidate));
    }

    [Fact]
    public void HardExclusionEndsContinuityEvenWhenSpeechRemainsAttributed()
    {
        var candidate = Candidate(
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.HardExclusion, "zoom.media"));

        Assert.False(ApplicationRuntime.HasActiveMeetingContinuity(candidate));
    }

    private static MeetingDetectionCandidateSnapshot Candidate(params MeetingEvidenceFact[] evidence)
    {
        var frame = new MeetingObservationFrame(
            Now,
            "zoom:desktop:42",
            "zoom",
            "Zoom",
            MeetingCandidateContext.DedicatedApplication,
            42,
            "zoom.exe",
            evidence);
        return new MeetingDetectionCandidateSnapshot(
            frame.CandidateId,
            frame,
            new MeetingConfidenceScorer().Score(frame),
            IsSuspected: true,
            IsSuppressed: true,
            SuppressionReason: "record",
            AskStableFor: TimeSpan.Zero,
            IsPromptEligible: false);
    }
}
