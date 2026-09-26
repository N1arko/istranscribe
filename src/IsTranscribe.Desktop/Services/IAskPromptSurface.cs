using Avalonia.Controls;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Test seam around the native prompt surface.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// </remarks>
public interface IAskPromptSurface
{
    event EventHandler? Closed;

    AskPromptViewModel ViewModel { get; }

    void ShowNear(Window? placementOwner);

    void CloseWithoutResolution();
}
