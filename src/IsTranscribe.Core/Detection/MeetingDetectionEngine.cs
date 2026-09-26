namespace IsTranscribe.Core.Detection;

/// <summary>
/// Temporal meeting-candidate lifecycle with hysteresis, one-prompt gating and skip suppression.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </remarks>
public sealed class MeetingDetectionEngine(
    MeetingConfidenceScorer? scorer = null,
    MeetingDetectionTimingPolicy? timing = null,
    MeetingDetectionMode mode = MeetingDetectionMode.Live)
{
    private static readonly TimeSpan MeetingUiBoundaryReleaseDebounce = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly MeetingConfidenceScorer _scorer = scorer ?? new MeetingConfidenceScorer();
    private readonly MeetingDetectionTimingPolicy _timing = timing ?? MeetingDetectionTimingPolicy.Default;
    private readonly Dictionary<string, CandidateTracker> _trackers = new(StringComparer.Ordinal);
    private string? _activePromptCandidateId;
    private string? _activeSessionCandidateId;

    public MeetingDetectionBatchResult EvaluateBatch(
        IReadOnlyList<MeetingObservationFrame> frames,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(frames);

        lock (_gate)
        {
            var scoredFrames = frames
                .Select(static frame => frame.Canonicalize())
                .Where(static frame => !string.IsNullOrWhiteSpace(frame.CandidateId))
                .Select(frame => new ScoredFrame(frame, _scorer.Score(frame)))
                .GroupBy(static item => item.Frame.CandidateId, StringComparer.Ordinal)
                .Select(static group => group
                    .OrderByDescending(static item => item.Score.Score)
                    .First())
                .ToArray();

            var observedIds = scoredFrames
                .Select(static item => item.Frame.CandidateId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in scoredFrames)
            {
                UpdateObservedTracker(item, nowUtc);
            }

            foreach (var tracker in _trackers.Values.Where(tracker => !observedIds.Contains(tracker.CandidateId)).ToArray())
            {
                UpdateMissingTracker(tracker, nowUtc);
            }

            RemoveStaleTrackers(nowUtc);

            var snapshots = scoredFrames
                .Select(item => ToSnapshot(_trackers[item.Frame.CandidateId], item, nowUtc))
                .OrderByDescending(static snapshot => snapshot.Score.Score)
                .ThenBy(static snapshot => snapshot.CandidateId, StringComparer.Ordinal)
                .ToArray();

            var shouldCancelActivePrompt = _activePromptCandidateId is not null
                && (!observedIds.Contains(_activePromptCandidateId)
                    || _trackers[_activePromptCandidateId].LastScore?.Score < _timing.AskExitThreshold
                    || _trackers[_activePromptCandidateId].LastScore?.IsHardExcluded == true);

            MeetingPromptCandidate? promptCandidate = null;
            MeetingPromptCandidate? shadowPromptCandidate = null;
            if (_activePromptCandidateId is null && _activeSessionCandidateId is null)
            {
                var selected = snapshots.FirstOrDefault(static snapshot => snapshot.IsPromptEligible);
                if (selected is not null)
                {
                    var tracker = _trackers[selected.CandidateId];
                    tracker.PromptEmitted = true;
                    var candidate = new MeetingPromptCandidate(selected.Frame, selected.Score);
                    if (mode == MeetingDetectionMode.Shadow)
                    {
                        shadowPromptCandidate = candidate;
                    }
                    else
                    {
                        _activePromptCandidateId = selected.CandidateId;
                        promptCandidate = candidate;
                    }
                }
            }

            return new MeetingDetectionBatchResult(
                snapshots,
                promptCandidate,
                shadowPromptCandidate,
                _activePromptCandidateId,
                shouldCancelActivePrompt);
        }
    }

    public bool ResolvePrompt(
        string candidateId,
        MeetingPromptResolution resolution,
        DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!string.Equals(_activePromptCandidateId, candidateId, StringComparison.Ordinal)
                || !_trackers.TryGetValue(candidateId, out var tracker))
            {
                return false;
            }

            if (resolution == MeetingPromptResolution.CandidateEnded)
            {
                ReleaseCandidateLifecycle(tracker);
                tracker.LastDecisionAtUtc = nowUtc;
                _activePromptCandidateId = null;
                return true;
            }

            tracker.IsSuppressed = true;
            tracker.SuppressionReason = resolution.ToString().ToLowerInvariant();
            tracker.SuppressionReleaseSinceUtc = null;
            tracker.AskStableSinceUtc = null;
            tracker.PromptEmitted = true;
            tracker.LastDecisionAtUtc = nowUtc;
            if (resolution == MeetingPromptResolution.Record)
            {
                _activeSessionCandidateId = candidateId;
            }

            _activePromptCandidateId = null;
            return true;
        }
    }

    public bool EndActiveSession(string candidateId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!string.Equals(_activeSessionCandidateId, candidateId, StringComparison.Ordinal))
            {
                return false;
            }

            _activeSessionCandidateId = null;
            if (_trackers.TryGetValue(candidateId, out var tracker))
            {
                tracker.LastDecisionAtUtc = nowUtc;
                TryReleaseEndedMeetingUiBoundary(tracker, nowUtc);
            }

            return true;
        }
    }

    private void UpdateObservedTracker(ScoredFrame item, DateTimeOffset nowUtc)
    {
        if (!_trackers.TryGetValue(item.Frame.CandidateId, out var tracker))
        {
            tracker = new CandidateTracker(item.Frame.CandidateId);
            _trackers[item.Frame.CandidateId] = tracker;
        }

        var hasMeetingUiBoundary = HasMeetingUiBoundary(item.Frame);
        if (hasMeetingUiBoundary)
        {
            tracker.HadMeetingUiBoundary = true;
            tracker.MeetingUiBoundaryLostSinceUtc = null;
        }
        else if (tracker.HadMeetingUiBoundary)
        {
            tracker.MeetingUiBoundaryLostSinceUtc ??= nowUtc;
            TryReleaseEndedMeetingUiBoundary(tracker, nowUtc);
        }

        tracker.LastFrame = item.Frame;
        tracker.LastScore = item.Score;
        tracker.LastSeenAtUtc = nowUtc;
        tracker.MissingSinceUtc = null;

        if (item.Score.Score >= _timing.AskEntryThreshold)
        {
            tracker.AskStableSinceUtc ??= nowUtc;
        }
        else if (item.Score.Score < _timing.AskExitThreshold || item.Score.IsHardExcluded)
        {
            tracker.AskStableSinceUtc = null;
        }

        tracker.IsSuspected = item.Score.Score >= _timing.SuspectedEntryThreshold
            || (tracker.IsSuspected && item.Score.Score >= _timing.SuspectedExitThreshold);

        UpdateReleaseTimers(tracker, item.Score.Score < _timing.AskExitThreshold, nowUtc);
    }

    private void UpdateMissingTracker(CandidateTracker tracker, DateTimeOffset nowUtc)
    {
        tracker.MissingSinceUtc ??= nowUtc;
        if (tracker.HadMeetingUiBoundary)
        {
            tracker.MeetingUiBoundaryLostSinceUtc ??= nowUtc;
            TryReleaseEndedMeetingUiBoundary(tracker, nowUtc);
        }

        tracker.AskStableSinceUtc = null;
        tracker.IsSuspected = false;
        UpdateReleaseTimers(tracker, belowAskEligibility: true, nowUtc);
    }

    private static bool HasMeetingUiBoundary(MeetingObservationFrame frame) =>
        frame.Evidence.Any(static fact =>
            (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
            || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));

    private static void ReleaseCandidateLifecycle(CandidateTracker tracker)
    {
        tracker.IsSuppressed = false;
        tracker.SuppressionReason = null;
        tracker.SuppressionReleaseSinceUtc = null;
        tracker.PromptEmitted = false;
        tracker.PromptResetSinceUtc = null;
        tracker.AskStableSinceUtc = null;
        tracker.MeetingUiBoundaryLostSinceUtc = null;
    }

    private void TryReleaseEndedMeetingUiBoundary(CandidateTracker tracker, DateTimeOffset nowUtc)
    {
        if (string.Equals(_activeSessionCandidateId, tracker.CandidateId, StringComparison.Ordinal)
            || tracker.MeetingUiBoundaryLostSinceUtc is not { } boundaryLostSinceUtc
            || nowUtc - boundaryLostSinceUtc < MeetingUiBoundaryReleaseDebounce)
        {
            return;
        }

        ReleaseCandidateLifecycle(tracker);
        tracker.HadMeetingUiBoundary = false;
    }

    private void UpdateReleaseTimers(
        CandidateTracker tracker,
        bool belowAskEligibility,
        DateTimeOffset nowUtc)
    {
        if (string.Equals(_activeSessionCandidateId, tracker.CandidateId, StringComparison.Ordinal))
        {
            tracker.SuppressionReleaseSinceUtc = null;
            tracker.PromptResetSinceUtc = null;
            return;
        }

        if (!belowAskEligibility)
        {
            tracker.SuppressionReleaseSinceUtc = null;
            tracker.PromptResetSinceUtc = null;
            return;
        }

        if (tracker.IsSuppressed)
        {
            tracker.SuppressionReleaseSinceUtc ??= nowUtc;
            if (nowUtc - tracker.SuppressionReleaseSinceUtc.Value >= _timing.SuppressionReleaseWindow)
            {
                tracker.IsSuppressed = false;
                tracker.SuppressionReason = null;
                tracker.SuppressionReleaseSinceUtc = null;
                tracker.PromptEmitted = false;
            }

            return;
        }

        if (tracker.PromptEmitted && !string.Equals(_activePromptCandidateId, tracker.CandidateId, StringComparison.Ordinal))
        {
            tracker.PromptResetSinceUtc ??= nowUtc;
            if (nowUtc - tracker.PromptResetSinceUtc.Value >= _timing.SuppressionReleaseWindow)
            {
                tracker.PromptEmitted = false;
                tracker.PromptResetSinceUtc = null;
            }
        }
    }

    private void RemoveStaleTrackers(DateTimeOffset nowUtc)
    {
        foreach (var tracker in _trackers.Values.ToArray())
        {
            if (string.Equals(_activePromptCandidateId, tracker.CandidateId, StringComparison.Ordinal)
                || string.Equals(_activeSessionCandidateId, tracker.CandidateId, StringComparison.Ordinal)
                || nowUtc - tracker.LastSeenAtUtc < _timing.StaleCandidateRetention)
            {
                continue;
            }

            _trackers.Remove(tracker.CandidateId);
        }
    }

    private MeetingDetectionCandidateSnapshot ToSnapshot(
        CandidateTracker tracker,
        ScoredFrame item,
        DateTimeOffset nowUtc)
    {
        var stableFor = tracker.AskStableSinceUtc.HasValue
            ? nowUtc - tracker.AskStableSinceUtc.Value
            : TimeSpan.Zero;
        var eligible = !tracker.IsSuppressed
            && !tracker.PromptEmitted
            && item.Score.Band == MeetingDecisionBand.Ask
            && stableFor >= _timing.AskStabilityWindow;

        return new MeetingDetectionCandidateSnapshot(
            item.Frame.CandidateId,
            item.Frame,
            item.Score,
            tracker.IsSuspected,
            tracker.IsSuppressed,
            tracker.SuppressionReason,
            stableFor,
            eligible);
    }

    private sealed record ScoredFrame(MeetingObservationFrame Frame, MeetingScoreResult Score);

    private sealed class CandidateTracker(string candidateId)
    {
        public string CandidateId { get; } = candidateId;

        public MeetingObservationFrame? LastFrame { get; set; }

        public MeetingScoreResult? LastScore { get; set; }

        public DateTimeOffset LastSeenAtUtc { get; set; }

        public DateTimeOffset? MissingSinceUtc { get; set; }

        public DateTimeOffset? AskStableSinceUtc { get; set; }

        public bool IsSuspected { get; set; }

        public bool IsSuppressed { get; set; }

        public bool HadMeetingUiBoundary { get; set; }

        public DateTimeOffset? MeetingUiBoundaryLostSinceUtc { get; set; }

        public string? SuppressionReason { get; set; }

        public DateTimeOffset? SuppressionReleaseSinceUtc { get; set; }

        public bool PromptEmitted { get; set; }

        public DateTimeOffset? PromptResetSinceUtc { get; set; }

        public DateTimeOffset? LastDecisionAtUtc { get; set; }
    }
}

public sealed record MeetingDetectionTimingPolicy(
    int SuspectedEntryThreshold,
    int SuspectedExitThreshold,
    int AskEntryThreshold,
    int AskExitThreshold,
    TimeSpan AskStabilityWindow,
    TimeSpan SuppressionReleaseWindow,
    TimeSpan StaleCandidateRetention)
{
    public static MeetingDetectionTimingPolicy Default { get; } = new(
        SuspectedEntryThreshold: 40,
        SuspectedExitThreshold: 32,
        AskEntryThreshold: 70,
        AskExitThreshold: 65,
        AskStabilityWindow: TimeSpan.FromSeconds(3),
        SuppressionReleaseWindow: TimeSpan.FromSeconds(20),
        StaleCandidateRetention: TimeSpan.FromMinutes(10));
}

public enum MeetingDetectionMode
{
    Live,
    Shadow
}

public enum MeetingPromptResolution
{
    Record,
    Skip,
    IgnoreApplication,
    Timeout,
    Closed,
    CandidateEnded
}

public sealed record MeetingPromptCandidate(
    MeetingObservationFrame Frame,
    MeetingScoreResult Score);

public sealed record MeetingDetectionCandidateSnapshot(
    string CandidateId,
    MeetingObservationFrame Frame,
    MeetingScoreResult Score,
    bool IsSuspected,
    bool IsSuppressed,
    string? SuppressionReason,
    TimeSpan AskStableFor,
    bool IsPromptEligible);

public sealed record MeetingDetectionBatchResult(
    IReadOnlyList<MeetingDetectionCandidateSnapshot> Candidates,
    MeetingPromptCandidate? PromptCandidate,
    MeetingPromptCandidate? ShadowPromptCandidate,
    string? ActivePromptCandidateId,
    bool ShouldCancelActivePrompt);
