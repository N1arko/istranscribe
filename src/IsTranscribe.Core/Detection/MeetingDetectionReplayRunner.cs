namespace IsTranscribe.Core.Detection;

/// <summary>
/// Replays privacy-reduced observation frames through the same scorer and temporal lifecycle as live detection.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// </remarks>
public sealed class MeetingDetectionReplayRunner(
    MeetingConfidenceScorer? scorer = null,
    MeetingDetectionTimingPolicy? timing = null)
{
    private readonly MeetingConfidenceScorer _scorer = scorer ?? new MeetingConfidenceScorer();
    private readonly MeetingDetectionTimingPolicy _timing = timing ?? MeetingDetectionTimingPolicy.Default;

    public MeetingDetectionReplayResult Run(IReadOnlyList<MeetingDetectionReplayStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var engine = new MeetingDetectionEngine(
            _scorer,
            _timing,
            MeetingDetectionMode.Shadow);
        var decisions = new List<MeetingDetectionReplayDecision>(steps.Count);
        foreach (var step in steps.OrderBy(static step => step.ObservedAtUtc))
        {
            var batch = engine.EvaluateBatch(step.Frames, step.ObservedAtUtc);
            decisions.Add(new MeetingDetectionReplayDecision(
                step.ObservedAtUtc,
                batch.Candidates,
                batch.ShadowPromptCandidate));
        }

        return new MeetingDetectionReplayResult(
            decisions,
            decisions
                .Where(static decision => decision.WouldPrompt is not null)
                .Select(static decision => decision.WouldPrompt!)
                .ToArray());
    }

    public MeetingDetectionReplayResult RunRecords(
        IReadOnlyList<MeetingDetectionReplayRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var steps = records
            .GroupBy(static record => record.ObservedAtUtc)
            .Select(static group => new MeetingDetectionReplayStep(
                group.Key,
                group.Select(static record => record.ToFrame()).ToArray()))
            .ToArray();
        return Run(steps);
    }
}

public sealed record MeetingDetectionReplayStep(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<MeetingObservationFrame> Frames);

public sealed record MeetingDetectionReplayDecision(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<MeetingDetectionCandidateSnapshot> Candidates,
    MeetingPromptCandidate? WouldPrompt);

public sealed record MeetingDetectionReplayResult(
    IReadOnlyList<MeetingDetectionReplayDecision> Decisions,
    IReadOnlyList<MeetingPromptCandidate> WouldPromptCandidates);
