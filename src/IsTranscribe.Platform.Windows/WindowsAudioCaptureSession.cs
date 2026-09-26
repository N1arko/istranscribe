using IsTranscribe.Core.Audio;
using HostArtifactKind = IsTranscribe.Host.Audio.Capture.AudioCaptureArtifactKind;
using HostCaptureSession = IsTranscribe.Host.Audio.Capture.AudioRecorderSession;
using HostCaptureSourceKind = IsTranscribe.Host.Audio.Capture.AudioCaptureSourceKind;
using HostCaptureState = IsTranscribe.Host.Audio.Capture.AudioRecorderState;
using HostSnapshot = IsTranscribe.Host.Audio.Capture.AudioRecorderSessionSnapshot;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Keeps the concrete Windows recorder session behind the Core capture boundary.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// </remarks>
internal sealed class WindowsAudioCaptureSession : IAudioCaptureSession
{
    private readonly HostCaptureSession _inner;
    private bool _disposed;

    public WindowsAudioCaptureSession(HostCaptureSession inner)
    {
        _inner = inner;
        Snapshot = Map(inner.Snapshot);
        _inner.SnapshotChanged += HandleSnapshotChanged;
    }

    public event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged;

    public AudioCaptureSessionSnapshot Snapshot { get; private set; }

    public ValueTask PauseAsync(CancellationToken cancellationToken) => _inner.PauseAsync(cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public ValueTask PromotePrebufferAsync(CancellationToken cancellationToken) =>
        _inner.PromotePrebufferAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.SnapshotChanged -= HandleSnapshotChanged;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleSnapshotChanged(object? sender, HostSnapshot snapshot)
    {
        Snapshot = Map(snapshot);
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private static AudioCaptureSessionSnapshot Map(HostSnapshot snapshot) => new(
        snapshot.SessionId,
        snapshot.State switch
        {
            HostCaptureState.Starting => AudioCaptureState.Starting,
            HostCaptureState.Running => AudioCaptureState.Running,
            HostCaptureState.Stopping => AudioCaptureState.Stopping,
            HostCaptureState.Completed => AudioCaptureState.Completed,
            HostCaptureState.Faulted => AudioCaptureState.Faulted,
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.State, "Unknown recorder state.")
        },
        snapshot.IsPersistingAudio,
        snapshot.Artifacts.Select(static artifact => new AudioCaptureArtifactSnapshot(
            artifact.Kind switch
            {
                HostArtifactKind.Output => AudioCaptureArtifactKind.Output,
                HostArtifactKind.Microphone => AudioCaptureArtifactKind.Microphone,
                HostArtifactKind.Mixed => AudioCaptureArtifactKind.Mixed,
                _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact.Kind, "Unknown artifact kind.")
            },
            artifact.Path,
            artifact.BytesWritten,
            artifact.FinalizedAtUtc,
            artifact.RelativeStartOffset)).ToArray(),
        snapshot.Faults.Select(static fault => new AudioCaptureFailureSnapshot(
            fault.OccurredAtUtc,
            fault.FailureKind.ToString(),
            fault.SourceKind switch
            {
                HostCaptureSourceKind.ProcessOutput => AudioCaptureSourceKind.ProcessOutput,
                HostCaptureSourceKind.DeviceLoopback => AudioCaptureSourceKind.DeviceLoopback,
                HostCaptureSourceKind.Microphone => AudioCaptureSourceKind.Microphone,
                null => null,
                _ => throw new ArgumentOutOfRangeException(nameof(fault), fault.SourceKind, "Unknown source kind.")
            },
            fault.Code,
            fault.Message,
            fault.ArtifactPath)).ToArray());
}
