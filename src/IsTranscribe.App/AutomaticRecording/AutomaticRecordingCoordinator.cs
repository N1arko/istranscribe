using System.IO;
using System.Runtime.Versioning;
using IsTranscribe.App.Configuration;
using IsTranscribe.App.ManualControls;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.App.AutomaticRecording;

public sealed record AutomaticCoordinatorContextChangedEventArgs(
    ApplicationSettings Settings,
    IReadOnlyList<AppRuleRecord> AppRules);

[SupportedOSPlatform("windows")]
public sealed class AutomaticRecordingCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly BootstrapFileLogger _logger;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly AppRuleRepository _appRuleRepository;
    private readonly IProcessWatcher _processWatcher;
    private readonly IAudioSessionWatcher _sessionWatcher;
    private readonly ManualControlGateway _manualControlGateway;
    private readonly IAutomaticPromptService _promptService;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DetectionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private ApplicationSettings _settings = ApplicationSettings.Default;
    private HostCapabilitySnapshot _capability = new(HostCapabilityState.Blocked, false, "Capability state is unavailable.");
    private IReadOnlyList<AppRuleRecord> _appRules = Array.Empty<AppRuleRecord>();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private PendingPromptState? _pendingPrompt;
    private AutomaticRuntimeSession? _automaticSession;

    public AutomaticRecordingCoordinator(
        BootstrapFileLogger logger,
        IApplicationSettingsStore settingsStore,
        AppRuleRepository appRuleRepository,
        IProcessWatcher processWatcher,
        IAudioSessionWatcher sessionWatcher,
        ManualControlGateway manualControlGateway,
        IAutomaticPromptService promptService,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _settingsStore = settingsStore;
        _appRuleRepository = appRuleRepository;
        _processWatcher = processWatcher;
        _sessionWatcher = sessionWatcher;
        _manualControlGateway = manualControlGateway;
        _promptService = promptService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<AutomaticCoordinatorContextChangedEventArgs>? ContextChanged;

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#coordinator-model.gating
    public void UpdateHostContext(ApplicationSettings settings, HostCapabilitySnapshot capability, IReadOnlyList<AppRuleRecord> appRules)
    {
        _settings = settings;
        _capability = capability;
        _appRules = appRules
            .Select(AppRuleRecord.Normalize)
            .ToArray();

        _processWatcher.UpdateWatchList(_appRules.Where(static rule => rule.Enabled).Select(static rule => rule.ProcessName));
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return ValueTask.CompletedTask;
        }

        _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunAsync(_loopCancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCancellation is null || _loopTask is null)
        {
            return;
        }

        _loopCancellation.Cancel();
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loopCancellation.Dispose();
            _loopCancellation = null;
            _loopTask = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await TickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var observedCandidates = CollectCandidates();
            UpdateTrackers(observedCandidates, now);

            if (_automaticSession is not null)
            {
                await ProcessAutomaticSessionAsync(observedCandidates, now).ConfigureAwait(false);
                return;
            }

            if (_pendingPrompt is not null)
            {
                await ProcessPendingPromptAsync(observedCandidates, now).ConfigureAwait(false);
                return;
            }

            if (!CanRunAutomaticLifecycle())
            {
                return;
            }

            if (_manualControlGateway.Snapshot.CurrentRecording is not null
                || _manualControlGateway.Snapshot.RecordingState == RecordingActivityState.AwaitingConfirmation)
            {
                return;
            }

            var selectedCandidate = SelectReadyCandidate(observedCandidates, now);
            if (selectedCandidate is null)
            {
                return;
            }

            await StartCandidateFlowAsync(selectedCandidate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.loss-of-eligibility
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#lifecycle.merge
    private async Task ProcessAutomaticSessionAsync(
        IReadOnlyDictionary<string, ObservedMeetingCandidate> observedCandidates,
        DateTimeOffset now)
    {
        var automaticSession = _automaticSession;
        if (automaticSession is null)
        {
            return;
        }

        if (_manualControlGateway.Snapshot.CurrentRecording is null)
        {
            _automaticSession = null;
            return;
        }

        if (_manualControlGateway.IsMergePending && !automaticSession.IsMergePending)
        {
            automaticSession.IsMergePending = true;
            automaticSession.MergeDeadlineUtc = (_manualControlGateway.MergePendingStartedAtUtc ?? now)
                .AddSeconds(Math.Max(0, _settings.Recording.MergeWindowSeconds));
        }

        observedCandidates.TryGetValue(automaticSession.ProcessName, out var matchingCandidate);

        if (automaticSession.IsMergePending)
        {
            if (matchingCandidate is not null
                && CanRunAutomaticLifecycle()
                 && now < automaticSession.MergeDeadlineUtc)
             {
                 var plan = _manualControlGateway.ResolveAutomaticRecordingPlan(matchingCandidate.RootProcessId);
                 if (plan.Sources.Count > 0 && string.Equals(plan.MergeSourceFamily, automaticSession.MergeSourceFamily, StringComparison.OrdinalIgnoreCase))
                 {
                     var resumed = await _manualControlGateway.ResumeAutomaticRecordingAsync(
                         new AutomaticRecordingStartRequest(automaticSession.Mode, automaticSession.SourceApp, matchingCandidate.RootProcessId, plan)).ConfigureAwait(false);

                    if (resumed)
                    {
                        automaticSession.IsMergePending = false;
                        automaticSession.LossStartedAtUtc = null;
                        automaticSession.RootProcessId = matchingCandidate.RootProcessId;
                    }
                }
            }

            if (automaticSession.IsMergePending && now >= automaticSession.MergeDeadlineUtc)
            {
                await _manualControlGateway.StopCurrentRecordingAsync().ConfigureAwait(false);
                _automaticSession = null;
            }

            return;
        }

        if (_manualControlGateway.Snapshot.RecordingState == RecordingActivityState.Paused)
        {
            automaticSession.LossStartedAtUtc = null;
            return;
        }

        if (matchingCandidate is not null)
        {
            automaticSession.LossStartedAtUtc = null;
            automaticSession.RootProcessId = matchingCandidate.RootProcessId;
            return;
        }

        automaticSession.LossStartedAtUtc ??= now;
        if (now - automaticSession.LossStartedAtUtc < TimeSpan.FromSeconds(Math.Max(0, _settings.Recording.StopDelaySeconds)))
        {
            return;
        }

        if (_settings.Recording.MergeWindowSeconds > 0)
        {
            var entered = await _manualControlGateway.EnterAutomaticMergePendingAsync().ConfigureAwait(false);
            if (entered)
            {
                automaticSession.IsMergePending = true;
                automaticSession.MergeDeadlineUtc = now.AddSeconds(_settings.Recording.MergeWindowSeconds);
                automaticSession.LossStartedAtUtc = null;
            }

            return;
        }

        await _manualControlGateway.StopCurrentRecordingAsync().ConfigureAwait(false);
        _automaticSession = null;
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.cancellation
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#session-bootstrap.when
    private async Task ProcessPendingPromptAsync(
        IReadOnlyDictionary<string, ObservedMeetingCandidate> observedCandidates,
        DateTimeOffset now)
    {
        if (_pendingPrompt is null)
        {
            return;
        }

        if (!_pendingPrompt.DecisionTask.IsCompleted)
        {
            if (!CanKeepPromptOpen(observedCandidates, now, out var reason))
            {
                await CancelPendingPromptAsync(reason).ConfigureAwait(false);
            }

            return;
        }

        var pendingPrompt = _pendingPrompt;
        _pendingPrompt = null;

        var decision = await pendingPrompt.DecisionTask.ConfigureAwait(false);
        switch (decision)
        {
            case AutomaticPromptDecision.StartRecording:
                if (await _manualControlGateway.ActivatePreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false))
                {
                    _automaticSession = pendingPrompt.ToRuntimeSession();
                }

                break;
            case AutomaticPromptDecision.AddAndStart:
                await UpsertDiscoveredRuleAsync(pendingPrompt.SourceApp, pendingPrompt.ProcessName).ConfigureAwait(false);
                if (await _manualControlGateway.ActivatePreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false))
                {
                    _automaticSession = pendingPrompt.ToRuntimeSession();
                }

                break;
            case AutomaticPromptDecision.StartOnce:
                SuppressCurrentMeeting(pendingPrompt.ProcessName);
                if (await _manualControlGateway.ActivatePreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false))
                {
                    _automaticSession = pendingPrompt.ToRuntimeSession();
                }

                break;
            case AutomaticPromptDecision.IgnoreThisApp:
                await IgnoreProcessAsync(pendingPrompt.ProcessName).ConfigureAwait(false);
                SuppressCurrentMeeting(pendingPrompt.ProcessName);
                await _manualControlGateway.DismissPreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false);
                break;
            case AutomaticPromptDecision.No:
            case AutomaticPromptDecision.NotNow:
                SuppressCurrentMeeting(pendingPrompt.ProcessName);
                await _manualControlGateway.DismissPreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false);
                break;
            case AutomaticPromptDecision.Cancelled:
                await _manualControlGateway.DismissPreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId).ConfigureAwait(false);
                break;
        }

        pendingPrompt.Cancellation.Dispose();
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#coordinator-model.gating
    private bool CanRunAutomaticLifecycle() =>
        _settings.OnboardingCompleted
        && _capability.State != HostCapabilityState.Blocked
        && !string.Equals(_settings.Recording.Mode, "off", StringComparison.OrdinalIgnoreCase)
        && !_manualControlGateway.Snapshot.PrivacyPauseEnabled;

    private bool CanKeepPromptOpen(
        IReadOnlyDictionary<string, ObservedMeetingCandidate> observedCandidates,
        DateTimeOffset now,
        out string reason)
    {
        if (!CanRunAutomaticLifecycle())
        {
            reason = "Automatic recording is unavailable in the current state.";
            return false;
        }

        if (_pendingPrompt is null)
        {
            reason = string.Empty;
            return false;
        }

        if (!observedCandidates.ContainsKey(_pendingPrompt.ProcessName))
        {
            reason = "Meeting activity ended before confirmation.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task CancelPendingPromptAsync(string reason)
    {
        if (_pendingPrompt is null)
        {
            return;
        }

        var pendingPrompt = _pendingPrompt;
        _pendingPrompt = null;
        pendingPrompt.Cancellation.Cancel();
        await _manualControlGateway.DismissPreparedAutomaticRecordingAsync(pendingPrompt.PreparedSessionId, reason).ConfigureAwait(false);
        pendingPrompt.Cancellation.Dispose();
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.auto-add
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.unknown-app
    private async Task StartCandidateFlowAsync(ObservedMeetingCandidate candidate, CancellationToken cancellationToken)
    {
        var recordingMode = _settings.Recording.Mode.ToLowerInvariant();
        if (candidate.IsKnown)
        {
            await StartKnownCandidateFlowAsync(candidate, recordingMode, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (_settings.Applications.AutoDiscoveryPolicy.ToLowerInvariant())
        {
            case "auto_add":
                await UpsertDiscoveredRuleAsync(candidate.SourceApp, candidate.ProcessName).ConfigureAwait(false);
                await StartKnownCandidateFlowAsync(candidate with { IsKnown = true }, recordingMode, cancellationToken).ConfigureAwait(false);
                break;
            case "ask_to_add":
                await StartPromptFlowAsync(candidate, recordingMode, "unknown_combined", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task StartKnownCandidateFlowAsync(ObservedMeetingCandidate candidate, string recordingMode, CancellationToken cancellationToken)
    {
        await RefreshKnownRuleDisplayNameAsync(candidate.ProcessName, candidate.SourceApp).ConfigureAwait(false);

        if (recordingMode == "auto")
        {
            var plan = _manualControlGateway.ResolveAutomaticRecordingPlan(candidate.RootProcessId);
            if (plan.Sources.Count == 0)
            {
                return;
            }

            var started = await _manualControlGateway.StartAutomaticRecordingAsync(
                new AutomaticRecordingStartRequest(recordingMode, candidate.SourceApp, candidate.RootProcessId, plan)).ConfigureAwait(false);
             if (started)
             {
                 _automaticSession = new AutomaticRuntimeSession(candidate.ProcessName, candidate.SourceApp, candidate.RootProcessId, recordingMode, plan.MergeSourceFamily);
             }

            return;
        }

        await StartPromptFlowAsync(candidate, recordingMode, "known_ask", cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.ask
    private async Task StartPromptFlowAsync(
        ObservedMeetingCandidate candidate,
        string recordingMode,
        string promptKind,
        CancellationToken cancellationToken)
    {
        var plan = _manualControlGateway.ResolveAutomaticRecordingPlan(candidate.RootProcessId);
        if (plan.Sources.Count == 0)
        {
            return;
        }

        var prepared = await _manualControlGateway.PrepareAutomaticRecordingAsync(
            new AutomaticRecordingStartRequest(recordingMode, candidate.SourceApp, candidate.RootProcessId, plan),
            promptKind).ConfigureAwait(false);

        if (prepared is null)
        {
            return;
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pendingPrompt = new PendingPromptState(
            candidate.ProcessName,
            candidate.SourceApp,
             candidate.RootProcessId,
             recordingMode,
             plan.MergeSourceFamily,
             promptKind,
             prepared.SessionId,
             linkedCancellation,
            _promptService.ShowAsync(
                new AutomaticPromptRequest(
                    promptKind,
                    candidate.SourceApp,
                    candidate.ProcessName,
                    TimeoutSeconds: 8),
                linkedCancellation.Token));
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.known-app
    private ObservedMeetingCandidate? SelectReadyCandidate(
        IReadOnlyDictionary<string, ObservedMeetingCandidate> observedCandidates,
        DateTimeOffset now)
    {
        var ruleOrder = _appRules
            .Select((rule, index) => new { Rule = rule, Index = index })
            .ToDictionary(static item => item.Rule.ProcessName, static item => item.Index, StringComparer.OrdinalIgnoreCase);

        return observedCandidates.Values
            .Where(candidate => _trackers.TryGetValue(candidate.ProcessName, out var tracker)
                                && !tracker.SuppressedUntilMeetingEnds
                                && tracker.EligibleSinceUtc.HasValue
                                && now - tracker.EligibleSinceUtc.Value >= TimeSpan.FromSeconds(Math.Max(0, _settings.Recording.StartDelaySeconds)))
            .OrderBy(static candidate => candidate.IsKnown ? 0 : 1)
            .ThenBy(candidate => ruleOrder.TryGetValue(candidate.ProcessName, out var index) ? index : int.MaxValue)
            .ThenByDescending(static candidate => candidate.SignalLevelDbfs)
            .FirstOrDefault();
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.signal
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.known-app
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.unknown-app
    private IReadOnlyDictionary<string, ObservedMeetingCandidate> CollectCandidates()
    {
        var enabledRules = _appRules
            .Where(static rule => rule.Enabled)
            .GroupBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var silenceThreshold = _settings.Recording.SilenceThresholdDbfs;
        var currentSnapshot = _sessionWatcher.CurrentSnapshot
            .Where(snapshot =>
                string.Equals(snapshot.AudioSessionState, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase)
                && snapshot.SignalLevelDbfs >= silenceThreshold)
            .GroupBy(snapshot => AppRuleRecord.NormalizeProcessName(snapshot.RootProcessName), StringComparer.OrdinalIgnoreCase);

        var results = new Dictionary<string, ObservedMeetingCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in currentSnapshot)
        {
            var processName = group.Key;
            if (ApplicationDiscoveryDefaults.BuiltInExclusions.Contains(processName, StringComparer.OrdinalIgnoreCase)
                || _settings.Applications.Exclusions.Contains(processName, StringComparer.OrdinalIgnoreCase)
                || _settings.Applications.IgnoredAppSuggestions.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var bestSnapshot = group
                .OrderByDescending(static snapshot => snapshot.SignalLevelDbfs)
                .First();

            var isKnown = enabledRules.ContainsKey(processName);
            var sourceApp = ResolveSourceApp(bestSnapshot.RootProcessId, processName);
            results[processName] = new ObservedMeetingCandidate(
                processName,
                sourceApp,
                bestSnapshot.RootProcessId,
                isKnown,
                bestSnapshot.SignalLevelDbfs);
        }

        return results;
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.known-app
    private static string ResolveSourceApp(int processId, string processName) =>
        ProcessDisplayNameResolver.Resolve(processId, processName);

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.suppression
    private void UpdateTrackers(IReadOnlyDictionary<string, ObservedMeetingCandidate> observedCandidates, DateTimeOffset now)
    {
        foreach (var candidate in observedCandidates.Values)
        {
            if (!_trackers.TryGetValue(candidate.ProcessName, out var tracker))
            {
                tracker = new DetectionTracker();
                _trackers[candidate.ProcessName] = tracker;
            }

            tracker.EligibleSinceUtc ??= now;
            tracker.LastObservedEligibleAtUtc = now;
        }

        foreach (var entry in _trackers.ToArray())
        {
            if (observedCandidates.ContainsKey(entry.Key))
            {
                continue;
            }

            entry.Value.EligibleSinceUtc = null;
            if (entry.Value.SuppressedUntilMeetingEnds
                && entry.Value.LastObservedEligibleAtUtc is DateTimeOffset lastEligibleAtUtc
                && now - lastEligibleAtUtc >= TimeSpan.FromSeconds(Math.Max(1, _settings.Recording.StopDelaySeconds)))
            {
                entry.Value.SuppressedUntilMeetingEnds = false;
            }

            if (entry.Value.LastObservedEligibleAtUtc is DateTimeOffset previous
                && now - previous >= TimeSpan.FromMinutes(10))
            {
                _trackers.Remove(entry.Key);
            }
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.suppression
    private void SuppressCurrentMeeting(string processName)
    {
        if (_trackers.TryGetValue(processName, out var tracker))
        {
            tracker.SuppressedUntilMeetingEnds = true;
            tracker.EligibleSinceUtc = null;
        }
    }

    private async Task UpsertDiscoveredRuleAsync(string sourceApp, string processName)
    {
        await _appRuleRepository.UpsertAsync(AppRuleRecord.Create(sourceApp, processName), CancellationToken.None).ConfigureAwait(false);
        _appRules = await _appRuleRepository.ListAsync(CancellationToken.None).ConfigureAwait(false);
        _processWatcher.UpdateWatchList(_appRules.Where(static rule => rule.Enabled).Select(static rule => rule.ProcessName));
        PublishContextChanged(new AutomaticCoordinatorContextChangedEventArgs(_settings, _appRules));
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#detection.known-app
    private async Task RefreshKnownRuleDisplayNameAsync(string processName, string displayName)
    {
        var existingRule = _appRules.FirstOrDefault(rule => string.Equals(rule.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existingRule is null
            || string.Equals(existingRule.DisplayName, displayName, StringComparison.Ordinal))
        {
            return;
        }

        await _appRuleRepository.UpsertAsync(existingRule with { DisplayName = displayName }, CancellationToken.None).ConfigureAwait(false);
        _appRules = await _appRuleRepository.ListAsync(CancellationToken.None).ConfigureAwait(false);
        _processWatcher.UpdateWatchList(_appRules.Where(static rule => rule.Enabled).Select(static rule => rule.ProcessName));
        PublishContextChanged(new AutomaticCoordinatorContextChangedEventArgs(_settings, _appRules));
    }

    private async Task IgnoreProcessAsync(string processName)
    {
        if (_settings.Applications.IgnoredAppSuggestions.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _settings = _settings with
        {
            Applications = _settings.Applications with
            {
                IgnoredAppSuggestions = _settings.Applications.IgnoredAppSuggestions
                    .Append(processName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            }
        };

        await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        PublishContextChanged(new AutomaticCoordinatorContextChangedEventArgs(_settings, _appRules));
    }

    private void PublishContextChanged(AutomaticCoordinatorContextChangedEventArgs args)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                var (self, eventArgs) = ((AutomaticRecordingCoordinator self, AutomaticCoordinatorContextChangedEventArgs eventArgs))state!;
                self.ContextChanged?.Invoke(self, eventArgs);
            },
            (this, args));
    }

    private sealed class DetectionTracker
    {
        public DateTimeOffset? EligibleSinceUtc { get; set; }

        public DateTimeOffset? LastObservedEligibleAtUtc { get; set; }

        public bool SuppressedUntilMeetingEnds { get; set; }
    }

    private sealed record ObservedMeetingCandidate(
        string ProcessName,
        string SourceApp,
        int RootProcessId,
        bool IsKnown,
        double SignalLevelDbfs);

     private sealed record PendingPromptState(
         string ProcessName,
         string SourceApp,
         int RootProcessId,
         string Mode,
         string MergeSourceFamily,
         string PromptKind,
         Guid PreparedSessionId,
         CancellationTokenSource Cancellation,
         Task<AutomaticPromptDecision> DecisionTask)
     {
         public AutomaticRuntimeSession ToRuntimeSession() =>
             new(ProcessName, SourceApp, RootProcessId, Mode, MergeSourceFamily);
     }

     private sealed class AutomaticRuntimeSession(
         string processName,
         string sourceApp,
         int rootProcessId,
         string mode,
         string mergeSourceFamily)
     {
        public string ProcessName { get; } = processName;

        public string SourceApp { get; } = sourceApp;

        public int RootProcessId { get; set; } = rootProcessId;

        public string Mode { get; } = mode;

         public string MergeSourceFamily { get; } = mergeSourceFamily;

        public DateTimeOffset? LossStartedAtUtc { get; set; }

        public bool IsMergePending { get; set; }

        public DateTimeOffset MergeDeadlineUtc { get; set; }
    }
}
