namespace IsTranscribe.Host.Audio.Processes;

public interface IProcessWatcher : IAsyncDisposable
{
    event EventHandler<ProcessWatcherSnapshot>? SnapshotChanged;

    ProcessWatcherSnapshot CurrentSnapshot { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    void UpdateWatchList(IEnumerable<string> processNames);
}
