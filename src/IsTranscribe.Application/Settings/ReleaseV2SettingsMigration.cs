using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Application.Settings;

/// <summary>
/// Deterministic canonical projection of the v1 settings and application rules into the v2 contract.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// </remarks>
public static class ReleaseV2SettingsMigration
{
    public static ReleaseV2Settings Migrate(
        ApplicationSettings legacy,
        IReadOnlyList<AppRuleRecord> applicationRules)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(applicationRules);

        var profiles = new MeetingProfileRegistry();
        // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
        var applications = applicationRules
            .Where(rule => !profiles.IsBrowserProcess(rule.ProcessName))
            .Select(ToPreference)
            .ToArray();

        return new ReleaseV2Settings(
            Version: ReleaseV2Settings.CurrentVersion,
            OnboardingCompleted: legacy.OnboardingCompleted,
            ServiceEnabled: !string.Equals(legacy.Recording.Mode, "off", StringComparison.OrdinalIgnoreCase),
            Autostart: legacy.General.Autostart,
            Notifications: legacy.General.Notifications,
            Language: legacy.General.Language,
            Theme: legacy.General.AppTheme,
            MicrophoneDeviceId: legacy.Devices.MicrophoneDeviceId,
            FollowSystemDefaultMicrophone: legacy.Devices.FollowSystemDefaultMic,
            RecordingsFolder: legacy.Storage.RecordingsFolder,
            Applications: applications,
            Transcription: TranscriptionPreferences.Default)
            .Canonicalize();
    }

    private static MeetingApplicationPreference ToPreference(AppRuleRecord rule) => new(
        ProfileId: $"legacy:{AppRuleRecord.NormalizeProcessName(rule.ProcessName)}",
        DisplayName: rule.DisplayName,
        Policy: rule.Enabled ? MeetingApplicationPolicy.Ask : MeetingApplicationPolicy.Ignore);
}
