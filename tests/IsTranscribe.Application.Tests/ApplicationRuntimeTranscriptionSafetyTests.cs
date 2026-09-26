using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Recording;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Application-composition regressions for the remote-transcription opt-in and privacy boundary.
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </summary>
public sealed class ApplicationRuntimeTranscriptionSafetyTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshDefaultStartupPerformsNoProviderHttpRequests()
    {
        var handler = new CountingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        await using var fixture = await RuntimeFixture.CreateAsync(
            releaseSettings: null,
            vault: new MemorySecretVault(),
            transcriptionEngines: null,
            transcriptionHttpClient: httpClient);

        await Task.Delay(150);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, await fixture.CountJobsAsync());
    }

    [Fact]
    public async Task AutomaticTranscriptionEnqueuesOnlyAfterReadyFinalization()
    {
        var engine = SyntheticRemoteEngine.Blocking();
        var vault = new MemorySecretVault(
            (RemoteTranscriptionSecrets.Groq, "gsk_synthetic-runtime-opt-in"));
        await using var fixture = await RuntimeFixture.CreateAsync(
            CreateAutomaticSettings(),
            vault,
            [engine],
            transcriptionHttpClient: null);
        var sessionId = await fixture.AddReadySessionAsync(TimeSpan.FromSeconds(2));

        foreach (var stage in new[]
                 {
                     RecordingArtifactStage.Stopping,
                     RecordingArtifactStage.Processing,
                     RecordingArtifactStage.Verifying,
                     RecordingArtifactStage.Promoting
                 })
        {
            fixture.Runtime.ApplyRecordingSnapshot(CreateFinalizationSnapshot(sessionId, stage));
        }

        await Task.Delay(150);
        Assert.Equal(0, engine.RequestCount);
        Assert.Equal(0, await fixture.CountJobsAsync());

        fixture.Runtime.ApplyRecordingSnapshot(
            CreateFinalizationSnapshot(sessionId, RecordingArtifactStage.Ready));

        await engine.WaitForRequestAsync();
        Assert.Equal(1, engine.RequestCount);
        Assert.Equal(1, await fixture.CountJobsAsync());
        var jobId = Assert.IsType<string>(await fixture.ReadSingleJobIdAsync());
        var job = await fixture.Repository.GetAsync(jobId, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(TranscriptionTriggerKind.Automatic, job.TriggerKind);
    }

    [Fact]
    public async Task ProviderFailureSentinelsDoNotReachLogsSettingsOrSqlite()
    {
        const string groqSecret = "gsk_RUNTIME_SECURITY_SENTINEL_71b55a";
        const string openRouterSecret = "sk-or-v1-RUNTIME_SECURITY_SENTINEL_33d8d4";
        const string transcriptText = "RUNTIME TRANSCRIPT SECURITY SENTINEL 8ea1a6";
        var encodedAudio = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("RUNTIME AUDIO SECURITY SENTINEL 6fc134"));
        var engine = SyntheticRemoteEngine.Failing(new TranscriptionError(
            TranscriptionErrorCategory.Authentication,
            $"provider_{groqSecret}",
            $"{transcriptText}; {encodedAudio}; {openRouterSecret}",
            requestId: $"Bearer {groqSecret}",
            disposition: TranscriptionFailureDisposition.AttentionRequired));
        var vault = new MemorySecretVault((RemoteTranscriptionSecrets.Groq, groqSecret));
        await using var fixture = await RuntimeFixture.CreateAsync(
            CreateAutomaticSettings(),
            vault,
            [engine],
            transcriptionHttpClient: null);
        var sessionId = await fixture.AddReadySessionAsync(TimeSpan.FromSeconds(2));

        fixture.Runtime.ApplyRecordingSnapshot(
            CreateFinalizationSnapshot(sessionId, RecordingArtifactStage.Ready));
        var job = await fixture.WaitForSingleJobStatusAsync(
            TranscriptionJobStatus.AttentionRequired);
        var chunk = Assert.Single(await fixture.Repository.ListChunksAsync(
            job.Id,
            CancellationToken.None));

        Assert.Equal("transcription_error", job.StableErrorCode);
        Assert.DoesNotContain(groqSecret, job.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(transcriptText, job.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(chunk.EngineRequestId);

        await fixture.StopRuntimeAsync();

        fixture.AssertFilesDoNotContain(
            [groqSecret, openRouterSecret, transcriptText, encodedAudio]);
    }

    [Fact]
    public async Task RemovingSelectedLocalModelPersistsAutomaticTranscriptionDisabled()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            CreateAutomaticLocalSettings(),
            new MemorySecretVault(),
            transcriptionEngines: [],
            transcriptionHttpClient: null,
            enableLocalTranscription: true);

        await fixture.Runtime.RemoveTranscriptionModelAsync(
            LocalWhisperTranscriptionEngine.EngineId,
            "large-v3-turbo",
            CancellationToken.None);

        Assert.False(fixture.Runtime.Snapshot.UserSettings.Transcription.AutomaticEnabled);
        var persisted = await new JsonApplicationSettingsStore(fixture.Paths)
            .LoadAsync(CancellationToken.None);
        var release = Assert.IsType<ReleaseV2Settings>(persisted.ReleaseV2);
        var transcription = Assert.IsType<TranscriptionPreferences>(release.Transcription);
        Assert.False(transcription.AutomaticEnabled);
    }

    private static ReleaseV2Settings CreateAutomaticSettings() => ReleaseV2Settings.Default with
    {
        OnboardingCompleted = false,
        ServiceEnabled = false,
        Autostart = false,
        AutostartPreference = false,
        Transcription = new TranscriptionPreferences(
            SelectedEngineId: GroqTranscriptionEngine.EngineId,
            AutomaticEnabled: true,
            Language: "en",
            Engines:
            [
                new TranscriptionEnginePreference(
                    GroqTranscriptionEngine.EngineId,
                    "whisper-large-v3-turbo",
                    DisclosureAccepted: true,
                    RemoteTranscriptionDisclosureCatalog.GroqRevision)
            ],
            RequireZeroDataRetention: true)
    };

    private static ReleaseV2Settings CreateAutomaticLocalSettings() => ReleaseV2Settings.Default with
    {
        OnboardingCompleted = false,
        ServiceEnabled = false,
        Autostart = false,
        AutostartPreference = false,
        Transcription = new TranscriptionPreferences(
            SelectedEngineId: LocalWhisperTranscriptionEngine.EngineId,
            AutomaticEnabled: true,
            Language: "en",
            Engines:
            [
                new TranscriptionEnginePreference(
                    LocalWhisperTranscriptionEngine.EngineId,
                    "large-v3-turbo",
                    DisclosureAccepted: false,
                    DisclosureRevision: null)
            ],
            RequireZeroDataRetention: true)
    };

    private static RecordingCoordinatorSnapshot CreateFinalizationSnapshot(
        Guid sessionId,
        RecordingArtifactStage stage) => new(
        ApplicationActivityState.Paused,
        ActiveMeeting: null,
        new RecordingFinalizationSnapshot(sessionId, stage),
        AttentionMessage: null,
        RefreshRecentRecordings: false);

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _root;
        private ApplicationRuntime? _runtime;

        private RuntimeFixture(
            string root,
            LocalAppPaths paths,
            ApplicationRuntime runtime)
        {
            _root = root;
            Paths = paths;
            _runtime = runtime;
            Repository = new TranscriptionJobRepository(paths);
        }

        public LocalAppPaths Paths { get; }

        public ApplicationRuntime Runtime => _runtime
            ?? throw new InvalidOperationException("The runtime has already stopped.");

        public TranscriptionJobRepository Repository { get; }

        public static async ValueTask<RuntimeFixture> CreateAsync(
            ReleaseV2Settings? releaseSettings,
            MemorySecretVault vault,
            IReadOnlyList<ITranscriptionEngine>? transcriptionEngines,
            HttpClient? transcriptionHttpClient,
            bool enableLocalTranscription = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"istranscribe-runtime-transcription-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var settingsStore = new JsonApplicationSettingsStore(paths);
            if (releaseSettings is not null)
            {
                await settingsStore.SaveReleaseV2Async(
                    ApplicationSettings.Default.WithReleaseV2Projection(releaseSettings),
                    CancellationToken.None);
            }

            var audioPlatform = new QuietAudioPlatform();
            var localOptions = enableLocalTranscription
                ? new LocalTranscriptionRuntimeOptions(
                    WorkerExecutablePath: Path.Combine(
                        root,
                        "transcription-worker",
                        "synthetic-worker"),
                    RequestedBackend: LocalTranscriptionBackend.Cpu,
                    ThreadCount: 4,
                    ResourcePolicy: new DeterministicLocalTranscriptionResourcePolicy(
                        new LocalTranscriptionResourceAssessment(
                            LocalTranscriptionResourceDisposition.Allowed,
                            new LocalTranscriptionResourceState(
                                IsLowPowerMode: false,
                                AvailableMemoryBytes: 4L * 1024 * 1024 * 1024,
                                AvailableDiskBytes: 4L * 1024 * 1024 * 1024)
                            {
                                InstalledMemoryBytes = 8L * 1024 * 1024 * 1024
                            })),
                    ModelStoreRoot: Path.Combine(root, "models"))
                : null;
            var runtime = new ApplicationRuntime(
                platform: new TestPlatformAdapter(root, vault),
                rootDirectoryOverride: root,
                audioPlatform,
                detectionMode: MeetingDetectionMode.Shadow,
                detectionCoordinatorFactory: null,
                autostartService: new DisabledAutostartService(),
                settingsStore,
                readyFinalizationHold: TimeSpan.FromMinutes(1),
                onboardingMicrophoneHealthTimeout: TimeSpan.FromSeconds(1),
                activeMeetingLossDelay: TimeSpan.FromSeconds(1),
                secretVault: vault,
                transcriptionEngines,
                transcriptionHttpClient,
                timeProvider: new FixedTimeProvider(Baseline),
                localTranscriptionOptions: localOptions);
            try
            {
                await runtime.InitializeAsync(CancellationToken.None);
                return new RuntimeFixture(root, paths, runtime);
            }
            catch
            {
                await runtime.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask<Guid> AddReadySessionAsync(TimeSpan duration)
        {
            var sessionId = Guid.NewGuid();
            var fixtureDirectory = Path.Combine(_root, "fixtures");
            Directory.CreateDirectory(fixtureDirectory);
            var audioPath = Path.Combine(fixtureDirectory, $"{sessionId:N}.wav");
            await WriteWaveAsync(audioPath, duration, sampleRate: 1_000);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Paths.DatabaseFilePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            var session = MeetingSessionRecord.Create(
                sessionId,
                Baseline,
                "manual",
                "mixed",
                "synthetic-output",
                "synthetic-microphone") with
            {
                Status = "ready",
                SourceApp = "Synthetic runtime safety fixture",
                EndedAtUtc = Baseline.Add(duration),
                PrimaryAudioPath = audioPath,
                DurationSeconds = duration.TotalSeconds,
                ArtifactProgress = 1,
                UpdatedAtUtc = Baseline.Add(duration)
            };
            await new MeetingSessionRepository(connection)
                .UpsertAsync(session, CancellationToken.None);
            return sessionId;
        }

        public async ValueTask<long> CountJobsAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Paths.DatabaseFilePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transcription_job;";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask<TranscriptionJobRecord> WaitForSingleJobStatusAsync(
            TranscriptionJobStatus status)
        {
            var timeout = Stopwatch.StartNew();
            TranscriptionJobRecord? last = null;
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                var jobId = await ReadSingleJobIdAsync();
                if (jobId is not null)
                {
                    last = await Repository.GetAsync(jobId, CancellationToken.None);
                    if (last?.Status == status)
                    {
                        return last;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException(
                $"The runtime job did not reach {status}; last status was {last?.Status}.");
        }

        public void AssertFilesDoNotContain(IReadOnlyList<string> sentinels)
        {
            var files = new List<string>();
            if (File.Exists(Paths.SettingsFilePath))
            {
                files.Add(Paths.SettingsFilePath);
            }

            if (Directory.Exists(Paths.LogsDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(
                    Paths.LogsDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly));
            }

            if (Directory.Exists(Paths.DataDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(
                    Paths.DataDirectory,
                    $"{Path.GetFileName(Paths.DatabaseFilePath)}*",
                    SearchOption.TopDirectoryOnly));
            }

            Assert.NotEmpty(files);
            foreach (var file in files.Distinct(StringComparer.Ordinal))
            {
                var payload = File.ReadAllBytes(file);
                foreach (var sentinel in sentinels)
                {
                    Assert.True(
                        payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes(sentinel)) < 0,
                        $"Security sentinel was found in '{file}'.");
                }
            }
        }

        public async ValueTask StopRuntimeAsync()
        {
            var runtime = Interlocked.Exchange(ref _runtime, null);
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopRuntimeAsync();
            Directory.Delete(_root, recursive: true);
        }

        public async ValueTask<string?> ReadSingleJobIdAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Paths.DatabaseFilePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM transcription_job ORDER BY queued_at, id LIMIT 1;";
            return await command.ExecuteScalarAsync() as string;
        }
    }

    private sealed class SyntheticRemoteEngine(
        Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> handler)
        : ITranscriptionEngine
    {
        private readonly Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> _handler =
            handler;
        private readonly ConcurrentQueue<TranscriptionRequest> _requests = new();
        private readonly TaskCompletionSource _requestObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TranscriptionEngineCapabilities Capabilities { get; } = new(
            GroqTranscriptionEngine.EngineId,
            "Synthetic remote transcription",
            TranscriptionExecutionKind.Remote,
            RequiresNetwork: true,
            "Synthetic in-process provider used only by application tests.",
            SupportedTestPlatforms,
            [new TranscriptionModelCapability("whisper-large-v3-turbo", "Synthetic model", IsRecommended: true)],
            ["en"],
            SupportsAutomaticLanguageDetection: true,
            SupportsDiarization: false,
            TranscriptionTimestampCapabilities.Segment);

        public int RequestCount => _requests.Count;

        public static SyntheticRemoteEngine Blocking() => new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });

        public static SyntheticRemoteEngine Failing(TranscriptionError error) => new(
            (_, _) => ValueTask.FromResult(TranscriptionResult.Failed(error)));

        public ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            request.Validate();
            _requests.Enqueue(request);
            _requestObserved.TrySetResult();
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Uploading, 0.5));
            return _handler(request, cancellationToken);
        }

        public Task WaitForRequestAsync() => _requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class MemorySecretVault(params (string Key, string Value)[] values) : ISecretVault
    {
        private readonly ConcurrentDictionary<string, string> _values = new(
            values.ToDictionary(static pair => pair.Key, static pair => pair.Value),
            StringComparer.Ordinal);

        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken)
        {
            if (value is null)
            {
                _values.TryRemove(key, out _);
            }
            else
            {
                _values[key] = value;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPlatformAdapter(string root, ISecretVault vault)
        : IApplicationPlatformRuntimeAdapter
    {
        public LocalAppPaths CreateAppPaths(string applicationName, string? rootDirectoryOverride) =>
            new(applicationName, rootDirectoryOverride ?? root);

        public ISecretVault CreateSecretVault(LocalAppPaths paths) => vault;

        public IAudioPlatform CreateAudioPlatform(BootstrapFileLogger logger) => new QuietAudioPlatform();

        public IAutostartService CreateAutostartService() => new DisabledAutostartService();

        public AudioArtifactEncoderAvailability ProbeRecordingEncoder() => new(
            IsAvailable: false,
            Codec: "mp3",
            SampleRate: 48_000,
            Channels: 2,
            BitsPerSample: 16,
            BitRate: 128_000,
            ReasonCode: "synthetic_test_platform",
            Detail: "Encoding is outside this regression fixture.");

        public IRecordingSessionCoordinator CreateRecordingCoordinator(
            IAudioPlatform audioPlatform,
            MeetingSessionRepository sessionRepository,
            ArtifactPathResolver artifactPathResolver,
            Func<ApplicationSettings> settingsAccessor,
            BootstrapFileLogger logger) => new QuietRecordingCoordinator();

        public IRecordingArtifactDeletionService CreateRecordingArtifactDeletionService(
            LocalAppPaths paths,
            ArtifactPathResolver artifactPathResolver,
            Func<ApplicationSettings> settingsAccessor) => new QuietArtifactDeletionService();

        public MeetingDetectionCoordinator CreateMeetingDetectionCoordinator(
            IAudioPlatform audioPlatform,
            MeetingProfileRegistry profiles,
            IReadOnlyList<MeetingApplicationPreference> preferences,
            MeetingDetectionMode mode,
            BootstrapFileLogger logger) =>
            throw new InvalidOperationException("Detection must remain stopped in this fixture.");
    }

    private sealed class QuietAudioPlatform : IAudioPlatform
    {
        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AudioPlatformSnapshot Snapshot { get; } = new(
            Baseline,
            new AudioPlatformCapabilities(
                IsSupported: true,
                SupportsProcessOutputCapture: true,
                Summary: "Synthetic test audio platform."),
            [],
            [],
            [],
            []);

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IAudioCaptureSession>(
                new InvalidOperationException("Capture is outside this regression fixture."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisabledAutostartService : IAutostartService
    {
        public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AutostartRegistrationState(false));

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AutostartRegistrationState(enabled));
    }

    private sealed class QuietRecordingCoordinator : IRecordingSessionCoordinator
    {
        public event EventHandler<RecordingCoordinatorSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public bool IsBusy => false;

        public Guid? ActiveSessionId => null;

        public ValueTask<bool> StartAskAsync(
            string sourceLabel,
            int rootProcessId,
            string processName,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> StartManualAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask PauseOrResumeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> FinishForRuntimeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> DiscardAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public Task RecoverPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QuietArtifactDeletionService : IRecordingArtifactDeletionService
    {
        public void DeleteRecentArtifacts(RecentRecordingRemovalWorkItem workItem)
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static IReadOnlyList<TranscriptionPlatformTarget> SupportedTestPlatforms { get; } =
    [
        new(TranscriptionOperatingSystem.Windows, Architecture.X64),
        new(TranscriptionOperatingSystem.Windows, Architecture.Arm64),
        new(TranscriptionOperatingSystem.MacOS, Architecture.X64),
        new(TranscriptionOperatingSystem.MacOS, Architecture.Arm64),
        new(TranscriptionOperatingSystem.Linux, Architecture.X64),
        new(TranscriptionOperatingSystem.Linux, Architecture.Arm64)
    ];

    private static async ValueTask WriteWaveAsync(
        string path,
        TimeSpan duration,
        int sampleRate)
    {
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channelCount * (bitsPerSample / 8);
        var sampleCount = checked((int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        var dataSize = checked(sampleCount * blockAlign);
        var payload = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)(36 + dataSize)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(payload, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), checked((ushort)channelCount));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(28, 4),
            checked((uint)(sampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(32, 2), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(34, 2), checked((ushort)bitsPerSample));
        Encoding.ASCII.GetBytes("data").CopyTo(payload, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40, 4), checked((uint)dataSize));
        await File.WriteAllBytesAsync(path, payload);
    }
}
