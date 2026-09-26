using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IsTranscribe.Application.Transcription;

/// <summary>
/// Immutable identity of the finalized primary audio used to derive a chunk manifest.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record AudioSourceFingerprint(
    string Sha256,
    long SizeBytes,
    TimeSpan Duration,
    string Format)
{
    public AudioSourceFingerprint Validate()
    {
        if (Sha256.Length != 64 || Sha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The source SHA-256 must contain 64 hexadecimal characters.", nameof(Sha256));
        }

        if (SizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SizeBytes), SizeBytes, "Source size must be positive.");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration), Duration, "Source duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(Format))
        {
            throw new ArgumentException("Source format is required.", nameof(Format));
        }

        return this with
        {
            Sha256 = Sha256.ToLowerInvariant(),
            Format = Format.Trim().TrimStart('.').ToLowerInvariant()
        };
    }
}

/// <summary>
/// Versioned deterministic plan shared by remote and local engines.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </remarks>
public sealed record AudioChunkManifest(
    int Version,
    AudioSourceFingerprint Source,
    IReadOnlyList<AudioChunkDescriptor> Chunks)
{
    public AudioChunkDescriptor GetRequiredChunk(string chunkId) =>
        Chunks.Single(chunk => string.Equals(chunk.Id, chunkId, StringComparison.Ordinal));
}

public sealed record AudioChunkDescriptor(
    string Id,
    int SequenceIndex,
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Overlap,
    long EstimatedRawBytes,
    string? ParentChunkId = null,
    int SplitDepth = 0)
{
    public TimeSpan Duration => End - Start;
}

public sealed record AudioChunkPlannerOptions(
    TimeSpan MaximumDuration,
    long MaximumRawBytes,
    TimeSpan Overlap,
    TimeSpan MinimumSplitDuration)
{
    public static AudioChunkPlannerOptions Default { get; } = new(
        // The shared profile stays within the stricter local C API boundary and remains a valid
        // bounded remote request profile.
        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
        MaximumDuration: TimeSpan.FromMinutes(5),
        MaximumRawBytes: 20L * 1024 * 1024,
        Overlap: TimeSpan.FromSeconds(2),
        MinimumSplitDuration: TimeSpan.FromSeconds(60));
}

/// <summary>
/// Plans bounded time ranges without reading, rewriting or decoding the source file.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public sealed class AudioChunkPlanner
{
    public const int CurrentManifestVersion = 1;

    private readonly AudioChunkPlannerOptions _options;

    public AudioChunkPlanner(AudioChunkPlannerOptions? options = null)
    {
        _options = options ?? AudioChunkPlannerOptions.Default;
        ValidateOptions(_options);
    }

    public AudioChunkManifest Plan(AudioSourceFingerprint source)
    {
        var canonicalSource = source.Validate();
        var maximumRange = GetMaximumRange(canonicalSource);
        if (maximumRange <= _options.Overlap && canonicalSource.Duration > maximumRange)
        {
            throw new InvalidOperationException(
                "The source bitrate is too high for the configured size and overlap budgets.");
        }

        var chunks = new List<AudioChunkDescriptor>();
        var sourceEndMilliseconds = checked((long)Math.Ceiling(canonicalSource.Duration.TotalMilliseconds));
        var maximumRangeMilliseconds = Math.Max(1L, (long)Math.Floor(maximumRange.TotalMilliseconds));
        var overlapMilliseconds = Math.Max(0L, (long)Math.Floor(_options.Overlap.TotalMilliseconds));
        long startMilliseconds = 0;

        while (startMilliseconds < sourceEndMilliseconds)
        {
            var endMilliseconds = Math.Min(sourceEndMilliseconds, checked(startMilliseconds + maximumRangeMilliseconds));
            var actualOverlap = chunks.Count == 0
                ? 0
                : Math.Max(0, chunks[^1].End.TotalMilliseconds - startMilliseconds);
            chunks.Add(CreateDescriptor(
                canonicalSource,
                sequenceIndex: chunks.Count,
                startMilliseconds,
                endMilliseconds,
                overlapMilliseconds: (long)Math.Round(actualOverlap, MidpointRounding.AwayFromZero),
                parentChunkId: null,
                splitDepth: 0));

            if (endMilliseconds == sourceEndMilliseconds)
            {
                break;
            }

            startMilliseconds = checked(endMilliseconds - overlapMilliseconds);
        }

        return new AudioChunkManifest(CurrentManifestVersion, canonicalSource, chunks);
    }

    public AudioChunkManifest Split(AudioChunkManifest manifest, string chunkId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Version != CurrentManifestVersion)
        {
            throw new ArgumentException("The chunk manifest version is unsupported.", nameof(manifest));
        }

        var source = manifest.Source.Validate();
        var target = manifest.GetRequiredChunk(chunkId);
        if (target.Duration < _options.MinimumSplitDuration + _options.MinimumSplitDuration)
        {
            throw new InvalidOperationException("The chunk is already at the minimum splittable duration.");
        }

        var midpoint = target.Start + TimeSpan.FromTicks(target.Duration.Ticks / 2);
        var halfOverlap = TimeSpan.FromTicks(_options.Overlap.Ticks / 2);
        var leftEnd = Min(target.End, midpoint + halfOverlap);
        var rightStart = Max(target.Start, midpoint - halfOverlap);
        if (leftEnd - target.Start < _options.MinimumSplitDuration
            || target.End - rightStart < _options.MinimumSplitDuration)
        {
            throw new InvalidOperationException("Splitting would create a chunk below the minimum duration.");
        }

        var ranges = manifest.Chunks
            .Where(chunk => !string.Equals(chunk.Id, target.Id, StringComparison.Ordinal))
            .Select(chunk => new PlannedRange(
                chunk.Start,
                chunk.End,
                chunk.Overlap,
                chunk.ParentChunkId,
                chunk.SplitDepth))
            .Append(new PlannedRange(
                target.Start,
                leftEnd,
                target.Overlap,
                target.Id,
                target.SplitDepth + 1))
            .Append(new PlannedRange(
                rightStart,
                target.End,
                leftEnd - rightStart,
                target.Id,
                target.SplitDepth + 1))
            .OrderBy(static range => range.Start)
            .ThenBy(static range => range.End)
            .ToArray();

        var chunks = ranges
            .Select((range, index) => CreateDescriptor(
                source,
                index,
                checked((long)Math.Round(range.Start.TotalMilliseconds, MidpointRounding.AwayFromZero)),
                checked((long)Math.Round(range.End.TotalMilliseconds, MidpointRounding.AwayFromZero)),
                checked((long)Math.Round(range.Overlap.TotalMilliseconds, MidpointRounding.AwayFromZero)),
                range.ParentChunkId,
                range.SplitDepth))
            .ToArray();

        return manifest with { Source = source, Chunks = chunks };
    }

    private TimeSpan GetMaximumRange(AudioSourceFingerprint source)
    {
        var durationLimitedMilliseconds = _options.MaximumDuration.TotalMilliseconds;
        var sizeLimitedMilliseconds = source.Duration.TotalMilliseconds
            * _options.MaximumRawBytes
            / source.SizeBytes;
        return TimeSpan.FromMilliseconds(Math.Min(durationLimitedMilliseconds, sizeLimitedMilliseconds));
    }

    private static AudioChunkDescriptor CreateDescriptor(
        AudioSourceFingerprint source,
        int sequenceIndex,
        long startMilliseconds,
        long endMilliseconds,
        long overlapMilliseconds,
        string? parentChunkId,
        int splitDepth)
    {
        var estimatedBytes = Math.Min(
            source.SizeBytes,
            checked((long)Math.Ceiling(
                source.SizeBytes * ((endMilliseconds - startMilliseconds) / source.Duration.TotalMilliseconds))));
        var identity = string.Join(
            '|',
            CurrentManifestVersion.ToString(CultureInfo.InvariantCulture),
            source.Sha256,
            source.SizeBytes.ToString(CultureInfo.InvariantCulture),
            checked((long)Math.Ceiling(source.Duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture),
            startMilliseconds.ToString(CultureInfo.InvariantCulture),
            endMilliseconds.ToString(CultureInfo.InvariantCulture),
            splitDepth.ToString(CultureInfo.InvariantCulture));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

        return new AudioChunkDescriptor(
            id,
            sequenceIndex,
            TimeSpan.FromMilliseconds(startMilliseconds),
            TimeSpan.FromMilliseconds(endMilliseconds),
            TimeSpan.FromMilliseconds(overlapMilliseconds),
            estimatedBytes,
            parentChunkId,
            splitDepth);
    }

    private static void ValidateOptions(AudioChunkPlannerOptions options)
    {
        if (options.MaximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum duration must be positive.");
        }

        if (options.MaximumRawBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum raw bytes must be positive.");
        }

        if (options.Overlap < TimeSpan.Zero || options.Overlap >= options.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Overlap must fit within a chunk.");
        }

        if (options.MinimumSplitDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum split duration must be positive.");
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private sealed record PlannedRange(
        TimeSpan Start,
        TimeSpan End,
        TimeSpan Overlap,
        string? ParentChunkId,
        int SplitDepth);
}
