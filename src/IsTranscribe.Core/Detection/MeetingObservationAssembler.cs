using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Settings;

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Combines independent process, audio, speech and window facts without letting providers make decisions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// </remarks>
public sealed class MeetingObservationAssembler(MeetingProfileRegistry profiles)
{
    private readonly MeetingProfileRegistry _profiles = profiles;
    private readonly Dictionary<string, DateTimeOffset> _candidateFirstSeen = new(StringComparer.Ordinal);

    public IReadOnlyList<MeetingObservationFrame> Assemble(
        AudioPlatformSnapshot audioPlatform,
        IReadOnlyList<MeetingWindowEvidenceSnapshot> windowEvidence,
        MeetingSpeechActivitySnapshot speechActivity,
        IReadOnlyList<MeetingApplicationPreference> applicationPreferences,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(audioPlatform);
        ArgumentNullException.ThrowIfNull(windowEvidence);
        ArgumentNullException.ThrowIfNull(speechActivity);
        ArgumentNullException.ThrowIfNull(applicationPreferences);

        var ignoredProfiles = applicationPreferences
            .Where(static preference => preference.Policy == MeetingApplicationPolicy.Ignore)
            .Select(static preference => preference.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processByRootId = audioPlatform.Processes
            .ToDictionary(static process => process.RootProcessId);
        var seeds = new Dictionary<string, CandidateSeed>(StringComparer.Ordinal);

        foreach (var window in windowEvidence.Where(window => !ignoredProfiles.Contains(window.ProfileId)))
        {
            if (_profiles.FindById(window.ProfileId) is not { } profile)
            {
                continue;
            }

            seeds[window.CandidateId] = new CandidateSeed(
                window.CandidateId,
                profile,
                window.Context,
                window.RootProcessId,
                window.ProcessName,
                window.Evidence);
        }

        foreach (var process in audioPlatform.Processes)
        {
            foreach (var profile in _profiles.MatchProcess(process.ProcessName))
            {
                if (ignoredProfiles.Contains(profile.Id)
                    || !profile.MatchesDedicatedProcess(process.ProcessName))
                {
                    continue;
                }

                var candidateId = $"{profile.Id}:desktop:{process.RootProcessId}";
                if (!seeds.ContainsKey(candidateId))
                {
                    seeds[candidateId] = new CandidateSeed(
                        candidateId,
                        profile,
                        profile.Id.StartsWith("user:", StringComparison.OrdinalIgnoreCase)
                            ? MeetingCandidateContext.UserDefinedApplication
                            : MeetingCandidateContext.DedicatedApplication,
                        process.RootProcessId,
                        process.ProcessName,
                        []);
                }
            }
        }

        var frames = new List<MeetingObservationFrame>(seeds.Count);
        foreach (var seed in seeds.Values)
        {
            if (!processByRootId.TryGetValue(seed.RootProcessId, out var process))
            {
                continue;
            }

            _candidateFirstSeen.TryAdd(seed.CandidateId, nowUtc);
            var evidence = new List<MeetingEvidenceFact>(seed.WindowEvidence)
            {
                new(
                    seed.Context switch
                    {
                        MeetingCandidateContext.BrowserService => MeetingEvidenceKind.BrowserServiceIdentity,
                        MeetingCandidateContext.UserDefinedApplication => MeetingEvidenceKind.UserDefinedApplicationIdentity,
                        _ => MeetingEvidenceKind.KnownApplicationIdentity
                    },
                    $"{seed.Profile.Id}.identity",
                    ProviderId: "profile")
            };

            var renderSignals = audioPlatform.Signals
                .Where(signal => signal.RootProcessId == seed.RootProcessId
                    && (seed.Profile.ProcessTreeBehavior == MeetingProcessTreeBehavior.RootAndDescendants
                        || !signal.IsProcessTreeMatch))
                .ToArray();
            var hasEligibleActiveRenderSignal = renderSignals.Any(static signal =>
                IsActiveRenderState(signal.SessionState));
            if (hasEligibleActiveRenderSignal)
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.RenderSessionActive,
                    "audio.render-session-active",
                    ProviderId: "audio-session"));
            }

            var renderSpeech = hasEligibleActiveRenderSignal
                && speechActivity.RenderByRootProcessId.TryGetValue(
                    seed.RootProcessId,
                    out var attributedRenderSpeech)
                ? attributedRenderSpeech
                : null;
            var renderOriginEligible = seed.Profile.ProcessTreeBehavior == MeetingProcessTreeBehavior.RootAndDescendants
                || (speechActivity.RenderUsesDescendantProcessByRootProcessId?.TryGetValue(
                        seed.RootProcessId,
                        out var usesDescendantProcess) == true
                    && !usesDescendantProcess);
            if (renderOriginEligible && renderSpeech?.IsSustainedSpeech == true)
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.RenderSpeech,
                    "speech.render-envelope",
                    renderSpeech.SpeechProbability,
                    "speech-envelope"));
            }

            var hasMeetingUiEvidence = evidence.Any(static fact =>
                (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
                || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));
            var canAssociateMicrophone = hasMeetingUiEvidence
                || (renderOriginEligible && renderSpeech?.IsSustainedSpeech == true);
            if (canAssociateMicrophone && speechActivity.Microphone.ActiveRatio > 0.1)
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.MicrophoneInUse,
                    "audio.microphone-active",
                    Math.Clamp(speechActivity.Microphone.ActiveRatio, 0, 1),
                    "microphone-meter"));
            }

            if (canAssociateMicrophone && speechActivity.Microphone.IsSustainedSpeech)
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.MicrophoneSpeech,
                    "speech.microphone-envelope",
                    speechActivity.Microphone.SpeechProbability,
                    "speech-envelope"));
            }

            if (canAssociateMicrophone
                && renderOriginEligible
                && speechActivity.ConversationalAlternationByRootProcessId.TryGetValue(
                    seed.RootProcessId,
                    out var alternationStrength)
                && alternationStrength > 0)
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.ConversationalAlternation,
                    "speech.turn-taking",
                    alternationStrength,
                    "speech-envelope"));
            }

            if (nowUtc - _candidateFirstSeen[seed.CandidateId] >= TimeSpan.FromSeconds(3))
            {
                evidence.Add(new MeetingEvidenceFact(
                    MeetingEvidenceKind.ProcessStable,
                    "process.stable-3s",
                    ProviderId: "process"));
            }

            frames.Add(new MeetingObservationFrame(
                nowUtc,
                seed.CandidateId,
                seed.Profile.Id,
                seed.Profile.DisplayName,
                seed.Context,
                process.RootProcessId,
                process.ProcessName,
                evidence,
                seed.Profile.EvidenceWeightOverrides));
        }

        var activeCandidateIds = frames
            .Select(static frame => frame.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _candidateFirstSeen.Keys.Where(id => !activeCandidateIds.Contains(id)).ToArray())
        {
            _candidateFirstSeen.Remove(staleId);
        }

        return frames;
    }

    private static bool IsActiveRenderState(string state) =>
        string.Equals(state, "active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase);

    private sealed record CandidateSeed(
        string CandidateId,
        MeetingAppProfile Profile,
        MeetingCandidateContext Context,
        int RootProcessId,
        string ProcessName,
        IReadOnlyList<MeetingEvidenceFact> WindowEvidence);
}
