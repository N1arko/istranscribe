using System.Globalization;

namespace IsTranscribe.Desktop.Localization;

/// <summary>
/// Languages shipped with the release-v2 desktop surface.
/// </summary>
// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#scope.in
// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
public enum UiLanguage
{
    Russian,
    English,
}

public static class UiLanguageExtensions
{
    public static CultureInfo ToCulture(this UiLanguage language) =>
        CultureInfo.GetCultureInfo(language switch
        {
            UiLanguage.Russian => "ru-RU",
            UiLanguage.English => "en-US",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
        });

    public static UiLanguage FromCulture(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Russian
            : UiLanguage.English;
}
