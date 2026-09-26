using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Host.Audio.Bootstrap;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using CoreCaptureRequest = IsTranscribe.Core.Audio.AudioCaptureRequest;
using CoreCaptureSourceKind = IsTranscribe.Core.Audio.AudioCaptureSourceKind;
using HostCaptureMode = IsTranscribe.Host.Audio.Capture.AudioCaptureMode;
using HostCaptureRequest = IsTranscribe.Host.Audio.Capture.AudioCaptureRequest;
using HostCaptureSource = IsTranscribe.Host.Audio.Capture.AudioCaptureSourceRequest;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Windows WASAPI/process implementation exposed exclusively through Core-owned contracts.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioPlatform : IAudioPlatform, IAudioPlatformCapabilityRefresher
{
    private readonly BootstrapFileLogger _logger;
    private readonly IHostCapabilityAssessor _capabilityAssessor;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly WindowsAudioDeviceManager _deviceManager;
    private readonly WindowsProcessWatcher _processWatcher;
    private readonly WindowsAudioSessionWatcher _sessionWatcher;
    private readonly AudioRecorderEngine _recorderEngine;
    private readonly AudioFoundationBackgroundServicesCoordinator _backgroundServices;
    private HostCapabilitySnapshot? _hostCapability;
    private bool _backgroundServicesStarted;
    private bool _started;
    private bool _disposed;

    public WindowsAudioPlatform(BootstrapFileLogger logger)
        : this(logger, new WindowsCapabilityAssessor(new HostOperatingSystemDetector()))
    {
    }

    internal WindowsAudioPlatform(
        BootstrapFileLogger logger,
        IHostCapabilityAssessor capabilityAssessor)
    {
        _logger = logger;
        _capabilityAssessor = capabilityAssessor;
        _deviceManager = new WindowsAudioDeviceManager(logger);
        _processWatcher = new WindowsProcessWatcher(logger);
        _sessionWatcher = new WindowsAudioSessionWatcher(logger, _deviceManager, _processWatcher);
        _recorderEngine = new AudioRecorderEngine(logger);
        _backgroundServices = new AudioFoundationBackgroundServicesCoordinator(
            logger,
            _deviceManager,
            _processWatcher,
            _sessionWatcher,
            _recorderEngine);

        _deviceManager.SnapshotChanged += HandleDeviceSnapshotChanged;
        _processWatcher.SnapshotChanged += HandleProcessSnapshotChanged;
        _sessionWatcher.SnapshotChanged += HandleSignalChanged;
    }

    public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

    public AudioPlatformSnapshot Snapshot { get; private set; } = AudioPlatformSnapshot.Empty;

    public async ValueTask StartAsync(
        IReadOnlyCollection<string> watchedProcessNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchedProcessNames);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                _processWatcher.UpdateWatchList(watchedProcessNames);
                PublishSnapshot();
                return;
            }

            _hostCapability = _capabilityAssessor.Assess();
            if (_hostCapability.State != HostCapabilityState.Blocked)
            {
                await _backgroundServices.StartAsync(_hostCapability, cancellationToken).ConfigureAwait(false);
                _backgroundServicesStarted = true;
            }

            _processWatcher.UpdateWatchList(watchedProcessNames);
            _started = true;
            PublishSnapshot();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostCapability = _capabilityAssessor.Assess();
            if (_started
                && !_backgroundServicesStarted
                && _hostCapability.State != HostCapabilityState.Blocked)
            {
                await _backgroundServices.StartAsync(_hostCapability, cancellationToken).ConfigureAwait(false);
                _backgroundServicesStarted = true;
            }

            PublishSnapshot();
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
        _processWatcher.UpdateWatchList(processNames);
        PublishSnapshot();
    }

    public async ValueTask<IAudioCaptureSession> StartCaptureAsync(
        CoreCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request.Validate();

        if (!_started || _hostCapability is null)
        {
            throw new InvalidOperationException("Windows audio platform has not been started.");
        }

        if (_hostCapability.State == HostCapabilityState.Blocked)
        {
            throw new PlatformNotSupportedException(_hostCapability.BlockingReason ?? _hostCapability.Summary);
        }

        var hostRequest = new HostCaptureRequest(
            request.SessionId,
            request.Mode == IsTranscribe.Core.Audio.AudioCaptureMode.Ask
                ? HostCaptureMode.Ask
                : HostCaptureMode.Manual,
            request.Sources.Select(MapSource).ToArray(),
            request.TempSessionDirectoryPath,
            request.PrebufferSeconds,
            request.CreateMixedArtifact);

        var session = await _recorderEngine.StartAsync(hostRequest, cancellationToken).ConfigureAwait(false);
        return new WindowsAudioCaptureSession(session);
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
            _deviceManager.SnapshotChanged -= HandleDeviceSnapshotChanged;
            _processWatcher.SnapshotChanged -= HandleProcessSnapshotChanged;
            _sessionWatcher.SnapshotChanged -= HandleSignalChanged;

            if (_backgroundServicesStarted)
            {
                await _backgroundServices.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _backgroundServicesStarted = false;
            }
            else
            {
                _deviceManager.Dispose();
                await _sessionWatcher.DisposeAsync().ConfigureAwait(false);
                await _processWatcher.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static HostCaptureSource MapSource(IsTranscribe.Core.Audio.AudioCaptureSource source) => source.Kind switch
    {
        CoreCaptureSourceKind.ProcessOutput => HostCaptureSource.ProcessOutput(
            source.RootProcessId!.Value,
            source.ProcessName),
        CoreCaptureSourceKind.DeviceLoopback => HostCaptureSource.DeviceLoopback(source.DeviceId!),
        CoreCaptureSourceKind.Microphone => HostCaptureSource.Microphone(source.DeviceId!),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown capture source kind.")
    };

    private void HandleDeviceSnapshotChanged(object? sender, AudioDeviceInventorySnapshot snapshot) =>
        PublishSnapshot();

    private void HandleProcessSnapshotChanged(object? sender, ProcessWatcherSnapshot snapshot) =>
        PublishSnapshot();

    private void HandleSignalChanged(
        object? sender,
        IReadOnlyList<IsTranscribe.Host.Audio.Sessions.AudioSignalObservationSnapshot> args) => PublishSnapshot();

    private void PublishSnapshot()
    {
        var capability = _hostCapability is null
            ? AudioPlatformCapabilities.Unknown
            : new AudioPlatformCapabilities(
                IsSupported: _hostCapability.State != HostCapabilityState.Blocked,
                SupportsProcessOutputCapture: _hostCapability.ProcessLoopbackAvailable,
                Summary: _hostCapability.Summary,
                BlockingReason: _hostCapability.BlockingReason);

        Snapshot = new AudioPlatformSnapshot(
            DateTimeOffset.UtcNow,
            capability,
            _deviceManager.CurrentSnapshot.RenderDevices.Select(MapEndpoint).ToArray(),
            _deviceManager.CurrentSnapshot.CaptureDevices.Select(MapEndpoint).ToArray(),
            _processWatcher.CurrentSnapshot.Processes
                .Select(static process => new IsTranscribe.Core.Audio.ObservedProcessSnapshot(
                    process.RootProcessId,
                    process.RootProcessName,
                    process.ProcessTreeIds))
                .ToArray(),
            _sessionWatcher.CurrentSnapshot
                .Select(static signal => new IsTranscribe.Core.Audio.AudioSignalSnapshot(
                    signal.ObservedAtUtc,
                    signal.RootProcessName,
                    signal.RootProcessId,
                    signal.AudioSessionState,
                    signal.SignalLevelDbfs,
                    signal.RenderDeviceId,
                    signal.IsWhitelisted,
                    signal.IsProcessTreeMatch))
                .ToArray());

        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private static AudioEndpointSnapshot MapEndpoint(AudioDeviceSnapshot endpoint) => new(
        endpoint.Id,
        endpoint.FriendlyName,
        endpoint.IsDefault,
        endpoint.IsActive);
}
