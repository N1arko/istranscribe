using Avalonia.Controls;
using Avalonia.Platform;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Resolves the monitor containing the foreground work window without activating app-owned surfaces.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// </remarks>
internal static class ForegroundScreenLocator
{
    public static Screen? TryGet(Screens screens)
    {
        var center = DesktopComposition.Current.ActiveWorkAreaProvider
            .TryGetForegroundWindowCenter();
        if (center is null)
        {
            return null;
        }

        return screens.ScreenFromPoint(new Avalonia.PixelPoint(center.Value.X, center.Value.Y));
    }
}
