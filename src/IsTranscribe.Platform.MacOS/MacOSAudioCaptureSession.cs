using IsTranscribe.Core.Audio;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Loss-resistant macOS source capture, timeline mix and staged MP3 finalization.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal sealed class MacOSAudioCaptureSession : IAudioCaptureSession
{
    private readonly object _gate = new();
    private readonly AudioCaptureRequest _request;
    private readonly MacOSAudioPlatform _platform;
    private readonly double _hostTicksPerSecond;
    private readonly IReadOnlyList<Source> _sources;
    private readonly MacOSMp3Encoder _encoder = new();
    private readonly List<AudioCaptureFailureSnapshot> _runtimeFailures = [];
    private Task? _stopTask;
    private bool _stopping;
    private bool _disposed;

    private MacOSAudioCaptureSession(
        AudioCaptureRequest request,
        MacOSAudioPlatform platform,
        double hostTicksPerSecond,
        IReadOnlyList<Source> sources,
        IReadOnlyList<AudioCaptureFailureSnapshot> startupFailures,
        bool isPersisting)
    {
        _request = request;
        _platform = platform;
        _hostTicksPerSecond = hostTicksPerSecond;
        _sources = sources;
        Snapshot = new AudioCaptureSessionSnapshot(
            request.SessionId,
            AudioCaptureState.Running,
            isPersisting,
            [],
            startupFailures);
        _platform.SnapshotChanged += HandlePlatformSnapshotChanged;
    }

    public event EventHandler<AudioCaptureSessionSnapshot>? SnapshotChanged;

    public AudioCaptureSessionSnapshot Snapshot { get; private set; }

    public static MacOSAudioCaptureSession Start(MacOSAudioPlatform platform, AudioCaptureRequest request)
    {
        var sources = new List<Source>();
        var failures = new List<AudioCaptureFailureSnapshot>();
        foreach (var requestedSource in request.Sources)
        {
            try
            {
                var artifactKind = requestedSource.Kind == AudioCaptureSourceKind.Microphone
                    ? AudioCaptureArtifactKind.Microphone
                    : AudioCaptureArtifactKind.Output;
                var stem = artifactKind == AudioCaptureArtifactKind.Microphone ? "microphone" : "output";
                var writer = new MacOSWaveSourceWriter(
                    Path.Combine(request.TempSessionDirectoryPath, $"{stem}.wav"),
                    request.PrebufferSeconds,
                    platform.HostTicksPerSecond);
                var stream = requestedSource.Kind switch
                {
                    AudioCaptureSourceKind.ProcessOutput => platform.StartProcessPcmStream(
                        requestedSource.RootProcessId!.Value),
                    AudioCaptureSourceKind.DeviceLoopback => platform.StartSystemOutputPcmStream(
                        requestedSource.DeviceId!),
                    AudioCaptureSourceKind.Microphone => platform.StartMicrophonePcmStream(
                        requestedSource.DeviceId!),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        requestedSource.Kind,
                        "Unknown capture source kind.")
                };
                sources.Add(new Source(requestedSource, artifactKind, stream, writer));
            }
            catch (Exception exception)
            {
                failures.Add(Failure(requestedSource.Kind, "source_start_failed", exception.Message));
            }
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No requested macOS audio source could be started.");
        }

        var persistImmediately = request.Mode == AudioCaptureMode.Manual || request.PrebufferSeconds == 0;
        if (persistImmediately)
        {
            foreach (var source in sources)
            {
                source.Writer.Promote();
            }
        }

        return new MacOSAudioCaptureSession(
            request,
            platform,
            platform.HostTicksPerSecond,
            sources,
            failures,
            persistImmediately);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        foreach (var source in _sources)
        {
            source.Writer.SetPaused(true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        foreach (var source in _sources)
        {
            source.Writer.SetPaused(false);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PromotePrebufferAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        foreach (var source in _sources)
        {
            source.Writer.Promote();
        }

        Publish(Snapshot with { IsPersistingAudio = true });
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Task stopTask;
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        foreach (var source in _sources)
        {
            source.Writer.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        Publish(Snapshot with { State = AudioCaptureState.Stopping });
        lock (_gate)
        {
            _stopping = true;
        }

        _platform.SnapshotChanged -= HandlePlatformSnapshotChanged;
        foreach (var source in _sources)
        {
            source.Detach();
        }

        var failures = Snapshot.Failures.Concat(_runtimeFailures).Distinct().ToList();
        var completedSources = new List<MacOSWaveSourceArtifact>();
        foreach (var source in _sources)
        {
            if (source.Stream.State == MacOSPcmStreamState.Faulted)
            {
                failures.Add(Failure(source.Kind, source.LastFailureCode ?? "source_failed", "Audio source stopped unexpectedly."));
            }

            try
            {
                var artifact = source.Writer.Complete(source.ArtifactKind);
                if (artifact is not null)
                {
                    completedSources.Add(artifact);
                }
            }
            catch (Exception exception)
            {
                failures.Add(Failure(source.Kind, "source_finalize_failed", exception.Message));
            }
        }

        var artifacts = completedSources.Select(static source => new AudioCaptureArtifactSnapshot(
            source.Kind,
            source.Path,
            source.DataBytes,
            DateTimeOffset.UtcNow)).ToList();
        if (_request.CreateMixedArtifact && completedSources.Count > 0)
        {
            try
            {
                var firstHostTime = completedSources.Min(static source => source.FirstHostTime);
                var aligned = completedSources.Select(source => (
                    source,
                    OffsetFrames(source.FirstHostTime - firstHostTime))).ToArray();
                var mixedWave = Path.Combine(_request.TempSessionDirectoryPath, "mix.wav");
                MacOSWaveMixer.Mix(aligned, mixedWave);
                var partialMp3 = Path.Combine(_request.TempSessionDirectoryPath, ".mix.mp3.partial");
                var finalMp3 = Path.Combine(_request.TempSessionDirectoryPath, "mix.mp3");
                _ = await _encoder.EncodeAsync(mixedWave, partialMp3, CancellationToken.None).ConfigureAwait(false);
                File.Move(partialMp3, finalMp3, overwrite: false);
                var verified = _encoder.Probe(finalMp3);
                artifacts.Add(new AudioCaptureArtifactSnapshot(
                    AudioCaptureArtifactKind.Mixed,
                    finalMp3,
                    verified.Bytes,
                    DateTimeOffset.UtcNow));
            }
            catch (Exception exception)
            {
                failures.Add(Failure(null, "mixed_artifact_build_failed", exception.Message));
            }
        }

        var state = artifacts.Count > 0 ? AudioCaptureState.Completed : AudioCaptureState.Faulted;
        Publish(new AudioCaptureSessionSnapshot(
            _request.SessionId,
            state,
            Snapshot.IsPersistingAudio,
            artifacts,
            failures));
    }

    private long OffsetFrames(ulong hostTicks) => checked((long)Math.Round(
        hostTicks * MacOSMp3Encoder.SampleRate / _hostTicksPerSecond));

    private void Publish(AudioCaptureSessionSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void HandlePlatformSnapshotChanged(object? sender, AudioPlatformSnapshot snapshot)
    {
        AudioCaptureSessionSnapshot? changed = null;
        lock (_gate)
        {
            if (_stopping || _disposed)
            {
                return;
            }

            foreach (var source in _sources.Where(static source => source.Stream.State == MacOSPcmStreamState.Faulted))
            {
                var previousFailure = source.Stream.FailureCode ?? "source_failed";
                try
                {
                    var replacement = CreateReplacement(source.Request, snapshot);
                    if (replacement is null)
                    {
                        continue;
                    }

                    source.Replace(replacement);
                    _runtimeFailures.Add(Failure(
                        source.Kind,
                        previousFailure == "process_exited" ? "process_output_fallback" : "source_rebound",
                        "Audio source changed and capture continued on an available source."));
                }
                catch (Exception exception)
                {
                    if (!_runtimeFailures.Any(failure =>
                            failure.SourceKind == source.Kind && failure.Code == "source_rebind_failed"))
                    {
                        _runtimeFailures.Add(Failure(source.Kind, "source_rebind_failed", exception.Message));
                    }
                }
            }

            if (_runtimeFailures.Count > 0)
            {
                changed = Snapshot with { Failures = Snapshot.Failures.Concat(_runtimeFailures).Distinct().ToArray() };
                Snapshot = changed;
            }
        }

        if (changed is not null)
        {
            SnapshotChanged?.Invoke(this, changed);
        }
    }

    private MacOSPcmStream? CreateReplacement(AudioCaptureSource requested, AudioPlatformSnapshot snapshot)
    {
        if (requested.Kind == AudioCaptureSourceKind.ProcessOutput
            && requested.RootProcessId is int processId
            && snapshot.Processes.Any(process => process.RootProcessId == processId))
        {
            return _platform.StartProcessPcmStream(processId);
        }

        if (requested.Kind is AudioCaptureSourceKind.ProcessOutput or AudioCaptureSourceKind.DeviceLoopback)
        {
            var output = snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive && device.IsDefault)
                         ?? snapshot.OutputDevices.FirstOrDefault(static device => device.IsActive);
            return output is null ? null : _platform.StartSystemOutputPcmStream(output.Id);
        }

        var microphone = snapshot.Microphones.FirstOrDefault(device =>
                             device.IsActive && device.Id == requested.DeviceId)
                         ?? snapshot.Microphones.FirstOrDefault(static device => device.IsActive && device.IsDefault)
                         ?? snapshot.Microphones.FirstOrDefault(static device => device.IsActive);
        return microphone is null ? null : _platform.StartMicrophonePcmStream(microphone.Id);
    }

    private static AudioCaptureFailureSnapshot Failure(
        AudioCaptureSourceKind? sourceKind,
        string code,
        string message) => new(
            DateTimeOffset.UtcNow,
            "AudioSourceFailure",
            sourceKind,
            code,
            message,
            null);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Source
    {
        private readonly EventHandler<MacOSPcmFrame> _frameHandler;

        public Source(
            AudioCaptureSource request,
            AudioCaptureArtifactKind artifactKind,
            MacOSPcmStream stream,
            MacOSWaveSourceWriter writer)
        {
            Request = request;
            ArtifactKind = artifactKind;
            Writer = writer;
            _frameHandler = (_, frame) => writer.Append(frame);
            Stream = stream;
            Stream.FrameReady += _frameHandler;
        }

        public AudioCaptureSource Request { get; }
        public AudioCaptureSourceKind Kind => Request.Kind;
        public AudioCaptureArtifactKind ArtifactKind { get; }
        public MacOSWaveSourceWriter Writer { get; }
        public MacOSPcmStream Stream { get; private set; }
        public string? LastFailureCode { get; private set; }

        public void Replace(MacOSPcmStream replacement)
        {
            LastFailureCode = Stream.FailureCode;
            Stream.FrameReady -= _frameHandler;
            Stream.Dispose();
            Stream = replacement;
            Stream.FrameReady += _frameHandler;
        }

        public void Detach()
        {
            LastFailureCode ??= Stream.FailureCode;
            Stream.FrameReady -= _frameHandler;
            Stream.Dispose();
        }
    }
}
