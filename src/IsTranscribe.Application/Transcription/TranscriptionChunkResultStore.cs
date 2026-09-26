using System.Security.Cryptography;
using System.Text.Json;
using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Transcription;

public sealed record StoredTranscriptionChunkResult(
    string Path,
    string Sha256,
    string? EngineRequestId,
    string? MetadataJson,
    string? UsageJson);

/// <summary>
/// Atomically checkpoints normalized chunk results in app-owned storage. Transcript text stays
/// in the result file and is never copied into SQLite metadata or diagnostics.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed class TranscriptionChunkResultStore
{
    private const long MaximumCheckpointBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async ValueTask<StoredTranscriptionChunkResult> WriteAsync(
        string directory,
        string chunkId,
        TranscriptionResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A checkpoint directory is required.", nameof(directory));
        }

        var normalizedChunkId = NormalizeIdentifier(chunkId, nameof(chunkId));
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.Text is null)
        {
            throw new ArgumentException("Only a completed transcription result can be checkpointed.", nameof(result));
        }

        var normalizedResult = SanitizeCompletedResult(result);
        var checkpoint = ChunkResultCheckpoint.FromResult(normalizedResult);
        var payload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, SerializerOptions);
        if (payload.LongLength > MaximumCheckpointBytes)
        {
            throw new InvalidDataException("The normalized transcription checkpoint is too large.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{normalizedChunkId}.result.json");
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return new StoredTranscriptionChunkResult(
            path,
            Sha256(payload),
            normalizedResult.Metadata?.RequestId,
            BuildMetadataJson(normalizedResult),
            normalizedResult.Metadata?.Usage is null
                ? null
                : JsonSerializer.Serialize(normalizedResult.Metadata.Usage, SerializerOptions));
    }

    public async ValueTask<TranscriptionResult> ReadAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A checkpoint path is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The transcription chunk checkpoint is unavailable.", path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumCheckpointBytes)
        {
            throw new InvalidDataException("The transcription chunk checkpoint is too large.");
        }

        using var memory = new MemoryStream(checked((int)stream.Length));
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        var payload = memory.ToArray();
        if (!string.Equals(Sha256(payload), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The transcription chunk checkpoint hash did not match persistence.");
        }

        var checkpoint = JsonSerializer.Deserialize<ChunkResultCheckpoint>(payload, SerializerOptions)
            ?? throw new InvalidDataException("The transcription chunk checkpoint is invalid.");
        return SanitizeCompletedResult(checkpoint.ToResult());
    }

    private static TranscriptionResult SanitizeCompletedResult(TranscriptionResult result)
    {
        var metadata = result.Metadata;
        if (metadata is null)
        {
            return result;
        }

        var requestId = SanitizeProviderRequestId(metadata.RequestId);
        if (string.Equals(requestId, metadata.RequestId, StringComparison.Ordinal))
        {
            return result;
        }

        return TranscriptionResult.Completed(
            result.Text!,
            result.DetectedLanguage,
            result.Segments,
            metadata with { RequestId = requestId });
    }

    private static string? SanitizeProviderRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var normalized = requestId.Trim();
        return normalized.Length <= 128
               && !LooksSensitive(normalized)
               && normalized.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static bool LooksSensitive(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sk-or-v1-", StringComparison.OrdinalIgnoreCase);

    private static string BuildMetadataJson(TranscriptionResult result)
    {
        var metadata = result.Metadata;
        var bounded = new ChunkResultDatabaseMetadata(
            Version: 1,
            DetectedLanguage: NormalizeBounded(result.DetectedLanguage, 35),
            ResolvedModelId: NormalizeBounded(metadata?.ResolvedModelId, 256),
            SourceStartMilliseconds: ToMilliseconds(metadata?.SourceStart),
            SourceEndMilliseconds: ToMilliseconds(metadata?.SourceEnd),
            AudioDurationMilliseconds: ToMilliseconds(metadata?.AudioDuration));
        return JsonSerializer.Serialize(bounded, SerializerOptions);
    }

    private static long? ToMilliseconds(TimeSpan? value) => value is null
        ? null
        : checked((long)Math.Round(value.Value.TotalMilliseconds, MidpointRounding.AwayFromZero));

    private static string? NormalizeBounded(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumLength && normalized.All(static character => !char.IsControl(character))
            ? normalized
            : throw new InvalidDataException("Engine metadata exceeded its normalized boundary.");
    }

    private static string NormalizeIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A checkpoint identifier is required.", parameterName);
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 256 || normalized.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            throw new ArgumentException("The checkpoint identifier is invalid.", parameterName);
        }

        return normalized;
    }

    private static string Sha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ChunkResultDatabaseMetadata(
        int Version,
        string? DetectedLanguage,
        string? ResolvedModelId,
        long? SourceStartMilliseconds,
        long? SourceEndMilliseconds,
        long? AudioDurationMilliseconds);

    private sealed record ChunkResultCheckpoint(
        int Version,
        string Text,
        string? DetectedLanguage,
        IReadOnlyList<ChunkSegmentCheckpoint> Segments,
        ChunkMetadataCheckpoint? Metadata)
    {
        public const int CurrentVersion = 1;

        public static ChunkResultCheckpoint FromResult(TranscriptionResult result) => new(
            CurrentVersion,
            result.Text!,
            result.DetectedLanguage,
            result.Segments.Select(ChunkSegmentCheckpoint.FromSegment).ToArray(),
            result.Metadata is null ? null : ChunkMetadataCheckpoint.FromMetadata(result.Metadata));

        public TranscriptionResult ToResult()
        {
            if (Version != CurrentVersion || Text is null || Segments is null)
            {
                throw new InvalidDataException("The transcription chunk checkpoint version is unsupported.");
            }

            return TranscriptionResult.Completed(
                Text,
                DetectedLanguage,
                Segments.Select(static segment => segment.ToSegment()).ToArray(),
                Metadata?.ToMetadata());
        }
    }

    private sealed record ChunkSegmentCheckpoint(
        string Text,
        long? StartMilliseconds,
        long? EndMilliseconds,
        string? SpeakerLabel,
        IReadOnlyList<ChunkWordCheckpoint> Words)
    {
        public static ChunkSegmentCheckpoint FromSegment(TranscriptionSegment segment) => new(
            segment.Text,
            ToMilliseconds(segment.Start),
            ToMilliseconds(segment.End),
            segment.SpeakerLabel,
            segment.Words.Select(ChunkWordCheckpoint.FromWord).ToArray());

        public TranscriptionSegment ToSegment() => new(
            Text,
            StartMilliseconds is null ? null : TimeSpan.FromMilliseconds(StartMilliseconds.Value),
            EndMilliseconds is null ? null : TimeSpan.FromMilliseconds(EndMilliseconds.Value),
            SpeakerLabel,
            (Words ?? []).Select(static word => word.ToWord()).ToArray());
    }

    private sealed record ChunkWordCheckpoint(
        string Text,
        long StartMilliseconds,
        long EndMilliseconds,
        double? Confidence)
    {
        public static ChunkWordCheckpoint FromWord(TranscriptionWord word) => new(
            word.Text,
            ToMilliseconds(word.Start)!.Value,
            ToMilliseconds(word.End)!.Value,
            word.Confidence);

        public TranscriptionWord ToWord() => new(
            Text,
            TimeSpan.FromMilliseconds(StartMilliseconds),
            TimeSpan.FromMilliseconds(EndMilliseconds),
            Confidence);
    }

    private sealed record ChunkMetadataCheckpoint(
        string? ResolvedModelId,
        string? RequestId,
        TranscriptionUsage? Usage,
        long? SourceStartMilliseconds,
        long? SourceEndMilliseconds,
        long? AudioDurationMilliseconds)
    {
        public static ChunkMetadataCheckpoint FromMetadata(TranscriptionResultMetadata metadata) => new(
            metadata.ResolvedModelId,
            metadata.RequestId,
            metadata.Usage,
            ToMilliseconds(metadata.SourceStart),
            ToMilliseconds(metadata.SourceEnd),
            ToMilliseconds(metadata.AudioDuration));

        public TranscriptionResultMetadata ToMetadata() => new(
            ResolvedModelId,
            RequestId,
            Usage,
            SourceStartMilliseconds is null ? null : TimeSpan.FromMilliseconds(SourceStartMilliseconds.Value),
            SourceEndMilliseconds is null ? null : TimeSpan.FromMilliseconds(SourceEndMilliseconds.Value),
            AudioDurationMilliseconds is null
                ? null
                : TimeSpan.FromMilliseconds(AudioDurationMilliseconds.Value));
    }
}
