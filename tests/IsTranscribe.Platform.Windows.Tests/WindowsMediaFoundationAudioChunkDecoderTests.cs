using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using IsTranscribe.Application.Transcription;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Windows system-codec acceptance and cross-build contracts for bounded transcription decode.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaFoundationAudioChunkDecoderTests
{
    [Fact]
    public void AdapterPublishesSystemDecoderThroughSharedPlatformContract()
    {
        ITranscriptionAudioDecoderPlatformAdapter adapter =
            new WindowsApplicationPlatformRuntimeAdapter();

        Assert.IsType<WindowsMediaFoundationAudioChunkDecoder>(
            adapter.CreateTranscriptionAudioChunkDecoder());
    }

    [Fact]
    public void CapabilityMappingIsBoundedAndIncludesOwnedOggOpusDecoder()
    {
        var available = new WindowsMediaFoundationDecodeCapabilities(
            PcmWave: true,
            Mp3: true,
            Aac: true,
            Alac: true,
            Flac: true,
            OggOpus: true);

        Assert.All(
            new[] { "wav", ".wave", "MP3", "m4a", ".mp4", "flac", "ogg", "oga", "opus" },
            format => Assert.True(available.Supports(format), format));
        Assert.All(
            new[] { "aac", "mov", "exe", "" },
            format => Assert.False(available.Supports(format), format));
        Assert.All(
            new[] { "wav", "mp3", "m4a", "mp4", "flac" },
            format => Assert.False(
                WindowsMediaFoundationDecodeCapabilities.Empty.Supports(format),
                format));
    }

    [Fact]
    public async Task RejectsRangeAboveFiveMinutesBeforeNativeOrFileWork()
    {
        var request = new PlatformAudioChunkDecodeRequest(
            Path.Combine(Path.GetTempPath(), "missing-source.wav"),
            "wav",
            new string('a', 64),
            ExpectedSourceSizeBytes: 1,
            ExpectedSourceDuration: TimeSpan.FromMinutes(6),
            Start: TimeSpan.Zero,
            End: TimeSpan.FromMinutes(6),
            OutputWavePath: Path.Combine(Path.GetTempPath(), "missing-output.wav.tmp"));
        var decoder = new WindowsMediaFoundationAudioChunkDecoder();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            decoder.DecodeAsync(request, CancellationToken.None).AsTask());
    }

    [Fact]
    public void SystemCapabilityProbeDeclaresRequiredNativeFormatsWhenAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var decoder = new WindowsMediaFoundationAudioChunkDecoder();

        Assert.True(decoder.CanDecode("wav"));
        Assert.True(decoder.CanDecode("mp3"));
        Assert.True(decoder.CanDecode("m4a"));
        Assert.True(decoder.CanDecode("mp4"));
        Assert.True(decoder.CanDecode("flac"));
        Assert.True(decoder.CanDecode("ogg"));
        Assert.True(decoder.CanDecode("opus"));
    }

    [Fact]
    public async Task DecodesExactStereoWaveRangeToBoundedCanonicalPcm()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var source = CreateSegmentedStereoPcmWave();
            await File.WriteAllBytesAsync(sourcePath, source);
            var outputPath = Path.Combine(root, "range.wav.tmp");
            var decoder = new WindowsMediaFoundationAudioChunkDecoder();

            var result = await decoder.DecodeAsync(
                CreateRequest(
                    sourcePath,
                    source,
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    outputPath),
                CancellationToken.None);

            Assert.Equal(16_000, result.SampleRate);
            Assert.Equal(1, result.Channels);
            Assert.Equal(16, result.BitsPerSample);
            Assert.Equal(16_000, result.SampleFrames);
            Assert.Equal(TimeSpan.FromSeconds(1), result.DecodedStart);
            Assert.Equal(TimeSpan.FromSeconds(2), result.DecodedEnd);
            var samples = AssertCanonicalWave(outputPath, expectedFrames: 16_000);
            Assert.True(
                MeasureFrequencyMagnitude(samples, 16_000, 880) > 0.15,
                "Decoded range does not contain the selected middle-segment frequency.");
            Assert.True(
                MeasureFrequencyMagnitude(samples, 16_000, 220) < 0.03,
                "Decoded range retained too much audio from before the selected boundary.");
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FingerprintCancellationAndExistingOutputFailClosed()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var source = CreateSegmentedStereoPcmWave();
            await File.WriteAllBytesAsync(sourcePath, source);
            var decoder = new WindowsMediaFoundationAudioChunkDecoder();

            var mismatchPath = Path.Combine(root, "size-mismatch.wav.tmp");
            var mismatch = CreateRequest(
                sourcePath,
                source,
                TimeSpan.FromSeconds(3),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                mismatchPath) with
            {
                ExpectedSourceSizeBytes = source.LongLength + 1
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                decoder.DecodeAsync(mismatch, CancellationToken.None).AsTask());
            Assert.False(File.Exists(mismatchPath));

            var hashMismatchPath = Path.Combine(root, "hash-mismatch.wav.tmp");
            var hashMismatch = CreateRequest(
                sourcePath,
                source,
                TimeSpan.FromSeconds(3),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                hashMismatchPath) with
            {
                ExpectedSourceSha256 = new string('b', 64)
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                decoder.DecodeAsync(hashMismatch, CancellationToken.None).AsTask());
            Assert.False(File.Exists(hashMismatchPath));

            var cancellationPath = Path.Combine(root, "cancelled.wav.tmp");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                decoder.DecodeAsync(
                        CreateRequest(
                            sourcePath,
                            source,
                            TimeSpan.FromSeconds(3),
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            cancellationPath),
                        cancellation.Token)
                    .AsTask());
            Assert.False(File.Exists(cancellationPath));

            var existingPath = Path.Combine(root, "existing.wav.tmp");
            await File.WriteAllBytesAsync(existingPath, [1, 2, 3, 4]);
            await Assert.ThrowsAsync<IOException>(() =>
                decoder.DecodeAsync(
                        CreateRequest(
                            sourcePath,
                            source,
                            TimeSpan.FromSeconds(3),
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            existingPath),
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(existingPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MidDecodeCancellationRemovesOwnedPartialOutput()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "long-source.wav");
            var source = CreateSegmentedStereoPcmWave(durationSeconds: 300);
            await File.WriteAllBytesAsync(sourcePath, source);
            var outputPath = Path.Combine(root, "cancelled-during-decode.wav.tmp");
            using var cancellation = new CancellationTokenSource();
            var decode = new WindowsMediaFoundationAudioChunkDecoder().DecodeAsync(
                    CreateRequest(
                        sourcePath,
                        source,
                        TimeSpan.FromMinutes(5),
                        TimeSpan.Zero,
                        TimeSpan.FromMinutes(5),
                        outputPath),
                    cancellation.Token)
                .AsTask();

            for (var attempt = 0; attempt < 5_000 && !File.Exists(outputPath) && !decode.IsCompleted; attempt++)
            {
                await Task.Delay(1);
            }

            Assert.True(File.Exists(outputPath), "Decode completed before an owned partial output could be observed.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
            Assert.False(File.Exists(outputPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsSourceThatAlreadyHasAnActiveWriter()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "mutable-source.wav");
            var source = CreateSegmentedStereoPcmWave();
            await File.WriteAllBytesAsync(sourcePath, source);
            await using var writer = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            var outputPath = Path.Combine(root, "mutable-output.wav.tmp");

            await Assert.ThrowsAsync<IOException>(() =>
                new WindowsMediaFoundationAudioChunkDecoder().DecodeAsync(
                        CreateRequest(
                            sourcePath,
                            source,
                            TimeSpan.FromSeconds(3),
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            outputPath),
                        CancellationToken.None)
                    .AsTask());

            Assert.False(File.Exists(outputPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("mp4")]
    [InlineData("flac")]
    public async Task RealSystemCodecFixturePreservesStartTailAndAdjacentBoundaries(string format)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", $"piecewise.{format}");
        Assert.True(File.Exists(sourcePath), $"Missing checked-in {format} acceptance fixture.");
        var source = await File.ReadAllBytesAsync(sourcePath);
        var decoder = new WindowsMediaFoundationAudioChunkDecoder();
        Assert.True(decoder.CanDecode(format), $"System capability is unavailable for {format}.");
        var root = CreateRoot();
        try
        {
            var start = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(1.1),
                TimeSpan.FromSeconds(1.8),
                Path.Combine(root, $"{format}-start.wav.tmp"));
            AssertDominantFrequency(start, 880, 220, $"{format} nonzero start");

            var tail = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(3.1),
                TimeSpan.FromSeconds(3.8),
                Path.Combine(root, $"{format}-tail.wav.tmp"));
            AssertDominantFrequency(tail, 660, 440, $"{format} tail");

            var left = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(0.5),
                TimeSpan.FromSeconds(2),
                Path.Combine(root, $"{format}-left.wav.tmp"));
            var right = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3.5),
                Path.Combine(root, $"{format}-right.wav.tmp"));
            Assert.Equal(24_000, left.Length);
            Assert.Equal(24_000, right.Length);
            AssertDominantFrequency(left[^8_000..], 880, 440, $"{format} left adjacent tail");
            AssertDominantFrequency(right[..8_000], 440, 880, $"{format} right adjacent start");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("ISTRANSCRIBE_TEST_WINDOWS_MP3_FIXTURE", "mp3")]
    [InlineData("ISTRANSCRIBE_TEST_WINDOWS_M4A_FIXTURE", "m4a")]
    [InlineData("ISTRANSCRIBE_TEST_WINDOWS_MP4_FIXTURE", "mp4")]
    [InlineData("ISTRANSCRIBE_TEST_WINDOWS_FLAC_FIXTURE", "flac")]
    [InlineData("ISTRANSCRIBE_TEST_WINDOWS_WAV_FIXTURE", "wav")]
    public async Task DecodesProvidedWindowsSystemCodecFixture(
        string environmentVariable,
        string format)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var sourcePath = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(format, Path.GetExtension(sourcePath).TrimStart('.'), ignoreCase: true);
        var decoder = new WindowsMediaFoundationAudioChunkDecoder();
        Assert.True(decoder.CanDecode(format), $"System capability is unavailable for {format}.");

        TimeSpan sourceDuration;
        using (var reader = new MediaFoundationReader(sourcePath))
        {
            sourceDuration = reader.TotalTime;
        }

        Assert.True(sourceDuration > TimeSpan.Zero);
        var start = sourceDuration > TimeSpan.FromSeconds(1.25)
            ? TimeSpan.FromMilliseconds(250)
            : TimeSpan.Zero;
        var end = TimeSpan.FromTicks(Math.Min(
            sourceDuration.Ticks,
            (start + TimeSpan.FromSeconds(1)).Ticks));
        Assert.True(end > start);

        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(sourceStream))
            .ToLowerInvariant();
        var sourceSize = sourceStream.Length;
        var root = CreateRoot();
        try
        {
            var outputPath = Path.Combine(root, $"{format}-range.wav.tmp");
            var result = await decoder.DecodeAsync(
                new PlatformAudioChunkDecodeRequest(
                    sourcePath,
                    format,
                    sha256,
                    sourceSize,
                    sourceDuration,
                    start,
                    end,
                    outputPath),
                CancellationToken.None);

            Assert.True(result.SampleFrames > 0);
            Assert.InRange(result.SampleFrames, 1, 16_000);
            AssertCanonicalWave(outputPath, result.SampleFrames);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OwnedOggOpusDecoderPreservesNonzeroAndAdjacentRanges()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19_041))
        {
            return;
        }

        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "piecewise.ogg");
        var source = await File.ReadAllBytesAsync(sourcePath);
        var decoder = new WindowsMediaFoundationAudioChunkDecoder();
        Assert.True(decoder.CanDecode("ogg"));
        var root = CreateRoot();
        try
        {
            var middle = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(1.1),
                TimeSpan.FromSeconds(1.8),
                Path.Combine(root, "ogg-middle.wav.tmp"));
            var adjacent = await DecodeRange(
                decoder,
                sourcePath,
                source,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3.5),
                Path.Combine(root, "ogg-adjacent.wav.tmp"));

            AssertDominantFrequency(middle, 880, 220, "OGG nonzero start");
            AssertDominantFrequency(adjacent[..8_000], 440, 880, "OGG adjacent start");
            AssertDominantFrequency(adjacent[^8_000..], 660, 440, "OGG tail");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ProductDecoderContainsNoCodecProcessOrFullWaveFallback()
    {
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            root,
            "src",
            "IsTranscribe.Platform.Windows",
            "Audio",
            "Transcription",
            "WindowsMediaFoundationAudioChunkDecoder.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("MediaFoundationReader", source, StringComparison.Ordinal);
        Assert.Contains("MediaFoundationResampler", source, StringComparison.Ordinal);
        Assert.Contains("OggOpusRangeWaveProvider", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileShare.Read", source, StringComparison.Ordinal);
        Assert.Contains("FramesPerRead = 4_096", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ffmpeg", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whisper-cli", source, StringComparison.OrdinalIgnoreCase);
    }

    private static PlatformAudioChunkDecodeRequest CreateRequest(
        string sourcePath,
        byte[] source,
        TimeSpan sourceDuration,
        TimeSpan start,
        TimeSpan end,
        string outputPath) =>
        new(
            sourcePath,
            Path.GetExtension(sourcePath),
            Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
            source.LongLength,
            sourceDuration,
            start,
            end,
            outputPath);

    private static async Task<short[]> DecodeRange(
        WindowsMediaFoundationAudioChunkDecoder decoder,
        string sourcePath,
        byte[] source,
        TimeSpan start,
        TimeSpan end,
        string outputPath)
    {
        var result = await decoder.DecodeAsync(
            CreateRequest(
                sourcePath,
                source,
                TimeSpan.FromSeconds(4),
                start,
                end,
                outputPath),
            CancellationToken.None);
        var expectedFrames = checked((long)Math.Ceiling(
            (end - start).TotalSeconds * PlatformAudioChunkDecodeRequest.TargetSampleRate));
        Assert.Equal(expectedFrames, result.SampleFrames);
        Assert.Equal(start, result.DecodedStart);
        Assert.Equal(end, result.DecodedEnd);
        return AssertCanonicalWave(outputPath, expectedFrames);
    }

    private static void AssertDominantFrequency(
        IReadOnlyList<short> samples,
        double expected,
        double rejected,
        string evidence)
    {
        var expectedMagnitude = MeasureFrequencyMagnitude(samples, 16_000, expected);
        var rejectedMagnitude = MeasureFrequencyMagnitude(samples, 16_000, rejected);
        Assert.True(
            expectedMagnitude > 0.04,
            $"{evidence}: expected {expected} Hz magnitude was {expectedMagnitude:F4}.");
        Assert.True(
            expectedMagnitude > rejectedMagnitude * 4,
            $"{evidence}: expected {expected} Hz ({expectedMagnitude:F4}) did not dominate {rejected} Hz ({rejectedMagnitude:F4}).");
    }

    private static short[] AssertCanonicalWave(string path, long expectedFrames)
    {
        var wave = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), wave.AsSpan(0, 4).ToArray());
        Assert.Equal("WAVE"u8.ToArray(), wave.AsSpan(8, 4).ToArray());
        Assert.Equal("fmt "u8.ToArray(), wave.AsSpan(12, 4).ToArray());
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(22, 2)));
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24, 4)));
        Assert.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(28, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(32, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(34, 2)));
        Assert.Equal("data"u8.ToArray(), wave.AsSpan(36, 4).ToArray());
        Assert.Equal(
            checked((uint)(expectedFrames * sizeof(short))),
            BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal(44 + (expectedFrames * sizeof(short)), wave.LongLength);

        var samples = new short[checked((int)expectedFrames)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                wave.AsSpan(44 + (index * sizeof(short)), sizeof(short)));
        }

        return samples;
    }

    private static byte[] CreateSegmentedStereoPcmWave(int durationSeconds = 3)
    {
        const int sampleRate = 48_000;
        const ushort channels = 2;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * bitsPerSample / 8;
        var sampleFrames = sampleRate * durationSeconds;
        var dataLength = sampleFrames * blockAlign;
        var wave = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(4, 4),
            checked((uint)(wave.Length - 8)));
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(28, 4),
            sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(40, 4),
            checked((uint)dataLength));

        for (var frame = 0; frame < sampleFrames; frame++)
        {
            var second = frame / sampleRate;
            var frequency = (second % 3) switch
            {
                0 => 220d,
                1 => 880d,
                _ => 440d
            };
            var sample = checked((short)Math.Round(
                Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * 12_000));
            var offset = 44 + (frame * blockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(offset, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(offset + 2, 2), sample);
        }

        return wave;
    }

    private static double MeasureFrequencyMagnitude(
        IReadOnlyList<short> samples,
        int sampleRate,
        double frequency)
    {
        double sine = 0;
        double cosine = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var value = samples[index] / 32_768d;
            var phase = 2 * Math.PI * frequency * index / sampleRate;
            sine += value * Math.Sin(phase);
            cosine += value * Math.Cos(phase);
        }

        return samples.Count == 0
            ? 0
            : 2 * Math.Sqrt((sine * sine) + (cosine * cosine)) / samples.Count;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-windows-audio-decode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root from the test output path.");
    }
}
