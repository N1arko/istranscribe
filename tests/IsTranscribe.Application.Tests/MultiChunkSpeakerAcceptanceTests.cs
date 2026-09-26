using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Transcription.Local.Models;
using IsTranscribe.Transcription.Local.Worker;
using Xunit;

namespace IsTranscribe.Application.Tests;

public sealed partial class LocalWhisperApplicationIntegrationTests
{
    // Two repeated 16-second public/synthetic PCM clips; no private recordings or downloads.
    // This exercises real inference, bounded materialization, identity reconciliation and merging.
    // The fixture-only PCM decoder does not certify platform capture or compressed-media decoding.
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#verification
    [Fact]
    public async Task RealPublishedWorkerPreservesSpeakersAcrossOverlappingChunks()
    {
        if (Environment.GetEnvironmentVariable("ISTRANSCRIBE_MULTICHUNK_ACCEPTANCE") != "1") return;
        var assets = RequiredEnvironment("ISTRANSCRIBE_SPEAKER_ACCEPTANCE_ASSETS");
        var worker = RequiredEnvironment("ISTRANSCRIBE_WHISPER_ACCEPTANCE_WORKER");
        var model = RequiredEnvironment("ISTRANSCRIBE_WHISPER_ACCEPTANCE_MODEL");
        var voices = RequiredEnvironment("ISTRANSCRIBE_MULTICHUNK_OUTPUT_WAV");
        var microphone = RequiredEnvironment("ISTRANSCRIBE_MULTICHUNK_MIC_WAV");
        var backend = Enum.Parse<LocalTranscriptionBackend>(
            Environment.GetEnvironmentVariable("ISTRANSCRIBE_WHISPER_ACCEPTANCE_BACKEND") ?? "metal", true);
        await using var fixture = await LocalEngineFixture.CreateAsync();
        var job = await fixture.AddPreparedJobAsync(durationSeconds: 32, prepare: false,
            requestedBackend: backend, realModelPath: model, realOutputWave: voices, realMicrophoneWave: microphone);
        using var denyNetwork = new DenyNetworkHandler();
        using var modelHttp = new HttpClient(denyNetwork);
        await using var harness = fixture.CreateEngine(AllowedPolicy(), new CoordinatorLocalWorkerClientFactory(), modelHttp, worker, backend);
        var decoder = new AcceptancePcmDecoder();
        var materializer = new ManagedAudioChunkMaterializer(decoder);
        var processor = new SpeakerAwareChunkProcessor(new SpeakerDiarizationClient(worker, new DiarizationAssets(assets)), materializer);
        var planner = new AudioChunkPlanner(new AudioChunkPlannerOptions(
            TimeSpan.FromSeconds(18), 20 * 1024 * 1024, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
        await using var queue = fixture.CreateQueueWorker(harness.Engine, planner, speakerProcessor: processor);
        await queue.StartAsync(CancellationToken.None);
        var completed = await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed, timeoutSeconds: 300, failOnAttention: true);
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(completed.TranscriptJsonPath!));
        var document = artifact.RootElement;
        foreach (var turn in document.GetProperty("turns").EnumerateArray())
            output.WriteLine($"{turn.GetProperty("speaker_id").GetString()} {turn.GetProperty("start_milliseconds")}..{turn.GetProperty("end_milliseconds")}: {turn.GetProperty("text").GetString()}");
        var reportPath = Environment.GetEnvironmentVariable("ISTRANSCRIBE_MULTICHUNK_REPORT_JSON");
        if (!string.IsNullOrWhiteSpace(reportPath)) File.Copy(completed.TranscriptJsonPath!, reportPath, overwrite: false);
        var ids = document.GetProperty("speakers").EnumerateArray().Select(static speaker => speaker.GetProperty("id").GetString()).Order().ToArray();
        var establishedIds = ids.Where(static id => id != "speaker_unresolved").ToArray();
        Assert.Equal(new[] { "remote:1", "remote:2", "self" }, establishedIds);
        var chunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, static chunk => Assert.Equal(TranscriptionChunkStatus.Completed, chunk.Status));
        var turns = document.GetProperty("turns").EnumerateArray().ToArray();
        foreach (var id in establishedIds)
        {
            Assert.Contains(turns, turn => turn.GetProperty("speaker_id").GetString() == id && turn.GetProperty("start_milliseconds").GetInt64() < 16_000);
            Assert.Contains(turns, turn => turn.GetProperty("speaker_id").GetString() == id && turn.GetProperty("start_milliseconds").GetInt64() >= 16_000);
        }
        Assert.All(turns, static turn =>
        {
            var start = turn.GetProperty("start_milliseconds").GetInt64();
            var end = turn.GetProperty("end_milliseconds").GetInt64();
            Assert.InRange(start, 0, 32_000);
            Assert.InRange(end, start, 32_000);
        });
        var hasUnresolved = turns.Any(static turn => turn.GetProperty("speaker_id").GetString() == "speaker_unresolved");
        Assert.Equal(hasUnresolved, ids.Contains("speaker_unresolved"));
        Assert.Equal(hasUnresolved, document.GetProperty("warnings").EnumerateArray()
            .Any(static warning => warning.GetString() == "speaker_unresolved"));
        Assert.Equal(4, decoder.CallCount);
        Assert.Equal(4, (await fixture.Repository.ListLocalChunkAttemptsAsync(job.JobId, CancellationToken.None)).Count);
        Assert.Equal(4, document.GetProperty("source_recognitions").GetArrayLength());
        Assert.Equal(0, denyNetwork.RequestCount);
        Assert.True(File.Exists(job.Request.PrimaryAudioArtifactPath));
        output.WriteLine($"Real overlapping-chunk acceptance: 2 chunks, 4 source decodes/recognitions, {turns.Length} turns, self + 2 stable remote identities, unresolved={hasUnresolved}, zero downloads.");
    }

    private sealed class AcceptancePcmDecoder : IPlatformAudioChunkDecoder
    {
        public int CallCount { get; private set; }
        public bool CanDecode(string sourceFormat) => sourceFormat == "wav";

        public ValueTask<PlatformAudioChunkDecodeResult> DecodeAsync(PlatformAudioChunkDecodeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            using var reader = new BinaryReader(File.OpenRead(request.SourcePath));
            Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            reader.ReadInt32();
            Assert.Equal("WAVE", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            var formatVerified = false;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var size = reader.ReadInt32();
                var next = reader.BaseStream.Position + size + (size & 1);
                Assert.InRange(next, reader.BaseStream.Position, reader.BaseStream.Length);
                if (id == "fmt ")
                {
                    Assert.Equal(1, reader.ReadInt16());
                    Assert.Equal(1, reader.ReadInt16());
                    Assert.Equal(16_000, reader.ReadInt32());
                    Assert.Equal(32_000, reader.ReadInt32());
                    Assert.Equal(2, reader.ReadInt16());
                    Assert.Equal(16, reader.ReadInt16());
                    formatVerified = true;
                }
                if (id == "data")
                {
                    Assert.True(formatVerified);
                    var first = checked((int)Math.Round(request.Start.TotalSeconds * 16_000));
                    var frames = checked((int)Math.Round(request.Duration.TotalSeconds * 16_000));
                    Assert.InRange((first + frames) * 2, 0, size);
                    reader.BaseStream.Position += first * 2;
                    var pcm = reader.ReadBytes(frames * 2);
                    using var writer = new BinaryWriter(File.Create(request.OutputWavePath));
                    writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8);
                    writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                    writer.Write(16_000); writer.Write(32_000); writer.Write((short)2); writer.Write((short)16);
                    writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
                    CallCount++;
                    return ValueTask.FromResult(new PlatformAudioChunkDecodeResult(
                        request.OutputWavePath, 16_000, 1, 16, frames, request.Start, request.End));
                }
                reader.BaseStream.Position = next;
            }
            throw new InvalidDataException("Acceptance WAV contains no PCM data.");
        }
    }
}
