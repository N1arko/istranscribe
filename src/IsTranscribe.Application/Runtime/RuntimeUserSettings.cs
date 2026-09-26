using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Runtime;

/// <summary>
/// Platform-neutral user-facing settings projected into every runtime snapshot.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed record RuntimeUserSettingsSnapshot(
    bool OnboardingCompleted,
    bool ServiceEnabled,
    bool Autostart,
    bool Notifications,
    string Language,
    string Theme,
    string? MicrophoneDeviceId,
    bool FollowSystemDefaultMicrophone,
    string? RecordingsFolder,
    IReadOnlyList<MeetingApplicationPreference> Applications)
{
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    public RuntimeTranscriptionSettingsSnapshot Transcription { get; init; } =
        RuntimeTranscriptionSettingsSnapshot.Initial;

    public static RuntimeUserSettingsSnapshot Initial { get; } = new(
        OnboardingCompleted: false,
        ServiceEnabled: false,
        Autostart: ReleaseV2Settings.Default.Autostart,
        Notifications: ReleaseV2Settings.Default.Notifications,
        Language: ReleaseV2Settings.Default.Language,
        Theme: ReleaseV2Settings.Default.Theme,
        MicrophoneDeviceId: ReleaseV2Settings.Default.MicrophoneDeviceId,
        FollowSystemDefaultMicrophone: ReleaseV2Settings.Default.FollowSystemDefaultMicrophone,
        RecordingsFolder: ReleaseV2Settings.Default.RecordingsFolder,
        Applications: ReleaseV2Settings.Default.Applications);

    public RuntimeUserSettingsUpdate ToUpdate() => new(
        ServiceEnabled,
        Autostart,
        Notifications,
        Language,
        Theme,
        MicrophoneDeviceId,
        FollowSystemDefaultMicrophone,
        RecordingsFolder,
        Applications);
}

/// <summary>
/// Complete set of editable release-v2 choices submitted by setup or settings UI.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed record RuntimeUserSettingsUpdate(
    bool ServiceEnabled,
    bool Autostart,
    bool Notifications,
    string Language,
    string Theme,
    string? MicrophoneDeviceId,
    bool FollowSystemDefaultMicrophone,
    string? RecordingsFolder,
    IReadOnlyList<MeetingApplicationPreference> Applications);

/// <summary>
/// Credential-free transcription configuration exposed to the desktop shell.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed record RuntimeTranscriptionSettingsSnapshot(
    string? SelectedEngineId,
    bool AutomaticEnabled,
    string Language,
    bool RequireZeroDataRetention,
    IReadOnlyList<RuntimeTranscriptionEngineSnapshot> Engines)
{
    public static RuntimeTranscriptionSettingsSnapshot Initial { get; } = new(
        SelectedEngineId: null,
        AutomaticEnabled: false,
        Language: "auto",
        RequireZeroDataRetention: true,
        Engines: []);
}

public sealed record RuntimeTranscriptionEngineSnapshot(
    string EngineId,
    string DisplayName,
    TranscriptionExecutionKind ExecutionKind,
    bool RequiresNetwork,
    string PrivacyDisclosure,
    Uri? PolicyUri,
    bool HasCredential,
    string? SelectedModelId,
    IReadOnlyList<TranscriptionModelCapability> Models,
    bool SupportsModelDiscovery,
    bool DisclosureAccepted,
    string? RequiredDisclosureRevision)
{
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    public IReadOnlyList<RuntimeLocalModelSnapshot> LocalModels { get; init; } = [];

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
    public RuntimeLocalResourceSnapshot? LocalResources { get; init; }
}

public enum RuntimeLocalModelState
{
    NotInstalled,
    Downloading,
    Verifying,
    Installed,
    Removing,
    Failed,
}

/// <summary>
/// Credential- and path-free projection of one reviewed local model payload.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed record RuntimeLocalModelSnapshot(
    string ModelId,
    string DisplayName,
    long DownloadSizeBytes,
    long? InstalledSizeBytes,
    bool IsRecommended,
    RuntimeLocalModelState State,
    double Progress,
    bool IsVerified,
    string? StableErrorCode);

/// <summary>
/// Bounded local resource evidence for the selected model. Raw device names and process data are
/// intentionally absent.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
public sealed record RuntimeLocalResourceSnapshot(
    bool IsLowPowerMode,
    bool AutomaticDeferred,
    long? AvailableMemoryBytes,
    long? RequiredMemoryBytes,
    long? AvailableDiskBytes,
    long? RequiredDiskBytes,
    string? StableBlockCode)
{
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
    public long? InstalledMemoryBytes { get; init; }
}

/// <summary>
/// User choices submitted without a credential value.
/// </summary>
public sealed record RuntimeTranscriptionSettingsUpdate(
    string? SelectedEngineId,
    bool AutomaticEnabled,
    string Language,
    bool RequireZeroDataRetention,
    string? SelectedModelId,
    bool DisclosureAccepted);

/// <summary>
/// Selectable active microphone exposed by a platform runtime.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed record RuntimeMicrophoneSnapshot(
    string Id,
    string DisplayName,
    bool IsDefault);
