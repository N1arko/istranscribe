using System.Text.Json;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Application.Transcription;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition
// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
public sealed class SpeakerAwareChunkProcessor(ISpeakerDiarizer diarizer, IAudioChunkMaterializer materializer)
{
    private readonly TranscriptionChunkResultStore _store = new();

    public async ValueTask<TranscriptionResult> ProcessAsync(TranscriptionJobRecord job, AudioChunkDescriptor chunk,
        TranscriptionSessionContext session, string jobDirectory, ITranscriptionEngine engine,
        TranscriptionRequest request, IProgress<TranscriptionProgress> progress, CancellationToken cancellationToken)
    {
        await diarizer.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var tracks = await GetSourcesAsync(job, session, jobDirectory, cancellationToken).ConfigureAwait(false);
        var words = new List<TranscriptionSegment>();
        var outcomes = new List<TranscriptionResult>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var start = Math.Max(chunk.Start.TotalSeconds, track.OffsetSeconds);
            var end = Math.Min(chunk.End.TotalSeconds, track.OffsetSeconds + track.DurationSeconds);
            if (end - start < 0.02) continue;
            var directory = Path.Combine(jobDirectory, "sources", i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            var sourceJob = job with
            {
                InputAudioPath = track.Path,
                InputSha256 = track.Sha256,
                InputSizeBytes = track.SizeBytes,
                InputDurationSeconds = track.DurationSeconds,
                ExecutionKind = TranscriptionExecutionKind.Local
            };
            var sourceChunk = chunk with
            {
                Start = TimeSpan.FromSeconds(start - track.OffsetSeconds),
                End = TimeSpan.FromSeconds(end - track.OffsetSeconds)
            };
            var audio = await materializer.MaterializeAsync(sourceJob, sourceChunk, Path.Combine(directory, "input"), cancellationToken).ConfigureAwait(false);
            try
            {
                SpeakerDiarizationResult? speakers = null;
                var prefix = $"{chunk.Id}/{i}:";
                // Segmentation also guards microphone-only silence. Its identity remains source-known self.
                speakers = await diarizer.ProcessAsync(audio, Path.Combine(directory, chunk.Id + ".speakers.json"), cancellationToken).ConfigureAwait(false);
                if (speakers.Intervals.Count == 0) continue; // no detected speech; never upload this chunk
                var receiptPath = Path.Combine(directory, chunk.Id + ".asr.json");
                TranscriptionResult result;
                if (File.Exists(receiptPath))
                {
                    var receipt = await ReadJsonAsync<SourceRecognitionCheckpoint>(receiptPath, cancellationToken).ConfigureAwait(false);
                    if (receipt.InputSha256 != audio.Sha256) throw new SpeakerProcessingException("input_changed");
                    result = await _store.ReadAsync(receipt.Result.Path, receipt.Result.Sha256, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await engine.TranscribeAsync(request with
                    {
                        PrimaryAudioArtifactPath = audio.Path,
                        Language = null,
                        SourceStart = TimeSpan.FromSeconds(start),
                        SourceEnd = TimeSpan.FromSeconds(end)
                    }, progress, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded) return result;
                    // This checkpoint precedes speaker assembly so a retry does not repeat a paid ASR request.
                    var stored = await _store.WriteAsync(directory, chunk.Id, result, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(receiptPath, new SourceRecognitionCheckpoint(audio.Sha256, stored), cancellationToken).ConfigureAwait(false);
                }
                outcomes.Add(result);
                if (!string.IsNullOrWhiteSpace(result.Text) && !result.Segments.Any(static segment => segment.Words.Count > 0))
                    throw new SpeakerProcessingException("timestamp_capability_missing");
                var assigned = SpeakerTurnAssembler.Assign(result.Segments, track.Role, speakers, prefix);
                var offset = TimeSpan.FromSeconds(start) - chunk.Start;
                words.AddRange(assigned.Select(segment => new TranscriptionSegment(segment.Text,
                    segment.Start + offset, segment.End + offset, segment.SpeakerLabel,
                    segment.Words.Select(word => word with { Start = word.Start + offset, End = word.End + offset }).ToArray())));
            }
            finally { materializer.DeleteTemporary(audio); }
        }
        var ordered = words.OrderBy(static word => word.Start).ThenBy(static word => word.SpeakerLabel, StringComparer.Ordinal).ToArray();
        return TranscriptionResult.Completed(string.Join(' ', ordered.Select(static word => word.Text)),
            outcomes.Select(static result => result.DetectedLanguage).FirstOrDefault(static language => !string.IsNullOrEmpty(language)),
            ordered, new TranscriptionResultMetadata(ResolvedModelId: job.ModelId,
                Usage: TranscriptMerger.AggregateUsage(outcomes.Select(static result => result.Metadata?.Usage))));
    }

    // Rebuild from immutable per-chunk voice vectors in timeline order on every finalization.
    // No mutable cross-job voice database; crash/restart yields the same ordinals.
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#speakers.identity
    public async ValueTask<IReadOnlyList<CompletedTranscriptionChunk>> ReconcileAsync(
        IReadOnlyList<CompletedTranscriptionChunk> chunks, string jobDirectory, CancellationToken cancellationToken)
    {
        var tracks = await ReadJsonAsync<TranscriptionSourceTrack[]>(Path.Combine(jobDirectory, "source-plan.json"), cancellationToken).ConfigureAwait(false);
        var registry = new MeetingSpeakerRegistry();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in chunks.OrderBy(static item => item.Chunk.Start).ThenBy(static item => item.Chunk.SequenceIndex))
        {
            for (var i = 0; i < tracks.Length; i++)
            {
                if (tracks[i].Role == "microphone") continue;
                var directory = Path.Combine(jobDirectory, "sources", i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                var path = Path.Combine(directory, chunk.Chunk.Id + ".speakers.json");
                if (!File.Exists(path)) continue;
                var result = await ReadJsonAsync<SpeakerDiarizationResult>(path, cancellationToken).ConfigureAwait(false);
                result.Validate();
                var used = new HashSet<int>();
                foreach (var speaker in result.Speakers.OrderBy(speaker => result.Intervals.Where(interval => interval.Speaker == speaker.Id).Min(static interval => interval.Start)))
                {
                    var ordinal = registry.Resolve(speaker.Vector, used);
                    mapping[$"{chunk.Chunk.Id}/{i}:{speaker.Id}"] = (tracks[i].Role == "mixed" ? "unknown:" : "remote:") + ordinal;
                }
            }
        }
        return chunks.Select(chunk => chunk with
        {
            Result = TranscriptionResult.Completed(chunk.Result.Text!, chunk.Result.DetectedLanguage,
                chunk.Result.Segments.Select(segment => new TranscriptionSegment(segment.Text, segment.Start, segment.End,
                    segment.SpeakerLabel is "self" or "speaker_unresolved" ? segment.SpeakerLabel
                        : segment.SpeakerLabel is { } label && mapping.TryGetValue(label, out var identity) ? identity
                        : throw new SpeakerProcessingException("diarization_failed"), segment.Words)).ToArray(), chunk.Result.Metadata)
        }).ToArray();
    }

    public async ValueTask<IReadOnlyList<NormalizedSourceRecognition>> ReadProvenanceAsync(
        IReadOnlyList<CompletedTranscriptionChunk> chunks, string jobDirectory, CancellationToken cancellationToken)
    {
        var tracks = await ReadJsonAsync<TranscriptionSourceTrack[]>(Path.Combine(jobDirectory, "source-plan.json"), cancellationToken).ConfigureAwait(false);
        var evidence = new List<NormalizedSourceRecognition>();
        foreach (var chunk in chunks.OrderBy(static item => item.Chunk.SequenceIndex))
        {
            for (var i = 0; i < tracks.Length; i++)
            {
                var path = Path.Combine(jobDirectory, "sources", i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), chunk.Chunk.Id + ".asr.json");
                if (!File.Exists(path)) continue; // Silent or non-intersecting source.
                var receipt = await ReadJsonAsync<SourceRecognitionCheckpoint>(path, cancellationToken).ConfigureAwait(false);
                var result = await _store.ReadAsync(receipt.Result.Path, receipt.Result.Sha256, cancellationToken).ConfigureAwait(false);
                evidence.Add(new(chunk.Chunk.Id, i, tracks[i].Role, receipt.InputSha256, result.DetectedLanguage,
                    result.Metadata?.ResolvedModelId, result.Metadata?.RequestId, result.Metadata?.Usage));
            }
        }
        return evidence;
    }

    public static async ValueTask<IReadOnlyList<TranscriptionSourceTrack>> GetSourcesAsync(TranscriptionJobRecord job,
        TranscriptionSessionContext session, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "source-plan.json");
        if (File.Exists(path)) return await ReadJsonAsync<TranscriptionSourceTrack[]>(path, cancellationToken).ConfigureAwait(false);
        var tracks = TranscriptionSourceHandoff.Exists(session.TempSessionPath)
            ? TranscriptionSourceHandoff.Read(session.TempSessionPath!, Guid.ParseExact(job.SessionId, "N")).Tracks
            : [new TranscriptionSourceTrack("mixed", job.InputAudioPath, job.InputSha256, job.InputSizeBytes,
                job.InputDurationSeconds ?? throw new SpeakerProcessingException("input_missing"), 0)];
        await WriteJsonAsync(path, tracks, cancellationToken).ConfigureAwait(false);
        return tracks;
    }

    private static async ValueTask<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("speaker_checkpoint_invalid");
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("speaker_checkpoint_invalid");
    }

    private static async ValueTask WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, value, cancellationToken: cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SourceRecognitionCheckpoint(string InputSha256, StoredTranscriptionChunkResult Result);
}
