using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Threading;
using WpfBinding = System.Windows.Data.Binding;

namespace IsTranscribe.App.Strings;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly ResourceManager Rm =
        new("IsTranscribe.App.Strings.Strings",
            typeof(LocalizationManager).Assembly);

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public static LocalizationManager Instance { get; } = new();

    public string this[string key] =>
        Rm.GetString(key, _culture) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public void SwitchLanguage(string cultureName)
    {
        _culture = new CultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(WpfBinding.IndexerName));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetCurrentLanguage() =>
        _culture.TwoLetterISOLanguageName;
}
