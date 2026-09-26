using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
internal sealed class WasapiMicrophoneCaptureAdapter(
    BootstrapFileLogger logger,
    AudioCaptureSourceRequest source,
    string artifactPath,
    int prebufferSeconds) : WasapiCaptureAdapterBase(logger, source, artifactPath, prebufferSeconds)
{
    protected override AudioCaptureArtifactKind ArtifactKind => AudioCaptureArtifactKind.Microphone;

    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiCapture(device);
}
