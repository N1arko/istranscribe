using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Settings;

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Runs signal providers on bounded cadences and turns normalized observations into one safe Ask prompt.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </remarks>
public sealed class MeetingDetectionCoordinator : IAsyncDisposable
{
    private readonly IAudioPlatform _audioPlatform;
    private readonly IMeetingWindowEvidenceProvider _windowProvider;
    private readonly IMeetingSpeechActivityProvider _speechProvider;
    private readonly MeetingProfileRegistry _profiles;
    private readonly MeetingObservationAssembler _assembler;
    private readonly MeetingDetectionEngine _engine;
    private readonly MeetingDetectionCoordinatorOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MeetingApplicationPreference> _applicationPreferences;
    private IReadOnlyList<MeetingWindowEvidenceSnapshot> _windowEvidence = [];
    private DateTimeOffset _nextWindowObservationUtc = DateTimeOffset.MinValue;
    private MeetingPendingPrompt? _pendingPrompt;
    private string? _activeSessionCandidateId;
    private int? _activeSessionRootProcessId;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _speechProviderStarted;
    private bool _disposed;

    public MeetingDetectionCoordinator(
        IAudioPlatform audioPlatform,
        IMeetingWindowEvidenceProvider windowProvider,
        IMeetingSpeechActivityProvider speechProvider,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> applicationPreferences,
        MeetingDetectionEngine? engine = null,
        MeetingDetectionCoordinatorOptions? options = null)
    {
        _audioPlatform = audioPlatform;
        _windowProvider = windowProvider;
        _speechProvider = speechProvider;
        _profiles = profiles;
        _assembler = new MeetingObservationAssembler(profiles);
        _applicationPreferences = applicationPreferences;
        _engine = engine ?? new MeetingDetectionEngine();
        _options = options ?? MeetingDetectionCoordinatorOptions.Default;
    }

    public event EventHandler<MeetingDetectionCoordinatorSnapshot>? SnapshotChanged;

    public MeetingDetectionCoordinatorSnapshot Snapshot { get; private set; } =
        MeetingDetectionCoordinatorSnapshot.Empty;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loopTask is not null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSpeechProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        _loopCancellation = new CancellationTokenSource();
        _loopTask = RunAsync(_loopCancellation.Token);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var cancellation = _loopCancellation;
        var task = _loopTask;
        _loopCancellation = null;
        _loopTask = null;
        if (cancellation is not null && task is not null)
        {
            cancellation.Cancel();
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        if (_speechProviderStarted)
        {
            await _speechProvider.ApplyObservationDemandAsync(
                MeetingSpeechObservationDemand.Empty,
                _audioPlatform.Snapshot,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<MeetingDetectionCoordinatorSnapshot> EvaluateOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureSpeechProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MeetingDetectionCoordinatorSnapshot snapshot;
        try
        {
            snapshot = await EvaluateUnderGateAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            Snapshot = snapshot;
        }
        finally
        {
            _gate.Release();
        }

        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask<MeetingSpeechCaptureHealth> ObserveMicrophoneCaptureHealthOnceAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureSpeechProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _speechProvider
                .ObserveMicrophoneCaptureHealthAsync(_audioPlatform.Snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> ResolvePromptAsync(
        string candidateId,
        MeetingPromptResolution resolution,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MeetingDetectionCoordinatorSnapshot? changedSnapshot = null;
        bool resolved;
        try
        {
            var pendingPrompt = _pendingPrompt;
            resolved = _engine.ResolvePrompt(candidateId, resolution, nowUtc);
            if (resolved)
            {
                if (resolution == MeetingPromptResolution.Record && pendingPrompt is not null)
                {
                    _activeSessionCandidateId = candidateId;
                    _activeSessionRootProcessId = pendingPrompt.Candidate.Frame.RootProcessId;
                }

                _pendingPrompt = null;
                changedSnapshot = Snapshot with
                {
                    ObservedAtUtc = nowUtc,
                    PendingPrompt = null,
                    Candidates = Snapshot.Candidates
                        .Select(candidate => string.Equals(candidate.CandidateId, candidateId, StringComparison.Ordinal)
                            ? candidate with
                            {
                                IsSuppressed = true,
                                SuppressionReason = resolution.ToString().ToLowerInvariant()
                            }
                            : candidate)
                        .ToArray()
                };
                Snapshot = changedSnapshot;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changedSnapshot is not null)
        {
            SnapshotChanged?.Invoke(this, changedSnapshot);
        }

        return resolved;
    }

    public async ValueTask<bool> EndActiveSessionAsync(
        string candidateId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ended = _engine.EndActiveSession(candidateId, nowUtc);
            if (ended && string.Equals(_activeSessionCandidateId, candidateId, StringComparison.Ordinal))
            {
                _activeSessionCandidateId = null;
                _activeSessionRootProcessId = null;
            }

            return ended;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateApplicationPreferences(
        IReadOnlyList<MeetingApplicationPreference> applicationPreferences)
    {
        ArgumentNullException.ThrowIfNull(applicationPreferences);
        _applicationPreferences = applicationPreferences;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _speechProvider.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ObservationCadence);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(TimeProvider.System.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                var degraded = Snapshot with
                {
                    ObservedAtUtc = TimeProvider.System.GetUtcNow(),
                    DegradedSignals = ["evaluation"]
                };
                Snapshot = degraded;
                SnapshotChanged?.Invoke(this, degraded);
            }
        }
    }

    private async ValueTask<MeetingDetectionCoordinatorSnapshot> EvaluateUnderGateAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var degradedSignals = new List<string>();
        var audio = _audioPlatform.Snapshot;
        if (nowUtc >= _nextWindowObservationUtc)
        {
            try
            {
                _windowEvidence = await _windowProvider
                    .ObserveAsync(audio, _profiles, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _windowEvidence = [];
                degradedSignals.Add("window_evidence");
            }

            _nextWindowObservationUtc = nowUtc + _options.WindowEvidenceCadence;
        }

        MeetingSpeechActivitySnapshot speech;
        var observationDemand = BuildObservationDemand(audio);
        try
        {
            await _speechProvider.ApplyObservationDemandAsync(
                observationDemand,
                audio,
                cancellationToken).ConfigureAwait(false);
            speech = await _speechProvider.ObserveAsync(audio, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            speech = MeetingSpeechActivitySnapshot.Empty;
            degradedSignals.Add("speech_activity");
        }

        if (observationDemand.ObserveMicrophone
            && speech.MicrophoneCaptureHealth == MeetingSpeechCaptureHealth.Unavailable)
        {
            degradedSignals.Add("microphone_capture");
        }

        if (_pendingPrompt is not null && nowUtc >= _pendingPrompt.ExpiresAtUtc)
        {
            _engine.ResolvePrompt(
                _pendingPrompt.Candidate.Frame.CandidateId,
                MeetingPromptResolution.Timeout,
                nowUtc);
            _pendingPrompt = null;
        }

        var frames = _assembler.Assemble(
            audio,
            _windowEvidence,
            speech,
            _applicationPreferences,
            nowUtc);
        var detection = _engine.EvaluateBatch(frames, nowUtc);
        if (detection.ShouldCancelActivePrompt && _pendingPrompt is not null)
        {
            _engine.ResolvePrompt(
                _pendingPrompt.Candidate.Frame.CandidateId,
                MeetingPromptResolution.CandidateEnded,
                nowUtc);
            _pendingPrompt = null;
        }

        if (_pendingPrompt is not null)
        {
            var refreshedCandidate = detection.Candidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.CandidateId,
                    _pendingPrompt.Candidate.Frame.CandidateId,
                    StringComparison.Ordinal));
            if (refreshedCandidate is not null)
            {
                _pendingPrompt = _pendingPrompt with
                {
                    Candidate = new MeetingPromptCandidate(
                        refreshedCandidate.Frame,
                        refreshedCandidate.Score)
                };
            }
        }

        if (_pendingPrompt is null && detection.PromptCandidate is not null)
        {
            _pendingPrompt = new MeetingPendingPrompt(
                detection.PromptCandidate,
                nowUtc,
                nowUtc + _options.PromptTimeout);
        }

        return new MeetingDetectionCoordinatorSnapshot(
            nowUtc,
            detection.Candidates.Any(static candidate => candidate.IsSuspected),
            _pendingPrompt,
            detection.ShadowPromptCandidate,
            detection.Candidates,
            degradedSignals);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    private MeetingSpeechObservationDemand BuildObservationDemand(AudioPlatformSnapshot audio)
    {
        var excludedCandidateIds = Snapshot.Candidates
            .Where(static candidate => candidate.IsSuppressed || candidate.Score.IsHardExcluded)
            .Select(static candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        var roots = MeetingAudioObservationEligibilityPolicy.FindEligibleCandidates(
                audio,
                _windowEvidence,
                _profiles,
                _applicationPreferences)
            .Where(candidate => !excludedCandidateIds.Contains(candidate.CandidateId))
            .Select(static candidate => candidate.RootProcessId)
            .ToHashSet();

        if (_pendingPrompt is not null)
        {
            roots.Add(_pendingPrompt.Candidate.Frame.RootProcessId);
        }

        if (_activeSessionRootProcessId is int activeRootProcessId)
        {
            roots.Add(activeRootProcessId);
        }

        return roots.Count == 0
            ? MeetingSpeechObservationDemand.Empty
            : new MeetingSpeechObservationDemand(roots.ToArray(), ObserveMicrophone: true);
    }

    private async ValueTask EnsureSpeechProviderStartedAsync(CancellationToken cancellationToken)
    {
        if (_speechProviderStarted)
        {
            return;
        }

        await _speechProvider.StartAsync(cancellationToken).ConfigureAwait(false);
        _speechProviderStarted = true;
    }
}

public sealed record MeetingDetectionCoordinatorOptions(
    TimeSpan ObservationCadence,
    TimeSpan WindowEvidenceCadence,
    TimeSpan PromptTimeout)
{
    public static MeetingDetectionCoordinatorOptions Default { get; } = new(
        ObservationCadence: TimeSpan.FromMilliseconds(250),
        WindowEvidenceCadence: TimeSpan.FromSeconds(1),
        PromptTimeout: TimeSpan.FromSeconds(12));
}

public sealed record MeetingPendingPrompt(
    MeetingPromptCandidate Candidate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record MeetingDetectionCoordinatorSnapshot(
    DateTimeOffset ObservedAtUtc,
    bool IsSuspected,
    MeetingPendingPrompt? PendingPrompt,
    MeetingPromptCandidate? ShadowWouldPromptCandidate,
    IReadOnlyList<MeetingDetectionCandidateSnapshot> Candidates,
    IReadOnlyList<string> DegradedSignals)
{
    public static MeetingDetectionCoordinatorSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        IsSuspected: false,
        PendingPrompt: null,
        ShadowWouldPromptCandidate: null,
        Candidates: [],
        DegradedSignals: []);
}
