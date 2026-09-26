using System.Reflection;
using IsTranscribe.App.AutomaticRecording;
using IsTranscribe.App.ManualControls;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.App.Tests;

public sealed class AutomaticRecordingCoordinatorTests
{
    [Fact]
    public async Task KnownAskCandidateWaitsForStartDelayAndResetsSuppressionAfterMeetingEnds()
    {
        using var harness = await CoordinatorHarness.CreateAsync(settings: CreateSettings(mode: "ask"));
        harness.SetAppRules([AppRuleRecord.Create("Zoom", "zoom.exe")]);
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);

        await harness.TickAsync();
        Assert.Equal(0, harness.PromptService.ShowCount);

        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync();
        Assert.Equal(0, harness.PromptService.ShowCount);

        harness.Advance(TimeSpan.FromSeconds(1.1));
        await harness.TickAsync();
        Assert.Equal(1, harness.PromptService.ShowCount);

        await harness.TickAsync();
        Assert.Equal(0, harness.CountMeetingSessions());
        Assert.Equal(RecordingActivityState.Idle, harness.Gateway.Snapshot.RecordingState);

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.TickAsync();
        Assert.Equal(1, harness.PromptService.ShowCount);

        harness.SetObservedSessions([]);
        harness.Advance(TimeSpan.FromSeconds(20.1));
        await harness.TickAsync();

        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 202, isWhitelisted: true)]);
        await harness.TickAsync();
        Assert.Equal(1, harness.PromptService.ShowCount);

        harness.Advance(TimeSpan.FromSeconds(2.1));
        await harness.TickAsync();
        Assert.Equal(2, harness.PromptService.ShowCount);
    }

    [Fact]
    public async Task ModeOffBlocksAutomaticLifecycle()
    {
        using var harness = await CoordinatorHarness.CreateAsync(settings: CreateSettings(mode: "off"));
        harness.SetAppRules([AppRuleRecord.Create("Zoom", "zoom.exe")]);
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.TickAsync();
        await harness.TickAsync();

        Assert.Equal(0, harness.PromptService.ShowCount);
        Assert.Null(harness.Gateway.Snapshot.CurrentRecording);
        Assert.Equal(RecordingActivityState.Idle, harness.Gateway.Snapshot.RecordingState);
    }

    [Fact]
    public async Task PrivacyPauseCancelsPendingPrompt()
    {
        using var harness = await CoordinatorHarness.CreateAsync(settings: CreateSettings(mode: "ask"));
        harness.SetAppRules([AppRuleRecord.Create("Zoom", "zoom.exe")]);
        harness.PromptService.EnqueuePendingDecision();
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);

        await harness.TickAsync();
        harness.Advance(TimeSpan.FromSeconds(2.1));
        await harness.TickAsync();

        Assert.Equal(RecordingActivityState.AwaitingConfirmation, harness.Gateway.Snapshot.RecordingState);

        await harness.Gateway.TogglePrivacyPauseAsync();
        await harness.TickAsync();

        Assert.True(harness.PromptService.CancellationObserved);
        Assert.Equal(RecordingActivityState.Idle, harness.Gateway.Snapshot.RecordingState);
        Assert.True(harness.Gateway.Snapshot.PrivacyPauseEnabled);
    }

    [Fact]
    public async Task AutoRecordingResumesWithinMergeWindowUsingSameSessionId()
    {
        using var harness = await CoordinatorHarness.CreateAsync(settings: CreateSettings(mode: "auto"));
        harness.SetAppRules([AppRuleRecord.Create("Zoom", "zoom.exe")]);
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);

        await harness.TickAsync();
        harness.Advance(TimeSpan.FromSeconds(2.1));
        await harness.TickAsync();

        var startedSessionId = harness.Gateway.Snapshot.CurrentRecording?.SessionId;
        Assert.NotNull(startedSessionId);
        Assert.Equal(RecordingActivityState.Recording, harness.Gateway.Snapshot.RecordingState);

        harness.SetObservedSessions([]);
        await harness.TickAsync();
        harness.Advance(TimeSpan.FromSeconds(20.1));
        await harness.TickAsync();

        Assert.Equal(RecordingActivityState.Paused, harness.Gateway.Snapshot.RecordingState);
        Assert.Equal(startedSessionId, harness.Gateway.Snapshot.CurrentRecording?.SessionId);

        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);
        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync();

        Assert.Equal(RecordingActivityState.Recording, harness.Gateway.Snapshot.RecordingState);
        Assert.Equal(startedSessionId, harness.Gateway.Snapshot.CurrentRecording?.SessionId);
    }

    [Fact]
    public async Task MergePendingSessionDoesNotResumeWhenSourceFamilyChanges()
    {
        using var harness = await CoordinatorHarness.CreateAsync(settings: CreateSettings(
            mode: "auto",
            defaultSourcesAuto: ["process_output", "device_loopback", "mic"]));
        harness.SetAppRules([AppRuleRecord.Create("Zoom", "zoom.exe")]);
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);

        await harness.TickAsync();
        harness.Advance(TimeSpan.FromSeconds(2.1));
        await harness.TickAsync();

        var startedSessionId = harness.Gateway.Snapshot.CurrentRecording?.SessionId;
        Assert.NotNull(startedSessionId);
        Assert.Equal(RecordingActivityState.Recording, harness.Gateway.Snapshot.RecordingState);

        harness.SetObservedSessions([]);
        await harness.TickAsync();
        harness.Advance(TimeSpan.FromSeconds(20.1));
        await harness.TickAsync();

        Assert.Equal(RecordingActivityState.Paused, harness.Gateway.Snapshot.RecordingState);

        harness.UpdateCapability(new HostCapabilitySnapshot(HostCapabilityState.Degraded, false, "degraded"));
        Assert.Equal("process+mic", harness.CurrentMergeSourceFamily);
        Assert.Equal("device+mic", harness.ResolveAutomaticMergeSourceFamily(101));
        harness.SetObservedSessions([harness.CreateObservation("zoom.exe", 101, isWhitelisted: true)]);
        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync();

        Assert.Equal(RecordingActivityState.Paused, harness.Gateway.Snapshot.RecordingState);
        Assert.Equal(startedSessionId, harness.Gateway.Snapshot.CurrentRecording?.SessionId);
    }

    private static ApplicationSettings CreateSettings(string mode, string[]? defaultSourcesAuto = null) =>
        ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            Recording = ApplicationSettings.Default.Recording with
            {
                Mode = mode,
                DefaultSourcesAuto = defaultSourcesAuto ?? ["mic"],
                StartDelaySeconds = 2,
                StopDelaySeconds = 20,
                MergeWindowSeconds = 60,
                PrebufferSeconds = 5
            }
        };

    private sealed class CoordinatorHarness : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly SqliteConnection _connection;
        private readonly FakeTimeProvider _timeProvider;
        private readonly FakeProcessWatcher _processWatcher;
        private readonly FakeSessionWatcher _sessionWatcher;
        private readonly AppRuleRepository _appRuleRepository;
        private readonly MeetingSessionRepository _meetingSessionRepository;

        private CoordinatorHarness(
            string rootDirectory,
            SqliteConnection connection,
            FakeTimeProvider timeProvider,
            FakeProcessWatcher processWatcher,
            FakeSessionWatcher sessionWatcher,
            AppRuleRepository appRuleRepository,
            MeetingSessionRepository meetingSessionRepository,
            AutomaticRecordingCoordinator coordinator,
            ManualControlGateway gateway,
            FakePromptService promptService)
        {
            _rootDirectory = rootDirectory;
            _connection = connection;
            _timeProvider = timeProvider;
            _processWatcher = processWatcher;
            _sessionWatcher = sessionWatcher;
            _appRuleRepository = appRuleRepository;
            _meetingSessionRepository = meetingSessionRepository;
            Coordinator = coordinator;
            Gateway = gateway;
            PromptService = promptService;
        }

        public AutomaticRecordingCoordinator Coordinator { get; }

        public ManualControlGateway Gateway { get; }

        public FakePromptService PromptService { get; }

        public static async Task<CoordinatorHarness> CreateAsync(ApplicationSettings settings)
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), "isTranscribe-app-tests", Guid.NewGuid().ToString("N"));
            var paths = new LocalAppPaths("isTranscribe-tests", rootDirectory);
            var logger = new BootstrapFileLogger(Path.Combine(paths.LogsDirectory, "test.log"));
            var initializer = new SqliteDatabaseInitializer(paths);
            var connection = await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var appRuleRepository = new AppRuleRepository(connection);
            var meetingSessionRepository = new MeetingSessionRepository(connection);
            var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-08T12:00:00Z"));
            var deviceManager = new FakeAudioDeviceManager();
            var recorderEngine = new FakeAudioRecorderEngine(logger);
            var gateway = new ManualControlGateway(logger, recorderEngine, deviceManager, promptService: null, new ArtifactPathResolver(paths), new AudioCompressor(logger), paths, timeProvider);
            var capability = new HostCapabilitySnapshot(HostCapabilityState.Full, true, "full");
            gateway.UpdateHostContext(settings, capability, meetingSessionRepository);

            var processWatcher = new FakeProcessWatcher();
            var sessionWatcher = new FakeSessionWatcher();
            var promptService = new FakePromptService();
            var coordinator = new AutomaticRecordingCoordinator(
                logger,
                new FakeSettingsStore(settings),
                appRuleRepository,
                processWatcher,
                sessionWatcher,
                gateway,
                promptService,
                timeProvider);
            coordinator.UpdateHostContext(settings, capability, []);

            return new CoordinatorHarness(
                rootDirectory,
                connection,
                timeProvider,
                processWatcher,
                sessionWatcher,
                appRuleRepository,
                meetingSessionRepository,
                coordinator,
                gateway,
                promptService);
        }

        public void SetObservedSessions(IReadOnlyList<AudioSignalObservationSnapshot> snapshots)
        {
            _sessionWatcher.CurrentSnapshot = snapshots;
        }

        public AudioSignalObservationSnapshot CreateObservation(string processName, int processId, bool isWhitelisted) =>
            new(
                _timeProvider.GetUtcNow(),
                processName,
                processId,
                "AudioSessionStateActive",
                -10,
                "render-default",
                isWhitelisted,
                IsProcessTreeMatch: false);

        public void Advance(TimeSpan amount) => _timeProvider.Advance(amount);

        public async Task TickAsync()
        {
            var method = typeof(AutomaticRecordingCoordinator).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TickAsync method was not found.");
            var task = (Task?)method.Invoke(Coordinator, [CancellationToken.None])
                ?? throw new InvalidOperationException("TickAsync invocation did not return a task.");
            await task.ConfigureAwait(false);
        }

        public void SetAppRules(IReadOnlyList<AppRuleRecord> rules)
        {
            _appRuleRepository.ReplaceAllAsync(rules, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var updatedRules = _appRuleRepository.ListAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Gateway.UpdateHostContext(GatewaySettings, GatewayCapability, _meetingSessionRepository);
            Coordinator.UpdateHostContext(GatewaySettings, GatewayCapability, updatedRules);
        }

        public void UpdateCapability(HostCapabilitySnapshot capability)
        {
            Gateway.UpdateHostContext(GatewaySettings, capability, _meetingSessionRepository);
            Coordinator.UpdateHostContext(GatewaySettings, capability, _appRuleRepository.ListAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
        }

        public int CountMeetingSessions()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM meeting_session;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public string ResolveAutomaticMergeSourceFamily(int rootProcessId) =>
            Gateway.ResolveAutomaticRecordingPlan(rootProcessId).MergeSourceFamily;

        public string? CurrentMergeSourceFamily
        {
            get
            {
                var activeSession = typeof(ManualControlGateway)
                    .GetField("_activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Gateway);
                if (activeSession is null)
                {
                    return null;
                }

                var currentPlan = activeSession.GetType()
                    .GetProperty("CurrentPlan", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(activeSession);

                return (string?)currentPlan?.GetType()
                    .GetProperty("MergeSourceFamily", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(currentPlan);
            }
        }

        private ApplicationSettings GatewaySettings => (ApplicationSettings)typeof(ManualControlGateway)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Gateway)!;

        private HostCapabilitySnapshot GatewayCapability => (HostCapabilitySnapshot)typeof(ManualControlGateway)
            .GetField("_capability", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Gateway)!;

        public void Dispose()
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _connection.Dispose();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeSettingsStore(ApplicationSettings settings) : IApplicationSettingsStore
    {
        private ApplicationSettings _settings = settings;

        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_settings);

        public ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAudioDeviceManager : IAudioDeviceManager
    {
        public event EventHandler<AudioDeviceInventorySnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AudioDeviceInventorySnapshot CurrentSnapshot { get; } = new(
            DateTimeOffset.UtcNow,
            [new AudioDeviceSnapshot("render-default", "Default Speakers", AudioDeviceKind.Render, true, true)],
            [new AudioDeviceSnapshot("mic-default", "Default Mic", AudioDeviceKind.Capture, true, true)]);

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeProcessWatcher : IProcessWatcher
    {
        public event EventHandler<ProcessWatcherSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public ProcessWatcherSnapshot CurrentSnapshot { get; private set; } = ProcessWatcherSnapshot.Empty;

        public List<string> WatchList { get; } = [];

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void UpdateWatchList(IEnumerable<string> processNames)
        {
            WatchList.Clear();
            WatchList.AddRange(processNames);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSessionWatcher : IAudioSessionWatcher
    {
        public event EventHandler<IReadOnlyList<AudioSignalObservationSnapshot>>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SourceDeviceDrift>? SourceDeviceDriftDetected
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioSignalObservationSnapshot> CurrentSnapshot { get; set; } = [];

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePromptService : IAutomaticPromptService
    {
        private readonly Queue<Func<CancellationToken, Task<AutomaticPromptDecision>>> _responses = new();

        public int ShowCount { get; private set; }

        public bool CancellationObserved { get; private set; }

        public Task<AutomaticPromptDecision> ShowAsync(AutomaticPromptRequest request, CancellationToken cancellationToken)
        {
            ShowCount++;
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : static _ => Task.FromResult(AutomaticPromptDecision.No);
            return response(cancellationToken);
        }

        public void EnqueuePendingDecision()
        {
            _responses.Enqueue(cancellationToken =>
            {
                var completion = new TaskCompletionSource<AutomaticPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    CancellationObserved = true;
                    completion.TrySetCanceled(cancellationToken);
                });

                return completion.Task;
            });
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
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
