using System.Buffers.Binary;
using NAudio.Wave;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Converts a WASAPI packet to mono floats without retaining the source byte buffer.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal static class WindowsAudioSampleDecoder
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    public static float[] DecodeMono(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);
        if (bytesRecorded < 0 || bytesRecorded > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesRecorded));
        }

        var bytesPerSample = format.BitsPerSample / 8;
        var blockAlign = bytesPerSample * format.Channels;
        if (bytesPerSample <= 0 || format.Channels <= 0 || blockAlign <= 0)
        {
            throw new NotSupportedException($"Invalid WASAPI format: {format}.");
        }

        var encoding = ResolveEncoding(format);
        var frameCount = bytesRecorded / blockAlign;
        var result = new float[frameCount];
        var source = buffer.AsSpan(0, frameCount * blockAlign);
        for (var frame = 0; frame < frameCount; frame++)
        {
            double mixed = 0;
            var frameOffset = frame * blockAlign;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = frameOffset + (channel * bytesPerSample);
                mixed += DecodeSample(source.Slice(sampleOffset, bytesPerSample), encoding, format.BitsPerSample);
            }

            result[frame] = (float)Math.Clamp(mixed / format.Channels, -1, 1);
        }

        return result;
    }

    private static WaveFormatEncoding ResolveEncoding(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
        {
            return format.Encoding;
        }

        if (format is not WaveFormatExtensible extensible)
        {
            throw new NotSupportedException($"Unsupported extensible WASAPI format: {format}.");
        }

        if (extensible.SubFormat == IeeeFloatSubFormat)
        {
            return WaveFormatEncoding.IeeeFloat;
        }

        if (extensible.SubFormat == PcmSubFormat)
        {
            return WaveFormatEncoding.Pcm;
        }

        throw new NotSupportedException($"Unsupported WASAPI sub-format: {extensible.SubFormat}.");
    }

    private static float DecodeSample(
        ReadOnlySpan<byte> source,
        WaveFormatEncoding encoding,
        int bitsPerSample)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bitsPerSample == 32)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
        }

        if (encoding != WaveFormatEncoding.Pcm)
        {
            throw new NotSupportedException($"Unsupported WASAPI encoding: {encoding}/{bitsPerSample}.");
        }

        return bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(source) / 32768f,
            24 => DecodePcm24(source) / 8_388_608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(source) / 2_147_483_648f,
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}.")
        };
    }

    private static int DecodePcm24(ReadOnlySpan<byte> source)
    {
        var value = source[0] | (source[1] << 8) | (source[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }
}
