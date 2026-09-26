using IsTranscribe.Host.Audio.Capture;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class WaveArtifactConcatenatorTests
{
    [Fact]
    public void ConcatenateAppendsWaveDataIntoSingleArtifact()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var first = Path.Combine(tempRoot, "first.wav");
            var second = Path.Combine(tempRoot, "second.wav");
            var destination = Path.Combine(tempRoot, "merged.wav");
            WriteWave(first, [1, 2, 3, 4]);
            WriteWave(second, [5, 6, 7, 8]);

            WaveArtifactConcatenator.Concatenate([first, second], destination);

            using var reader = new WaveFileReader(destination);
            Assert.NotEqual(0, new FileInfo(destination).Length);
            Assert.Equal(8, reader.Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void WriteWave(string path, byte[] payload)
    {
        var format = new WaveFormat(8000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(payload, 0, payload.Length);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "isTranscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
