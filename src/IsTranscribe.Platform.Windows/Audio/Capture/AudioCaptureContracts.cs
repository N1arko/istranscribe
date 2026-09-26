using System.Collections.ObjectModel;

namespace IsTranscribe.Host.Audio.Capture;

public enum AudioCaptureMode
{
    Auto,
    Ask,
    Manual
}

public enum AudioCaptureSourceKind
{
    ProcessOutput,
    DeviceLoopback,
    Microphone
}

public enum AudioCaptureArtifactKind
{
    Output,
    Microphone,
    Mixed
}

public enum AudioCaptureFailureKind
{
    Unsupported,
    AccessDenied,
    DeviceLost,
    ProcessExited,
    SessionObservationLost,
    IoFailure,
    UnexpectedRuntimeFailure
}

public enum AudioRecorderState
{
    Starting,
    Running,
    Stopping,
    Completed,
    Faulted
}

public enum AudioCaptureStopKind
{
    Requested,
    SourceCompleted,
    ProcessExited
}

public sealed record AudioCaptureSourceRequest(
    AudioCaptureSourceKind Kind,
    string? DeviceId = null,
    int? RootProcessId = null,
    string? RootProcessName = null)
{
    public static AudioCaptureSourceRequest ProcessOutput(int rootProcessId, string? rootProcessName = null) =>
        new(AudioCaptureSourceKind.ProcessOutput, RootProcessId: rootProcessId, RootProcessName: rootProcessName);

    public static AudioCaptureSourceRequest DeviceLoopback(string deviceId) =>
        new(AudioCaptureSourceKind.DeviceLoopback, DeviceId: deviceId);

    public static AudioCaptureSourceRequest Microphone(string deviceId) =>
        new(AudioCaptureSourceKind.Microphone, DeviceId: deviceId);
}

public sealed record AudioCaptureRequest(
    Guid SessionId,
    AudioCaptureMode Mode,
    IReadOnlyList<AudioCaptureSourceRequest> Sources,
    string TempSessionDirectoryPath,
    int PrebufferSeconds,
    bool CreateMixedArtifact)
{
    private static readonly ReadOnlyCollection<int> AllowedPrebufferSeconds = Array.AsReadOnly([0, 5, 10, 15, 30]);

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#source-model.request
    public void Validate()
    {
        if (SessionId == Guid.Empty)
        {
            throw new ArgumentException("SessionId must be a non-empty GUID.", nameof(SessionId));
        }

        if (string.IsNullOrWhiteSpace(TempSessionDirectoryPath))
        {
            throw new ArgumentException("Temp session directory path is required.", nameof(TempSessionDirectoryPath));
        }

        if (Sources is null || Sources.Count == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(Sources));
        }

        if (!AllowedPrebufferSeconds.Contains(PrebufferSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(PrebufferSeconds), "Prebuffer must be one of 0, 5, 10, 15 or 30 seconds.");
        }

        var sourceKinds = new HashSet<AudioCaptureSourceKind>();
        var outputSourceCount = 0;

        foreach (var source in Sources)
        {
            if (!sourceKinds.Add(source.Kind))
            {
                throw new ArgumentException($"Duplicate source kind '{source.Kind}' is not allowed.", nameof(Sources));
            }

            switch (source.Kind)
            {
                case AudioCaptureSourceKind.ProcessOutput:
                    outputSourceCount++;
                    if (source.RootProcessId is null or <= 0)
                    {
                        throw new ArgumentException("Process output source requires a positive root process id.", nameof(Sources));
                    }

                    break;
                case AudioCaptureSourceKind.DeviceLoopback:
                    outputSourceCount++;
                    if (string.IsNullOrWhiteSpace(source.DeviceId))
                    {
                        throw new ArgumentException("Device loopback source requires a concrete render device id.", nameof(Sources));
                    }

                    break;
                case AudioCaptureSourceKind.Microphone:
                    if (string.IsNullOrWhiteSpace(source.DeviceId))
                    {
                        throw new ArgumentException("Microphone source requires a concrete capture device id.", nameof(Sources));
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(Sources), source.Kind, "Unknown source kind.");
            }
        }

        if (outputSourceCount > 1)
        {
            throw new ArgumentException("Process output and device loopback cannot be requested together in one capture plan.", nameof(Sources));
        }
    }
}

/// <summary>
/// One closed source or prepared artifact and its position on the recording timeline.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// </remarks>
public sealed record AudioCaptureArtifact(
    AudioCaptureArtifactKind Kind,
    string Path,
    long BytesWritten,
    DateTimeOffset FinalizedAtUtc,
    TimeSpan RelativeStartOffset = default);

public sealed record AudioCaptureFault(
    DateTimeOffset OccurredAtUtc,
    AudioCaptureFailureKind FailureKind,
    AudioCaptureSourceKind? SourceKind,
    string Code,
    string Message,
    string? ArtifactPath = null);

public sealed record AudioCaptureStopEvent(
    DateTimeOffset OccurredAtUtc,
    AudioCaptureSourceKind SourceKind,
    AudioCaptureStopKind StopKind,
    string Code,
    string Message);

public sealed record AudioRecorderSessionSnapshot(
    Guid SessionId,
    AudioRecorderState State,
    bool IsPersistingAudio,
    IReadOnlyList<AudioCaptureArtifact> Artifacts,
    IReadOnlyList<AudioCaptureFault> Faults,
    IReadOnlyList<AudioCaptureStopEvent> StopEvents);
