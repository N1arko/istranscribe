using System.Reflection;
using IsTranscribe.App.AutomaticRecording;
using IsTranscribe.App.ManualControls;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.App.Tests;

public sealed class ManualControlGatewayDeviceContinuityTests
{
    [Fact]
    public async Task SeamlessSwitchKeepsSessionIdentity()
    {
        using var harness = await DeviceContinuityHarness.CreateAsync(ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            Devices = ApplicationSettings.Default.Devices with
            {
                FollowSystemDefaultOutput = true,
                OutputChangePolicy = "seamless_switch",
                ActiveRecordingDevicePolicy = "seamless_switch"
            }
        });

        await harness.Gateway.StartForceRecordAsync();
        var before = harness.Gateway.Snapshot.CurrentRecording;
        Assert.NotNull(before);

        harness.DeviceManager.UpdateSnapshot(
            renderDevices:
            [
                new AudioDeviceSnapshot("render-b", "Speaker B", AudioDeviceKind.Render, IsDefault: true, IsActive: true)
            ],
            captureDevices:
            [
                new AudioDeviceSnapshot("mic-a", "Mic A", AudioDeviceKind.Capture, IsDefault: true, IsActive: true)
            ]);

        await harness.Gateway.HandleDeviceInventoryChangedAsync();

        var after = harness.Gateway.Snapshot.CurrentRecording;
        Assert.NotNull(after);
        Assert.Equal(before!.SessionId, after!.SessionId);
        Assert.Equal("Speaker B", after.OutputDeviceName);
    }

    [Fact]
    public async Task AskPolicyUsesPinnedFallbackWhenPromptTimesOut()
    {
        var prompt = new FakePromptService();
        prompt.Enqueue(AutomaticPromptDecision.Cancelled);

        using var harness = await DeviceContinuityHarness.CreateAsync(ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            Recording = ApplicationSettings.Default.Recording with
            {
                DefaultSourcesForce = ["device_loopback", "mic"]
            },
            Devices = ApplicationSettings.Default.Devices with
            {
                FollowSystemDefaultOutput = false,
                OutputDeviceId = "render-a",
                OutputChangePolicy = "ask",
                ActiveRecordingDevicePolicy = "ask"
            }
        }, prompt);

        await harness.Gateway.StartForceRecordAsync();
        var before = harness.Gateway.Snapshot.CurrentRecording;
        Assert.NotNull(before);

        harness.DeviceManager.UpdateSnapshot(
            renderDevices:
            [
                new AudioDeviceSnapshot("render-b", "Speaker B", AudioDeviceKind.Render, IsDefault: true, IsActive: true)
            ],
            captureDevices:
            [
                new AudioDeviceSnapshot("mic-a", "Mic A", AudioDeviceKind.Capture, IsDefault: true, IsActive: true)
            ]);

        await harness.Gateway.HandleDeviceInventoryChangedAsync();

        var after = harness.Gateway.Snapshot.CurrentRecording;
        Assert.NotNull(after);
        Assert.NotEqual(before!.SessionId, after!.SessionId);
        Assert.Equal(1, prompt.CallCount);
    }

    private sealed class DeviceContinuityHarness : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly SqliteConnection _connection;

        private DeviceContinuityHarness(
            string rootDirectory,
            SqliteConnection connection,
            ManualControlGateway gateway,
            FakeAudioDeviceManager deviceManager)
        {
            _rootDirectory = rootDirectory;
            _connection = connection;
            Gateway = gateway;
            DeviceManager = deviceManager;
        }

        public ManualControlGateway Gateway { get; }

        public FakeAudioDeviceManager DeviceManager { get; }

        public static async Task<DeviceContinuityHarness> CreateAsync(ApplicationSettings settings, IAutomaticPromptService? promptService = null)
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), "isTranscribe-device-tests", Guid.NewGuid().ToString("N"));
            var paths = new LocalAppPaths("isTranscribe-tests", rootDirectory);
            var logger = new BootstrapFileLogger(Path.Combine(paths.LogsDirectory, "test.log"));
            var initializer = new SqliteDatabaseInitializer(paths);
            var connection = await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var meetingSessionRepository = new MeetingSessionRepository(connection);
            var deviceManager = new FakeAudioDeviceManager();
            var recorderEngine = new FakeAudioRecorderEngine(logger);
            var gateway = new ManualControlGateway(logger, recorderEngine, deviceManager, promptService, new ArtifactPathResolver(paths), new AudioCompressor(logger), paths);
            gateway.UpdateHostContext(settings, new HostCapabilitySnapshot(HostCapabilityState.Full, true, "full"), meetingSessionRepository);
            return new DeviceContinuityHarness(rootDirectory, connection, gateway, deviceManager);
        }

        public void Dispose()
        {
            _connection.Dispose();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeAudioDeviceManager : IAudioDeviceManager
    {
        public event EventHandler<AudioDeviceInventorySnapshot>? SnapshotChanged;

        public AudioDeviceInventorySnapshot CurrentSnapshot { get; private set; } = new(
            DateTimeOffset.UtcNow,
            [new AudioDeviceSnapshot("render-a", "Speaker A", AudioDeviceKind.Render, IsDefault: true, IsActive: true)],
            [new AudioDeviceSnapshot("mic-a", "Mic A", AudioDeviceKind.Capture, IsDefault: true, IsActive: true)]);

        public void UpdateSnapshot(IReadOnlyList<AudioDeviceSnapshot> renderDevices, IReadOnlyList<AudioDeviceSnapshot> captureDevices)
        {
            CurrentSnapshot = new AudioDeviceInventorySnapshot(DateTimeOffset.UtcNow, renderDevices, captureDevices);
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
        }

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePromptService : IAutomaticPromptService
    {
        private readonly Queue<AutomaticPromptDecision> _decisions = new();

        public int CallCount { get; private set; }

        public void Enqueue(AutomaticPromptDecision decision) => _decisions.Enqueue(decision);

        public Task<AutomaticPromptDecision> ShowAsync(AutomaticPromptRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            var decision = _decisions.Count > 0
                ? _decisions.Dequeue()
                : AutomaticPromptDecision.Cancelled;
            return Task.FromResult(decision);
        }
    }

    private sealed class FakeAudioRecorderEngine(BootstrapFileLogger logger) : IAudioRecorderEngine
    {
        private readonly BootstrapFileLogger _logger = logger;

        public ValueTask<AudioRecorderSession> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken)
        {
            var session = CreateSession(request, request.Mode != AudioCaptureMode.Ask || request.PrebufferSeconds == 0);
            return ValueTask.FromResult(session);
        }

        private AudioRecorderSession CreateSession(AudioCaptureRequest request, bool isPersistingAudio)
        {
            var hostAssembly = typeof(AudioRecorderSession).Assembly;
            var adapterType = hostAssembly.GetType("IsTranscribe.Host.Audio.Capture.IAudioCaptureAdapter")
                ?? throw new InvalidOperationException("IAudioCaptureAdapter type was not found.");
            var adapters = Array.CreateInstance(adapterType, 0);
            var mixBuilderType = hostAssembly.GetType("IsTranscribe.Host.Audio.Capture.AudioMixArtifactBuilder")
                ?? throw new InvalidOperationException("AudioMixArtifactBuilder type was not found.");
            var mixBuilder = Activator.CreateInstance(mixBuilderType, nonPublic: true)
                ?? throw new InvalidOperationException("AudioMixArtifactBuilder could not be created.");

            var constructor = typeof(AudioRecorderSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();

            return (AudioRecorderSession)(constructor.Invoke([_logger, request, adapters, mixBuilder, isPersistingAudio]));
        }
    }
}
