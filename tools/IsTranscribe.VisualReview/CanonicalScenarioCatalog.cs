using IsTranscribe.Application.Runtime;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Stable domain content for the eight FEAT-013 product states in both shipped UI languages.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
internal static class CanonicalScenarioCatalog
{
    public static DateTimeOffset ClockUtc { get; } =
        new(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);

    public static IReadOnlyList<CanonicalMainScenario> MainScenarios { get; } =
    [
        new("listening", CreateSnapshot(ApplicationActivityState.Listening, recentRecordings: ReadyItems(2))),
        new("suspected", CreateSnapshot(ApplicationActivityState.Suspected, recentRecordings: ReadyItems(1))),
        new(
            "awaiting-confirmation",
            CreateSnapshot(
                ApplicationActivityState.AwaitingConfirmation,
                pendingPrompt: Prompt("Google Meet · Zen Browser", "google-meet"),
                recentRecordings: ReadyItems(1))),
        new(
            "recording",
            CreateSnapshot(
                ApplicationActivityState.Recording,
                activeMeeting: new ActiveMeetingSnapshot(
                    Guid.Parse("13000000-0000-0000-0000-000000000004"),
                    "Zoom",
                    ClockUtc - TimeSpan.FromMinutes(24) - TimeSpan.FromSeconds(18),
                    HasOutput: true,
                    HasMicrophone: true,
                    IsPaused: false),
                recentRecordings: ReadyItems(1))),
        new(
            "processing",
            CreateSnapshot(
                ApplicationActivityState.Processing,
                recentRecordings:
                [
                    Recent(
                        "13000000-0000-0000-0000-000000000005",
                        "Microsoft Teams",
                        ClockUtc - TimeSpan.FromMinutes(49),
                        TimeSpan.FromMinutes(47),
                        RecentRecordingState.Processing,
                        path: null)
                ],
                finalization: new RecordingFinalizationSnapshot(
                    Guid.Parse("13000000-0000-0000-0000-000000000005"),
                    RecordingArtifactStage.Processing,
                    Progress: 0.62))),
        new("ready", CreateSnapshot(ApplicationActivityState.Ready, recentRecordings: ReadyItems(3))),
        new(
            "attention-required",
            CreateSnapshot(
                ApplicationActivityState.AttentionRequired,
                recentRecordings:
                [
                    Recent(
                        "13000000-0000-0000-0000-000000000007",
                        "Контур.Толк",
                        ClockUtc - TimeSpan.FromMinutes(41),
                        TimeSpan.FromMinutes(39),
                        RecentRecordingState.AttentionRequired,
                        @"C:\Users\review\Music\isTranscribe\2026-07-12 Контур.Толк.m4a")
                ],
                finalization: new RecordingFinalizationSnapshot(
                    Guid.Parse("13000000-0000-0000-0000-000000000007"),
                    RecordingArtifactStage.AttentionRequired,
                    @"C:\Users\review\Music\isTranscribe\recovery\meeting.partial.m4a"),
                attentionMessage: "artifact_recovery_required")),
        new(
            "paused",
            CreateSnapshot(
                ApplicationActivityState.Paused,
                serviceEnabled: false,
                recentRecordings: ReadyItems(2)))
    ];

    public static IReadOnlyList<RuntimeMicrophoneSnapshot> Microphones { get; } =
    [
        new("mic-studio", "Studio Display Microphone", IsDefault: true),
        new("mic-headset", "Jabra Evolve2 65", IsDefault: false)
    ];

    public static RuntimeUserSettingsSnapshot Settings(
        bool serviceEnabled = true,
        string theme = "light",
        string language = "ru") =>
        RuntimeUserSettingsSnapshot.Initial with
        {
            OnboardingCompleted = true,
            ServiceEnabled = serviceEnabled,
            Autostart = true,
            Notifications = true,
            Language = language,
            Theme = theme,
            FollowSystemDefaultMicrophone = true,
            MicrophoneDeviceId = null,
            RecordingsFolder = @"C:\Users\review\Music\isTranscribe"
        };

    public static ApplicationRuntimeSnapshot WithTheme(
        ApplicationRuntimeSnapshot snapshot,
        string theme) => snapshot with
        {
            Theme = theme,
            UserSettings = snapshot.UserSettings with { Theme = theme }
        };

    public static ApplicationRuntimeSnapshot WithThemeAndLanguage(
        ApplicationRuntimeSnapshot snapshot,
        string theme,
        string language) => snapshot with
        {
            Theme = theme,
            UserSettings = snapshot.UserSettings with
            {
                Theme = theme,
                Language = language
            }
        };

    private static ApplicationRuntimeSnapshot CreateSnapshot(
        ApplicationActivityState activity,
        bool serviceEnabled = true,
        ActiveMeetingSnapshot? activeMeeting = null,
        IReadOnlyList<RecentRecordingSnapshot>? recentRecordings = null,
        MeetingPromptSnapshot? pendingPrompt = null,
        RecordingFinalizationSnapshot? finalization = null,
        string? attentionMessage = null) =>
        new(
            activity,
            serviceEnabled,
            Theme: "light",
            activeMeeting,
            recentRecordings ?? [],
            attentionMessage,
            pendingPrompt,
            finalization)
        {
            Capability = new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Full,
                SupportsProcessOutputCapture: true,
                Summary: "ready"),
            UserSettings = Settings(serviceEnabled),
            AvailableMicrophones = Microphones
        };

    private static MeetingPromptSnapshot Prompt(string sourceLabel, string profileId) => new(
        "visual-review-candidate",
        profileId,
        sourceLabel,
        ClockUtc - TimeSpan.FromSeconds(8),
        ClockUtc + TimeSpan.FromSeconds(22),
        ConfidenceScore: 86,
        DecisionReasons: ["conversation_detected", "meeting_application_active"]);

    private static IReadOnlyList<RecentRecordingSnapshot> ReadyItems(int count)
    {
        var items = new[]
        {
            Recent(
                "13000000-0000-0000-0000-000000000101",
                "Google Meet · Zen Browser",
                ClockUtc - TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(38) + TimeSpan.FromSeconds(12),
                RecentRecordingState.Ready,
                @"C:\Users\review\Music\isTranscribe\2026-07-12 Google Meet.m4a"),
            Recent(
                "13000000-0000-0000-0000-000000000102",
                "Яндекс Телемост",
                ClockUtc - TimeSpan.FromHours(5),
                TimeSpan.FromMinutes(26) + TimeSpan.FromSeconds(44),
                RecentRecordingState.Ready,
                @"C:\Users\review\Music\isTranscribe\2026-07-12 Телемост.m4a"),
            Recent(
                "13000000-0000-0000-0000-000000000103",
                "Zoom",
                ClockUtc - TimeSpan.FromDays(1),
                TimeSpan.FromMinutes(51) + TimeSpan.FromSeconds(3),
                RecentRecordingState.Ready,
                @"C:\Users\review\Music\isTranscribe\2026-07-11 Zoom.m4a")
        };
        return items.Take(count).ToArray();
    }

    private static RecentRecordingSnapshot Recent(
        string sessionId,
        string sourceLabel,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        RecentRecordingState state,
        string? path) => new(
            Guid.Parse(sessionId),
            sourceLabel,
            startedAtUtc,
            duration,
            path,
            RequiresAttention: state == RecentRecordingState.AttentionRequired)
        {
            State = state
        };
}

internal sealed record CanonicalMainScenario(
    string Id,
    ApplicationRuntimeSnapshot Snapshot);
