namespace IsTranscribe.Host.Audio.Sessions;

public interface IAudioSessionWatcher : IAsyncDisposable
{
    event EventHandler<IReadOnlyList<AudioSignalObservationSnapshot>>? SnapshotChanged;

    event EventHandler<SourceDeviceDrift>? SourceDeviceDriftDetected;

    IReadOnlyList<AudioSignalObservationSnapshot> CurrentSnapshot { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);
}

public sealed record SourceDeviceDrift(
    string RootProcessName,
    int RootProcessId,
    string PreviousRenderDeviceId,
    string CurrentRenderDeviceId);
