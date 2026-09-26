using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Capture;

[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackCaptureAdapter(
    BootstrapFileLogger logger,
    AudioCaptureSourceRequest source,
    string artifactPath,
    int prebufferSeconds) : IAudioCaptureAdapter
{
    private static readonly WaveFormat CaptureWaveFormat = new(44_100, 16, 2);
    private readonly BootstrapFileLogger _logger = logger;
    private readonly AudioCaptureSourceRequest _source = source;
    private readonly string _artifactPath = artifactPath;
    private readonly int _prebufferSeconds = prebufferSeconds;
    private readonly CaptureStopCompletion _stopCompletion = new();
    private readonly CaptureAdapterLifecycleArbiter _lifecycle = new();
    private readonly Guid _audioSessionId = Guid.NewGuid();
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private AutoResetEvent? _captureSignal;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureLoopTask;
    private BufferedWaveArtifactWriter? _writer;
    private byte[]? _packetBuffer;
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

    public async ValueTask StartAsync(
        bool persistImmediately,
        DateTimeOffset sessionTimelineOriginUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsActive)
        {
            return;
        }

        try
        {
            _audioClient = await ProcessLoopbackAudioClientActivator
                .ActivateAsync(_source.RootProcessId!.Value, cancellationToken)
                .ConfigureAwait(false);

            _writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Output,
                _artifactPath,
                CaptureWaveFormat,
                _prebufferSeconds);

            if (persistImmediately)
            {
                _writer.Promote();
            }

            _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                0,
                0,
                CaptureWaveFormat,
                _audioSessionId);

            _captureSignal = new AutoResetEvent(false);
            _audioClient.SetEventHandle(_captureSignal.SafeWaitHandle.DangerousGetHandle());
            _captureClient = _audioClient.AudioCaptureClient;
            _captureCancellation = new CancellationTokenSource();
            // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
            _relativeStartOffset = GetRelativeStartOffset(sessionTimelineOriginUtc);
            _audioClient.Start();
            IsActive = true;
            _captureLoopTask = Task.Run(CaptureLoopAsync);
        }
        catch (Exception exception)
        {
            Cleanup();
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
            _stopEvent ??= new AudioCaptureStopEvent(
                DateTimeOffset.UtcNow,
                _source.Kind,
                AudioCaptureStopKind.Requested,
                "stop_requested",
                "Capture stopped on request.");
            _captureCancellation?.Cancel();
            _captureSignal?.Set();
        });

        if (_captureLoopTask is not null)
        {
            await _captureLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            EnsureStopEvent();
            CompleteStop();
        }

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
        Cleanup();
    }

    private async Task CaptureLoopAsync()
    {
        try
        {
            using var process = Process.GetProcessById(_source.RootProcessId!.Value);
            var processExited = false;

            while (!_captureCancellation!.IsCancellationRequested)
            {
                _captureSignal!.WaitOne(millisecondsTimeout: 250);
                DrainPackets();

                if (processExited)
                {
                    break;
                }

                try
                {
                    processExited = process.HasExited;
                }
                catch
                {
                    processExited = true;
                }

                if (processExited)
                {
                    _stopEvent ??= new AudioCaptureStopEvent(
                        DateTimeOffset.UtcNow,
                        _source.Kind,
                        AudioCaptureStopKind.ProcessExited,
                        "process_exited",
                        $"Process {_source.RootProcessId} exited during capture.");
                    _captureCancellation.Cancel();
                    _captureSignal.Set();
                }
            }

            DrainPackets();
        }
        catch (Exception exception)
        {
            RaiseFault(AudioCaptureFailureClassifier.Classify(exception), "capture_runtime_failed", exception.Message);
        }
        finally
        {
            try
            {
                _audioClient?.Stop();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Process loopback stop failed for process {_source.RootProcessId}.");
            }

            IsActive = false;
            EnsureStopEvent();
            CompleteStop();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private void DrainPackets()
    {
        if (_captureClient is null || _writer is null || _audioClient is null)
        {
            return;
        }

        while (_captureClient.GetNextPacketSize() > 0)
        {
            var framesAvailable = 0;
            AudioClientBufferFlags flags = AudioClientBufferFlags.None;

            try
            {
                var packetPointer = _captureClient.GetBuffer(out framesAvailable, out flags);
                var bytesRecorded = checked(framesAvailable * CaptureWaveFormat.BlockAlign);
                if (bytesRecorded <= 0)
                {
                    continue;
                }

                if ((flags & AudioClientBufferFlags.Silent) == AudioClientBufferFlags.Silent)
                {
                    _writer.AppendSilence(bytesRecorded);
                    continue;
                }

                _packetBuffer ??= new byte[bytesRecorded];
                if (_packetBuffer.Length < bytesRecorded)
                {
                    _packetBuffer = new byte[bytesRecorded];
                }

                Marshal.Copy(packetPointer, _packetBuffer, 0, bytesRecorded);
                _writer.Append(_packetBuffer, bytesRecorded);
            }
            finally
            {
                if (framesAvailable > 0)
                {
                    _captureClient.ReleaseBuffer(framesAvailable);
                }
            }
        }
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

    private void Cleanup()
    {
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        _captureClient?.Dispose();
        _captureClient = null;
        _audioClient?.Dispose();
        _audioClient = null;
        _captureSignal?.Dispose();
        _captureSignal = null;
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
                    Cleanup();
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
