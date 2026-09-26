using System.Security.Cryptography;
using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Protocol;
using Xunit;

namespace IsTranscribe.Application.Tests;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#verification
public sealed class SpeakerSourcePipelineTests
{
    [Fact]
    public async Task TwoSourcesUseSeparateAutomaticRequestsAndReusePaidResultsAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "istranscribe-speaker-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sessionId = Guid.NewGuid();
            var output = WriteWave(Path.Combine(root, "output.wav"));
            var microphone = WriteWave(Path.Combine(root, "microphone.wav"));
            TranscriptionSourceHandoff.Create(root, sessionId,
                [new(AudioCaptureArtifactKind.Output, output, new FileInfo(output).Length, DateTimeOffset.UtcNow),
                 new(AudioCaptureArtifactKind.Microphone, microphone, new FileInfo(microphone).Length, DateTimeOffset.UtcNow)]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output)));
            // This fixture tests the processing boundary independently of queue admission.
            var job = JsonSerializer.Deserialize<TranscriptionJobRecord>(JsonSerializer.Serialize(new
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId.ToString("N"),
                EngineId = "remote.groq",
                ExecutionKind = TranscriptionExecutionKind.Remote,
                ModelId = "whisper-large-v3-turbo",
                InputAudioPath = output,
                InputSha256 = hash,
                InputSizeBytes = new FileInfo(output).Length,
                InputDurationSeconds = 3d,
                RequestedLanguage = "auto"
            }))!;
            var context = new TranscriptionSessionContext(job.SessionId, "Synthetic", DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(3), output, job.Id, root);
            var chunk = new AudioChunkDescriptor("first", 0, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.Zero, 96000);
            var directory = Path.Combine(root, "job");
            var engine = new Recognizer();
            var materializer = new Materializer();
            var request = new TranscriptionRequest(sessionId, output, job.ModelId, JobId: Guid.ParseExact(job.Id, "N"), ChunkId: chunk.Id);
            var processor = new SpeakerAwareChunkProcessor(new Diarizer(), materializer);
            var result = await processor.ProcessAsync(job, chunk, context, directory, engine, request, new Progress<TranscriptionProgress>(), CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(2, engine.Requests.Count);
            Assert.All(engine.Requests, static request => Assert.Null(request.Language));
            Assert.Equal(2, engine.Requests.Select(static request => request.PrimaryAudioArtifactPath).Distinct().Count());
            Assert.Equal(2, materializer.Sources.Count);
            var completed = await processor.ReconcileAsync([new(chunk, result)], directory, CancellationToken.None);
            Assert.Contains(completed[0].Result.Segments, static item => item.SpeakerLabel == "self");
            Assert.Contains(completed[0].Result.Segments, static item => item.SpeakerLabel == "remote:1");
            Assert.Equal("pt", completed[0].Result.DetectedLanguage);
            var evidence = await processor.ReadProvenanceAsync([new(chunk, result)], directory, CancellationToken.None);
            Assert.Equal(2, evidence.Count);
            Assert.All(evidence, static item => Assert.Equal("pt", item.DetectedLanguage));
            Assert.Contains(evidence, static item => item.Role == "microphone");
            Assert.Contains(evidence, static item => item.Role == "system_output");

            var restarted = new SpeakerAwareChunkProcessor(new Diarizer(), new Materializer());
            var replay = await restarted.ProcessAsync(job, chunk, context, directory, engine, request, new Progress<TranscriptionProgress>(), CancellationToken.None);
            Assert.Equal(2, engine.Requests.Count); // No new paid requests.
            var replayed = await restarted.ReconcileAsync([new(chunk, replay)], directory, CancellationToken.None);
            Assert.Equal(completed[0].Result.Segments.Select(static item => item.SpeakerLabel), replayed[0].Result.Segments.Select(static item => item.SpeakerLabel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HandoffFreezesContinuityOffsetsAndCleanupPreservesPrimaryAndReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "istranscribe-speaker-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var primary = Path.Combine(Path.GetTempPath(), "istranscribe-primary-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWave(primary);
            var source = WriteWave(Path.Combine(root, "source.wav"));
            var id = Guid.NewGuid();
            TranscriptionSourceHandoff.Create(root, id, [new(AudioCaptureArtifactKind.Output, source, new FileInfo(source).Length,
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(12))]);
            var handoff = TranscriptionSourceHandoff.Read(root, id);
            var track = Assert.Single(handoff.Tracks);
            Assert.Equal(12, track.OffsetSeconds);
            Assert.Equal(3, track.DurationSeconds);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))), track.Sha256);
            Assert.False(TranscriptionSourceHandoff.ReleaseFiles(root, root + "-wrong", id, primary));
            Assert.True(File.Exists(source));
            Assert.False(TranscriptionSourceHandoff.ReleaseFiles(root, root, id, source));
            Assert.True(TranscriptionSourceHandoff.ReleaseFiles(root, root, id, primary));
            Assert.True(File.Exists(primary));
            Assert.False(File.Exists(source));
            Assert.True(TranscriptionSourceHandoff.Exists(root));
            Assert.True(TranscriptionSourceHandoff.ReleaseFiles(root, root, id, primary));
        }
        finally { Directory.Delete(root, recursive: true); File.Delete(primary); }
    }

    private static string WriteWave(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        const int length = 3 * 16000 * 2;
        writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        writer.Write(new byte[length]);
        return path;
    }

    private sealed class Materializer : IAudioChunkMaterializer
    {
        public List<string> Sources { get; } = [];
        public ValueTask<MaterializedAudioChunk> MaterializeAsync(TranscriptionJobRecord job, AudioChunkDescriptor chunk, string outputDirectory, CancellationToken cancellationToken)
        {
            Sources.Add(job.InputAudioPath);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(job.InputAudioPath)));
            Assert.Equal(job.InputSha256, hash);
            return ValueTask.FromResult(new MaterializedAudioChunk(job.InputAudioPath, "wav", hash, job.InputSizeBytes, false));
        }
        public void DeleteTemporary(MaterializedAudioChunk chunk) { }
    }

    private sealed class Diarizer : ISpeakerDiarizer
    {
        public async ValueTask<SpeakerDiarizationResult> ProcessAsync(MaterializedAudioChunk audio, string checkpointPath, CancellationToken cancellationToken)
        {
            var vector = new float[256]; vector[0] = 1;
            var result = new SpeakerDiarizationResult(DiarizationAssets.RuntimeVersion, DiarizationAssets.ParametersVersion,
                audio.Sha256, 3, [new(0, 2, 0)], [new(0, vector)]);
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
            await File.WriteAllTextAsync(checkpointPath, JsonSerializer.Serialize(result), cancellationToken);
            return result;
        }
    }

    private sealed class Recognizer : ITranscriptionEngine
    {
        public TranscriptionEngineCapabilities Capabilities => throw new NotSupportedException();
        public List<TranscriptionRequest> Requests { get; } = [];
        public ValueTask<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(TranscriptionResult.Completed("Olá", "pt",
                [new("Olá", TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(1), words: [new("Olá", TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(1))])]));
        }
    }
}
