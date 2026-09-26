using Avalonia.Controls;
using Avalonia.Threading;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;
using IsTranscribe.Desktop.Views;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Keeps the runtime pending prompt and the single non-modal desktop surface in sync.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// </remarks>
public sealed class AskPromptController : IAsyncDisposable
{
    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _localization;
    private readonly Func<Window?> _placementOwner;
    private readonly TimeProvider _timeProvider;
    private readonly bool _showAdvancedDiagnostics;
    private readonly Func<AskPromptViewModel, IAskPromptSurface> _surfaceFactory;
    private readonly Action<Action> _dispatch;
    private readonly ISystemNotificationService? _systemNotifications;
    private IAskPromptSurface? _surface;
    private AskPromptViewModel? _viewModel;
    private bool _started;
    private bool _disposed;

    public AskPromptController(
        IApplicationRuntime runtime,
        ILocalizationService localization,
        Func<Window?>? placementOwner = null,
        TimeProvider? timeProvider = null,
        bool showAdvancedDiagnostics = false,
        Func<AskPromptViewModel, IAskPromptSurface>? surfaceFactory = null,
        Action<Action>? dispatch = null,
        ISystemNotificationService? systemNotifications = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _placementOwner = placementOwner ?? (static () => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _showAdvancedDiagnostics = showAdvancedDiagnostics;
        _surfaceFactory = surfaceFactory ?? (static viewModel => new AskPromptWindow(viewModel));
        _dispatch = dispatch ?? DispatchOnUiThread;
        _systemNotifications = systemNotifications;
    }

    public AskPromptViewModel? CurrentPrompt => _viewModel;

    public bool IsPromptVisible => _surface is not null;

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

    /// <summary>
    /// Applies one runtime snapshot. Public for deterministic controller tests with a fake surface.
    /// </summary>
    public void ApplySnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (snapshot.PendingMeetingPrompt is not { } pending)
        {
            // Resolving the prompt clears it from the runtime snapshot before a confirmed
            // recording has finished starting. Keep the surface-owned cancellation token
            // alive until the command reports its final result.
            // @spec spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e
            if (_viewModel is { IsResolving: true })
            {
                return;
            }

            CloseCurrentPrompt();
            return;
        }

        if (_viewModel is { } current
            && string.Equals(current.CandidateId, pending.CandidateId, StringComparison.Ordinal))
        {
            current.UpdatePrompt(pending);
            return;
        }

        CloseCurrentPrompt();
        var viewModel = new AskPromptViewModel(
            _runtime,
            _localization,
            pending,
            _timeProvider,
            _showAdvancedDiagnostics);
        var surface = _surfaceFactory(viewModel)
            ?? throw new InvalidOperationException("Ask prompt surface factory returned null.");

        _viewModel = viewModel;
        _surface = surface;
        viewModel.Resolved += ViewModel_OnResolved;
        surface.Closed += Surface_OnClosed;
        surface.ShowNear(_placementOwner());
        if (_systemNotifications is not null)
        {
            try
            {
                _systemNotifications.ShowAsync(viewModel.Title, viewModel.AppLine, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
            {
                // The accessible in-app Ask surface remains the primary action path.
            }
        }
        _ = ObserveCountdownAsync(viewModel);
    }

    public async ValueTask DisposeAsync()
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

        var current = _viewModel;
        if (current is { IsResolved: false })
        {
            await current.SkipAsync().ConfigureAwait(true);
        }

        _dispatch(() => CloseCurrentPrompt());
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

    private void ViewModel_OnResolved(object? sender, AskPromptResolvedEventArgs args)
    {
        if (sender is not AskPromptViewModel resolved)
        {
            return;
        }

        _dispatch(() => CloseCurrentPrompt(resolved));
    }

    private void Surface_OnClosed(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, _surface) || _viewModel is not { } closedViewModel)
        {
            return;
        }

        if (!closedViewModel.IsResolved && !_disposed)
        {
            _ = closedViewModel.SkipAsync();
        }
    }

    private void CloseCurrentPrompt(AskPromptViewModel? expectedViewModel = null)
    {
        if (expectedViewModel is not null && !ReferenceEquals(expectedViewModel, _viewModel))
        {
            return;
        }

        var surface = _surface;
        var viewModel = _viewModel;
        _surface = null;
        _viewModel = null;

        if (viewModel is not null)
        {
            viewModel.Resolved -= ViewModel_OnResolved;
        }

        if (surface is not null)
        {
            surface.Closed -= Surface_OnClosed;
            surface.CloseWithoutResolution();
        }

        viewModel?.Dispose();
    }

    private static async Task ObserveCountdownAsync(AskPromptViewModel viewModel)
    {
        try
        {
            await viewModel.RunCountdownAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // Surface replacement can dispose the view model between timer continuations.
        }
    }

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
