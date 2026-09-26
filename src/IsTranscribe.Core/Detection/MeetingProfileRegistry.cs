namespace IsTranscribe.Core.Detection;

/// <summary>
/// Versioned initial application profile registry. Normal profiles are added as data.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#zen
/// </remarks>
public sealed class MeetingProfileRegistry(IEnumerable<MeetingAppProfile>? additionalProfiles = null)
{
    public const int ProfileSchemaVersion = 1;

    private static readonly string[] ChromiumAndFirefoxProcesses =
    [
        "chrome.exe",
        "msedge.exe",
        "brave.exe",
        "firefox.exe",
        "zen.exe",
        "safari.exe",
        "opera.exe",
        "vivaldi.exe",
        "chromium.exe",
        "yandex.exe",
        "browser.exe"
    ];

    private readonly IReadOnlyList<MeetingAppProfile> _profiles = BuiltInProfiles
        .Concat(additionalProfiles ?? [])
        .GroupBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static group => group.Last())
        .ToArray();

    public IReadOnlyList<MeetingAppProfile> Profiles => _profiles;

    public IReadOnlyCollection<string> WatchedProcessNames => _profiles
        .SelectMany(static profile => profile.DedicatedProcessNames.Concat(profile.BrowserProcessNames))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public MeetingAppProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MeetingAppProfile> MatchProcess(string processName) =>
        _profiles
            .Where(profile => profile.MatchesDedicatedProcess(processName) || profile.MatchesBrowserProcess(processName))
            .ToArray();

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
    public bool IsBrowserProcess(string processName) =>
        _profiles.Any(profile => profile.MatchesBrowserProcess(processName));

    private static IReadOnlyList<MeetingAppProfile> BuiltInProfiles { get; } =
    [
        Dedicated(
            "zoom",
            "Zoom",
            ["zoom.exe"],
            ["Zoom Meeting", "Zoom Webinar", "Zoom Конференция"],
            CommonCallControls("zoom"),
            "zoom"),
        Hybrid(
            "microsoft-teams",
            "Microsoft Teams",
            ["ms-teams.exe", "teams.exe"],
            ["Microsoft Teams meeting", "Meeting | Microsoft Teams", "Собрание | Microsoft Teams", "Собрание с организатором", "Звонок | Microsoft Teams"],
            CommonCallControls("teams"),
            "teams"),
        Browser(
            "google-meet",
            "Google Meet",
            [" - Google Meet", " — Google Meet"],
            ["YouTube", "Google Search"],
            CommonCallControls("google-meet"),
            "google-meet",
            weakBrandPhrases: ["Google Meet"]),
        Hybrid(
            "yandex-telemost",
            "Яндекс Телемост",
            ["telemost.exe", "yandextelemost.exe"],
            ["Яндекс Телемост", "Телемост", "Yandex Telemost"],
            CommonCallControls("telemost"),
            "telemost",
            windowStrength: 0.25),
        Hybrid(
            "kontur-talk",
            "Контур.Толк",
            ["ktalk.exe", "talk.exe", "tolk.exe", "konturtalk.exe", "kontur-talk.exe", "kontur.talk.exe"],
            ["Контур.Толк", "Контур Толк", "Толк —", "Kontur Talk"],
            CommonCallControls("kontur-talk"),
            "kontur-talk",
            windowStrength: 0.25),
        GenericBrowserFallback()
    ];

    private static MeetingAppProfile Dedicated(
        string id,
        string displayName,
        IReadOnlyList<string> processes,
        IReadOnlyList<string> windowPhrases,
        IReadOnlyList<MeetingControlEvidenceRule> controls,
        string iconKey,
        double windowStrength = 1) => new(
            id,
            displayName,
            processes.Select(MeetingAppProfile.NormalizeProcessName).ToArray(),
            [],
            [new MeetingWindowEvidenceRule($"{id}.meeting-window", windowPhrases, [], windowStrength)],
            controls,
            StrongDedicatedWindowWeights(),
            iconKey);

    private static MeetingAppProfile Hybrid(
        string id,
        string displayName,
        IReadOnlyList<string> processes,
        IReadOnlyList<string> windowPhrases,
        IReadOnlyList<MeetingControlEvidenceRule> controls,
        string iconKey,
        double windowStrength = 1) => new(
            id,
            displayName,
            processes.Select(MeetingAppProfile.NormalizeProcessName).ToArray(),
            ChromiumAndFirefoxProcesses,
            [new MeetingWindowEvidenceRule($"{id}.meeting-window", windowPhrases, [], windowStrength)],
            controls,
            StrongDedicatedWindowWeights(),
            iconKey);

    private static MeetingAppProfile Browser(
        string id,
        string displayName,
        IReadOnlyList<string> windowPhrases,
        IReadOnlyList<string> excludedPhrases,
        IReadOnlyList<MeetingControlEvidenceRule> controls,
        string iconKey,
        IReadOnlyList<string>? weakBrandPhrases = null) => new(
            id,
            displayName,
            [],
            ChromiumAndFirefoxProcesses,
            BuildBrowserWindowRules(id, windowPhrases, excludedPhrases, weakBrandPhrases),
            controls,
            new Dictionary<MeetingEvidenceKind, int>(),
            iconKey);

    private static IReadOnlyList<MeetingWindowEvidenceRule> BuildBrowserWindowRules(
        string id,
        IReadOnlyList<string> windowPhrases,
        IReadOnlyList<string> excludedPhrases,
        IReadOnlyList<string>? weakBrandPhrases)
    {
        var rules = new List<MeetingWindowEvidenceRule>
        {
            new($"{id}.meeting-window", windowPhrases, excludedPhrases)
        };
        if (weakBrandPhrases is { Count: > 0 })
        {
            rules.Add(new MeetingWindowEvidenceRule(
                $"{id}.brand-window",
                weakBrandPhrases,
                excludedPhrases,
                Strength: 0.15));
        }

        return rules;
    }

    private static IReadOnlyList<MeetingControlEvidenceRule> CommonCallControls(
        string profileId,
        bool exactNames = false) =>
    [
        new MeetingControlEvidenceRule(
            $"{profileId}.call-controls",
            RequiredControlGroups:
            [
                [
                    "Mute", "Unmute", "Turn off microphone", "Turn on microphone",
                    "Отключить микрофон", "Включить микрофон", "Выключить микрофон",
                    "Выключить звук", "Отключить звук", "Включить звук"
                ],
                ["Leave", "End call", "Hang up", "Выйти", "Завершить звонок", "Завершить", "Завершение", "Покинуть"]
            ],
            MinimumMatchedGroups: 2,
            exactNames
                ? MeetingControlNameMatchMode.Exact
                : MeetingControlNameMatchMode.Contains)
    ];

    private static IReadOnlyDictionary<MeetingEvidenceKind, int> StrongDedicatedWindowWeights() =>
        new Dictionary<MeetingEvidenceKind, int>
        {
            [MeetingEvidenceKind.MeetingWindow] = 45
        };

    private static MeetingAppProfile GenericBrowserFallback() => new(
        "generic-browser",
        "Other browser meeting",
        [],
        ChromiumAndFirefoxProcesses,
        [],
        CommonCallControls("generic-browser", exactNames: true),
        new Dictionary<MeetingEvidenceKind, int>
        {
            [MeetingEvidenceKind.BrowserServiceIdentity] = 0,
            [MeetingEvidenceKind.RenderSessionActive] = 8,
            [MeetingEvidenceKind.RenderSpeech] = 28,
            [MeetingEvidenceKind.MicrophoneInUse] = 4,
            [MeetingEvidenceKind.MicrophoneSpeech] = 8,
            [MeetingEvidenceKind.ConversationalAlternation] = 12,
            [MeetingEvidenceKind.MeetingWindow] = 0,
            [MeetingEvidenceKind.MeetingControls] = 34,
            [MeetingEvidenceKind.ForegroundWindow] = 2,
            [MeetingEvidenceKind.ProcessStable] = 4
        },
        "generic-meeting",
        SchemaVersion: ProfileSchemaVersion,
        ProcessTreeBehavior: MeetingProcessTreeBehavior.RootAndDescendants,
        CaptureSourcePreference: MeetingCaptureSourcePreference.ProcessOutputPreferred,
        AllowControlOnlyBrowserMatch: true,
        IsFallbackProfile: true,
        HardExclusionRules:
        [
            new MeetingWindowEvidenceRule(
                "generic-browser.media-window",
                ["YouTube", "Netflix", "Spotify", "Twitch", "SoundCloud", "VK Видео", "Кинопоиск"],
                [])
        ]);
}
