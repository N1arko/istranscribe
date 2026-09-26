using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IsTranscribe.Host.Audio.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioDeviceManager : IAudioDeviceManager
{
    private readonly BootstrapFileLogger _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notificationClient;
    private readonly object _gate = new();

    private bool _started;
    private bool _disposed;

    public WindowsAudioDeviceManager(BootstrapFileLogger logger)
    {
        _logger = logger;
        _enumerator = new MMDeviceEnumerator();
        _notificationClient = new NotificationClient(this);
        CurrentSnapshot = AudioDeviceInventorySnapshot.Empty;
    }

    public event EventHandler<AudioDeviceInventorySnapshot>? SnapshotChanged;

    public AudioDeviceInventorySnapshot CurrentSnapshot { get; private set; }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.device-manager
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            MarshalThrowIfFailed(_enumerator.RegisterEndpointNotificationCallback(_notificationClient), "register device notification callback");
            _started = true;
            PublishSnapshot("initial inventory");
        }
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.device-manager
    public void Dispose()
    {
        bool unregisterNotifications;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            unregisterNotifications = _started;
            _started = false;
            _disposed = true;
        }

        // Core Audio may finish an in-flight notification synchronously while the
        // callback is being unregistered. Keep that COM call outside _gate so a
        // callback can observe _started == false and return without deadlocking.
        try
        {
            if (unregisterNotifications)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
        }
        finally
        {
            _enumerator.Dispose();
        }
    }

    internal void HandleNotification(string origin)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            PublishSnapshot(origin);
        }
    }

    private void PublishSnapshot(string origin)
    {
        try
        {
            var snapshot = BuildSnapshot(_enumerator, TimeProvider.System.GetUtcNow());
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
            _logger.Info($"Audio device inventory refreshed ({origin}). Render={snapshot.RenderDevices.Count}, Capture={snapshot.CaptureDevices.Count}.");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Failed to refresh audio device inventory ({origin}).");
        }
    }

    internal static AudioDeviceInventorySnapshot BuildSnapshot(MMDeviceEnumerator enumerator, DateTimeOffset observedAtUtc)
    {
        var defaultRenderId = TryGetDefaultDeviceId(enumerator, DataFlow.Render);
        var defaultCaptureId = TryGetDefaultDeviceId(enumerator, DataFlow.Capture);

        var renderDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceSnapshot(
                device.ID,
                device.FriendlyName,
                AudioDeviceKind.Render,
                IsDefault: string.Equals(device.ID, defaultRenderId, StringComparison.Ordinal),
                IsActive: device.State == DeviceState.Active))
            .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var captureDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioDeviceSnapshot(
                device.ID,
                device.FriendlyName,
                AudioDeviceKind.Capture,
                IsDefault: string.Equals(device.ID, defaultCaptureId, StringComparison.Ordinal),
                IsActive: device.State == DeviceState.Active))
            .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AudioDeviceInventorySnapshot(observedAtUtc, renderDevices, captureDevices);
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
        {
            return null;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return device.ID;
    }

    private static void MarshalThrowIfFailed(int hr, string operation)
    {
        if (hr >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hr:X8}.");
    }

    private sealed class NotificationClient(WindowsAudioDeviceManager owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            owner.HandleNotification($"state:{deviceId}:{newState}");

        public void OnDeviceAdded(string pwstrDeviceId) =>
            owner.HandleNotification($"added:{pwstrDeviceId}");

        public void OnDeviceRemoved(string deviceId) =>
            owner.HandleNotification($"removed:{deviceId}");

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia)
            {
                owner.HandleNotification($"default:{flow}:{defaultDeviceId}");
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
            owner.HandleNotification($"property:{pwstrDeviceId}");
    }
}
