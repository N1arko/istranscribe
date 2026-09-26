using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;

namespace IsTranscribe.Host.Audio.Sessions;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioSessionWatcher(
    BootstrapFileLogger logger,
    IAudioDeviceManager deviceManager,
    IProcessWatcher processWatcher) : IAudioSessionWatcher
{
    private static readonly TimeSpan ActiveRefreshCadence = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleRefreshCadence = TimeSpan.FromSeconds(2);

    private readonly BootstrapFileLogger _logger = logger;
    private readonly IAudioDeviceManager _deviceManager = deviceManager;
    private readonly IProcessWatcher _processWatcher = processWatcher;
    private readonly object _gate = new();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _wasActivelyObserving;
    private int _lastLoggedSessionCount = -1;
    private readonly Dictionary<int, string> _lastKnownRenderDevicePerProcess = new();

    public event EventHandler<IReadOnlyList<AudioSignalObservationSnapshot>>? SnapshotChanged;

    public event EventHandler<SourceDeviceDrift>? SourceDeviceDriftDetected;

    public IReadOnlyList<AudioSignalObservationSnapshot> CurrentSnapshot { get; private set; } = Array.Empty<AudioSignalObservationSnapshot>();

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

        if (loopTask is null || loopCancellation is null)
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

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.audio-session-watcher
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        PublishSnapshot("initial scan");

        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = _processWatcher.CurrentSnapshot.Processes.Count > 0
                ? ActiveRefreshCadence
                : IdleRefreshCadence;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            PublishSnapshot("periodic scan");
        }
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.audio-session-watcher
    private void PublishSnapshot(string origin)
    {
        try
        {
            var probes = CollectSessionProbes(_deviceManager.CurrentSnapshot);
            var snapshot = AudioSessionSnapshotCollector.Collect(
                _processWatcher.CurrentSnapshot.Processes,
                probes,
                TimeProvider.System.GetUtcNow());

            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
            DetectDeviceDrift(snapshot);
            if (snapshot.Count == 0)
            {
                PublishIdleSnapshot(origin);
                return;
            }

            if (!_wasActivelyObserving || _lastLoggedSessionCount != snapshot.Count || !string.Equals(origin, "periodic scan", StringComparison.Ordinal))
            {
                _logger.Info($"Audio session watcher refreshed ({origin}). Sessions={snapshot.Count}.");
                _lastLoggedSessionCount = snapshot.Count;
            }

            _wasActivelyObserving = true;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Audio session watcher refresh failed ({origin}).");
        }
    }

    private void PublishIdleSnapshot(string origin)
    {
        CurrentSnapshot = Array.Empty<AudioSignalObservationSnapshot>();
        SnapshotChanged?.Invoke(this, CurrentSnapshot);

        if (_wasActivelyObserving || !string.Equals(origin, "periodic scan", StringComparison.Ordinal))
        {
            _logger.Info($"Audio session watcher idle ({origin}). No observed processes.");
        }

        _wasActivelyObserving = false;
        _lastLoggedSessionCount = 0;
    }

    internal static IReadOnlyList<SessionProbe> CollectSessionProbes(AudioDeviceInventorySnapshot deviceInventory)
    {
        var probes = new List<SessionProbe>();

        using var enumerator = new MMDeviceEnumerator();
        foreach (var renderDevice in deviceInventory.RenderDevices.Where(device => device.IsActive))
        {
            using var device = enumerator.GetDevice(renderDevice.Id);
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                if (session.IsSystemSoundsSession)
                {
                    continue;
                }

                probes.Add(new SessionProbe(
                    checked((int)session.GetProcessID),
                    session.State,
                    session.AudioMeterInformation.MasterPeakValue,
                    renderDevice.Id));
            }
        }

        return probes;
    }

    private void DetectDeviceDrift(IReadOnlyList<AudioSignalObservationSnapshot> snapshot)
    {
        var currentDevicePerProcess = new Dictionary<int, string>();
        foreach (var entry in snapshot)
        {
            if (!entry.IsWhitelisted || string.IsNullOrEmpty(entry.RenderDeviceId))
            {
                continue;
            }

            // Use the first render device seen for this process (deterministic per snapshot).
            currentDevicePerProcess.TryAdd(entry.RootProcessId, entry.RenderDeviceId);
        }

        foreach (var (processId, currentDeviceId) in currentDevicePerProcess)
        {
            if (_lastKnownRenderDevicePerProcess.TryGetValue(processId, out var previousDeviceId)
                && !string.Equals(previousDeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                var processEntry = snapshot.FirstOrDefault(e => e.RootProcessId == processId);
                var drift = new SourceDeviceDrift(
                    processEntry?.RootProcessName ?? $"pid:{processId}",
                    processId,
                    previousDeviceId,
                    currentDeviceId);

                _logger.Info($"Source device drift detected: {drift.RootProcessName} moved from {drift.PreviousRenderDeviceId} to {drift.CurrentRenderDeviceId}.");
                SourceDeviceDriftDetected?.Invoke(this, drift);
            }
        }

        // Remove processes no longer observed, update current state.
        _lastKnownRenderDevicePerProcess.Clear();
        foreach (var (processId, deviceId) in currentDevicePerProcess)
        {
            _lastKnownRenderDevicePerProcess[processId] = deviceId;
        }
    }
}
