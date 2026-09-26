using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Shared manual/confirmed-Ask recording lifecycle with durable promotion checkpoints.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal sealed class MacOSRecordingSessionCoordinator : IRecordingSessionCoordinator
{
    private static readonly string[] ActiveStatuses =
        ["recording", "paused", "stopping", "processing", "verifying", "promoting"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAudioPlatform _audioPlatform;
    private readonly MeetingSessionRepository _repository;
    private readonly ArtifactPathResolver _pathResolver;
    private readonly Func<ApplicationSettings> _settingsAccessor;
    private readonly BootstrapFileLogger _logger;
    private ActiveRecording? _active;
    private bool _finalizing;
    private bool _disposed;

    public MacOSRecordingSessionCoordinator(
        IAudioPlatform audioPlatform,
        MeetingSessionRepository repository,
        ArtifactPathResolver pathResolver,
        Func<ApplicationSettings> settingsAccessor,
        BootstrapFileLogger logger)
    {
        _audioPlatform = audioPlatform;
        _repository = repository;
        _pathResolver = pathResolver;
        _settingsAccessor = settingsAccessor;
        _logger = logger;
    }

    public event EventHandler<RecordingCoordinatorSnapshot>? SnapshotChanged;

    public bool IsBusy => _active is not null || _finalizing;

    public Guid? ActiveSessionId => _active?.SessionId;

    public ValueTask<bool> StartAskAsync(
        string sourceLabel,
        int rootProcessId,
        string processName,
        CancellationToken cancellationToken) =>
        StartAsync(
            new StartContext(AudioCaptureMode.Ask, sourceLabel, rootProcessId, processName),
            cancellationToken);

    public ValueTask<bool> StartManualAsync(CancellationToken cancellationToken) =>
        StartAsync(
            new StartContext(AudioCaptureMode.Manual, "Manual recording", null, null),
            cancellationToken);

    public async ValueTask PauseOrResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_active is not { } active)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (active.IsPaused)
            {
                await active.Capture.ResumeAsync(cancellationToken).ConfigureAwait(false);
                _repository.TryTransitionArtifactStage(active.SessionId.ToString("N"), ["paused"], "recording", 0, now);
                _active = active with { IsPaused = false, SegmentStartedAtUtc = now };
            }
            else
            {
                await active.Capture.PauseAsync(cancellationToken).ConfigureAwait(false);
                _repository.TryTransitionArtifactStage(active.SessionId.ToString("N"), ["recording"], "paused", 0, now);
                _active = active with
                {
                    IsPaused = true,
                    ActiveDuration = CalculateDuration(active, now),
                    SegmentStartedAtUtc = null
                };
            }

            PublishRecording(_active);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> FinishForRuntimeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_active is not { } active || _finalizing)
            {
                return false;
            }

            _active = null;
            _finalizing = true;
            PublishStage(active.SessionId, RecordingArtifactStage.Stopping, null);
            try
            {
                await active.Capture.StopAsync(cancellationToken).ConfigureAwait(false);
                var artifacts = active.Capture.Snapshot.Artifacts;
                var primary = artifacts.FirstOrDefault(static artifact => artifact.Kind == AudioCaptureArtifactKind.Mixed)
                    ?? throw new InvalidDataException("The capture produced no verified mixed artifact.");
                var duration = new MacOSMp3Encoder().Probe(primary.Path).Duration.TotalSeconds;
                var checkpoint = new MeetingSessionArtifactCheckpoint(
                    "processing",
                    active.TempPath,
                    null,
                    active.StagedPath,
                    artifacts.FirstOrDefault(static artifact => artifact.Kind == AudioCaptureArtifactKind.Output)?.Path,
                    artifacts.FirstOrDefault(static artifact => artifact.Kind == AudioCaptureArtifactKind.Microphone)?.Path,
                    primary.Path,
                    duration,
                    0.25);
                if (!_repository.TryCheckpointArtifact(
                        active.SessionId.ToString("N"),
                        ["recording", "paused", "stopping"],
                        checkpoint,
                        DateTimeOffset.UtcNow))
                {
                    throw new InvalidOperationException("Recording checkpoint could not be persisted.");
                }

                var finalPath = await PromoteAsync(active, primary.Path, cancellationToken).ConfigureAwait(false);
                await active.Capture.DisposeAsync().ConfigureAwait(false);
                if (!RetainTranscriptionSources(active.SessionId, active.TempPath, artifacts))
                {
                    DeleteCanonicalTemp(active);
                    _repository.TryCompleteSourceCleanup(active.SessionId.ToString("N"), DateTimeOffset.UtcNow);
                }
                PublishReady(active.SessionId, finalPath);
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"macOS recording finalization failed for {active.SessionId:N}.");
                try
                {
                    await active.Capture.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    _logger.Error(stopException, $"macOS recovery close failed for {active.SessionId:N}.");
                }

                _repository.TryMarkArtifactAttentionRequired(
                    active.SessionId.ToString("N"),
                    ActiveStatuses,
                    "artifact_finalization_failed",
                    exception.Message,
                    DateTimeOffset.UtcNow);
                var recoverable = active.Capture.Snapshot.Artifacts.FirstOrDefault()?.Path;
                PublishAttention(active.SessionId, exception.Message, recoverable);
                return false;
            }
            finally
            {
                _finalizing = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DiscardAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_active is not { } active || _finalizing)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (!_repository.TryBeginActiveDiscard(
                    active.SessionId.ToString("N"),
                    now,
                    CalculateDuration(active, now).TotalSeconds))
            {
                return false;
            }

            _active = null;
            _finalizing = true;
            try
            {
                await active.Capture.DisposeAsync().ConfigureAwait(false);
                DeleteCanonicalTemp(active);
                if (!_repository.TryCompleteActiveDiscard(active.SessionId.ToString("N"), DateTimeOffset.UtcNow))
                {
                    throw new InvalidOperationException("Recording discard could not be checkpointed.");
                }

                Publish(new RecordingCoordinatorSnapshot(
                    ApplicationActivityState.Ready,
                    null,
                    null,
                    null,
                    true));
                return true;
            }
            catch (Exception exception)
            {
                _repository.TryMarkActiveDiscardAttention(
                    active.SessionId.ToString("N"),
                    exception.Message,
                    DateTimeOffset.UtcNow);
                PublishAttention(active.SessionId, exception.Message, active.TempPath);
                return false;
            }
            finally
            {
                _finalizing = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach (var workItem in _repository.ListRecoverableArtifactWork())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(workItem.Id, "N", out var sessionId)
                    || string.Equals(workItem.Status, "discarding", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (string.Equals(workItem.Status, "ready", StringComparison.Ordinal))
                    {
                        if (RetainTranscriptionSources(sessionId, workItem.TempSessionPath,
                            RecoverySourceArtifacts(workItem))) continue;
                        DeleteCanonicalTemp(sessionId, workItem.TempSessionPath);
                        _repository.TryCompleteSourceCleanup(workItem.Id, DateTimeOffset.UtcNow);
                        continue;
                    }

                    var settings = _settingsAccessor();
                    var finalPath = _pathResolver.GetPrimaryAudioFilePath(
                        settings,
                        sessionId,
                        workItem.CreatedAtUtc,
                        workItem.SourceApp ?? "Recording");
                    var source = FirstReadableMp3(
                        finalPath,
                        workItem.StagedPrimaryPath,
                        workItem.AudioMixPath,
                        string.IsNullOrWhiteSpace(workItem.TempSessionPath)
                            ? null
                            : Path.Combine(workItem.TempSessionPath, "mix.mp3"));
                    source ??= await BuildRecoveryMp3Async(workItem, sessionId, cancellationToken).ConfigureAwait(false);
                    if (source is null)
                    {
                        continue;
                    }

                    var stagedPath = _pathResolver.GetStagedPrimaryAudioFilePath(
                        settings,
                        sessionId,
                        workItem.CreatedAtUtc,
                        workItem.SourceApp ?? "Recording");
                    var active = new ActiveRecording(
                        sessionId,
                        workItem.SourceApp ?? "Recording",
                        workItem.CreatedAtUtc,
                        false,
                        TimeSpan.Zero,
                        null,
                        null!,
                        workItem.TempSessionPath ?? string.Empty,
                        stagedPath,
                        finalPath,
                        []);
                    _repository.TryTransitionArtifactStage(
                        workItem.Id,
                        [workItem.Status, "attention_required"],
                        "processing",
                        0.25,
                        DateTimeOffset.UtcNow);
                    _ = await PromoteAsync(active, source, cancellationToken).ConfigureAwait(false);
                    if (RetainTranscriptionSources(sessionId, workItem.TempSessionPath,
                        RecoverySourceArtifacts(workItem))) continue;
                    DeleteCanonicalTemp(sessionId, workItem.TempSessionPath);
                    _repository.TryCompleteSourceCleanup(workItem.Id, DateTimeOffset.UtcNow);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, $"macOS recording recovery failed for {workItem.Id}.");
                    _repository.TryMarkArtifactAttentionRequired(
                        workItem.Id,
                        ActiveStatuses.Append(workItem.Status).Append("attention_required").ToArray(),
                        "artifact_recovery_failed",
                        exception.Message,
                        DateTimeOffset.UtcNow);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_active is not null)
        {
            _ = await FinishForRuntimeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async ValueTask<bool> StartAsync(StartContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_active is not null || _finalizing)
            {
                return false;
            }

            var settings = _settingsAccessor();
            var sources = SelectSources(context, settings);
            if (sources.Count == 0)
            {
                return false;
            }

            var sessionId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;
            var tempPath = _pathResolver.GetTempSessionDirectoryPath(settings, sessionId);
            var stagedPath = _pathResolver.GetStagedPrimaryAudioFilePath(settings, sessionId, startedAt, context.SourceLabel);
            var finalPath = _pathResolver.GetPrimaryAudioFilePath(settings, sessionId, startedAt, context.SourceLabel);
            var record = CreateRecord(context, sessionId, startedAt, tempPath, stagedPath, sources);
            await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            IAudioCaptureSession capture;
            try
            {
                capture = await _audioPlatform.StartCaptureAsync(
                    new AudioCaptureRequest(sessionId, context.Mode, sources, tempPath, 0, true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _repository.TryMarkArtifactAttentionRequired(
                    sessionId.ToString("N"),
                    ["recording"],
                    "capture_start_failed",
                    exception.Message,
                    DateTimeOffset.UtcNow);
                return false;
            }

            _active = new ActiveRecording(
                sessionId,
                context.SourceLabel,
                startedAt,
                false,
                TimeSpan.Zero,
                startedAt,
                capture,
                tempPath,
                stagedPath,
                finalPath,
                sources);
            PublishRecording(_active);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> PromoteAsync(
        ActiveRecording active,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(active.FinalPath), StringComparison.Ordinal))
        {
            _ = new MacOSMp3Encoder().Probe(active.FinalPath);
            _repository.TryTransitionArtifactStage(
                active.SessionId.ToString("N"),
                ["processing", "verifying", "promoting", "attention_required"],
                "promoting",
                0.9,
                DateTimeOffset.UtcNow);
            if (!_repository.TryMarkArtifactReady(
                    active.SessionId.ToString("N"),
                    ["promoting"],
                    active.FinalPath,
                    DateTimeOffset.UtcNow,
                    sourceCleanupPending: true))
            {
                throw new InvalidOperationException("Recovered recording could not be marked ready.");
            }

            return active.FinalPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(active.FinalPath)!);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(active.StagedPath), StringComparison.Ordinal))
        {
            File.Copy(sourcePath, active.StagedPath, overwrite: false);
        }

        using (var staged = new FileStream(active.StagedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            staged.Flush(flushToDisk: true);
        }

        _repository.TryTransitionArtifactStage(
            active.SessionId.ToString("N"),
            ["processing", "stopping", "attention_required"],
            "verifying",
            0.6,
            DateTimeOffset.UtcNow);
        _ = new MacOSMp3Encoder().Probe(active.StagedPath);
        _repository.TryTransitionArtifactStage(
            active.SessionId.ToString("N"),
            ["verifying"],
            "promoting",
            0.85,
            DateTimeOffset.UtcNow);
        File.Move(active.StagedPath, active.FinalPath, overwrite: false);
        _ = new MacOSMp3Encoder().Probe(active.FinalPath);
        if (!_repository.TryMarkArtifactReady(
                active.SessionId.ToString("N"),
                ["promoting"],
                active.FinalPath,
                DateTimeOffset.UtcNow,
                sourceCleanupPending: true))
        {
            throw new InvalidOperationException("Promoted recording could not be marked ready.");
        }

        return active.FinalPath;
    }

    private IReadOnlyList<AudioCaptureSource> SelectSources(StartContext context, ApplicationSettings settings)
    {
        var snapshot = _audioPlatform.Snapshot;
        var sources = new List<AudioCaptureSource>();
        if (context.Mode == AudioCaptureMode.Ask && context.RootProcessId is > 0)
        {
            sources.Add(AudioCaptureSource.ProcessOutput(context.RootProcessId.Value, context.ProcessName));
        }
        else
        {
            var output = snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive && device.IsDefault)
                         ?? snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive);
            if (output is not null)
            {
                sources.Add(AudioCaptureSource.DeviceLoopback(output.Id));
            }
        }

        var configuredMicrophone = settings.ReleaseV2?.MicrophoneDeviceId ?? settings.Devices.MicrophoneDeviceId;
        var followDefault = settings.ReleaseV2?.FollowSystemDefaultMicrophone ?? settings.Devices.FollowSystemDefaultMic;
        var microphone = !followDefault && !string.IsNullOrWhiteSpace(configuredMicrophone)
            ? snapshot.Microphones.FirstOrDefault(device => device.IsActive && device.Id == configuredMicrophone)
            : snapshot.Microphones.FirstOrDefault(static device => device.IsActive && device.IsDefault)
              ?? snapshot.Microphones.FirstOrDefault(static device => device.IsActive);
        if (microphone is not null)
        {
            sources.Add(AudioCaptureSource.Microphone(microphone.Id));
        }

        return sources;
    }

    private static MeetingSessionRecord CreateRecord(
        StartContext context,
        Guid sessionId,
        DateTimeOffset startedAt,
        string tempPath,
        string stagedPath,
        IReadOnlyList<AudioCaptureSource> sources)
    {
        var output = sources.FirstOrDefault(static source => source.Kind != AudioCaptureSourceKind.Microphone);
        var microphone = sources.FirstOrDefault(static source => source.Kind == AudioCaptureSourceKind.Microphone);
        return MeetingSessionRecord.Create(
            sessionId,
            startedAt,
            context.Mode.ToString().ToLowerInvariant(),
            string.Join('+', sources.Select(static source => source.Kind.ToString().ToLowerInvariant())),
            output?.DeviceId,
            microphone?.DeviceId) with
        {
            SourceApp = context.SourceLabel,
            SourceProcessId = output?.RootProcessId,
            TempSessionPath = tempPath,
            StagedPrimaryPath = stagedPath,
            AudioOutputPath = output is null ? null : Path.Combine(tempPath, "output.wav"),
            AudioMicPath = microphone is null ? null : Path.Combine(tempPath, "microphone.wav")
        };
    }

    private static TimeSpan CalculateDuration(ActiveRecording active, DateTimeOffset now) =>
        active.ActiveDuration + (active.SegmentStartedAtUtc is { } started ? now - started : TimeSpan.Zero);

    private void DeleteCanonicalTemp(ActiveRecording active) => DeleteCanonicalTemp(active.SessionId, active.TempPath);

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
    private bool RetainTranscriptionSources(Guid sessionId, string? root, IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        if (!IsTranscribe.Application.Transcription.TranscriptionSourceHandoff.Exists(root)
            && _settingsAccessor().ReleaseV2?.Transcription?.Mode is not ("local" or "online")) return false;
        IsTranscribe.Application.Transcription.TranscriptionSourceHandoff.Create(root, sessionId, artifacts);
        return true;
    }

    private static IReadOnlyList<AudioCaptureArtifactSnapshot> RecoverySourceArtifacts(MeetingSessionArtifactWorkItem workItem) =>
        new[] { (AudioCaptureArtifactKind.Microphone, workItem.AudioMicPath), (AudioCaptureArtifactKind.Output, workItem.AudioOutputPath) }
            .Where(static item => !string.IsNullOrWhiteSpace(item.Item2) && File.Exists(item.Item2))
            .Select(static item => new AudioCaptureArtifactSnapshot(item.Item1, item.Item2!, new FileInfo(item.Item2!).Length, DateTimeOffset.UtcNow)).ToArray();

    private void DeleteCanonicalTemp(Guid sessionId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var expected = Path.GetFullPath(_pathResolver.GetTempSessionDirectoryPath(_settingsAccessor(), sessionId));
        if (!string.Equals(expected, Path.GetFullPath(path), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporary recording path is outside its canonical location.");
        }

        if (Directory.Exists(expected))
        {
            Directory.Delete(expected, recursive: true);
        }
    }

    private static string? FirstReadableMp3(params string?[] paths) => paths.FirstOrDefault(path =>
        !string.IsNullOrWhiteSpace(path)
        && (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp3.partial", StringComparison.OrdinalIgnoreCase))
        && File.Exists(path));

    private async Task<string?> BuildRecoveryMp3Async(
        MeetingSessionArtifactWorkItem workItem,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workItem.TempSessionPath))
        {
            return null;
        }

        var expected = Path.GetFullPath(_pathResolver.GetTempSessionDirectoryPath(_settingsAccessor(), sessionId));
        if (!string.Equals(expected, Path.GetFullPath(workItem.TempSessionPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery source directory is outside its canonical location.");
        }

        var candidates = new[]
        {
            (AudioCaptureArtifactKind.Output, workItem.AudioOutputPath ?? Path.Combine(expected, "output.wav")),
            (AudioCaptureArtifactKind.Microphone, workItem.AudioMicPath ?? Path.Combine(expected, "microphone.wav"))
        }.Where(static candidate => File.Exists(candidate.Item2)).ToArray();
        var sources = new List<(MacOSWaveSourceArtifact Artifact, long OffsetFrames)>();
        foreach (var (kind, path) in candidates)
        {
            if (MacOSWaveSourceWriter.TryRepair(path))
            {
                sources.Add((new MacOSWaveSourceArtifact(kind, path, new FileInfo(path).Length - 44, 0), 0));
            }
        }

        if (sources.Count == 0)
        {
            return null;
        }

        var mixedWave = Path.Combine(expected, "mix.wav");
        MacOSWaveMixer.Mix(sources, mixedWave);
        var mp3 = Path.Combine(expected, "mix.mp3");
        _ = await new MacOSMp3Encoder().EncodeAsync(mixedWave, mp3, cancellationToken).ConfigureAwait(false);
        return mp3;
    }

    private void PublishRecording(ActiveRecording active)
    {
        var now = DateTimeOffset.UtcNow;
        Publish(new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Recording,
            new ActiveMeetingSnapshot(
                active.SessionId,
                active.SourceLabel,
                active.StartedAtUtc,
                active.Sources.Any(static source => source.Kind != AudioCaptureSourceKind.Microphone),
                active.Sources.Any(static source => source.Kind == AudioCaptureSourceKind.Microphone),
                active.IsPaused)
            {
                ActiveDuration = CalculateDuration(active, now),
                ActiveDurationMeasuredAtUtc = now
            },
            null,
            null,
            false));
    }

    private void PublishStage(Guid id, RecordingArtifactStage stage, string? path) => Publish(
        new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Processing,
            null,
            new RecordingFinalizationSnapshot(id, stage, path),
            null,
            false));

    private void PublishReady(Guid id, string path) => Publish(
        new RecordingCoordinatorSnapshot(
            ApplicationActivityState.Ready,
            null,
            new RecordingFinalizationSnapshot(id, RecordingArtifactStage.Ready, path),
            null,
            true));

    private void PublishAttention(Guid id, string message, string? path) => Publish(
        new RecordingCoordinatorSnapshot(
            ApplicationActivityState.AttentionRequired,
            null,
            new RecordingFinalizationSnapshot(id, RecordingArtifactStage.AttentionRequired, path),
            message,
            true));

    private void Publish(RecordingCoordinatorSnapshot snapshot)
    {
        foreach (EventHandler<RecordingCoordinatorSnapshot> subscriber in
                 SnapshotChanged?.GetInvocationList() ?? [])
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "macOS recording snapshot observer failed.");
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record StartContext(
        AudioCaptureMode Mode,
        string SourceLabel,
        int? RootProcessId,
        string? ProcessName);

    private sealed record ActiveRecording(
        Guid SessionId,
        string SourceLabel,
        DateTimeOffset StartedAtUtc,
        bool IsPaused,
        TimeSpan ActiveDuration,
        DateTimeOffset? SegmentStartedAtUtc,
        IAudioCaptureSession Capture,
        string TempPath,
        string StagedPath,
        string FinalPath,
        IReadOnlyList<AudioCaptureSource> Sources);
}
