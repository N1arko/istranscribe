using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IsTranscribe.Transcription.Worker.Runtime.Native;

internal sealed class NormalizedWaveException : Exception
{
    public NormalizedWaveException(string stableCode)
        : base(stableCode)
    {
        StableCode = stableCode;
    }

    public string StableCode { get; }
}

internal sealed record NormalizedWaveData(
    float[] Samples,
    long DurationMilliseconds);

/// <summary>
/// Strict checkpoint reader for the platform decoder's bounded 16 kHz mono WAV output.
/// MP3 and legacy container decoding remains platform-owned.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
internal static class NormalizedWaveReader
{
    private const int MaximumContainerOverheadBytes = 1024 * 1024;
    private const ushort PcmFormat = 1;
    private const ushort IeeeFloatFormat = 3;

    public static NormalizedWaveData Read(string path) => Read(path, expectedSha256: null);

    public static NormalizedWaveData Read(string path, string? expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new NormalizedWaveException("input_path_not_absolute");
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var maximumLength = checked((long)NativeWhisperAbi.MaximumSamples * sizeof(float)
                                        + MaximumContainerOverheadBytes);
            if (stream.Length < 44 || stream.Length > maximumLength)
            {
                throw new NormalizedWaveException("normalized_wav_size_invalid");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new NormalizedWaveException("input_changed_during_read");
            }
        }
        catch (FileNotFoundException)
        {
            throw new NormalizedWaveException("input_missing");
        }
        catch (NormalizedWaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new NormalizedWaveException("input_path_invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new NormalizedWaveException("input_unreadable");
        }

        if (expectedSha256 is not null)
        {
            var actual = SHA256.HashData(bytes);
            var expected = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new NormalizedWaveException("input_hash_mismatch");
            }
        }

        return Parse(bytes);
    }

    internal static NormalizedWaveData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
        {
            throw new NormalizedWaveException("normalized_wav_header_invalid");
        }

        WaveFormat? format = null;
        ReadOnlySpan<byte> data = default;
        var hasData = false;
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
            {
                throw new NormalizedWaveException("normalized_wav_chunk_invalid");
            }

            var chunkId = bytes.Slice(offset, 4);
            var rawChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var contentOffset = offset + 8;
            if (rawChunkLength > int.MaxValue
                || rawChunkLength > bytes.Length - contentOffset)
            {
                throw new NormalizedWaveException("normalized_wav_chunk_invalid");
            }

            var chunkLength = (int)rawChunkLength;
            var contentEnd = contentOffset + chunkLength;
            var content = bytes.Slice(contentOffset, chunkLength);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (format is not null)
                {
                    throw new NormalizedWaveException("normalized_wav_format_duplicate");
                }

                format = ParseFormat(content);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (hasData)
                {
                    throw new NormalizedWaveException("normalized_wav_data_duplicate");
                }

                data = content;
                hasData = true;
            }

            if ((chunkLength & 1) != 0)
            {
                if (contentEnd == bytes.Length)
                {
                    throw new NormalizedWaveException("normalized_wav_chunk_invalid");
                }

                contentEnd++;
            }

            offset = contentEnd;
        }

        if (format is null || !hasData || data.Length == 0)
        {
            throw new NormalizedWaveException("normalized_wav_chunks_missing");
        }

        var resolvedFormat = format.Value;
        if (data.Length % resolvedFormat.BlockAlign != 0)
        {
            throw new NormalizedWaveException("normalized_wav_data_invalid");
        }

        var sampleCount = data.Length / resolvedFormat.BlockAlign;
        if (sampleCount == 0 || checked((ulong)sampleCount) > NativeWhisperAbi.MaximumSamples)
        {
            throw new NormalizedWaveException("normalized_wav_duration_invalid");
        }

        var samples = new float[sampleCount];
        if (resolvedFormat.Encoding == PcmFormat)
        {
            switch (resolvedFormat.BitsPerSample)
            {
                case 16:
                    for (var index = 0; index < samples.Length; index++)
                    {
                        samples[index] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(index * 2, 2))
                                         / 32768f;
                    }

                    break;
                case 24:
                    for (var index = 0; index < samples.Length; index++)
                    {
                        var sampleOffset = index * 3;
                        var value = data[sampleOffset]
                                    | data[sampleOffset + 1] << 8
                                    | data[sampleOffset + 2] << 16;
                        if ((value & 0x0080_0000) != 0)
                        {
                            value |= unchecked((int)0xff00_0000);
                        }

                        samples[index] = value / 8388608f;
                    }

                    break;
                case 32:
                    for (var index = 0; index < samples.Length; index++)
                    {
                        samples[index] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index * 4, 4))
                                         / 2147483648f;
                    }

                    break;
            }
        }
        else
        {
            for (var index = 0; index < samples.Length; index++)
            {
                var bits = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index * 4, 4));
                var sample = BitConverter.Int32BitsToSingle(bits);
                if (!float.IsFinite(sample) || sample is < -1f or > 1f)
                {
                    throw new NormalizedWaveException("normalized_wav_samples_invalid");
                }

                samples[index] = sample;
            }
        }

        var duration = checked(((long)sampleCount * 1000 + NativeWhisperAbi.SampleRate - 1)
                               / NativeWhisperAbi.SampleRate);
        return new NormalizedWaveData(samples, duration);
    }

    private static WaveFormat ParseFormat(ReadOnlySpan<byte> content)
    {
        if (content.Length < 16)
        {
            throw new NormalizedWaveException("normalized_wav_format_invalid");
        }

        var encoding = BinaryPrimitives.ReadUInt16LittleEndian(content[..2]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(2, 2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(4, 4));
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(8, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(12, 2));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(14, 2));
        var supportedBits = encoding == PcmFormat
            ? bitsPerSample is 16 or 24 or 32
            : bitsPerSample == 32;
        var expectedBlockAlign = bitsPerSample / 8;

        if (encoding is not (PcmFormat or IeeeFloatFormat)
            || !supportedBits
            || channels != 1
            || sampleRate != NativeWhisperAbi.SampleRate
            || blockAlign != expectedBlockAlign
            || byteRate != sampleRate * blockAlign)
        {
            throw new NormalizedWaveException("normalized_wav_format_unsupported");
        }

        return new WaveFormat(encoding, blockAlign, bitsPerSample);
    }

    private readonly record struct WaveFormat(
        ushort Encoding,
        ushort BlockAlign,
        ushort BitsPerSample);
}
