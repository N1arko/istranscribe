using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization
/// </remarks>
public sealed class MacOSMp3EncoderTests
{
    [Fact]
    public async Task AudioToolboxProducesTheExactVerifiedMp3Preset()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-mp3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.wav");
            var destination = Path.Combine(directory, "artifact.mp3");
            WriteGoldenWave(source, TimeSpan.FromSeconds(1));
            var encoder = new MacOSMp3Encoder();

            var info = await encoder.EncodeAsync(source, destination, CancellationToken.None);

            Assert.Equal(48_000, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.InRange(info.Bitrate, 126_000, 130_000);
            Assert.InRange(info.Duration.TotalSeconds, 0.95, 1.1);
            Assert.True(info.Bytes > 1_000);
            Assert.Equal(info, encoder.Probe(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingStagedArtifactIsNeverOverwrittenOrDeleted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-mp3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.wav");
            var destination = Path.Combine(directory, "artifact.mp3.partial");
            WriteGoldenWave(source, TimeSpan.FromMilliseconds(100));
            File.WriteAllBytes(destination, [1, 2, 3]);

            await Assert.ThrowsAsync<IOException>(() =>
                new MacOSMp3Encoder().EncodeAsync(source, destination, CancellationToken.None).AsTask());

            Assert.Equal([1, 2, 3], File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Long")]
    public async Task TwoHourSparseWaveEncodesWithBoundedProcessMemory()
    {
        if (!OperatingSystem.IsMacOS()
            || !string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LONG_AUDIO"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-mp3-long-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "two-hours.wav");
            var destination = Path.Combine(directory, "two-hours.mp3.partial");
            WriteSparseWave(source, TimeSpan.FromHours(2));
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var baseline = process.PrivateMemorySize64;
            var peak = baseline;
            var task = new MacOSMp3Encoder().EncodeAsync(source, destination, CancellationToken.None).AsTask();
            while (!task.IsCompleted)
            {
                await Task.Delay(100);
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
            }

            var info = await task;
            Assert.InRange(info.Duration.TotalSeconds, 7_199, 7_201);
            Assert.InRange(peak - baseline, 0, 512L * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteGoldenWave(string path, TimeSpan duration)
    {
        const int sampleRate = 48_000;
        const short channels = 2;
        const short bitsPerSample = 16;
        var frameCount = checked((int)(sampleRate * duration.TotalSeconds));
        var dataBytes = frameCount * channels * (bitsPerSample / 8);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frameCount; frame++)
        {
            writer.Write(ToPcm16(Math.Sin(2 * Math.PI * 440 * frame / sampleRate) * 0.25));
            writer.Write(ToPcm16(Math.Sin(2 * Math.PI * 660 * frame / sampleRate) * 0.25));
        }
    }

    private static short ToPcm16(double sample) => checked((short)Math.Round(sample * short.MaxValue));

    private static void WriteSparseWave(string path, TimeSpan duration)
    {
        const int byteRate = 48_000 * 2 * sizeof(short);
        var dataBytes = checked((uint)(duration.TotalSeconds * byteRate));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(dataBytes + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(48_000);
        writer.Write(byteRate);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Flush();
        stream.SetLength(44L + dataBytes);
        stream.Flush(flushToDisk: true);
    }
}
