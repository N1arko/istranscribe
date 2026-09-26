namespace IsTranscribe.Host.Audio.Capture;

public interface IAudioRecorderEngine
{
    ValueTask<AudioRecorderSession> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken);
}
