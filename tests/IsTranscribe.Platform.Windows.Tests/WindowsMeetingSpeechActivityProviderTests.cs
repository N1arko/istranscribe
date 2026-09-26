using System.Buffers.Binary;
using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Platform.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMeetingSpeechActivityProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    [Fact]
    public async Task MicrophoneCaptureHealthRecoversAfterRetrySucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new RetryableMicrophoneCaptureFactory { FailMicrophone = true };
            var provider = new WindowsMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            var audio = new AudioPlatformSnapshot(
                Now,
                new AudioPlatformCapabilities(true, true, "test"),
                [],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)],
                [],
                []);
            try
            {
                await provider.StartAsync(CancellationToken.None);
                await provider.ApplyObservationDemandAsync(
                    new MeetingSpeechObservationDemand([], ObserveMicrophone: true),
                    audio,
                    CancellationToken.None);

                var failed = await provider.ObserveAsync(audio, CancellationToken.None);
                Assert.Equal(
                    MeetingSpeechCaptureHealth.Unavailable,
                    failed.MicrophoneCaptureHealth);

                factory.FailMicrophone = false;
                var recovered = await provider.ObserveAsync(
                    audio with { ObservedAtUtc = Now.AddSeconds(6) },
                    CancellationToken.None);

                Assert.Equal(
                    MeetingSpeechCaptureHealth.Available,
                    recovered.MicrophoneCaptureHealth);
                Assert.Equal(2, factory.MicrophoneStartCount);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    [Fact]
    public async Task MicrophoneHealthOnlyObservationDoesNotStartRenderCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-health-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new RetryableMicrophoneCaptureFactory();
            var provider = new WindowsMeetingSpeechActivityProvider(
                new BootstrapFileLogger(Path.Combine(root, "host.log")),
                factory);
            var audio = new AudioPlatformSnapshot(
                Now,
                new AudioPlatformCapabilities(true, true, "test"),
                [new AudioEndpointSnapshot("output", "Speakers", true, true)],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)],
                [],
                [Signal("zoom.exe", 42, -24, "output")]);
            try
            {
                await provider.StartAsync(CancellationToken.None);

                var health = await provider.ObserveMicrophoneCaptureHealthAsync(
                    audio,
                    CancellationToken.None);

                Assert.Equal(MeetingSpeechCaptureHealth.Available, health);
                Assert.Equal(1, factory.MicrophoneStartCount);
                Assert.True(factory.LastCapture!.IsDisposed);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task IdleProviderDoesNotStartRenderOrMicrophoneCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-idle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new FakeObservationCaptureFactory();
        var provider = new WindowsMeetingSpeechActivityProvider(
            new BootstrapFileLogger(Path.Combine(root, "host.log")),
            factory);
        try
        {
            await provider.StartAsync(CancellationToken.None);
            var audio = new AudioPlatformSnapshot(
                Now,
                new AudioPlatformCapabilities(true, true, "test"),
                [new AudioEndpointSnapshot("output", "Speakers", true, true)],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)],
                [new ObservedProcessSnapshot(42, "zen.exe", new HashSet<int> { 42 })],
                [Signal("zen.exe", 42, -24, "output")]);

            var observed = await provider.ObserveAsync(audio, CancellationToken.None);

            Assert.Empty(factory.AllCaptures);
            Assert.Empty(observed.RenderByRootProcessId);
            Assert.Equal(MeetingSpeechCaptureHealth.Unknown, observed.MicrophoneCaptureHealth);
        }
        finally
        {
            await provider.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    [Fact]
    public async Task RemovingDemandDisposesRenderAndMicrophoneCaptures()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-demand-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new FakeObservationCaptureFactory();
        var provider = new WindowsMeetingSpeechActivityProvider(
            new BootstrapFileLogger(Path.Combine(root, "host.log")),
            factory);
        try
        {
            await provider.StartAsync(CancellationToken.None);
            var audio = new AudioPlatformSnapshot(
                Now,
                new AudioPlatformCapabilities(true, true, "test"),
                [new AudioEndpointSnapshot("output", "Speakers", true, true)],
                [new AudioEndpointSnapshot("mic", "Microphone", true, true)],
                [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 })],
                [Signal("zoom.exe", 42, -24, "output")]);
            await provider.ApplyObservationDemandAsync(
                new MeetingSpeechObservationDemand([42], ObserveMicrophone: true),
                audio,
                CancellationToken.None);

            await provider.ApplyObservationDemandAsync(
                MeetingSpeechObservationDemand.Empty,
                audio,
                CancellationToken.None);

            Assert.Equal(2, factory.AllCaptures.Count);
            Assert.All(factory.AllCaptures, static capture => Assert.True(capture.IsDisposed));
        }
        finally
        {
            await provider.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FloatStereoPacketIsReducedToMono()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var buffer = new byte[4 * 4];
        WriteFloat(buffer, 0, 0.5f);
        WriteFloat(buffer, 4, -0.5f);
        WriteFloat(buffer, 8, 0.25f);
        WriteFloat(buffer, 12, 0.75f);

        var decoded = WindowsAudioSampleDecoder.DecodeMono(buffer, buffer.Length, format);

        Assert.Equal([0f, 0.5f], decoded);
    }

    [Fact]
    public void Pcm16PacketIsReducedToMono()
    {
        var format = new WaveFormat(16_000, 16, 2);
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(4, 2), short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(6, 2), short.MinValue);

        var decoded = WindowsAudioSampleDecoder.DecodeMono(buffer, buffer.Length, format);

        Assert.InRange(decoded[0], 0.999f, 1f);
        Assert.Equal(-1f, decoded[1]);
    }

    [Fact]
    public void SustainedRenderSpeechIsAttributedOnlyToRecentlyAudibleRoot()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(4), 0.6);
        var audio = Platform(
            new AudioSignalSnapshot(
                Now,
                "zoom.exe",
                RootProcessId: 42,
                "AudioSessionStateActive",
                SignalLevelDbfs: -24,
                OutputDeviceId: "output",
                IsWatchedProcess: true,
                IsProcessTreeMatch: true),
            new AudioSignalSnapshot(
                Now,
                "player.exe",
                RootProcessId: 84,
                "AudioSessionStateActive",
                SignalLevelDbfs: -80,
                OutputDeviceId: "output",
                IsWatchedProcess: false,
                IsProcessTreeMatch: false));

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);

        Assert.True(snapshot.RenderByRootProcessId.ContainsKey(42));
        Assert.False(snapshot.RenderByRootProcessId.ContainsKey(84));
    }

    [Fact]
    public void RootAttributionExpiresAfterHoldWindow()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>
        {
            [42] = new("output", -24, Now)
        };
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(4), 0.6);

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            Platform(),
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now.AddSeconds(2));

        Assert.Empty(snapshot.RenderByRootProcessId);
    }

    [Fact]
    public void RenderSpeechUsesTheMatchingOutputDevice()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(4), 0.6);
        var audio = Platform(
            new AudioSignalSnapshot(
                Now,
                "zoom.exe",
                RootProcessId: 42,
                "AudioSessionStateActive",
                SignalLevelDbfs: -24,
                OutputDeviceId: "headset",
                IsWatchedProcess: true,
                IsProcessTreeMatch: true),
            new AudioSignalSnapshot(
                Now,
                "teams.exe",
                RootProcessId: 84,
                "AudioSessionStateActive",
                SignalLevelDbfs: -24,
                OutputDeviceId: "speakers",
                IsWatchedProcess: true,
                IsProcessTreeMatch: true));

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary>
            {
                ["headset"] = speech,
                ["speakers"] = MeetingSpeechActivitySummary.Empty
            },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "headset" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);

        Assert.True(snapshot.RenderByRootProcessId.ContainsKey(42));
        Assert.False(snapshot.RenderByRootProcessId.ContainsKey(84));
    }

    [Fact]
    public void RenderSpeechFollowsAProcessAcrossOutputDeviceSwitch()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.85, true, TimeSpan.FromSeconds(4), 0.65);

        var beforeSwitch = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            Platform(Signal("zoom.exe", 42, -22, "headset")),
            new Dictionary<string, MeetingSpeechActivitySummary>
            {
                ["headset"] = speech,
                ["speakers"] = MeetingSpeechActivitySummary.Empty
            },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "headset" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);
        var afterSwitch = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            Platform(Signal("zoom.exe", 42, -20, "speakers")),
            new Dictionary<string, MeetingSpeechActivitySummary>
            {
                ["headset"] = MeetingSpeechActivitySummary.Empty,
                ["speakers"] = speech
            },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "speakers" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now.AddSeconds(1));

        Assert.True(beforeSwitch.RenderByRootProcessId.ContainsKey(42));
        Assert.True(afterSwitch.RenderByRootProcessId.ContainsKey(42));
        Assert.Equal("speakers", recent[42].OutputDeviceId);
    }

    [Fact]
    public async Task CapturePlannerRebindsRenderObservationAcrossOutputDeviceSwitch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new FakeObservationCaptureFactory();
        var provider = new WindowsMeetingSpeechActivityProvider(
            new BootstrapFileLogger(Path.Combine(root, "host.log")),
            factory);
        try
        {
            await provider.StartAsync(CancellationToken.None);
            await provider.ApplyObservationDemandAsync(
                new MeetingSpeechObservationDemand([42], ObserveMicrophone: false),
                PlatformWithOutput("headset", Signal("zoom.exe", 42, -22, "headset"), Now),
                CancellationToken.None);
            await provider.ObserveAsync(
                PlatformWithOutput("headset", Signal("zoom.exe", 42, -22, "headset"), Now),
                CancellationToken.None);
            var headsetCapture = factory.Captures["headset"];
            Assert.False(headsetCapture.IsDisposed);

            await provider.ObserveAsync(
                PlatformWithOutput(
                    "speakers",
                    Signal("zoom.exe", 42, -20, "speakers"),
                    Now.AddSeconds(1)),
                CancellationToken.None);

            Assert.True(headsetCapture.IsDisposed);
            Assert.False(factory.Captures["speakers"].IsDisposed);
            Assert.Equal(["headset", "speakers"], factory.StartedEndpointIds);
        }
        finally
        {
            await provider.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }

        Assert.True(factory.Captures["speakers"].IsDisposed);
    }

    [Fact]
    public void ComparableRootsOnOneEndpointDoNotShareSpeechEvidence()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.9, true, TimeSpan.FromSeconds(4), 0.7);
        var audio = Platform(
            Signal("zoom.exe", 42, -24, "output"),
            Signal("player.exe", 84, -29, "output"));

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);

        Assert.Empty(snapshot.RenderByRootProcessId);
        Assert.Empty(snapshot.ConversationalAlternationByRootProcessId);
    }

    [Fact]
    public void DominantRootReceivesSharedEndpointSpeechEvidence()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.9, true, TimeSpan.FromSeconds(4), 0.7);
        var audio = Platform(
            Signal("zoom.exe", 42, -22, "output"),
            Signal("player.exe", 84, -38, "output"));

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);

        Assert.True(snapshot.RenderByRootProcessId.ContainsKey(42));
        Assert.False(snapshot.RenderByRootProcessId.ContainsKey(84));
    }

    [Fact]
    public void RenderSpeechOriginTracksTheDominantDescendantSession()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.9, true, TimeSpan.FromSeconds(4), 0.7);
        var descendant = Signal("zoom.exe", 42, -20, "output");
        var root = descendant with
        {
            SignalLevelDbfs = -40,
            IsProcessTreeMatch = false
        };

        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            Platform(root, descendant),
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);

        Assert.True(snapshot.RenderByRootProcessId.ContainsKey(42));
        Assert.True(snapshot.RenderUsesDescendantProcessByRootProcessId![42]);
    }

    [Fact]
    public void ConversationAlternationIsAttributedToItsRenderRoot()
    {
        var recent = new Dictionary<int, WindowsMeetingSpeechActivityProvider.RecentRootAudioActivity>();
        var trackers = new Dictionary<int, MeetingConversationAlternationTracker>();
        var speech = new MeetingSpeechActivitySummary(0.9, true, TimeSpan.FromSeconds(4), 0.7);
        var audio = Platform(Signal("zoom.exe", 42, -22, "output"));

        WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now);
        WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            new MeetingSpeechActivitySummary(0.8, true, TimeSpan.FromSeconds(1), 0.5),
            new HashSet<string>(),
            microphoneSpeechNow: true,
            recent,
            trackers,
            Now.AddSeconds(1));
        var snapshot = WindowsMeetingSpeechActivityProvider.BuildSnapshot(
            audio,
            new Dictionary<string, MeetingSpeechActivitySummary> { ["output"] = speech },
            MeetingSpeechActivitySummary.Empty,
            new HashSet<string> { "output" },
            microphoneSpeechNow: false,
            recent,
            trackers,
            Now.AddSeconds(2));

        Assert.True(snapshot.ConversationalAlternationByRootProcessId[42] > 0);
    }

    [Fact]
    public void AlternatingExclusiveTurnsCreateConversationEvidence()
    {
        var tracker = new MeetingConversationAlternationTracker();

        Assert.Equal(0, tracker.Observe(Now, renderSpeech: true, microphoneSpeech: false));
        Assert.Equal(0, tracker.Observe(Now.AddSeconds(1), renderSpeech: false, microphoneSpeech: true));
        Assert.True(tracker.Observe(Now.AddSeconds(2), renderSpeech: true, microphoneSpeech: false) > 0);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveDefaultEndpointsStartWithoutPersistingAudio()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LIVE_AUDIO"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-vad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "host.log");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var audio = new AudioPlatformSnapshot(
                DateTimeOffset.UtcNow,
                new AudioPlatformCapabilities(true, true, "live"),
                [new AudioEndpointSnapshot(render.ID, render.FriendlyName, true, true)],
                [new AudioEndpointSnapshot(microphone.ID, microphone.FriendlyName, true, true)],
                [],
                []);

            var provider = new WindowsMeetingSpeechActivityProvider(new BootstrapFileLogger(logPath));
            try
            {
                await provider.StartAsync(CancellationToken.None);
                await provider.ApplyObservationDemandAsync(
                    new MeetingSpeechObservationDemand([42], ObserveMicrophone: true),
                    audio,
                    CancellationToken.None);
                await provider.ObserveAsync(audio, CancellationToken.None);
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                await provider.ObserveAsync(
                    audio with { ObservedAtUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None);
            }
            finally
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                static path => Path.GetExtension(path) is ".wav" or ".ogg" or ".mp3" or ".m4a");
            var log = File.ReadAllText(logPath);
            Assert.DoesNotContain("DETECTION_RENDER_VAD_DEGRADED", log, StringComparison.Ordinal);
            Assert.DoesNotContain("DETECTION_MIC_VAD_DEGRADED", log, StringComparison.Ordinal);
            var logLines = File.ReadAllLines(logPath);
            Assert.Contains(logLines, static line =>
                line.Contains("\"event_code\":\"DETECTION_VAD_CAPTURE_STOPPED\"", StringComparison.Ordinal) &&
                line.Contains("\"channel\":\"render\"", StringComparison.Ordinal));
            Assert.Contains(logLines, static line =>
                line.Contains("\"event_code\":\"DETECTION_VAD_CAPTURE_STOPPED\"", StringComparison.Ordinal) &&
                line.Contains("\"channel\":\"microphone\"", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AudioPlatformSnapshot Platform(params AudioSignalSnapshot[] signals) => new(
        Now,
        new AudioPlatformCapabilities(true, true, "test"),
        [],
        [],
        [],
        signals);

    private static AudioPlatformSnapshot PlatformWithOutput(
        string outputDeviceId,
        AudioSignalSnapshot signal,
        DateTimeOffset observedAtUtc) => new(
            observedAtUtc,
            new AudioPlatformCapabilities(true, true, "test"),
            [new AudioEndpointSnapshot(outputDeviceId, outputDeviceId, true, true)],
            [],
            [new ObservedProcessSnapshot(42, "zoom.exe", new HashSet<int> { 42 })],
            [signal with { ObservedAtUtc = observedAtUtc }]);

    private static AudioSignalSnapshot Signal(
        string processName,
        int rootProcessId,
        double levelDbfs,
        string outputDeviceId) => new(
            Now,
            processName,
            rootProcessId,
            "AudioSessionStateActive",
            levelDbfs,
            outputDeviceId,
            IsWatchedProcess: true,
            IsProcessTreeMatch: true);

    private static void WriteFloat(byte[] buffer, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(offset, sizeof(float)),
            BitConverter.SingleToInt32Bits(value));

    private sealed class FakeObservationCaptureFactory : IWindowsSpeechObservationCaptureFactory
    {
        public Dictionary<string, FakeObservationCapture> Captures { get; } = new(StringComparer.Ordinal);

        public List<string> StartedEndpointIds { get; } = [];

        public List<FakeObservationCapture> AllCaptures { get; } = [];

        public IWindowsSpeechObservationCapture Start(
            string endpointId,
            bool isRender,
            BootstrapFileLogger logger)
        {
            var capture = new FakeObservationCapture(endpointId);
            AllCaptures.Add(capture);
            if (isRender)
            {
                Captures[endpointId] = capture;
                StartedEndpointIds.Add(endpointId);
            }
            return capture;
        }
    }

    private sealed class RetryableMicrophoneCaptureFactory : IWindowsSpeechObservationCaptureFactory
    {
        public bool FailMicrophone { get; set; }

        public int MicrophoneStartCount { get; private set; }

        public FakeObservationCapture? LastCapture { get; private set; }

        public IWindowsSpeechObservationCapture Start(
            string endpointId,
            bool isRender,
            BootstrapFileLogger logger)
        {
            Assert.False(isRender);
            MicrophoneStartCount++;
            if (FailMicrophone)
            {
                throw new UnauthorizedAccessException("Injected microphone access denial.");
            }

            LastCapture = new FakeObservationCapture(endpointId);
            return LastCapture;
        }
    }

    private sealed class FakeObservationCapture(string endpointId) : IWindowsSpeechObservationCapture
    {
        public string EndpointId { get; } = endpointId;

        public bool IsActive => !IsDisposed;

        public bool IsDisposed { get; private set; }

        public SpeechActivityEstimator Estimator { get; } = new();

        public void Dispose()
        {
            IsDisposed = true;
            Estimator.Reset();
        }
    }
}
