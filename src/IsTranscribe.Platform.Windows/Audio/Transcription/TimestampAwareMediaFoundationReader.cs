// Adapted from NAudio 2.3.0 MediaFoundationReader under the MIT license.
// The seek correction follows the timestamp-aware implementation accepted upstream.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Reads decoded PCM from an already verified stream and corrects Media Foundation's
/// keyframe seek by discarding decoded samples according to their presentation timestamps.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TimestampAwareMediaFoundationReader : WaveStream
{
    private readonly Stream _source;
    private readonly ManagedComStream _comStream;
    private IMFSourceReader? _reader;
    private WaveFormat _waveFormat;
    private readonly long _length;
    private long _position;
    private byte[] _decoderBuffer = [];
    private int _decoderOffset;
    private int _decoderCount;

    internal TimestampAwareMediaFoundationReader(Stream source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The Media Foundation source stream must be readable and seekable.", nameof(source));
        }

        MediaFoundationApi.Startup();
        _comStream = new ManagedComStream(source);
        _reader = CreateReader(_comStream);
        _waveFormat = GetCurrentWaveFormat(_reader);
        _length = GetLength(_reader, _waveFormat);
    }

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Reposition(Math.Min(value, _length));
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested read exceeds the destination buffer.", nameof(count));
        }

        var written = 0;
        if (_decoderCount > 0)
        {
            written += ReadBuffered(buffer, offset, count);
        }

        while (written < count && ReadNextDecoderBuffer(out _))
        {
            written += ReadBuffered(buffer, offset + written, count - written);
        }

        _position = checked(_position + written);
        return written;
    }

    private static IMFSourceReader CreateReader(ManagedComStream source)
    {
        var byteStream = MediaFoundationApi.CreateByteStream(source);
        var reader = MediaFoundationApi.CreateSourceReaderFromByteStream(byteStream);
        try
        {
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);

            reader.GetCurrentMediaType(
                MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                out var nativeMediaType);
            try
            {
                var native = new MediaType(nativeMediaType);
                var requested = new MediaType
                {
                    MajorType = MediaTypes.MFMediaType_Audio,
                    SubType = AudioSubtypes.MFAudioFormat_Float,
                    ChannelCount = native.ChannelCount,
                    SampleRate = native.SampleRate
                };
                try
                {
                    reader.SetCurrentMediaType(
                        MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                        IntPtr.Zero,
                        requested.MediaFoundationObject);
                }
                finally
                {
                    Marshal.ReleaseComObject(requested.MediaFoundationObject);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(nativeMediaType);
            }

            return reader;
        }
        catch
        {
            Marshal.ReleaseComObject(reader);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(byteStream);
        }
    }

    private bool ReadNextDecoderBuffer(out ulong timestamp)
    {
        timestamp = 0;
        var reader = _reader ?? throw new ObjectDisposedException(nameof(TimestampAwareMediaFoundationReader));
        while (true)
        {
            reader.ReadSample(
                MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                0,
                out _,
                out var flags,
                out var sampleTimestamp,
                out var sample);

            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                Release(sample);
                return false;
            }

            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
            {
                _waveFormat = GetCurrentWaveFormat(reader);
            }

            var informational = MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NEWSTREAM
                                | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED
                                | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_STREAMTICK
                                | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ALLEFFECTSREMOVED;
            var unexpected = flags & ~(informational
                                       | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED);
            if (unexpected != 0)
            {
                Release(sample);
                throw new InvalidOperationException($"Media Foundation read failed with flags '{unexpected}'.");
            }

            if (sample is null)
            {
                continue;
            }

            IMFMediaBuffer? mediaBuffer = null;
            var locked = false;
            try
            {
                sample.ConvertToContiguousBuffer(out mediaBuffer);
                mediaBuffer.Lock(out var audioData, out _, out var byteCount);
                locked = true;
                if (_decoderBuffer.Length < byteCount)
                {
                    _decoderBuffer = new byte[byteCount];
                }

                Marshal.Copy(audioData, _decoderBuffer, 0, byteCount);
                _decoderOffset = 0;
                _decoderCount = byteCount;
                timestamp = sampleTimestamp;
                return true;
            }
            finally
            {
                if (locked)
                {
                    mediaBuffer!.Unlock();
                }

                Release(mediaBuffer);
                Release(sample);
            }
        }
    }

    private void Reposition(long desiredPosition)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(TimestampAwareMediaFoundationReader));
        var timestamp = checked(10_000_000L * desiredPosition / _waveFormat.AverageBytesPerSecond);
        var value = PropVariant.FromLong(timestamp);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(value));
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            reader.SetCurrentPosition(Guid.Empty, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        _decoderOffset = 0;
        _decoderCount = 0;
        var achievedPosition = desiredPosition;
        while (ReadNextDecoderBuffer(out var sampleTimestamp))
        {
            var samplePosition = checked((long)(
                sampleTimestamp * (ulong)_waveFormat.AverageBytesPerSecond / 10_000_000UL));
            samplePosition = AlignDown(samplePosition, _waveFormat.BlockAlign);
            var sampleEnd = checked(samplePosition + _decoderCount);
            if (sampleEnd <= desiredPosition)
            {
                _decoderOffset = 0;
                _decoderCount = 0;
                continue;
            }

            if (samplePosition < desiredPosition)
            {
                var skip = checked((int)(desiredPosition - samplePosition));
                skip = checked((int)AlignDown(skip, _waveFormat.BlockAlign));
                _decoderOffset = skip;
                _decoderCount -= skip;
                achievedPosition = checked(samplePosition + skip);
            }
            else
            {
                achievedPosition = samplePosition;
            }

            _position = achievedPosition;
            return;
        }

        _position = _length;
    }

    private int ReadBuffered(byte[] destination, int offset, int count)
    {
        var copied = Math.Min(count, _decoderCount);
        Array.Copy(_decoderBuffer, _decoderOffset, destination, offset, copied);
        _decoderOffset += copied;
        _decoderCount -= copied;
        if (_decoderCount == 0)
        {
            _decoderOffset = 0;
        }

        return copied;
    }

    private static WaveFormat GetCurrentWaveFormat(IMFSourceReader reader)
    {
        reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
            out var mediaTypeObject);
        try
        {
            var mediaType = new MediaType(mediaTypeObject);
            return mediaType.SubType == AudioSubtypes.MFAudioFormat_Float
                ? WaveFormat.CreateIeeeFloatWaveFormat(mediaType.SampleRate, mediaType.ChannelCount)
                : new WaveFormat(mediaType.SampleRate, mediaType.BitsPerSample, mediaType.ChannelCount);
        }
        finally
        {
            Marshal.ReleaseComObject(mediaTypeObject);
        }
    }

    private static long GetLength(IMFSourceReader reader, WaveFormat format)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            var result = reader.GetPresentationAttribute(
                MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE,
                MediaFoundationAttributes.MF_PD_DURATION,
                pointer);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            var duration = Marshal.PtrToStructure<PropVariant>(pointer);
            return checked((long)duration.Value * format.AverageBytesPerSecond / 10_000_000L);
        }
        finally
        {
            PropVariant.Clear(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_reader is not null)
        {
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }

        if (disposing)
        {
            _source.Position = 0;
        }

        base.Dispose(disposing);
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ManagedComStream : IStream
    {
        private readonly Stream _stream;

        internal ManagedComStream(Stream stream) => _stream = stream;

        public void Clone(out IStream ppstm) => ppstm = null!;

        public void Commit(int grfCommitFlags) => _stream.Flush();

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) =>
            throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType) =>
            throw new NotSupportedException();

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            var read = _stream.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbRead, read);
            }
        }

        public void Revert() => throw new NotSupportedException();

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            var position = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(plibNewPosition, position);
            }
        }

        public void SetSize(long libNewSize) => throw new NotSupportedException();

        public void Stat(out STATSTG pstatstg, int grfStatFlag) =>
            pstatstg = new STATSTG
            {
                type = 2,
                cbSize = _stream.Length,
                grfMode = 0
            };

        public void UnlockRegion(long libOffset, long cb, int dwLockType) =>
            throw new NotSupportedException();

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) =>
            throw new NotSupportedException();
    }
}
