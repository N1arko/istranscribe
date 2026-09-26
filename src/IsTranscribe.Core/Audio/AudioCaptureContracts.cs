namespace IsTranscribe.Core.Audio;

/// <summary>
/// Core-owned recording boundary. Platform adapters keep native handles and implementation types internal.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// </remarks>
public interface IAudioCaptureSession : IAsyncDisposable
{
    event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged;

    AudioCaptureSessionSnapshot Snapshot { get; }

    ValueTask PauseAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);

    ValueTask PromotePrebufferAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public enum AudioCaptureMode
{
    Ask,
    Manual
}

public enum AudioCaptureSourceKind
{
    ProcessOutput,
    DeviceLoopback,
    Microphone
}

public sealed record AudioCaptureSource(
    AudioCaptureSourceKind Kind,
    string? DeviceId = null,
    int? RootProcessId = null,
    string? ProcessName = null)
{
    public static AudioCaptureSource ProcessOutput(int rootProcessId, string? processName = null) =>
        new(AudioCaptureSourceKind.ProcessOutput, RootProcessId: rootProcessId, ProcessName: processName);

    public static AudioCaptureSource DeviceLoopback(string deviceId) =>
        new(AudioCaptureSourceKind.DeviceLoopback, DeviceId: deviceId);

    public static AudioCaptureSource Microphone(string deviceId) =>
        new(AudioCaptureSourceKind.Microphone, DeviceId: deviceId);
}

public sealed record AudioCaptureRequest(
    Guid SessionId,
    AudioCaptureMode Mode,
    IReadOnlyList<AudioCaptureSource> Sources,
    string TempSessionDirectoryPath,
    int PrebufferSeconds,
    bool CreateMixedArtifact)
{
    private static readonly int[] AllowedPrebufferSeconds = [0, 5, 10, 15, 30];

    public void Validate()
    {
        if (SessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id is required.", nameof(SessionId));
        }

        if (string.IsNullOrWhiteSpace(TempSessionDirectoryPath))
        {
            throw new ArgumentException("A temporary session directory is required.", nameof(TempSessionDirectoryPath));
        }

        if (Sources is null || Sources.Count == 0)
        {
            throw new ArgumentException("At least one capture source is required.", nameof(Sources));
        }

        if (!AllowedPrebufferSeconds.Contains(PrebufferSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(PrebufferSeconds));
        }

        var kinds = new HashSet<AudioCaptureSourceKind>();
        var outputCount = 0;
        foreach (var source in Sources)
        {
            if (!kinds.Add(source.Kind))
            {
                throw new ArgumentException($"Duplicate capture source '{source.Kind}'.", nameof(Sources));
            }

            switch (source.Kind)
            {
                case AudioCaptureSourceKind.ProcessOutput when source.RootProcessId is null or <= 0:
                    throw new ArgumentException("Process output requires a positive process id.", nameof(Sources));
                case AudioCaptureSourceKind.ProcessOutput:
                    outputCount++;
                    break;
                case AudioCaptureSourceKind.DeviceLoopback when string.IsNullOrWhiteSpace(source.DeviceId):
                    throw new ArgumentException("Device loopback requires an output device id.", nameof(Sources));
                case AudioCaptureSourceKind.DeviceLoopback:
                    outputCount++;
                    break;
                case AudioCaptureSourceKind.Microphone when string.IsNullOrWhiteSpace(source.DeviceId):
                    throw new ArgumentException("Microphone capture requires a device id.", nameof(Sources));
                case AudioCaptureSourceKind.Microphone:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(Sources), source.Kind, "Unknown capture source kind.");
            }
        }

        if (outputCount > 1)
        {
            throw new ArgumentException("Only one output capture source can be active per session.", nameof(Sources));
        }
    }
}

public enum AudioCaptureState
{
    Starting,
    Running,
    Stopping,
    Completed,
    Faulted
}

public enum AudioCaptureArtifactKind
{
    Output,
    Microphone,
    Mixed
}

public sealed record AudioCaptureArtifactSnapshot(
    AudioCaptureArtifactKind Kind,
    string Path,
    long BytesWritten,
    DateTimeOffset FinalizedAtUtc,
    TimeSpan RelativeStartOffset = default);

public sealed record AudioCaptureFailureSnapshot(
    DateTimeOffset OccurredAtUtc,
    string Kind,
    AudioCaptureSourceKind? SourceKind,
    string Code,
    string Message,
    string? ArtifactPath);

public sealed record AudioCaptureSessionSnapshot(
    Guid SessionId,
    AudioCaptureState State,
    bool IsPersistingAudio,
    IReadOnlyList<AudioCaptureArtifactSnapshot> Artifacts,
    IReadOnlyList<AudioCaptureFailureSnapshot> Failures);
