using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.Theming;
using IsTranscribe.Desktop.ViewModels;
using IsTranscribe.Desktop.Views;

namespace IsTranscribe.Desktop;

/// <summary>
/// Avalonia composition root for the compact tray-first desktop experience.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#tray
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#lifecycle
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#placement
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// </remarks>
public sealed partial class App : Avalonia.Application
{
    private static readonly Uri AppIconUri =
        new("avares://IsTranscribe.Desktop.UI/Assets/isTranscribe.png");

    private readonly DesktopComposition _composition;
    private readonly LocalizationService _localization = new();
    private readonly CalmInstrumentThemeService _themes = new();
    private readonly IPlatformShell _shell;
    private readonly IWindowPlacementStore _windowPlacementStore = new LocalWindowPlacementStore();
    private readonly IRecordingOverlayPlacementStore _recordingOverlayPlacementStore =
        new LocalRecordingOverlayPlacementStore();
    private IApplicationRuntime? _runtime;
    private IApplicationInstanceCoordinator? _instanceCoordinator;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private IActivatableLifetime? _activatableLifetime;
    private MainWindowViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private SetupViewModel? _setupViewModel;
    private SetupWindow? _setupWindow;
    private SettingsViewModel? _settingsViewModel;
    private SettingsWindow? _settingsWindow;
    private DiagnosticsViewModel? _diagnosticsViewModel;
    private DiagnosticsWindow? _diagnosticsWindow;
    private HelpWindow? _helpWindow;
    private AskPromptController? _askPromptController;
    private PassiveNotificationController? _notificationController;
    private RecordingOverlayController? _recordingOverlayController;
    private RecordingOverlayAnchor? _recordingOverlayAnchor;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayServiceMenuItem;
    private NativeMenuItem? _trayRecordMenuItem;
    private NativeMenuItem? _trayPauseResumeMenuItem;
    private NativeMenuItem? _trayFinishMenuItem;
    private bool _setupCompleting;
    private bool _quitRequested;
    private bool _shutdownPrepared;
    private Task? _shutdownTask;
    private ApplicationActivityState? _trayActivity;
    private static ApplicationInstanceCoordinatorLease? s_preRegisteredInstanceCoordinator;

    public App()
    {
        _composition = DesktopComposition.Current;
        _shell = _composition.Shell;
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
    public static void SetPreRegisteredInstanceCoordinator(
        ApplicationInstanceCoordinatorLease coordinatorLease)
    {
        ArgumentNullException.ThrowIfNull(coordinatorLease);
        if (Interlocked.CompareExchange(
                ref s_preRegisteredInstanceCoordinator,
                coordinatorLease,
                comparand: null) is not null)
        {
            throw new InvalidOperationException("An early instance coordinator is already registered.");
        }
    }

    public static void ClearPreRegisteredInstanceCoordinator(
        ApplicationInstanceCoordinatorLease coordinatorLease) =>
        Interlocked.CompareExchange(
            ref s_preRegisteredInstanceCoordinator,
            value: null,
            comparand: coordinatorLease);

    public void DisposeInstanceCoordinatorAfterStartupFailure()
    {
        var coordinator = Interlocked.Exchange(ref _instanceCoordinator, null);
        try
        {
            coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Preserve the startup exception; process exit still releases the named identity.
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _localization.Attach(this);
        _themes.Attach(this);
        _localization.LanguageChanged += Localization_OnLanguageChanged;

        _trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (_trayIcon is not null)
        {
            _trayIcon.Icon = CreateAppIcon();
            var menuItems = _trayIcon.Menu?.Items;
            _trayServiceMenuItem = menuItems?.ElementAtOrDefault(0) as NativeMenuItem;
            _trayRecordMenuItem = menuItems?.ElementAtOrDefault(2) as NativeMenuItem;
            _trayPauseResumeMenuItem = menuItems?.ElementAtOrDefault(3) as NativeMenuItem;
            _trayFinishMenuItem = menuItems?.ElementAtOrDefault(4) as NativeMenuItem;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            EnsureSupportedPlatformArchitecture();
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _instanceCoordinator = Interlocked.Exchange(
                ref s_preRegisteredInstanceCoordinator,
                    null)?.Take()
                ?? _composition.CreateInstanceCoordinator();
            var role = _instanceCoordinator.RegisterAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (role == ApplicationInstanceRole.Secondary)
            {
                try
                {
                    _instanceCoordinator.NotifyPrimaryAsync(CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    _instanceCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _instanceCoordinator = null;
                }
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _runtime = _composition.CreateRuntime(desktop.Args ?? []);
            _mainViewModel = new MainWindowViewModel(_runtime, _shell, _localization);
            _mainViewModel.SettingsRequested += MainViewModel_OnSettingsRequested;
            _mainViewModel.HelpRequested += MainViewModel_OnHelpRequested;
            _mainViewModel.DiagnosticsRequested += MainViewModel_OnDiagnosticsRequested;
            _mainViewModel.InitializationRetryRequested += MainViewModel_OnInitializationRetryRequested;
            _mainViewModel.DiscardRequested += MainViewModel_OnDiscardRequested;
            _mainViewModel.RecentRecordingRemovalRequested += MainViewModel_OnRecentRecordingRemovalRequested;
            _mainViewModel.RecentRecordingRenameRequested += MainViewModel_OnRecentRecordingRenameRequested;
            _mainViewModel.RecentRecordingTranscriptionRequested +=
                MainViewModel_OnRecentRecordingTranscriptionRequested;
            _mainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
                Icon = CreateAppIcon()
            };
            _mainWindow.AttachPlacementStore(_windowPlacementStore);
            _mainWindow.Closing += MainWindow_OnClosing;
            _mainWindow.QuitRequested += MainWindow_OnQuitRequested;

            _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
            _instanceCoordinator.ActivationRequested += InstanceCoordinator_OnActivationRequested;
            desktop.ShutdownRequested += Desktop_OnShutdownRequested;
            desktop.Exit += Desktop_OnExit;
            if (ApplicationLifetime is IActivatableLifetime activatableLifetime)
            {
                _activatableLifetime = activatableLifetime;
                activatableLifetime.Activated += ActivatableLifetime_OnActivated;
            }

            var showAdvancedDiagnostics = desktop.Args?.Any(static argument =>
                string.Equals(argument, "--advanced-diagnostics", StringComparison.OrdinalIgnoreCase)) == true;
            _askPromptController = new AskPromptController(
                _runtime,
                _localization,
                () => _mainWindow,
                TimeProvider.System,
                showAdvancedDiagnostics,
                surfaceFactory: viewModel => new AskPromptWindow(
                    viewModel,
                    _composition.PresentAskPromptWindow),
                systemNotifications: _composition.SystemNotifications);
            _notificationController = new PassiveNotificationController(
                _runtime,
                _localization,
                OpenOrFocusMainWindow,
                () => _mainWindow,
                TimeProvider.System,
                _composition.SystemNotifications);
            _recordingOverlayController = new RecordingOverlayController(
                _runtime,
                () => _mainWindow,
                () => new RecordingOverlayWindow(
                    _mainViewModel,
                    _recordingOverlayPlacementStore,
                    _recordingOverlayAnchor));

            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = true;
            }

            var startHidden = desktop.Args?.Any(static argument =>
                string.Equals(argument, "--autostart", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)) == true;
            _ = InitializeAndRouteAsync(startHidden);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAndRouteAsync(bool startHidden)
    {
        if (_mainViewModel is null || _runtime is null)
        {
            return;
        }

        await _mainViewModel.InitializeAsync(CancellationToken.None);
        ApplyPresentationSettings(_runtime.Snapshot.UserSettings);
        if (!_mainViewModel.InitializationSucceeded)
        {
            OpenOrFocusMainWindow();
            return;
        }

        _recordingOverlayAnchor = await _recordingOverlayPlacementStore.LoadAsync(
            CancellationToken.None);
        UpdateTrayPresentation(_runtime.Snapshot);
        _askPromptController?.Start();
        _notificationController?.Start();
        _recordingOverlayController?.Start();

        if (!_runtime.Snapshot.UserSettings.OnboardingCompleted)
        {
            OpenSetupWindow();
            return;
        }

        if (!startHidden)
        {
            OpenOrFocusMainWindow();
        }
    }

    private void OpenSetupWindow()
    {
        if (_runtime is null || _desktop is null)
        {
            return;
        }

        _setupViewModel?.Dispose();
        _setupViewModel = new SetupViewModel(
            _runtime,
            _localization,
            _runtime.Snapshot.UserSettings,
            _runtime.Snapshot.AvailableMicrophones,
            permissionService: _composition.Permissions);
        _setupViewModel.Completed += SetupViewModel_OnCompleted;
        _setupWindow = new SetupWindow { Icon = CreateAppIcon() };
        _setupWindow.Bind(_setupViewModel);
        _setupWindow.Closing += SetupWindow_OnClosing;
        _desktop.MainWindow = _setupWindow;
        _setupWindow.Show();
    }

    private void SetupViewModel_OnCompleted(object? sender, EventArgs args)
    {
        if (_setupWindow is null)
        {
            return;
        }

        _setupCompleting = true;
        _setupWindow.Closing -= SetupWindow_OnClosing;
        _setupWindow.Close();
        _setupWindow = null;
        if (_setupViewModel is not null)
        {
            _setupViewModel.Completed -= SetupViewModel_OnCompleted;
            _setupViewModel.Dispose();
            _setupViewModel = null;
        }

        _setupCompleting = false;
        OpenOrFocusMainWindow();
    }

    private void SetupWindow_OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_setupCompleting || _quitRequested)
        {
            return;
        }

        _ = QuitAsync();
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_quitRequested || _mainWindow is null)
        {
            return;
        }

        args.Cancel = true;
        _mainWindow.Hide();
    }

    private async void MainWindow_OnQuitRequested(object? sender, EventArgs args) => await QuitAsync();

    private void MainViewModel_OnSettingsRequested(object? sender, EventArgs args) => OpenSettingsWindow();

    private void MainViewModel_OnHelpRequested(object? sender, EventArgs args) => OpenHelpWindow();

    private void MainViewModel_OnDiagnosticsRequested(object? sender, EventArgs args) => OpenDiagnosticsWindow();

    private async void MainViewModel_OnInitializationRetryRequested(object? sender, EventArgs args) =>
        await InitializeAndRouteAsync(startHidden: false);

    private async void MainViewModel_OnDiscardRequested(object? sender, EventArgs args)
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        using var viewModel = new ConfirmationDialogViewModel(
            _localization,
            "String.Confirmation.Discard.Title",
            "String.Confirmation.Discard.Description",
            "String.Action.Discard",
            "String.Action.Cancel",
            primaryIsDestructive: true);
        var dialog = new ConfirmationDialog { Icon = CreateAppIcon() };
        if (await dialog.ShowAsync(_mainWindow, viewModel) == ConfirmationDialogResult.Primary)
        {
            await _mainViewModel.ConfirmDiscardAsync();
        }
    }

    private async void MainViewModel_OnRecentRecordingRemovalRequested(
        object? sender,
        RecentRecordingRemovalRequestedEventArgs args)
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        using var viewModel = new ConfirmationDialogViewModel(
            _localization,
            "String.Confirmation.RemoveHistory.Title",
            "String.Confirmation.RemoveHistory.Description",
            "String.Confirmation.RemoveHistory.KeepFile",
            "String.Action.Cancel",
            args.HasAudioFile
                ? "String.Confirmation.RemoveHistory.DeleteFile"
                : null);
        var dialog = new ConfirmationDialog { Icon = CreateAppIcon() };
        var result = await dialog.ShowAsync(_mainWindow, viewModel);
        if (result == ConfirmationDialogResult.Primary)
        {
            await _mainViewModel.ConfirmRecentRemovalAsync(args.SessionId, deleteAudioFile: false);
        }
        else if (result == ConfirmationDialogResult.Destructive)
        {
            await _mainViewModel.ConfirmRecentRemovalAsync(args.SessionId, deleteAudioFile: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    private async void MainViewModel_OnRecentRecordingRenameRequested(
        object? sender,
        RecentRecordingRenameRequestedEventArgs args)
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        using var viewModel = new RenameRecordingDialogViewModel(_localization, args.CurrentTitle);
        var dialog = new RenameRecordingDialog { Icon = CreateAppIcon() };
        var displayTitle = await dialog.ShowAsync(_mainWindow, viewModel);
        if (displayTitle is not null)
        {
            await _mainViewModel.ConfirmRecentRenameAsync(args.SessionId, displayTitle);
        }
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
    private async void MainViewModel_OnRecentRecordingTranscriptionRequested(
        object? sender,
        RecentRecordingTranscriptionRequestedEventArgs args)
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        if (args.ReplaceExisting)
        {
            using var viewModel = new ConfirmationDialogViewModel(
                _localization,
                "String.Confirmation.ReplaceTranscript.Title",
                "String.Confirmation.ReplaceTranscript.Description",
                "String.Transcription.Action.ReplaceTranscript",
                "String.Action.Cancel");
            var dialog = new ConfirmationDialog { Icon = CreateAppIcon() };
            if (await dialog.ShowAsync(_mainWindow, viewModel) != ConfirmationDialogResult.Primary)
            {
                return;
            }
        }

        await _mainViewModel.ConfirmRecentTranscriptionAsync(
            args.SessionId,
            args.ReplaceExisting);
    }

    private void Runtime_OnSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyPresentationSettings(snapshot.UserSettings);
            UpdateTrayPresentation(snapshot);
        });
    }

    private void ApplyPresentationSettings(RuntimeUserSettingsSnapshot settings)
    {
        var language = _settingsViewModel?.LanguagePreviewValue ?? settings.Language;
        _localization.SetLanguage(string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.English
            : UiLanguage.Russian);
        _themes.SetTheme(settings.Theme switch
        {
            "light" => UiThemeMode.Light,
            "dark" => UiThemeMode.Dark,
            _ => UiThemeMode.System
        });
    }

    private void UpdateTrayPresentation(ApplicationRuntimeSnapshot snapshot)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _localization.Format(
                "String.Tray.Tooltip.Format",
                _localization.Get($"String.State.{snapshot.Activity}.Title"));
            if (_trayActivity != snapshot.Activity)
            {
                using var iconStream = TrayStateIconFactory.CreatePng(snapshot.Activity);
                _trayIcon.Icon = new WindowIcon(iconStream);
                _trayActivity = snapshot.Activity;
            }
        }

        if (_trayServiceMenuItem is not null)
        {
            _trayServiceMenuItem.Header = _localization.Get(
                snapshot.ServiceEnabled ? "String.Service.Listening" : "String.Service.Paused");
            _trayServiceMenuItem.IsEnabled = snapshot.Activity != ApplicationActivityState.Recording;
        }

        if (_trayRecordMenuItem is not null)
        {
            _trayRecordMenuItem.IsVisible = snapshot.Activity != ApplicationActivityState.Recording;
            _trayRecordMenuItem.IsEnabled = snapshot.ServiceEnabled
                && snapshot.Activity is ApplicationActivityState.Listening
                    or ApplicationActivityState.Suspected;
        }

        if (_trayPauseResumeMenuItem is not null)
        {
            _trayPauseResumeMenuItem.IsVisible = snapshot.Activity == ApplicationActivityState.Recording;
            _trayPauseResumeMenuItem.Header = _localization.Get(
                snapshot.ActiveMeeting?.IsPaused == true ? "String.Action.Resume" : "String.Action.Pause");
        }

        if (_trayFinishMenuItem is not null)
        {
            _trayFinishMenuItem.IsVisible = snapshot.Activity == ApplicationActivityState.Recording;
        }
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_runtime is not null)
        {
            UpdateTrayPresentation(_runtime.Snapshot);
        }
    }

    private void InstanceCoordinator_OnActivationRequested(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(OpenOrFocusPrimarySurface);

    private void ActivatableLifetime_OnActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind == ActivationKind.Reopen)
        {
            Dispatcher.UIThread.Post(OpenOrFocusPrimarySurface);
        }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs args) => OpenOrFocusPrimarySurface();

    private void OpenMenuItem_OnClick(object? sender, EventArgs args) => OpenOrFocusPrimarySurface();

    private void SettingsMenuItem_OnClick(object? sender, EventArgs args) => OpenSettingsWindow();

    private async void TrayServiceMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (_mainViewModel?.ToggleServiceCommand.CanExecute(null) == true)
        {
            await _mainViewModel.ToggleServiceCommand.ExecuteAsync(null);
        }
    }

    private async void TrayRecordMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (_mainViewModel?.StartManualRecordingCommand.CanExecute(null) == true)
        {
            await _mainViewModel.StartManualRecordingCommand.ExecuteAsync(null);
        }
    }

    private async void TrayPauseResumeMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (_mainViewModel?.PauseOrResumeCommand.CanExecute(null) == true)
        {
            await _mainViewModel.PauseOrResumeCommand.ExecuteAsync(null);
        }
    }

    private async void TrayFinishMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (_mainViewModel?.FinishRecordingCommand.CanExecute(null) == true)
        {
            await _mainViewModel.FinishRecordingCommand.ExecuteAsync(null);
        }
    }

    private async void QuitMenuItem_OnClick(object? sender, EventArgs args) => await QuitAsync();

    private void OpenOrFocusPrimarySurface()
    {
        if (_setupWindow?.IsVisible == true)
        {
            _setupWindow.Activate();
            return;
        }

        OpenOrFocusMainWindow();
    }

    private void OpenOrFocusMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            if (_desktop is not null)
            {
                _desktop.MainWindow = _mainWindow;
            }

            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void OpenSettingsWindow()
    {
        if (_runtime is null || !_runtime.Snapshot.UserSettings.OnboardingCompleted)
        {
            OpenOrFocusPrimarySurface();
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsViewModel = new SettingsViewModel(
                _runtime,
                _localization,
                _themes,
                _runtime.Snapshot.UserSettings,
                _runtime.Snapshot.AvailableMicrophones,
                shell: _shell,
                permissionService: _composition.Permissions);
            _settingsViewModel.DiagnosticsRequested += SettingsViewModel_OnDiagnosticsRequested;
            _settingsWindow = new SettingsWindow { Icon = CreateAppIcon() };
            _settingsWindow.Bind(_settingsViewModel);
        }

        if (!_settingsWindow.IsVisible)
        {
            if (_mainWindow?.IsVisible == true)
            {
                _settingsWindow.Show(_mainWindow);
            }
            else
            {
                _settingsWindow.Show();
            }
        }

        _settingsWindow.Activate();
    }

    private void SettingsViewModel_OnDiagnosticsRequested(object? sender, EventArgs args) => OpenDiagnosticsWindow();

    private void OpenDiagnosticsWindow()
    {
        if (_runtime is null)
        {
            return;
        }

        if (_diagnosticsWindow?.IsVisible == true)
        {
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsViewModel?.Dispose();
        _diagnosticsViewModel = new DiagnosticsViewModel(_runtime, _localization, _shell);
        _diagnosticsWindow = new DiagnosticsWindow { Icon = CreateAppIcon() };
        _diagnosticsWindow.Bind(_diagnosticsViewModel);
        _diagnosticsWindow.Closed += DiagnosticsWindow_OnClosed;
        if (_settingsWindow?.IsVisible == true)
        {
            _diagnosticsWindow.Show(_settingsWindow);
        }
        else if (_mainWindow?.IsVisible == true)
        {
            _diagnosticsWindow.Show(_mainWindow);
        }
        else
        {
            _diagnosticsWindow.Show();
        }
    }

    private void DiagnosticsWindow_OnClosed(object? sender, EventArgs args)
    {
        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Closed -= DiagnosticsWindow_OnClosed;
            _diagnosticsWindow = null;
        }

        _diagnosticsViewModel?.Dispose();
        _diagnosticsViewModel = null;
    }

    private void OpenHelpWindow()
    {
        if (_helpWindow?.IsVisible == true)
        {
            _helpWindow.Activate();
            return;
        }

        _helpWindow = new HelpWindow { Icon = CreateAppIcon() };
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        if (_mainWindow?.IsVisible == true)
        {
            _helpWindow.Show(_mainWindow);
        }
        else
        {
            _helpWindow.Show();
        }
    }

    private Task QuitAsync()
    {
        if (_shutdownTask is not null)
        {
            return _shutdownTask;
        }

        _quitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        _shutdownTask = PrepareAndShutdownAsync();
        return _shutdownTask;
    }

    private async Task PrepareAndShutdownAsync()
    {
        try
        {
            if (_settingsViewModel is not null)
            {
                await _settingsViewModel.FlushAsync(CancellationToken.None);
            }
        }
        catch
        {
        }

        try
        {
            if (_notificationController is not null)
            {
                await _notificationController.DisposeAsync();
            }
        }
        catch
        {
        }

        try
        {
            if (_askPromptController is not null)
            {
                await _askPromptController.DisposeAsync();
            }
        }
        catch
        {
        }

        try
        {
            _recordingOverlayController?.Dispose();
        }
        catch
        {
        }

        _settingsWindow?.CloseForShutdown();
        _diagnosticsWindow?.Close();
        _helpWindow?.Close();
        _setupWindow?.Close();
        _mainWindow?.Close();

        DetachAndDisposeViewModels();

        if (_runtime is not null)
        {
            _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
            try
            {
                await _runtime.DisposeAsync();
            }
            catch
            {
            }
        }

        if (_instanceCoordinator is not null)
        {
            _instanceCoordinator.ActivationRequested -= InstanceCoordinator_OnActivationRequested;
            try
            {
                await _instanceCoordinator.DisposeAsync();
            }
            catch
            {
            }
        }

        _trayIcon?.Dispose();
        _shutdownPrepared = true;
        _desktop?.Shutdown();
    }

    private void DetachAndDisposeViewModels()
    {
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        if (_activatableLifetime is not null)
        {
            _activatableLifetime.Activated -= ActivatableLifetime_OnActivated;
            _activatableLifetime = null;
        }
        if (_mainViewModel is not null)
        {
            _mainViewModel.SettingsRequested -= MainViewModel_OnSettingsRequested;
            _mainViewModel.HelpRequested -= MainViewModel_OnHelpRequested;
            _mainViewModel.DiagnosticsRequested -= MainViewModel_OnDiagnosticsRequested;
            _mainViewModel.InitializationRetryRequested -= MainViewModel_OnInitializationRetryRequested;
            _mainViewModel.DiscardRequested -= MainViewModel_OnDiscardRequested;
            _mainViewModel.RecentRecordingRemovalRequested -= MainViewModel_OnRecentRecordingRemovalRequested;
            _mainViewModel.RecentRecordingRenameRequested -= MainViewModel_OnRecentRecordingRenameRequested;
            _mainViewModel.RecentRecordingTranscriptionRequested -=
                MainViewModel_OnRecentRecordingTranscriptionRequested;
            _mainViewModel.Dispose();
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= MainWindow_OnClosing;
            _mainWindow.QuitRequested -= MainWindow_OnQuitRequested;
        }

        if (_setupViewModel is not null)
        {
            _setupViewModel.Completed -= SetupViewModel_OnCompleted;
            _setupViewModel.Dispose();
        }

        if (_settingsViewModel is not null)
        {
            _settingsViewModel.DiagnosticsRequested -= SettingsViewModel_OnDiagnosticsRequested;
            _settingsViewModel.Dispose();
        }

        _diagnosticsViewModel?.Dispose();
    }

    private void Desktop_OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_shutdownPrepared)
        {
            return;
        }

        args.Cancel = true;
        _ = QuitAsync();
    }

    private void Desktop_OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        if (_desktop is not null)
        {
            _desktop.ShutdownRequested -= Desktop_OnShutdownRequested;
            _desktop.Exit -= Desktop_OnExit;
        }

        if (!_shutdownPrepared)
        {
            _recordingOverlayController?.Dispose();
            DetachAndDisposeViewModels();
            _ = _runtime?.DisposeAsync();
            _ = _instanceCoordinator?.DisposeAsync();
            _trayIcon?.Dispose();
        }
    }

    private void EnsureSupportedPlatformArchitecture()
    {
        if (!_composition.PlatformDescriptor.IsReleaseArchitectureSupported)
        {
            throw new PlatformNotSupportedException(
                $"The current {_composition.PlatformDescriptor.OperatingSystem} architecture is unsupported.");
        }
    }

    private static WindowIcon CreateAppIcon()
    {
        // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
        using var stream = AssetLoader.Open(AppIconUri);
        return new WindowIcon(stream);
    }
}
