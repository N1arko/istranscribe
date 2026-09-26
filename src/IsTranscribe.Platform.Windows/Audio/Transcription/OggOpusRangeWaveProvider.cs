using System.Buffers.Binary;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Provides an exact PCM range from an Ogg/Opus stream using the pinned managed
/// Concentus decoder. The verified source stream remains owned by the caller.
/// </summary>
internal sealed class OggOpusRangeWaveProvider : IWaveProvider, IDisposable
{
    private const int OpusSampleRate = 48_000;
    private const int MaximumPacketFrames = OpusSampleRate * 120 / 1_000;
    private const int PrerollFrames = OpusSampleRate * 2;
    private readonly OpusDecoder _decoder;
    private readonly OpusOggReadStream _reader;
    private readonly Queue<CompressedPacket> _prefetchedPackets = new();
    private readonly int _channels;
    private readonly long _targetFrame;
    private long _nextPacketStartFrame;
    private long _decodedFrames;
    private short[]? _packet;
    private int _packetSampleOffset;

    internal OggOpusRangeWaveProvider(Stream source, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The Ogg/Opus source must be readable and seekable.", nameof(source));
        }

        var header = ReadHeader(source);
        _channels = header.Channels;
        _targetFrame = checked(
            header.PreSkip + (long)Math.Floor(start.TotalSeconds * OpusSampleRate));
        source.Position = 0;
#pragma warning disable CS0618 // Direct construction guarantees the audited managed decoder path.
        _decoder = new OpusDecoder(OpusSampleRate, _channels);
#pragma warning restore CS0618
        _reader = new OpusOggReadStream(_decoder, source);
        if (!_reader.HasNextPacket || _reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"The Ogg/Opus stream is unreadable: {_reader.LastError ?? "no audio packets"}.");
        }

        WaveFormat = new WaveFormat(OpusSampleRate, 16, _channels);
        PrimeDecoder();
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested read exceeds the destination buffer.", nameof(count));
        }

        count -= count % WaveFormat.BlockAlign;
        var written = 0;
        while (written < count)
        {
            if (!EnsurePacket())
            {
                break;
            }

            var packetFrames = _packet!.Length / _channels;
            var packetStartFrame = _decodedFrames;
            var packetEndFrame = checked(packetStartFrame + packetFrames);
            if (_packetSampleOffset == 0 && packetEndFrame <= _targetFrame)
            {
                _decodedFrames = packetEndFrame;
                _packet = null;
                continue;
            }

            if (_packetSampleOffset == 0 && packetStartFrame < _targetFrame)
            {
                _packetSampleOffset = checked((int)(_targetFrame - packetStartFrame) * _channels);
            }

            var availableBytes = checked((_packet.Length - _packetSampleOffset) * sizeof(short));
            var copiedBytes = Math.Min(count - written, availableBytes);
            copiedBytes -= copiedBytes % WaveFormat.BlockAlign;
            Buffer.BlockCopy(_packet, _packetSampleOffset * sizeof(short), buffer, offset + written, copiedBytes);
            var copiedSamples = copiedBytes / sizeof(short);
            _packetSampleOffset += copiedSamples;
            written += copiedBytes;
            if (_packetSampleOffset == _packet.Length)
            {
                _decodedFrames = packetEndFrame;
                _packet = null;
                _packetSampleOffset = 0;
            }
        }

        return written;
    }

    private bool EnsurePacket()
    {
        if (_packet is not null)
        {
            return true;
        }

        CompressedPacket compressedPacket;
        if (_prefetchedPackets.TryDequeue(out var prefetchedPacket))
        {
            compressedPacket = prefetchedPacket;
        }
        else
        {
            var payload = _reader.ReadNextRawPacket();
            if (payload is null)
            {
                if (!string.IsNullOrWhiteSpace(_reader.LastError))
                {
                    throw new InvalidDataException(
                        $"Concentus could not read Ogg/Opus audio: {_reader.LastError}");
                }

                return false;
            }

            compressedPacket = CreatePacket(payload, _nextPacketStartFrame);
            _nextPacketStartFrame = compressedPacket.EndFrame;
        }

        _decodedFrames = compressedPacket.StartFrame;
        var packet = new short[checked(compressedPacket.FrameCount * _channels)];
        var decodedFrameCount = _decoder.Decode(
            compressedPacket.Payload,
            packet,
            compressedPacket.FrameCount,
            false);
        if (decodedFrameCount != compressedPacket.FrameCount)
        {
            throw new InvalidDataException(
                $"Concentus decoded {decodedFrameCount} Opus frames; expected {compressedPacket.FrameCount}.");
        }

        _packet = packet;
        _packetSampleOffset = 0;
        return true;
    }

    private void PrimeDecoder()
    {
        var retainedPackets = new Queue<CompressedPacket>();
        var retainFromFrame = Math.Max(0, _targetFrame - PrerollFrames);
        var nextFrame = 0L;
        while (_reader.HasNextPacket)
        {
            var payload = _reader.ReadNextRawPacket();
            if (payload is null)
            {
                break;
            }

            var packet = CreatePacket(payload, nextFrame);
            retainedPackets.Enqueue(packet);
            nextFrame = packet.EndFrame;
            while (retainedPackets.TryPeek(out var oldest) && oldest.EndFrame <= retainFromFrame)
            {
                retainedPackets.Dequeue();
            }

            if (nextFrame > _targetFrame)
            {
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(_reader.LastError))
        {
            throw new InvalidDataException($"Concentus could not index Ogg/Opus audio: {_reader.LastError}");
        }

        foreach (var packet in retainedPackets)
        {
            _prefetchedPackets.Enqueue(packet);
        }

        _nextPacketStartFrame = nextFrame;
        _decodedFrames = _prefetchedPackets.TryPeek(out var firstPacket)
            ? firstPacket.StartFrame
            : nextFrame;
        _decoder.ResetState();
    }

    private static CompressedPacket CreatePacket(byte[] payload, long startFrame)
    {
        if (payload.Length == 0)
        {
            throw new InvalidDataException("The Ogg stream contains an empty Opus packet.");
        }

        var frameCount = OpusPacketInfo.GetNumSamples(payload, OpusSampleRate);
        if (frameCount is <= 0 or > MaximumPacketFrames)
        {
            throw new InvalidDataException(
                $"The Ogg stream contains an invalid Opus packet duration ({frameCount} frames).");
        }

        return new CompressedPacket(payload, startFrame, frameCount);
    }

    private static OpusHeader ReadHeader(Stream source)
    {
        source.Position = 0;
        var search = new byte[64 * 1024];
        var read = source.Read(search, 0, search.Length);
        var signature = "OpusHead"u8;
        var index = search.AsSpan(0, read).IndexOf(signature);
        if (index < 0 || read - index < 19)
        {
            throw new InvalidDataException("The Ogg source does not contain a complete OpusHead packet.");
        }

        var channels = search[index + 9];
        var mappingFamily = search[index + 18];
        if (channels is < 1 or > 2 || mappingFamily != 0)
        {
            throw new NotSupportedException(
                "The Windows Ogg decoder supports Opus mono/stereo streams with mapping family 0.");
        }

        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(search.AsSpan(index + 10, 2));
        return new OpusHeader(channels, preSkip);
    }

    public void Dispose()
    {
        _reader.Close();
        _decoder.Dispose();
    }

    private readonly record struct OpusHeader(int Channels, ushort PreSkip);

    private readonly record struct CompressedPacket(byte[] Payload, long StartFrame, int FrameCount)
    {
        internal long EndFrame => checked(StartFrame + FrameCount);
    }
}
