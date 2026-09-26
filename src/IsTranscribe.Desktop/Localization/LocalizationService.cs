using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using System.Globalization;

namespace IsTranscribe.Desktop.Localization;

/// <summary>
/// Owns the active localized resource dictionary and the formatting culture.
/// Call <see cref="Attach"/> during application startup before creating windows.
/// </summary>
// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#scope.in
// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
// @spec spec://modules/app/FEAT-010.A-release-v2-localization#resources
public sealed class LocalizationService : ILocalizationService
{
    private static readonly Uri AssemblyBaseUri = new("avares://IsTranscribe.Desktop.UI/");

    private Avalonia.Application? _application;
    private readonly List<ResourceInclude> _activeDictionaries = [];

    public LocalizationService(UiLanguage initialLanguage = UiLanguage.Russian)
    {
        CurrentLanguage = initialLanguage;
        ApplyCulture(initialLanguage.ToCulture());
    }

    public UiLanguage CurrentLanguage { get; private set; }

    public CultureInfo CurrentCulture => CurrentLanguage.ToCulture();

    public event EventHandler? LanguageChanged;

    public void Attach(Avalonia.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (ReferenceEquals(_application, application))
        {
            return;
        }

        DetachCurrentDictionaries();
        _application = application;
        ReplaceDictionaries();
    }

    public void SetLanguage(UiLanguage language)
    {
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        ApplyCulture(CurrentCulture);
        ReplaceDictionaries();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        if (_application is null)
        {
            throw new InvalidOperationException("Attach the localization service before requesting UI resources.");
        }

        if (_application.TryFindResource(resourceKey, out object? value) && value is string localized)
        {
            return localized;
        }

        throw new KeyNotFoundException($"Localized resource '{resourceKey}' was not found for {CurrentCulture.Name}.");
    }

    public string Format(string resourceKey, params object?[] arguments) =>
        string.Format(CurrentCulture, Get(resourceKey), arguments);

    private void ReplaceDictionaries()
    {
        if (_application is null)
        {
            return;
        }

        DetachCurrentDictionaries();

        foreach (var languageCode in CatalogLanguageCodes(CurrentLanguage))
        {
            var dictionary = CreateDictionary(languageCode);
            _activeDictionaries.Add(dictionary);
            _application.Resources.MergedDictionaries.Add(dictionary);
        }
    }

    private void DetachCurrentDictionaries()
    {
        if (_application is not null)
        {
            for (var index = _activeDictionaries.Count - 1; index >= 0; index--)
            {
                _application.Resources.MergedDictionaries.Remove(_activeDictionaries[index]);
            }
        }

        _activeDictionaries.Clear();
    }

    internal static IReadOnlyList<string> CatalogLanguageCodes(UiLanguage language) => language switch
    {
        UiLanguage.Russian => ["en", "ru"],
        UiLanguage.English => ["en"],
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    private static ResourceInclude CreateDictionary(string languageCode) => new(AssemblyBaseUri)
    {
        Source = new Uri($"avares://IsTranscribe.Desktop.UI/Localization/Resources/Strings.{languageCode}.axaml"),
    };

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
