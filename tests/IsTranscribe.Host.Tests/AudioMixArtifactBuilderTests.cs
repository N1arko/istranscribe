using System.Buffers.Binary;
using IsTranscribe.Host.Audio.Capture;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// </summary>
public sealed class AudioMixArtifactBuilderTests
{
    [Fact]
    public async Task PrepareAsyncProducesCanonicalWaveContainingBothSourceSignals()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempRoot, "output.wav");
            var micPath = Path.Combine(tempRoot, "mic.wav");
            WriteToneWave(outputPath, sampleRate: 16_000, channels: 1, frequencyHz: 440, amplitude: 0.6f, duration: TimeSpan.FromSeconds(1));
            WriteToneWave(micPath, sampleRate: 44_100, channels: 2, frequencyHz: 880, amplitude: 0.5f, duration: TimeSpan.FromSeconds(1));

            var artifact = await PrepareAsync(
                tempRoot,
                [
                    CreateArtifact(AudioCaptureArtifactKind.Output, outputPath),
                    CreateArtifact(AudioCaptureArtifactKind.Microphone, micPath)
                ]);

            Assert.NotNull(artifact);
            Assert.Equal(AudioCaptureArtifactKind.Mixed, artifact!.Kind);
            var decoded = ReadCanonicalWave(artifact.Path);
            Assert.InRange(CalculateToneMagnitude(decoded.MonoSamples, decoded.SampleRate, 440), 0.20, 0.60);
            Assert.InRange(CalculateToneMagnitude(decoded.MonoSamples, decoded.SampleRate, 880), 0.15, 0.55);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncProducesSingleSourceWaveWhenCaptureTimeMixWasDisabled()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var micPath = Path.Combine(tempRoot, "mic.wav");
            WriteToneWave(micPath, sampleRate: 22_050, channels: 1, frequencyHz: 620, amplitude: 0.45f, duration: TimeSpan.FromMilliseconds(400));

            var artifact = await PrepareAsync(
                tempRoot,
                [CreateArtifact(AudioCaptureArtifactKind.Microphone, micPath)],
                [AudioCaptureSourceRequest.Microphone("mic-1")],
                createMixedArtifact: false);

            Assert.NotNull(artifact);
            var decoded = ReadCanonicalWave(artifact!.Path);
            Assert.InRange(CalculateToneMagnitude(decoded.MonoSamples, decoded.SampleRate, 620), 0.25, 0.55);
            Assert.InRange(decoded.Duration.TotalMilliseconds, 390, 420);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncKeepsHealthySourceWhenAnotherSourceIsUnreadable()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempRoot, "output.wav");
            var micPath = Path.Combine(tempRoot, "mic.wav");
            await File.WriteAllBytesAsync(outputPath, [1, 2, 3, 4]);
            WriteToneWave(micPath, sampleRate: 48_000, channels: 1, frequencyHz: 520, amplitude: 0.4f, duration: TimeSpan.FromMilliseconds(250));

            var artifact = await PrepareAsync(
                tempRoot,
                [
                    CreateArtifact(AudioCaptureArtifactKind.Output, outputPath),
                    CreateArtifact(AudioCaptureArtifactKind.Microphone, micPath)
                ]);

            Assert.NotNull(artifact);
            var decoded = ReadCanonicalWave(artifact!.Path);
            Assert.InRange(CalculateToneMagnitude(decoded.MonoSamples, decoded.SampleRate, 520), 0.25, 0.50);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncAppliesHeadroomAndLimitsOverlappingFullScaleSources()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempRoot, "output.wav");
            var micPath = Path.Combine(tempRoot, "mic.wav");
            WriteConstantWave(outputPath, sampleRate: 48_000, channels: 2, value: 1f, duration: TimeSpan.FromMilliseconds(100));
            WriteConstantWave(micPath, sampleRate: 48_000, channels: 1, value: 1f, duration: TimeSpan.FromMilliseconds(100));

            var artifact = await PrepareAsync(
                tempRoot,
                [
                    CreateArtifact(AudioCaptureArtifactKind.Output, outputPath),
                    CreateArtifact(AudioCaptureArtifactKind.Microphone, micPath)
                ]);

            var decoded = ReadCanonicalWave(artifact!.Path);
            var peak = decoded.MonoSamples.Max(Math.Abs);
            Assert.InRange(peak, 0.948, 0.952);
            Assert.All(decoded.MonoSamples, sample => Assert.InRange(sample, -0.952f, 0.952f));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncPreservesRelativeStartOffsetAsLeadingSilence()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempRoot, "output.wav");
            WriteToneWave(outputPath, sampleRate: 48_000, channels: 2, frequencyHz: 1_000, amplitude: 0.5f, duration: TimeSpan.FromMilliseconds(200));
            var source = CreateArtifact(AudioCaptureArtifactKind.Output, outputPath) with
            {
                RelativeStartOffset = TimeSpan.FromMilliseconds(250)
            };

            var artifact = await PrepareAsync(
                tempRoot,
                [source],
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")]);

            var decoded = ReadCanonicalWave(artifact!.Path);
            var leadingSilence = decoded.MonoSamples.Take((int)(decoded.SampleRate * 0.20)).ToArray();
            var activeSignal = decoded.MonoSamples
                .Skip((int)(decoded.SampleRate * 0.28))
                .Take((int)(decoded.SampleRate * 0.10))
                .ToArray();
            Assert.True(CalculateRms(leadingSilence) < 0.0001);
            Assert.True(CalculateRms(activeSignal) > 0.20);
            Assert.InRange(decoded.Duration.TotalMilliseconds, 440, 470);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncKeepsSilenceGapBetweenSameSourceTimelineSegments()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var firstPath = Path.Combine(tempRoot, "output-1.wav");
            var secondPath = Path.Combine(tempRoot, "output-2.wav");
            WriteToneWave(firstPath, sampleRate: 48_000, channels: 2, frequencyHz: 700, amplitude: 0.5f, duration: TimeSpan.FromMilliseconds(100));
            WriteToneWave(secondPath, sampleRate: 48_000, channels: 2, frequencyHz: 700, amplitude: 0.5f, duration: TimeSpan.FromMilliseconds(100));

            var first = CreateArtifact(AudioCaptureArtifactKind.Output, firstPath);
            var second = CreateArtifact(AudioCaptureArtifactKind.Output, secondPath) with
            {
                RelativeStartOffset = TimeSpan.FromMilliseconds(300)
            };
            var artifact = await PrepareAsync(
                tempRoot,
                [first, second],
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")]);

            var decoded = ReadCanonicalWave(artifact!.Path);
            var middleGap = decoded.MonoSamples
                .Skip((int)(decoded.SampleRate * 0.15))
                .Take((int)(decoded.SampleRate * 0.10))
                .ToArray();
            var secondSegment = decoded.MonoSamples
                .Skip((int)(decoded.SampleRate * 0.32))
                .Take((int)(decoded.SampleRate * 0.05))
                .ToArray();
            Assert.True(CalculateRms(middleGap) < 0.0001);
            Assert.True(CalculateRms(secondSegment) > 0.20);
            Assert.InRange(decoded.Duration.TotalMilliseconds, 390, 420);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsyncRepairsStaleCaptureHeaderBeforeReadingRecoverySource()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempRoot, "output.wav");
            WriteToneWave(
                outputPath,
                sampleRate: 48_000,
                channels: 2,
                frequencyHz: 700,
                amplitude: 0.5f,
                duration: TimeSpan.FromMilliseconds(400));
            SetDeclaredWaveDataLength(outputPath, declaredDataLength: 48_000 * 2 * 2 / 20);

            var artifact = await PrepareAsync(
                tempRoot,
                [CreateArtifact(AudioCaptureArtifactKind.Output, outputPath)],
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")]);

            Assert.NotNull(artifact);
            var decoded = ReadCanonicalWave(artifact!.Path);
            Assert.InRange(decoded.Duration.TotalMilliseconds, 390, 420);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<AudioCaptureArtifact?> PrepareAsync(
        string tempRoot,
        IReadOnlyList<AudioCaptureArtifact> artifacts,
        IReadOnlyList<AudioCaptureSourceRequest>? sources = null,
        bool createMixedArtifact = true)
    {
        var builder = new AudioMixArtifactBuilder();
        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Manual,
            sources ??
            [
                AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                AudioCaptureSourceRequest.Microphone("mic-1")
            ],
            tempRoot,
            PrebufferSeconds: 0,
            CreateMixedArtifact: createMixedArtifact);

        return await builder.PrepareAsync(request, artifacts, CancellationToken.None);
    }

    private static AudioCaptureArtifact CreateArtifact(AudioCaptureArtifactKind kind, string path) =>
        new(kind, path, new FileInfo(path).Length, DateTimeOffset.UtcNow);

    private static void WriteToneWave(
        string path,
        int sampleRate,
        int channels,
        double frequencyHz,
        float amplitude,
        TimeSpan duration)
    {
        WriteWave(path, sampleRate, channels, duration, frame =>
            amplitude * MathF.Sin((float)(2 * Math.PI * frequencyHz * frame / sampleRate)));
    }

    private static void WriteConstantWave(
        string path,
        int sampleRate,
        int channels,
        float value,
        TimeSpan duration) =>
        WriteWave(path, sampleRate, channels, duration, _ => value);

    private static void WriteWave(
        string path,
        int sampleRate,
        int channels,
        TimeSpan duration,
        Func<int, float> sampleFactory)
    {
        var frames = (int)Math.Round(duration.TotalSeconds * sampleRate);
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = sampleFactory(frame);
            for (var channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = sample;
            }
        }

        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static DecodedWave ReadCanonicalWave(string path)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(AudioMixArtifactBuilder.CanonicalSampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(AudioMixArtifactBuilder.CanonicalBitsPerSample, reader.WaveFormat.BitsPerSample);
        Assert.Equal(AudioMixArtifactBuilder.CanonicalChannels, reader.WaveFormat.Channels);

        var provider = reader.ToSampleProvider();
        var readBuffer = new float[8_192];
        var monoSamples = new List<float>();
        while (true)
        {
            var read = provider.Read(readBuffer, 0, readBuffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index + 1 < read; index += AudioMixArtifactBuilder.CanonicalChannels)
            {
                monoSamples.Add((readBuffer[index] + readBuffer[index + 1]) / 2f);
            }
        }

        return new DecodedWave(
            reader.WaveFormat.SampleRate,
            monoSamples.ToArray(),
            TimeSpan.FromSeconds((double)monoSamples.Count / reader.WaveFormat.SampleRate));
    }

    private static double CalculateToneMagnitude(IReadOnlyList<float> samples, int sampleRate, double frequencyHz)
    {
        var real = 0d;
        var imaginary = 0d;
        for (var index = 0; index < samples.Count; index++)
        {
            var phase = 2 * Math.PI * frequencyHz * index / sampleRate;
            real += samples[index] * Math.Cos(phase);
            imaginary -= samples[index] * Math.Sin(phase);
        }

        return samples.Count == 0
            ? 0
            : 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / samples.Count;
    }

    private static double CalculateRms(IReadOnlyList<float> samples) => samples.Count == 0
        ? 0
        : Math.Sqrt(samples.Sum(sample => sample * sample) / samples.Count);

    private static void SetDeclaredWaveDataLength(string path, int declaredDataLength)
    {
        var bytes = File.ReadAllBytes(path);
        var chunkPosition = 12;
        while (chunkPosition + 8 <= bytes.Length)
        {
            var chunkId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkPosition, 4));
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkPosition + 4, 4));
            if (chunkId == 0x61746164)
            {
                var dataPosition = chunkPosition + 8;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(chunkPosition + 4, 4),
                    checked((uint)declaredDataLength));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(4, 4),
                    checked((uint)(dataPosition + declaredDataLength - 8)));
                File.WriteAllBytes(path, bytes);
                return;
            }

            chunkPosition = checked(chunkPosition + 8 + (int)chunkLength + (int)(chunkLength & 1));
        }

        throw new InvalidDataException("Test WAV has no data chunk.");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "isTranscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record DecodedWave(int SampleRate, float[] MonoSamples, TimeSpan Duration);
}
