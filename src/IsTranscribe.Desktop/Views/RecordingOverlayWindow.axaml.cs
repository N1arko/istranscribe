using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Frameless recording controls that remember a user-selected screen position.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#surface
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#expanded
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#collapsed
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// </remarks>
public sealed partial class RecordingOverlayWindow : Window, IRecordingOverlaySurface
{
    private const double ExpandedWidth = 228;
    private const double CollapsedWidth = 44;
    private const double EdgeMargin = 8;
    private const double CollapsedDragThreshold = 4;
    private static readonly TimeSpan PlacementSettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly IRecordingOverlayPlacementStore? _placementStore;
    private CancellationTokenSource? _placementSettleCancellation;
    private RecordingOverlayAnchor? _anchor;
    private PixelPoint? _programmaticPosition;
    private Window? _placementOwner;
    private bool _hasUserPlacement;
    private bool _placementReady;
    private bool _screensSubscribed;
    private bool _suppressPositionTracking;
    private PixelPoint? _collapsedDragPointerStart;
    private PixelPoint? _collapsedDragWindowStart;
    private bool _collapsedDragMoved;
    private bool _closed;

    public RecordingOverlayWindow()
        : this(placementStore: null, initialAnchor: null)
    {
    }

    private RecordingOverlayWindow(
        IRecordingOverlayPlacementStore? placementStore,
        RecordingOverlayAnchor? initialAnchor)
    {
        _placementStore = placementStore;
        _anchor = initialAnchor;
        _hasUserPlacement = initialAnchor.HasValue;
        InitializeComponent();
        Opened += Window_OnOpened;
        Closed += Window_OnClosed;
        PositionChanged += Window_OnPositionChanged;
        CollapsedContent.AddHandler(
            PointerPressedEvent,
            CollapsedContent_OnPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        CollapsedContent.AddHandler(
            PointerMovedEvent,
            CollapsedContent_OnPointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        CollapsedContent.AddHandler(
            PointerReleasedEvent,
            CollapsedContent_OnPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    public RecordingOverlayWindow(
        MainWindowViewModel viewModel,
        IRecordingOverlayPlacementStore placementStore,
        RecordingOverlayAnchor? initialAnchor)
        : this(
            placementStore ?? throw new ArgumentNullException(nameof(placementStore)),
            initialAnchor)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public void ShowNear(Window? placementOwner)
    {
        _placementOwner = placementOwner;
        if (!IsVisible)
        {
            Show();
        }

        QueuePositionFromPreferredAnchor();
    }

    public void ExpandForNewSession() => SetExpanded(expanded: true);

    public void HideSurface()
    {
        PersistCurrentPlacement();
        if (IsVisible)
        {
            Hide();
        }
    }

    public void CloseForShutdown()
    {
        CancelPlacementSettle();
        SavePlacementForShutdown();
        Close();
    }

    private void Window_OnOpened(object? sender, EventArgs args)
    {
        if (!_screensSubscribed)
        {
            Screens.Changed += Screens_OnChanged;
            _screensSubscribed = true;
        }

        QueuePositionFromPreferredAnchor();
    }

    private void Window_OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        CancelPlacementSettle();
        if (_screensSubscribed)
        {
            Screens.Changed -= Screens_OnChanged;
            _screensSubscribed = false;
        }

        PositionChanged -= Window_OnPositionChanged;
    }

    private void Screens_OnChanged(object? sender, EventArgs args) =>
        QueuePositionFromPreferredAnchor(saveAdjustedPlacement: _hasUserPlacement);

    private void Window_OnPositionChanged(object? sender, PixelPointEventArgs args)
    {
        if (_suppressPositionTracking || !_placementReady || _closed)
        {
            return;
        }

        if (_programmaticPosition == args.Point)
        {
            _programmaticPosition = null;
            return;
        }

        var screen = TryGetWindowScreen() ?? ResolveScreenForAnchor(_anchor);
        if (screen is null)
        {
            return;
        }

        _anchor = RecordingOverlayPlacementPolicy.AnchorFromPosition(
            args.Point,
            GetPixelSize(screen));
        _hasUserPlacement = true;
        SchedulePlacementSettle();
    }

    private void DragHandle_OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(args);
        args.Handled = true;
    }

    private void CollapseButton_OnClick(object? sender, RoutedEventArgs args) => SetExpanded(expanded: false);

    private void ExpandButton_OnClick(object? sender, RoutedEventArgs args)
    {
        if (!_collapsedDragMoved)
        {
            SetExpanded(expanded: true);
        }
    }

    private void CollapsedContent_OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _collapsedDragPointerStart = this.PointToScreen(args.GetPosition(this));
        _collapsedDragWindowStart = Position;
        _collapsedDragMoved = false;
        args.Pointer.Capture(CollapsedContent);
    }

    private void CollapsedContent_OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_collapsedDragPointerStart is not { } pointerStart
            || _collapsedDragWindowStart is not { } windowStart
            || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pointerPosition = this.PointToScreen(args.GetPosition(this));
        var deltaX = pointerPosition.X - pointerStart.X;
        var deltaY = pointerPosition.Y - pointerStart.Y;
        var threshold = checked((int)Math.Ceiling(CollapsedDragThreshold * RenderScaling));
        if (!_collapsedDragMoved
            && ((long)deltaX * deltaX) + ((long)deltaY * deltaY) < (long)threshold * threshold)
        {
            return;
        }

        _collapsedDragMoved = true;
        Position = new PixelPoint(windowStart.X + deltaX, windowStart.Y + deltaY);
        args.Handled = true;
    }

    private void CollapsedContent_OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        args.Pointer.Capture(null);
        _collapsedDragPointerStart = null;
        _collapsedDragWindowStart = null;
        Dispatcher.UIThread.Post(
            () => _collapsedDragMoved = false,
            DispatcherPriority.Input);
    }

    private void SetExpanded(bool expanded)
    {
        Width = expanded ? ExpandedWidth : CollapsedWidth;
        ExpandedContent.IsVisible = expanded;
        CollapsedContent.IsVisible = !expanded;
        QueuePositionFromPreferredAnchor();
    }

    private void QueuePositionFromPreferredAnchor(bool saveAdjustedPlacement = false) =>
        Dispatcher.UIThread.Post(
            () => PositionFromPreferredAnchor(saveAdjustedPlacement),
            DispatcherPriority.Render);

    private void PositionFromPreferredAnchor(bool saveAdjustedPlacement)
    {
        if (_closed || Width <= 0 || Height <= 0)
        {
            return;
        }

        var previousAnchor = _anchor;
        if (_anchor is { } anchor)
        {
            ApplyAnchor(anchor);
        }
        else
        {
            ApplyDefaultPosition();
        }

        _placementReady = true;
        if (saveAdjustedPlacement
            && _hasUserPlacement
            && previousAnchor != _anchor)
        {
            PersistCurrentPlacement();
        }
    }

    private void ApplyDefaultPosition()
    {
        var screen = ForegroundScreenLocator.TryGet(Screens)
            ?? TryGetPlacementScreen()
            ?? Screens.Primary
            ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var size = GetPixelSize(screen);
        var position = RecordingOverlayPlacementPolicy.DefaultAtRightEdge(
            screen.WorkingArea,
            size,
            GetPixelMargin(screen));
        SetProgrammaticPosition(position);
        _anchor = RecordingOverlayPlacementPolicy.AnchorFromPosition(position, size);
    }

    private void ApplyAnchor(RecordingOverlayAnchor anchor)
    {
        var screen = ResolveScreenForAnchor(anchor);
        if (screen is null)
        {
            ApplyDefaultPosition();
            return;
        }

        var size = GetPixelSize(screen);
        var position = RecordingOverlayPlacementPolicy.PositionAroundAnchor(
            anchor,
            size,
            screen.WorkingArea,
            GetPixelMargin(screen));
        SetProgrammaticPosition(position);
        _anchor = RecordingOverlayPlacementPolicy.AnchorFromPosition(position, size);
    }

    private void SetProgrammaticPosition(PixelPoint position)
    {
        _suppressPositionTracking = true;
        _programmaticPosition = position;
        try
        {
            Position = position;
        }
        finally
        {
            _suppressPositionTracking = false;
        }
    }

    private void SchedulePlacementSettle()
    {
        CancelPlacementSettle();
        _placementSettleCancellation = new CancellationTokenSource();
        _ = SettleAndPersistPlacementAsync(_placementSettleCancellation.Token);
    }

    private async Task SettleAndPersistPlacementAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PlacementSettleDelay, cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(
                SettleAndPersistPlacement,
                DispatcherPriority.Render,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SettleAndPersistPlacement()
    {
        if (_closed || _anchor is not { } anchor)
        {
            return;
        }

        ApplyAnchor(anchor);
        PersistCurrentPlacement();
    }

    private void PersistCurrentPlacement()
    {
        CancelPlacementSettle();
        if (!_hasUserPlacement || _placementStore is null || _anchor is not { } anchor)
        {
            return;
        }

        _ = SavePlacementAsync(anchor);
    }

    private async Task SavePlacementAsync(RecordingOverlayAnchor anchor)
    {
        try
        {
            await _placementStore!.SaveAsync(anchor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Placement is a convenience and must not interrupt recording controls.
        }
    }

    private void SavePlacementForShutdown()
    {
        if (!_hasUserPlacement || _placementStore is null || _anchor is not { } anchor)
        {
            return;
        }

        try
        {
            _placementStore.SaveAsync(anchor, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Placement persistence cannot block a clean application shutdown.
        }
    }

    private void CancelPlacementSettle()
    {
        var cancellation = Interlocked.Exchange(ref _placementSettleCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private Screen? ResolveScreenForAnchor(RecordingOverlayAnchor? anchor)
    {
        if (anchor is null || Screens.All.Count == 0)
        {
            return TryGetWindowScreen()
                ?? TryGetPlacementScreen()
                ?? Screens.Primary
                ?? Screens.All.FirstOrDefault();
        }

        var selectedArea = RecordingOverlayPlacementPolicy.SelectWorkingArea(
            anchor.Value,
            Screens.All.Select(static screen => screen.WorkingArea).ToArray());
        return Screens.All.FirstOrDefault(screen => screen.WorkingArea == selectedArea)
            ?? Screens.Primary
            ?? Screens.All.FirstOrDefault();
    }

    private Screen? TryGetWindowScreen()
    {
        try
        {
            return Screens.ScreenFromWindow(this);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
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

    private PixelSize GetPixelSize(Screen screen) =>
        new(
            checked((int)Math.Ceiling(Width * screen.Scaling)),
            checked((int)Math.Ceiling(Height * screen.Scaling)));

    private static int GetPixelMargin(Screen screen) =>
        checked((int)Math.Ceiling(EdgeMargin * screen.Scaling));
}
