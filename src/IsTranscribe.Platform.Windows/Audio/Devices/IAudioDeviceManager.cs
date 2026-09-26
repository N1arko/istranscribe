namespace IsTranscribe.Host.Audio.Devices;

public interface IAudioDeviceManager : IDisposable
{
    event EventHandler<AudioDeviceInventorySnapshot>? SnapshotChanged;

    AudioDeviceInventorySnapshot CurrentSnapshot { get; }

    void Start();
}
