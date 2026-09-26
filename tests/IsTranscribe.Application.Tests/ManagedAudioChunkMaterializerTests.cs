using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </summary>
public sealed class ManagedAudioChunkMaterializerTests
{
    [Fact]
    public async Task FullSourceRequestPassesThroughWithoutCopyingOrDeletingTheRecording()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "recording.ogg");
            var sourceBytes = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(sourcePath, sourceBytes, TimeSpan.FromSeconds(10));
            var materializer = new ManagedAudioChunkMaterializer();

            var result = await materializer.MaterializeAsync(
                job,
                CreateChunk("full", TimeSpan.Zero, TimeSpan.FromSeconds(10), sourceBytes.Length),
                Path.Combine(root, "chunks"),
                CancellationToken.None);

            Assert.Equal(sourcePath, result.Path);
            Assert.Equal("ogg", result.Format);
            Assert.Equal(job.InputSha256, result.Sha256);
            Assert.Equal(sourceBytes.Length, result.SizeBytes);
            Assert.False(result.IsTemporary);
            Assert.False(Directory.Exists(Path.Combine(root, "chunks")));

            materializer.DeleteTemporary(result);
            Assert.True(File.Exists(sourcePath));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PcmWaveSliceHasDeterministicHeaderPayloadAndHash()
    {
        var root = CreateRoot();
        try
        {
            var sourceBytes = CreatePcmWave(
                sampleRate: 1_000,
                duration: TimeSpan.FromSeconds(2));
            var sourcePath = Path.Combine(root, "recording.wav");
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(sourcePath, sourceBytes, TimeSpan.FromSeconds(2));
            var chunk = CreateChunk(
                "wave-slice",
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                estimatedRawBytes: 1_000);
            var outputDirectory = Path.Combine(root, "chunks");
            var materializer = new ManagedAudioChunkMaterializer();

            var first = await materializer.MaterializeAsync(
                job,
                chunk,
                outputDirectory,
                CancellationToken.None);
            var second = await materializer.MaterializeAsync(
                job,
                chunk,
                outputDirectory,
                CancellationToken.None);

            Assert.True(first.IsTemporary);
            Assert.Equal("wav", first.Format);
            Assert.Equal(first, second);
            var sliced = await File.ReadAllBytesAsync(first.Path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(sliced, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(sliced, 8, 4));
            Assert.Equal("fmt ", Encoding.ASCII.GetString(sliced, 12, 4));
            Assert.Equal("data", Encoding.ASCII.GetString(sliced, 36, 4));
            Assert.Equal(1_000u, BinaryPrimitives.ReadUInt32LittleEndian(sliced.AsSpan(40, 4)));
            Assert.Equal(1_044, sliced.Length);
            Assert.Equal(
                sourceBytes.AsSpan(44 + 1_000, 1_000).ToArray(),
                sliced.AsSpan(44, 1_000).ToArray());
            Assert.Equal(Sha256(sliced), first.Sha256);

            materializer.DeleteTemporary(first);
            Assert.False(File.Exists(first.Path));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticMp3SliceCopiesOnlyIntersectingCompleteFramesDeterministically()
    {
        const int frameLength = 417;
        var root = CreateRoot();
        try
        {
            var sourceBytes = CreateSyntheticMp3(frameCount: 8);
            var sourcePath = Path.Combine(root, "recording.mp3");
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var frameDuration = TimeSpan.FromSeconds(1152d / 44_100d);
            var job = CreateJob(sourcePath, sourceBytes, frameDuration * 8);
            var chunk = CreateChunk(
                "mp3-slice",
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(130),
                estimatedRawBytes: frameLength * 3);
            var outputDirectory = Path.Combine(root, "chunks");
            var materializer = new ManagedAudioChunkMaterializer();

            var first = await materializer.MaterializeAsync(
                job,
                chunk,
                outputDirectory,
                CancellationToken.None);
            var second = await materializer.MaterializeAsync(
                job,
                chunk,
                outputDirectory,
                CancellationToken.None);

            Assert.Equal(first, second);
            Assert.True(first.IsTemporary);
            Assert.Equal("mp3", first.Format);
            var sliced = await File.ReadAllBytesAsync(first.Path);
            Assert.Equal(frameLength * 3, sliced.Length);
            Assert.Equal(new byte[] { 0xff, 0xfb, 0x90, 0x00 }, sliced.AsSpan(0, 4).ToArray());
            Assert.Equal(0x22, sliced[4]);
            Assert.Equal(0x23, sliced[frameLength + 4]);
            Assert.Equal(0x24, sliced[(frameLength * 2) + 4]);
            Assert.Equal(Sha256(sliced), first.Sha256);

            materializer.DeleteTemporary(first);
            Assert.False(File.Exists(first.Path));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedLongContainerAndFailedSliceLeaveNoTemporaryArtifacts()
    {
        var root = CreateRoot();
        try
        {
            var materializer = new ManagedAudioChunkMaterializer();
            var outputDirectory = Path.Combine(root, "chunks");
            var unsupportedPath = Path.Combine(root, "recording.m4a");
            var unsupportedBytes = new byte[128];
            await File.WriteAllBytesAsync(unsupportedPath, unsupportedBytes);

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                materializer.MaterializeAsync(
                        CreateJob(unsupportedPath, unsupportedBytes, TimeSpan.FromMinutes(30)),
                        CreateChunk(
                            "unsupported",
                            TimeSpan.Zero,
                            TimeSpan.FromMinutes(10),
                            unsupportedBytes.Length),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.Empty(Directory.EnumerateFiles(outputDirectory));

            var malformedPath = Path.Combine(root, "malformed.mp3");
            var malformedBytes = new byte[512];
            await File.WriteAllBytesAsync(malformedPath, malformedBytes);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.MaterializeAsync(
                        CreateJob(malformedPath, malformedBytes, TimeSpan.FromSeconds(10)),
                        CreateChunk(
                            "malformed",
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(5),
                            malformedBytes.Length),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());

            Assert.Empty(Directory.EnumerateFiles(outputDirectory));
            Assert.True(File.Exists(unsupportedPath));
            Assert.True(File.Exists(malformedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalShortInputAlwaysUsesPlatformDecoderAndProducesCanonicalPcm()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "recording.m4a");
            var sourceBytes = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(
                sourcePath,
                sourceBytes,
                TimeSpan.FromSeconds(3),
                TranscriptionExecutionKind.Local);
            var decoder = new SyntheticPlatformDecoder();
            var materializer = new ManagedAudioChunkMaterializer(decoder);

            var result = await materializer.MaterializeAsync(
                job,
                CreateChunk("local-full", TimeSpan.Zero, TimeSpan.FromSeconds(3), sourceBytes.Length),
                Path.Combine(root, "chunks"),
                CancellationToken.None);

            Assert.Equal(1, decoder.CallCount);
            Assert.Equal("wav", result.Format);
            Assert.True(result.IsTemporary);
            Assert.NotEqual(sourcePath, result.Path);
            var wave = await File.ReadAllBytesAsync(result.Path);
            Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24, 4)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(22, 2)));
            Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(34, 2)));
            Assert.Equal(96_000u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteShortInputKeepsFullSourcePassThroughWhenPlatformDecoderExists()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "recording.flac");
            var sourceBytes = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(sourcePath, sourceBytes, TimeSpan.FromSeconds(3));
            var decoder = new SyntheticPlatformDecoder();
            var materializer = new ManagedAudioChunkMaterializer(decoder);

            var result = await materializer.MaterializeAsync(
                job,
                CreateChunk("remote-full", TimeSpan.Zero, TimeSpan.FromSeconds(3), sourceBytes.Length),
                Path.Combine(root, "chunks"),
                CancellationToken.None);

            Assert.Equal(0, decoder.CallCount);
            Assert.Equal(sourcePath, result.Path);
            Assert.Equal("flac", result.Format);
            Assert.False(result.IsTemporary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteRangedInputUsesPlatformDecoderAndFingerprintGuard()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "recording.ogg");
            var sourceBytes = Enumerable.Range(0, 1024).Select(static value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(sourcePath, sourceBytes, TimeSpan.FromMinutes(8));
            var decoder = new SyntheticPlatformDecoder();
            var materializer = new ManagedAudioChunkMaterializer(decoder);
            var outputDirectory = Path.Combine(root, "chunks");

            var result = await materializer.MaterializeAsync(
                job,
                CreateChunk("remote-range", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), 384),
                outputDirectory,
                CancellationToken.None);

            Assert.Equal(1, decoder.CallCount);
            Assert.Equal("wav", result.Format);
            Assert.True(result.IsTemporary);

            var changedJob = job with { InputSha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.MaterializeAsync(
                        changedJob,
                        CreateChunk("changed", TimeSpan.Zero, TimeSpan.FromMinutes(3), 384),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal(1, decoder.CallCount);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidRangeAndCancelledPlatformDecodeLeaveNoTemporaryArtifacts()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "recording.mp4");
            var sourceBytes = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var job = CreateJob(
                sourcePath,
                sourceBytes,
                TimeSpan.FromMinutes(6),
                TranscriptionExecutionKind.Local);
            var decoder = new SyntheticPlatformDecoder(throwAfterWrite: true);
            var materializer = new ManagedAudioChunkMaterializer(decoder);
            var outputDirectory = Path.Combine(root, "chunks");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.MaterializeAsync(
                        job,
                        CreateChunk("past-end", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7), 128),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.False(Directory.Exists(outputDirectory));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                materializer.MaterializeAsync(
                        job,
                        CreateChunk(
                            "too-long",
                            TimeSpan.Zero,
                            TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1),
                            128),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.Empty(Directory.EnumerateFiles(outputDirectory));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                materializer.MaterializeAsync(
                        job,
                        CreateChunk("cancelled", TimeSpan.Zero, TimeSpan.FromMinutes(5), 128),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.True(Directory.Exists(outputDirectory));
            Assert.Empty(Directory.EnumerateFiles(outputDirectory));

            var truncatedMaterializer = new ManagedAudioChunkMaterializer(
                new SyntheticPlatformDecoder(forcedSampleFrames: 1));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                truncatedMaterializer.MaterializeAsync(
                        job,
                        CreateChunk("truncated", TimeSpan.Zero, TimeSpan.FromMinutes(5), 128),
                        outputDirectory,
                        CancellationToken.None)
                    .AsTask());
            Assert.Empty(Directory.EnumerateFiles(outputDirectory));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AudioChunkDescriptor CreateChunk(
        string id,
        TimeSpan start,
        TimeSpan end,
        long estimatedRawBytes) =>
        new(
            Id: id,
            SequenceIndex: 0,
            Start: start,
            End: end,
            Overlap: TimeSpan.Zero,
            EstimatedRawBytes: estimatedRawBytes);

    private static TranscriptionJobRecord CreateJob(
        string sourcePath,
        byte[] sourceBytes,
        TimeSpan duration,
        TranscriptionExecutionKind executionKind = TranscriptionExecutionKind.Remote)
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        return new TranscriptionJobRecord(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: Guid.NewGuid().ToString("N"),
            EngineId: "remote.groq",
            ExecutionKind: executionKind,
            ModelId: "whisper-large-v3-turbo",
            EngineOptionsJson: null,
            RequestedLanguage: "en",
            DetectedLanguage: null,
            InputAudioPath: sourcePath,
            InputSha256: Sha256(sourceBytes),
            InputSizeBytes: sourceBytes.LongLength,
            InputDurationSeconds: duration.TotalSeconds,
            Status: TranscriptionJobStatus.Preparing,
            Progress: 0,
            CurrentChunkIndex: null,
            CurrentChunkId: null,
            QueuedAtUtc: now,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AttemptCount: 1,
            NextAttemptAtUtc: null,
            LastAttemptAtUtc: now,
            StableErrorCode: null,
            ErrorMessage: null,
            TranscriptMarkdownPath: null,
            TranscriptJsonPath: null,
            UsageJson: null,
            RemoteConsentRevision: "synthetic-consent",
            RemoteConsentAtUtc: now,
            PrivacyPolicyJson: "{}",
            ManifestVersion: 1,
            ManifestPath: null,
            ArtifactPublicationState: TranscriptionArtifactPublicationState.None,
            StagedTranscriptMarkdownPath: null,
            StagedTranscriptJsonPath: null,
            StagedTranscriptMarkdownSha256: null,
            StagedTranscriptJsonSha256: null,
            ReplaceExisting: false,
            CancellationRequested: false,
            CompletedAtUtc: null,
            CancelledAtUtc: null);
    }

    private static byte[] CreatePcmWave(int sampleRate, TimeSpan duration)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * bitsPerSample / 8;
        var byteRate = checked(sampleRate * blockAlign);
        var dataLength = checked((int)Math.Round(
            duration.TotalSeconds * byteRate,
            MidpointRounding.AwayFromZero));
        var bytes = new byte[44 + dataLength];
        WriteAscii(bytes.AsSpan(0, 4), "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        WriteAscii(bytes.AsSpan(8, 4), "WAVE");
        WriteAscii(bytes.AsSpan(12, 4), "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), checked((uint)byteRate));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), bitsPerSample);
        WriteAscii(bytes.AsSpan(36, 4), "data");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)dataLength));
        for (var index = 0; index < dataLength; index++)
        {
            bytes[44 + index] = (byte)(index % 251);
        }

        return bytes;
    }

    private static byte[] CreateSyntheticMp3(int frameCount)
    {
        const int frameLength = 417;
        var bytes = new byte[10 + (frameCount * frameLength)];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'3';
        bytes[3] = 4;
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var offset = 10 + (frameIndex * frameLength);
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = 0x90;
            bytes[offset + 3] = 0x00;
            bytes.AsSpan(offset + 4, frameLength - 4).Fill((byte)(0x20 + frameIndex));
        }

        return bytes;
    }

    private static void WriteAscii(Span<byte> destination, string value) =>
        Encoding.ASCII.GetBytes(value, destination);

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-audio-chunks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SyntheticPlatformDecoder(
        bool throwAfterWrite = false,
        long? forcedSampleFrames = null) : IPlatformAudioChunkDecoder
    {
        public int CallCount { get; private set; }

        public bool CanDecode(string sourceFormat) => true;

        public async ValueTask<PlatformAudioChunkDecodeResult> DecodeAsync(
            PlatformAudioChunkDecodeRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var validated = request.Validate();
            var sampleFrames = forcedSampleFrames ?? checked((long)Math.Round(
                validated.Duration.TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate,
                MidpointRounding.AwayFromZero));
            var wave = CreatePcmWave(
                PlatformAudioChunkDecodeRequest.TargetSampleRate,
                TimeSpan.FromSeconds(
                    (double)sampleFrames / PlatformAudioChunkDecodeRequest.TargetSampleRate));
            await File.WriteAllBytesAsync(validated.OutputWavePath, wave, cancellationToken);
            if (throwAfterWrite)
            {
                throw new OperationCanceledException("Synthetic platform cancellation.");
            }

            return new PlatformAudioChunkDecodeResult(
                validated.OutputWavePath,
                PlatformAudioChunkDecodeRequest.TargetSampleRate,
                PlatformAudioChunkDecodeRequest.TargetChannels,
                PlatformAudioChunkDecodeRequest.TargetBitsPerSample,
                sampleFrames,
                validated.Start,
                validated.End);
        }
    }
}
