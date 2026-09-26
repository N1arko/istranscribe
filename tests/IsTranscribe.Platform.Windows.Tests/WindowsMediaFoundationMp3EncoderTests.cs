using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Application.Recording;
using IsTranscribe.Platform.Windows.Audio.Encoding;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#verification
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaFoundationMp3EncoderTests
{
    [Fact]
    public void AvailabilityProbeFindsCanonicalMp3MediaType()
    {
        var encoder = new WindowsMediaFoundationMp3Encoder();

        var availability = encoder.ProbeAvailability();

        Assert.True(availability.IsAvailable, availability.Detail);
        Assert.Equal("windows-media-foundation-mp3", availability.Codec);
        Assert.Equal(48_000, availability.SampleRate);
        Assert.Equal(2, availability.Channels);
        Assert.Equal(16, availability.BitsPerSample);
        Assert.Equal(128_000, availability.BitRate);
    }

    [Fact]
    public async Task ExplicitMp3StreamEncodesPartialPathAndCanonicalizesFloatMono()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var partialPath = Path.Combine(root, "meeting.mp3.partial");
            WriteSineWave(sourcePath, sampleRate: 16_000, channels: 1, duration: TimeSpan.FromSeconds(1));
            var encoder = new WindowsMediaFoundationMp3Encoder();

            var result = await encoder.EncodeAsync(sourcePath, partialPath, CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal(Path.GetFullPath(partialPath), result.PartialOutputPath);
            Assert.Equal(48_000, result.SampleRate);
            Assert.Equal(2, result.Channels);
            Assert.Equal(16, result.BitsPerSample);
            Assert.Equal(128_000, result.BitRate);
            Assert.True(result.OutputBytes > 4);
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(partialPath));
            Assert.True(HasMp3StreamSignature(partialPath));

            var readability = encoder.ProbeReadability(partialPath);
            Assert.True(readability.IsReadable, readability.Detail);
            Assert.Equal(48_000, readability.SampleRate);
            Assert.Equal(2, readability.Channels);
            Assert.InRange(
                readability.Duration!.Value.TotalSeconds,
                0.9,
                1.1);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PreparedOutputAndMicrophoneFrequenciesRemainPresentInPrimaryMp3()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(root, "output.wav");
            var microphonePath = Path.Combine(root, "mic.wav");
            WriteSineWave(
                outputPath,
                sampleRate: 44_100,
                channels: 1,
                duration: TimeSpan.FromSeconds(1),
                frequency: 440);
            WriteSineWave(
                microphonePath,
                sampleRate: 48_000,
                channels: 2,
                duration: TimeSpan.FromSeconds(1),
                frequency: 880);
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("output"),
                    AudioCaptureSourceRequest.Microphone("mic")
                ],
                root,
                PrebufferSeconds: 0,
                CreateMixedArtifact: true);
            var prepared = await new AudioMixArtifactBuilder().PrepareAsync(
                request,
                [
                    new AudioCaptureArtifact(
                        AudioCaptureArtifactKind.Output,
                        outputPath,
                        new FileInfo(outputPath).Length,
                        DateTimeOffset.UtcNow),
                    new AudioCaptureArtifact(
                        AudioCaptureArtifactKind.Microphone,
                        microphonePath,
                        new FileInfo(microphonePath).Length,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMilliseconds(40))
                ],
                CancellationToken.None);
            Assert.NotNull(prepared);

            var partialPath = Path.Combine(root, "meeting.mp3.partial");
            var encoder = new WindowsMediaFoundationMp3Encoder();
            var encoded = await encoder.EncodeAsync(
                prepared.Path,
                partialPath,
                CancellationToken.None);

            Assert.True(encoded.Succeeded, encoded.ErrorMessage);
            using var reader = new MediaFoundationReader(partialPath);
            var samples = ReadSamples(reader.ToSampleProvider());
            var leftChannel = samples
                .Where((_, index) => index % 2 == 0)
                .ToArray();
            Assert.True(
                MeasureFrequencyMagnitude(leftChannel, reader.WaveFormat.SampleRate, 440) > 0.03,
                "Decoded primary artifact does not contain the output frequency.");
            Assert.True(
                MeasureFrequencyMagnitude(leftChannel, reader.WaveFormat.SampleRate, 880) > 0.03,
                "Decoded primary artifact does not contain the microphone frequency.");
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.95, 1.15);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CancellationDeletesOnlyPartialOutputAndPreservesSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var partialPath = Path.Combine(root, "meeting.mp3.partial");
            WriteSineWave(sourcePath, sampleRate: 48_000, channels: 2, duration: TimeSpan.FromSeconds(4));
            var encoder = new WindowsMediaFoundationMp3Encoder();
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<AudioArtifactEncodingProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoder.EncodeAsync(
                new AudioArtifactEncodeRequest(sourcePath, partialPath),
                progress,
                cancellation.Token));

            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UnsupportedChannelLayoutFailsWithoutDeletingSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var partialPath = Path.Combine(root, "meeting.mp3.partial");
            WriteSineWave(sourcePath, sampleRate: 48_000, channels: 3, duration: TimeSpan.FromMilliseconds(250));
            var encoder = new WindowsMediaFoundationMp3Encoder();

            var result = await encoder.EncodeAsync(sourcePath, partialPath, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("source_format_unsupported", result.ErrorCode);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExistingPartialArtifactIsNeverOverwritten()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "source.wav");
            var partialPath = Path.Combine(root, "meeting.mp3.partial");
            WriteSineWave(sourcePath, sampleRate: 44_100, channels: 2, duration: TimeSpan.FromMilliseconds(250));
            await File.WriteAllBytesAsync(partialPath, [1, 2, 3, 4]);
            var encoder = new WindowsMediaFoundationMp3Encoder();

            var result = await encoder.EncodeAsync(sourcePath, partialPath, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("partial_output_exists", result.ErrorCode);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(partialPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReadabilityProbeRejectsNonMp3Payload()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "broken.mp3.partial");
            File.WriteAllBytes(path, new byte[32]);
            var encoder = new WindowsMediaFoundationMp3Encoder();

            var result = encoder.ProbeReadability(path);

            Assert.False(result.IsReadable);
            Assert.Equal("mp3_stream_invalid", result.ReasonCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Long")]
    public async Task TwoHourSparseWaveEncodesWithBoundedProcessMemory()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LONG_AUDIO"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "two-hours.wav");
            var partialPath = Path.Combine(root, "two-hours.mp3.partial");
            WriteSparsePcm16SilenceWave(sourcePath, TimeSpan.FromHours(2));
            var encoder = new WindowsMediaFoundationMp3Encoder();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var baselineBytes = process.PrivateMemorySize64;
            var peakBytes = baselineBytes;

            var encodeTask = encoder.EncodeAsync(sourcePath, partialPath, CancellationToken.None);
            while (!encodeTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                process.Refresh();
                peakBytes = Math.Max(peakBytes, process.PrivateMemorySize64);
            }

            var result = await encodeTask;

            Assert.True(result.Succeeded, result.ErrorMessage);
            var probe = encoder.ProbeReadability(partialPath);
            Assert.True(probe.IsReadable, probe.Detail);
            Assert.InRange(probe.Duration!.Value.TotalSeconds, 7_199, 7_201);
            var observedMemoryGrowthBytes = peakBytes - baselineBytes;
            Console.WriteLine(
                $"Two-hour encode observed peak private-memory growth: {observedMemoryGrowthBytes} bytes.");
            Assert.InRange(observedMemoryGrowthBytes, 0, 512L * 1024 * 1024);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void WriteSineWave(
        string path,
        int sampleRate,
        int channels,
        TimeSpan duration,
        double frequency = 440)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(path, format);
        var frameCount = checked((int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * 0.25);
            for (var channel = 0; channel < channels; channel++)
            {
                writer.WriteSample(sample);
            }
        }
    }

    private static float[] ReadSamples(ISampleProvider sampleProvider)
    {
        var samples = new List<float>();
        var buffer = new float[8_192];
        while (true)
        {
            var read = sampleProvider.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return samples.ToArray();
            }

            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    private static double MeasureFrequencyMagnitude(
        IReadOnlyList<float> samples,
        int sampleRate,
        double frequency)
    {
        double sine = 0;
        double cosine = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var phase = 2 * Math.PI * frequency * index / sampleRate;
            sine += samples[index] * Math.Sin(phase);
            cosine += samples[index] * Math.Cos(phase);
        }

        return samples.Count == 0
            ? 0
            : 2 * Math.Sqrt((sine * sine) + (cosine * cosine)) / samples.Count;
    }

    private static void WriteSparsePcm16SilenceWave(string path, TimeSpan duration)
    {
        const int sampleRate = 48_000;
        const short channels = 2;
        const short bitsPerSample = 16;
        const short blockAlign = channels * (bitsPerSample / 8);
        const int byteRate = sampleRate * blockAlign;
        var dataBytes = checked((uint)(duration.TotalSeconds * byteRate));

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(dataBytes + 36));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Flush();
        stream.SetLength(checked(44L + dataBytes));
        stream.Flush(flushToDisk: true);
    }

    private static bool HasMp3StreamSignature(string path)
    {
        var bytes = File.ReadAllBytes(path).AsSpan(0, checked((int)Math.Min(64 * 1024, new FileInfo(path).Length)));
        if (bytes.Length >= 3
            && bytes[0] == (byte)'I'
            && bytes[1] == (byte)'D'
            && bytes[2] == (byte)'3')
        {
            return true;
        }

        for (var offset = 0; offset <= bytes.Length - 4; offset++)
        {
            var second = bytes[offset + 1];
            var third = bytes[offset + 2];
            if (bytes[offset] == byte.MaxValue
                && (second & 0xE0) == 0xE0
                && (second & 0x18) == 0x18
                && (second & 0x06) == 0x02
                && (third & 0xF0) is not 0x00 and not 0xF0
                && (third & 0x0C) != 0x0C)
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-mp3-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
