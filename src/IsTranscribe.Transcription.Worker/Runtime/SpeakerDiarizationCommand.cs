using System.Text.Json;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Runtime.Native;
using SherpaOnnx;

namespace IsTranscribe.Transcription.Worker.Runtime;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
internal static class SpeakerDiarizationCommand
{
    public const string ModeArgument = "--diarize";
    // Invoked by app-owned child process; no audio, embeddings or native diagnostics on stdout.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (5 or 6)) return 64;
        using var lifetime = new CancellationTokenSource();
        if (args.Length == 6)
        {
            if (!int.TryParse(args[5], out var parentId) || parentId <= 0 || parentId == Environment.ProcessId) return 64;
            _ = Task.Run(async () =>
            {
                try
                {
                    await new ParentProcessMonitor().WaitForExitAsync(parentId, lifetime.Token).ConfigureAwait(false);
                    Environment.Exit(75);
                }
                catch (OperationCanceledException) { }
            });
        }
        try
        {
            var assets = new DiarizationAssets(args[3]);
            if (!await assets.VerifyAsync(CancellationToken.None).ConfigureAwait(false)) return 66;
            var wave = NormalizedWaveReader.Read(args[1], args[2]);
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = assets.SegmentationPath;
            config.Segmentation.NumThreads = 2;
            config.Embedding.Model = assets.EmbeddingPath;
            config.Embedding.NumThreads = 2;
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = 0.5f;
            using var diarizer = new OfflineSpeakerDiarization(config);
            if (diarizer.SampleRate != 16000) return 65;
            var intervals = diarizer.Process(wave.Samples)
                .Select(static segment => new SpeakerInterval(segment.Start, segment.End, segment.Speaker)).ToArray();
            using var extractor = new SpeakerEmbeddingExtractor(config.Embedding);
            var embeddings = new List<SpeakerEmbedding>();
            foreach (var speaker in intervals.GroupBy(static segment => segment.Speaker))
            {
                // Use up to 30s of clean speech, excluding simultaneous speakers.
                var samples = new List<float>(480000);
                foreach (var segment in speaker.OrderByDescending(static segment => segment.End - segment.Start))
                {
                    var cursor = segment.Start;
                    foreach (var other in intervals.Where(other => other.Speaker != segment.Speaker && other.Start < segment.End && other.End > segment.Start)
                                 .OrderBy(static other => other.Start))
                    {
                        AddClean(cursor, Math.Min(other.Start, segment.End));
                        cursor = Math.Max(cursor, other.End);
                    }
                    AddClean(cursor, segment.End);
                    if (samples.Count >= 480000) break;

                    void AddClean(double from, double to)
                    {
                        if (to - from < 0.2 || samples.Count >= 480000) return;
                        var start = Math.Clamp((int)(from * 16000), 0, wave.Samples.Length);
                        var end = Math.Clamp((int)(to * 16000), start, wave.Samples.Length);
                        var count = Math.Min(end - start, 480000 - samples.Count);
                        samples.AddRange(wave.Samples.AsSpan(start, count).ToArray());
                    }
                }
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform(16000, samples.ToArray());
                stream.InputFinished();
                if (!extractor.IsReady(stream)) return 65;
                embeddings.Add(new SpeakerEmbedding(speaker.Key, extractor.Compute(stream)));
            }
            var result = new SpeakerDiarizationResult(DiarizationAssets.RuntimeVersion,
                DiarizationAssets.ParametersVersion, args[2], wave.DurationMilliseconds / 1000d, intervals, embeddings);
            result.Validate();
            // Parent supplies a unique temporary path and promotes after validation.
            await using var output = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(output, result).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return 0;
        }
        catch (Exception) { return 70; }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
}
