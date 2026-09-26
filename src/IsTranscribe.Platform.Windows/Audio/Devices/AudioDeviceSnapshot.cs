namespace IsTranscribe.Host.Audio.Devices;

public enum AudioDeviceKind
{
    Render,
    Capture
}

public sealed record AudioDeviceSnapshot(
    string Id,
    string FriendlyName,
    AudioDeviceKind Kind,
    bool IsDefault,
    bool IsActive);

public sealed record AudioDeviceInventorySnapshot(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<AudioDeviceSnapshot> RenderDevices,
    IReadOnlyList<AudioDeviceSnapshot> CaptureDevices)
{
    public static AudioDeviceInventorySnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        Array.Empty<AudioDeviceSnapshot>(),
        Array.Empty<AudioDeviceSnapshot>());
}
