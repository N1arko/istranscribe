using IsTranscribe.Application.Runtime;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Bounded degraded-audio scenarios that complement, without changing, the canonical matrix.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class EdgeCaseScenarioCatalog
{
    public const string UnavailableMicrophoneId = "review-microphone-disconnected";

    public static ApplicationRuntimeSnapshot RecordingWithoutMicrophone(string theme, string language)
    {
        var canonical = CanonicalScenarioCatalog.MainScenarios
            .Single(static scenario => scenario.Id == "recording")
            .Snapshot;
        return CanonicalScenarioCatalog.WithThemeAndLanguage(canonical, theme, language) with
        {
            ActiveMeeting = canonical.ActiveMeeting! with { HasMicrophone = false },
            Capability = new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Degraded,
                SupportsProcessOutputCapture: true,
                Summary: "microphone capture unavailable")
            {
                Issue = RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
                HasActiveOutput = true,
                HasActiveMicrophone = true
            }
        };
    }

    public static ApplicationRuntimeSnapshot SelectedMicrophoneUnavailable(
        string theme,
        string language,
        bool onboardingCompleted,
        RuntimeCapabilityIssue issue = RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable)
    {
        var settings = SelectedMicrophoneUnavailableSettings(theme, language, onboardingCompleted);
        return CanonicalScenarioCatalog.WithThemeAndLanguage(
            CanonicalScenarioCatalog.MainScenarios[0].Snapshot,
            theme,
            language) with
        {
            Capability = new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Degraded,
                SupportsProcessOutputCapture: true,
                Summary: "microphone unavailable")
            {
                Issue = issue,
                HasActiveOutput = true,
                HasActiveMicrophone = true
            },
            UserSettings = settings,
            AvailableMicrophones = CanonicalScenarioCatalog.Microphones
        };
    }

    public static ApplicationRuntimeSnapshot LegacyTranscriptArtifact(string theme, string language)
    {
        var canonical = CanonicalScenarioCatalog.MainScenarios
            .Single(static scenario => scenario.Id == "ready")
            .Snapshot;
        var recent = canonical.RecentRecordings.ToArray();
        recent[0] = recent[0] with
        {
            LegacyTranscriptMarkdownPath = @"C:\Recordings\legacy-meeting.md",
            LegacyTranscriptJsonPath = @"C:\Recordings\legacy-meeting.json"
        };
        return CanonicalScenarioCatalog.WithThemeAndLanguage(canonical, theme, language) with
        {
            RecentRecordings = recent
        };
    }

    public static RuntimeUserSettingsSnapshot SelectedMicrophoneUnavailableSettings(
        string theme,
        string language,
        bool onboardingCompleted) => CanonicalScenarioCatalog.Settings(
            theme: theme,
            language: language) with
        {
            OnboardingCompleted = onboardingCompleted,
            FollowSystemDefaultMicrophone = false,
            MicrophoneDeviceId = UnavailableMicrophoneId
        };
}
