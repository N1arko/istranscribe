using System.Buffers.Binary;
using IsTranscribe.Host.Audio.Prebuffer;
using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Capture;

internal sealed class BufferedWaveArtifactWriter : IDisposable
{
    private const int SilenceWriteBufferBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly AudioCaptureArtifactKind _artifactKind;
    private readonly string _artifactPath;
    private readonly WaveFormat _waveFormat;
    private readonly AudioPrebuffer? _prebuffer;
    private readonly long _checkpointIntervalBytes;
    private readonly Action<FileStream> _flushToDisk;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _silenceBuffer;
    private WaveFileWriter? _writer;
    private FileStream? _artifactStream;
    private long _dataStartPosition;
    private long _bytesWritten;
    private long _lastCheckpointBytes;
    private long _prebufferBytesReceived;
    private TimeSpan _discardedPrebufferDuration;
    private bool _disposed;
    private bool _paused;
    private bool _persisting;
    private long? _pauseStartedTimestamp;

    public BufferedWaveArtifactWriter(
        AudioCaptureArtifactKind artifactKind,
        string artifactPath,
        WaveFormat waveFormat,
        int prebufferSeconds)
        : this(
            artifactKind,
            artifactPath,
            waveFormat,
            prebufferSeconds,
            static stream => stream.Flush(flushToDisk: true),
            TimeProvider.System)
    {
    }

    internal BufferedWaveArtifactWriter(
        AudioCaptureArtifactKind artifactKind,
        string artifactPath,
        WaveFormat waveFormat,
        int prebufferSeconds,
        Action<FileStream> flushToDisk,
        TimeProvider? timeProvider = null)
    {
        _artifactKind = artifactKind;
        _artifactPath = artifactPath;
        _waveFormat = waveFormat;
        _prebuffer = prebufferSeconds > 0 ? new AudioPrebuffer(waveFormat, prebufferSeconds) : null;
        _checkpointIntervalBytes = Math.Max(waveFormat.BlockAlign, waveFormat.AverageBytesPerSecond * 2L);
        _flushToDisk = flushToDisk ?? throw new ArgumentNullException(nameof(flushToDisk));
        _timeProvider = timeProvider ?? TimeProvider.System;
        var silenceBufferLength = Math.Max(
            waveFormat.BlockAlign,
            SilenceWriteBufferBytes - (SilenceWriteBufferBytes % waveFormat.BlockAlign));
        _silenceBuffer = new byte[silenceBufferLength];
    }

    public bool IsPersisting
    {
        get
        {
            lock (_gate)
            {
                return _persisting;
            }
        }
    }

    public void Append(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_paused)
            {
                return;
            }

            if (_persisting)
            {
                _writer!.Write(buffer, 0, bytesRecorded);
                _bytesWritten += bytesRecorded;
                CheckpointHeaderIfNeeded();
                return;
            }

            if (_prebuffer is not null)
            {
                _prebuffer.Append(buffer.AsSpan(0, bytesRecorded));
                _prebufferBytesReceived += bytesRecorded;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_paused == paused)
            {
                return;
            }

            if (paused)
            {
                _paused = true;
                _pauseStartedTimestamp = _timeProvider.GetTimestamp();
                return;
            }

            ResumeFromPause(_timeProvider.GetTimestamp());
        }
    }

    public void AppendSilence(int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        Append(new byte[bytesRecorded], bytesRecorded);
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#prebuffer.promotion
    public void Promote()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_persisting)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_artifactPath)!);
            _artifactStream = new FileStream(
                _artifactPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            _writer = new WaveFileWriter(_artifactStream, _waveFormat);
            _dataStartPosition = _artifactStream.Position;
            _persisting = true;
            if (_paused)
            {
                _pauseStartedTimestamp = _timeProvider.GetTimestamp();
            }

            var prebufferSnapshot = _prebuffer?.Snapshot() ?? Array.Empty<byte>();
            var discardedPrebufferBytes = Math.Max(0, _prebufferBytesReceived - prebufferSnapshot.LongLength);
            _discardedPrebufferDuration = TimeSpan.FromSeconds(
                (double)discardedPrebufferBytes / _waveFormat.AverageBytesPerSecond);
            if (prebufferSnapshot.Length > 0)
            {
                _writer.Write(prebufferSnapshot, 0, prebufferSnapshot.Length);
                _bytesWritten += prebufferSnapshot.Length;
                _prebuffer?.Clear();
            }

            CheckpointHeaderIfNeeded(force: true);
        }
    }

    public AudioCaptureArtifact? Complete(TimeSpan relativeStartOffset = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_paused)
            {
                ResumeFromPause(_timeProvider.GetTimestamp());
            }

            try
            {
                CheckpointHeaderIfNeeded(force: true);
            }
            finally
            {
                var writer = _writer;
                var artifactStream = _artifactStream;
                _writer = null;
                _artifactStream = null;
                try
                {
                    writer?.Dispose();
                }
                finally
                {
                    artifactStream?.Dispose();
                }
            }

            return _persisting && File.Exists(_artifactPath)
                ? new AudioCaptureArtifact(
                    _artifactKind,
                    _artifactPath,
                    _bytesWritten,
                    _timeProvider.GetUtcNow(),
                    relativeStartOffset + _discardedPrebufferDuration)
                : null;
        }
    }

    public void Discard()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer?.Dispose();
            _writer = null;
            _artifactStream = null;
            _prebuffer?.Clear();
            _paused = false;
            _pauseStartedTimestamp = null;

            if (File.Exists(_artifactPath))
            {
                File.Delete(_artifactPath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();
        _writer = null;
        _artifactStream = null;
        _prebuffer?.Clear();
        _paused = false;
        _pauseStartedTimestamp = null;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private void CheckpointHeaderIfNeeded(bool force = false)
    {
        if (_writer is null || _artifactStream is null || _dataStartPosition < 8)
        {
            return;
        }

        if (!force
            && (_bytesWritten == 0
                || (_lastCheckpointBytes > 0
                    && _bytesWritten - _lastCheckpointBytes < _checkpointIntervalBytes)))
        {
            return;
        }

        _writer.Flush();
        var currentPosition = _artifactStream.Position;
        var riffLength = checked(_dataStartPosition + _bytesWritten - 8);
        if (riffLength > uint.MaxValue || _bytesWritten > uint.MaxValue)
        {
            _flushToDisk(_artifactStream);
            _lastCheckpointBytes = _bytesWritten;
            return;
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)riffLength);
        _artifactStream.Position = 4;
        _artifactStream.Write(lengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)_bytesWritten);
        _artifactStream.Position = _dataStartPosition - sizeof(uint);
        _artifactStream.Write(lengthBytes);
        _artifactStream.Position = currentPosition;
        _flushToDisk(_artifactStream);
        _lastCheckpointBytes = _bytesWritten;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
    private void ResumeFromPause(long resumedTimestamp)
    {
        var pauseStartedTimestamp = _pauseStartedTimestamp;
        _paused = false;
        _pauseStartedTimestamp = null;
        if (!_persisting || _writer is null || pauseStartedTimestamp is null)
        {
            return;
        }

        var pausedDuration = _timeProvider.GetElapsedTime(
            pauseStartedTimestamp.Value,
            resumedTimestamp);
        if (pausedDuration <= TimeSpan.Zero)
        {
            return;
        }

        var frameCount = checked(
            pausedDuration.Ticks * (long)_waveFormat.SampleRate / TimeSpan.TicksPerSecond);
        var remainingBytes = checked(frameCount * _waveFormat.BlockAlign);
        while (remainingBytes > 0)
        {
            var bytesToWrite = (int)Math.Min(remainingBytes, _silenceBuffer.Length);
            _writer.Write(_silenceBuffer, 0, bytesToWrite);
            _bytesWritten += bytesToWrite;
            remainingBytes -= bytesToWrite;
        }

        CheckpointHeaderIfNeeded();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
