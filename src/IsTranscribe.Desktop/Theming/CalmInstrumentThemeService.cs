using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;

namespace IsTranscribe.Desktop.Theming;

/// <summary>
/// Installs the Calm Instrument semantic token set and applies the selected theme policy.
/// </summary>
// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#design-gate
// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#visual-system
public sealed class CalmInstrumentThemeService : IThemeService
{
    private static readonly Uri AssemblyBaseUri = new("avares://IsTranscribe.Desktop.UI/");
    private static readonly Uri TokenDictionaryUri =
        new("avares://IsTranscribe.Desktop.UI/Theming/Resources/CalmInstrument.axaml");

    private Avalonia.Application? _application;
    private ResourceInclude? _tokens;

    public CalmInstrumentThemeService(UiThemeMode initialMode = UiThemeMode.System)
    {
        CurrentMode = initialMode;
    }

    public UiThemeMode CurrentMode { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Attach(Avalonia.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (ReferenceEquals(_application, application))
        {
            return;
        }

        DetachTokens();
        _application = application;
        _tokens = new ResourceInclude(AssemblyBaseUri)
        {
            Source = TokenDictionaryUri,
        };
        _application.Resources.MergedDictionaries.Add(_tokens);
        if (_application.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += PlatformSettings_OnColorValuesChanged;
        }

        ApplyRequestedVariant();
    }

    public void SetTheme(UiThemeMode mode)
    {
        if (CurrentMode == mode)
        {
            return;
        }

        CurrentMode = mode;
        ApplyRequestedVariant();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRequestedVariant()
    {
        if (_application is null)
        {
            return;
        }

        var highContrast = _application.PlatformSettings?.GetColorValues().ContrastPreference
            == ColorContrastPreference.High;
        _application.RequestedThemeVariant = highContrast
            ? CalmInstrumentThemeVariants.HighContrast
            : CurrentMode switch
            {
                UiThemeMode.System => ThemeVariant.Default,
                UiThemeMode.Light => ThemeVariant.Light,
                UiThemeMode.Dark => ThemeVariant.Dark,
                _ => throw new ArgumentOutOfRangeException(nameof(CurrentMode), CurrentMode, null),
            };
    }

    private void PlatformSettings_OnColorValuesChanged(object? sender, PlatformColorValues args)
    {
        ApplyRequestedVariant();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetachTokens()
    {
        if (_application is not null && _tokens is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_tokens);
        }

        if (_application?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged -= PlatformSettings_OnColorValuesChanged;
        }

        _tokens = null;
    }
}

public static class CalmInstrumentThemeVariants
{
    public static ThemeVariant HighContrast { get; } = new("HighContrast", ThemeVariant.Dark);
}
