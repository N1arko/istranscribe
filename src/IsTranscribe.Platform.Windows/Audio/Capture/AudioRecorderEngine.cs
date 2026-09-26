using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
public sealed class AudioRecorderEngine : IAudioRecorderEngine
{
    private readonly BootstrapFileLogger _logger;
    private readonly IAudioCaptureAdapterFactory _adapterFactory;
    private readonly IAudioArtifactPreparer _artifactPreparer;
    private AudioFoundationCapabilitySnapshot _capabilitySnapshot = new(
        ProcessLoopbackSupported: false,
        DeviceLoopbackSupported: false,
        MicrophoneCaptureSupported: false,
        AudioSessionObservationSupported: false,
        DeviceNotificationsSupported: false);

    public AudioRecorderEngine(BootstrapFileLogger logger)
        : this(logger, new AudioCaptureAdapterFactory(logger), new AudioMixArtifactBuilder())
    {
    }

    internal AudioRecorderEngine(
        BootstrapFileLogger logger,
        IAudioCaptureAdapterFactory adapterFactory,
        IAudioArtifactPreparer artifactPreparer)
    {
        _logger = logger;
        _adapterFactory = adapterFactory;
        _artifactPreparer = artifactPreparer;
    }

    public void UpdateCapabilitySnapshot(AudioFoundationCapabilitySnapshot capabilitySnapshot) =>
        _capabilitySnapshot = capabilitySnapshot;

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#recorder-engine.adapters
    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#capability.degraded
    public async ValueTask<AudioRecorderSession> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateCapabilities(request);

        var startBuffered = request.Mode == AudioCaptureMode.Ask && request.PrebufferSeconds > 0;
        var adapters = request.Sources
            .Select(source => _adapterFactory.Create(request, source, startBuffered ? request.PrebufferSeconds : 0))
            .ToArray();

        try
        {
            // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
            var sessionTimelineOriginUtc = DateTimeOffset.UtcNow;
            foreach (var adapter in adapters)
            {
                await adapter.StartAsync(
                    persistImmediately: !startBuffered,
                    sessionTimelineOriginUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.Info($"Audio recorder session started. SessionId={request.SessionId:N}, Sources={string.Join(",", request.Sources.Select(source => source.Kind))}.");

            return new AudioRecorderSession(
                _logger,
                request,
                adapters,
                _artifactPreparer,
                isPersistingAudio: !startBuffered);
        }
        catch
        {
            foreach (var adapter in adapters)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private void ValidateCapabilities(AudioCaptureRequest request)
    {
        foreach (var source in request.Sources)
        {
            switch (source.Kind)
            {
                case AudioCaptureSourceKind.ProcessOutput when !_capabilitySnapshot.ProcessLoopbackSupported:
                    throw new NotSupportedException("Process loopback is unavailable on this Windows build.");
                case AudioCaptureSourceKind.DeviceLoopback when !_capabilitySnapshot.DeviceLoopbackSupported:
                    throw new NotSupportedException("Device loopback capture is unavailable on this host.");
                case AudioCaptureSourceKind.Microphone when !_capabilitySnapshot.MicrophoneCaptureSupported:
                    throw new NotSupportedException("Microphone capture is unavailable on this host.");
            }
        }
    }
}
