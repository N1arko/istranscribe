using Microsoft.Data.Sqlite;
using System.Text.Json;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Recovery;
using IsTranscribe.Application.Settings;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Core.Runtime;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Transcription.Local.Models;

namespace IsTranscribe.Application;

/// <summary>
/// Shared release-v2 runtime that owns product decisions over normalized platform contracts.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#acceptance
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public class ApplicationRuntime : IApplicationRuntime
{
    private const string ApplicationName = "isTranscribe";
    private const int MaxDiagnosticCandidates = 8;
    private const int MaxDiagnosticEvidenceItems = 16;
    private const int MaximumProviderRequestIdLength = 128;
    private static readonly TimeSpan DefaultReadyFinalizationHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultOnboardingMicrophoneHealthTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions TranscriptionUsageSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly LocalAppPaths _paths;
    private readonly IApplicationPlatformRuntimeAdapter _platform;
    private readonly BootstrapFileLogger _logger;
    private readonly object _recordingGateSync = new();
    private readonly object _snapshotPublicationSync = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _capabilityLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _microphoneHealthProbeGate = new(1, 1);
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);
    private readonly SemaphoreSlim _recentRecordingMutationGate = new(1, 1);
    private readonly SemaphoreSlim _transcriptionCommandGate = new(1, 1);
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IAutostartService _autostartService;
    private readonly PersistenceBootstrapper _persistenceBootstrapper;
    private readonly IAudioPlatform _audioPlatform;
    private readonly MeetingDetectionMode _detectionMode;
    private readonly TimeSpan _readyFinalizationHold;
    private readonly TimeSpan _onboardingMicrophoneHealthTimeout;
    private readonly TimeSpan? _activeMeetingLossDelayOverride;
    private readonly ISecretVault _secretVault;
    private readonly HttpClient? _transcriptionHttpClient;
    private readonly bool _ownsTranscriptionHttpClient;
    private readonly TranscriptionEngineRegistry _transcriptionEngines;
    private readonly TimeProvider _timeProvider;
    private readonly LocalTranscriptionServices? _localTranscription;
    private readonly Dictionary<string, bool> _transcriptionCredentialPresence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TranscriptionModelCapability>>
        _discoveredTranscriptionModels = new(StringComparer.Ordinal);
    private readonly Func<
        IAudioPlatform,
        MeetingProfileRegistry,
        IReadOnlyList<MeetingApplicationPreference>,
        MeetingDetectionMode,
        MeetingDetectionCoordinator>? _detectionCoordinatorFactory;
    private MeetingProfileRegistry _profiles = new();
    private MeetingDetectionCoordinator? _detectionCoordinator;
    private SqliteConnection? _databaseConnection;
    private AppRuleRepository? _appRuleRepository;
    private MeetingSessionRepository? _sessionRepository;
    private IRecordingSessionCoordinator? _recordingCoordinator;
    private IRecordingArtifactDeletionService? _artifactDeletionService;
    private TranscriptionJobRepository? _transcriptionJobRepository;
    private TranscriptionJobEnqueuer? _transcriptionJobEnqueuer;
    private TranscriptionQueueWorker? _transcriptionWorker;
    private IsTranscribe.Transcription.Local.Models.DiarizationAssets? _diarizationAssets;
    private string? _diarizationWorkerPath;
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recoveryTask;
    private ApplicationSettings _legacySettings = ApplicationSettings.Default;
    private ReleaseV2Settings _settings = ReleaseV2Settings.Default;
    private IReadOnlyList<AppRuleRecord> _appRules = [];
    private string? _lastDetectionDiagnosticFingerprint;
    private DateTimeOffset _lastDetectionDiagnosticAtUtc;
    private string? _activeDetectionCandidateId;
    private DateTimeOffset? _activeDetectionLossStartedAtUtc;
    private bool _activeDetectionAutoFinishScheduled;
    private bool _hasActionableAttention;
    private Guid? _actionableAttentionSessionId;
    private string? _actionableAttentionMessage;
    private RuntimeCapabilityIssue? _activeRecordingCapabilityIssue;
    private RuntimeCapabilityIssue? _persistentCaptureCapabilityIssue;
    private bool _detectionMicrophoneCaptureUnavailable;
    private Task? _recordingGateCompletionTask;
    private CancellationTokenSource? _readyFinalizationClearCancellation;
    private long _recordingGateGeneration;
    private long _audioProjectionPublicationGeneration;
    private bool _disposing;
    private bool _initialized;

    public ApplicationRuntime(
        IApplicationPlatformRuntimeAdapter platform,
        string? rootDirectoryOverride = null,
        IAudioPlatform? audioPlatform = null,
        MeetingDetectionMode detectionMode = MeetingDetectionMode.Live,
        LocalTranscriptionRuntimeOptions? localTranscriptionOptions = null)
        : this(
            platform,
            rootDirectoryOverride,
            audioPlatform,
            detectionMode,
            detectionCoordinatorFactory: null,
            localTranscriptionOptions: localTranscriptionOptions)
    {
    }

    internal ApplicationRuntime(
        IApplicationPlatformRuntimeAdapter platform,
        string? rootDirectoryOverride,
        IAudioPlatform? audioPlatform,
        MeetingDetectionMode detectionMode,
        Func<
            IAudioPlatform,
            MeetingProfileRegistry,
            IReadOnlyList<MeetingApplicationPreference>,
        MeetingDetectionMode,
        MeetingDetectionCoordinator>? detectionCoordinatorFactory,
        IAutostartService? autostartService = null,
        IApplicationSettingsStore? settingsStore = null,
        TimeSpan? readyFinalizationHold = null,
        TimeSpan? onboardingMicrophoneHealthTimeout = null,
        TimeSpan? activeMeetingLossDelay = null,
        ISecretVault? secretVault = null,
        IReadOnlyList<ITranscriptionEngine>? transcriptionEngines = null,
        HttpClient? transcriptionHttpClient = null,
        TimeProvider? timeProvider = null,
        LocalTranscriptionRuntimeOptions? localTranscriptionOptions = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _paths = _platform.CreateAppPaths(ApplicationName, rootDirectoryOverride);
        _logger = new BootstrapFileLogger(_paths.HostLogFilePath);
        _settingsStore = settingsStore ?? new JsonApplicationSettingsStore(_paths);
        _secretVault = secretVault ?? _platform.CreateSecretVault(_paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        IReadOnlyList<ITranscriptionEngine> configuredEngines;
        if (transcriptionEngines is null)
        {
            _transcriptionHttpClient = transcriptionHttpClient ?? new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(4)
            };
            _ownsTranscriptionHttpClient = transcriptionHttpClient is null;
            configuredEngines =
            [
                new GroqTranscriptionEngine(_transcriptionHttpClient, _secretVault),
                new OpenRouterTranscriptionEngine(_transcriptionHttpClient, _secretVault)
            ];
        }
        else
        {
            _transcriptionHttpClient = transcriptionHttpClient;
            _ownsTranscriptionHttpClient = false;
            configuredEngines = transcriptionEngines;
        }

        _transcriptionJobRepository = new TranscriptionJobRepository(_paths);
        if (localTranscriptionOptions is not null)
        {
            localTranscriptionOptions.Validate();
            if (configuredEngines.Any(engine => string.Equals(
                    engine.Capabilities.EngineId,
                    LocalWhisperTranscriptionEngine.EngineId,
                    StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "The local Whisper engine cannot be supplied twice.",
                    nameof(transcriptionEngines));
            }

            var modelStoreRoot = localTranscriptionOptions.ModelStoreRoot
                ?? WhisperModelStorageLayout.GetDefaultRootPath();
            _diarizationAssets = new IsTranscribe.Transcription.Local.Models.DiarizationAssets(Path.Combine(modelStoreRoot, "speaker-v1"));
            _diarizationWorkerPath = localTranscriptionOptions.WorkerExecutablePath;
            _localTranscription = LocalTranscriptionServices.Create(
                modelStoreRoot,
                _transcriptionJobRepository,
                localTranscriptionOptions,
                _timeProvider);
            _localTranscription.StateChanged += HandleLocalTranscriptionStateChanged;
            configuredEngines = configuredEngines
                .Append(new LocalWhisperTranscriptionEngine(
                    _transcriptionJobRepository,
                    _localTranscription,
                    localTranscriptionOptions,
                    _timeProvider))
                .ToArray();
        }

        _transcriptionEngines = new TranscriptionEngineRegistry(configuredEngines);
        // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#scope.in
        _autostartService = autostartService ?? _platform.CreateAutostartService();
        _persistenceBootstrapper = new PersistenceBootstrapper(
            _paths,
            _settingsStore,
            new SqliteDatabaseInitializer(_paths),
            new LegacyStorageLayoutMigrator(_paths));
        _audioPlatform = audioPlatform ?? _platform.CreateAudioPlatform(_logger);
        _detectionMode = detectionMode;
        _detectionCoordinatorFactory = detectionCoordinatorFactory;
        _readyFinalizationHold = readyFinalizationHold ?? DefaultReadyFinalizationHold;
        _onboardingMicrophoneHealthTimeout = onboardingMicrophoneHealthTimeout
            ?? DefaultOnboardingMicrophoneHealthTimeout;
        _activeMeetingLossDelayOverride = activeMeetingLossDelay;
        if (_readyFinalizationHold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readyFinalizationHold),
                "The ready-state hold duration cannot be negative.");
        }

        if (_onboardingMicrophoneHealthTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onboardingMicrophoneHealthTimeout),
                "The onboarding microphone-health timeout must be positive.");
        }

        if (_activeMeetingLossDelayOverride is { } activeMeetingLossDelayOverride
            && activeMeetingLossDelayOverride <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeMeetingLossDelay),
                "The active-meeting loss delay must be positive.");
        }
    }

    public event EventHandler<ApplicationRuntimeSnapshot>? SnapshotChanged;

    public ApplicationRuntimeSnapshot Snapshot { get; private set; } = ApplicationRuntimeSnapshot.Initial;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {

                // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
                var persistence = await _persistenceBootstrapper
                    .InitializeReleaseV2Async(cancellationToken)
                    .ConfigureAwait(false);
                _legacySettings = persistence.Settings;
                _databaseConnection = persistence.DatabaseConnection;
                _appRuleRepository = new AppRuleRepository(_databaseConnection);
                _sessionRepository = new MeetingSessionRepository(_databaseConnection);
                var recoveryDecision = await new SqliteRecoveryCoordinator(_logger, () => _databaseConnection)
                    .EvaluateAsync(cancellationToken)
                    .ConfigureAwait(false);
                var persistedAppRules = await _appRuleRepository
                    .ListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var builtInProfiles = new MeetingProfileRegistry();
                _appRules = ToReleaseV2ApplicationRules(persistedAppRules, builtInProfiles);
                if (_appRules.Count != persistedAppRules.Count)
                {
                    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
                    await _appRuleRepository
                        .ReplaceAllAsync(_appRules, cancellationToken)
                        .ConfigureAwait(false);
                }

                _profiles = BuildProfileRegistry(_appRules);
                _settings = CanonicalizeReleaseV2Settings(
                    _legacySettings.ReleaseV2
                    ?? ReleaseV2SettingsMigration.Migrate(_legacySettings, _appRules));
                await LoadTranscriptionCredentialPresenceAsync(cancellationToken).ConfigureAwait(false);
                if (_localTranscription is not null)
                {
                    await _localTranscription.InitializeAsync(cancellationToken).ConfigureAwait(false);
                }

                // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
                try
                {
                    if (_autostartService is IAutostartStateReconciler reconciler)
                    {
                        if (_settings.OnboardingCompleted)
                        {
                            var desiredAutostart = _settings.AutostartPreference ?? _settings.Autostart;
                            var autostart = await reconciler
                                .ReconcileDesiredStateAsync(
                                    desiredAutostart,
                                    _settings.PackagedAutostartObserved,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            _settings = _settings with
                            {
                                Autostart = autostart.IsEnabled,
                                AutostartPreference = autostart.DesiredEnabled,
                                PackagedAutostartObserved = autostart.PackagedObservedEnabled
                            };
                            if (autostart.Failure is not null)
                            {
                                _logger.Error(
                                    autostart.Failure,
                                    "Packaged launch-at-login reconciliation completed in a degraded state.");
                            }
                        }
                        else
                        {
                            await reconciler
                                .CleanupMigrationArtifactsAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else if (_settings.OnboardingCompleted)
                    {
                        var autostart = await _autostartService
                            .GetStateAsync(cancellationToken)
                            .ConfigureAwait(false);
                        _settings = _settings with
                        {
                            Autostart = autostart.IsEnabled,
                            AutostartPreference = autostart.IsEnabled,
                            PackagedAutostartObserved = null
                        };
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.Error(exception, "Failed to reconcile the platform launch-at-login state.");
                }

                _legacySettings = _legacySettings.WithReleaseV2Projection(_settings);
                await _settingsStore
                    .SaveReleaseV2Async(_legacySettings, cancellationToken)
                    .ConfigureAwait(false);

                _audioPlatform.SnapshotChanged += HandleAudioPlatformSnapshotChanged;
                try
                {
                    await _audioPlatform.StartAsync(GetWatchedProcessNames(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _audioPlatform.SnapshotChanged -= HandleAudioPlatformSnapshotChanged;
                    throw;
                }
                // @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#decisions.implementation
                var encoderAvailability = _platform.ProbeRecordingEncoder();
                _logger.LogEvent(
                    encoderAvailability.IsAvailable ? "Info" : "Warning",
                    "MP3_ENCODER_CAPABILITY",
                    encoderAvailability.IsAvailable
                        ? "The platform MP3 encoder is available."
                        : "The platform MP3 encoder is unavailable; source audio will be preserved.",
                    metadata: new Dictionary<string, object?>
                    {
                        ["available"] = encoderAvailability.IsAvailable,
                        ["codec"] = encoderAvailability.Codec,
                        ["sample_rate"] = encoderAvailability.SampleRate,
                        ["channels"] = encoderAvailability.Channels,
                        ["bit_rate"] = encoderAvailability.BitRate,
                        ["reason_code"] = encoderAvailability.ReasonCode
                    });
                var artifactPathResolver = new ArtifactPathResolver(_paths);
                _artifactDeletionService = _platform.CreateRecordingArtifactDeletionService(
                    _paths,
                    artifactPathResolver,
                    () => _legacySettings);
                _recordingCoordinator = _platform.CreateRecordingCoordinator(
                    _audioPlatform,
                    _sessionRepository,
                    artifactPathResolver,
                    () => _legacySettings,
                    _logger);
                _recordingCoordinator.SnapshotChanged += HandleRecordingSnapshotChanged;
                var transcriptionWorkerRoot = Path.Combine(_paths.TempDirectory, "transcription");
                _transcriptionJobRepository ??= new TranscriptionJobRepository(_paths);
                _transcriptionJobEnqueuer = new TranscriptionJobEnqueuer(
                    _transcriptionJobRepository,
                    _transcriptionEngines,
                    _secretVault,
                    transcriptionWorkerRoot,
                    _timeProvider,
                    _localTranscription);
                _transcriptionWorker = new TranscriptionQueueWorker(
                    _transcriptionJobRepository,
                    new TranscriptionSessionContextReader(_paths),
                    _transcriptionEngines,
                    new AudioChunkPlanner(),
                    new ManagedAudioChunkMaterializer(
                        (_platform as ITranscriptionAudioDecoderPlatformAdapter)
                        ?.CreateTranscriptionAudioChunkDecoder()),
                    new TranscriptionChunkResultStore(),
                    new TranscriptMerger(),
                    new TranscriptArtifactMaterializer(artifactPathResolver),
                    () => _legacySettings,
                    transcriptionWorkerRoot,
                    _timeProvider,
                    localModelOrphanCollector: _localTranscription,
                    speakerProcessor: _diarizationAssets is null ? null : new SpeakerAwareChunkProcessor(
                        new SpeakerDiarizationClient(_diarizationWorkerPath!, _diarizationAssets),
                        new ManagedAudioChunkMaterializer(
                            (_platform as ITranscriptionAudioDecoderPlatformAdapter)?.CreateTranscriptionAudioChunkDecoder())));
                _transcriptionWorker.StateChanged += HandleTranscriptionStateChanged;
                _initialized = true;
                await _transcriptionWorker.StartAsync(cancellationToken).ConfigureAwait(false);

                if (recoveryDecision.RequiresAttention)
                {
                    SetActionableAttention(sessionId: null, recoveryDecision.Summary);
                }

                var idleSnapshot = BuildIdleSnapshot();
                PublishSnapshot(idleSnapshot);
                if (_settings.OnboardingCompleted && _settings.ServiceEnabled)
                {
                    await StartDetectionAsync(cancellationToken).ConfigureAwait(false);
                }

                _recoveryCancellation = new CancellationTokenSource();
                _recoveryTask = Task.Run(
                    () => RunArtifactRecoveryAsync(_recoveryCancellation.Token),
                    CancellationToken.None);

                _logger.LogEvent(
                    "Info",
                    "V2_RUNTIME_BRIDGE_READY",
                    "Release v2 migration bridge loaded existing settings and recordings.");
            }
            catch
            {
                await ResetFailedInitializationAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        _persistentCaptureCapabilityIssue = null;
        var publicationGeneration = Volatile.Read(ref _audioProjectionPublicationGeneration);
        if (_audioPlatform is IAudioPlatformCapabilityRefresher refresher)
        {
            await refresher.RefreshCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _audioProjectionPublicationGeneration) == publicationGeneration)
        {
            ApplyAudioPlatformSnapshot(_audioPlatform.Snapshot, forcePublish: true);
        }

        await ObserveOnboardingMicrophoneHealthAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileDetectionForCapabilityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetServiceEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await ApplyReleaseV2SettingsAsync(
            current => current with { ServiceEnabled = enabled },
            applyAutostart: false,
            cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    public async ValueTask CompleteOnboardingAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(settings);
        await ApplyReleaseV2SettingsAsync(
            current => MergeUserSettings(current, settings) with { OnboardingCompleted = true },
            applyAutostart: true,
            cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    public async ValueTask UpdateSettingsAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(settings);
        await ApplyReleaseV2SettingsAsync(
            current => MergeUserSettings(current, settings),
            applyAutostart: true,
            cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    public async ValueTask UpdateTranscriptionSettingsAsync(
        RuntimeTranscriptionSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(settings);
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (_settings.Transcription ?? TranscriptionPreferences.Default).Canonicalize();
            var selectedEngineId = string.IsNullOrWhiteSpace(settings.SelectedEngineId)
                ? null
                : settings.SelectedEngineId.Trim().ToLowerInvariant();
            var enginePreferences = current.Engines.ToList();
            if (selectedEngineId is not null)
            {
                var engine = _transcriptionEngines.GetRequired(selectedEngineId);
                if (!_transcriptionEngines.GetSupportedCurrentPlatform().Any(candidate => string.Equals(
                        candidate.Capabilities.EngineId,
                        selectedEngineId,
                        StringComparison.Ordinal)))
                {
                    throw new TranscriptionCommandException(
                        "engine_platform_unsupported",
                        "The selected transcription engine is unavailable on this device.");
                }

                var selectedModelId = string.IsNullOrWhiteSpace(settings.SelectedModelId)
                    ? null
                    : settings.SelectedModelId.Trim();
                if (selectedModelId is not null
                    && !GetRuntimeModels(engine).Any(model => string.Equals(
                        model.Id,
                        selectedModelId,
                        StringComparison.Ordinal)))
                {
                    throw new TranscriptionCommandException(
                        "model_unavailable",
                        "The selected speech-to-text model is unavailable. Refresh the model list.");
                }

                string? disclosureRevision = null;
                if (engine.Capabilities.ExecutionKind == TranscriptionExecutionKind.Remote
                    && settings.DisclosureAccepted)
                {
                    disclosureRevision = RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(selectedEngineId);
                }

                enginePreferences.RemoveAll(preference => string.Equals(
                    preference.EngineId,
                    selectedEngineId,
                    StringComparison.Ordinal));
                enginePreferences.Add(new TranscriptionEnginePreference(
                    selectedEngineId,
                    selectedModelId,
                    settings.DisclosureAccepted,
                    disclosureRevision));
            }

            var updated = new TranscriptionPreferences(
                    selectedEngineId,
                    settings.AutomaticEnabled && selectedEngineId is not null,
                    settings.Language,
                    enginePreferences,
                    settings.RequireZeroDataRetention)
                .Canonicalize();
            if (updated.AutomaticEnabled && updated.Mode == "online")
            {
                EnsureAutomaticTranscriptionReady(updated);
            }

            await ApplyReleaseV2SettingsAsync(
                    release => release with { Transcription = updated },
                    applyAutostart: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask SaveTranscriptionCredentialAsync(
        string engineId,
        string credential,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var normalizedEngineId = RequireRemoteEngineId(engineId);
        var normalizedCredential = NormalizeCredential(credential);
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secretVault.WriteAsync(
                    RemoteTranscriptionSecrets.ForEngine(normalizedEngineId),
                    normalizedCredential,
                    cancellationToken)
                .ConfigureAwait(false);
            _transcriptionCredentialPresence[normalizedEngineId] = true;
            PublishSnapshot(Snapshot);
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask DeleteTranscriptionCredentialAsync(
        string engineId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var normalizedEngineId = RequireRemoteEngineId(engineId);
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secretVault.WriteAsync(
                    RemoteTranscriptionSecrets.ForEngine(normalizedEngineId),
                    value: null,
                    cancellationToken)
                .ConfigureAwait(false);
            _transcriptionCredentialPresence[normalizedEngineId] = false;
            _discoveredTranscriptionModels.Remove(normalizedEngineId);
            var preferences = (_settings.Transcription ?? TranscriptionPreferences.Default).Canonicalize();
            if (preferences.AutomaticEnabled
                && string.Equals(preferences.SelectedEngineId, normalizedEngineId, StringComparison.Ordinal))
            {
                await ApplyReleaseV2SettingsAsync(
                        release => release with
                        {
                            Transcription = preferences with { AutomaticEnabled = false }
                        },
                        applyAutostart: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                PublishSnapshot(Snapshot);
            }
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverTranscriptionModelsAsync(
        string engineId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var normalizedEngineId = RequireRemoteEngineId(engineId);
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_transcriptionCredentialPresence.GetValueOrDefault(normalizedEngineId))
            {
                throw new TranscriptionCommandException(
                    "credential_required",
                    "Save an API key before refreshing provider models.");
            }

            var engine = _transcriptionEngines.GetRequired(normalizedEngineId);
            if (engine is not ITranscriptionModelDiscovery discovery)
            {
                return engine.Capabilities.Models;
            }

            var models = (await discovery.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false))
                .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
                .DistinctBy(static model => model.Id, StringComparer.Ordinal)
                .ToArray();
            foreach (var model in models)
            {
                model.Validate();
            }

            _discoveredTranscriptionModels[normalizedEngineId] = models;
            PublishSnapshot(Snapshot);
            return models;
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    /// <summary>
    /// Explicit model acquisition; progress is projected through normal runtime snapshots.
    /// </summary>
    /// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download</remarks>
    public async ValueTask InstallTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var local = RequireLocalTranscription(engineId);
        await local.InstallAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#modes.online
    public async ValueTask InstallDiarizationAssetsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var assets = _diarizationAssets ?? throw new TranscriptionCommandException("diarization_runtime_missing", "Speaker processing is unavailable in this package.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await assets.InstallAsync(http, cancellationToken).ConfigureAwait(false);
        PublishSnapshot(Snapshot);
    }

    public ValueTask CancelTranscriptionModelInstallAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        RequireLocalTranscription(engineId).CancelInstall(modelId);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RemoveTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var local = RequireLocalTranscription(engineId);
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await local.RemoveAsync(modelId, cancellationToken).ConfigureAwait(false);
            var preferences = (_settings.Transcription ?? TranscriptionPreferences.Default)
                .Canonicalize();
            var effectiveModelId = preferences.GetModelId(LocalWhisperTranscriptionEngine.EngineId)
                ?? local.Catalog.RecommendedModel.Id;
            if (preferences.AutomaticEnabled
                && string.Equals(
                    preferences.SelectedEngineId,
                    LocalWhisperTranscriptionEngine.EngineId,
                    StringComparison.Ordinal)
                && string.Equals(
                    effectiveModelId,
                    modelId,
                    StringComparison.Ordinal))
            {
                await ApplyReleaseV2SettingsAsync(
                        release => release with
                        {
                            Transcription = preferences with { AutomaticEnabled = false }
                        },
                        applyAutostart: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                PublishSnapshot(Snapshot);
            }
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    /// <summary>
    /// Authorizes one retry of the selected manual local job under a platform-reported power
    /// warning. The authorization is memory-only and bound to the exact durable job.
    /// </summary>
    /// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
    public async ValueTask ConfirmLocalTranscriptionPowerOverrideAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A recording session id is required.", nameof(sessionId));
        }

        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = FindTranscriptionSession(sessionId);
            var current = session.CurrentTranscription
                ?? throw new TranscriptionCommandException(
                    "job_unavailable",
                    "This recording has no local transcription job to continue.");
            if (!string.Equals(current.EngineId, LocalWhisperTranscriptionEngine.EngineId, StringComparison.Ordinal)
                || current.Status != TranscriptionJobStatus.AttentionRequired
                || !string.Equals(
                    current.StableErrorCode,
                    "low_power_override_required",
                    StringComparison.Ordinal))
            {
                throw new TranscriptionCommandException(
                    "power_override_unavailable",
                    "This local transcription job has no pending power warning.");
            }

            var local = RequireLocalTranscription(current.EngineId);
            local.AuthorizeOneShotPowerOverride(current.JobId);
            var requeued = false;
            try
            {
                requeued = await (_transcriptionJobRepository
                        ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                    .RequeueAsync(current.JobId, _timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                if (!requeued)
                {
                    throw new TranscriptionCommandException(
                        "power_override_unavailable",
                        "The local transcription job could not be resumed.");
                }
            }
            finally
            {
                if (!requeued)
                {
                    local.RevokeOneShotPowerOverride(current.JobId);
                }
            }

            _transcriptionWorker?.Signal();
            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask TranscribeRecentRecordingAsync(
        Guid sessionId,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A recording session id is required.", nameof(sessionId));
        }

        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = FindTranscriptionSession(sessionId);
            var preferences = (_settings.Transcription ?? TranscriptionPreferences.Default).Canonicalize();
            if (preferences.Mode == "off")
                throw new TranscriptionCommandException("engine_not_selected", "Enable transcription in settings first.");
            await EnsureSpeakerAssetsReadyAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSelectedEngineModelsDiscoveredAsync(preferences, cancellationToken)
                .ConfigureAwait(false);
            await (_transcriptionJobEnqueuer
                    ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                .EnqueueAsync(
                    session,
                    preferences,
                    _discoveredTranscriptionModels,
                    replaceExisting,
                    cancellationToken)
                .ConfigureAwait(false);
            _transcriptionWorker?.Signal();
            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask CancelTranscriptionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = FindTranscriptionSession(sessionId).CurrentTranscription;
            if (current is null)
            {
                return;
            }

            if (await (_transcriptionJobRepository
                    ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                .CancelAsync(current.JobId, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
            {
                _transcriptionWorker?.CancelActiveJob(current.JobId);
                PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
            }
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    public async ValueTask RetryTranscriptionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _transcriptionCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = FindTranscriptionSession(sessionId);
            var current = session.CurrentTranscription
                ?? throw new TranscriptionCommandException(
                    "job_unavailable",
                    "This recording has no transcription job to retry.");
            if (TranscriptionJobEnqueuer.RequiresConfigurationSupersede(current.StableErrorCode))
            {
                var preferences = (_settings.Transcription ?? TranscriptionPreferences.Default)
                    .Canonicalize();
                if (!string.Equals(
                        preferences.SelectedEngineId,
                        current.EngineId,
                        StringComparison.Ordinal))
                {
                    throw new TranscriptionCommandException(
                        "provider_change_requires_new_run",
                        "Start a new transcription when changing the provider.");
                }

                await EnsureSelectedEngineModelsDiscoveredAsync(preferences, cancellationToken)
                    .ConfigureAwait(false);
                await (_transcriptionJobEnqueuer
                        ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                    .SupersedeConfigurationAsync(
                        current.JobId,
                        session,
                        preferences,
                        _discoveredTranscriptionModels,
                        cancellationToken)
                    .ConfigureAwait(false);
                _transcriptionWorker?.Signal();
                PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
                return;
            }

            if (await (_transcriptionJobRepository
                    ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                .RequeueAsync(current.JobId, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
            {
                _transcriptionWorker?.Signal();
                PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
            }
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    private static ReleaseV2Settings MergeUserSettings(
        ReleaseV2Settings current,
        RuntimeUserSettingsUpdate update) =>
        (current with
        {
            ServiceEnabled = update.ServiceEnabled,
            Autostart = update.Autostart,
            Notifications = update.Notifications,
            Language = update.Language,
            Theme = update.Theme,
            MicrophoneDeviceId = update.MicrophoneDeviceId,
            FollowSystemDefaultMicrophone = update.FollowSystemDefaultMicrophone,
            RecordingsFolder = update.RecordingsFolder,
            Applications = update.Applications ?? []
        }).Canonicalize();

    private async ValueTask LoadTranscriptionCredentialPresenceAsync(CancellationToken cancellationToken)
    {
        _transcriptionCredentialPresence.Clear();
        foreach (var engine in _transcriptionEngines.Engines.Where(static engine =>
                     engine.Capabilities.ExecutionKind == TranscriptionExecutionKind.Remote))
        {
            try
            {
                var value = await _secretVault.ReadAsync(
                        RemoteTranscriptionSecrets.ForEngine(engine.Capabilities.EngineId),
                        cancellationToken)
                    .ConfigureAwait(false);
                _transcriptionCredentialPresence[engine.Capabilities.EngineId] =
                    !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _transcriptionCredentialPresence[engine.Capabilities.EngineId] = false;
                _logger.Error(exception, "A transcription credential could not be read from the platform vault.");
            }
        }
    }

    private IReadOnlyList<TranscriptionModelCapability> GetRuntimeModels(ITranscriptionEngine engine) =>
        _discoveredTranscriptionModels.TryGetValue(engine.Capabilities.EngineId, out var discovered)
            ? discovered
            : engine.Capabilities.Models;

    private async ValueTask EnsureSpeakerAssetsReadyAsync(CancellationToken cancellationToken)
    {
        if (_diarizationAssets is not null && !await _diarizationAssets.VerifyAsync(cancellationToken).ConfigureAwait(false))
            throw new TranscriptionCommandException("diarization_assets_missing", "Install speaker processing components in transcription settings.");
    }

    private async ValueTask EnsureSelectedEngineModelsDiscoveredAsync(
        TranscriptionPreferences preferences,
        CancellationToken cancellationToken)
    {
        var engineId = preferences.SelectedEngineId
            ?? throw new TranscriptionCommandException(
                "engine_not_selected",
                "Choose a transcription engine first.");
        if (_discoveredTranscriptionModels.ContainsKey(engineId))
        {
            return;
        }

        var engine = _transcriptionEngines.GetRequired(engineId);
        if (!engine.Capabilities.SupportsModelDiscovery
            || engine is not ITranscriptionModelDiscovery discovery)
        {
            return;
        }

        if (engine.Capabilities.ExecutionKind == TranscriptionExecutionKind.Remote)
        {
            if (!_transcriptionCredentialPresence.GetValueOrDefault(engineId))
            {
                throw new TranscriptionCommandException(
                    "credential_required",
                    "Save an API key for the selected transcription provider.");
            }

            var revision = RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(engineId);
            if (!preferences.HasAcceptedDisclosure(engineId, revision))
            {
                throw new TranscriptionCommandException(
                    "remote_consent_required",
                    "Accept the current remote audio-transfer disclosure before transcription.");
            }
        }

        var models = (await discovery.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false))
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(static model => model.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var model in models)
        {
            model.Validate();
        }

        _discoveredTranscriptionModels[engineId] = models;
        PublishSnapshot(Snapshot);
    }

    private void EnsureAutomaticTranscriptionReady(TranscriptionPreferences preferences)
    {
        var engineId = preferences.SelectedEngineId
            ?? throw new TranscriptionCommandException(
                "engine_not_selected",
                "Choose a transcription engine before enabling automatic transcription.");
        var engine = _transcriptionEngines.GetRequired(engineId);
        if (engine.Capabilities.ExecutionKind == TranscriptionExecutionKind.Remote)
        {
            if (!_transcriptionCredentialPresence.GetValueOrDefault(engineId))
            {
                throw new TranscriptionCommandException(
                    "credential_required",
                    "Save an API key before enabling automatic transcription.");
            }

            var disclosureRevision = RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(engineId);
            if (!preferences.HasAcceptedDisclosure(engineId, disclosureRevision))
            {
                throw new TranscriptionCommandException(
                    "remote_consent_required",
                    "Accept the current remote audio-transfer disclosure first.");
            }
        }
        else
        {
            var local = RequireLocalTranscription(engineId);
            var selectedModelId = preferences.GetModelId(engineId)
                ?? local.Catalog.RecommendedModel.Id;
            var installed = local.GetModelSnapshots().FirstOrDefault(model => string.Equals(
                model.ModelId,
                selectedModelId,
                StringComparison.Ordinal));
            if (installed is not { IsVerified: true, State: RuntimeLocalModelState.Installed })
            {
                throw new TranscriptionCommandException(
                    "model_missing",
                    "Install and verify the selected local transcription model before enabling automatic transcription.");
            }
        }

        var modelId = preferences.GetModelId(engineId);
        var models = GetRuntimeModels(engine);
        if (modelId is not null && !models.Any(model => string.Equals(
                model.Id,
                modelId,
                StringComparison.Ordinal)))
        {
            throw new TranscriptionCommandException(
                "model_unavailable",
                "Refresh the speech-to-text model list before enabling automatic transcription.");
        }

        if (modelId is null && models.Count == 0)
        {
            throw new TranscriptionCommandException(
                "model_discovery_required",
                "Refresh the speech-to-text model list before enabling automatic transcription.");
        }
    }

    private string RequireRemoteEngineId(string engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            throw new ArgumentException("A transcription engine id is required.", nameof(engineId));
        }

        var normalized = engineId.Trim().ToLowerInvariant();
        var engine = _transcriptionEngines.GetRequired(normalized);
        if (engine.Capabilities.ExecutionKind != TranscriptionExecutionKind.Remote)
        {
            throw new TranscriptionCommandException(
                "credential_not_supported",
                "The selected transcription engine does not use an API key.");
        }

        _ = RemoteTranscriptionSecrets.ForEngine(normalized);
        return normalized;
    }

    private LocalTranscriptionServices RequireLocalTranscription(string engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId)
            || !string.Equals(
                engineId.Trim(),
                LocalWhisperTranscriptionEngine.EngineId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionCommandException(
                "local_engine_required",
                "Choose the on-device transcription engine for this model action.");
        }

        return _localTranscription
            ?? throw new TranscriptionCommandException(
                "local_runtime_unavailable",
                "The packaged local transcription runtime is unavailable.");
    }

    private static string NormalizeCredential(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("An API key is required.", nameof(credential));
        }

        var normalized = credential.Trim();
        if (normalized.Length is < 8 or > 2048
            || normalized.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException("The API key format is invalid.", nameof(credential));
        }

        return normalized;
    }

    private MeetingSessionListItem FindTranscriptionSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A recording session id is required.", nameof(sessionId));
        }

        var normalizedId = sessionId.ToString("N");
        return ListRecentSessionItems(limit: 4096, offset: 0)
            .FirstOrDefault(session => string.Equals(session.Id, normalizedId, StringComparison.Ordinal))
            ?? throw new TranscriptionCommandException(
                "recording_unavailable",
                "The selected recording is unavailable.");
    }

    private async ValueTask ApplyReleaseV2SettingsAsync(
        Func<ReleaseV2Settings, ReleaseV2Settings> update,
        bool applyAutostart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _settings;
            var canonical = CanonicalizeReleaseV2Settings(update(previous));
            var shouldApplyAutostart = applyAutostart &&
                (canonical.Autostart != previous.Autostart ||
                 (!previous.OnboardingCompleted && canonical.OnboardingCompleted));
            AutostartRegistrationState? previousAutostart = null;
            if (shouldApplyAutostart)
            {
                var desiredAutostart = canonical.Autostart;
                previousAutostart = await _autostartService
                    .GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var appliedAutostart = await _autostartService
                        .SetEnabledAsync(desiredAutostart, cancellationToken)
                        .ConfigureAwait(false);
                    if (appliedAutostart.IsEnabled != desiredAutostart)
                    {
                        throw new InvalidOperationException(
                            "The launch-at-login service returned an unexpected state.");
                    }

                    canonical = canonical with
                    {
                        Autostart = appliedAutostart.IsEnabled,
                        AutostartPreference = desiredAutostart,
                        PackagedAutostartObserved = _autostartService is IAutostartStateReconciler
                            ? appliedAutostart.IsEnabled
                            : null
                    };
                }
                catch (Exception exception)
                {
                    var rollbackException = await TryRestoreAutostartAsync(
                        previousAutostart.IsEnabled).ConfigureAwait(false);
                    _logger.Error(exception, "Failed to apply the platform launch-at-login state.");
                    if (rollbackException is not null)
                    {
                        throw new AggregateException(
                            "Applying and rolling back launch-at-login both failed.",
                            exception,
                            rollbackException);
                    }

                    throw;
                }
            }
            else
            {
                canonical = canonical with
                {
                    Autostart = previous.Autostart,
                    AutostartPreference = previous.AutostartPreference ?? previous.Autostart,
                    PackagedAutostartObserved = previous.PackagedAutostartObserved
                };
            }

            var persisted = _legacySettings.WithReleaseV2Projection(canonical);
            try
            {
                await _settingsStore
                    .SaveReleaseV2Async(persisted, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (previousAutostart is not null)
            {
                var rollbackException = await TryRestoreAutostartAsync(
                    previousAutostart.IsEnabled).ConfigureAwait(false);
                _logger.Error(exception, "Settings persistence failed after launch-at-login changed.");
                if (rollbackException is not null)
                {
                    throw new AggregateException(
                        "Saving settings and rolling back launch-at-login both failed.",
                        exception,
                        rollbackException);
                }

                throw;
            }

            _settings = canonical;
            _legacySettings = persisted;
            if (previous.ServiceEnabled && !canonical.ServiceEnabled)
            {
                await StopDetectionAsync(cancellationToken).ConfigureAwait(false);
            }

            _detectionCoordinator?.UpdateApplicationPreferences(GetEffectiveApplicationPreferences());
            _audioPlatform.UpdateWatchedProcessNames(GetWatchedProcessNames());
            var recordingBusy = _recordingCoordinator?.IsBusy == true;
            PublishSnapshot(recordingBusy
                || _hasActionableAttention
                || IsTerminalFinalization(Snapshot.RecordingFinalization)
                ? Snapshot
                : BuildIdleSnapshot());
            if (canonical.OnboardingCompleted
                && canonical.ServiceEnabled
                && _detectionCoordinator is null
                && !recordingBusy)
            {
                await StartDetectionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _settingsUpdateGate.Release();
        }
    }

    private async ValueTask<Exception?> TryRestoreAutostartAsync(bool enabled)
    {
        try
        {
            var restored = await _autostartService
                .SetEnabledAsync(enabled, CancellationToken.None)
                .ConfigureAwait(false);
            return restored.IsEnabled == enabled
                ? null
                : new InvalidOperationException("The launch-at-login rollback returned an unexpected state.");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to roll back the platform launch-at-login state.");
            return exception;
        }
    }

    public async ValueTask ResolveMeetingPromptAsync(
        string candidateId,
        MeetingPromptUserAction action,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var coordinator = _detectionCoordinator;
        if (coordinator?.Snapshot.PendingPrompt is not { } pending ||
            !string.Equals(pending.Candidate.Frame.CandidateId, candidateId, StringComparison.Ordinal))
        {
            return;
        }

        var resolution = action switch
        {
            MeetingPromptUserAction.Record => MeetingPromptResolution.Record,
            MeetingPromptUserAction.Skip => MeetingPromptResolution.Skip,
            MeetingPromptUserAction.IgnoreApplication => MeetingPromptResolution.IgnoreApplication,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown prompt action.")
        };
        var resolved = await coordinator.ResolvePromptAsync(
            candidateId,
            resolution,
            TimeProvider.System.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!resolved)
        {
            return;
        }

        if (action == MeetingPromptUserAction.Record)
        {
            var recordingStarted = false;
            var recordingGateClaimed = false;
            try
            {
                await BeginRecordingGateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                recordingGateClaimed = true;
                recordingStarted = await (_recordingCoordinator
                        ?? throw new InvalidOperationException("Recording coordinator has not been initialized."))
                    .StartAskAsync(
                        pending.Candidate.Frame.DisplayName,
                        pending.Candidate.Frame.RootProcessId,
                        pending.Candidate.Frame.ProcessName,
                        cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!recordingStarted)
                {
                    if (recordingGateClaimed)
                    {
                        await CompleteRecordingGateAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await coordinator.EndActiveSessionAsync(
                            candidateId,
                            TimeProvider.System.GetUtcNow(),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        if (action == MeetingPromptUserAction.IgnoreApplication)
        {
            await PersistIgnoredProfileAsync(
                pending.Candidate.Frame.ProfileId,
                pending.Candidate.Frame.DisplayName,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogEvent(
            "Info",
            "DETECTION_PROMPT_RESOLVED",
            "Meeting prompt was resolved by a local user action.",
            metadata: new Dictionary<string, object?>
            {
                ["candidate_id"] = candidateId,
                ["profile_id"] = pending.Candidate.Frame.ProfileId,
                ["resolution"] = resolution.ToString().ToLowerInvariant()
            });
    }

    public async ValueTask StartManualRecordingAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await BeginRecordingGateAsync(candidateId: null, cancellationToken).ConfigureAwait(false);
        var started = false;
        try
        {
            if (_detectionCoordinator is not null)
            {
                await StopDetectionAsync(cancellationToken).ConfigureAwait(false);
            }

            started = await (_recordingCoordinator
                    ?? throw new InvalidOperationException("Recording coordinator has not been initialized."))
                .StartManualAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!started)
            {
                await CompleteRecordingGateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public ValueTask PauseOrResumeAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return (_recordingCoordinator
                ?? throw new InvalidOperationException("Recording coordinator has not been initialized."))
            .PauseOrResumeAsync(cancellationToken);
    }

    public async ValueTask FinishRecordingAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await (_recordingCoordinator
                ?? throw new InvalidOperationException("Recording coordinator has not been initialized."))
            .FinishForRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await CompleteRecordingGateAsync(cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    public async ValueTask DiscardRecordingAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var coordinator = _recordingCoordinator
            ?? throw new InvalidOperationException("Recording coordinator has not been initialized.");
        var activeSessionId = coordinator.ActiveSessionId;
        await coordinator.DiscardAsync(cancellationToken).ConfigureAwait(false);
        if (activeSessionId.HasValue && coordinator.ActiveSessionId is null)
        {
            await CompleteRecordingGateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The repository enters `file_deletion_pending` before any filesystem mutation. Cancellation
    // is consequently honored while waiting for the gate and ignored after the durable checkpoint.
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public async ValueTask RemoveRecentRecordingAsync(
        Guid sessionId,
        bool deleteAudioFile,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A recording session id is required.", nameof(sessionId));
        }

        await _recentRecordingMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentTranscription = ListRecentSessionItems(limit: 4096, offset: 0)
                .FirstOrDefault(session => string.Equals(
                    session.Id,
                    sessionId.ToString("N"),
                    StringComparison.Ordinal))
                ?.CurrentTranscription;
            if (currentTranscription is not null
                && currentTranscription.Status is not (
                    TranscriptionJobStatus.Completed
                    or TranscriptionJobStatus.Cancelled
                    or TranscriptionJobStatus.Failed))
            {
                await (_transcriptionJobRepository
                        ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                    .CancelAsync(
                        currentTranscription.JobId,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                _transcriptionWorker?.CancelActiveJob(currentTranscription.JobId);
            }

            var repository = _sessionRepository
                ?? throw new InvalidOperationException("Meeting session repository has not been initialized.");
            var workItem = repository.TryBeginRecentRecordingRemoval(
                sessionId.ToString("N"),
                deleteAudioFile,
                DateTimeOffset.UtcNow);
            if (workItem is null)
            {
                PublishAfterRecentRecordingRemoval(sessionId);
                return;
            }

            if (deleteAudioFile)
            {
                try
                {
                    (_artifactDeletionService
                        ?? throw new InvalidOperationException("Recording artifact deletion is unavailable."))
                        .DeleteRecentArtifacts(workItem);
                    if (!repository.TryCompleteRecentRecordingRemoval(
                            workItem.Id,
                            DateTimeOffset.UtcNow))
                    {
                        throw new InvalidOperationException(
                            "The completed recording removal could not be checkpointed.");
                    }
                }
                catch (Exception exception)
                {
                    repository.TryMarkRecentRecordingRemovalFailed(
                        workItem.Id,
                        exception.Message,
                        DateTimeOffset.UtcNow);
                    SetActionableAttention(sessionId, exception.Message);
                    PublishSnapshot(Snapshot with
                    {
                        Activity = Snapshot.ActiveMeeting is null
                            ? ApplicationActivityState.AttentionRequired
                            : Snapshot.Activity,
                        RecentRecordings = LoadRecentRecordings(),
                        AttentionMessage = exception.Message
                    });
                    throw;
                }
            }

            PublishAfterRecentRecordingRemoval(sessionId);
            _logger.LogEvent(
                "Info",
                "RECENT_RECORDING_REMOVED",
                deleteAudioFile
                    ? "A recording history row and its app-owned audio artifacts were removed."
                    : "A recording history row was hidden while its audio artifacts were retained.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = workItem.Id,
                    ["audio_deleted"] = deleteAudioFile
                });
        }
        finally
        {
            _recentRecordingMutationGate.Release();
        }
    }

    // The display title stays in persistence while source metadata and the artifact filename remain
    // stable. The shared mutation gate keeps rename and removal atomic on the SQLite connection.
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    public async ValueTask RenameRecentRecordingAsync(
        Guid sessionId,
        string displayTitle,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A recording session id is required.", nameof(sessionId));
        }

        var normalizedTitle = RecentRecordingTitle.Normalize(displayTitle);
        await _recentRecordingMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repository = _sessionRepository
                ?? throw new InvalidOperationException("Meeting session repository has not been initialized.");
            if (!repository.TryRenameRecentRecording(
                    sessionId.ToString("N"),
                    normalizedTitle,
                    DateTimeOffset.UtcNow))
            {
                throw new InvalidOperationException(
                    "Only a completed recent recording can be renamed.");
            }

            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
            _logger.LogEvent(
                "Info",
                "RECENT_RECORDING_RENAMED",
                "A recent recording display title was updated.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId.ToString("N")
                });
        }
        finally
        {
            _recentRecordingMutationGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    public ValueTask AcknowledgeAttentionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var clearsFinalization = sessionId != Guid.Empty
            && Snapshot.RecordingFinalization is
            {
                SessionId: var currentSessionId,
                Stage: RecordingArtifactStage.AttentionRequired
            }
            && currentSessionId == sessionId;
        var clearsRuntimeAttention = _hasActionableAttention
            && (sessionId == Guid.Empty
                ? true
                : _actionableAttentionSessionId == sessionId);
        if (!clearsFinalization && !clearsRuntimeAttention)
        {
            return ValueTask.CompletedTask;
        }

        if (clearsRuntimeAttention)
        {
            ClearActionableAttention();
        }

        var next = clearsFinalization || !IsTerminalFinalization(Snapshot.RecordingFinalization)
            ? BuildCoherentPostFinalizationSnapshot()
            : Snapshot;
        PublishSnapshot(next with
        {
            RecordingFinalization = clearsFinalization ? null : next.RecordingFinalization,
            RecentRecordings = LoadRecentRecordings()
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task? recordingGateCompletion;
        CancellationTokenSource? readyFinalizationClear;
        lock (_recordingGateSync)
        {
            _disposing = true;
            recordingGateCompletion = _recordingGateCompletionTask;
            readyFinalizationClear = _readyFinalizationClearCancellation;
            _readyFinalizationClearCancellation = null;
        }

        readyFinalizationClear?.Cancel();

        if (recordingGateCompletion is not null)
        {
            try
            {
                await recordingGateCompletion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Recording gate completion failed during runtime disposal.");
            }
        }

        _audioPlatform.SnapshotChanged -= HandleAudioPlatformSnapshotChanged;
        await _capabilityLifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopDetectionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _capabilityLifecycleGate.Release();
        }
        _recoveryCancellation?.Cancel();
        if (_recoveryTask is not null)
        {
            await _recoveryTask.ConfigureAwait(false);
        }

        _recoveryCancellation?.Dispose();
        _recoveryCancellation = null;
        _recoveryTask = null;
        if (_transcriptionWorker is not null)
        {
            _transcriptionWorker.StateChanged -= HandleTranscriptionStateChanged;
            await _transcriptionWorker.DisposeAsync().ConfigureAwait(false);
        }

        _transcriptionWorker = null;
        _transcriptionJobEnqueuer = null;
        if (_localTranscription is not null)
        {
            _localTranscription.StateChanged -= HandleLocalTranscriptionStateChanged;
            await _localTranscription.DisposeAsync().ConfigureAwait(false);
        }

        _transcriptionJobRepository = null;
        if (_recordingCoordinator is not null)
        {
            _recordingCoordinator.SnapshotChanged -= HandleRecordingSnapshotChanged;
            await _recordingCoordinator.DisposeAsync().ConfigureAwait(false);
        }

        _recordingCoordinator = null;
        _activeRecordingCapabilityIssue = null;
        _persistentCaptureCapabilityIssue = null;
        _detectionMicrophoneCaptureUnavailable = false;
        _artifactDeletionService = null;

        await _audioPlatform.DisposeAsync().ConfigureAwait(false);
        _databaseConnection?.Dispose();
        _databaseConnection = null;
        _appRuleRepository = null;
        _sessionRepository = null;
        lock (_recordingGateSync)
        {
            _activeDetectionCandidateId = null;
            _activeDetectionLossStartedAtUtc = null;
            _activeDetectionAutoFinishScheduled = false;
            _recordingGateCompletionTask = null;
            _recordingGateGeneration++;
        }
        _initialized = false;
        if (_ownsTranscriptionHttpClient)
        {
            _transcriptionHttpClient?.Dispose();
        }

        _logger.LogEvent(
            "Info",
            "V2_RUNTIME_STOPPED",
            "Release v2 runtime stopped cleanly.");
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    private async ValueTask ResetFailedInitializationAsync()
    {
        _initialized = false;
        _audioPlatform.SnapshotChanged -= HandleAudioPlatformSnapshotChanged;
        CancelReadyFinalizationClear();

        _recoveryCancellation?.Cancel();
        if (_recoveryTask is not null)
        {
            try
            {
                await _recoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed initialization recovery work did not stop cleanly.");
            }
        }

        _recoveryCancellation?.Dispose();
        _recoveryCancellation = null;
        _recoveryTask = null;

        if (_transcriptionWorker is { } transcriptionWorker)
        {
            _transcriptionWorker = null;
            transcriptionWorker.StateChanged -= HandleTranscriptionStateChanged;
            try
            {
                await transcriptionWorker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Partially initialized transcription services did not stop cleanly.");
            }
        }

        _transcriptionJobEnqueuer = null;
        if (_localTranscription is null)
        {
            _transcriptionJobRepository = null;
        }

        await _capabilityLifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_detectionCoordinator is { } detection)
            {
                _detectionCoordinator = null;
                detection.SnapshotChanged -= HandleDetectionSnapshotChanged;
                try
                {
                    await detection.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Partially initialized detection services did not stop cleanly.");
                }

                try
                {
                    await detection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Partially initialized detection services did not dispose cleanly.");
                }
            }
        }
        finally
        {
            _capabilityLifecycleGate.Release();
        }

        if (_recordingCoordinator is { } recording)
        {
            _recordingCoordinator = null;
            recording.SnapshotChanged -= HandleRecordingSnapshotChanged;
            try
            {
                await recording.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Partially initialized recording services did not stop cleanly.");
            }
        }

        _artifactDeletionService = null;
        try
        {
            _databaseConnection?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Partially initialized persistence did not dispose cleanly.");
        }

        _databaseConnection = null;
        _appRuleRepository = null;
        _sessionRepository = null;
        _appRules = [];
        _profiles = new MeetingProfileRegistry();
        ClearActionableAttention();
        _activeRecordingCapabilityIssue = null;
        _persistentCaptureCapabilityIssue = null;
        _detectionMicrophoneCaptureUnavailable = false;
        _lastDetectionDiagnosticFingerprint = null;
        _lastDetectionDiagnosticAtUtc = default;
        Interlocked.Exchange(ref _audioProjectionPublicationGeneration, 0);
        lock (_recordingGateSync)
        {
            _activeDetectionCandidateId = null;
            _recordingGateCompletionTask = null;
            _recordingGateGeneration++;
            _disposing = false;
        }

        Snapshot = ApplicationRuntimeSnapshot.Initial;
    }

    private ApplicationRuntimeSnapshot BuildIdleSnapshot()
    {
        var serviceEnabled = _settings.ServiceEnabled;
        var listeningEnabled = _settings.OnboardingCompleted && serviceEnabled;
        var capability = ApplyActiveRecordingCapabilityIssue(
            ToRuntimeCapability(_audioPlatform.Snapshot));
        var capabilityRequiresAttention = listeningEnabled
            && IsActionableCapability(capability);
        var requiresAttention = _hasActionableAttention || capabilityRequiresAttention;
        return new ApplicationRuntimeSnapshot(
            requiresAttention
                ? ApplicationActivityState.AttentionRequired
                : listeningEnabled ? ApplicationActivityState.Listening : ApplicationActivityState.Paused,
            serviceEnabled,
            _settings.Theme,
            ActiveMeeting: null,
            RecentRecordings: LoadRecentRecordings(),
            AttentionMessage: _hasActionableAttention
                ? _actionableAttentionMessage
                : capabilityRequiresAttention && capability.Issue == RuntimeCapabilityIssue.PlatformBlocked
                    ? capability.BlockingReason ?? capability.Summary
                    : null,
            PendingMeetingPrompt: null)
        {
            Capability = capability
        };
    }

    private IReadOnlyList<RecentRecordingSnapshot> LoadRecentRecordings() =>
        _sessionRepository is null
            ? []
            : ListRecentSessionItems(limit: 12, offset: 0)
            .Where(static item => !string.Equals(
                item.RecordingStatus,
                "discarded",
                StringComparison.OrdinalIgnoreCase))
            .Select(ToRecentRecording)
            .ToArray();

    private IReadOnlyList<MeetingSessionListItem> ListRecentSessionItems(int limit, int offset)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        return new MeetingSessionRepository(connection).ListRecent(limit, offset);
    }

    private RecentRecordingSnapshot ToRecentRecording(MeetingSessionListItem item)
    {
        var parsedId = Guid.TryParseExact(item.Id, "N", out var id) ? id : Guid.Empty;
        var recordingStatus = (item.RecordingStatus ?? string.Empty).Trim().ToLowerInvariant();
        var primaryAudioPath = FirstExistingPath(
            item.PrimaryAudioPath,
            item.AudioMixPath,
            item.AudioOutputPath,
            item.AudioMicPath);
        var transcriptMarkdownPath = FirstExistingPath(item.TranscriptMarkdownPath);
        var transcriptJsonPath = FirstExistingPath(item.TranscriptJsonPath);
        var hasLegacyTranscript = transcriptMarkdownPath is not null || transcriptJsonPath is not null;
        var currentTranscriptionRequiresAttention = item.CurrentTranscription?.Status is
            TranscriptionJobStatus.AttentionRequired or TranscriptionJobStatus.Failed;
        var readyStatus = recordingStatus is "ready" or "saved";
        var readyArtifactMissing = readyStatus
            && primaryAudioPath is null
            && !hasLegacyTranscript;
        var requiresAttention = readyArtifactMissing
            || currentTranscriptionRequiresAttention
            || recordingStatus is "failed" or "attention_required" or "discarding" or "file_deletion_pending"
            || !string.IsNullOrWhiteSpace(item.ArtifactErrorCode);
        var state = requiresAttention
            ? RecentRecordingState.AttentionRequired
            : readyStatus && (primaryAudioPath is not null || hasLegacyTranscript)
                ? RecentRecordingState.Ready
                : recordingStatus is "recording" or "stopping" or "processing" or "verifying" or "promoting"
                    ? RecentRecordingState.Processing
                    : RecentRecordingState.Unspecified;
        return new RecentRecordingSnapshot(
            parsedId,
            item.SourceApp ?? string.Empty,
            item.StartedAtUtc ?? DateTimeOffset.MinValue,
            TimeSpan.FromSeconds(Math.Max(0, item.DurationSeconds ?? 0)),
            primaryAudioPath,
            requiresAttention)
        {
            DisplayTitle = string.IsNullOrWhiteSpace(item.DisplayTitle)
                ? item.SourceApp ?? string.Empty
                : item.DisplayTitle,
            State = state,
            // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
            LegacyTranscriptMarkdownPath = transcriptMarkdownPath,
            LegacyTranscriptJsonPath = transcriptJsonPath,
            TranscriptMarkdownPath = transcriptMarkdownPath,
            TranscriptJsonPath = transcriptJsonPath,
            Transcription = item.CurrentTranscription is null
                ? null
                : AttachLocalDiagnostics(ToRuntimeTranscriptionJob(item.CurrentTranscription))
        };
    }

    private RuntimeTranscriptionJobSnapshot AttachLocalDiagnostics(
        RuntimeTranscriptionJobSnapshot snapshot)
    {
        if (snapshot.LocalDiagnostics is not null
            || _localTranscription is null
            || !string.Equals(snapshot.EngineId, LocalWhisperTranscriptionEngine.EngineId, StringComparison.Ordinal)
            || !_localTranscription.TryGetDiagnostics(snapshot.JobId, out var diagnostics))
        {
            return snapshot;
        }

        return snapshot with { LocalDiagnostics = diagnostics };
    }

    internal static RuntimeTranscriptionJobSnapshot ToRuntimeTranscriptionJob(
        CurrentTranscriptionJobListItem job) => new(
        job.JobId,
        job.EngineId,
        job.ModelId,
        job.Status switch
        {
            TranscriptionJobStatus.Queued => RuntimeTranscriptionJobState.Queued,
            TranscriptionJobStatus.Preparing => RuntimeTranscriptionJobState.Preparing,
            TranscriptionJobStatus.Uploading => RuntimeTranscriptionJobState.Uploading,
            TranscriptionJobStatus.Processing or TranscriptionJobStatus.Finalizing =>
                RuntimeTranscriptionJobState.Processing,
            TranscriptionJobStatus.RetryScheduled => RuntimeTranscriptionJobState.RetryScheduled,
            TranscriptionJobStatus.AttentionRequired => RuntimeTranscriptionJobState.AttentionRequired,
            TranscriptionJobStatus.Completed => RuntimeTranscriptionJobState.Completed,
            TranscriptionJobStatus.Cancelled => RuntimeTranscriptionJobState.Cancelled,
            TranscriptionJobStatus.Failed => RuntimeTranscriptionJobState.Failed,
            _ => RuntimeTranscriptionJobState.NotStarted
        },
        Math.Clamp(job.Progress, 0, 1),
        job.CurrentChunkIndex,
        job.NextAttemptAtUtc,
        job.StableErrorCode,
        job.ErrorMessage,
        ParseTranscriptionUsage(job.UsageJson))
        {
            ChunkCount = Math.Max(0, job.ChunkCount),
            ProviderRequestId = SanitizeProviderRequestId(job.ProviderRequestId),
            LocalDiagnostics = job.LocalDiagnostics is null
                ? null
                : new RuntimeLocalTranscriptionDiagnosticsSnapshot(
                    job.LocalDiagnostics.RequestedBackend,
                    job.LocalDiagnostics.ResolvedBackend,
                    job.LocalDiagnostics.ThreadCount,
                    job.LocalDiagnostics.RuntimeVersion,
                    job.LocalDiagnostics.NativeBundleManifestSha256,
                    job.LocalDiagnostics.ModelSha256,
                    job.LocalDiagnostics.ProcessingDurationMilliseconds)
        };

    private static string? SanitizeProviderRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var normalized = requestId.Trim();
        return normalized.Length <= MaximumProviderRequestIdLength
               && !LooksSensitiveProviderRequestId(normalized)
               && normalized.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static bool LooksSensitiveProviderRequestId(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sk-or-v1-", StringComparison.OrdinalIgnoreCase);

    internal static TranscriptionUsage? ParseTranscriptionUsage(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson) || usageJson.Length > 16 * 1024)
        {
            return null;
        }

        try
        {
            var usage = JsonSerializer.Deserialize<TranscriptionUsage>(
                usageJson,
                TranscriptionUsageSerializerOptions);
            usage?.Validate();
            return usage;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FirstExistingPath(params string?[] paths) =>
        paths.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private void PublishSnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        lock (_snapshotPublicationSync)
        {
            PublishSnapshotCore(snapshot);
        }
    }

    private void PublishSnapshotUpdate(
        Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_snapshotPublicationSync)
        {
            PublishSnapshotCore(update(Snapshot));
        }
    }

    private void PublishSnapshotWithLatestRecentRecordings()
    {
        lock (_snapshotPublicationSync)
        {
            PublishSnapshotCore(Snapshot with { RecentRecordings = LoadRecentRecordings() });
        }
    }

    private void PublishSnapshotCore(ApplicationRuntimeSnapshot snapshot)
    {
        var capability = ApplyActiveRecordingCapabilityIssue(
            ToRuntimeCapability(_audioPlatform.Snapshot));

        snapshot = snapshot with
        {
            ServiceEnabled = _settings.ServiceEnabled,
            Theme = _settings.Theme,
            Capability = capability,
            UserSettings = ToRuntimeUserSettings(_settings),
            AvailableMicrophones = GetAvailableMicrophones()
        };
        Snapshot = snapshot;
        var subscribers = SnapshotChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<ApplicationRuntimeSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Application runtime snapshot observer failed.");
            }
        }
    }

    private RuntimeCapabilitySnapshot ApplyActiveRecordingCapabilityIssue(
        RuntimeCapabilitySnapshot capability)
    {
        if (!_settings.ServiceEnabled && _activeRecordingCapabilityIssue is null)
        {
            return capability;
        }

        if (capability.State == RuntimeCapabilityState.Blocked
            || capability.Issue is RuntimeCapabilityIssue.NoActiveOutput
                or RuntimeCapabilityIssue.NoActiveMicrophone
                or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable)
        {
            return capability;
        }

        var recordingIssue = _activeRecordingCapabilityIssue
            ?? _persistentCaptureCapabilityIssue
            ?? (_detectionMicrophoneCaptureUnavailable
                ? RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
                : null);
        return recordingIssue is null
            ? capability
            : capability with
            {
                State = RuntimeCapabilityState.Degraded,
                Issue = recordingIssue.Value
            };
    }

    private RuntimeUserSettingsSnapshot ToRuntimeUserSettings(ReleaseV2Settings settings) => new(
        settings.OnboardingCompleted,
        settings.ServiceEnabled,
        settings.Autostart,
        settings.Notifications,
        settings.Language,
        settings.Theme,
        settings.MicrophoneDeviceId,
        settings.FollowSystemDefaultMicrophone,
        settings.RecordingsFolder,
        ToEffectiveApplicationPreferences(settings.Applications))
    {
        Transcription = ToRuntimeTranscriptionSettings(
            settings.Transcription ?? TranscriptionPreferences.Default)
    };

    private RuntimeTranscriptionSettingsSnapshot ToRuntimeTranscriptionSettings(
        TranscriptionPreferences preferences)
    {
        var canonical = preferences.Canonicalize();
        var engines = _transcriptionEngines.GetSupportedCurrentPlatform()
            .Select(engine =>
            {
                var capabilities = engine.Capabilities;
                var isRemote = capabilities.ExecutionKind == TranscriptionExecutionKind.Remote;
                var disclosureRevision = isRemote
                    ? RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(capabilities.EngineId)
                    : null;
                var snapshot = new RuntimeTranscriptionEngineSnapshot(
                    capabilities.EngineId,
                    capabilities.DisplayName,
                    capabilities.ExecutionKind,
                    capabilities.RequiresNetwork,
                    capabilities.PrivacyDisclosure,
                    isRemote
                        ? RemoteTranscriptionDisclosureCatalog.GetPolicyUri(capabilities.EngineId)
                        : null,
                    isRemote && _transcriptionCredentialPresence.GetValueOrDefault(capabilities.EngineId),
                    canonical.GetModelId(capabilities.EngineId),
                    GetRuntimeModels(engine),
                    capabilities.SupportsModelDiscovery,
                    isRemote && canonical.HasAcceptedDisclosure(
                        capabilities.EngineId,
                        disclosureRevision),
                    disclosureRevision);
                if (!string.Equals(
                        capabilities.EngineId,
                        LocalWhisperTranscriptionEngine.EngineId,
                        StringComparison.Ordinal)
                    || _localTranscription is null)
                {
                    return snapshot;
                }

                return snapshot with
                {
                    LocalModels = _localTranscription.GetModelSnapshots(),
                    LocalResources = _localTranscription.GetResourceSnapshot(
                        canonical.GetModelId(capabilities.EngineId))
                };
            })
            .ToArray();
        return new RuntimeTranscriptionSettingsSnapshot(
            canonical.SelectedEngineId,
            canonical.AutomaticEnabled,
            canonical.Language,
            canonical.RequireZeroDataRetention,
            engines);
    }

    private IReadOnlyList<RuntimeMicrophoneSnapshot> GetAvailableMicrophones() =>
        GetAvailableMicrophones(_audioPlatform.Snapshot);

    private static IReadOnlyList<RuntimeMicrophoneSnapshot> GetAvailableMicrophones(
        AudioPlatformSnapshot audioPlatform) =>
        audioPlatform.Microphones
            .Where(static microphone => microphone.IsActive && !string.IsNullOrWhiteSpace(microphone.Id))
            .GroupBy(static microphone => microphone.Id, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static microphone => microphone.IsDefault)
                .First())
            .Select(static microphone => new RuntimeMicrophoneSnapshot(
                microphone.Id,
                string.IsNullOrWhiteSpace(microphone.DisplayName)
                    ? microphone.Id
                    : microphone.DisplayName,
                microphone.IsDefault))
            .OrderByDescending(static microphone => microphone.IsDefault)
            .ThenBy(static microphone => microphone.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private void HandleAudioPlatformSnapshotChanged(object? sender, AudioPlatformSnapshot snapshot)
    {
        if (!_initialized || _disposing)
        {
            return;
        }

        ApplyAudioPlatformSnapshot(snapshot, forcePublish: false);
    }

    private void ApplyAudioPlatformSnapshot(AudioPlatformSnapshot audioPlatform, bool forcePublish)
    {
        var capability = ApplyActiveRecordingCapabilityIssue(ToRuntimeCapability(audioPlatform));
        var availableMicrophones = GetAvailableMicrophones(audioPlatform);
        var capabilityChanged = Snapshot.Capability != capability;
        var microphonesChanged = !Snapshot.AvailableMicrophones.SequenceEqual(availableMicrophones);
        var detectionLifecycleNeedsReconciliation =
            DetectionLifecycleNeedsReconciliation(capability);
        if (!forcePublish && !capabilityChanged && !microphonesChanged)
        {
            if (detectionLifecycleNeedsReconciliation)
            {
                Interlocked.Increment(ref _audioProjectionPublicationGeneration);
                ScheduleDetectionCapabilityReconciliation();
            }

            return;
        }

        Interlocked.Increment(ref _audioProjectionPublicationGeneration);
        if (capabilityChanged && ShouldRecomputeIdleForCapability(capability))
        {
            PublishSnapshot(BuildIdleSnapshot());
            ScheduleDetectionCapabilityReconciliation();
            return;
        }

        PublishSnapshot(Snapshot with
        {
            Capability = capability,
            AvailableMicrophones = availableMicrophones
        });
        if (capabilityChanged || detectionLifecycleNeedsReconciliation)
        {
            ScheduleDetectionCapabilityReconciliation();
        }
    }

    private bool DetectionLifecycleNeedsReconciliation(RuntimeCapabilitySnapshot capability)
    {
        if (!_initialized || _disposing)
        {
            return false;
        }

        if (_detectionCoordinator is not null)
        {
            return capability.State == RuntimeCapabilityState.Blocked
                || !capability.HasActiveOutput;
        }

        return _settings.OnboardingCompleted
            && _settings.ServiceEnabled
            && _recordingCoordinator?.IsBusy != true
            && capability.State != RuntimeCapabilityState.Blocked
            && capability.HasActiveOutput;
    }

    private bool ShouldRecomputeIdleForCapability(RuntimeCapabilitySnapshot capability)
    {
        if (_recordingCoordinator?.IsBusy == true
            || Snapshot.ActiveMeeting is not null
            || _hasActionableAttention
            || IsTerminalFinalization(Snapshot.RecordingFinalization))
        {
            return false;
        }

        if (capability.State == RuntimeCapabilityState.Blocked)
        {
            return true;
        }

        if (Snapshot.Activity is ApplicationActivityState.Listening or ApplicationActivityState.Paused)
        {
            return true;
        }

        var previousCapabilityMessage = Snapshot.Capability.BlockingReason ?? Snapshot.Capability.Summary;
        var capabilityAttentionMatches = Snapshot.Capability.Issue is
            RuntimeCapabilityIssue.NoActiveAudioEndpoints
            or RuntimeCapabilityIssue.NoActiveMicrophone
            or RuntimeCapabilityIssue.NoActiveOutput
            or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
            or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
            or RuntimeCapabilityIssue.OutputCaptureUnavailable
            ? Snapshot.AttentionMessage is null
            : string.Equals(
                Snapshot.AttentionMessage,
                previousCapabilityMessage,
                StringComparison.Ordinal);
        return Snapshot.Activity == ApplicationActivityState.AttentionRequired
            && IsActionableCapability(Snapshot.Capability)
            && capabilityAttentionMatches;
    }

    private IReadOnlyCollection<string> GetWatchedProcessNames()
    {
        if (!_settings.OnboardingCompleted || !_settings.ServiceEnabled)
        {
            return [];
        }

        var ignoredProfiles = GetEffectiveApplicationPreferences()
            .Where(static preference => preference.Policy == MeetingApplicationPolicy.Ignore)
            .Select(static preference => preference.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _profiles.Profiles
            .Where(profile => !ignoredProfiles.Contains(profile.Id))
            .SelectMany(static profile => profile.DedicatedProcessNames.Concat(profile.BrowserProcessNames))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    private void ScheduleDetectionCapabilityReconciliation()
    {
        if (!_initialized || _disposing)
        {
            return;
        }

        _ = ReconcileDetectionForCapabilitySafelyAsync();
    }

    private async Task ReconcileDetectionForCapabilitySafelyAsync()
    {
        try
        {
            await ReconcileDetectionForCapabilityAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Detection lifecycle could not follow an audio capability change.");
            if (!_disposing)
            {
                SetActionableAttention(sessionId: null, exception.Message);
                PublishSnapshot(BuildIdleSnapshot());
            }
        }
    }

    private async ValueTask ReconcileDetectionForCapabilityAsync(CancellationToken cancellationToken)
    {
        await _capabilityLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized || _disposing)
            {
                return;
            }

            var capability = ToRuntimeCapability(_audioPlatform.Snapshot);
            if (capability.State == RuntimeCapabilityState.Blocked || !capability.HasActiveOutput)
            {
                await StopDetectionAsync(cancellationToken).ConfigureAwait(false);
                if (ShouldRecomputeIdleForCapability(capability))
                {
                    PublishSnapshot(BuildIdleSnapshot());
                }

                return;
            }

            if (_settings.OnboardingCompleted
                && _settings.ServiceEnabled
                && _recordingCoordinator?.IsBusy != true)
            {
                await StartDetectionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _capabilityLifecycleGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    private async ValueTask ObserveOnboardingMicrophoneHealthAsync(
        CancellationToken cancellationToken)
    {
        await _microphoneHealthProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ShouldObserveOnboardingMicrophoneHealth())
            {
                return;
            }

            var coordinator = CreateDetectionCoordinator();
            using var observationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var observationTask = coordinator
                .ObserveMicrophoneCaptureHealthOnceAsync(observationCancellation.Token)
                .AsTask();
            var cleanupDeferred = false;
            try
            {
                MeetingSpeechCaptureHealth health;
                try
                {
                    health = await observationTask
                        .WaitAsync(_onboardingMicrophoneHealthTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    observationCancellation.Cancel();
                    cleanupDeferred = true;
                    ScheduleMicrophoneHealthProbeCleanup(coordinator, observationTask);
                    _logger.LogEvent(
                        "Warning",
                        "ONBOARDING_MIC_HEALTH_TIMEOUT",
                        "The first microphone capture-health observation did not complete in time.",
                        metadata: new Dictionary<string, object?>
                        {
                            ["timeout_ms"] = _onboardingMicrophoneHealthTimeout.TotalMilliseconds
                        });
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    observationCancellation.Cancel();
                    cleanupDeferred = true;
                    ScheduleMicrophoneHealthProbeCleanup(coordinator, observationTask);
                    throw;
                }

                ApplyObservedMicrophoneCaptureHealth(health);
            }
            finally
            {
                if (!cleanupDeferred)
                {
                    await DisposeMicrophoneHealthProbeSafelyAsync(coordinator).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _microphoneHealthProbeGate.Release();
        }
    }

    private bool ShouldObserveOnboardingMicrophoneHealth()
    {
        if (_disposing || _settings.OnboardingCompleted || !_settings.ServiceEnabled)
        {
            return false;
        }

        var capability = ToRuntimeCapability(_audioPlatform.Snapshot);
        return capability.State is RuntimeCapabilityState.Full or RuntimeCapabilityState.Degraded
            && capability.HasActiveOutput
            && capability.HasActiveMicrophone
            && capability.Issue != RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable;
    }

    private void ApplyObservedMicrophoneCaptureHealth(MeetingSpeechCaptureHealth health)
    {
        if (health == MeetingSpeechCaptureHealth.Unknown)
        {
            return;
        }

        var unavailable = health == MeetingSpeechCaptureHealth.Unavailable;
        if (_detectionMicrophoneCaptureUnavailable == unavailable)
        {
            return;
        }

        _detectionMicrophoneCaptureUnavailable = unavailable;
        PublishSnapshot(Snapshot);
    }

    private void ScheduleMicrophoneHealthProbeCleanup(
        MeetingDetectionCoordinator coordinator,
        Task<MeetingSpeechCaptureHealth> observationTask) =>
        _ = DisposeMicrophoneHealthProbeAfterObservationAsync(coordinator, observationTask);

    private async Task DisposeMicrophoneHealthProbeAfterObservationAsync(
        MeetingDetectionCoordinator coordinator,
        Task<MeetingSpeechCaptureHealth> observationTask)
    {
        await DisposeMicrophoneHealthProbeSafelyAsync(coordinator).ConfigureAwait(false);
        try
        {
            await observationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Deferred microphone-health observation failed during cleanup.");
        }
    }

    private async ValueTask DisposeMicrophoneHealthProbeSafelyAsync(
        MeetingDetectionCoordinator coordinator)
    {
        try
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Microphone-health observation resources did not dispose cleanly.");
        }
    }

    private async ValueTask StartDetectionAsync(CancellationToken cancellationToken)
    {
        var capability = ToRuntimeCapability(_audioPlatform.Snapshot);
        if (_detectionCoordinator is not null
            || !_settings.OnboardingCompleted
            || !_settings.ServiceEnabled
            || capability.State == RuntimeCapabilityState.Blocked
            || !capability.HasActiveOutput)
        {
            return;
        }

        var coordinator = CreateDetectionCoordinator();
        coordinator.SnapshotChanged += HandleDetectionSnapshotChanged;
        _detectionCoordinator = coordinator;
        try
        {
            await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            coordinator.SnapshotChanged -= HandleDetectionSnapshotChanged;
            _detectionCoordinator = null;
            await coordinator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private MeetingDetectionCoordinator CreateDetectionCoordinator()
    {
        var preferences = GetEffectiveApplicationPreferences();
        return _detectionCoordinatorFactory?.Invoke(
                _audioPlatform,
                _profiles,
                preferences,
                _detectionMode)
            ?? _platform.CreateMeetingDetectionCoordinator(
                _audioPlatform,
                _profiles,
                preferences,
                _detectionMode,
                _logger);
    }

    private async ValueTask StopDetectionAsync(CancellationToken cancellationToken)
    {
        var coordinator = _detectionCoordinator;
        _detectionMicrophoneCaptureUnavailable = false;
        if (coordinator is null)
        {
            return;
        }

        _detectionCoordinator = null;
        coordinator.SnapshotChanged -= HandleDetectionSnapshotChanged;
        await coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        await coordinator.DisposeAsync().ConfigureAwait(false);
        _lastDetectionDiagnosticFingerprint = null;
        _lastDetectionDiagnosticAtUtc = default;
    }

    private void HandleDetectionSnapshotChanged(
        object? sender,
        MeetingDetectionCoordinatorSnapshot detection)
    {
        if (!_settings.OnboardingCompleted || !_settings.ServiceEnabled)
        {
            return;
        }

        var microphoneCaptureUnavailable = detection.DegradedSignals.Contains(
            "microphone_capture",
            StringComparer.Ordinal);
        var microphoneCaptureHealthChanged =
            _detectionMicrophoneCaptureUnavailable != microphoneCaptureUnavailable;
        _detectionMicrophoneCaptureUnavailable = microphoneCaptureUnavailable;
        if (microphoneCaptureHealthChanged)
        {
            PublishSnapshot(Snapshot);
        }

        if (_recordingCoordinator?.IsBusy == true)
        {
            if (TryScheduleAutomaticMeetingFinish(
                    detection,
                    out var candidateId,
                    out var gateGeneration,
                    out var lossStartedAtUtc,
                    out var lastScore))
            {
                _ = Task.Run(
                    () => FinishDetectedMeetingSafelyAsync(
                        candidateId,
                        gateGeneration,
                        lossStartedAtUtc,
                        lastScore),
                    CancellationToken.None);
            }

            LogDetectionDecision(detection);
            return;
        }

        if (_hasActionableAttention)
        {
            if (Snapshot.PendingMeetingPrompt is not null)
            {
                PublishSnapshot(Snapshot with { PendingMeetingPrompt = null });
            }

            LogDetectionDecision(detection);
            return;
        }

        var pendingPrompt = detection.PendingPrompt is null
            ? null
            : ToRuntimePrompt(detection.PendingPrompt);
        if (IsTerminalFinalization(Snapshot.RecordingFinalization))
        {
            if (Snapshot.RecordingFinalization?.Stage == RecordingArtifactStage.AttentionRequired)
            {
                if (Snapshot.PendingMeetingPrompt is not null)
                {
                    PublishSnapshot(Snapshot with { PendingMeetingPrompt = null });
                }

                LogDetectionDecision(detection);
                return;
            }

            if (!string.Equals(
                    Snapshot.PendingMeetingPrompt?.CandidateId,
                    pendingPrompt?.CandidateId,
                    StringComparison.Ordinal))
            {
                PublishSnapshot(Snapshot with { PendingMeetingPrompt = pendingPrompt });
            }

            LogDetectionDecision(detection);
            return;
        }

        var capability = ApplyActiveRecordingCapabilityIssue(
            ToRuntimeCapability(_audioPlatform.Snapshot));
        if (IsActionableCapability(capability))
        {
            if (Snapshot.Activity != ApplicationActivityState.AttentionRequired
                || Snapshot.Capability != capability
                || Snapshot.PendingMeetingPrompt is not null)
            {
                PublishSnapshot(BuildIdleSnapshot());
            }

            LogDetectionDecision(detection);
            return;
        }

        var activity = pendingPrompt is not null
            ? ApplicationActivityState.AwaitingConfirmation
            : detection.IsSuspected
                ? ApplicationActivityState.Suspected
                : ApplicationActivityState.Listening;
        if (Snapshot.Activity != activity ||
            !string.Equals(
                Snapshot.PendingMeetingPrompt?.CandidateId,
                pendingPrompt?.CandidateId,
                StringComparison.Ordinal))
        {
            PublishSnapshot(Snapshot with
            {
                Activity = activity,
                PendingMeetingPrompt = pendingPrompt,
                AttentionMessage = null
            });
        }

        LogDetectionDecision(detection);
    }

    // @spec spec://common/PROP-006-release-v2-product-canon#product-model.primary-loop
    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
    private bool TryScheduleAutomaticMeetingFinish(
        MeetingDetectionCoordinatorSnapshot detection,
        out string candidateId,
        out long gateGeneration,
        out DateTimeOffset lossStartedAtUtc,
        out int? lastScore)
    {
        candidateId = string.Empty;
        gateGeneration = 0;
        lossStartedAtUtc = default;
        lastScore = null;

        lock (_recordingGateSync)
        {
            if (_disposing
                || string.IsNullOrWhiteSpace(_activeDetectionCandidateId)
                || Snapshot.ActiveMeeting?.IsPaused == true)
            {
                _activeDetectionLossStartedAtUtc = null;
                _activeDetectionAutoFinishScheduled = false;
                return false;
            }

            if (detection.DegradedSignals.Contains("evaluation", StringComparer.Ordinal)
                || detection.DegradedSignals.Contains("window_evidence", StringComparer.Ordinal))
            {
                _activeDetectionLossStartedAtUtc = null;
                _activeDetectionAutoFinishScheduled = false;
                return false;
            }

            var activeCandidate = detection.Candidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.CandidateId,
                    _activeDetectionCandidateId,
                    StringComparison.Ordinal));
            var remainsEligible = HasActiveMeetingContinuity(activeCandidate);
            if (remainsEligible)
            {
                _activeDetectionLossStartedAtUtc = null;
                _activeDetectionAutoFinishScheduled = false;
                return false;
            }

            _activeDetectionLossStartedAtUtc ??= detection.ObservedAtUtc;
            lastScore = activeCandidate?.Score.Score;
            var lossDelay = _activeMeetingLossDelayOverride
                ?? TimeSpan.FromSeconds(Math.Clamp(
                    _legacySettings.Recording.StopDelaySeconds,
                    5,
                    120));
            if (_activeDetectionAutoFinishScheduled
                || detection.ObservedAtUtc - _activeDetectionLossStartedAtUtc.Value < lossDelay)
            {
                return false;
            }

            _activeDetectionAutoFinishScheduled = true;
            candidateId = _activeDetectionCandidateId;
            gateGeneration = _recordingGateGeneration;
            lossStartedAtUtc = _activeDetectionLossStartedAtUtc.Value;
            return true;
        }
    }

    /// <summary>
    /// Keeps a confirmed recording attached to the same meeting through temporary
    /// presentation/full-screen window transitions while meaningful attributed audio remains.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence.temporal
    /// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity
    /// </remarks>
    internal static bool HasActiveMeetingContinuity(MeetingDetectionCandidateSnapshot? candidate)
    {
        if (candidate is null || candidate.Score.IsHardExcluded)
        {
            return false;
        }

        if (candidate.Score.Score >= MeetingDetectionTimingPolicy.Default.AskExitThreshold)
        {
            return true;
        }

        var evidence = candidate.Frame.Evidence;
        var hasMeetingUiBoundary = evidence.Any(static fact =>
            (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
            || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));
        if (hasMeetingUiBoundary)
        {
            return true;
        }

        var hasActiveRender = evidence.Any(static fact =>
            fact.Kind == MeetingEvidenceKind.RenderSessionActive
            && fact.NormalizedStrength >= 0.5);
        var hasAttributedConversation = evidence.Any(static fact =>
            (fact.Kind == MeetingEvidenceKind.RenderSpeech
                || fact.Kind == MeetingEvidenceKind.ConversationalAlternation)
            && fact.NormalizedStrength >= 0.5);
        return hasActiveRender && hasAttributedConversation;
    }

    private async Task FinishDetectedMeetingSafelyAsync(
        string candidateId,
        long gateGeneration,
        DateTimeOffset lossStartedAtUtc,
        int? lastScore)
    {
        try
        {
            IRecordingSessionCoordinator? recording;
            lock (_recordingGateSync)
            {
                if (_disposing
                    || gateGeneration != _recordingGateGeneration
                    || !_activeDetectionAutoFinishScheduled
                    || !string.Equals(
                        _activeDetectionCandidateId,
                        candidateId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                recording = _recordingCoordinator;
            }

            if (recording is null || !recording.IsBusy)
            {
                return;
            }

            _logger.LogEvent(
                "Info",
                "DETECTION_ACTIVE_SESSION_AUTO_STOP",
                "The active meeting lost eligibility and recording finalization started.",
                metadata: new Dictionary<string, object?>
                {
                    ["candidate_id"] = SafeDiagnosticToken(candidateId),
                    ["loss_started_at_utc"] = lossStartedAtUtc,
                    ["last_score"] = lastScore
                });

            var result = await recording
                .FinishForRuntimeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (result)
            {
                await CompleteRecordingGateAsync(
                    gateGeneration,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Automatic recording finalization failed after the meeting ended.");
            lock (_recordingGateSync)
            {
                if (gateGeneration == _recordingGateGeneration
                    && string.Equals(
                        _activeDetectionCandidateId,
                        candidateId,
                        StringComparison.Ordinal))
                {
                    _activeDetectionAutoFinishScheduled = false;
                    _activeDetectionLossStartedAtUtc = TimeProvider.System.GetUtcNow();
                }
            }
        }
    }

    private void LogDetectionDecision(MeetingDetectionCoordinatorSnapshot detection)
    {
        var candidates = detection.Candidates.Take(MaxDiagnosticCandidates).ToArray();
        var fingerprint = string.Join(
            '|',
            string.Join(',', candidates.Select(static candidate => string.Join(
                ':',
                SafeDiagnosticToken(candidate.CandidateId),
                ((candidate.Score.Score / 5) * 5).ToString(System.Globalization.CultureInfo.InvariantCulture),
                candidate.Score.Band,
                candidate.IsSuppressed))),
            SafeDiagnosticToken(detection.PendingPrompt?.Candidate.Frame.CandidateId) ?? "none",
            SafeDiagnosticToken(detection.ShadowWouldPromptCandidate?.Frame.CandidateId) ?? "none",
            string.Join(',', detection.DegradedSignals.Select(SafeDiagnosticToken)));
        if (string.Equals(fingerprint, _lastDetectionDiagnosticFingerprint, StringComparison.Ordinal) &&
            detection.ObservedAtUtc - _lastDetectionDiagnosticAtUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastDetectionDiagnosticFingerprint = fingerprint;
        _lastDetectionDiagnosticAtUtc = detection.ObservedAtUtc;
        var candidateSummaries = candidates
            .Select(static candidate => new Dictionary<string, object?>
            {
                ["candidate_id"] = SafeDiagnosticToken(candidate.CandidateId),
                ["profile_id"] = SafeDiagnosticToken(candidate.Frame.ProfileId),
                ["score"] = candidate.Score.Score,
                ["band"] = candidate.Score.Band.ToString().ToLowerInvariant(),
                ["suppressed"] = candidate.IsSuppressed,
                ["reason_codes"] = candidate.Score.DecisionReasons
                    .Take(MaxDiagnosticEvidenceItems)
                    .Select(SafeDiagnosticToken)
                    .ToArray(),
                ["evidence"] = candidate.Frame.Evidence
                    .Take(MaxDiagnosticEvidenceItems)
                    .Select(static fact => new Dictionary<string, object?>
                    {
                        ["kind"] = fact.Kind.ToString(),
                        ["rule_id"] = SafeDiagnosticToken(fact.RuleId),
                        ["provider_id"] = SafeDiagnosticToken(fact.ProviderId),
                        ["strength"] = fact.NormalizedStrength
                    })
                    .ToArray(),
                ["contributions"] = candidate.Score.Contributions
                    .Take(MaxDiagnosticEvidenceItems)
                    .Select(static contribution => new Dictionary<string, object?>
                    {
                        ["kind"] = contribution.Kind.ToString(),
                        ["rule_id"] = SafeDiagnosticToken(contribution.RuleId),
                        ["strength"] = contribution.Strength,
                        ["weight"] = contribution.Weight,
                        ["score_delta"] = contribution.ScoreDelta
                    })
                    .ToArray()
            })
            .ToArray();
        _logger.LogEvent(
            "Info",
            "DETECTION_DECISION",
            "Meeting detection state changed.",
            metadata: new Dictionary<string, object?>
            {
                ["candidate_count"] = detection.Candidates.Count,
                ["candidates"] = candidateSummaries,
                ["candidates_truncated"] = detection.Candidates.Count > MaxDiagnosticCandidates,
                ["prompt_visible"] = detection.PendingPrompt is not null,
                ["prompt_candidate_id"] = SafeDiagnosticToken(
                    detection.PendingPrompt?.Candidate.Frame.CandidateId),
                ["shadow_would_prompt_candidate_id"] = SafeDiagnosticToken(
                    detection.ShadowWouldPromptCandidate?.Frame.CandidateId),
                ["degraded_signals"] = detection.DegradedSignals.Select(SafeDiagnosticToken).ToArray()
            });

        foreach (var candidate in detection.Candidates)
        {
            var replayRecord = new MeetingDetectionReplayRecord(
                MeetingDetectionReplayRecord.CurrentSchemaVersion,
                detection.ObservedAtUtc,
                SafeDiagnosticToken(candidate.CandidateId) ?? "untrusted",
                SafeDiagnosticToken(candidate.Frame.ProfileId) ?? "untrusted",
                ToSnakeCase(candidate.Frame.Context),
                candidate.Frame.Evidence
                    .Select(static fact => new MeetingDetectionReplayEvidenceRecord(
                        ToSnakeCase(fact.Kind),
                        SafeDiagnosticToken(fact.RuleId) ?? "untrusted",
                        fact.NormalizedStrength,
                        SafeDiagnosticToken(fact.ProviderId)))
                    .ToArray(),
                (candidate.Frame.EvidenceWeightOverrides
                    ?? new Dictionary<MeetingEvidenceKind, int>())
                    .ToDictionary(
                        static pair => ToSnakeCase(pair.Key),
                        static pair => pair.Value,
                        StringComparer.Ordinal),
                candidate.Score.Score,
                ToSnakeCase(candidate.Score.Band),
                candidate.IsSuppressed);
            _logger.LogEvent(
                "Info",
                "DETECTION_REPLAY_FRAME",
                "A privacy-reduced meeting candidate frame is available for replay.",
                metadata: new Dictionary<string, object?>
                {
                    ["record"] = replayRecord
                });
        }
    }

    private static string ToSnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string? SafeDiagnosticToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
        {
            return value is null ? null : "untrusted";
        }

        foreach (var character in value)
        {
            var isAllowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-' or ':';
            if (!isAllowed)
            {
                return "untrusted";
            }
        }

        return value;
    }

    private async ValueTask PersistIgnoredProfileAsync(
        string profileId,
        string displayName,
        CancellationToken cancellationToken)
    {
        // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
        await ApplyReleaseV2SettingsAsync(
            current => current with
            {
                Applications = current.Applications
                    .Where(preference => !string.Equals(
                        MapLegacyPreference(preference).ProfileId,
                        profileId,
                        StringComparison.OrdinalIgnoreCase))
                    .Append(new MeetingApplicationPreference(
                        profileId,
                        displayName,
                        MeetingApplicationPolicy.Ignore))
                    .ToArray()
            },
            applyAutostart: false,
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<MeetingApplicationPreference> GetEffectiveApplicationPreferences() =>
        ToEffectiveApplicationPreferences(_settings.Applications);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private IReadOnlyList<MeetingApplicationPreference> ToEffectiveApplicationPreferences(
        IReadOnlyList<MeetingApplicationPreference> applications) =>
        applications
            .Where(preference => !IsBrowserHostPreference(preference, _profiles))
            .Select(MapLegacyPreference)
            .GroupBy(static preference => preference.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .ToArray();

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private ReleaseV2Settings CanonicalizeReleaseV2Settings(ReleaseV2Settings settings)
    {
        var canonical = settings.Canonicalize();
        return canonical with
        {
            Applications = canonical.Applications
                .Where(preference => !IsBrowserHostPreference(preference, _profiles))
                .ToArray()
        };
    }

    private MeetingApplicationPreference MapLegacyPreference(MeetingApplicationPreference preference)
    {
        if (!preference.ProfileId.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
        {
            return preference;
        }

        var processName = preference.ProfileId["legacy:".Length..];
        var profile = _profiles.Profiles.FirstOrDefault(candidate =>
            candidate.MatchesDedicatedProcess(processName));
        return preference with
        {
            ProfileId = profile?.Id ?? $"user:{preference.ProfileId}"
        };
    }

    private static MeetingProfileRegistry BuildProfileRegistry(IReadOnlyList<AppRuleRecord> appRules)
    {
        var builtIn = new MeetingProfileRegistry();
        // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
        var additional = appRules
            .Where(rule => !builtIn.IsBrowserProcess(rule.ProcessName))
            .Where(rule => !builtIn.Profiles.Any(profile => profile.MatchesDedicatedProcess(rule.ProcessName)))
            .Select(rule => MeetingAppProfile.CreateUserDefined(
                $"legacy:{AppRuleRecord.NormalizeProcessName(rule.ProcessName)}",
                rule.DisplayName,
                rule.ProcessName))
            .ToArray();
        return new MeetingProfileRegistry(additional);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private static IReadOnlyList<AppRuleRecord> ToReleaseV2ApplicationRules(
        IReadOnlyList<AppRuleRecord> appRules,
        MeetingProfileRegistry profiles) =>
        appRules
            .Where(rule => !profiles.IsBrowserProcess(rule.ProcessName))
            .ToArray();

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private static bool IsBrowserHostPreference(
        MeetingApplicationPreference preference,
        MeetingProfileRegistry profiles)
    {
        var processName = TryGetUserDefinedProcessName(preference.ProfileId);
        return processName is not null && profiles.IsBrowserProcess(processName);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    private static string? TryGetUserDefinedProcessName(string profileId)
    {
        const string userPrefix = "user:";
        const string legacyPrefix = "legacy:";

        var candidate = profileId.Trim();
        var isUserDefined = false;
        while (candidate.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase))
        {
            isUserDefined = true;
            candidate = candidate[userPrefix.Length..];
        }

        if (candidate.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            isUserDefined = true;
            candidate = candidate[legacyPrefix.Length..];
        }

        return isUserDefined && !string.IsNullOrWhiteSpace(candidate)
            ? candidate
            : null;
    }

    private static MeetingPromptSnapshot ToRuntimePrompt(MeetingPendingPrompt pending) => new(
        pending.Candidate.Frame.CandidateId,
        pending.Candidate.Frame.ProfileId,
        pending.Candidate.Frame.DisplayName,
        pending.CreatedAtUtc,
        pending.ExpiresAtUtc,
        pending.Candidate.Score.Score,
        pending.Candidate.Score.DecisionReasons);

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private void HandleRecordingSnapshotChanged(
        object? sender,
        RecordingCoordinatorSnapshot recording) => ApplyRecordingSnapshot(recording);

    internal void ApplyRecordingSnapshot(RecordingCoordinatorSnapshot recording)
    {
        _activeRecordingCapabilityIssue = recording.Activity == ApplicationActivityState.Recording
            ? recording.CapabilityIssue
            : null;
        if (recording.Activity == ApplicationActivityState.Recording)
        {
            if (recording.CapabilityIssue is RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
                or RuntimeCapabilityIssue.OutputCaptureUnavailable)
            {
                _persistentCaptureCapabilityIssue = recording.CapabilityIssue;
            }
            else if (recording.CapabilityIssue is null
                     && recording.ActiveMeeting is { HasOutput: true, HasMicrophone: true })
            {
                _persistentCaptureCapabilityIssue = null;
            }
        }

        PublishSnapshot(Snapshot with
        {
            Activity = recording.Activity,
            ActiveMeeting = recording.ActiveMeeting,
            RecentRecordings = recording.RefreshRecentRecordings
                ? LoadRecentRecordings()
                : Snapshot.RecentRecordings,
            AttentionMessage = recording.AttentionMessage,
            PendingMeetingPrompt = null,
            RecordingFinalization = recording.Finalization
        });

        if (recording.Finalization?.Stage == RecordingArtifactStage.Ready)
        {
            ScheduleReadyFinalizationClear(recording.Finalization);
            _ = EnqueueAutomaticTranscriptionSafelyAsync(recording.Finalization.SessionId);
        }
        else
        {
            CancelReadyFinalizationClear();
        }

        if (recording.Finalization?.Stage is RecordingArtifactStage.Ready
            or RecordingArtifactStage.AttentionRequired)
        {
            var gateGeneration = GetRecordingGateGeneration();
            _ = Task.Run(
                () => CompleteRecordingGateSafelyAsync(gateGeneration),
                CancellationToken.None);
        }
    }

    private void HandleTranscriptionStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!_initialized || _disposing)
        {
            return;
        }

        try
        {
            PublishSnapshotWithLatestRecentRecordings();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "The transcription state could not be projected into the desktop snapshot.");
        }
    }

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    private void HandleLocalTranscriptionStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!_initialized || _disposing)
        {
            return;
        }

        try
        {
            PublishSnapshotWithLatestRecentRecordings();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "The local transcription state could not be projected.");
        }
    }

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    private async Task EnqueueAutomaticTranscriptionSafelyAsync(Guid sessionId)
    {
        if (!_initialized || _disposing)
        {
            return;
        }

        await _transcriptionCommandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var preferences = (_settings.Transcription ?? TranscriptionPreferences.Default).Canonicalize();
            if (preferences.Mode == "off")
            {
                return;
            }

            var session = FindTranscriptionSession(sessionId);
            if (session.CurrentTranscription is not null)
            {
                return;
            }

            await EnsureSpeakerAssetsReadyAsync(CancellationToken.None).ConfigureAwait(false);
            await EnsureSelectedEngineModelsDiscoveredAsync(preferences, CancellationToken.None)
                .ConfigureAwait(false);
            await (_transcriptionJobEnqueuer
                    ?? throw new InvalidOperationException("The transcription queue is unavailable."))
                .EnqueueAsync(
                    session,
                    preferences,
                    _discoveredTranscriptionModels,
                    replaceExisting: false,
                    cancellationToken: CancellationToken.None,
                    triggerKind: TranscriptionTriggerKind.Automatic)
                .ConfigureAwait(false);
            _transcriptionWorker?.Signal();
            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
            _logger.LogEvent(
                "Info",
                "TRANSCRIPTION_AUTO_ENQUEUED",
                "A finalized recording entered the opted-in transcription queue.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = session.Id,
                    ["engine_id"] = preferences.SelectedEngineId
                });
        }
        catch (TranscriptionCommandException exception)
        {
            SetActionableAttention(
                sessionId,
                "Автоматическая транскрибация требует проверки настроек провайдера.");
            _logger.LogEvent(
                "Warning",
                "TRANSCRIPTION_AUTO_ATTENTION",
                "Automatic transcription requires a settings action.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId.ToString("N"),
                    ["reason_code"] = exception.Code
                });
            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Automatic transcription could not be enqueued after recording finalization.");
            SetActionableAttention(
                sessionId,
                "Автоматическая транскрибация не запустилась; запись сохранена.");
            PublishSnapshot(Snapshot with { RecentRecordings = LoadRecentRecordings() });
        }
        finally
        {
            _transcriptionCommandGate.Release();
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    private void ScheduleReadyFinalizationClear(RecordingFinalizationSnapshot finalization)
    {
        CancelReadyFinalizationClear();
        var cancellation = new CancellationTokenSource();
        lock (_recordingGateSync)
        {
            if (_disposing)
            {
                cancellation.Dispose();
                return;
            }

            _readyFinalizationClearCancellation = cancellation;
        }

        _ = ClearReadyFinalizationAfterHoldAsync(finalization, cancellation);
    }

    private void CancelReadyFinalizationClear()
    {
        CancellationTokenSource? cancellation;
        lock (_recordingGateSync)
        {
            cancellation = _readyFinalizationClearCancellation;
            _readyFinalizationClearCancellation = null;
        }

        cancellation?.Cancel();
    }

    private async Task ClearReadyFinalizationAfterHoldAsync(
        RecordingFinalizationSnapshot finalization,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_readyFinalizationHold, cancellation.Token).ConfigureAwait(false);
            lock (_recordingGateSync)
            {
                if (_disposing || !ReferenceEquals(_readyFinalizationClearCancellation, cancellation))
                {
                    return;
                }

                _readyFinalizationClearCancellation = null;
            }

            if (Snapshot.RecordingFinalization is not { Stage: RecordingArtifactStage.Ready } current
                || current.SessionId != finalization.SessionId)
            {
                return;
            }

            PublishSnapshot(BuildCoherentPostFinalizationSnapshot());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_recordingGateSync)
            {
                if (ReferenceEquals(_readyFinalizationClearCancellation, cancellation))
                {
                    _readyFinalizationClearCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private ApplicationRuntimeSnapshot BuildCoherentPostFinalizationSnapshot()
    {
        var idle = BuildIdleSnapshot();
        if (idle.Activity != ApplicationActivityState.Listening
            || _detectionCoordinator?.Snapshot is not { } detection)
        {
            return idle;
        }

        var pendingPrompt = detection.PendingPrompt is null
            ? null
            : ToRuntimePrompt(detection.PendingPrompt);
        return idle with
        {
            Activity = pendingPrompt is not null
                ? ApplicationActivityState.AwaitingConfirmation
                : detection.IsSuspected
                    ? ApplicationActivityState.Suspected
                    : ApplicationActivityState.Listening,
            PendingMeetingPrompt = pendingPrompt
        };
    }

    private void PublishAfterRecentRecordingRemoval(Guid sessionId)
    {
        var recentRecordings = LoadRecentRecordings();
        var rowWasRemoved = !recentRecordings.Any(recording => recording.SessionId == sessionId);
        var clearsFinalization = Snapshot.RecordingFinalization is { } finalization
            && finalization.SessionId == sessionId
            && IsTerminalFinalization(finalization)
            && rowWasRemoved;
        var clearsRuntimeAttention = _hasActionableAttention
            && _actionableAttentionSessionId == sessionId
            && rowWasRemoved;
        if (clearsRuntimeAttention)
        {
            ClearActionableAttention();
        }

        if (clearsFinalization || clearsRuntimeAttention)
        {
            CancelReadyFinalizationClear();
            PublishSnapshot(BuildCoherentPostFinalizationSnapshot() with
            {
                RecordingFinalization = clearsFinalization ? null : Snapshot.RecordingFinalization,
                RecentRecordings = recentRecordings
            });
            return;
        }

        PublishSnapshot(Snapshot with { RecentRecordings = recentRecordings });
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    private void SetActionableAttention(Guid? sessionId, string? message)
    {
        _hasActionableAttention = true;
        _actionableAttentionSessionId = sessionId;
        _actionableAttentionMessage = message;
    }

    private void ClearActionableAttention()
    {
        _hasActionableAttention = false;
        _actionableAttentionSessionId = null;
        _actionableAttentionMessage = null;
    }

    private static bool IsActionableCapability(RuntimeCapabilitySnapshot capability) =>
        capability.State == RuntimeCapabilityState.Blocked
        || capability.Issue is RuntimeCapabilityIssue.NoActiveOutput
            or RuntimeCapabilityIssue.NoActiveMicrophone
            or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
            or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
            or RuntimeCapabilityIssue.OutputCaptureUnavailable;

    private static bool IsTerminalFinalization(RecordingFinalizationSnapshot? finalization) =>
        finalization?.Stage is RecordingArtifactStage.Ready or RecordingArtifactStage.AttentionRequired;

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async ValueTask CompleteRecordingGateAsync(CancellationToken cancellationToken)
    {
        var gateGeneration = GetRecordingGateGeneration();
        await CompleteRecordingGateAsync(gateGeneration, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteRecordingGateAsync(
        long gateGeneration,
        CancellationToken cancellationToken)
    {
        Task completionTask;
        lock (_recordingGateSync)
        {
            if (_disposing || gateGeneration != _recordingGateGeneration)
            {
                return;
            }

            _recordingGateCompletionTask ??= CompleteRecordingGateCoreAsync();
            completionTask = _recordingGateCompletionTask;
        }

        await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteRecordingGateCoreAsync()
    {
        string? candidateId;
        lock (_recordingGateSync)
        {
            candidateId = _activeDetectionCandidateId;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(candidateId) && _detectionCoordinator is { } detection)
            {
                await detection.EndActiveSessionAsync(
                    candidateId,
                    TimeProvider.System.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_transcriptionWorker is not null)
            {
                await _transcriptionWorker.ResumeLocalExecutionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        lock (_recordingGateSync)
        {
            if (string.Equals(_activeDetectionCandidateId, candidateId, StringComparison.Ordinal))
            {
                _activeDetectionCandidateId = null;
                _activeDetectionLossStartedAtUtc = null;
                _activeDetectionAutoFinishScheduled = false;
            }
        }

        bool restartDetection;
        lock (_recordingGateSync)
        {
            restartDetection = !_disposing;
        }

        if (restartDetection
            && _settings.OnboardingCompleted
            && _settings.ServiceEnabled
            && _detectionCoordinator is null)
        {
            await StartDetectionAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CompleteRecordingGateSafelyAsync(long gateGeneration)
    {
        try
        {
            await CompleteRecordingGateAsync(gateGeneration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Recording gate completion failed after terminal capture state.");
            ResetFaultedRecordingGate(gateGeneration);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    private async ValueTask BeginRecordingGateAsync(
        string? candidateId,
        CancellationToken cancellationToken)
    {
        CancelReadyFinalizationClear();
        Task? previousCompletion;
        lock (_recordingGateSync)
        {
            previousCompletion = _recordingGateCompletionTask;
        }

        if (previousCompletion is not null)
        {
            try
            {
                await previousCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch when (previousCompletion.IsFaulted)
            {
                lock (_recordingGateSync)
                {
                    if (ReferenceEquals(_recordingGateCompletionTask, previousCompletion))
                    {
                        _recordingGateCompletionTask = null;
                    }
                }
            }
        }

        var suspended = false;
        try
        {
            if (_transcriptionWorker is not null)
            {
                await _transcriptionWorker.SuspendLocalExecutionAsync(cancellationToken)
                    .ConfigureAwait(false);
                suspended = true;
            }

            lock (_recordingGateSync)
            {
                ObjectDisposedException.ThrowIf(_disposing, this);
                _activeDetectionCandidateId = candidateId;
                _activeDetectionLossStartedAtUtc = null;
                _activeDetectionAutoFinishScheduled = false;
                _recordingGateCompletionTask = null;
                _recordingGateGeneration++;
            }
        }
        catch
        {
            if (suspended && _transcriptionWorker is not null)
            {
                await _transcriptionWorker.ResumeLocalExecutionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    private long GetRecordingGateGeneration()
    {
        lock (_recordingGateSync)
        {
            return _recordingGateGeneration;
        }
    }

    private void ResetFaultedRecordingGate(long gateGeneration)
    {
        lock (_recordingGateSync)
        {
            if (gateGeneration == _recordingGateGeneration
                && _recordingGateCompletionTask?.IsFaulted == true)
            {
                _recordingGateCompletionTask = null;
            }
        }
    }

    private async Task RunArtifactRecoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_recordingCoordinator is { } coordinator)
            {
                await coordinator.RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Source files and checkpoints remain available for the next launch.
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Recording artifact startup recovery failed.");
            SetActionableAttention(sessionId: null, "Не удалось восстановить незавершённую запись; исходные файлы сохранены.");
            PublishSnapshot(Snapshot with
            {
                Activity = ApplicationActivityState.AttentionRequired,
                AttentionMessage = "Не удалось восстановить незавершённую запись; исходные файлы сохранены."
            });
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Release v2 runtime has not been initialized.");
        }
    }

    private RuntimeCapabilitySnapshot ToRuntimeCapability(AudioPlatformSnapshot audioPlatform)
    {
        var capability = audioPlatform.Capabilities;
        var hasActiveOutput = audioPlatform.OutputDevices.Any(static endpoint => endpoint.IsActive);
        var hasActiveMicrophone = audioPlatform.Microphones.Any(static endpoint => endpoint.IsActive);
        var platformState = capability == AudioPlatformCapabilities.Unknown
            ? RuntimeCapabilityState.Unknown
            : !capability.IsSupported
                ? RuntimeCapabilityState.Blocked
                : capability.SupportsProcessOutputCapture
                    ? RuntimeCapabilityState.Full
                    : RuntimeCapabilityState.Degraded;
        var state = platformState is RuntimeCapabilityState.Full or RuntimeCapabilityState.Degraded
            ? !hasActiveOutput && !hasActiveMicrophone
                ? RuntimeCapabilityState.Blocked
                : hasActiveOutput && hasActiveMicrophone
                    ? platformState
                    : RuntimeCapabilityState.Degraded
            : platformState;
        var issue = platformState switch
        {
            RuntimeCapabilityState.Blocked => RuntimeCapabilityIssue.PlatformBlocked,
            RuntimeCapabilityState.Unknown => RuntimeCapabilityIssue.None,
            _ when !hasActiveOutput && !hasActiveMicrophone => RuntimeCapabilityIssue.NoActiveAudioEndpoints,
            _ when !hasActiveOutput => RuntimeCapabilityIssue.NoActiveOutput,
            _ when !hasActiveMicrophone => RuntimeCapabilityIssue.NoActiveMicrophone,
            RuntimeCapabilityState.Degraded => RuntimeCapabilityIssue.ProcessOutputCaptureUnavailable,
            _ => RuntimeCapabilityIssue.None
        };
        var configuredMicrophoneMissing = !_settings.FollowSystemDefaultMicrophone
            && !string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId)
            && !audioPlatform.Microphones.Any(microphone =>
                microphone.IsActive
                && string.Equals(
                    microphone.Id,
                    _settings.MicrophoneDeviceId,
                    StringComparison.OrdinalIgnoreCase));
        if (platformState is RuntimeCapabilityState.Full or RuntimeCapabilityState.Degraded
            && hasActiveOutput
            && configuredMicrophoneMissing)
        {
            state = RuntimeCapabilityState.Degraded;
            issue = RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable;
        }

        return new RuntimeCapabilitySnapshot(
            state,
            capability.SupportsProcessOutputCapture,
            capability.Summary,
            capability.BlockingReason)
        {
            Issue = issue,
            HasActiveOutput = hasActiveOutput,
            HasActiveMicrophone = hasActiveMicrophone
        };
    }
}
