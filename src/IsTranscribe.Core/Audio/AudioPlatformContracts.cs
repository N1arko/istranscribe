namespace IsTranscribe.Core.Audio;

/// <summary>
/// Platform-neutral view of the audio environment used by detection and recording orchestration.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// </remarks>
public interface IAudioPlatform : IAsyncDisposable
{
    event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

    AudioPlatformSnapshot Snapshot { get; }

    ValueTask StartAsync(IReadOnlyCollection<string> watchedProcessNames, CancellationToken cancellationToken);

    void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames);

    ValueTask<IAudioCaptureSession> StartCaptureAsync(AudioCaptureRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Optional extension implemented by audio adapters that can reassess host capture readiness in place.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </remarks>
public interface IAudioPlatformCapabilityRefresher
{
    ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken);
}

public sealed record AudioPlatformCapabilities(
    bool IsSupported,
    bool SupportsProcessOutputCapture,
    string Summary,
    string? BlockingReason = null)
{
    public static AudioPlatformCapabilities Unknown { get; } = new(
        IsSupported: false,
        SupportsProcessOutputCapture: false,
        Summary: "Audio platform has not been initialized.");
}

public sealed record AudioPlatformSnapshot(
    DateTimeOffset ObservedAtUtc,
    AudioPlatformCapabilities Capabilities,
    IReadOnlyList<AudioEndpointSnapshot> OutputDevices,
    IReadOnlyList<AudioEndpointSnapshot> Microphones,
    IReadOnlyList<ObservedProcessSnapshot> Processes,
    IReadOnlyList<AudioSignalSnapshot> Signals)
{
    public static AudioPlatformSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        AudioPlatformCapabilities.Unknown,
        [],
        [],
        [],
        []);
}

public sealed record AudioEndpointSnapshot(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsActive);

public sealed record ObservedProcessSnapshot(
    int RootProcessId,
    string ProcessName,
    IReadOnlySet<int> ProcessTreeIds);

public sealed record AudioSignalSnapshot(
    DateTimeOffset ObservedAtUtc,
    string ProcessName,
    int RootProcessId,
    string SessionState,
    double SignalLevelDbfs,
    string OutputDeviceId,
    bool IsWatchedProcess,
    bool IsProcessTreeMatch);
