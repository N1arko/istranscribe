using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using IsTranscribe.App.AutomaticRecording;
using IsTranscribe.App.Configuration;
using IsTranscribe.App.Lifecycle;
using IsTranscribe.App.ManualControls;
using IsTranscribe.App.SingleInstance;
using IsTranscribe.App.Strings;
using IsTranscribe.App.Windows;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Bootstrap;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Processes;
using IsTranscribe.Host.Audio.Sessions;
using IsTranscribe.Host.Bootstrap;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Application.Recovery;
using IsTranscribe.Host.Routing;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using IsTranscribe.Host.Transcription;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace IsTranscribe.App.Hosting;

public sealed class DesktopHostApplication : IAsyncDisposable
{
    private const string ApplicationName = "isTranscribe";

    private readonly WpfApplication _application;
    private readonly AppIdentity _identity;
    private readonly LocalAppPaths _paths;
    private readonly BootstrapFileLogger _logger;
    private readonly WindowsCapabilityAssessor _capabilityAssessor;
    private readonly StartupSurfaceRouter _router;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly PersistenceBootstrapper _persistenceBootstrapper;
    private IRecoveryCoordinator _recoveryCoordinator;
    private readonly IBackgroundServicesCoordinator _backgroundServices;
    private readonly ManualControlGateway _manualControlGateway;
    private readonly IAudioDeviceManager _deviceManager;
    private readonly IProcessWatcher _processWatcher;
    private readonly IAudioSessionWatcher _sessionWatcher;
    private readonly RunningAudioProcessCatalog _runningAudioProcessCatalog;
    private readonly GlobalHotkeyRuntimeManager _globalHotkeys;
    private readonly IAutomaticPromptService _automaticPromptService;

    private ApplicationSettings _settings = ApplicationSettings.Default;
    private AppSecrets _secrets = AppSecrets.Empty;
    private IReadOnlyList<AppRuleRecord> _appRules = Array.Empty<AppRuleRecord>();
    private HostCapabilitySnapshot? _capability;
    private Microsoft.Data.Sqlite.SqliteConnection? _databaseConnection;
    private AppRuleRepository? _appRuleRepository;
    private MeetingSessionRepository? _meetingSessionRepository;
    private NamedPipeSingleInstanceManager? _singleInstance;
    private TrayIconController? _trayIcon;
    private AutomaticRecordingCoordinator? _automaticRecordingCoordinator;
    private MainShellWindow? _mainShellWindow;
    private FirstRunWizardWindow? _wizardWindow;
    private RecoveryWindow? _recoveryWindow;
    private BlockedStartupWindow? _blockedWindow;
    private TranscriptionBackgroundWorker? _transcriptionBackgroundWorker;
    private bool _disposed;
    private bool _quitRequested;
    private bool _hasAutoShownCurrentRecording;
    private LaunchArguments _launchArguments = new(false);

    private DesktopHostApplication(
        WpfApplication application,
        AppIdentity identity,
        LocalAppPaths paths,
        BootstrapFileLogger logger,
        WindowsCapabilityAssessor capabilityAssessor,
        StartupSurfaceRouter router,
        IApplicationSettingsStore settingsStore,
        ISecretStore secretStore,
        PersistenceBootstrapper persistenceBootstrapper,
        IRecoveryCoordinator recoveryCoordinator,
        AudioFoundationBackgroundServicesCoordinator audioFoundationCoordinator,
        ManualControlGateway manualControlGateway,
        IAudioDeviceManager deviceManager,
        IProcessWatcher processWatcher,
        IAudioSessionWatcher sessionWatcher,
        RunningAudioProcessCatalog runningAudioProcessCatalog,
        GlobalHotkeyRuntimeManager globalHotkeys,
        IAutomaticPromptService automaticPromptService)
    {
        _application = application;
        _identity = identity;
        _paths = paths;
        _logger = logger;
        _capabilityAssessor = capabilityAssessor;
        _router = router;
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _persistenceBootstrapper = persistenceBootstrapper;
        _recoveryCoordinator = recoveryCoordinator;
        _backgroundServices = new OperationalBackgroundServicesCoordinator(audioFoundationCoordinator, paths, logger);
        _manualControlGateway = manualControlGateway;
        _deviceManager = deviceManager;
        _processWatcher = processWatcher;
        _sessionWatcher = sessionWatcher;
        _runningAudioProcessCatalog = runningAudioProcessCatalog;
        _globalHotkeys = globalHotkeys;
        _automaticPromptService = automaticPromptService;
    }

    public static DesktopHostApplication Create(WpfApplication application)
    {
        var paths = new LocalAppPaths(ApplicationName);
        var identity = new AppIdentityFactory().Create(ApplicationName);
        var logger = new BootstrapFileLogger(paths.HostLogFilePath);
        var settingsStore = new JsonApplicationSettingsStore(paths);
        var secretStore = new DpapiSecretStore(paths, new DpapiSecretProtector());
        var deviceManager = new WindowsAudioDeviceManager(logger);
        var processWatcher = new WindowsProcessWatcher(logger);
        var sessionWatcher = new WindowsAudioSessionWatcher(logger, deviceManager, processWatcher);
        var recorderEngine = new AudioRecorderEngine(logger);
        var artifactPathResolver = new ArtifactPathResolver(paths);
        var automaticPromptService = new AutomaticPromptService(application.Dispatcher);
        var audioFoundationCoordinator = new AudioFoundationBackgroundServicesCoordinator(
            logger,
            deviceManager,
            processWatcher,
            sessionWatcher,
            recorderEngine);
        var persistenceBootstrapper = new PersistenceBootstrapper(
            paths,
            settingsStore,
            secretStore,
            new SqliteDatabaseInitializer(paths),
            new LegacyStorageLayoutMigrator(paths));

        return new DesktopHostApplication(
            application,
            identity,
            paths,
            logger,
            new WindowsCapabilityAssessor(new HostOperatingSystemDetector()),
            new StartupSurfaceRouter(),
            settingsStore,
            secretStore,
            persistenceBootstrapper,
            new NullRecoveryCoordinator(),
            audioFoundationCoordinator,
            new ManualControlGateway(
                logger,
                audioFoundationCoordinator.RecorderEngine,
                deviceManager,
                automaticPromptService,
                artifactPathResolver,
                new AudioCompressor(logger),
                paths),
            deviceManager,
            processWatcher,
            sessionWatcher,
            new RunningAudioProcessCatalog(deviceManager),
            new GlobalHotkeyRuntimeManager(),
            automaticPromptService);
    }

    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#bootstrap.sequence
    public async Task<bool> StartAsync(LaunchArguments launchArguments)
    {
        _launchArguments = launchArguments;
        _logger.Info("Host bootstrap started.");
        _logger.LogEvent("Info", "APP_START", "Host bootstrap started.");

        try
        {
            _capability = _capabilityAssessor.Assess();
            if (_capability.State == HostCapabilityState.Blocked)
            {
                ShowBlockedStartup(_capability);
                return false;
            }

            _singleInstance = new NamedPipeSingleInstanceManager(_logger);
            var registration = await _singleInstance.RegisterAsync(_identity, CancellationToken.None);
            if (!registration.IsPrimary)
            {
                await registration.ForwardActivationAsync(ActivationMessage.OpenOrFocusApp(), CancellationToken.None);
                return true;
            }

            _singleInstance.ActivationReceived += (_, _) =>
                _application.Dispatcher.InvokeAsync(new Action(() =>
                {
                    _ = OpenOrFocusAppAsync();
                }));

            _trayIcon = new TrayIconController(_manualControlGateway, OpenOrFocusAppAsync, RequestQuitAsync, () => _settings.General.Notifications);
            _trayIcon.Initialize();
            _manualControlGateway.SnapshotChanged += HandleManualControlSnapshotChanged;
            _deviceManager.SnapshotChanged += HandleAudioDeviceSnapshotChanged;
            _sessionWatcher.SourceDeviceDriftDetected += HandleSourceDeviceDriftDetected;

            var persistence = await _persistenceBootstrapper.InitializeAsync(CancellationToken.None);
            _settings = persistence.Settings;
            _secrets = persistence.Secrets;
            LocalizationManager.Instance.SwitchLanguage(_settings.General.Language ?? "ru");
            App.ApplyTheme(_settings.General.AppTheme);
            _databaseConnection = persistence.DatabaseConnection;
            _appRuleRepository = new AppRuleRepository(_databaseConnection);
            _meetingSessionRepository = new MeetingSessionRepository(_databaseConnection);
            _recoveryCoordinator = new SqliteRecoveryCoordinator(_logger, () => _databaseConnection);
            _transcriptionBackgroundWorker = new TranscriptionBackgroundWorker(
                _logger,
                _meetingSessionRepository,
                new ArtifactPathResolver(_paths),
                new FireworksTranscriptionProvider(new HttpClient
                {
                    BaseAddress = new Uri("https://api.fireworks.ai", UriKind.Absolute)
                }),
                () => _settings,
                () => _secrets);
            _appRules = await _appRuleRepository.ListAsync(CancellationToken.None);
            _logger.Info(_secrets.FireworksApiKey is null
                ? "Fireworks API key is not configured."
                : "Fireworks API key loaded from protected storage.");
            _manualControlGateway.UpdateHostContext(_settings, _capability, _meetingSessionRepository);
            _automaticRecordingCoordinator = new AutomaticRecordingCoordinator(
                _logger,
                _settingsStore,
                _appRuleRepository,
                _processWatcher,
                _sessionWatcher,
                _manualControlGateway,
                _automaticPromptService);
            _automaticRecordingCoordinator.ContextChanged += HandleAutomaticCoordinatorContextChanged;
            _automaticRecordingCoordinator.UpdateHostContext(_settings, _capability, _appRules);
            _globalHotkeys.Triggered += HandleGlobalHotkeyTriggered;
            if (!_globalHotkeys.Apply(_settings.General.Hotkeys, out var hotkeyError))
            {
                _logger.Warning($"Global hotkeys were not registered at startup. {hotkeyError}");
            }

            var recovery = await _recoveryCoordinator.EvaluateAsync(CancellationToken.None);
            await _backgroundServices.StartAsync(_capability, CancellationToken.None);
            if (_automaticRecordingCoordinator is not null)
            {
                await _automaticRecordingCoordinator.StartAsync(CancellationToken.None);
            }

            _transcriptionBackgroundWorker.Start();

            var decision = _router.Decide(_capability, _settings.ToBootstrapSnapshot(), recovery, _launchArguments.ToLaunchContext());
            ApplyRouting(decision, recovery);
            _logger.Info($"Host bootstrap completed with surface {decision.Surface}.");
            return false;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Host bootstrap failed.");
            await DisposeAsync();
            ShowBlockedStartup(new HostCapabilitySnapshot(HostCapabilityState.Blocked, false, "Bootstrap failure.", exception.Message));
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_automaticRecordingCoordinator is not null)
        {
            _automaticRecordingCoordinator.ContextChanged -= HandleAutomaticCoordinatorContextChanged;
            await _automaticRecordingCoordinator.DisposeAsync();
        }

        if (_transcriptionBackgroundWorker is not null)
        {
            await _transcriptionBackgroundWorker.DisposeAsync();
            _transcriptionBackgroundWorker = null;
        }

        await _backgroundServices.StopAsync(CancellationToken.None);
        if (_backgroundServices is IAsyncDisposable asyncDisposableBackgroundServices)
        {
            await asyncDisposableBackgroundServices.DisposeAsync();
        }
        if (_singleInstance is not null)
        {
            await _singleInstance.DisposeAsync();
        }

        _databaseConnection?.Dispose();
        _manualControlGateway.SnapshotChanged -= HandleManualControlSnapshotChanged;
        _deviceManager.SnapshotChanged -= HandleAudioDeviceSnapshotChanged;
        _sessionWatcher.SourceDeviceDriftDetected -= HandleSourceDeviceDriftDetected;
        _globalHotkeys.Triggered -= HandleGlobalHotkeyTriggered;
        _globalHotkeys.Dispose();
        _trayIcon?.Dispose();
        _logger.LogEvent("Info", "APP_STOP", "Host shutdown completed.");
    }

    private void ApplyRouting(StartupRoutingDecision decision, RecoveryLaunchDecision recovery)
    {
        if (decision.Surface == StartupSurface.None)
        {
            _mainShellWindow?.Hide();
            _wizardWindow?.Hide();
            _recoveryWindow?.Hide();
            return;
        }

        switch (decision.Surface)
        {
            case StartupSurface.FirstRunWizard:
                ShowWizard();
                break;
            case StartupSurface.Recovery:
                ShowRecovery(recovery);
                break;
            case StartupSurface.MainShell:
                ShowMainShell();
                break;
        }
    }

    private void ShowMainShell()
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings
        _wizardWindow?.Hide();
        _recoveryWindow?.Hide();
        _mainShellWindow ??= CreateMainShellWindow();
        _mainShellWindow.LoadSnapshot(BuildShellSnapshot());
        if (_manualControlGateway.Snapshot.CurrentRecording is not null)
        {
            _mainShellWindow.ShowCurrentRecordingSurface();
        }

        _mainShellWindow.Show();
        _mainShellWindow.Activate();
    }

    private void ShowWizard()
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.preconditions
        _mainShellWindow?.Hide();
        _recoveryWindow?.Hide();
        _wizardWindow ??= CreateWizardWindow();
        _wizardWindow.LoadSnapshot(BuildShellSnapshot());
        _wizardWindow.Show();
        _wizardWindow.Activate();
    }

    private void ShowRecovery(RecoveryLaunchDecision recovery)
    {
        _mainShellWindow?.Hide();
        _wizardWindow?.Hide();
        _recoveryWindow ??= CreateRecoveryWindow();
        _recoveryWindow.ApplyRecovery(recovery);
        _recoveryWindow.Show();
        _recoveryWindow.Activate();
    }

    private void ShowBlockedStartup(HostCapabilitySnapshot capability)
    {
        if (_blockedWindow is null)
        {
            _blockedWindow = new BlockedStartupWindow();
            _blockedWindow.Closed += (_, _) => _application.Shutdown();
        }

        _blockedWindow.ApplyCapability(capability);
        _blockedWindow.Show();
        _blockedWindow.Activate();
    }

    private Task OpenOrFocusAppAsync()
        => RouteForActivationAsync();

    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#tray-window-lifecycle.quit
    private async Task RequestQuitAsync()
    {
        if (_quitRequested)
        {
            return;
        }

        if (_manualControlGateway.Snapshot.CurrentRecording is not null
            || _manualControlGateway.Snapshot.RecordingState == RecordingActivityState.Stopping)
        {
            var result = WpfMessageBox.Show(
                LocalizationManager.Instance["Host_QuitConfirm_Message"],
                LocalizationManager.Instance["Host_QuitConfirm_Title"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            await _manualControlGateway.StopCurrentRecordingAsync();
        }

        _quitRequested = true;
        await DisposeAsync();

        if (_mainShellWindow is not null)
        {
            _mainShellWindow.AllowClose = true;
            _mainShellWindow.Close();
        }

        if (_wizardWindow is not null)
        {
            _wizardWindow.AllowClose = true;
            _wizardWindow.Close();
        }

        if (_recoveryWindow is not null)
        {
            _recoveryWindow.AllowClose = true;
            _recoveryWindow.Close();
        }

        _application.Shutdown();
    }

    private MainShellWindow CreateMainShellWindow()
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.rules
        var window = new MainShellWindow();
        window.CloseRequested += async (_, _) => await RequestQuitAsync();
        window.ForceRecordRequested += async (_, _) =>
        {
            await _manualControlGateway.StartForceRecordAsync();
            window.UpdateRuntimeSnapshot(BuildShellSnapshot());
            if (_manualControlGateway.Snapshot.CurrentRecording is not null)
            {
                window.ShowCurrentRecordingSurface();
            }
        };
        window.PauseRequested += async (_, _) =>
        {
            await _manualControlGateway.PauseCurrentRecordingAsync();
            window.UpdateRuntimeSnapshot(BuildShellSnapshot());
        };
        window.ResumeRequested += async (_, _) =>
        {
            await _manualControlGateway.ResumeCurrentRecordingAsync();
            window.UpdateRuntimeSnapshot(BuildShellSnapshot());
        };
        window.StopRequested += async (_, _) =>
        {
            await _manualControlGateway.StopCurrentRecordingAsync();
            window.UpdateRuntimeSnapshot(BuildShellSnapshot());
        };
        window.DiscardRequested += async (_, _) =>
        {
            if (ConfirmDiscardCurrentRecording())
            {
                await _manualControlGateway.DiscardCurrentRecordingAsync();
                window.UpdateRuntimeSnapshot(BuildShellSnapshot());
            }
        };
        window.TogglePrivacyPauseRequested += async (_, _) =>
        {
            await _manualControlGateway.TogglePrivacyPauseAsync();
            window.UpdateRuntimeSnapshot(BuildShellSnapshot());
        };
        window.OpenLogsRequested += (_, _) => OpenDirectory(_paths.LogsDirectory);
        window.RefreshSnapshotAsync = () => Task.FromResult(BuildShellSnapshot());
        window.SaveGeneralAsync = SaveGeneralAsync;
        window.SaveRecordingAsync = SaveRecordingAsync;
        window.SaveDevicesAsync = SaveDevicesAsync;
        window.SaveApplicationsAsync = SaveApplicationsAsync;
        window.SaveStorageAsync = SaveStorageAsync;
        window.SaveTranscriptionAsync = SaveTranscriptionAsync;
        window.RetryTranscriptionAsync = RetryTranscriptionAsync;
        window.Closing += (_, args) =>
        {
            if (_quitRequested || !_settings.General.MinimizeToTrayOnClose)
            {
                return;
            }

            // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#tray-window-lifecycle.windows
            args.Cancel = true;
            window.Hide();
        };
        window.Closed += (_, _) =>
        {
            if (!_quitRequested)
            {
                _mainShellWindow = null;
            }
        };
        return window;
    }

    private FirstRunWizardWindow CreateWizardWindow()
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.navigation
        var window = new FirstRunWizardWindow();
        window.CompleteAsync = CompleteWizardAsync;
        window.CloseRequested += async (_, _) => await RequestQuitAsync();
        window.Closing += (_, args) =>
        {
            if (_quitRequested)
            {
                return;
            }

            args.Cancel = true;
            window.Hide();
        };
        return window;
    }

    private RecoveryWindow CreateRecoveryWindow()
    {
        var window = new RecoveryWindow();
        window.OpenAppRequested += (_, _) => ShowMainShell();
        window.CloseRequested += async (_, _) => await RequestQuitAsync();
        window.Closing += (_, args) =>
        {
            if (_quitRequested)
            {
                return;
            }

            args.Cancel = true;
            window.Hide();
        };
        return window;
    }

    private void HandleManualControlSnapshotChanged(object? sender, ManualControlSnapshot snapshot)
    {
        if (_mainShellWindow is null || _capability is null)
        {
            return;
        }

        _application.Dispatcher.Invoke(() =>
        {
            _mainShellWindow.UpdateRuntimeSnapshot(BuildShellSnapshot());
            if (snapshot.CurrentRecording is not null && !_hasAutoShownCurrentRecording)
            {
                _hasAutoShownCurrentRecording = true;
                _mainShellWindow.ShowCurrentRecordingSurface();
            }
            else if (snapshot.CurrentRecording is null)
            {
                _hasAutoShownCurrentRecording = false;
            }
        });
    }

    private void HandleAutomaticCoordinatorContextChanged(object? sender, AutomaticCoordinatorContextChangedEventArgs args)
    {
        _application.Dispatcher.Invoke(() =>
        {
            _settings = args.Settings;
            _appRules = args.AppRules;
            if (_capability is not null)
            {
                _manualControlGateway.UpdateHostContext(_settings, _capability, _meetingSessionRepository);
                _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability, _appRules);
            }

            _mainShellWindow?.UpdateRuntimeSnapshot(BuildShellSnapshot());
        });
    }

    private void HandleAudioDeviceSnapshotChanged(object? sender, AudioDeviceInventorySnapshot snapshot)
    {
        _ = _application.Dispatcher.InvokeAsync(async () =>
        {
            await _manualControlGateway.HandleDeviceInventoryChangedAsync();
            _mainShellWindow?.UpdateRuntimeSnapshot(BuildShellSnapshot());
        });
    }

    private void HandleSourceDeviceDriftDetected(object? sender, SourceDeviceDrift drift)
    {
        _ = _application.Dispatcher.InvokeAsync(async () =>
        {
            await _manualControlGateway.HandleSourceDeviceDriftAsync(drift);
            _mainShellWindow?.UpdateRuntimeSnapshot(BuildShellSnapshot());
        });
    }

    private async Task RouteForActivationAsync()
    {
        var recovery = await _recoveryCoordinator.EvaluateAsync(CancellationToken.None);
        var decision = _router.Decide(
            _capability!,
            _settings.ToBootstrapSnapshot(),
            recovery,
            new HostLaunchContext(HostLaunchOrigin.UserLaunch));

        ApplyRouting(decision, recovery);
    }

    private ShellConfigurationSnapshot BuildShellSnapshot() =>
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings
        // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#screen-model.data-source
        new(
            Capability: _capability ?? new HostCapabilitySnapshot(HostCapabilityState.Blocked, false, LocalizationManager.Instance["Host_CapabilityUnavailable"]),
            ManualControl: _manualControlGateway.Snapshot,
            Paths: _paths,
            Settings: _settings,
            Secrets: _secrets,
            AppRules: _appRules,
            Devices: _deviceManager.CurrentSnapshot,
            RunningProcesses: _runningAudioProcessCatalog.GetCandidates(),
            RecentSessions: _meetingSessionRepository?.ListRecent(limit: 200, offset: 0) ?? Array.Empty<MeetingSessionListItem>());

    private Task<WindowOperationResult> RetryTranscriptionAsync(string sessionId)
    {
        if (_meetingSessionRepository is null)
        {
            return Task.FromResult(WindowOperationResult.Fail(LocalizationManager.Instance["Host_MeetingSessionsUnavailable"]));
        }

        var queued = _meetingSessionRepository.TryQueueTranscriptionRetry(sessionId, DateTimeOffset.UtcNow);
        if (!queued)
        {
            return Task.FromResult(WindowOperationResult.Fail(LocalizationManager.Instance["Host_RetryUnavailable"]));
        }

        _logger.LogEvent(
            "Info",
            "TX_QUEUED",
            "Manual transcription retry queued from Home.",
            sessionId: sessionId,
            jobName: "HomeRetry");

        return Task.FromResult(WindowOperationResult.Ok());
    }

    private async Task<WindowOperationResult> CompleteWizardAsync(WizardCompletionRequest request)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.completion
        if (_appRuleRepository is null)
        {
            return WindowOperationResult.Fail(LocalizationManager.Instance["Host_AppRulesUnavailable"]);
        }

        try
        {
            var sanitizedSettings = SanitizeForCapability(CreateWizardSettings(request));
            var preCompletionSettings = sanitizedSettings with { OnboardingCompleted = false };
            var completedSettings = sanitizedSettings with { OnboardingCompleted = true };

            await _settingsStore.SaveAsync(preCompletionSettings, CancellationToken.None);
            await _secretStore.SaveAsync(new AppSecrets(request.FireworksApiKey.Trim()), CancellationToken.None);
            await _appRuleRepository.ReplaceAllAsync(request.SelectedApplications, CancellationToken.None);
            await _settingsStore.SaveAsync(completedSettings, CancellationToken.None);

            _settings = completedSettings;
            _secrets = new AppSecrets(request.FireworksApiKey.Trim());
            _appRules = await _appRuleRepository.ListAsync(CancellationToken.None);
            LocalizationManager.Instance.SwitchLanguage(_settings.General.Language ?? "ru");
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);

            var recovery = await _recoveryCoordinator.EvaluateAsync(CancellationToken.None);
            var decision = _router.Decide(_capability!, _settings.ToBootstrapSnapshot(), recovery, _launchArguments.ToLaunchContext());
            ApplyRouting(decision, recovery);
            return WindowOperationResult.Ok();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "First-run wizard completion failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveGeneralAsync(GeneralSettings general)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
        var previousGeneral = _settings.General;
        if (!_globalHotkeys.Apply(general.Hotkeys, out var hotkeyError))
        {
            return WindowOperationResult.Fail(hotkeyError);
        }

        try
        {
            _settings = _settings with { General = general };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            LocalizationManager.Instance.SwitchLanguage(_settings.General.Language ?? "ru");
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok();
        }
        catch (Exception exception)
        {
            _globalHotkeys.Apply(previousGeneral.Hotkeys, out _);
            _logger.Error(exception, "General settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveRecordingAsync(RecordingSettings recording)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
        try
        {
            _settings = SanitizeForCapability(_settings with { Recording = recording });
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok(IsActiveRecordingInProgress()
                ? LocalizationManager.Instance["Host_SaveNote_NextRecording"]
                : null);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Recording settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveDevicesAsync(DeviceSettings devices)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
        try
        {
            _settings = _settings with { Devices = devices };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok(IsActiveRecordingInProgress()
                ? LocalizationManager.Instance["Host_SaveNote_NextRecording"]
                : null);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Device settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveApplicationsAsync(ApplicationSectionSaveRequest request)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
        if (_appRuleRepository is null)
        {
            return WindowOperationResult.Fail(LocalizationManager.Instance["Host_AppRulesUnavailable"]);
        }

        try
        {
            _settings = _settings with
            {
                Applications = _settings.Applications with
                {
                    AutoDiscoveryPolicy = request.AutoDiscoveryPolicy,
                    IgnoredAppSuggestions = request.IgnoredAppSuggestions.ToArray(),
                    Exclusions = request.Exclusions.ToArray()
                }
            };

            await _appRuleRepository.ReplaceAllAsync(request.AppRules, CancellationToken.None);
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _appRules = await _appRuleRepository.ListAsync(CancellationToken.None);
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Applications settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveStorageAsync(StorageSettings storage)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
        try
        {
            _settings = _settings with { Storage = storage };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok(IsActiveRecordingInProgress()
                ? LocalizationManager.Instance["Host_SaveNote_NextRecording"]
                : null);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Storage settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private async Task<WindowOperationResult> SaveTranscriptionAsync(TranscriptionSectionSaveRequest request)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
        try
        {
            if (request.RemoveApiKey)
            {
                _secrets = AppSecrets.Empty;
            }
            else
            {
                _secrets = new AppSecrets(request.FireworksApiKey?.Trim());
            }

            _settings = _settings with { Transcription = request.Settings };
            await _secretStore.SaveAsync(_secrets, CancellationToken.None);
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _manualControlGateway.UpdateHostContext(_settings, _capability!, _meetingSessionRepository);
            _automaticRecordingCoordinator?.UpdateHostContext(_settings, _capability!, _appRules);
            return WindowOperationResult.Ok();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Transcription settings save failed.");
            return WindowOperationResult.Fail(exception.Message);
        }
    }

    private ApplicationSettings CreateWizardSettings(WizardCompletionRequest request)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.language
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.storage
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.devices
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.recording-mode
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
        var recordingsFolder = NormalizeDefaultPath(request.RecordingsFolder, _paths.DefaultRecordingsDirectory);
        var transcriptsFolder = NormalizeDefaultPath(request.TranscriptsFolder, _paths.DefaultTranscriptsDirectory);
        var language = string.IsNullOrWhiteSpace(request.UiLanguage)
            ? ApplicationSettings.Default.General.Language
            : request.UiLanguage.Trim().ToLowerInvariant();

        return ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            General = ApplicationSettings.Default.General with
            {
                Language = language
            },
            Recording = ApplicationSettings.Default.Recording with
            {
                Mode = request.RecordingMode.ToLowerInvariant()
            },
            Devices = ApplicationSettings.Default.Devices with
            {
                OutputDeviceId = request.DetermineDevicesAutomatically || request.FollowSystemDefaultOutput ? null : request.OutputDeviceId,
                MicrophoneDeviceId = request.DetermineDevicesAutomatically || request.FollowSystemDefaultMic ? null : request.MicrophoneDeviceId,
                FollowSystemDefaultOutput = request.DetermineDevicesAutomatically || request.FollowSystemDefaultOutput,
                FollowSystemDefaultMic = request.DetermineDevicesAutomatically || request.FollowSystemDefaultMic,
                AutoDiscoverOutput = request.DetermineDevicesAutomatically,
                AutoDiscoverMic = request.DetermineDevicesAutomatically
            },
            Applications = ApplicationSettings.Default.Applications with
            {
                AutoDiscoveryPolicy = request.SuggestAppsAutomatically ? "ask_to_add" : "off",
                IgnoredAppSuggestions = []
            },
            Storage = ApplicationSettings.Default.Storage with
            {
                RecordingsFolder = recordingsFolder,
                TranscriptsFolder = transcriptsFolder
            }
        };
    }

     private ApplicationSettings SanitizeForCapability(ApplicationSettings settings)
     {
         // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#degraded
         return CapabilityAwareSettingsSanitizer.Sanitize(settings, _capability);
     }

    private static string? NormalizeDefaultPath(string? selectedPath, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return null;
        }

        var normalizedSelected = Path.GetFullPath(selectedPath.Trim());
        var normalizedDefault = Path.GetFullPath(defaultPath);
        return string.Equals(normalizedSelected, normalizedDefault, StringComparison.OrdinalIgnoreCase)
            ? null
            : normalizedSelected;
    }

    private bool IsActiveRecordingInProgress() =>
        _manualControlGateway.Snapshot.RecordingState is RecordingActivityState.Recording or RecordingActivityState.Paused;

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#hotkeys.runtime
    private void HandleGlobalHotkeyTriggered(object? sender, GlobalHotkeyAction action)
    {
        _ = _application.Dispatcher.InvokeAsync(async () =>
        {
            var manualSnapshot = _manualControlGateway.Snapshot;
            switch (action)
            {
                case GlobalHotkeyAction.ForceRecordToggle:
                    if (manualSnapshot.RecordingState is RecordingActivityState.Recording or RecordingActivityState.Paused)
                    {
                        await _manualControlGateway.StopCurrentRecordingAsync();
                    }
                    else if (manualSnapshot.RecordingState == RecordingActivityState.Idle && manualSnapshot.CanStartForceRecord)
                    {
                        await _manualControlGateway.StartForceRecordAsync();
                    }
                    else
                    {
                        ShowHotkeyFeedback(LocalizationManager.Instance["Host_Hotkey_ForceRecord_Title"], GetForceRecordToggleFeedback(manualSnapshot));
                    }

                    break;
                case GlobalHotkeyAction.PrivacyPauseToggle:
                    await _manualControlGateway.TogglePrivacyPauseAsync();
                    break;
                case GlobalHotkeyAction.DiscardCurrent:
                    if (_manualControlGateway.Snapshot.CanDiscard && ConfirmDiscardCurrentRecording())
                    {
                        await _manualControlGateway.DiscardCurrentRecordingAsync();
                    }
                    else if (!_manualControlGateway.Snapshot.CanDiscard)
                    {
                        ShowHotkeyFeedback(LocalizationManager.Instance["Host_Hotkey_Discard_Title"], GetDiscardFeedback(_manualControlGateway.Snapshot));
                    }

                    break;
                case GlobalHotkeyAction.OpenMainWindow:
                    await OpenOrFocusAppAsync();
                    break;
            }

            if (_mainShellWindow is not null)
            {
                _mainShellWindow.UpdateRuntimeSnapshot(BuildShellSnapshot());
                if (_manualControlGateway.Snapshot.CurrentRecording is not null)
                {
                    _mainShellWindow.ShowCurrentRecordingSurface();
                }
            }
        });
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#hotkeys.runtime
    private void ShowHotkeyFeedback(string title, string message)
    {
        _trayIcon?.ShowNotification(title, message, ToolTipIcon.Info);
    }

    private static string GetForceRecordToggleFeedback(ManualControlSnapshot snapshot) => snapshot.RecordingState switch
    {
        RecordingActivityState.Stopping => LocalizationManager.Instance["Host_Hotkey_ForceRecord_Stopping"],
        RecordingActivityState.AwaitingConfirmation => snapshot.BlockingReason ?? LocalizationManager.Instance["Host_Hotkey_ForceRecord_AwaitingConfirmation"],
        RecordingActivityState.OnboardingBlocked => snapshot.BlockingReason ?? LocalizationManager.Instance["Host_Hotkey_ForceRecord_OnboardingBlocked"],
        RecordingActivityState.Idle => snapshot.BlockingReason ?? LocalizationManager.Instance["Host_Hotkey_ForceRecord_Unavailable"],
        _ => snapshot.BlockingReason ?? LocalizationManager.Instance["Host_Hotkey_ForceRecord_CannotRun"]
    };

    private static string GetDiscardFeedback(ManualControlSnapshot snapshot) => snapshot.RecordingState switch
    {
        RecordingActivityState.Stopping => LocalizationManager.Instance["Host_Hotkey_ForceRecord_Stopping"],
        RecordingActivityState.Idle => LocalizationManager.Instance["Host_Hotkey_Discard_NoRecording"],
        RecordingActivityState.OnboardingBlocked => LocalizationManager.Instance["Host_Hotkey_Discard_NoRecording"],
        _ => snapshot.BlockingReason ?? LocalizationManager.Instance["Host_Hotkey_Discard_Unavailable"]
    };

    private static bool ConfirmDiscardCurrentRecording() =>
        WpfMessageBox.Show(
            LocalizationManager.Instance["Host_Discard_Confirm_Message"],
            LocalizationManager.Instance["Host_Discard_Confirm_Title"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
