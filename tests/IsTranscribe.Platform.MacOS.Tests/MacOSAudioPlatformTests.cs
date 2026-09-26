using IsTranscribe.Platform.MacOS;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// Deterministic lifecycle and privacy coverage for the Core Audio boundary.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed class MacOSAudioPlatformTests
{
    [Fact]
    public async Task NativeBridgePublishesTheHostAudioInventory()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
        {
            return;
        }

        await using var platform = new MacOSAudioPlatform();
        await platform.StartAsync([], CancellationToken.None);

        Assert.True(platform.Snapshot.Capabilities.IsSupported);
        Assert.NotEmpty(platform.Snapshot.OutputDevices);
        Assert.All(platform.Snapshot.OutputDevices, static device => Assert.False(string.IsNullOrWhiteSpace(device.Id)));
    }

    [Fact]
    public async Task SnapshotPublishesCoherentDevicesProcessesAndSignals()
    {
        var native = new FakeNative
        {
            Devices =
            [
                new(1, "output", "Speakers", true, false, true, false),
                new(2, "mic", "Microphone", false, true, false, true)
            ],
            Processes = [new(10, 42, "zoom.us", "us.zoom.xos", true, true)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));

        await platform.StartAsync(["zoom.us"], CancellationToken.None);

        Assert.Single(platform.Snapshot.OutputDevices);
        Assert.Single(platform.Snapshot.Microphones);
        Assert.Equal(42, Assert.Single(platform.Snapshot.Processes).RootProcessId);
        var signal = Assert.Single(platform.Snapshot.Signals);
        Assert.True(signal.IsWatchedProcess);
        Assert.Equal("active", signal.SessionState);
        Assert.Equal("output", signal.OutputDeviceId);
        Assert.True(platform.Snapshot.Capabilities.IsSupported);
    }

    [Fact]
    public async Task ProcessExitAndDeviceChangeReplaceTheWholeSnapshot()
    {
        var native = new FakeNative
        {
            Devices = [new(1, "old", "Old", true, false, true, false)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)]
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync(["Meet"], CancellationToken.None);
        native.Devices = [new(2, "new", "New", true, false, true, false)];
        native.Processes = [];

        await platform.RefreshCapabilitiesAsync(CancellationToken.None);

        Assert.Empty(platform.Snapshot.Processes);
        Assert.Empty(platform.Snapshot.Signals);
        Assert.Equal("new", Assert.Single(platform.Snapshot.OutputDevices).Id);
    }

    [Fact]
    public async Task PermissionRevocationIsReportedAndBlocksNewMicrophoneStream()
    {
        var native = new FakeNative
        {
            Devices = [new(2, "mic", "Microphone", false, true, false, true)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        native.Permission = MacOSMicrophonePermission.Denied;

        await platform.RefreshCapabilitiesAsync(CancellationToken.None);

        Assert.Equal("microphone_permission_denied", platform.Snapshot.Capabilities.BlockingReason);
        Assert.Throws<UnauthorizedAccessException>(() => platform.StartMicrophonePcmStream("mic"));
    }

    [Fact]
    public async Task OutputAndMicrophoneFramesShareTheNativeHostTimeline()
    {
        var native = new FakeNative
        {
            Devices = [new(2, "mic", "Microphone", false, true, false, true)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        using var output = platform.StartProcessPcmStream(42);
        using var microphone = platform.StartMicrophonePcmStream("mic");

        native.EmitProcess(Frame(1_000, [0.25f, -0.25f]));
        native.EmitMicrophone(Frame(1_024, [0.5f, -0.5f]));

        Assert.True(output.TryRead(out var outputFrame));
        Assert.True(microphone.TryRead(out var microphoneFrame));
        Assert.Equal(24UL, microphoneFrame!.HostTime - outputFrame!.HostTime);
        Assert.Equal([0.25f, -0.25f], outputFrame.Samples.ToArray());
        Assert.Equal([0.5f, -0.5f], microphoneFrame.Samples.ToArray());
    }

    [Fact]
    public async Task ActiveSourcesFailAndTearDownWhenTheirAuthorityDisappears()
    {
        var native = new FakeNative
        {
            Devices = [new(2, "mic", "Microphone", false, true, false, true)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        using var output = platform.StartProcessPcmStream(42);
        using var microphone = platform.StartMicrophonePcmStream("mic");
        native.Processes = [];
        native.Permission = MacOSMicrophonePermission.Denied;

        await platform.RefreshCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(MacOSPcmStreamState.Faulted, output.State);
        Assert.Equal("process_exited", output.FailureCode);
        Assert.Equal(MacOSPcmStreamState.Faulted, microphone.State);
        Assert.Equal("microphone_permission_revoked", microphone.FailureCode);
        Assert.All(native.Captures, static capture => Assert.True(capture.Disposed));
    }

    [Fact]
    public void MemoryBufferIsBoundedAndCallbackStopsOnDispose()
    {
        var capture = new FakeCapture();
        Action<MacOSPcmFrame>? callback = null;
        using var stream = new MacOSPcmStream(4, handler =>
        {
            callback = handler;
            return capture;
        });
        callback!(Frame(1, [1f, 2f, 3f]));
        callback(Frame(2, [4f, 5f, 6f]));

        Assert.Equal(1, stream.BufferedFrameCount);
        stream.Dispose();
        callback(Frame(3, [7f]));

        Assert.True(capture.Disposed);
        Assert.Equal(0, stream.BufferedFrameCount);
    }

    [Fact]
    public async Task ManualCaptureFinalizesAlignedSourcesIntoVerifiedMp3()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-capture-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Devices = [new(2, "mic", "Microphone", false, true, false, true)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Manual,
                [
                    IsTranscribe.Core.Audio.AudioCaptureSource.ProcessOutput(42, "Meet"),
                    IsTranscribe.Core.Audio.AudioCaptureSource.Microphone("mic")
                ],
                directory,
                0,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitProcess(Tone(1_000, 440));
            native.EmitMicrophone(Tone(1_000, 660));

            await session.StopAsync(CancellationToken.None);

            Assert.Equal(IsTranscribe.Core.Audio.AudioCaptureState.Completed, session.Snapshot.State);
            Assert.Empty(session.Snapshot.Failures);
            var mixed = Assert.Single(
                session.Snapshot.Artifacts,
                static artifact => artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
            var info = new MacOSMp3Encoder().Probe(mixed.Path);
            Assert.Equal(48_000, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.True(info.Duration > TimeSpan.FromMilliseconds(90));
            Assert.True(new MacOSMp3Encoder().MeasureTone(mixed.Path, 440) > 0.05);
            Assert.True(new MacOSMp3Encoder().MeasureTone(mixed.Path, 660) > 0.05);
            Assert.True(File.Exists(Path.Combine(directory, "output.wav")));
            Assert.True(File.Exists(Path.Combine(directory, "microphone.wav")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AskPrebufferStaysMemoryOnlyUntilPromotion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-prebuffer-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)]
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Ask,
                [IsTranscribe.Core.Audio.AudioCaptureSource.ProcessOutput(42)],
                directory,
                5,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitProcess(Tone(1_000, 440));

            Assert.False(Directory.Exists(directory));
            Assert.False(session.Snapshot.IsPersistingAudio);

            await session.PromotePrebufferAsync(CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(directory, "output.wav")));
            Assert.True(session.Snapshot.IsPersistingAudio);
            await session.StopAsync(CancellationToken.None);
            Assert.Contains(session.Snapshot.Artifacts, static artifact =>
                artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MissingMicrophoneDegradesToReadableOutputOnlyArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-degraded-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Devices = [new(2, "mic", "Microphone", false, true, false, true)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)],
            Permission = MacOSMicrophonePermission.Denied
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Manual,
                [
                    IsTranscribe.Core.Audio.AudioCaptureSource.ProcessOutput(42),
                    IsTranscribe.Core.Audio.AudioCaptureSource.Microphone("mic")
                ],
                directory,
                0,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitProcess(Tone(1_000, 440));
            await session.StopAsync(CancellationToken.None);

            Assert.Equal(IsTranscribe.Core.Audio.AudioCaptureState.Completed, session.Snapshot.State);
            Assert.Contains(session.Snapshot.Failures, static failure => failure.Code == "source_start_failed");
            Assert.Contains(session.Snapshot.Artifacts, static artifact =>
                artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PauseExcludesPausedFramesFromTheFinalTimeline()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-pause-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)]
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Manual,
                [IsTranscribe.Core.Audio.AudioCaptureSource.ProcessOutput(42)],
                directory,
                0,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitProcess(Tone(1_000, 440));
            await session.PauseAsync(CancellationToken.None);
            native.EmitProcess(Tone(101_000, 880));
            await session.ResumeAsync(CancellationToken.None);
            native.EmitProcess(Tone(201_000, 440));
            await session.StopAsync(CancellationToken.None);

            var mixed = Assert.Single(
                session.Snapshot.Artifacts,
                static artifact => artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
            var encoder = new MacOSMp3Encoder();
            var info = encoder.Probe(mixed.Path);
            Assert.InRange(info.Duration.TotalSeconds, 0.18, 0.29);
            Assert.True(encoder.MeasureTone(mixed.Path, 440) > 0.05);
            Assert.True(encoder.MeasureTone(mixed.Path, 880) < 0.02);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessExitRebindsToSystemOutputAndPreservesTimelineGap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-rebind-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Devices = [new(1, "output", "Speakers", true, false, true, false)],
            Processes = [new(10, 42, "Meet", "com.google.Meet", true, false)]
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Manual,
                [IsTranscribe.Core.Audio.AudioCaptureSource.ProcessOutput(42)],
                directory,
                0,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitProcess(Tone(1_000_000_000, 440));
            native.Processes = [];
            await platform.RefreshCapabilitiesAsync(CancellationToken.None);
            native.EmitProcess(Tone(1_200_000_000, 660));

            await session.StopAsync(CancellationToken.None);

            Assert.Contains(session.Snapshot.Failures, static failure => failure.Code == "process_output_fallback");
            var mixed = Assert.Single(
                session.Snapshot.Artifacts,
                static artifact => artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
            var encoder = new MacOSMp3Encoder();
            Assert.InRange(encoder.Probe(mixed.Path).Duration.TotalSeconds, 0.34, 0.4);
            Assert.True(encoder.MeasureTone(mixed.Path, 440) > 0.02);
            Assert.True(encoder.MeasureTone(mixed.Path, 660) > 0.02);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MicrophoneDeviceChangeStartsANewLegOnTheSameTimeline()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"istranscribe-mic-rebind-{Guid.NewGuid():N}");
        var native = new FakeNative
        {
            Devices = [new(2, "mic-a", "Microphone A", false, true, false, true)],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        try
        {
            var request = new IsTranscribe.Core.Audio.AudioCaptureRequest(
                Guid.NewGuid(),
                IsTranscribe.Core.Audio.AudioCaptureMode.Manual,
                [IsTranscribe.Core.Audio.AudioCaptureSource.Microphone("mic-a")],
                directory,
                0,
                true);
            await using var session = await platform.StartCaptureAsync(request, CancellationToken.None);
            native.EmitMicrophone(Tone(1_000_000_000, 440));
            native.Devices = [new(3, "mic-b", "Microphone B", false, true, false, true)];
            await platform.RefreshCapabilitiesAsync(CancellationToken.None);
            native.EmitMicrophone(Tone(1_200_000_000, 660));
            await session.StopAsync(CancellationToken.None);

            Assert.Contains(session.Snapshot.Failures, static failure => failure.Code == "source_rebound");
            var mixed = Assert.Single(
                session.Snapshot.Artifacts,
                static artifact => artifact.Kind == IsTranscribe.Core.Audio.AudioCaptureArtifactKind.Mixed);
            var encoder = new MacOSMp3Encoder();
            Assert.True(encoder.MeasureTone(mixed.Path, 440) > 0.02);
            Assert.True(encoder.MeasureTone(mixed.Path, 660) > 0.02);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CoordinatorPromotesPrimaryArtifactAndCleansRecoverableSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-coordinator-{Guid.NewGuid():N}");
        var paths = new LocalAppPaths("isTranscribe", root);
        await using var connection = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
        var repository = new MeetingSessionRepository(connection);
        var native = new FakeNative
        {
            Devices =
            [
                new(1, "output", "Speakers", true, false, true, false),
                new(2, "mic", "Microphone", false, true, false, true)
            ],
            Permission = MacOSMicrophonePermission.Granted
        };
        await using var platform = new MacOSAudioPlatform(native, TimeSpan.FromDays(1));
        await platform.StartAsync([], CancellationToken.None);
        var settings = ApplicationSettings.Default with
        {
            ReleaseV2 = ReleaseV2Settings.Default with
            {
                RecordingsFolder = Path.Combine(root, "recordings")
            }
        };
        var coordinator = new MacOSRecordingSessionCoordinator(
            platform,
            repository,
            new ArtifactPathResolver(paths),
            () => settings,
            new BootstrapFileLogger(paths.HostLogFilePath));
        try
        {
            Assert.True(await coordinator.StartManualAsync(CancellationToken.None));
            native.EmitProcess(Tone(1_000, 440));
            native.EmitMicrophone(Tone(1_000, 660));

            Assert.True(await coordinator.FinishForRuntimeAsync(CancellationToken.None));

            var recent = Assert.Single(repository.ListRecent(10, 0));
            Assert.Equal("ready", recent.RecordingStatus);
            Assert.NotNull(recent.PrimaryAudioPath);
            Assert.True(File.Exists(recent.PrimaryAudioPath));
            Assert.Null(recent.TempSessionPath);
            Assert.Null(recent.AudioOutputPath);
            Assert.Null(recent.AudioMicPath);
            Assert.Null(recent.AudioMixPath);
            Assert.False(recent.SourceCleanupPending);
        }
        finally
        {
            await coordinator.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MacOSPcmFrame Frame(ulong hostTime, float[] samples) =>
        new(DateTimeOffset.UnixEpoch, hostTime, samples.Length, 1, 48_000, samples);

    private static MacOSPcmFrame Tone(ulong hostTime, double frequency)
    {
        const int frames = 4_800;
        var samples = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame] = (float)(Math.Sin(2 * Math.PI * frequency * frame / 48_000) * 0.25);
        }

        return Frame(hostTime, samples);
    }

    private sealed class FakeNative : IMacOSAudioNative
    {
        private Action<MacOSPcmFrame>? _processCallback;
        private Action<MacOSPcmFrame>? _microphoneCallback;

        public MacOSMicrophonePermission Permission { get; set; } = MacOSMicrophonePermission.Undetermined;
        public double HostTicksPerSecond => 1_000_000_000;
        public IReadOnlyList<MacOSNativeProcess> Processes { get; set; } = [];
        public IReadOnlyList<MacOSNativeDevice> Devices { get; set; } = [];
        public List<FakeCapture> Captures { get; } = [];
        public MacOSMicrophonePermission MicrophonePermission => Permission;
        public IReadOnlyList<MacOSNativeProcess> EnumerateProcesses() => Processes;
        public IReadOnlyList<MacOSNativeDevice> EnumerateDevices() => Devices;

        public IMacOSNativeCapture StartProcessCapture(int processId, Action<MacOSPcmFrame> onFrame)
        {
            _processCallback = onFrame;
            var capture = new FakeCapture();
            Captures.Add(capture);
            return capture;
        }

        public IMacOSNativeCapture StartSystemOutputCapture(Action<MacOSPcmFrame> onFrame) =>
            StartProcessCapture(0, onFrame);

        public IMacOSNativeCapture StartMicrophoneCapture(string deviceId, Action<MacOSPcmFrame> onFrame)
        {
            _microphoneCallback = onFrame;
            var capture = new FakeCapture();
            Captures.Add(capture);
            return capture;
        }

        public void EmitProcess(MacOSPcmFrame frame) => _processCallback!(frame);
        public void EmitMicrophone(MacOSPcmFrame frame) => _microphoneCallback!(frame);
    }

    private sealed class FakeCapture : IMacOSNativeCapture
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
