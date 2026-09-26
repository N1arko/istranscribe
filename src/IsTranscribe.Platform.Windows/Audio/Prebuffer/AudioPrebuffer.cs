using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Prebuffer;

public sealed class AudioPrebuffer
{
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private int _writePosition;
    private int _bufferedBytes;

    public AudioPrebuffer(WaveFormat waveFormat, int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        CapacityBytes = checked(waveFormat.AverageBytesPerSecond * durationSeconds);
        _buffer = new byte[CapacityBytes];
    }

    public WaveFormat WaveFormat { get; }

    public int CapacityBytes { get; }

    public void Append(ReadOnlySpan<byte> audioBytes)
    {
        lock (_gate)
        {
            foreach (var value in audioBytes)
            {
                _buffer[_writePosition] = value;
                _writePosition = (_writePosition + 1) % _buffer.Length;
                _bufferedBytes = Math.Min(_bufferedBytes + 1, _buffer.Length);
            }
        }
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#prebuffer.promotion
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            if (_bufferedBytes == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[_bufferedBytes];
            var start = (_writePosition - _bufferedBytes + _buffer.Length) % _buffer.Length;

            if (start + _bufferedBytes <= _buffer.Length)
            {
                Array.Copy(_buffer, start, result, 0, _bufferedBytes);
                return result;
            }

            var firstLength = _buffer.Length - start;
            Array.Copy(_buffer, start, result, 0, firstLength);
            Array.Copy(_buffer, 0, result, firstLength, _bufferedBytes - firstLength);
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _writePosition = 0;
            _bufferedBytes = 0;
        }
    }
}
