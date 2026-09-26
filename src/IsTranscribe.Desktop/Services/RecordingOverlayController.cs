using Avalonia.Controls;
using Avalonia.Threading;
using IsTranscribe.Application.Runtime;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Keeps one edge-anchored recording surface synchronized with runtime state.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#surface
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#lifecycle
/// </remarks>
public sealed class RecordingOverlayController : IDisposable
{
    private readonly IApplicationRuntime _runtime;
    private readonly Func<Window?> _placementOwner;
    private readonly Func<IRecordingOverlaySurface> _surfaceFactory;
    private readonly Action<Action> _dispatch;
    private IRecordingOverlaySurface? _surface;
    private Guid? _activeSessionId;
    private bool _started;
    private bool _disposed;

    public RecordingOverlayController(
        IApplicationRuntime runtime,
        Func<Window?>? placementOwner = null,
        Func<IRecordingOverlaySurface>? surfaceFactory = null,
        Action<Action>? dispatch = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _placementOwner = placementOwner ?? (static () => null);
        _surfaceFactory = surfaceFactory
            ?? throw new ArgumentNullException(nameof(surfaceFactory));
        _dispatch = dispatch ?? DispatchOnUiThread;
    }

    public bool IsVisible => _surface?.IsVisible == true;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
        ScheduleSnapshot(_runtime.Snapshot);
    }

    public void ApplySnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var activeMeeting = snapshot.Activity == ApplicationActivityState.Recording
            ? snapshot.ActiveMeeting
            : null;
        if (activeMeeting is null)
        {
            _activeSessionId = null;
            _surface?.HideSurface();
            return;
        }

        var surface = _surface ??= _surfaceFactory()
            ?? throw new InvalidOperationException("Recording overlay surface factory returned null.");
        if (_activeSessionId != activeMeeting.SessionId)
        {
            _activeSessionId = activeMeeting.SessionId;
            surface.ExpandForNewSession();
        }

        if (!surface.IsVisible)
        {
            surface.ShowNear(_placementOwner());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
        }

        _surface?.CloseForShutdown();
        _surface = null;
        _activeSessionId = null;
    }

    private void Runtime_OnSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot) =>
        ScheduleSnapshot(snapshot);

    private void ScheduleSnapshot(ApplicationRuntimeSnapshot snapshot) =>
        _dispatch(() =>
        {
            if (!_disposed)
            {
                ApplySnapshot(snapshot);
            }
        });

    private static void DispatchOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
