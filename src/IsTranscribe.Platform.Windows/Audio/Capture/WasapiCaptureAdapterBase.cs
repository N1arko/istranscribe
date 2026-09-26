using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
internal abstract class WasapiCaptureAdapterBase(
    BootstrapFileLogger logger,
    AudioCaptureSourceRequest source,
    string artifactPath,
    int prebufferSeconds) : IAudioCaptureAdapter
{
    private readonly BootstrapFileLogger _logger = logger;
    private readonly AudioCaptureSourceRequest _source = source;
    private readonly string _artifactPath = artifactPath;
    private readonly int _prebufferSeconds = prebufferSeconds;
    private readonly CaptureStopCompletion _stopCompletion = new();
    private readonly CaptureAdapterLifecycleArbiter _lifecycle = new();
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _device;
    private IWaveIn? _capture;
    private BufferedWaveArtifactWriter? _writer;
    private AudioCaptureArtifact? _finalArtifact;
    private AudioCaptureStopEvent? _stopEvent;
    private bool _disposed;
    private bool _faultRaised;
    private TimeSpan _relativeStartOffset;

    public event EventHandler<AudioCaptureFault>? Faulted;

    public event EventHandler<AudioCaptureStopEvent>? Stopped;

    public bool IsActive { get; private set; }

    public AudioCaptureArtifact? FinalArtifact => _finalArtifact;

    public AudioCaptureStopEvent? StopEvent => _stopEvent;

    protected abstract AudioCaptureArtifactKind ArtifactKind { get; }

    protected abstract IWaveIn CreateCapture(MMDevice device);

    public ValueTask StartAsync(
        bool persistImmediately,
        DateTimeOffset sessionTimelineOriginUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsActive)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _device = _deviceEnumerator.GetDevice(_source.DeviceId!);
            _capture = CreateCapture(_device);
            _writer = new BufferedWaveArtifactWriter(ArtifactKind, _artifactPath, _capture.WaveFormat, _prebufferSeconds);
            if (persistImmediately)
            {
                _writer.Promote();
            }

            _capture.DataAvailable += HandleDataAvailable;
            _capture.RecordingStopped += HandleRecordingStopped;
            // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
            _relativeStartOffset = GetRelativeStartOffset(sessionTimelineOriginUtc);
            _capture.StartRecording();
            IsActive = true;
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            _writer?.Dispose();
            _capture?.Dispose();
            _device?.Dispose();
            _deviceEnumerator?.Dispose();
            RaiseFault(AudioCaptureFailureClassifier.Classify(exception), "capture_start_failed", exception.Message);
            throw;
        }
    }

    public ValueTask<AudioCapturePromotionOutcome> PromoteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
        var outcome = AudioCapturePromotionOutcome.SourceUnavailable;
        var accepted = _lifecycle.TryPromote(() =>
        {
            if (_writer is null)
            {
                return;
            }

            if (_writer.IsPersisting)
            {
                outcome = AudioCapturePromotionOutcome.AlreadyPersisting;
                return;
            }

            _writer.Promote();
            outcome = AudioCapturePromotionOutcome.Promoted;
        });
        return ValueTask.FromResult(accepted ? outcome : AudioCapturePromotionOutcome.SourceUnavailable);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer?.SetPaused(true);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer?.SetPaused(false);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AudioCaptureArtifact?> StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null)
        {
            return _finalArtifact;
        }

        _lifecycle.RequestStop(() =>
        {
            if (IsActive)
            {
                _capture!.StopRecording();
            }
            else
            {
                EnsureStopEvent();
                CompleteStop();
            }
        });

        await _stopCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _finalArtifact;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _writer?.Dispose();
        CleanupCapture();
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (_writer is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            _writer.Append(eventArgs.Buffer, eventArgs.BytesRecorded);
        }
        catch (Exception exception)
        {
            RaiseFault(AudioCaptureFailureClassifier.Classify(exception), "capture_write_failed", exception.Message);
        }
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null && !_lifecycle.IsStopRequested)
        {
            RaiseFault(AudioCaptureFailureClassifier.Classify(eventArgs.Exception), "capture_stopped_with_error", eventArgs.Exception.Message);
        }

        IsActive = false;
        EnsureStopEvent();
        CompleteStop();
    }

    private void RaiseFault(AudioCaptureFailureKind failureKind, string code, string message)
    {
        if (_faultRaised)
        {
            return;
        }

        _faultRaised = true;
        var fault = new AudioCaptureFault(
            DateTimeOffset.UtcNow,
            failureKind,
            _source.Kind,
            code,
            message,
            _artifactPath);

        _logger.Warning($"Audio capture fault. Source={_source.Kind}, Code={code}, Message={message}");
        Faulted?.Invoke(this, fault);
    }

    private void CleanupCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= HandleDataAvailable;
            _capture.RecordingStopped -= HandleRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    private AudioCaptureArtifact? FinalizeStop()
    {
        return _lifecycle.Finalize(
            () =>
            {
                if (_writer is null)
                {
                    return _finalArtifact;
                }

                try
                {
                    _finalArtifact = _writer.IsPersisting ? _writer.Complete(_relativeStartOffset) : null;
                    if (!_writer.IsPersisting)
                    {
                        _writer.Discard();
                    }
                }
                finally
                {
                    CleanupCapture();
                }

                return _finalArtifact;
            },
            () => _finalArtifact);
    }

    private void CompleteStop()
    {
        // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
        _stopCompletion.Complete(
            () => FinalizeStop(),
            exception => RaiseFault(
                AudioCaptureFailureClassifier.Classify(exception),
                "capture_finalize_failed",
                exception.Message),
            () => Stopped?.Invoke(this, _stopEvent!));
    }

    private void EnsureStopEvent()
    {
        _stopEvent ??= new AudioCaptureStopEvent(
            DateTimeOffset.UtcNow,
            _source.Kind,
            _lifecycle.IsStopRequested ? AudioCaptureStopKind.Requested : AudioCaptureStopKind.SourceCompleted,
            _lifecycle.IsStopRequested ? "stop_requested" : "source_completed",
            _lifecycle.IsStopRequested ? "Capture stopped on request." : "Capture source stopped.");
    }

    private static TimeSpan GetRelativeStartOffset(DateTimeOffset sessionTimelineOriginUtc)
    {
        var offset = DateTimeOffset.UtcNow - sessionTimelineOriginUtc;
        return offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
    }
}
