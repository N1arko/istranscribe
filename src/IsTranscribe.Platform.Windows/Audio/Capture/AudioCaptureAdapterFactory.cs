using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
internal sealed class AudioCaptureAdapterFactory(BootstrapFileLogger logger) : IAudioCaptureAdapterFactory
{
    private readonly BootstrapFileLogger _logger = logger;

    public IAudioCaptureAdapter Create(AudioCaptureRequest request, AudioCaptureSourceRequest source, int prebufferSeconds)
    {
        var artifactPath = GetRawArtifactPath(request.TempSessionDirectoryPath, source.Kind);

        return source.Kind switch
        {
            AudioCaptureSourceKind.ProcessOutput => new ProcessLoopbackCaptureAdapter(_logger, source, artifactPath, prebufferSeconds),
            AudioCaptureSourceKind.DeviceLoopback => new WasapiDeviceLoopbackCaptureAdapter(_logger, source, artifactPath, prebufferSeconds),
            AudioCaptureSourceKind.Microphone => new WasapiMicrophoneCaptureAdapter(_logger, source, artifactPath, prebufferSeconds),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown source kind.")
        };
    }

    private static string GetRawArtifactPath(string tempSessionDirectoryPath, AudioCaptureSourceKind kind) =>
        kind == AudioCaptureSourceKind.Microphone
            ? Path.Combine(tempSessionDirectoryPath, "mic.wav")
            : Path.Combine(tempSessionDirectoryPath, "output.wav");
}
