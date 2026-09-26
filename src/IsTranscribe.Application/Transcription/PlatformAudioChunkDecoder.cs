namespace IsTranscribe.Application.Transcription;

/// <summary>
/// Creates the OS-owned sample decoder used by the shared transcription materializer.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// </remarks>
public interface ITranscriptionAudioDecoderPlatformAdapter
{
    IPlatformAudioChunkDecoder CreateTranscriptionAudioChunkDecoder();
}

/// <summary>
/// Decodes one verified source time range into canonical transcription PCM.
/// </summary>
/// <remarks>
/// Implementations belong to a platform project. Common Application code owns the source
/// fingerprint check, deterministic output name and atomic promotion around this operation.
///
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public interface IPlatformAudioChunkDecoder
{
    bool CanDecode(string sourceFormat);

    ValueTask<PlatformAudioChunkDecodeResult> DecodeAsync(
        PlatformAudioChunkDecodeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable, fingerprint-bound request for a maximum five-minute PCM range.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public sealed record PlatformAudioChunkDecodeRequest(
    string SourcePath,
    string SourceFormat,
    string ExpectedSourceSha256,
    long ExpectedSourceSizeBytes,
    TimeSpan ExpectedSourceDuration,
    TimeSpan Start,
    TimeSpan End,
    string OutputWavePath)
{
    public const int TargetSampleRate = 16_000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    public static TimeSpan MaximumDuration { get; } = TimeSpan.FromMinutes(5);

    public TimeSpan Duration => End - Start;

    public PlatformAudioChunkDecodeRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new ArgumentException("A source audio path is required.", nameof(SourcePath));
        }

        if (string.IsNullOrWhiteSpace(SourceFormat))
        {
            throw new ArgumentException("A source audio format is required.", nameof(SourceFormat));
        }

        if (string.IsNullOrWhiteSpace(ExpectedSourceSha256)
            || ExpectedSourceSha256.Length != 64
            || ExpectedSourceSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The expected source SHA-256 must contain 64 hexadecimal characters.",
                nameof(ExpectedSourceSha256));
        }

        if (ExpectedSourceSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedSourceSizeBytes),
                ExpectedSourceSizeBytes,
                "The expected source size must be positive.");
        }

        if (ExpectedSourceDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedSourceDuration),
                ExpectedSourceDuration,
                "The expected source duration must be positive.");
        }

        if (Start < TimeSpan.Zero || End <= Start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Start),
                "The decoded range must have non-negative, increasing boundaries.");
        }

        if (End > ExpectedSourceDuration + TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(End),
                "The decoded range extends past the persisted source duration.");
        }

        if (Duration > MaximumDuration + TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(End),
                $"A decoded range cannot exceed {MaximumDuration.TotalMinutes:0} minutes.");
        }

        if (string.IsNullOrWhiteSpace(OutputWavePath))
        {
            throw new ArgumentException("A PCM WAV output path is required.", nameof(OutputWavePath));
        }

        var sourcePath = Path.GetFullPath(SourcePath);
        var outputPath = Path.GetFullPath(OutputWavePath);
        if (string.Equals(sourcePath, outputPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The decoder output must not replace the source audio.", nameof(OutputWavePath));
        }

        return this with
        {
            SourcePath = sourcePath,
            SourceFormat = SourceFormat.Trim().TrimStart('.').ToLowerInvariant(),
            ExpectedSourceSha256 = ExpectedSourceSha256.ToLowerInvariant(),
            OutputWavePath = outputPath
        };
    }
}

/// <summary>
/// Measured canonical PCM output produced by a platform decoder.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public sealed record PlatformAudioChunkDecodeResult(
    string OutputWavePath,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long SampleFrames,
    TimeSpan DecodedStart,
    TimeSpan DecodedEnd)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleFrames / SampleRate);
}
