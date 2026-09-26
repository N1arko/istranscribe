using IsTranscribe.App.ManualControls;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.App.Configuration;

public sealed record ShellConfigurationSnapshot(
    HostCapabilitySnapshot Capability,
    ManualControlSnapshot ManualControl,
    LocalAppPaths Paths,
    ApplicationSettings Settings,
    AppSecrets Secrets,
    IReadOnlyList<AppRuleRecord> AppRules,
    AudioDeviceInventorySnapshot Devices,
    IReadOnlyList<RunningAudioProcessCandidate> RunningProcesses,
    IReadOnlyList<MeetingSessionListItem> RecentSessions);

public sealed record WizardCompletionRequest(
    string UiLanguage,
    string RecordingsFolder,
    string TranscriptsFolder,
    bool DetermineDevicesAutomatically,
    string? OutputDeviceId,
    bool FollowSystemDefaultOutput,
    string? MicrophoneDeviceId,
    bool FollowSystemDefaultMic,
    string FireworksApiKey,
    string RecordingMode,
    bool SuggestAppsAutomatically,
    IReadOnlyList<AppRuleRecord> SelectedApplications);

public sealed record ApplicationSectionSaveRequest(
    string AutoDiscoveryPolicy,
    IReadOnlyList<string> IgnoredAppSuggestions,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<AppRuleRecord> AppRules);

public sealed record TranscriptionSectionSaveRequest(
    TranscriptionSettings Settings,
    string? FireworksApiKey,
    bool RemoveApiKey);

public readonly record struct WindowOperationResult(bool Success, string? Message = null)
{
    public static WindowOperationResult Ok(string? message = null) => new(true, message);

    public static WindowOperationResult Fail(string message) => new(false, message);
}
