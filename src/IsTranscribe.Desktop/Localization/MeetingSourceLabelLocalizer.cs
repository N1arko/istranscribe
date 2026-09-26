namespace IsTranscribe.Desktop.Localization;

/// <summary>
/// Maps stable meeting profile identities and migrated source labels to friendly localized copy.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#surfaces
/// </remarks>
public static class MeetingSourceLabelLocalizer
{
    public static string Localize(
        ILocalizationService strings,
        string? sourceLabel,
        string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var resourceKey = ProfileResourceKey(profileId) ?? MigratedSourceResourceKey(sourceLabel);
        if (resourceKey is not null)
        {
            return strings.Get(resourceKey);
        }

        return string.IsNullOrWhiteSpace(sourceLabel)
            ? strings.Get("String.Recent.Source.Unknown")
            : sourceLabel.Trim();
    }

    private static string? ProfileResourceKey(string? profileId) => profileId?.ToLowerInvariant() switch
    {
        "zoom" => "String.App.Zoom",
        "microsoft-teams" => "String.App.MicrosoftTeams",
        "google-meet" => "String.App.GoogleMeet",
        "yandex-telemost" => "String.App.YandexTelemost",
        "kontur-talk" => "String.App.KonturTalk",
        "generic-browser" => "String.App.OtherBrowser",
        _ => null
    };

    private static string? MigratedSourceResourceKey(string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel))
        {
            return null;
        }

        if (sourceLabel.Equals("Manual recording", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Ручная запись", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("source:manual", StringComparison.OrdinalIgnoreCase))
        {
            return "String.Source.ManualRecording";
        }

        if (sourceLabel.Equals("Яндекс Телемост", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Yandex Telemost", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Телемост", StringComparison.OrdinalIgnoreCase))
        {
            return "String.App.YandexTelemost";
        }

        if (sourceLabel.Equals("Контур.Толк", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Контур Толк", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Kontur Talk", StringComparison.OrdinalIgnoreCase))
        {
            return "String.App.KonturTalk";
        }

        return sourceLabel.Equals("Other browser meeting", StringComparison.OrdinalIgnoreCase)
            || sourceLabel.Equals("Другая встреча в браузере", StringComparison.OrdinalIgnoreCase)
            ? "String.App.OtherBrowser"
            : null;
    }
}
