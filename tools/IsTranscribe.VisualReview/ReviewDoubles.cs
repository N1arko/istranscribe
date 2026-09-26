using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Platform;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Side-effect-free runtime used to drive real desktop view models through canonical snapshots.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// </remarks>
internal sealed class ReviewApplicationRuntime(ApplicationRuntimeSnapshot snapshot) : IApplicationRuntime
{
    public event EventHandler<ApplicationRuntimeSnapshot>? SnapshotChanged;

    public ApplicationRuntimeSnapshot Snapshot { get; private set; } = snapshot;

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetServiceEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        Complete(cancellationToken);

    public ValueTask CompleteOnboardingAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask UpdateSettingsAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask ResolveMeetingPromptAsync(
        string candidateId,
        MeetingPromptUserAction action,
        CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask StartManualRecordingAsync(CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask PauseOrResumeAsync(CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask FinishRecordingAsync(CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask DiscardRecordingAsync(CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask RemoveRecentRecordingAsync(
        Guid sessionId,
        bool deleteAudioFile,
        CancellationToken cancellationToken) => Complete(cancellationToken);

    public void Publish(ApplicationRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ValueTask Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ReviewDesktopShell : IDesktopShell
{
    public ValueTask OpenFileAsync(string path, CancellationToken cancellationToken) => Complete(cancellationToken);

    public ValueTask OpenContainingFolderAsync(string path, CancellationToken cancellationToken) =>
        Complete(cancellationToken);

    public ValueTask OpenUriAsync(Uri uri, CancellationToken cancellationToken) => Complete(cancellationToken);

    private static ValueTask Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ReviewApplicationInstanceCoordinator : IApplicationInstanceCoordinator
{
    public event EventHandler? ActivationRequested;

    public ValueTask<ApplicationInstanceRole> RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ApplicationInstanceRole.Primary);
    }

    public ValueTask<bool> NotifyPrimaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivationRequested?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ReviewPlatformDescriptor : IPlatformDescriptor
{
    public string OperatingSystem => "Windows";

    public System.Runtime.InteropServices.Architecture Architecture =>
        System.Runtime.InteropServices.Architecture.X64;

    public bool IsReleaseArchitectureSupported => true;
}

internal sealed class ReviewActiveWorkAreaProvider : IActiveWorkAreaProvider
{
    public PlatformPoint? TryGetForegroundWindowCenter() => null;
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => 0;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) => new InertTimer();

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
