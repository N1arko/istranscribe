using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Compact non-modal surface for the Ask policy.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// </remarks>
public sealed partial class AskPromptWindow : Window, IAskPromptSurface
{
    private const double EdgeMargin = 18;
    private readonly Action<nint>? _platformPresenter;
    private Window? _placementOwner;
    private bool _allowClose;

    public AskPromptWindow()
        : this(platformPresenter: null)
    {
    }

    private AskPromptWindow(Action<nint>? platformPresenter)
    {
        _platformPresenter = platformPresenter;
        InitializeComponent();
        AccessibilityLiveRegion.ApplyPolite(CountdownLiveRegion);
        AccessibilityLiveRegion.ApplyAssertive(ResolutionErrorLiveRegion);
        Opened += AskPromptWindow_OnOpened;
    }

    public AskPromptWindow(AskPromptViewModel viewModel)
        : this(viewModel, platformPresenter: null)
    {
    }

    public AskPromptWindow(
        AskPromptViewModel viewModel,
        Action<nint>? platformPresenter)
        : this(platformPresenter) =>
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    public AskPromptViewModel ViewModel => DataContext as AskPromptViewModel
        ?? throw new InvalidOperationException("AskPromptWindow requires AskPromptViewModel as DataContext.");

    public void ShowNear(Window? placementOwner)
    {
        _placementOwner = placementOwner;
        Show();
    }

    public void CloseWithoutResolution()
    {
        _allowClose = true;
        if (IsVisible)
        {
            Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        if (!_allowClose
            && args.CloseReason is not WindowCloseReason.ApplicationShutdown
                and not WindowCloseReason.OSShutdown
            && DataContext is AskPromptViewModel { IsResolved: false } viewModel)
        {
            args.Cancel = true;
            _ = viewModel.SkipAsync();
        }

        base.OnClosing(args);
    }

    private void AskPromptWindow_OnOpened(object? sender, EventArgs args)
    {
        PresentOnCurrentWorkspace();
        PositionNearWorkArea();
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
    /// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
    /// </summary>
    private void PresentOnCurrentWorkspace()
    {
        if (_platformPresenter is null || TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        try
        {
            _platformPresenter(handle);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // Avalonia's ordinary topmost surface remains available if the optional
            // platform promotion cannot be applied.
        }
    }

    private void PositionNearWorkArea()
    {
        var screen = ForegroundScreenLocator.TryGet(Screens) ?? TryGetPlacementScreen() ?? Screens.Primary;
        if (screen is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scaling = screen.Scaling;
        var workingArea = screen.WorkingArea;
        var width = checked((int)Math.Ceiling(Bounds.Width * scaling));
        var height = checked((int)Math.Ceiling(Bounds.Height * scaling));
        var margin = checked((int)Math.Ceiling(EdgeMargin * scaling));
        var x = Math.Max(workingArea.X + margin, workingArea.Right - width - margin);
        var y = Math.Max(workingArea.Y + margin, workingArea.Bottom - height - margin);
        Position = new PixelPoint(x, y);
    }

    private Screen? TryGetPlacementScreen()
    {
        if (_placementOwner is null)
        {
            return null;
        }

        try
        {
            return Screens.ScreenFromWindow(_placementOwner);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
