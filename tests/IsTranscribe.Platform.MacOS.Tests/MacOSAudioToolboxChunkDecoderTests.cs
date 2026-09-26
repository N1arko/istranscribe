using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// Real macOS system-codec acceptance for bounded transcription sample decoding.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </remarks>
public sealed class MacOSAudioToolboxChunkDecoderTests
{
    [Fact]
    public async Task DecodesStereoWaveRangeToBoundedSixteenKilohertzMonoPcm()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var source = CreateStereoPcmWave(TimeSpan.FromSeconds(2));
            await File.WriteAllBytesAsync(sourcePath, source);
            var outputPath = Path.Combine(root, "range.wav.tmp");
            var decoder = new MacOSAudioToolboxChunkDecoder();

            var result = await decoder.DecodeAsync(
                CreateRequest(
                    sourcePath,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(1_250),
                    outputPath),
                CancellationToken.None);

            Assert.Equal(16_000, result.SampleRate);
            Assert.Equal(1, result.Channels);
            Assert.Equal(16, result.BitsPerSample);
            Assert.Equal(16_000, result.SampleFrames);
            AssertCanonicalWave(outputPath, expectedFrames: 16_000);
            var output = await File.ReadAllBytesAsync(outputPath);
            Assert.Contains(
                Enumerable.Range(44, output.Length - 44),
                index => output[index] != 0);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("m4a", "m4af", "aac")]
    [InlineData("mp4", "mp4f", "aac")]
    [InlineData("flac", "flac", "flac")]
    public async Task DecodesReadableSystemContainerRange(
        string extension,
        string fileFormat,
        string dataFormat)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var wavePath = Path.Combine(root, "source.wav");
            await File.WriteAllBytesAsync(
                wavePath,
                CreateStereoPcmWave(TimeSpan.FromSeconds(3)));
            var sourcePath = Path.Combine(root, $"source.{extension}");
            await ConvertFixtureAsync(wavePath, sourcePath, fileFormat, dataFormat);
            var source = await File.ReadAllBytesAsync(sourcePath);
            var outputPath = Path.Combine(root, $"{extension}-range.wav.tmp");
            var decoder = new MacOSAudioToolboxChunkDecoder();

            var result = await decoder.DecodeAsync(
                CreateRequest(
                    sourcePath,
                    source,
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(1_500),
                    outputPath),
                CancellationToken.None);

            Assert.InRange(result.SampleFrames, 15_990, 16_000);
            AssertCanonicalWave(outputPath, result.SampleFrames);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsFingerprintAndCancellationBeforeCreatingOutput()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var source = CreateStereoPcmWave(TimeSpan.FromSeconds(1));
            await File.WriteAllBytesAsync(sourcePath, source);
            var decoder = new MacOSAudioToolboxChunkDecoder();
            var sizeMismatchOutput = Path.Combine(root, "size-mismatch.wav.tmp");
            var sizeMismatch = CreateRequest(
                sourcePath,
                source,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                sizeMismatchOutput) with
            {
                ExpectedSourceSizeBytes = source.LongLength + 1
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                decoder.DecodeAsync(sizeMismatch, CancellationToken.None).AsTask());
            Assert.False(File.Exists(sizeMismatchOutput));

            var cancellationOutput = Path.Combine(root, "cancelled.wav.tmp");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                decoder.DecodeAsync(
                        CreateRequest(
                            sourcePath,
                            source,
                            TimeSpan.FromSeconds(1),
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            cancellationOutput),
                        cancellation.Token)
                    .AsTask());
            Assert.False(File.Exists(cancellationOutput));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeclaresAllReadableSavedRecordingFormatsWithoutExternalCodecProcess()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        var decoder = new MacOSAudioToolboxChunkDecoder();
        Assert.All(
            new[] { "mp3", "m4a", "mp4", "flac", "ogg", "oga", "opus", "wav" },
            format => Assert.True(decoder.CanDecode(format), format));
    }

    [Theory]
    [InlineData("ISTRANSCRIBE_TEST_MP3_FIXTURE")]
    [InlineData("ISTRANSCRIBE_TEST_OGG_FIXTURE")]
    public async Task DecodesProvidedReadableAcceptanceFixture(string environmentVariable)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
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
        var source = await File.ReadAllBytesAsync(sourcePath);
        var root = CreateRoot();
        try
        {
            var outputPath = Path.Combine(root, "acceptance-range.wav.tmp");
            var decoder = new MacOSAudioToolboxChunkDecoder();
            Assert.True(decoder.CanDecode(Path.GetExtension(sourcePath)));

            var result = await decoder.DecodeAsync(
                CreateRequest(
                    sourcePath,
                    source,
                    TimeSpan.FromMinutes(5),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    outputPath),
                CancellationToken.None);

            Assert.True(result.SampleFrames > 0);
            Assert.InRange(result.SampleFrames, 1, 16_000);
            AssertCanonicalWave(outputPath, result.SampleFrames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static async Task ConvertFixtureAsync(
        string inputPath,
        string outputPath,
        string fileFormat,
        string dataFormat)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/afconvert",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(fileFormat);
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add(dataFormat);
        if (string.Equals(dataFormat, "aac", StringComparison.Ordinal))
        {
            process.StartInfo.ArgumentList.Add("-b");
            process.StartInfo.ArgumentList.Add("64000");
        }

        Assert.True(process.Start());
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"afconvert fixture creation failed ({process.ExitCode}): {output} {error}");
        Assert.True(File.Exists(outputPath));
    }

    private static void AssertCanonicalWave(string path, long expectedFrames)
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
        Assert.Equal(checked((uint)(expectedFrames * sizeof(short))),
            BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal(44 + (expectedFrames * sizeof(short)), wave.LongLength);
    }

    private static byte[] CreateStereoPcmWave(TimeSpan duration)
    {
        const int sampleRate = 48_000;
        const ushort channels = 2;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * bitsPerSample / 8;
        var sampleFrames = checked((int)Math.Round(
            duration.TotalSeconds * sampleRate,
            MidpointRounding.AwayFromZero));
        var dataLength = checked(sampleFrames * blockAlign);
        var wave = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), checked((uint)(wave.Length - 8)));
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), checked((uint)dataLength));
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            var time = (double)frame / sampleRate;
            var left = checked((short)Math.Round(Math.Sin(2 * Math.PI * 440 * time) * 8_000));
            var right = checked((short)Math.Round(Math.Sin(2 * Math.PI * 660 * time) * 8_000));
            var offset = 44 + (frame * blockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(offset, 2), left);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(offset + 2, 2), right);
        }

        return wave;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-macos-audio-decode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
