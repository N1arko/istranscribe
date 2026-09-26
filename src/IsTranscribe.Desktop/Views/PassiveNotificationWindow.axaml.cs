using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Non-activating passive notification positioned at the active work-area edge.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// </remarks>
public sealed partial class PassiveNotificationWindow : Window
{
    private const double EdgeMargin = 18;
    private Window? _placementOwner;

    public PassiveNotificationWindow() => InitializeComponent();

    public PassiveNotificationWindow(PassiveNotificationViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Opened += Window_OnOpened;
    }

    public void ShowNear(Window? placementOwner)
    {
        _placementOwner = placementOwner;
        Show();
    }

    private void Window_OnOpened(object? sender, EventArgs args)
    {
        var screen = ForegroundScreenLocator.TryGet(Screens) ?? TryGetPlacementScreen() ?? Screens.Primary;
        if (screen is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scaling = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        var margin = (int)Math.Ceiling(EdgeMargin * scaling);
        Position = new PixelPoint(
            Math.Max(area.X + margin, area.Right - width - margin),
            Math.Max(area.Y + margin, area.Bottom - height - margin));
    }

    private Screen? TryGetPlacementScreen()
    {
        try
        {
            return _placementOwner is null ? null : Screens.ScreenFromWindow(_placementOwner);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
