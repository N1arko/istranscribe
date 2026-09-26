using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Transcription;

/// <summary>
/// One persisted chunk result ready for deterministic merge.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record CompletedTranscriptionChunk(
    AudioChunkDescriptor Chunk,
    TranscriptionResult Result);

public sealed record MergedTranscript(
    string Text,
    string? DetectedLanguage,
    string? ResolvedModelId,
    IReadOnlyList<TranscriptionSegment> Segments,
    IReadOnlyList<MergedTranscriptChunk> Chunks,
    TranscriptionUsage? Usage);

public sealed record MergedTranscriptChunk(
    string Id,
    int SequenceIndex,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? EngineRequestId,
    TranscriptionUsage? Usage);

/// <summary>
/// Merges timestamped overlaps by seam midpoint and text-only overlaps by a bounded token window.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </remarks>
public sealed class TranscriptMerger
{
    private const int MaximumDeduplicationTokens = 64;

    public MergedTranscript Merge(IReadOnlyList<CompletedTranscriptionChunk> completedChunks)
    {
        ArgumentNullException.ThrowIfNull(completedChunks);
        if (completedChunks.Count == 0)
        {
            throw new ArgumentException("At least one completed chunk is required.", nameof(completedChunks));
        }

        var chunks = completedChunks
            .OrderBy(static item => item.Chunk.Start)
            .ThenBy(static item => item.Chunk.SequenceIndex)
            .ToArray();
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            if (!chunk.Result.Succeeded || chunk.Result.Text is null)
            {
                throw new ArgumentException("Every chunk must contain a completed result.", nameof(completedChunks));
            }
        }

        var absoluteSegments = MergeSegments(chunks);
        var text = chunks.All(static chunk => chunk.Result.Segments.Count > 0)
            && absoluteSegments.Count > 0
                ? string.Join(' ', absoluteSegments.Select(static segment => segment.Text.Trim()))
                : MergeText(chunks.Select(static chunk => chunk.Result.Text!));
        var summaries = chunks.Select(static chunk => new MergedTranscriptChunk(
            chunk.Chunk.Id,
            chunk.Chunk.SequenceIndex,
            chunk.Chunk.Start,
            chunk.Chunk.End,
            chunk.Result.Text!,
            chunk.Result.Metadata?.RequestId,
            chunk.Result.Metadata?.Usage)).ToArray();

        return new MergedTranscript(
            text,
            chunks.Select(static chunk => chunk.Result.DetectedLanguage)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            chunks.Select(static chunk => chunk.Result.Metadata?.ResolvedModelId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            absoluteSegments,
            summaries,
            AggregateUsage(chunks.Select(static chunk => chunk.Result.Metadata?.Usage)));
    }

    private static IReadOnlyList<TranscriptionSegment> MergeSegments(
        IReadOnlyList<CompletedTranscriptionChunk> chunks)
    {
        var merged = new List<TranscriptionSegment>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var current = chunks[index];
            var leftSeam = index == 0
                ? (TimeSpan?)null
                : Midpoint(chunks[index - 1].Chunk.End, current.Chunk.Start);
            var rightSeam = index == chunks.Count - 1
                ? (TimeSpan?)null
                : Midpoint(current.Chunk.End, chunks[index + 1].Chunk.Start);

            foreach (var segment in current.Result.Segments)
            {
                // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#assembly
                if (segment.Words.Count > 0)
                {
                    foreach (var word in segment.Words)
                    {
                        var absoluteWord = word with { Start = current.Chunk.Start + word.Start, End = current.Chunk.Start + word.End };
                        var midpoint = Midpoint(absoluteWord.Start, absoluteWord.End);
                        if (leftSeam is { } lower && midpoint < lower || rightSeam is { } upper && midpoint >= upper) continue;
                        merged.Add(new TranscriptionSegment(word.Text, absoluteWord.Start, absoluteWord.End, segment.SpeakerLabel, [absoluteWord]));
                    }
                    continue;
                }
                var relativeStart = segment.Start ?? TimeSpan.Zero;
                var relativeEnd = segment.End ?? relativeStart;
                var absoluteStart = current.Chunk.Start + relativeStart;
                var absoluteEnd = current.Chunk.Start + relativeEnd;
                var segmentMidpoint = Midpoint(absoluteStart, absoluteEnd);
                if (leftSeam is { } left && segmentMidpoint < left)
                {
                    continue;
                }

                if (rightSeam is { } right && segmentMidpoint >= right)
                {
                    continue;
                }

                var words = segment.Words.Select(word => word with
                {
                    Start = current.Chunk.Start + word.Start,
                    End = current.Chunk.Start + word.End
                }).ToArray();
                merged.Add(new TranscriptionSegment(
                    segment.Text,
                    absoluteStart,
                    absoluteEnd,
                    segment.SpeakerLabel,
                    words));
            }
        }

        return merged
            .OrderBy(static segment => segment.Start)
            .ThenBy(static segment => segment.End)
            .ToArray();
    }

    private static string MergeText(IEnumerable<string> chunkTexts)
    {
        var merged = new List<string>();
        foreach (var text in chunkTexts)
        {
            var incoming = Tokenize(text);
            if (incoming.Length == 0)
            {
                continue;
            }

            var maximumOverlap = Math.Min(
                MaximumDeduplicationTokens,
                Math.Min(merged.Count, incoming.Length));
            var duplicateCount = 0;
            for (var candidate = maximumOverlap; candidate > 0; candidate--)
            {
                var matches = true;
                for (var offset = 0; offset < candidate; offset++)
                {
                    if (!string.Equals(
                            merged[merged.Count - candidate + offset],
                            incoming[offset],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    duplicateCount = candidate;
                    break;
                }
            }

            merged.AddRange(incoming.Skip(duplicateCount));
        }

        return string.Join(' ', merged);
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static TranscriptionUsage? AggregateUsage(IEnumerable<TranscriptionUsage?> values)
    {
        var usage = values.Where(static value => value is not null).Cast<TranscriptionUsage>().ToArray();
        if (usage.Length == 0)
        {
            return null;
        }

        var currencies = usage.Select(static value => value.Currency)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var canAggregateCost = currencies.Length <= 1;
        return new TranscriptionUsage(
            AudioSeconds: SumNullable(usage.Select(static value => value.AudioSeconds)),
            InputUnits: SumNullable(usage.Select(static value => value.InputUnits)),
            OutputUnits: SumNullable(usage.Select(static value => value.OutputUnits)),
            ReportedCost: canAggregateCost
                ? SumNullable(usage.Select(static value => value.ReportedCost))
                : null,
            Currency: canAggregateCost ? currencies.SingleOrDefault() : null);
    }

    private static double? SumNullable(IEnumerable<double?> values)
    {
        var snapshot = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return snapshot.Length == 0 ? null : snapshot.Sum();
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var snapshot = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return snapshot.Length == 0 ? null : snapshot.Sum();
    }

    private static decimal? SumNullable(IEnumerable<decimal?> values)
    {
        var snapshot = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return snapshot.Length == 0 ? null : snapshot.Sum();
    }

    private static TimeSpan Midpoint(TimeSpan left, TimeSpan right) =>
        left + TimeSpan.FromTicks((right - left).Ticks / 2);
}
