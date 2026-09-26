using System.Buffers.Binary;

namespace IsTranscribe.Host.Audio.Capture;

/// <summary>
/// Repairs an app-owned capture WAV whose checkpointed RIFF lengths lag behind durable PCM bytes.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal static class WaveArtifactRepair
{
    private const uint RiffChunkId = 0x46464952;
    private const uint WaveFormatId = 0x45564157;
    private const uint FormatChunkId = 0x20746D66;
    private const uint DataChunkId = 0x61746164;
    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int MinimumFormatChunkLength = 16;

    public static bool TryRepair(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.WriteThrough);

        if (stream.Length < RiffHeaderLength || stream.Length - 8 > uint.MaxValue)
        {
            return false;
        }

        Span<byte> riffHeader = stackalloc byte[RiffHeaderLength];
        stream.ReadExactly(riffHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(riffHeader) != RiffChunkId
            || BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[8..]) != WaveFormatId)
        {
            return false;
        }

        var declaredRiffLength = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[4..]);
        ushort blockAlign = 0;
        long chunkPosition = RiffHeaderLength;
        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderLength];
        Span<byte> formatHeader = stackalloc byte[MinimumFormatChunkLength];

        while (chunkPosition + ChunkHeaderLength <= stream.Length)
        {
            stream.Position = chunkPosition;
            stream.ReadExactly(chunkHeader);
            var chunkId = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var declaredChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var chunkDataPosition = chunkPosition + ChunkHeaderLength;

            if (chunkId == FormatChunkId)
            {
                if (declaredChunkLength < MinimumFormatChunkLength
                    || chunkDataPosition + MinimumFormatChunkLength > stream.Length)
                {
                    return false;
                }

                stream.Position = chunkDataPosition;
                stream.ReadExactly(formatHeader);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatHeader[12..]);
                if (blockAlign == 0)
                {
                    return false;
                }
            }
            else if (chunkId == DataChunkId)
            {
                if (blockAlign == 0)
                {
                    return false;
                }

                return RepairDataChunk(
                    stream,
                    declaredRiffLength,
                    chunkPosition,
                    chunkDataPosition,
                    declaredChunkLength,
                    blockAlign);
            }

            var paddedChunkLength = checked((long)declaredChunkLength + (declaredChunkLength & 1));
            var nextChunkPosition = chunkDataPosition + paddedChunkLength;
            if (nextChunkPosition <= chunkPosition || nextChunkPosition > stream.Length)
            {
                return false;
            }

            chunkPosition = nextChunkPosition;
        }

        return false;
    }

    private static bool RepairDataChunk(
        FileStream stream,
        uint declaredRiffLength,
        long dataChunkPosition,
        long dataPosition,
        uint declaredDataLength,
        ushort blockAlign)
    {
        var physicalDataLength = stream.Length - dataPosition;
        if (physicalDataLength < 0)
        {
            return false;
        }

        var declaredFileLength = (long)declaredRiffLength + 8;
        var declaredPaddedDataLength = (long)declaredDataLength + (declaredDataLength & 1);
        if (declaredFileLength == stream.Length
            && declaredPaddedDataLength == physicalDataLength)
        {
            return true;
        }

        var repairedDataLength = physicalDataLength - (physicalDataLength % blockAlign);
        var repairedFileLength = dataPosition + repairedDataLength;
        var repairedRiffLength = repairedFileLength - 8;
        if (repairedDataLength > uint.MaxValue || repairedRiffLength > uint.MaxValue)
        {
            return false;
        }

        if (stream.Length != repairedFileLength)
        {
            stream.SetLength(repairedFileLength);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)repairedDataLength);
        stream.Position = dataChunkPosition + sizeof(uint);
        stream.Write(lengthBytes);

        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)repairedRiffLength);
        stream.Position = sizeof(uint);
        stream.Write(lengthBytes);
        stream.Flush(flushToDisk: true);
        return true;
    }
}
