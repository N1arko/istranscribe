using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using IsTranscribe.Application.Runtime;

namespace IsTranscribe.Desktop.Services;

/// <summary>
/// Produces a compact code-native tray glyph with a state-colored status dot.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// </remarks>
internal static class TrayStateIconFactory
{
    private const int Size = 32;

    public static Stream CreatePng(ApplicationActivityState state)
    {
        var pixels = new byte[Size * Size * 4];
        DrawRoundedSquare(pixels, new Rgba(30, 38, 35, 255));
        DrawWaveform(pixels, new Rgba(246, 248, 247, 255));
        DrawStatusDot(pixels, StatusColor(state));

        return EncodePng(pixels);
    }

    private static void DrawRoundedSquare(byte[] pixels, Rgba color)
    {
        const int inset = 2;
        const int radius = 7;
        for (var y = inset; y < Size - inset; y++)
        {
            for (var x = inset; x < Size - inset; x++)
            {
                var cornerX = x < inset + radius
                    ? inset + radius - x
                    : x >= Size - inset - radius ? x - (Size - inset - radius - 1) : 0;
                var cornerY = y < inset + radius
                    ? inset + radius - y
                    : y >= Size - inset - radius ? y - (Size - inset - radius - 1) : 0;
                if (cornerX * cornerX + cornerY * cornerY <= radius * radius)
                {
                    SetPixel(pixels, x, y, color);
                }
            }
        }
    }

    private static void DrawWaveform(byte[] pixels, Rgba color)
    {
        DrawCapsule(pixels, centerX: 10, top: 12, bottom: 19, color);
        DrawCapsule(pixels, centerX: 15, top: 8, bottom: 23, color);
        DrawCapsule(pixels, centerX: 20, top: 11, bottom: 20, color);
    }

    private static void DrawCapsule(byte[] pixels, int centerX, int top, int bottom, Rgba color)
    {
        for (var y = top; y <= bottom; y++)
        {
            for (var x = centerX - 1; x <= centerX + 1; x++)
            {
                SetPixel(pixels, x, y, color);
            }
        }
    }

    private static void DrawStatusDot(byte[] pixels, Rgba color)
    {
        for (var y = 19; y <= 30; y++)
        {
            for (var x = 19; x <= 30; x++)
            {
                var distanceSquared = (x - 25) * (x - 25) + (y - 25) * (y - 25);
                if (distanceSquared <= 30)
                {
                    SetPixel(pixels, x, y, new Rgba(246, 248, 247, 255));
                }

                if (distanceSquared <= 14)
                {
                    SetPixel(pixels, x, y, color);
                }
            }
        }
    }

    private static Rgba StatusColor(ApplicationActivityState state) => state switch
    {
        ApplicationActivityState.Recording => new Rgba(226, 79, 79, 255),
        ApplicationActivityState.Processing => new Rgba(104, 123, 209, 255),
        ApplicationActivityState.Suspected or ApplicationActivityState.AwaitingConfirmation =>
            new Rgba(207, 151, 49, 255),
        ApplicationActivityState.AttentionRequired => new Rgba(222, 106, 67, 255),
        ApplicationActivityState.Paused => new Rgba(128, 137, 132, 255),
        _ => new Rgba(53, 161, 115, 255)
    };

    private static void SetPixel(byte[] pixels, int x, int y, Rgba color)
    {
        var index = (y * Size + x) * 4;
        pixels[index] = color.Red;
        pixels[index + 1] = color.Green;
        pixels[index + 2] = color.Blue;
        pixels[index + 3] = color.Alpha;
    }

    private static Stream EncodePng(byte[] pixels)
    {
        var result = new MemoryStream();
        result.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Size);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Size);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(result, "IHDR", header);

        var scanlines = new byte[Size * (1 + Size * 4)];
        for (var y = 0; y < Size; y++)
        {
            var destination = y * (1 + Size * 4);
            scanlines[destination] = 0;
            Buffer.BlockCopy(pixels, y * Size * 4, scanlines, destination + 1, Size * 4);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        WriteChunk(result, "IDAT", compressed.ToArray());
        WriteChunk(result, "IEND", []);
        result.Position = 0;
        return result;
    }

    private static void WriteChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        var checksumInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(checksumInput, 0);
        data.CopyTo(checksumInput.AsSpan(typeBytes.Length));
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, CalculateCrc32(checksumInput));
        target.Write(checksum);
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha);
}
