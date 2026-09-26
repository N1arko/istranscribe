using System.Diagnostics.Tracing;
using IsTranscribe.Core.Audio;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Counts forbidden capture starts while delegating every production audio operation.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal sealed class CountingAudioPlatform(IAudioPlatform inner) : IAudioPlatform
{
    private readonly IAudioPlatform _inner = inner;
    private int _captureStartCount;

    public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged
    {
        add => _inner.SnapshotChanged += value;
        remove => _inner.SnapshotChanged -= value;
    }

    public AudioPlatformSnapshot Snapshot => _inner.Snapshot;

    public int CaptureStartCount => Volatile.Read(ref _captureStartCount);

    public ValueTask StartAsync(
        IReadOnlyCollection<string> watchedProcessNames,
        CancellationToken cancellationToken) => _inner.StartAsync(watchedProcessNames, cancellationToken);

    public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames) =>
        _inner.UpdateWatchedProcessNames(processNames);

    public ValueTask<IAudioCaptureSession> StartCaptureAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _captureStartCount);
        return _inner.StartCaptureAsync(request, cancellationToken);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Observes managed HTTP events inside the isolated acceptance process.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal sealed class ManagedHttpEventCounter : EventListener
{
    private int _eventCount;

    public int EventCount => Volatile.Read(ref _eventCount);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("System.Net.Http", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.LogAlways);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
        Interlocked.Increment(ref _eventCount);
}

/// <summary>
/// Records transient filesystem creation under the three forbidden
/// pre-confirmation artifact roots, including files removed before final scan.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal sealed class FileCreationAudit : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _createdPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<FileSystemWatcher> _watchers;

    public FileCreationAudit(IReadOnlyList<string> roots)
    {
        _watchers = roots.Select(CreateWatcher).ToArray();
    }

    public int CreatedPathCount
    {
        get
        {
            lock (_gate)
            {
                return _createdPaths.Count;
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        watcher.Created += HandleCreated;
        watcher.Renamed += HandleRenamed;
        return watcher;
    }

    private void HandleCreated(object sender, FileSystemEventArgs eventArgs) => Record(eventArgs.FullPath);

    private void HandleRenamed(object sender, RenamedEventArgs eventArgs) => Record(eventArgs.FullPath);

    private void Record(string path)
    {
        lock (_gate)
        {
            _createdPaths.Add(Path.GetFullPath(path));
        }
    }
}
