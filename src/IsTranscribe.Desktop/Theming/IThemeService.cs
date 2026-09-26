using Avalonia;

namespace IsTranscribe.Desktop.Theming;

// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#visual-system
public interface IThemeService
{
    UiThemeMode CurrentMode { get; }

    event EventHandler? ThemeChanged;

    void Attach(Avalonia.Application application);

    void SetTheme(UiThemeMode mode);
}
