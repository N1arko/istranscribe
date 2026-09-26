namespace IsTranscribe.Core.Detection;

/// <summary>
/// Data-first profile contract for dedicated clients and browser meeting services.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles
/// </remarks>
public sealed record MeetingAppProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> DedicatedProcessNames,
    IReadOnlyList<string> BrowserProcessNames,
    IReadOnlyList<MeetingWindowEvidenceRule> WindowRules,
    IReadOnlyList<MeetingControlEvidenceRule> ControlRules,
    IReadOnlyDictionary<MeetingEvidenceKind, int> EvidenceWeightOverrides,
    string IconKey,
    int SchemaVersion = 1,
    MeetingProcessTreeBehavior ProcessTreeBehavior = MeetingProcessTreeBehavior.RootAndDescendants,
    MeetingCaptureSourcePreference CaptureSourcePreference = MeetingCaptureSourcePreference.ProcessOutputPreferred,
    bool AllowControlOnlyBrowserMatch = false,
    bool IsFallbackProfile = false,
    IReadOnlyList<MeetingWindowEvidenceRule>? HardExclusionRules = null)
{
    public bool MatchesDedicatedProcess(string processName) =>
        DedicatedProcessNames.Contains(NormalizeProcessName(processName), StringComparer.OrdinalIgnoreCase);

    public bool MatchesBrowserProcess(string processName) =>
        BrowserProcessNames.Contains(NormalizeProcessName(processName), StringComparer.OrdinalIgnoreCase);

    public static MeetingAppProfile CreateUserDefined(
        string id,
        string displayName,
        string processName) => new(
            NormalizeUserProfileId(id),
            displayName.Trim(),
            [NormalizeProcessName(processName)],
            [],
            [],
            [],
            new Dictionary<MeetingEvidenceKind, int>(),
            IconKey: "generic-meeting");

    private static string NormalizeUserProfileId(string id)
    {
        var normalized = id.Trim().ToLowerInvariant();
        return normalized.StartsWith("user:", StringComparison.Ordinal)
            ? normalized
            : $"user:{normalized}";
    }

    internal static string NormalizeProcessName(string processName)
    {
        var normalized = processName.Trim().ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}.exe";
    }
}

public enum MeetingProcessTreeBehavior
{
    RootOnly,
    RootAndDescendants
}

public enum MeetingCaptureSourcePreference
{
    ProcessOutputPreferred,
    DeviceLoopbackPreferred
}

public sealed record MeetingWindowEvidenceRule(
    string RuleId,
    IReadOnlyList<string> AnyPhrases,
    IReadOnlyList<string> ExcludedPhrases,
    double Strength = 1)
{
    public bool Matches(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || ExcludedPhrases.Any(phrase => value.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return AnyPhrases.Any(phrase => value.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record MeetingControlEvidenceRule(
    string RuleId,
    IReadOnlyList<IReadOnlyList<string>> RequiredControlGroups,
    int MinimumMatchedGroups,
    MeetingControlNameMatchMode NameMatchMode = MeetingControlNameMatchMode.Contains)
{
    public double MatchStrength(IEnumerable<string> accessibleNames)
    {
        var names = accessibleNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (names.Length == 0 || RequiredControlGroups.Count == 0)
        {
            return 0;
        }

        var matched = RequiredControlGroups.Count(group =>
            group.Any(phrase => names.Any(name => NameMatches(name, phrase))));
        return matched < MinimumMatchedGroups
            ? 0
            : Math.Clamp((double)matched / RequiredControlGroups.Count, 0, 1);
    }

    private bool NameMatches(string accessibleName, string phrase) => NameMatchMode switch
    {
        MeetingControlNameMatchMode.Exact => string.Equals(
            accessibleName.Trim(),
            phrase.Trim(),
            StringComparison.OrdinalIgnoreCase),
        _ => accessibleName.Contains(phrase, StringComparison.OrdinalIgnoreCase)
    };
}

public enum MeetingControlNameMatchMode
{
    Contains,
    Exact
}
