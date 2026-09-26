using System.Text.Json;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </summary>
public sealed class TranscriptionChunkResultStoreTests
{
    [Fact]
    public async Task RoundTripPreservesNormalizedResultAndKeepsTranscriptOutOfDatabaseMetadata()
    {
        const string transcriptSentinel = "PRIVATE_TRANSCRIPT_SENTINEL_7fbc";
        const string segmentSentinel = "PRIVATE_SEGMENT_SENTINEL_36ad";
        var root = CreateRoot();
        try
        {
            var store = new TranscriptionChunkResultStore();
            var usage = new TranscriptionUsage(
                AudioSeconds: 12.5,
                InputUnits: 10,
                OutputUnits: 20,
                ReportedCost: 0.01m,
                Currency: "USD");
            var result = TranscriptionResult.Completed(
                transcriptSentinel,
                detectedLanguage: "en",
                segments:
                [
                    new TranscriptionSegment(
                        segmentSentinel,
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromMilliseconds(1_750),
                        words:
                        [
                            new TranscriptionWord(
                                "private-word",
                                TimeSpan.FromMilliseconds(250),
                                TimeSpan.FromMilliseconds(700),
                                Confidence: 0.95)
                        ])
                ],
                metadata: new TranscriptionResultMetadata(
                    ResolvedModelId: "whisper-model",
                    RequestId: "request-safe-1",
                    Usage: usage,
                    SourceStart: TimeSpan.FromSeconds(5),
                    SourceEnd: TimeSpan.FromSeconds(17.5),
                    AudioDuration: TimeSpan.FromSeconds(12.5)));

            var stored = await store.WriteAsync(
                root,
                "CHUNK_01",
                result,
                CancellationToken.None);

            Assert.Equal(Path.Combine(root, "chunk_01.result.json"), stored.Path);
            Assert.Equal(64, stored.Sha256.Length);
            Assert.Equal("request-safe-1", stored.EngineRequestId);
            Assert.True(File.Exists(stored.Path));
            var checkpointText = await File.ReadAllTextAsync(stored.Path);
            Assert.Contains(transcriptSentinel, checkpointText, StringComparison.Ordinal);
            Assert.Contains(segmentSentinel, checkpointText, StringComparison.Ordinal);

            var databaseProjection = string.Join(
                "\n",
                stored.EngineRequestId,
                stored.MetadataJson,
                stored.UsageJson);
            Assert.DoesNotContain(transcriptSentinel, databaseProjection, StringComparison.Ordinal);
            Assert.DoesNotContain(segmentSentinel, databaseProjection, StringComparison.Ordinal);
            Assert.DoesNotContain("private-word", databaseProjection, StringComparison.Ordinal);
            using (var metadata = JsonDocument.Parse(Assert.IsType<string>(stored.MetadataJson)))
            {
                Assert.Equal(1, metadata.RootElement.GetProperty("version").GetInt32());
                Assert.Equal("en", metadata.RootElement.GetProperty("detectedLanguage").GetString());
                Assert.Equal(
                    "whisper-model",
                    metadata.RootElement.GetProperty("resolvedModelId").GetString());
                Assert.Equal(
                    5_000,
                    metadata.RootElement.GetProperty("sourceStartMilliseconds").GetInt64());
            }

            var restored = await store.ReadAsync(
                stored.Path,
                stored.Sha256,
                CancellationToken.None);

            Assert.True(restored.Succeeded);
            Assert.Equal(transcriptSentinel, restored.Text);
            Assert.Equal("en", restored.DetectedLanguage);
            var segment = Assert.Single(restored.Segments);
            Assert.Equal(segmentSentinel, segment.Text);
            Assert.Equal(TimeSpan.FromMilliseconds(250), segment.Start);
            Assert.Equal(TimeSpan.FromMilliseconds(1_750), segment.End);
            var word = Assert.Single(segment.Words);
            Assert.Equal("private-word", word.Text);
            Assert.Equal(0.95, word.Confidence);
            Assert.Equal("request-safe-1", restored.Metadata?.RequestId);
            Assert.Equal(usage, restored.Metadata?.Usage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TamperedCheckpointIsRejectedBeforeDeserialization()
    {
        var root = CreateRoot();
        try
        {
            var store = new TranscriptionChunkResultStore();
            var stored = await store.WriteAsync(
                root,
                "chunk-tamper",
                TranscriptionResult.Completed("Synthetic transcript."),
                CancellationToken.None);
            await File.AppendAllTextAsync(stored.Path, "\n{\"tampered\":true}");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadAsync(stored.Path, stored.Sha256, CancellationToken.None).AsTask());

            Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("authorization-Bearer-secret")]
    [InlineData("gsk_synthetic-secret-marker")]
    [InlineData("sk-or-v1-synthetic-secret-marker")]
    [InlineData("request id with spaces")]
    public async Task UnsafeProviderRequestIdIsRemovedFromCheckpointAndDatabaseProjection(
        string unsafeRequestId)
    {
        var root = CreateRoot();
        try
        {
            var store = new TranscriptionChunkResultStore();
            var stored = await store.WriteAsync(
                root,
                "chunk-sensitive-request-id",
                TranscriptionResult.Completed(
                    "Synthetic transcript.",
                    metadata: new TranscriptionResultMetadata(RequestId: unsafeRequestId)),
                CancellationToken.None);

            Assert.Null(stored.EngineRequestId);
            Assert.DoesNotContain(
                unsafeRequestId,
                await File.ReadAllTextAsync(stored.Path),
                StringComparison.Ordinal);
            var restored = await store.ReadAsync(
                stored.Path,
                stored.Sha256,
                CancellationToken.None);
            Assert.Null(restored.Metadata?.RequestId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-chunk-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
