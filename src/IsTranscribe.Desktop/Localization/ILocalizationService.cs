using Avalonia;
using System.Globalization;

namespace IsTranscribe.Desktop.Localization;

// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#scope.in
// @spec spec://modules/app/FEAT-010.A-release-v2-localization#resources
public interface ILocalizationService
{
    UiLanguage CurrentLanguage { get; }

    CultureInfo CurrentCulture { get; }

    event EventHandler? LanguageChanged;

    void Attach(Avalonia.Application application);

    void SetLanguage(UiLanguage language);

    string Get(string resourceKey);

    string Format(string resourceKey, params object?[] arguments);
}
