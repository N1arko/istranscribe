using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio.Processes;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessWatcher(BootstrapFileLogger logger) : IProcessWatcher
{
    private readonly BootstrapFileLogger _logger = logger;
    private readonly object _gate = new();
    private HashSet<string> _watchedProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;

    public event EventHandler<ProcessWatcherSnapshot>? SnapshotChanged;

    public ProcessWatcherSnapshot CurrentSnapshot { get; private set; } = ProcessWatcherSnapshot.Empty;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loopTask is not null)
            {
                return ValueTask.CompletedTask;
            }

            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = RunAsync(_loopCancellation.Token);
        }

        return ValueTask.CompletedTask;
    }

    public void UpdateWatchList(IEnumerable<string> processNames)
    {
        lock (_gate)
        {
            _watchedProcessNames = processNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        PublishSnapshot("watch list updated");
    }

    public async ValueTask DisposeAsync()
    {
        Task? loopTask;
        CancellationTokenSource? loopCancellation;

        lock (_gate)
        {
            loopTask = _loopTask;
            loopCancellation = _loopCancellation;
            _loopTask = null;
            _loopCancellation = null;
        }

        if (loopCancellation is null || loopTask is null)
        {
            return;
        }

        loopCancellation.Cancel();
        try
        {
            await loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loopCancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        PublishSnapshot("initial scan");

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            PublishSnapshot("periodic scan");
        }
    }

    private void PublishSnapshot(string origin)
    {
        string[] watchedProcesses;
        lock (_gate)
        {
            watchedProcesses = _watchedProcessNames.ToArray();
        }

        if (watchedProcesses.Length == 0)
        {
            CurrentSnapshot = new ProcessWatcherSnapshot(TimeProvider.System.GetUtcNow(), Array.Empty<ObservedProcessSnapshot>());
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            return;
        }

        try
        {
            var snapshot = ProcessTreeSnapshotBuilder.BuildSnapshot(watchedProcesses, TimeProvider.System.GetUtcNow());
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
            _logger.Info($"Process watcher refreshed ({origin}). Roots={snapshot.Processes.Count}.");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Process watcher refresh failed ({origin}).");
        }
    }
}
