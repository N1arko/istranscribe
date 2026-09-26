using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Bootstrap;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio.Bootstrap;

[SupportedOSPlatform("windows")]
public sealed class AudioFoundationBackgroundServicesCoordinator(
    BootstrapFileLogger logger,
    IAudioDeviceManager deviceManager,
    IProcessWatcher processWatcher,
    IAudioSessionWatcher sessionWatcher,
    AudioRecorderEngine recorderEngine) : IBackgroundServicesCoordinator
{
    private readonly BootstrapFileLogger _logger = logger;
    private readonly IAudioDeviceManager _deviceManager = deviceManager;
    private readonly IProcessWatcher _processWatcher = processWatcher;
    private readonly IAudioSessionWatcher _sessionWatcher = sessionWatcher;
    private readonly AudioRecorderEngine _recorderEngine = recorderEngine;
    private bool _running;

    public AudioFoundationCapabilitySnapshot? CapabilitySnapshot { get; private set; }

    public IAudioRecorderEngine RecorderEngine => _recorderEngine;

    public async ValueTask StartAsync(HostCapabilitySnapshot capability, CancellationToken cancellationToken)
    {
        if (_running)
        {
            return;
        }

        CapabilitySnapshot = AudioFoundationCapabilitySnapshot.FromHostCapability(capability);
        _recorderEngine.UpdateCapabilitySnapshot(CapabilitySnapshot);
        _deviceManager.Start();
        await _processWatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        await _sessionWatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        _running = true;
        _logger.Info("Audio foundation background services started.");
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!_running)
        {
            return;
        }

        _deviceManager.Dispose();
        await _sessionWatcher.DisposeAsync().ConfigureAwait(false);
        await _processWatcher.DisposeAsync().ConfigureAwait(false);
        _running = false;
        _logger.Info("Audio foundation background services stopped.");
    }
}
