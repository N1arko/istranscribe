using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Extracts bounded speech features from macOS process taps and the selected microphone.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed class MacOSMeetingSpeechActivityProvider : IMeetingSpeechActivityProvider
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly BootstrapFileLogger _logger;
    private readonly IMacOSDetectionCaptureFactory _captureFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, IMacOSDetectionCapture> _renderCaptures = [];
    private readonly Dictionary<int, DateTimeOffset> _renderRetryAfter = [];
    private readonly Dictionary<int, AlternationTracker> _alternation = [];
    private MeetingSpeechObservationDemand _demand = MeetingSpeechObservationDemand.Empty;
    private IMacOSDetectionCapture? _microphoneCapture;
    private string? _microphoneId;
    private DateTimeOffset _microphoneRetryAfter;
    private bool _started;
    private bool _disposed;

    public MacOSMeetingSpeechActivityProvider(MacOSAudioPlatform audioPlatform, BootstrapFileLogger logger)
        : this(logger, new MacOSDetectionCaptureFactory(audioPlatform))
    {
    }

    internal MacOSMeetingSpeechActivityProvider(
        BootstrapFileLogger logger,
        IMacOSDetectionCaptureFactory captureFactory)
    {
        _logger = logger;
        _captureFactory = captureFactory;
    }

    public MeetingSpeechActivitySnapshot Snapshot { get; private set; } = MeetingSpeechActivitySnapshot.Empty;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        return ValueTask.CompletedTask;
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    public async ValueTask ApplyObservationDemandAsync(
        MeetingSpeechObservationDemand demand,
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(audioPlatform);
        if (!_started) throw new InvalidOperationException("Speech activity provider has not been started.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _demand = demand;
            var now = ObservationTime(audioPlatform);
            ReconcileRender(audioPlatform, now);
            ReconcileMicrophone(audioPlatform, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) throw new InvalidOperationException("Speech activity provider has not been started.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = ObservationTime(audioPlatform);
            ReconcileRender(audioPlatform, now);
            ReconcileMicrophone(audioPlatform, now);

            var microphone = _microphoneCapture?.Estimator.GetSummary(now) ?? MeetingSpeechActivitySummary.Empty;
            var render = _renderCaptures
                .Select(pair => (pair.Key, Summary: pair.Value.Estimator.GetSummary(now)))
                .Where(static pair => pair.Summary.IsSustainedSpeech)
                .ToDictionary(static pair => pair.Key, static pair => pair.Summary);
            var alternation = new Dictionary<int, double>();
            foreach (var root in _renderCaptures.Keys)
            {
                if (!_alternation.TryGetValue(root, out var tracker))
                {
                    tracker = new AlternationTracker();
                    _alternation[root] = tracker;
                }
                var strength = tracker.Observe(
                    now,
                    _renderCaptures[root].Estimator.IsSpeechActive(now),
                    _microphoneCapture?.Estimator.IsSpeechActive(now) == true);
                if (strength > 0) alternation[root] = strength;
            }

            Snapshot = new MeetingSpeechActivitySnapshot(
                now,
                render,
                microphone,
                alternation,
                _renderCaptures.Keys.ToDictionary(static root => root, static _ => false))
            {
                MicrophoneCaptureHealth = string.IsNullOrWhiteSpace(_microphoneId)
                    ? MeetingSpeechCaptureHealth.Unknown
                    : _microphoneCapture is { IsActive: true }
                        ? MeetingSpeechCaptureHealth.Available
                        : MeetingSpeechCaptureHealth.Unavailable
            };
            return Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask<MeetingSpeechCaptureHealth> ObserveMicrophoneCaptureHealthAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioPlatform);
        if (!_started) throw new InvalidOperationException("Speech activity provider has not been started.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = ObservationTime(audioPlatform);
            if (_microphoneCapture is { IsActive: true })
            {
                Snapshot = Snapshot with
                {
                    ObservedAtUtc = now,
                    MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Available
                };
                return MeetingSpeechCaptureHealth.Available;
            }

            var microphoneId = SelectMicrophoneId(audioPlatform);
            if (string.IsNullOrWhiteSpace(microphoneId))
            {
                Snapshot = Snapshot with
                {
                    ObservedAtUtc = now,
                    MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Unknown
                };
                return MeetingSpeechCaptureHealth.Unknown;
            }

            IMacOSDetectionCapture? probe = null;
            var health = MeetingSpeechCaptureHealth.Unavailable;
            try
            {
                probe = _captureFactory.StartMicrophone(microphoneId);
                health = probe.IsActive
                    ? MeetingSpeechCaptureHealth.Available
                    : MeetingSpeechCaptureHealth.Unavailable;
            }
            catch (Exception exception)
            {
                LogDegraded("DETECTION_MIC_VAD_DEGRADED", exception);
            }
            finally
            {
                probe?.Dispose();
            }

            Snapshot = Snapshot with
            {
                ObservedAtUtc = now,
                MicrophoneCaptureHealth = health
            };
            return health;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var capture in _renderCaptures.Values) capture.Dispose();
            _renderCaptures.Clear();
            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
            _alternation.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ReconcileRender(AudioPlatformSnapshot snapshot, DateTimeOffset now)
    {
        var availableRoots = snapshot.Processes
            .Select(static process => process.RootProcessId)
            .ToHashSet();
        var desiredRoots = _demand.RenderRootProcessIds
            .Where(availableRoots.Contains)
            .ToHashSet();
        foreach (var obsolete in _renderCaptures.Keys.Where(root => !desiredRoots.Contains(root)).ToArray())
        {
            _renderCaptures[obsolete].Dispose();
            _renderCaptures.Remove(obsolete);
            _renderRetryAfter.Remove(obsolete);
            _alternation.Remove(obsolete);
        }

        foreach (var root in desiredRoots)
        {
            if (_renderCaptures.TryGetValue(root, out var existing) && existing.IsActive) continue;
            existing?.Dispose();
            _renderCaptures.Remove(root);
            if (_renderRetryAfter.TryGetValue(root, out var retry) && now < retry) continue;
            try
            {
                _renderCaptures[root] = _captureFactory.StartRender(root);
                _renderRetryAfter.Remove(root);
            }
            catch (Exception exception)
            {
                _renderRetryAfter[root] = now + RetryDelay;
                LogDegraded("DETECTION_RENDER_VAD_DEGRADED", exception);
            }
        }
    }

    private void ReconcileMicrophone(AudioPlatformSnapshot snapshot, DateTimeOffset now)
    {
        if (!_demand.ObserveMicrophone)
        {
            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
            _microphoneId = null;
            _microphoneRetryAfter = DateTimeOffset.MinValue;
            return;
        }

        var microphoneId = SelectMicrophoneId(snapshot);
        if (string.IsNullOrWhiteSpace(microphoneId))
        {
            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
            _microphoneId = null;
            return;
        }
        if (_microphoneCapture is { IsActive: true } && string.Equals(_microphoneId, microphoneId, StringComparison.Ordinal)) return;
        _microphoneCapture?.Dispose();
        _microphoneCapture = null;
        _microphoneId = microphoneId;
        if (now < _microphoneRetryAfter) return;
        try
        {
            _microphoneCapture = _captureFactory.StartMicrophone(microphoneId);
            _microphoneRetryAfter = DateTimeOffset.MinValue;
        }
        catch (Exception exception)
        {
            _microphoneRetryAfter = now + RetryDelay;
            LogDegraded("DETECTION_MIC_VAD_DEGRADED", exception);
        }
    }

    private void LogDegraded(string code, Exception exception) => _logger.LogEvent(
        "Warning",
        code,
        "Bounded in-memory speech observation is temporarily unavailable.",
        metadata: new Dictionary<string, object?> { ["exception_type"] = exception.GetType().Name });

    private static DateTimeOffset ObservationTime(AudioPlatformSnapshot snapshot) =>
        snapshot.ObservedAtUtc == DateTimeOffset.MinValue
            ? DateTimeOffset.UtcNow
            : snapshot.ObservedAtUtc;

    private static string? SelectMicrophoneId(AudioPlatformSnapshot snapshot) =>
        snapshot.Microphones.FirstOrDefault(static item => item.IsDefault && item.IsActive)?.Id
        ?? snapshot.Microphones.FirstOrDefault(static item => item.IsActive)?.Id;

    private sealed class AlternationTracker
    {
        private bool? _lastWasRender;
        private DateTimeOffset _lastTransition;
        private int _transitions;

        public double Observe(DateTimeOffset now, bool render, bool microphone)
        {
            if (render == microphone) return Math.Clamp(_transitions / 4d, 0, 1);
            var current = render;
            if (_lastWasRender is not null && _lastWasRender != current && now - _lastTransition <= TimeSpan.FromSeconds(10))
            {
                _transitions = Math.Min(_transitions + 1, 4);
            }
            else if (now - _lastTransition > TimeSpan.FromSeconds(20))
            {
                _transitions = 0;
            }
            _lastWasRender = current;
            _lastTransition = now;
            return Math.Clamp(_transitions / 4d, 0, 1);
        }
    }
}

internal interface IMacOSDetectionCaptureFactory
{
    IMacOSDetectionCapture StartRender(int rootProcessId);

    IMacOSDetectionCapture StartMicrophone(string deviceId);
}

internal interface IMacOSDetectionCapture : IDisposable
{
    bool IsActive { get; }

    SpeechActivityEstimator Estimator { get; }
}

internal sealed class MacOSDetectionCaptureFactory(MacOSAudioPlatform audioPlatform) : IMacOSDetectionCaptureFactory
{
    public IMacOSDetectionCapture StartRender(int rootProcessId) =>
        new Capture(audioPlatform.StartProcessPcmStream(rootProcessId));

    public IMacOSDetectionCapture StartMicrophone(string deviceId) =>
        new Capture(audioPlatform.StartMicrophonePcmStream(deviceId));

    private sealed class Capture : IMacOSDetectionCapture
    {
        private readonly MacOSPcmStream _stream;

        public Capture(MacOSPcmStream stream)
        {
            _stream = stream;
            Estimator = new SpeechActivityEstimator();
            _stream.FrameReady += OnFrame;
        }

        public bool IsActive => _stream.State == MacOSPcmStreamState.Running;

        public SpeechActivityEstimator Estimator { get; }

        public void Dispose()
        {
            _stream.FrameReady -= OnFrame;
            _stream.Dispose();
            Estimator.Reset();
        }

        private void OnFrame(object? sender, MacOSPcmFrame frame)
        {
            var mono = new float[frame.FrameCount];
            var samples = frame.Samples.Span;
            for (var sample = 0; sample < frame.FrameCount; sample++)
            {
                double sum = 0;
                for (var channel = 0; channel < frame.Channels; channel++)
                {
                    sum += samples[(sample * frame.Channels) + channel];
                }
                mono[sample] = (float)(sum / frame.Channels);
            }
            var startsAt = frame.ObservedAtUtc - TimeSpan.FromSeconds((double)frame.FrameCount / frame.SampleRate);
            Estimator.AppendMonoSamples(mono, frame.SampleRate, startsAt);
        }
    }
}
