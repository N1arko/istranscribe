namespace IsTranscribe.Host.Audio.Capture;

/// <summary>
/// Describes whether one source participated in buffered-audio promotion.
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </summary>
internal enum AudioCapturePromotionOutcome
{
    Promoted,
    AlreadyPersisting,
    SourceUnavailable
}

internal interface IAudioCaptureAdapter : IAsyncDisposable
{
    event EventHandler<AudioCaptureFault>? Faulted;

    event EventHandler<AudioCaptureStopEvent>? Stopped;

    bool IsActive { get; }

    AudioCaptureArtifact? FinalArtifact { get; }

    AudioCaptureStopEvent? StopEvent { get; }

    ValueTask StartAsync(
        bool persistImmediately,
        DateTimeOffset sessionTimelineOriginUtc,
        CancellationToken cancellationToken);

    ValueTask<AudioCapturePromotionOutcome> PromoteAsync(CancellationToken cancellationToken);

    ValueTask PauseAsync(CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);

    ValueTask<AudioCaptureArtifact?> StopAsync(CancellationToken cancellationToken);
}

internal interface IAudioCaptureAdapterFactory
{
    IAudioCaptureAdapter Create(AudioCaptureRequest request, AudioCaptureSourceRequest source, int prebufferSeconds);
}

/// <summary>
/// Converts healthy capture sources into the canonical PCM preparation artifact consumed by
/// a platform encoder.
/// Calling this contract explicitly performs preparation; <see cref="AudioCaptureRequest.CreateMixedArtifact"/>
/// controls only the recorder session's automatic invocation.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// </remarks>
public interface IAudioArtifactPreparer
{
    ValueTask<AudioCaptureArtifact?> PrepareAsync(
        AudioCaptureRequest request,
        IReadOnlyList<AudioCaptureArtifact> sourceArtifacts,
        CancellationToken cancellationToken);
}
