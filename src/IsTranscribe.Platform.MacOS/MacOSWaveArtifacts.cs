using System.Buffers.Binary;
using IsTranscribe.Core.Audio;

namespace IsTranscribe.Platform.MacOS;

// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
internal sealed class MacOSWaveSourceWriter : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(short);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _maximumPrebufferBytes;
    private readonly double _hostTicksPerSecond;
    private readonly Queue<byte[]> _prebuffer = new();
    private FileStream? _stream;
    private int _prebufferBytes;
    private long _dataBytes;
    private long _lastCheckpointBytes;
    private bool _paused;
    private bool _resumeWithoutGap;
    private bool _disposed;
    private ulong? _lastHostTime;
    private int _lastTargetFrames;

    public MacOSWaveSourceWriter(string path, int prebufferSeconds, double hostTicksPerSecond = 1_000_000_000)
    {
        _path = path;
        _maximumPrebufferBytes = checked(prebufferSeconds * SampleRate * BytesPerFrame);
        _hostTicksPerSecond = hostTicksPerSecond > 0 ? hostTicksPerSecond : 1_000_000_000;
    }

    public ulong? FirstHostTime { get; private set; }

    public bool IsPersisting
    {
        get
        {
            lock (_gate)
            {
                return _stream is not null;
            }
        }
    }

    public void Append(MacOSPcmFrame frame)
    {
        var pcm = Normalize(frame);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_paused || pcm.Length == 0)
            {
                return;
            }

            FirstHostTime ??= frame.HostTime;
            if (_lastHostTime is ulong previousHostTime && !_resumeWithoutGap && frame.HostTime > previousHostTime)
            {
                var elapsedFrames = checked((long)Math.Round(
                    (frame.HostTime - previousHostTime) * SampleRate / _hostTicksPerSecond));
                var missingFrames = elapsedFrames - _lastTargetFrames;
                if (missingFrames > 2)
                {
                    AppendSilence(missingFrames);
                }
            }

            _resumeWithoutGap = false;
            _lastHostTime = frame.HostTime;
            _lastTargetFrames = pcm.Length / BytesPerFrame;
            AppendBytes(pcm);
        }
    }

    private void AppendBytes(byte[] pcm)
    {
        if (_stream is not null)
        {
            _stream.Write(pcm);
            _dataBytes += pcm.Length;
            if (_dataBytes - _lastCheckpointBytes >= SampleRate * BytesPerFrame * 2L)
            {
                CheckpointHeader();
            }

            return;
        }

        if (_maximumPrebufferBytes == 0)
        {
            return;
        }

        if (pcm.Length > _maximumPrebufferBytes)
        {
            pcm = pcm[^_maximumPrebufferBytes..];
        }

        _prebuffer.Enqueue(pcm);
        _prebufferBytes += pcm.Length;
        while (_prebufferBytes > _maximumPrebufferBytes && _prebuffer.Count > 1)
        {
            _prebufferBytes -= _prebuffer.Dequeue().Length;
        }
    }

    private void AppendSilence(long frames)
    {
        var silence = new byte[4_096 * BytesPerFrame];
        while (frames > 0)
        {
            var frameCount = (int)Math.Min(frames, 4_096);
            AppendBytes(frameCount == 4_096 ? silence : silence[..(frameCount * BytesPerFrame)]);
            frames -= frameCount;
        }
    }

    public void Promote()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream is not null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _stream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                64 * 1024,
                FileOptions.WriteThrough);
            WriteHeader(_stream, 0);
            while (_prebuffer.TryDequeue(out var bytes))
            {
                _stream.Write(bytes);
                _dataBytes += bytes.Length;
            }

            _prebufferBytes = 0;
            CheckpointHeader();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_paused && !paused)
            {
                _resumeWithoutGap = true;
            }

            _paused = paused;
        }
    }

    public MacOSWaveSourceArtifact? Complete(AudioCaptureArtifactKind kind)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream is null)
            {
                return null;
            }

            CheckpointHeader();
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
            return _dataBytes > 0 && FirstHostTime is ulong firstHostTime
                ? new MacOSWaveSourceArtifact(kind, _path, _dataBytes, firstHostTime)
                : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_stream is not null)
            {
                CheckpointHeader();
                _stream.Flush(flushToDisk: true);
                _stream.Dispose();
                _stream = null;
            }

            _prebuffer.Clear();
            _prebufferBytes = 0;
            _disposed = true;
        }
    }

    private static byte[] Normalize(MacOSPcmFrame frame)
    {
        if (frame.FrameCount <= 0 || frame.Channels <= 0 || frame.SampleRate <= 0)
        {
            return [];
        }

        var source = frame.Samples.Span;
        if (source.Length < frame.FrameCount * frame.Channels)
        {
            throw new InvalidDataException("PCM frame contains fewer samples than declared.");
        }

        var targetFrames = checked((int)Math.Round((double)frame.FrameCount * SampleRate / frame.SampleRate));
        var result = new byte[checked(targetFrames * BytesPerFrame)];
        for (var targetFrame = 0; targetFrame < targetFrames; targetFrame++)
        {
            var sourcePosition = (double)targetFrame * frame.SampleRate / SampleRate;
            var first = Math.Min((int)sourcePosition, frame.FrameCount - 1);
            var second = Math.Min(first + 1, frame.FrameCount - 1);
            var fraction = sourcePosition - first;
            for (var channel = 0; channel < Channels; channel++)
            {
                var sourceChannel = Math.Min(channel, frame.Channels - 1);
                var firstSample = source[first * frame.Channels + sourceChannel];
                var secondSample = source[second * frame.Channels + sourceChannel];
                var sample = firstSample + ((secondSample - firstSample) * fraction);
                var pcm16 = (short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(
                    result.AsSpan((targetFrame * BytesPerFrame) + (channel * sizeof(short))),
                    pcm16);
            }
        }

        return result;
    }

    private void CheckpointHeader()
    {
        var position = _stream!.Position;
        _stream.Position = 0;
        WriteHeader(_stream, checked((int)_dataBytes));
        _stream.Position = position;
        _stream.Flush(flushToDisk: true);
        _lastCheckpointBytes = _dataBytes;
    }

    public static bool TryRepair(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length < 44 || stream.Length - 44 > int.MaxValue || (stream.Length - 44) % BytesPerFrame != 0)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[12];
        stream.ReadExactly(signature);
        if (!signature[..4].SequenceEqual("RIFF"u8) || !signature[8..].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        stream.Position = 0;
        WriteHeader(stream, checked((int)stream.Length - 44));
        stream.Flush(flushToDisk: true);
        return true;
    }

    internal static void WriteHeader(Stream stream, int dataBytes)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record MacOSWaveSourceArtifact(
    AudioCaptureArtifactKind Kind,
    string Path,
    long DataBytes,
    ulong FirstHostTime);

// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
internal static class MacOSWaveMixer
{
    private const int BytesPerFrame = 4;
    private const int BlockFrames = 4_096;

    public static void Mix(
        IReadOnlyList<(MacOSWaveSourceArtifact Artifact, long OffsetFrames)> sources,
        string destinationPath)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one readable source is required.", nameof(sources));
        }

        var readers = sources.Select(static source => new SourceReader(source.Artifact.Path, source.OffsetFrames)).ToArray();
        try
        {
            var totalFrames = readers.Max(static reader => reader.OffsetFrames + reader.FrameCount);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            MacOSWaveSourceWriter.WriteHeader(output, 0);
            var mixed = new byte[BlockFrames * BytesPerFrame];
            for (long blockStart = 0; blockStart < totalFrames; blockStart += BlockFrames)
            {
                var frames = checked((int)Math.Min(BlockFrames, totalFrames - blockStart));
                Array.Clear(mixed, 0, frames * BytesPerFrame);
                for (var frame = 0; frame < frames; frame++)
                {
                    var absoluteFrame = blockStart + frame;
                    var left = 0f;
                    var right = 0f;
                    foreach (var reader in readers)
                    {
                        if (reader.TryRead(absoluteFrame, out var sourceLeft, out var sourceRight))
                        {
                            left += sourceLeft;
                            right += sourceRight;
                        }
                    }

                    WriteLimited(mixed, frame * BytesPerFrame, left * 0.8f);
                    WriteLimited(mixed, (frame * BytesPerFrame) + sizeof(short), right * 0.8f);
                }

                output.Write(mixed, 0, frames * BytesPerFrame);
            }

            var dataBytes = checked((int)(totalFrames * BytesPerFrame));
            output.Position = 0;
            MacOSWaveSourceWriter.WriteHeader(output, dataBytes);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void WriteLimited(byte[] destination, int offset, float sample)
    {
        var limited = MathF.Tanh(sample);
        var value = (short)Math.Clamp(MathF.Round(limited * short.MaxValue), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset), value);
    }

    private sealed class SourceReader : IDisposable
    {
        private readonly BinaryReader _reader;

        public SourceReader(string path, long offsetFrames)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new BinaryReader(stream);
            if (new string(_reader.ReadChars(4)) != "RIFF")
            {
                throw new InvalidDataException("Source artifact is not RIFF.");
            }

            stream.Position = 40;
            var dataBytes = _reader.ReadInt32();
            OffsetFrames = offsetFrames;
            FrameCount = dataBytes / BytesPerFrame;
        }

        public long OffsetFrames { get; }
        public long FrameCount { get; }

        public bool TryRead(long absoluteFrame, out float left, out float right)
        {
            var sourceFrame = absoluteFrame - OffsetFrames;
            if (sourceFrame < 0 || sourceFrame >= FrameCount)
            {
                left = 0;
                right = 0;
                return false;
            }

            left = _reader.ReadInt16() / (float)short.MaxValue;
            right = _reader.ReadInt16() / (float)short.MaxValue;
            return true;
        }

        public void Dispose() => _reader.Dispose();
    }
}
