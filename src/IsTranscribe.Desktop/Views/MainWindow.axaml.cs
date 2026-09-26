using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private IWindowPlacementStore? _placementStore;

    public MainWindow()
    {
        InitializeComponent();
        AccessibilityLiveRegion.ApplyPolite(AttentionLiveRegion);
    }

    public event EventHandler? QuitRequested;

    public void AttachPlacementStore(IWindowPlacementStore placementStore) =>
        _placementStore = placementStore;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = RestorePlacementAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _ = SavePlacementAsync();
        }

        base.OnClosing(e);
    }

    private void QuitMenuItem_OnClick(object? sender, RoutedEventArgs args) =>
        QuitRequested?.Invoke(this, EventArgs.Empty);

    private async Task RestorePlacementAsync()
    {
        if (_placementStore is null)
        {
            return;
        }

        var placement = await _placementStore.LoadAsync(CancellationToken.None);
        if (placement is null)
        {
            return;
        }

        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        var hasValidScreen = Screens.All.Any(screen => HasVisibleRestoreArea(
            new PixelRect(
                placement.X,
                placement.Y,
                Math.Max(1, (int)Math.Ceiling(width * screen.Scaling)),
                Math.Max(1, (int)Math.Ceiling(height * screen.Scaling))),
            screen.WorkingArea));
        if (!hasValidScreen)
        {
            return;
        }

        Width = width;
        Height = height;
        Position = new PixelPoint(placement.X, placement.Y);
    }

    private async Task SavePlacementAsync()
    {
        if (_placementStore is null)
        {
            return;
        }

        try
        {
            await _placementStore.SaveAsync(
                new WindowPlacement(Position.X, Position.Y, Width, Height),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window placement is a convenience and must never block app shutdown.
        }
    }

    private static bool HasVisibleRestoreArea(PixelRect window, PixelRect workingArea)
    {
        var width = Math.Min(window.Right, workingArea.Right) - Math.Max(window.X, workingArea.X);
        var height = Math.Min(window.Bottom, workingArea.Bottom) - Math.Max(window.Y, workingArea.Y);
        return width >= 96 && height >= 64;
    }
}
