namespace IsTranscribe.Core.Settings;

/// <summary>
/// Minimal release-v2 settings contract owned by Core.
/// </summary>
/// <remarks>
/// @spec spec://common/PROP-006-release-v2-product-canon#experience-canon
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record ReleaseV2Settings(
    int Version,
    bool OnboardingCompleted,
    bool ServiceEnabled,
    bool Autostart,
    bool Notifications,
    string Language,
    string Theme,
    string? MicrophoneDeviceId,
    bool FollowSystemDefaultMicrophone,
    string? RecordingsFolder,
    IReadOnlyList<MeetingApplicationPreference> Applications,
    bool? AutostartPreference = null,
    bool? PackagedAutostartObserved = null,
    TranscriptionPreferences? Transcription = null)
{
    public const int CurrentVersion = 3;

    public static ReleaseV2Settings Default { get; } = new(
        Version: CurrentVersion,
        OnboardingCompleted: false,
        ServiceEnabled: true,
        Autostart: true,
        Notifications: true,
        Language: "ru",
        Theme: "system",
        MicrophoneDeviceId: null,
        FollowSystemDefaultMicrophone: true,
        RecordingsFolder: null,
        Applications: [],
        AutostartPreference: null,
        PackagedAutostartObserved: null,
        Transcription: TranscriptionPreferences.Default);

    public ReleaseV2Settings Canonicalize() => this with
    {
        Version = CurrentVersion,
        AutostartPreference = AutostartPreference ?? Autostart,
        Language = NormalizeChoice(Language, "ru", ["ru", "en"]),
        Theme = NormalizeChoice(Theme, "system", ["system", "light", "dark"]),
        Transcription = (Transcription ?? TranscriptionPreferences.Default).Canonicalize(),
        Applications = (Applications ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item.ProfileId))
            .Select(static item => item.Canonicalize())
            .DistinctBy(static item => item.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    };

    private static string NormalizeChoice(string? value, string fallback, IReadOnlyCollection<string> allowed) =>
        allowed.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? value!.ToLowerInvariant()
            : fallback;
}

/// <summary>
/// Provider-neutral transcription choices persisted with release-v2 settings.
/// Provider credentials remain in the platform secret vault.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record TranscriptionPreferences(
    string? SelectedEngineId,
    bool AutomaticEnabled,
    string Language,
    IReadOnlyList<TranscriptionEnginePreference> Engines,
    bool RequireZeroDataRetention)
{
    private const int MaximumEngineIdLength = 128;
    private const int MaximumModelIdLength = 256;
    private const int MaximumLanguageLength = 35;

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#modes.values
    public string Mode => !AutomaticEnabled ? "off" : SelectedEngineId switch
    {
        "local.whisper" => "local",
        "remote.groq" or "remote.openrouter" => "online",
        _ => "off"
    };

    public static TranscriptionPreferences Default { get; } = new(
        SelectedEngineId: null,
        AutomaticEnabled: false,
        Language: "auto",
        Engines: [],
        RequireZeroDataRetention: true);

    public TranscriptionPreferences Canonicalize()
    {
        var engines = (Engines ?? [])
            .OfType<TranscriptionEnginePreference>()
            .Select(static preference => new TranscriptionEnginePreference(
                NormalizeEngineId(preference.EngineId) ?? string.Empty,
                NormalizeModelId(preference.ModelId),
                preference.DisclosureAccepted,
                NormalizeDisclosureRevision(preference.DisclosureRevision)))
            .Where(static preference => preference.EngineId.Length > 0)
            .GroupBy(static preference => preference.EngineId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static preference => preference.EngineId, StringComparer.Ordinal)
            .ToArray();

        return this with
        {
            SelectedEngineId = NormalizeEngineId(SelectedEngineId),
            Language = "auto",
            Engines = engines
        };
    }

    public string? GetModelId(string engineId)
    {
        var normalizedEngineId = NormalizeEngineId(engineId);
        // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#migration
        if (normalizedEngineId == "local.whisper") return "large-v3-turbo";
        if (normalizedEngineId == "remote.groq") return "whisper-large-v3-turbo";
        if (normalizedEngineId == "remote.openrouter") return "openai/whisper-large-v3-turbo";
        return normalizedEngineId is null
            ? null
            : (Engines ?? []).OfType<TranscriptionEnginePreference>().FirstOrDefault(preference => string.Equals(
                preference.EngineId,
                normalizedEngineId,
                StringComparison.Ordinal))?.ModelId;
    }

    public bool HasAcceptedDisclosure(string engineId, string? requiredRevision = null)
    {
        var normalizedEngineId = NormalizeEngineId(engineId);
        var normalizedRevision = NormalizeDisclosureRevision(requiredRevision);
        return normalizedEngineId is not null
            && (Engines ?? []).OfType<TranscriptionEnginePreference>().Any(preference => string.Equals(
                preference.EngineId,
                normalizedEngineId,
                StringComparison.Ordinal)
                && preference.DisclosureAccepted
                && (normalizedRevision is null || string.Equals(
                    preference.DisclosureRevision,
                    normalizedRevision,
                    StringComparison.Ordinal)));
    }

    private static string? NormalizeEngineId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumEngineIdLength
            || !IsAsciiLetterOrDigit(normalized[0])
            || !IsAsciiLetterOrDigit(normalized[^1])
            || normalized.Any(static character =>
                !IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '-' and not ':'))
        {
            return null;
        }

        return normalized;
    }

    private static string? NormalizeModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= MaximumModelIdLength
               && normalized.All(static character => !char.IsControl(character))
            ? normalized
            : null;
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "auto";
        }

        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        if (string.Equals(normalized, "auto", StringComparison.Ordinal))
        {
            return normalized;
        }

        return normalized.Length <= MaximumLanguageLength
               && IsAsciiLetter(normalized[0])
               && IsAsciiLetterOrDigit(normalized[^1])
               && normalized.All(static character =>
                   IsAsciiLetterOrDigit(character) || character == '-')
            ? normalized
            : "auto";
    }

    private static string? NormalizeDisclosureRevision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 64
               && IsAsciiLetterOrDigit(normalized[0])
               && IsAsciiLetterOrDigit(normalized[^1])
               && normalized.All(static character =>
                   IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? normalized
            : null;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        IsAsciiLetter(character) || character is >= '0' and <= '9';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}

/// <summary>
/// Model and consent choices scoped to one registered transcription engine.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record TranscriptionEnginePreference(
    string EngineId,
    string? ModelId,
    bool DisclosureAccepted,
    string? DisclosureRevision = null);

/// <summary>
/// Ask-or-ignore policy for one normalized meeting profile.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// </remarks>
public sealed record MeetingApplicationPreference(
    string ProfileId,
    string DisplayName,
    MeetingApplicationPolicy Policy)
{
    public MeetingApplicationPreference Canonicalize() => this with
    {
        ProfileId = ProfileId.Trim().ToLowerInvariant(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? ProfileId.Trim() : DisplayName.Trim()
    };
}

public enum MeetingApplicationPolicy
{
    Ask,
    Ignore
}
