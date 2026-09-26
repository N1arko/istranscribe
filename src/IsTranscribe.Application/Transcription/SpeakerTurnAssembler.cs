using IsTranscribe.Core.Transcription;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Application.Transcription;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#assembly
public static class SpeakerTurnAssembler
{
    public static IReadOnlyList<TranscriptionSegment> Assign(
        IReadOnlyList<TranscriptionSegment> segments, string source,
        SpeakerDiarizationResult? diarization, string labelPrefix)
    {
        var result = new List<TranscriptionSegment>();
        foreach (var segment in segments)
        {
            if (segment.Words.Count == 0 && !string.IsNullOrWhiteSpace(segment.Text))
                throw new SpeakerProcessingException("timestamp_capability_missing");
            foreach (var word in segment.Words)
            {
                if (word.End < word.Start || word.Start < TimeSpan.Zero || string.IsNullOrWhiteSpace(word.Text)
                    || (diarization is not null && word.End.TotalSeconds > diarization.DurationSeconds + 0.05))
                    throw new SpeakerProcessingException("timestamp_capability_missing");
                string speaker;
                if (source == "microphone") speaker = "self";
                else
                {
                    if (diarization is null || diarization.Intervals.Count == 0)
                        throw new SpeakerProcessingException("diarization_failed");
                    var start = word.Start.TotalSeconds;
                    var end = word.End.TotalSeconds;
                    var candidates = diarization.Intervals.Select(interval => new
                    {
                        Interval = interval,
                        Overlap = Math.Max(0, Math.Min(end, interval.End) - Math.Max(start, interval.Start)),
                        Distance = Math.Max(0, Math.Max(interval.Start - end, start - interval.End))
                    }).OrderByDescending(static x => x.Overlap).ThenBy(static x => x.Distance)
                        .ThenBy(static x => x.Interval.Start).ThenBy(static x => x.Interval.Speaker).ToArray();
                    var best = candidates[0];
                    var tied = candidates.Skip(1).Any(candidate => candidate.Interval.Speaker != best.Interval.Speaker
                        && Math.Abs(candidate.Overlap - best.Overlap) < 0.0001
                        && Math.Abs(candidate.Distance - best.Distance) < 0.0001);
                    speaker = best.Distance > 0.35 || tied ? "speaker_unresolved" : labelPrefix + best.Interval.Speaker;
                }
                result.Add(new TranscriptionSegment(word.Text, word.Start, word.End, speaker, [word]));
            }
        }
        return result;
    }

    public static IReadOnlyList<TranscriptionSegment> GroupTurns(IReadOnlyList<TranscriptionSegment> words)
    {
        var turns = new List<TranscriptionSegment>();
        // Group each logical voice independently, preserving actual overlaps between voices.
        foreach (var group in words.GroupBy(static word => word.SpeakerLabel))
        {
            TranscriptionSegment? current = null;
            foreach (var word in group.OrderBy(static word => word.Start).ThenBy(static word => word.End))
            {
                if (current is null) { current = word; continue; }
                if (word.Start - current.End > TimeSpan.FromSeconds(1.25)
                    || word.End - current.Start > TimeSpan.FromSeconds(30)
                    || current.Text.Length + word.Text.Length > 420
                    || words.Any(other => other.SpeakerLabel != word.SpeakerLabel && other.Start > current.Start && other.Start < word.Start))
                {
                    turns.Add(current);
                    current = word;
                    continue;
                }
                current = new TranscriptionSegment(Join(current.Text, word.Text), current.Start,
                    current.End > word.End ? current.End : word.End, current.SpeakerLabel, current.Words.Concat(word.Words).ToArray());
            }
            if (current is not null) turns.Add(current);
        }
        return turns.OrderBy(static turn => turn.Start)
            .ThenBy(static turn => turn.SpeakerLabel == "self" ? 0 : 1)
            .ThenBy(static turn => turn.SpeakerLabel, StringComparer.Ordinal).ToArray();
    }

    public static string Join(string left, string right) => string.IsNullOrWhiteSpace(left) ? right.Trim()
        : right.Length > 0 && ",.!?:;。！？、，".Contains(right[0]) ? left.TrimEnd() + right.Trim()
        : left.TrimEnd() + " " + right.Trim();
}

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#speakers.identity
public sealed class MeetingSpeakerRegistry
{
    private readonly List<float[]> _centroids = [];
    public int Resolve(float[] vector, ISet<int> usedInCurrentChunk)
    {
        var normalized = Normalize(vector);
        var best = -1;
        var similarity = 0.65; // versioned conservative cosine threshold; quality gate uses real meetings.
        for (var i = 0; i < _centroids.Count; i++)
        {
            if (usedInCurrentChunk.Contains(i + 1)) continue;
            var score = normalized.Zip(_centroids[i], static (a, b) => (double)a * b).Sum();
            if (score > similarity) { best = i; similarity = score; }
        }
        if (best < 0)
        {
            if (_centroids.Count >= 64) throw new SpeakerProcessingException("diarization_speaker_limit");
            best = _centroids.Count;
            _centroids.Add(normalized);
        }
        else _centroids[best] = Normalize(_centroids[best].Zip(normalized, static (a, b) => a + b).ToArray());
        usedInCurrentChunk.Add(best + 1);
        return best + 1;
    }

    private static float[] Normalize(float[] vector)
    {
        if (vector.Length != 256 || vector.Any(static value => !float.IsFinite(value)))
            throw new SpeakerProcessingException("diarization_embedding_invalid");
        var norm = Math.Sqrt(vector.Sum(static value => (double)value * value));
        if (norm < 0.00001) throw new SpeakerProcessingException("diarization_embedding_invalid");
        return vector.Select(value => (float)(value / norm)).ToArray();
    }
}
