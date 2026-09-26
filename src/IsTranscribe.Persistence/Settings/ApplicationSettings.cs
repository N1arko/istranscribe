using IsTranscribe.Core.Settings;

namespace IsTranscribe.Host.Settings;

public sealed record ApplicationSettings(
    int Version,
    bool OnboardingCompleted,
    GeneralSettings General,
    RecordingSettings Recording,
    DeviceSettings Devices,
    ApplicationCatalogSettings Applications,
    StorageSettings Storage,
    TranscriptionSettings Transcription,
    ReleaseV2Settings? ReleaseV2 = null)
{
    /// <summary>
    /// Version 3 adds provider-neutral transcription preferences under release_v2.
    /// Provider credentials are intentionally excluded from this document.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    /// </remarks>
    public const int CurrentSchemaVersion = 3;

    public static ApplicationSettings Default { get; } = new(
        Version: CurrentSchemaVersion,
        OnboardingCompleted: false,
        General: GeneralSettings.Default,
        Recording: RecordingSettings.Default,
        Devices: DeviceSettings.Default,
        Applications: ApplicationCatalogSettings.Default,
        Storage: StorageSettings.Default,
        Transcription: TranscriptionSettings.Default,
        ReleaseV2: null);

    public BootstrapSettingsSnapshot ToBootstrapSnapshot() =>
        new(
            OnboardingCompleted,
            General.MinimizeToTrayOnClose,
            General.Notifications);

    /// <summary>
    /// Stores the release-v2 projection and keeps legacy sections aligned for rollback-safe migration.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    /// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    /// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    /// </remarks>
    public ApplicationSettings WithReleaseV2Projection(ReleaseV2Settings releaseV2)
    {
        ArgumentNullException.ThrowIfNull(releaseV2);
        var canonical = releaseV2.Canonicalize();

        return (this with
        {
            OnboardingCompleted = canonical.OnboardingCompleted,
            General = General with
            {
                Autostart = canonical.AutostartPreference ?? canonical.Autostart,
                Notifications = canonical.Notifications,
                Language = canonical.Language,
                AppTheme = canonical.Theme
            },
            Recording = Recording with
            {
                Mode = canonical.ServiceEnabled ? "ask" : "off"
            },
            Devices = Devices with
            {
                MicrophoneDeviceId = canonical.MicrophoneDeviceId,
                FollowSystemDefaultMic = canonical.FollowSystemDefaultMicrophone
            },
            Storage = Storage with
            {
                RecordingsFolder = canonical.RecordingsFolder
            },
            ReleaseV2 = canonical
        }).Canonicalize();
    }

    public ApplicationSettings Canonicalize() =>
        this with
        {
            Version = CurrentSchemaVersion,
            General = General ?? GeneralSettings.Default,
            Recording = Recording ?? RecordingSettings.Default,
            Devices = Devices ?? DeviceSettings.Default,
            Applications = Applications ?? ApplicationCatalogSettings.Default,
            Storage = Storage ?? StorageSettings.Default,
            Transcription = Transcription ?? TranscriptionSettings.Default,
            ReleaseV2 = ReleaseV2?.Canonicalize()
        };
}

public sealed record GeneralSettings(
    bool Autostart,
    bool MinimizeToTrayOnClose,
    bool Notifications,
    string Language,
    string AppTheme,
    HotkeySettings Hotkeys)
{
    public static GeneralSettings Default { get; } = new(
        Autostart: true,
        MinimizeToTrayOnClose: true,
        Notifications: true,
        Language: "ru",
        AppTheme: "system",
        Hotkeys: HotkeySettings.Default);
}

public sealed record HotkeySettings(
    string? ForceRecordToggle,
    string? PrivacyPauseToggle,
    string? DiscardCurrent,
    string? OpenMainWindow)
{
    public static HotkeySettings Default { get; } = new(
        ForceRecordToggle: null,
        PrivacyPauseToggle: null,
        DiscardCurrent: null,
        OpenMainWindow: null);
}

public sealed record RecordingSettings(
    string Mode,
    int PrebufferSeconds,
    int SilenceThresholdDbfs,
    int StartDelaySeconds,
    int StopDelaySeconds,
    int MergeWindowSeconds,
    string PrivacyPausePolicy,
    string[] DefaultSourcesAuto,
    string[] DefaultSourcesForce)
{
    public static RecordingSettings Default { get; } = new(
        Mode: "ask",
        PrebufferSeconds: 15,
        SilenceThresholdDbfs: -40,
        StartDelaySeconds: 2,
        StopDelaySeconds: 20,
        MergeWindowSeconds: 60,
        PrivacyPausePolicy: "pause",
        DefaultSourcesAuto: ["process_output", "mic"],
        DefaultSourcesForce: ["device_loopback", "mic"]);
}

public sealed record DeviceSettings(
    string? OutputDeviceId,
    string? MicrophoneDeviceId,
    bool FollowSystemDefaultOutput,
    bool FollowSystemDefaultMic,
    bool AutoDiscoverOutput,
    bool AutoDiscoverMic,
    string OutputChangePolicy,
    string MicChangePolicy,
    string ActiveRecordingDevicePolicy)
{
    public static DeviceSettings Default { get; } = new(
        OutputDeviceId: null,
        MicrophoneDeviceId: null,
        FollowSystemDefaultOutput: true,
        FollowSystemDefaultMic: true,
        AutoDiscoverOutput: true,
        AutoDiscoverMic: true,
        OutputChangePolicy: "seamless_switch",
        MicChangePolicy: "seamless_switch",
        ActiveRecordingDevicePolicy: "seamless_switch");
}

public sealed record ApplicationCatalogSettings(
    string AutoDiscoveryPolicy,
    string[] IgnoredAppSuggestions,
    string[] Exclusions)
{
    public static ApplicationCatalogSettings Default { get; } = new(
        AutoDiscoveryPolicy: "ask_to_add",
        IgnoredAppSuggestions: [],
        Exclusions: []);
}

public sealed record StorageSettings(
    string? RecordingsFolder,
    string? TranscriptsFolder,
    string? FailedTempFolder,
    string FilenameTemplate,
    bool KeepRawAfterSuccess,
    string TempRetentionPeriod,
    bool AudioCompressionEnabled)
{
    public static StorageSettings Default { get; } = new(
        RecordingsFolder: null,
        TranscriptsFolder: null,
        FailedTempFolder: null,
        FilenameTemplate: "YYYY-MM-DD HH-mm — {SourceApp} — {SessionId}",
        KeepRawAfterSuccess: true,
        TempRetentionPeriod: "7d",
        AudioCompressionEnabled: true);
}

public sealed record TranscriptionSettings(
    string Model,
    bool Diarization,
    int MinSpeakers,
    int MaxSpeakers,
    string Language,
    bool AutoRetry,
    int RetryCount)
{
    public static TranscriptionSettings Default { get; } = new(
        Model: "whisper-v3-turbo",
        Diarization: true,
        MinSpeakers: 2,
        MaxSpeakers: 6,
        Language: "auto",
        AutoRetry: true,
        RetryCount: 3);
}
