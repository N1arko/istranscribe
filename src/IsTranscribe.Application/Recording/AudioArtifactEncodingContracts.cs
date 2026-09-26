namespace IsTranscribe.Application.Recording;

/// <summary>
/// Platform-neutral boundary for encoding one finalized wave artifact.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression
/// </remarks>
public interface IAudioArtifactEncoder
{
    AudioArtifactEncoderAvailability ProbeAvailability();

    Task<AudioArtifactEncodeResult> EncodeAsync(
        string sourceWavePath,
        string partialOutputPath,
        CancellationToken cancellationToken) =>
        EncodeAsync(
            new AudioArtifactEncodeRequest(sourceWavePath, partialOutputPath),
            progress: null,
            cancellationToken);

    Task<AudioArtifactEncodeResult> EncodeAsync(
        AudioArtifactEncodeRequest request,
        IProgress<AudioArtifactEncodingProgress>? progress,
        CancellationToken cancellationToken);

    AudioArtifactReadabilityProbe ProbeReadability(string artifactPath);
}

public sealed record AudioArtifactEncodeRequest(
    string SourceWavePath,
    string PartialOutputPath);

public sealed record AudioArtifactEncodingProgress(
    long ProcessedBytes,
    long TotalBytes,
    double Fraction);

public sealed record AudioArtifactEncoderAvailability(
    bool IsAvailable,
    string Codec,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int BitRate,
    string? ReasonCode,
    string? Detail);

public sealed record AudioArtifactEncodeResult(
    bool Succeeded,
    string? PartialOutputPath,
    long OutputBytes,
    TimeSpan? Duration,
    int? SampleRate,
    int? Channels,
    int? BitsPerSample,
    int? BitRate,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record AudioArtifactReadabilityProbe(
    bool IsReadable,
    long FileBytes,
    TimeSpan? Duration,
    int? SampleRate,
    int? Channels,
    int? BitsPerSample,
    string? ReasonCode,
    string? Detail);
