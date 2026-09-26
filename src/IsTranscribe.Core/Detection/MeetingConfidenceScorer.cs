namespace IsTranscribe.Core.Detection;

/// <summary>
/// Explainable confidence model shared by live detection, shadow mode and replay fixtures.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </remarks>
public sealed class MeetingConfidenceScorer(MeetingScoringPolicy? policy = null)
{
    private readonly MeetingScoringPolicy _policy = policy ?? MeetingScoringPolicy.Default;

    public MeetingScoreResult Score(MeetingObservationFrame sourceFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        var frame = sourceFrame.Canonicalize();

        var hardExclusion = frame.Evidence.FirstOrDefault(static fact =>
            fact.Kind == MeetingEvidenceKind.HardExclusion && fact.NormalizedStrength > 0);
        if (hardExclusion is not null)
        {
            return new MeetingScoreResult(
                frame.CandidateId,
                frame.ProfileId,
                Score: 0,
                MeetingDecisionBand.Ignored,
                Contributions:
                [
                    CreateContribution(frame, hardExclusion)
                ],
                DecisionReasons: [$"hard_exclusion:{hardExclusion.RuleId}"],
                IsHardExcluded: true);
        }

        var contributions = frame.Evidence
            .GroupBy(static fact => fact.Kind)
            .Select(static group => group
                .OrderByDescending(static fact => fact.NormalizedStrength)
                .ThenBy(static fact => fact.RuleId, StringComparer.Ordinal)
                .First())
            .Select(fact => CreateContribution(frame, fact))
            .Where(static contribution => contribution.ScoreDelta != 0)
            .OrderByDescending(static contribution => Math.Abs(contribution.ScoreDelta))
            .ThenBy(static contribution => contribution.RuleId, StringComparer.Ordinal)
            .ToArray();

        var score = Math.Clamp(contributions.Sum(static contribution => contribution.ScoreDelta), 0, 100);
        var reasons = new List<string>();

        if (frame.Context == MeetingCandidateContext.BrowserService
            && !HasMeetingSpecificBrowserEvidence(frame.Evidence)
            && score >= _policy.AskThreshold)
        {
            score = _policy.AskThreshold - 1;
            reasons.Add("browser_requires_meeting_specific_evidence");
        }

        var band = score switch
        {
            var value when value >= _policy.AskThreshold => MeetingDecisionBand.Ask,
            var value when value >= _policy.SuspectedThreshold => MeetingDecisionBand.Suspected,
            _ => MeetingDecisionBand.Ignored
        };
        reasons.Add($"band:{band.ToString().ToLowerInvariant()}");

        return new MeetingScoreResult(
            frame.CandidateId,
            frame.ProfileId,
            score,
            band,
            contributions,
            reasons,
            IsHardExcluded: false);
    }

    private MeetingScoreContribution CreateContribution(
        MeetingObservationFrame frame,
        MeetingEvidenceFact fact)
    {
        var weight = frame.EvidenceWeightOverrides?.TryGetValue(fact.Kind, out var profileWeight) == true
            ? profileWeight
            : _policy.Weights[fact.Kind];
        var delta = (int)Math.Round(weight * fact.NormalizedStrength, MidpointRounding.AwayFromZero);
        return new MeetingScoreContribution(
            fact.Kind,
            fact.RuleId,
            fact.ProviderId,
            fact.NormalizedStrength,
            weight,
            delta);
    }

    private static bool HasMeetingSpecificBrowserEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) =>
        evidence.Any(static fact =>
            (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
            || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));
}

public sealed record MeetingScoringPolicy(
    int SuspectedThreshold,
    int AskThreshold,
    IReadOnlyDictionary<MeetingEvidenceKind, int> Weights)
{
    public static MeetingScoringPolicy Default { get; } = new(
        SuspectedThreshold: 40,
        AskThreshold: 70,
        Weights: new Dictionary<MeetingEvidenceKind, int>
        {
            [MeetingEvidenceKind.KnownApplicationIdentity] = 22,
            [MeetingEvidenceKind.UserDefinedApplicationIdentity] = 16,
            [MeetingEvidenceKind.BrowserServiceIdentity] = 4,
            [MeetingEvidenceKind.RenderSessionActive] = 8,
            [MeetingEvidenceKind.RenderSpeech] = 30,
            [MeetingEvidenceKind.MicrophoneInUse] = 8,
            [MeetingEvidenceKind.MicrophoneSpeech] = 15,
            [MeetingEvidenceKind.ConversationalAlternation] = 18,
            [MeetingEvidenceKind.MeetingWindow] = 26,
            [MeetingEvidenceKind.MeetingControls] = 40,
            [MeetingEvidenceKind.ForegroundWindow] = 3,
            [MeetingEvidenceKind.ProcessStable] = 5,
            [MeetingEvidenceKind.MediaPlayback] = -30,
            [MeetingEvidenceKind.NotificationLike] = -45,
            [MeetingEvidenceKind.HardExclusion] = -100
        });
}
