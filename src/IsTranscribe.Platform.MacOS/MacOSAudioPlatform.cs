using IsTranscribe.Core.Audio;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Core Audio device/process observation and bounded live PCM capture.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// </remarks>
public sealed class MacOSAudioPlatform : IAudioPlatform, IAudioPlatformCapabilityRefresher
{
    private const int LiveBufferSeconds = 30;
    private readonly IMacOSAudioNative _native;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly TimeSpan _pollInterval;
    private readonly object _streamGate = new();
    private readonly List<ActiveStream> _activeStreams = [];
    private HashSet<string> _watchedNames = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private bool _started;
    private bool _disposed;

    public MacOSAudioPlatform()
        : this(new MacOSAudioNative(), TimeSpan.FromSeconds(1))
    {
    }

    internal MacOSAudioPlatform(IMacOSAudioNative native, TimeSpan pollInterval)
    {
        _native = native;
        _pollInterval = pollInterval;
    }

    public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

    public AudioPlatformSnapshot Snapshot { get; private set; } = AudioPlatformSnapshot.Empty;

    internal double HostTicksPerSecond => _native.HostTicksPerSecond;

    public async ValueTask StartAsync(
        IReadOnlyCollection<string> watchedProcessNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchedProcessNames);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetWatchedNames(watchedProcessNames);
            PublishSnapshot();
            if (_started)
            {
                return;
            }

            _started = true;
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(processNames);
        SetWatchedNames(processNames);
        PublishSnapshot();
    }

    public ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        PublishSnapshot();
        return ValueTask.CompletedTask;
    }

    public MacOSPcmStream StartProcessPcmStream(int processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        EnsureStarted();
        if (_native.EnumerateProcesses().All(process => process.ProcessId != processId))
        {
            throw new InvalidOperationException($"Audio process '{processId}' is unavailable.");
        }

        var stream = new MacOSPcmStream(
            48_000 * LiveBufferSeconds,
            callback => _native.StartProcessCapture(processId, callback));
        RegisterStream(new ActiveStream(stream, processId, null, false));
        return stream;
    }

    public MacOSPcmStream StartSystemOutputPcmStream(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        EnsureStarted();
        if (_native.EnumerateDevices().All(device => !device.HasOutput || device.Id != deviceId))
        {
            throw new InvalidOperationException($"Output device '{deviceId}' is unavailable.");
        }

        var stream = new MacOSPcmStream(
            48_000 * LiveBufferSeconds,
            callback => _native.StartSystemOutputCapture(callback));
        RegisterStream(new ActiveStream(stream, null, deviceId, false));
        return stream;
    }

    public MacOSPcmStream StartMicrophonePcmStream(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        EnsureStarted();
        if (_native.MicrophonePermission != MacOSMicrophonePermission.Granted)
        {
            throw new UnauthorizedAccessException("Microphone permission is required for capture.");
        }

        var device = _native.EnumerateDevices().FirstOrDefault(candidate =>
            candidate.HasInput && string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));
        if (device is null)
        {
            throw new InvalidOperationException($"Microphone '{deviceId}' is unavailable.");
        }

        var stream = new MacOSPcmStream(
            48_000 * LiveBufferSeconds,
            callback => _native.StartMicrophoneCapture(deviceId, callback));
        RegisterStream(new ActiveStream(stream, null, deviceId, true));
        return stream;
    }

    public ValueTask<IAudioCaptureSession> StartCaptureAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();
        return ValueTask.FromResult<IAudioCaptureSession>(MacOSAudioCaptureSession.Start(this, request));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_streamGate)
            {
                foreach (var active in _activeStreams)
                {
                    active.Stream.Dispose();
                }

                _activeStreams.Clear();
            }
            if (_monitorCancellation is not null)
            {
                await _monitorCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (_monitorTask is not null)
        {
            await _monitorTask.ConfigureAwait(false);
        }

        _monitorCancellation?.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PublishSnapshot();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PublishSnapshot()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var devices = _native.EnumerateDevices();
        var processes = _native.EnumerateProcesses();
        var processFamilies = processes
            .GroupBy(static process => MacOSProcessIdentity.Normalize(process.Name, process.BundleId), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var members = group.OrderByDescending(static process => process.IsRunningOutput).ThenBy(static process => process.ProcessId).ToArray();
                return new
                {
                    Name = group.Key,
                    Root = members[0],
                    Members = members,
                    RunningOutput = members.Any(static process => process.IsRunningOutput)
                };
            })
            .ToArray();
        var defaultOutputId = devices.FirstOrDefault(static device => device.IsDefaultOutput)?.Id ?? string.Empty;
        var microphonePermission = _native.MicrophonePermission;
        var capabilities = CreateCapabilities(microphonePermission);
        ReconcileStreams(processes, devices, microphonePermission);
        var snapshot = new AudioPlatformSnapshot(
            observedAt,
            capabilities,
            devices.Where(static device => device.HasOutput).Select(static device => new AudioEndpointSnapshot(
                device.Id,
                device.Name,
                device.IsDefaultOutput,
                true)).ToArray(),
            devices.Where(static device => device.HasInput).Select(static device => new AudioEndpointSnapshot(
                device.Id,
                device.Name,
                device.IsDefaultInput,
                true)).ToArray(),
            processFamilies.Select(static family => new ObservedProcessSnapshot(
                family.Root.ProcessId,
                family.Name,
                family.Members.Select(static process => process.ProcessId).ToHashSet())).ToArray(),
            processFamilies.Select(family => new AudioSignalSnapshot(
                observedAt,
                family.Name,
                family.Root.ProcessId,
                family.RunningOutput ? "active" : "inactive",
                family.RunningOutput ? -40d : -100d,
                defaultOutputId,
                family.Members.Any(IsWatched),
                family.Members.Any(IsWatched))).ToArray());

        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static AudioPlatformCapabilities CreateCapabilities(MacOSMicrophonePermission permission)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return new AudioPlatformCapabilities(
                false,
                false,
                "Core Audio process taps require macOS 14.2 or later.",
                "macos_version_unsupported");
        }

        return permission switch
        {
            MacOSMicrophonePermission.Denied => new AudioPlatformCapabilities(
                true,
                true,
                "Core Audio process capture is available; microphone permission is denied.",
                "microphone_permission_denied"),
            MacOSMicrophonePermission.Undetermined => new AudioPlatformCapabilities(
                true,
                true,
                "Core Audio process capture is available; microphone permission is required.",
                "microphone_permission_required"),
            _ => new AudioPlatformCapabilities(true, true, "Core Audio process and microphone capture are available.")
        };
    }

    private bool IsWatched(MacOSNativeProcess process) =>
        _watchedNames.Any(name =>
            string.Equals(name, process.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, MacOSProcessIdentity.Normalize(process.Name, process.BundleId), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, process.BundleId, StringComparison.OrdinalIgnoreCase)
            || process.BundleId.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase));

    private void SetWatchedNames(IEnumerable<string> processNames) =>
        _watchedNames = processNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException("macOS audio platform has not been started.");
        }
    }

    private void RegisterStream(ActiveStream stream)
    {
        lock (_streamGate)
        {
            _activeStreams.Add(stream);
        }
    }

    private void ReconcileStreams(
        IReadOnlyList<MacOSNativeProcess> processes,
        IReadOnlyList<MacOSNativeDevice> devices,
        MacOSMicrophonePermission permission)
    {
        lock (_streamGate)
        {
            foreach (var active in _activeStreams)
            {
                if (active.Stream.State != MacOSPcmStreamState.Running)
                {
                    continue;
                }

                if (active.ProcessId is int processId
                    && processes.All(process => process.ProcessId != processId))
                {
                    active.Stream.Fail("process_exited");
                }
                else if (active.IsMicrophone
                         && active.DeviceId is string deviceId
                         && permission != MacOSMicrophonePermission.Granted)
                {
                    active.Stream.Fail("microphone_permission_revoked");
                }
                else if (active.DeviceId is string selectedDeviceId
                         && devices.All(device => device.Id != selectedDeviceId
                             || (active.IsMicrophone ? !device.HasInput : !device.HasOutput)))
                {
                    active.Stream.Fail("device_unavailable");
                }
            }

            _activeStreams.RemoveAll(static active => active.Stream.State != MacOSPcmStreamState.Running);
        }
    }

    private sealed record ActiveStream(
        MacOSPcmStream Stream,
        int? ProcessId,
        string? DeviceId,
        bool IsMicrophone);
}
