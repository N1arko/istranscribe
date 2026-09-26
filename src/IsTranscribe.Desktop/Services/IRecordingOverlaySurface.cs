using Avalonia.Controls;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Test seam around the native floating recording controls.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#surface
/// </remarks>
public interface IRecordingOverlaySurface
{
    bool IsVisible { get; }

    void ShowNear(Window? placementOwner);

    void ExpandForNewSession();

    void HideSurface();

    void CloseForShutdown();
}
