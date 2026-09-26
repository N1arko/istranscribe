namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// A timestamped, interleaved IEEE-float PCM frame on the shared Core Audio host timeline.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// </remarks>
public sealed record MacOSPcmFrame(
    DateTimeOffset ObservedAtUtc,
    ulong HostTime,
    int FrameCount,
    int Channels,
    int SampleRate,
    ReadOnlyMemory<float> Samples);

public enum MacOSPcmStreamState
{
    Running,
    Stopped,
    Faulted
}

/// <summary>
/// A bounded, memory-only live source used by detection and the recording adapter.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed class MacOSPcmStream : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<MacOSPcmFrame> _frames = new();
    private readonly int _maximumSampleFrames;
    private readonly IDisposable _nativeCapture;
    private int _bufferedSampleFrames;
    private bool _disposed;

    public MacOSPcmStreamState State { get; private set; } = MacOSPcmStreamState.Running;

    public string? FailureCode { get; private set; }

    internal MacOSPcmStream(int maximumSampleFrames, Func<Action<MacOSPcmFrame>, IDisposable> start)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSampleFrames, 1);
        _maximumSampleFrames = maximumSampleFrames;
        _nativeCapture = start(Append);
    }

    public event EventHandler<MacOSPcmFrame>? FrameReady;

    public int BufferedFrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public bool TryRead(out MacOSPcmFrame? frame)
    {
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Dequeue();
            _bufferedSampleFrames -= frame.FrameCount;
            return true;
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

            _disposed = true;
            if (State == MacOSPcmStreamState.Running)
            {
                State = MacOSPcmStreamState.Stopped;
            }
        }

        _nativeCapture.Dispose();
        lock (_gate)
        {
            _frames.Clear();
            _bufferedSampleFrames = 0;
        }
    }

    internal void Fail(string failureCode)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            State = MacOSPcmStreamState.Faulted;
            FailureCode = failureCode;
        }

        Dispose();
    }

    private void Append(MacOSPcmFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _frames.Enqueue(frame);
            _bufferedSampleFrames += frame.FrameCount;
            while (_bufferedSampleFrames > _maximumSampleFrames && _frames.Count > 1)
            {
                _bufferedSampleFrames -= _frames.Dequeue().FrameCount;
            }
        }

        FrameReady?.Invoke(this, frame);
    }
}
