using System.Buffers.Binary;
using IsTranscribe.Host.Audio.Capture;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// </summary>
public sealed class WaveArtifactRepairTests
{
    [Fact]
    public void TryRepairRestoresStaleRiffLengthsAndDropsIncompleteFrameTail()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempRoot, "output.wav");
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var writer = new WaveFileWriter(path, new WaveFormat(8_000, 16, 1)))
            {
                writer.Write(payload, 0, payload.Length);
            }

            var dataChunkPosition = FindDataChunkPosition(path);
            var dataPosition = dataChunkPosition + 8;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                WriteUInt32(stream, dataChunkPosition + 4, 2);
                WriteUInt32(stream, 4, checked((uint)(dataPosition + 2 - 8)));
                stream.Position = stream.Length;
                stream.WriteByte(0x7f);
                stream.Flush(flushToDisk: true);
            }

            Assert.True(WaveArtifactRepair.TryRepair(path));

            using var reader = new WaveFileReader(path);
            var repairedPayload = new byte[payload.Length];
            Assert.Equal(payload.Length, reader.Read(repairedPayload, 0, repairedPayload.Length));
            Assert.Equal(payload, repairedPayload);
            Assert.Equal(payload.Length, reader.Length);
            Assert.Equal(dataPosition + payload.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryRepairLeavesCoherentWaveUnchanged()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempRoot, "mic.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1)))
            {
                writer.Write([1, 2, 3, 4], 0, 4);
            }

            var before = File.ReadAllBytes(path);

            Assert.True(WaveArtifactRepair.TryRepair(path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryRepairRestoresDataLengthWhenRiffLengthReachedDiskFirst()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempRoot, "output.wav");
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var writer = new WaveFileWriter(path, new WaveFormat(8_000, 16, 1)))
            {
                writer.Write(payload, 0, payload.Length);
            }

            var dataChunkPosition = FindDataChunkPosition(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                WriteUInt32(stream, dataChunkPosition + 4, 2);
                stream.Flush(flushToDisk: true);
            }

            Assert.True(WaveArtifactRepair.TryRepair(path));

            using var reader = new WaveFileReader(path);
            Assert.Equal(payload.Length, reader.Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static long FindDataChunkPosition(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            if (chunkId == 0x61746164)
            {
                return position;
            }

            position = checked(position + 8 + (int)chunkLength + (int)(chunkLength & 1));
        }

        throw new InvalidDataException("Test WAV has no data chunk.");
    }

    private static void WriteUInt32(FileStream stream, long position, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Position = position;
        stream.Write(bytes);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "isTranscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
