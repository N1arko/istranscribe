namespace IsTranscribe.Core.Detection;

/// <summary>
/// One normalized, privacy-safe view of a logical meeting candidate.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// </remarks>
public sealed record MeetingObservationFrame(
    DateTimeOffset ObservedAtUtc,
    string CandidateId,
    string ProfileId,
    string DisplayName,
    MeetingCandidateContext Context,
    int RootProcessId,
    string ProcessName,
    IReadOnlyList<MeetingEvidenceFact> Evidence,
    IReadOnlyDictionary<MeetingEvidenceKind, int>? EvidenceWeightOverrides = null)
{
    public MeetingObservationFrame Canonicalize() => this with
    {
        CandidateId = CandidateId.Trim(),
        ProfileId = ProfileId.Trim().ToLowerInvariant(),
        DisplayName = DisplayName.Trim(),
        ProcessName = NormalizeProcessName(ProcessName),
        Evidence = Evidence ?? [],
        EvidenceWeightOverrides = EvidenceWeightOverrides
            ?? new Dictionary<MeetingEvidenceKind, int>()
    };

    private static string NormalizeProcessName(string processName)
    {
        var normalized = processName.Trim().ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}.exe";
    }
}

public enum MeetingCandidateContext
{
    DedicatedApplication,
    BrowserService,
    UserDefinedApplication
}

public enum MeetingDecisionBand
{
    Ignored,
    Suspected,
    Ask
}

public sealed record MeetingScoreResult(
    string CandidateId,
    string ProfileId,
    int Score,
    MeetingDecisionBand Band,
    IReadOnlyList<MeetingScoreContribution> Contributions,
    IReadOnlyList<string> DecisionReasons,
    bool IsHardExcluded);
