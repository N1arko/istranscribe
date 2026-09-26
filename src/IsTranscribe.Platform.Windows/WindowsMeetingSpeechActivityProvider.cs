using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Observes relevant Windows render endpoints and the default microphone in shared mode, keeping only bounded speech features.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMeetingSpeechActivityProvider : IMeetingSpeechActivityProvider
{
    private static readonly TimeSpan RootActivityHold = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromSeconds(5);
    private const double RootSignalThresholdDbfs = -48;
    private const double RootAttributionDominanceDb = 10;

    private readonly BootstrapFileLogger _logger;
    private readonly IWindowsSpeechObservationCaptureFactory _captureFactory;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Dictionary<string, IWindowsSpeechObservationCapture> _renderCaptures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _renderRetryAfterUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<int, RecentRootAudioActivity> _recentRootActivity = [];
    private readonly Dictionary<int, MeetingConversationAlternationTracker> _alternationByRoot = [];
    private MeetingSpeechObservationDemand _demand = MeetingSpeechObservationDemand.Empty;
    private IWindowsSpeechObservationCapture? _microphoneCapture;
    private DateTimeOffset _microphoneRetryAfterUtc;
    private bool _started;
    private bool _disposed;

    public WindowsMeetingSpeechActivityProvider(BootstrapFileLogger logger)
        : this(logger, new WindowsSpeechObservationCaptureFactory())
    {
    }

    internal WindowsMeetingSpeechActivityProvider(
        BootstrapFileLogger logger,
        IWindowsSpeechObservationCaptureFactory captureFactory)
    {
        _logger = logger;
        _captureFactory = captureFactory;
    }

    public MeetingSpeechActivitySnapshot Snapshot { get; private set; } = MeetingSpeechActivitySnapshot.Empty;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (!_started)
        {
            throw new InvalidOperationException("Speech activity provider has not been started.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _demand = demand;
            var nowUtc = ObservationTime(audioPlatform);
            EnsureRenderCaptures(audioPlatform, nowUtc);
            var microphoneEndpointId = demand.ObserveMicrophone
                ? SelectMicrophoneEndpointId(audioPlatform)
                : null;
            EnsureCapture(
                ref _microphoneCapture,
                microphoneEndpointId,
                isRender: false,
                nowUtc,
                ref _microphoneRetryAfterUtc);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioPlatform);
        if (!_started)
        {
            throw new InvalidOperationException("Speech activity provider has not been started.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nowUtc = ObservationTime(audioPlatform);
            EnsureRenderCaptures(audioPlatform, nowUtc);
            var microphoneEndpointId = _demand.ObserveMicrophone
                ? SelectMicrophoneEndpointId(audioPlatform)
                : null;
            EnsureCapture(
                ref _microphoneCapture,
                microphoneEndpointId,
                isRender: false,
                nowUtc,
                ref _microphoneRetryAfterUtc);

            Snapshot = BuildSnapshot(
                audioPlatform,
                _renderCaptures.ToDictionary(
                    static pair => pair.Key,
                    pair => pair.Value.Estimator.GetSummary(nowUtc),
                    StringComparer.Ordinal),
                _microphoneCapture?.Estimator.GetSummary(nowUtc) ?? MeetingSpeechActivitySummary.Empty,
                _renderCaptures
                    .Where(pair => pair.Value.Estimator.IsSpeechActive(nowUtc))
                    .Select(static pair => pair.Key)
                    .ToHashSet(StringComparer.Ordinal),
                _microphoneCapture?.Estimator.IsSpeechActive(nowUtc) == true,
                _recentRootActivity,
                _alternationByRoot,
                nowUtc,
                _demand.RenderRootProcessIds.ToHashSet()) with
            {
                MicrophoneCaptureHealth = string.IsNullOrWhiteSpace(microphoneEndpointId)
                    ? MeetingSpeechCaptureHealth.Unknown
                    : _microphoneCapture is { IsActive: true }
                        ? MeetingSpeechCaptureHealth.Available
                        : MeetingSpeechCaptureHealth.Unavailable
            };
            return Snapshot;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask<MeetingSpeechCaptureHealth> ObserveMicrophoneCaptureHealthAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioPlatform);
        if (!_started)
        {
            throw new InvalidOperationException("Speech activity provider has not been started.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nowUtc = ObservationTime(audioPlatform);
            if (_microphoneCapture is { IsActive: true })
            {
                Snapshot = Snapshot with
                {
                    ObservedAtUtc = nowUtc,
                    MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Available
                };
                return MeetingSpeechCaptureHealth.Available;
            }

            var microphoneEndpointId = SelectMicrophoneEndpointId(audioPlatform);
            if (string.IsNullOrWhiteSpace(microphoneEndpointId))
            {
                Snapshot = Snapshot with
                {
                    ObservedAtUtc = nowUtc,
                    MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Unknown
                };
                return MeetingSpeechCaptureHealth.Unknown;
            }

            IWindowsSpeechObservationCapture? probe = null;
            var health = MeetingSpeechCaptureHealth.Unavailable;
            try
            {
                probe = _captureFactory.Start(microphoneEndpointId, isRender: false, _logger);
                health = probe.IsActive
                    ? MeetingSpeechCaptureHealth.Available
                    : MeetingSpeechCaptureHealth.Unavailable;
            }
            catch (Exception exception)
            {
                LogCaptureDegraded(isRender: false, exception);
            }
            finally
            {
                probe?.Dispose();
            }
            Snapshot = Snapshot with
            {
                ObservedAtUtc = nowUtc,
                MicrophoneCaptureHealth = health
            };
            return health;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _captureGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var capture in _renderCaptures.Values)
            {
                capture.Dispose();
            }

            _renderCaptures.Clear();
            _renderRetryAfterUtc.Clear();
            _microphoneCapture?.Dispose();
            _microphoneCapture = null;
            _recentRootActivity.Clear();
            _alternationByRoot.Clear();
        }
        finally
        {
            _captureGate.Release();
            _captureGate.Dispose();
        }
    }

    internal static MeetingSpeechActivitySnapshot BuildSnapshot(
        AudioPlatformSnapshot audioPlatform,
        IReadOnlyDictionary<string, MeetingSpeechActivitySummary> renderByDevice,
        MeetingSpeechActivitySummary microphoneSummary,
        IReadOnlySet<string> activeRenderDeviceIds,
        bool microphoneSpeechNow,
        IDictionary<int, RecentRootAudioActivity> recentRootActivity,
        IDictionary<int, MeetingConversationAlternationTracker> alternationByRoot,
        DateTimeOffset nowUtc,
        IReadOnlySet<int>? demandedRootProcessIds = null)
    {
        foreach (var signal in audioPlatform.Signals
                     .Where(static signal =>
                         string.Equals(signal.SessionState, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase) &&
                         signal.SignalLevelDbfs >= RootSignalThresholdDbfs &&
                         !string.IsNullOrWhiteSpace(signal.OutputDeviceId))
                     .Where(signal => demandedRootProcessIds is null
                         || demandedRootProcessIds.Contains(signal.RootProcessId))
                     .GroupBy(static signal => signal.RootProcessId)
                     .Select(static group => group
                         .OrderByDescending(static signal => signal.SignalLevelDbfs)
                         .First()))
        {
            recentRootActivity[signal.RootProcessId] = new RecentRootAudioActivity(
                signal.OutputDeviceId,
                signal.SignalLevelDbfs,
                nowUtc,
                signal.IsProcessTreeMatch);
        }

        foreach (var staleRoot in recentRootActivity
                     .Where(pair => nowUtc - pair.Value.ObservedAtUtc > RootActivityHold)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            recentRootActivity.Remove(staleRoot);
        }

        var renderByRoot = new Dictionary<int, MeetingSpeechActivitySummary>();
        var renderUsesDescendantByRoot = new Dictionary<int, bool>();
        foreach (var endpointGroup in recentRootActivity.GroupBy(static pair => pair.Value.OutputDeviceId))
        {
            if (!renderByDevice.TryGetValue(endpointGroup.Key, out var summary) || !summary.IsSustainedSpeech)
            {
                continue;
            }

            var candidates = endpointGroup
                .OrderByDescending(static pair => pair.Value.SignalLevelDbfs)
                .ThenBy(static pair => pair.Key)
                .ToArray();
            if (candidates.Length > 1 &&
                candidates[0].Value.SignalLevelDbfs - candidates[1].Value.SignalLevelDbfs < RootAttributionDominanceDb)
            {
                continue;
            }

            renderByRoot[candidates[0].Key] = summary;
            renderUsesDescendantByRoot[candidates[0].Key] = candidates[0].Value.IsProcessTreeMatch;
        }

        var activeRenderRoots = renderByRoot.Keys
            .Where(rootProcessId =>
                recentRootActivity.TryGetValue(rootProcessId, out var activity) &&
                activeRenderDeviceIds.Contains(activity.OutputDeviceId))
            .ToHashSet();
        var alternationByRootSnapshot = new Dictionary<int, double>();
        foreach (var rootProcessId in recentRootActivity.Keys)
        {
            if (!alternationByRoot.TryGetValue(rootProcessId, out var tracker))
            {
                tracker = new MeetingConversationAlternationTracker();
                alternationByRoot[rootProcessId] = tracker;
            }

            var strength = tracker.Observe(
                nowUtc,
                renderSpeech: activeRenderRoots.Count == 1 && activeRenderRoots.Contains(rootProcessId),
                microphoneSpeechNow);
            if (strength > 0)
            {
                alternationByRootSnapshot[rootProcessId] = strength;
            }
        }

        foreach (var staleRoot in alternationByRoot.Keys
                     .Where(rootProcessId => !recentRootActivity.ContainsKey(rootProcessId))
                     .ToArray())
        {
            alternationByRoot.Remove(staleRoot);
        }

        return new MeetingSpeechActivitySnapshot(
            nowUtc,
            renderByRoot,
            microphoneSummary,
            alternationByRootSnapshot,
            renderUsesDescendantByRoot);
    }

    private void EnsureRenderCaptures(AudioPlatformSnapshot audioPlatform, DateTimeOffset nowUtc)
    {
        var demandedRootProcessIds = _demand.RenderRootProcessIds.ToHashSet();
        var activeEndpointIds = audioPlatform.OutputDevices
            .Where(static device => device.IsActive)
            .Select(static device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var desiredEndpointIds = audioPlatform.Signals
            .Where(signal => demandedRootProcessIds.Contains(signal.RootProcessId))
            .Select(static signal => signal.OutputDeviceId)
            .Where(endpointId => !string.IsNullOrWhiteSpace(endpointId) && activeEndpointIds.Contains(endpointId))
            .ToHashSet(StringComparer.Ordinal);
        var defaultEndpointId = audioPlatform.OutputDevices
            .FirstOrDefault(static device => device.IsDefault && device.IsActive)?.Id;
        if (demandedRootProcessIds.Count > 0
            && desiredEndpointIds.Count == 0
            && !string.IsNullOrWhiteSpace(defaultEndpointId))
        {
            desiredEndpointIds.Add(defaultEndpointId);
        }

        foreach (var obsoleteEndpointId in _renderCaptures.Keys
                     .Where(endpointId => !desiredEndpointIds.Contains(endpointId))
                     .ToArray())
        {
            _renderCaptures[obsoleteEndpointId].Dispose();
            _renderCaptures.Remove(obsoleteEndpointId);
            _renderRetryAfterUtc.Remove(obsoleteEndpointId);
        }

        foreach (var endpointId in desiredEndpointIds)
        {
            if (_renderCaptures.TryGetValue(endpointId, out var existing))
            {
                if (existing.IsActive)
                {
                    continue;
                }

                existing.Dispose();
                _renderCaptures.Remove(endpointId);
            }

            if (_renderRetryAfterUtc.TryGetValue(endpointId, out var retryAfterUtc) && nowUtc < retryAfterUtc)
            {
                continue;
            }

            try
            {
                _renderCaptures[endpointId] = _captureFactory.Start(endpointId, isRender: true, _logger);
                _renderRetryAfterUtc.Remove(endpointId);
            }
            catch (Exception exception)
            {
                _renderRetryAfterUtc[endpointId] = nowUtc + CaptureRetryDelay;
                LogCaptureDegraded(isRender: true, exception);
            }
        }
    }

    private void EnsureCapture(
        ref IWindowsSpeechObservationCapture? capture,
        string? endpointId,
        bool isRender,
        DateTimeOffset nowUtc,
        ref DateTimeOffset retryAfterUtc)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            capture?.Dispose();
            capture = null;
            retryAfterUtc = DateTimeOffset.MinValue;
            return;
        }

        if (capture is { IsActive: true } &&
            string.Equals(capture.EndpointId, endpointId, StringComparison.Ordinal))
        {
            return;
        }

        capture?.Dispose();
        capture = null;
        if (nowUtc < retryAfterUtc)
        {
            return;
        }

        try
        {
            capture = _captureFactory.Start(endpointId, isRender, _logger);
            retryAfterUtc = DateTimeOffset.MinValue;
        }
        catch (Exception exception)
        {
            retryAfterUtc = nowUtc + CaptureRetryDelay;
            LogCaptureDegraded(isRender, exception);
        }
    }

    private void LogCaptureDegraded(bool isRender, Exception exception) =>
        _logger.LogEvent(
            "Warning",
            isRender ? "DETECTION_RENDER_VAD_DEGRADED" : "DETECTION_MIC_VAD_DEGRADED",
            "In-memory speech activity observation is temporarily unavailable.",
            metadata: new Dictionary<string, object?>
            {
                ["exception_type"] = exception.GetType().Name
            });

    private static DateTimeOffset ObservationTime(AudioPlatformSnapshot audioPlatform) =>
        audioPlatform.ObservedAtUtc == DateTimeOffset.MinValue
            ? TimeProvider.System.GetUtcNow()
            : audioPlatform.ObservedAtUtc;

    private static string? SelectMicrophoneEndpointId(AudioPlatformSnapshot audioPlatform) =>
        audioPlatform.Microphones
            .FirstOrDefault(static device => device.IsDefault && device.IsActive)?.Id
        ?? audioPlatform.Microphones.FirstOrDefault(static device => device.IsActive)?.Id;

    internal sealed class ObservationCapture : IWindowsSpeechObservationCapture
    {
        private readonly BootstrapFileLogger _logger;
        private readonly string _channel;
        private readonly MMDeviceEnumerator _enumerator;
        private readonly MMDevice _device;
        private readonly IWaveIn _capture;
        private bool _disposed;

        private ObservationCapture(
            string endpointId,
            bool isRender,
            BootstrapFileLogger logger,
            MMDeviceEnumerator enumerator,
            MMDevice device,
            IWaveIn capture)
        {
            EndpointId = endpointId;
            _logger = logger;
            _channel = isRender ? "render" : "microphone";
            _enumerator = enumerator;
            _device = device;
            _capture = capture;
            Estimator = new SpeechActivityEstimator();
            _capture.DataAvailable += HandleDataAvailable;
            _capture.RecordingStopped += HandleRecordingStopped;
            _capture.StartRecording();
            IsActive = true;

            _logger.LogEvent(
                "Info",
                isRender ? "DETECTION_RENDER_VAD_STARTED" : "DETECTION_MIC_VAD_STARTED",
                "Bounded in-memory speech activity observation started.");
        }

        public string EndpointId { get; }

        public bool IsActive { get; private set; }

        public SpeechActivityEstimator Estimator { get; }

        public static ObservationCapture Start(
            string endpointId,
            bool isRender,
            BootstrapFileLogger logger)
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            IWaveIn? capture = null;
            try
            {
                device = enumerator.GetDevice(endpointId);
                capture = isRender
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device);
                return new ObservationCapture(endpointId, isRender, logger, enumerator, device, capture);
            }
            catch
            {
                capture?.Dispose();
                device?.Dispose();
                enumerator.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _logger.LogEvent(
                "Info",
                "DETECTION_VAD_CAPTURE_STOPPING",
                "In-memory speech activity capture is stopping.",
                metadata: new Dictionary<string, object?> { ["channel"] = _channel });
            _capture.DataAvailable -= HandleDataAvailable;
            _capture.RecordingStopped -= HandleRecordingStopped;
            if (IsActive)
            {
                try
                {
                    _capture.StopRecording();
                    _logger.LogEvent(
                        "Info",
                        "DETECTION_VAD_STOP_REQUESTED",
                        "Speech activity capture accepted the stop request.",
                        metadata: new Dictionary<string, object?> { ["channel"] = _channel });
                }
                catch (InvalidOperationException)
                {
                }
            }

            IsActive = false;
            _logger.LogEvent(
                "Info",
                "DETECTION_VAD_DISPOSING",
                "Speech activity capture resources are being released.",
                metadata: new Dictionary<string, object?> { ["channel"] = _channel });
            _capture.Dispose();
            _logger.LogEvent(
                "Info",
                "DETECTION_VAD_CAPTURE_STOPPED",
                "Speech activity capture stopped.",
                metadata: new Dictionary<string, object?> { ["channel"] = _channel });
            _device.Dispose();
            _enumerator.Dispose();
            Estimator.Reset();
        }

        private void HandleDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            if (_disposed || eventArgs.BytesRecorded <= 0)
            {
                return;
            }

            try
            {
                var samples = WindowsAudioSampleDecoder.DecodeMono(
                    eventArgs.Buffer,
                    eventArgs.BytesRecorded,
                    _capture.WaveFormat);
                var firstSampleAtUtc = TimeProvider.System.GetUtcNow() -
                                       TimeSpan.FromSeconds((double)samples.Length / _capture.WaveFormat.SampleRate);
                Estimator.AppendMonoSamples(samples, _capture.WaveFormat.SampleRate, firstSampleAtUtc);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                IsActive = false;
                _logger.LogEvent(
                    "Warning",
                    "DETECTION_VAD_PACKET_DEGRADED",
                    "An unsupported audio packet was excluded from speech activity observation.",
                    metadata: new Dictionary<string, object?>
                    {
                        ["exception_type"] = exception.GetType().Name
                    });
            }
        }

        private void HandleRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            IsActive = false;
            if (!_disposed && eventArgs.Exception is not null)
            {
                _logger.LogEvent(
                    "Warning",
                    "DETECTION_VAD_CAPTURE_STOPPED",
                    "Speech activity observation stopped and will be retried.",
                    metadata: new Dictionary<string, object?>
                    {
                        ["exception_type"] = eventArgs.Exception.GetType().Name
                    });
            }
        }
    }

    internal sealed record RecentRootAudioActivity(
        string OutputDeviceId,
        double SignalLevelDbfs,
        DateTimeOffset ObservedAtUtc,
        bool IsProcessTreeMatch = false);
}

internal interface IWindowsSpeechObservationCaptureFactory
{
    IWindowsSpeechObservationCapture Start(
        string endpointId,
        bool isRender,
        BootstrapFileLogger logger);
}

internal interface IWindowsSpeechObservationCapture : IDisposable
{
    string EndpointId { get; }

    bool IsActive { get; }

    SpeechActivityEstimator Estimator { get; }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeechObservationCaptureFactory : IWindowsSpeechObservationCaptureFactory
{
    public IWindowsSpeechObservationCapture Start(
        string endpointId,
        bool isRender,
        BootstrapFileLogger logger) =>
        WindowsMeetingSpeechActivityProvider.ObservationCapture.Start(endpointId, isRender, logger);
}
