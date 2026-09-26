using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Settings;

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Derives capture eligibility exclusively from metadata and privacy-reduced UI evidence.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public static class MeetingAudioObservationEligibilityPolicy
{
    public static IReadOnlyList<MeetingAudioObservationCandidate> FindEligibleCandidates(
        AudioPlatformSnapshot audioPlatform,
        IReadOnlyList<MeetingWindowEvidenceSnapshot> windowEvidence,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> applicationPreferences)
    {
        ArgumentNullException.ThrowIfNull(audioPlatform);
        ArgumentNullException.ThrowIfNull(windowEvidence);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(applicationPreferences);

        var ignoredProfiles = applicationPreferences
            .Where(static preference => preference.Policy == MeetingApplicationPolicy.Ignore)
            .Select(static preference => preference.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligible = new Dictionary<string, MeetingAudioObservationCandidate>(StringComparer.Ordinal);

        foreach (var process in audioPlatform.Processes)
        {
            if (!HasActiveRenderMetadata(audioPlatform, process.RootProcessId))
            {
                continue;
            }

            foreach (var profile in profiles.MatchProcess(process.ProcessName).Where(profile =>
                         profile.MatchesDedicatedProcess(process.ProcessName)
                         && !ignoredProfiles.Contains(profile.Id)))
            {
                var candidateId = $"{profile.Id}:desktop:{process.RootProcessId}";
                eligible[candidateId] = new MeetingAudioObservationCandidate(
                    candidateId,
                    process.RootProcessId);
            }
        }

        foreach (var window in windowEvidence.Where(window =>
                     window.Context == MeetingCandidateContext.BrowserService
                     && !ignoredProfiles.Contains(window.ProfileId)
                     && HasMeetingSpecificEvidence(window.Evidence)))
        {
            eligible[window.CandidateId] = new MeetingAudioObservationCandidate(
                window.CandidateId,
                window.RootProcessId);
        }

        return eligible.Values
            .OrderBy(static candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasActiveRenderMetadata(AudioPlatformSnapshot audioPlatform, int rootProcessId) =>
        audioPlatform.Signals.Any(signal =>
            signal.RootProcessId == rootProcessId
            && IsActive(signal.SessionState));

    private static bool HasMeetingSpecificEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) =>
        !evidence.Any(static fact => fact.Kind == MeetingEvidenceKind.HardExclusion)
        && evidence.Any(static fact =>
            (fact.Kind is MeetingEvidenceKind.MeetingWindow or MeetingEvidenceKind.MeetingControls)
            && fact.NormalizedStrength > 0);

    private static bool IsActive(string state) =>
        string.Equals(state, "active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase);
}

public sealed record MeetingAudioObservationCandidate(
    string CandidateId,
    int RootProcessId);
