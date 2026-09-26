using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using IsTranscribe.App.Configuration;
using IsTranscribe.App.ManualControls;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using IsTranscribe.App.Strings;
using ComboBox = System.Windows.Controls.ComboBox;

namespace IsTranscribe.App.Windows;

public partial class MainShellWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int HomeMeetingsPageSize = 50;

    private readonly EditableCollection<EditableAppRuleItem> _whitelist = [];
    private readonly EditableCollection<SelectableAppSuggestionItem> _knownApplications = [];
    private readonly EditableCollection<EditableStringItem> _ignoredAppSuggestions = [];
    private readonly EditableCollection<EditableStringItem> _exclusions = [];
    private readonly EditableCollection<HomeMeetingSessionItem> _homeMeetings = [];
    private ShellConfigurationSnapshot? _snapshot;
    private GeneralSettings _baselineGeneral = GeneralSettings.Default;
    private RecordingSettings _baselineRecording = RecordingSettings.Default;
    private DeviceSettings _baselineDevices = DeviceSettings.Default;
    private string _baselineAutoDiscoveryPolicy = ApplicationCatalogSettings.Default.AutoDiscoveryPolicy;
    private IReadOnlyList<AppRuleRecord> _baselineAppRules = Array.Empty<AppRuleRecord>();
    private IReadOnlyList<string> _baselineIgnoredAppSuggestions = Array.Empty<string>();
    private IReadOnlyList<string> _baselineUserExclusions = Array.Empty<string>();
    private StorageSettings _baselineStorage = StorageSettings.Default;
    private TranscriptionSettings _baselineTranscription = TranscriptionSettings.Default;
    private string? _baselineApiKey;
    private bool _transcriptionApiKeyEditMode;
    private bool _transcriptionApiKeyRemoved;
    private bool _showTranscriptionApiKey;
    private bool _syncingTranscriptionApiKey;
    private bool _syncingKnownApplications;
    private string _activeSettingsSection = "General";
    private bool _suppressSettingsTabSelection;
    private bool _isLoadingSnapshot;
    private string? _recordingSaveMessage;
    private string? _storageSaveMessage;
    private int _homeMeetingsVisibleCount = HomeMeetingsPageSize;

    public MainShellWindow()
    {
        InitializeComponent();
        KnownApplicationsItemsControl.ItemsSource = _knownApplications;
        WhitelistListBox.ItemsSource = _whitelist;
        IgnoredSuggestionsListBox.ItemsSource = _ignoredAppSuggestions;
        ExclusionsListBox.ItemsSource = _exclusions;
        HomeMeetingsListBox.ItemsSource = _homeMeetings;
        InitializeStaticOptions();
        LocalizationManager.Instance.LanguageChanged += (_, _) => Dispatcher.Invoke(InitializeStaticOptions);
        NavigationHeaderText.Text = LocalizationManager.Instance["Shell_Tab_Home"];
    }

    public bool AllowClose { get; set; }

    public Func<Task<ShellConfigurationSnapshot>>? RefreshSnapshotAsync { get; set; }

    public Func<GeneralSettings, Task<WindowOperationResult>>? SaveGeneralAsync { get; set; }

    public Func<RecordingSettings, Task<WindowOperationResult>>? SaveRecordingAsync { get; set; }

    public Func<DeviceSettings, Task<WindowOperationResult>>? SaveDevicesAsync { get; set; }

    public Func<ApplicationSectionSaveRequest, Task<WindowOperationResult>>? SaveApplicationsAsync { get; set; }

    public Func<StorageSettings, Task<WindowOperationResult>>? SaveStorageAsync { get; set; }

    public Func<TranscriptionSectionSaveRequest, Task<WindowOperationResult>>? SaveTranscriptionAsync { get; set; }

    public Func<string, Task<WindowOperationResult>>? RetryTranscriptionAsync { get; set; }

    public event EventHandler? ForceRecordRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? PauseRequested;

    public event EventHandler? ResumeRequested;

    public event EventHandler? DiscardRequested;

    public event EventHandler? TogglePrivacyPauseRequested;

    public event EventHandler? OpenLogsRequested;

    public event EventHandler? CloseRequested;

    public void LoadSnapshot(ShellConfigurationSnapshot snapshot)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings
        _snapshot = snapshot;
        _isLoadingSnapshot = true;
        try
        {
            ApplyRuntimeState(snapshot);
            LoadGeneralSection(snapshot.Settings.General);
            LoadRecordingSection(snapshot);
            LoadDevicesSection(snapshot);
            LoadApplicationsSection(snapshot);
            LoadStorageSection(snapshot);
            LoadTranscriptionSection(snapshot.Settings.Transcription, snapshot.Secrets);
        }
        finally
        {
            _isLoadingSnapshot = false;
        }

        UpdateSaveButtons();
    }

    public void UpdateRuntimeSnapshot(ShellConfigurationSnapshot snapshot)
    {
        _snapshot = snapshot;
        ApplyRuntimeState(snapshot);
    }

    // @spec spec://modules/app/FEAT-008-modern-ui-design-system#behavior.windows.main-shell
    public void ShowCurrentRecordingSurface()
    {
        var item = RootNavListBox.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(static i => string.Equals(i.Tag?.ToString(), "CurrentRecording", StringComparison.Ordinal));
        if (item is not null)
        {
            RootNavListBox.SelectedItem = item;
        }
    }

    // @spec spec://modules/app/FEAT-008-modern-ui-design-system#behavior.windows.main-shell
    private void OnRootNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePanel is null || RootNavListBox.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString();

        HomePanel.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        CurrentRecordingPanel.Visibility = tag == "CurrentRecording" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (NavigationHeaderText is not null)
        {
            NavigationHeaderText.Text = tag switch
            {
                "Home" => LocalizationManager.Instance["Shell_Tab_Home"],
                "CurrentRecording" => LocalizationManager.Instance["Shell_Tab_CurrentRecording"],
                "Settings" => LocalizationManager.Instance["Shell_Tab_Settings"],
                _ => string.Empty
            };
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#degraded
    private void ApplyRuntimeState(ShellConfigurationSnapshot snapshot)
    {
        CapabilityStateText.Text = snapshot.Capability.State switch
        {
            HostCapabilityState.Full => LocalizationManager.Instance["Shell_Home_Capability_Full"],
            HostCapabilityState.Degraded => LocalizationManager.Instance["Shell_Home_Capability_Degraded"],
            _ => LocalizationManager.Instance["Shell_Home_Capability_Blocked"]
        };

        CapabilitySummaryText.Text = snapshot.Capability.Summary;
        OnboardingText.Text = snapshot.Settings.OnboardingCompleted
            ? LocalizationManager.Instance["Shell_Home_Onboarding_Completed"]
            : LocalizationManager.Instance["Shell_Home_Onboarding_Incomplete"];
        RecordingStateText.Text = string.Format(LocalizationManager.Instance["Shell_Home_RecordingState"], snapshot.ManualControl.RecordingState);
        PrivacyPauseText.Text = string.Format(LocalizationManager.Instance["Shell_Home_PrivacyPause"], snapshot.ManualControl.PrivacyPauseEnabled ? LocalizationManager.Instance["Shell_Home_PrivacyPause_On"] : LocalizationManager.Instance["Shell_Home_PrivacyPause_Off"]);
        LogsPathText.Text = string.Format(LocalizationManager.Instance["Shell_Home_LogsPath"], snapshot.Paths.LogsDirectory);

        var currentRecording = snapshot.ManualControl.CurrentRecording;
        CurrentRecordingEmptyStateText.Text = currentRecording is null
            ? snapshot.ManualControl.RecordingState == RecordingActivityState.AwaitingConfirmation && snapshot.ManualControl.PendingConfirmation is not null
                ? string.Format(LocalizationManager.Instance["Shell_CurrentRecording_AwaitConfirmation"], snapshot.ManualControl.PendingConfirmation.SourceApp ?? snapshot.ManualControl.PendingConfirmation.SourceSummary)
                : snapshot.ManualControl.BlockingReason ?? LocalizationManager.Instance["Shell_CurrentRecording_NoActive"]
            : string.Empty;
        CurrentRecordingStatusText.Text = currentRecording is null
            ? string.Format(LocalizationManager.Instance["Shell_CurrentRecording_State"], snapshot.ManualControl.RecordingState)
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_State"], currentRecording.StatusText);
        CurrentRecordingModeText.Text = currentRecording is null
            ? string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Mode"], LocalizationManager.Instance["Shell_CurrentRecording_Mode_None"])
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Mode"], currentRecording.Mode);
        CurrentRecordingTimerText.Text = currentRecording is null
            ? string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Duration"], "00:00:00")
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Duration"], currentRecording.RecordedDuration.ToString(@"hh\:mm\:ss"));
        CurrentRecordingPrivacyText.Text = string.Format(LocalizationManager.Instance["Shell_Home_PrivacyPause"], snapshot.ManualControl.PrivacyPauseEnabled ? LocalizationManager.Instance["Shell_Home_PrivacyPause_On"] : LocalizationManager.Instance["Shell_Home_PrivacyPause_Off"]);
        CurrentRecordingSourcesText.Text = currentRecording is null
            ? LocalizationManager.Instance["Shell_CurrentRecording_Sources_None"]
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Sources"], currentRecording.SourceSummary);
        CurrentRecordingOutputText.Text = currentRecording is null
            ? LocalizationManager.Instance["Shell_CurrentRecording_Output_None"]
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Output"], BuildAvailabilityLabel(currentRecording.OutputAvailability, currentRecording.OutputDeviceName));
        CurrentRecordingMicText.Text = currentRecording is null
            ? LocalizationManager.Instance["Shell_CurrentRecording_Mic_None"]
            : string.Format(LocalizationManager.Instance["Shell_CurrentRecording_Mic"], BuildAvailabilityLabel(currentRecording.MicrophoneAvailability, currentRecording.MicrophoneDeviceName));
        CurrentRecordingBlockingReasonText.Text = currentRecording?.BlockingReason ?? snapshot.ManualControl.BlockingReason ?? string.Empty;
        HomeRecordingsFolderText.Text = string.Format(LocalizationManager.Instance["Shell_Home_RecordingsFolder"], ResolvePath(snapshot.Settings.Storage.RecordingsFolder, snapshot.Paths.DefaultRecordingsDirectory));
        HomeTranscriptsFolderText.Text = string.Format(LocalizationManager.Instance["Shell_Home_TranscriptsFolder"], ResolvePath(snapshot.Settings.Storage.TranscriptsFolder, snapshot.Paths.DefaultTranscriptsDirectory));
        HomeApiKeyStatusText.Text = snapshot.Secrets.FireworksApiKey is null
            ? LocalizationManager.Instance["Shell_Home_ApiKey_NotConfigured"]
            : string.Format(LocalizationManager.Instance["Shell_Home_ApiKey_Configured"], MaskApiKey(snapshot.Secrets.FireworksApiKey));
        RefreshHomeMeetings(snapshot);
        SettingsDegradedBanner.Visibility = snapshot.Capability.State == HostCapabilityState.Degraded ? Visibility.Visible : Visibility.Collapsed;

        HomeStartForceRecordButton.IsEnabled = snapshot.ManualControl.CanStartForceRecord;
        HomePauseButton.IsEnabled = snapshot.ManualControl.CanPause;
        HomeResumeButton.IsEnabled = snapshot.ManualControl.CanResume;
        HomeStopButton.IsEnabled = snapshot.ManualControl.CanStop;
        HomeDiscardButton.IsEnabled = snapshot.ManualControl.CanDiscard;
        CurrentRecordingStartButton.IsEnabled = snapshot.ManualControl.CanStartForceRecord;
        CurrentRecordingPauseButton.IsEnabled = snapshot.ManualControl.CanPause;
        CurrentRecordingResumeButton.IsEnabled = snapshot.ManualControl.CanResume;
        CurrentRecordingStopButton.IsEnabled = snapshot.ManualControl.CanStop;
        CurrentRecordingDiscardButton.IsEnabled = snapshot.ManualControl.CanDiscard;
    }

    private void RefreshHomeMeetings(ShellConfigurationSnapshot snapshot)
    {
        var recentSessions = snapshot.RecentSessions
            .Take(_homeMeetingsVisibleCount)
            .Select(static session => new HomeMeetingSessionItem(session))
            .ToArray();

        _homeMeetings.Clear();
        foreach (var session in recentSessions)
        {
            _homeMeetings.Add(session);
        }

        HomeMeetingsEmptyText.Visibility = _homeMeetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeMeetingsStatusText.Text = _homeMeetings.Count == 0
            ? LocalizationManager.Instance["Shell_Home_RecentSessions_None"]
            : string.Format(LocalizationManager.Instance["Shell_Home_RecentSessions_Info"], _homeMeetings.Count, snapshot.RecentSessions.Count);
        HomeLoadMoreButton.IsEnabled = snapshot.RecentSessions.Count > _homeMeetingsVisibleCount;
        UpdateHomeMeetingActionButtons();
    }

    private void UpdateHomeMeetingActionButtons()
    {
        if (HomeMeetingsListBox.SelectedItem is not HomeMeetingSessionItem item)
        {
            HomeOpenFolderButton.IsEnabled = false;
            HomeOpenMarkdownButton.IsEnabled = false;
            HomeRetryTranscriptionButton.IsEnabled = false;
            return;
        }

        HomeOpenFolderButton.IsEnabled = item.CanOpenFolder;
        HomeOpenMarkdownButton.IsEnabled = item.CanOpenMarkdown;
        HomeRetryTranscriptionButton.IsEnabled = item.CanRetryTranscription;
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void InitializeStaticOptions()
    {
        if (GeneralLanguageComboBox.Items.Count == 0)
        {
            GeneralLanguageComboBox.ItemsSource = new[]
            {
                new OptionItem("ru", LocalizationManager.Instance["Option_Language_Russian"]),
                new OptionItem("en", LocalizationManager.Instance["Option_Language_English"])
            };
        }

        if (GeneralThemeComboBox.Items.Count == 0)
        {
            GeneralThemeComboBox.ItemsSource = new[]
            {
                new OptionItem("system", LocalizationManager.Instance["Option_Theme_System"]),
                new OptionItem("light", LocalizationManager.Instance["Option_Theme_Light"]),
                new OptionItem("dark", LocalizationManager.Instance["Option_Theme_Dark"])
            };
        }

        if (RecordingModeComboBox.Items.Count == 0)
        {
            RecordingModeComboBox.ItemsSource = new[]
            {
                new OptionItem("off", LocalizationManager.Instance["Option_RecordingMode_Off"]),
                new OptionItem("ask", LocalizationManager.Instance["Option_RecordingMode_Ask"]),
                new OptionItem("auto", LocalizationManager.Instance["Option_RecordingMode_Auto"])
            };
        }

        if (RecordingPresetComboBox.Items.Count == 0)
        {
            RecordingPresetComboBox.ItemsSource = new[]
            {
                new OptionItem("meetings", LocalizationManager.Instance["Option_Preset_Meetings"]),
                new OptionItem("meetings_mic", LocalizationManager.Instance["Option_Preset_MeetingsMic"]),
                new OptionItem("quick_notes", LocalizationManager.Instance["Option_Preset_QuickNotes"]),
                new OptionItem("custom", LocalizationManager.Instance["Option_Preset_Custom"])
            };
        }

        if (PrebufferComboBox.Items.Count == 0)
        {
            PrebufferComboBox.ItemsSource = new[]
            {
                new OptionItem("5", LocalizationManager.Instance["Option_Prebuffer_5s"]),
                new OptionItem("10", LocalizationManager.Instance["Option_Prebuffer_10s"]),
                new OptionItem("15", LocalizationManager.Instance["Option_Prebuffer_15s"]),
                new OptionItem("30", LocalizationManager.Instance["Option_Prebuffer_30s"])
            };
        }

        if (PrivacyPausePolicyComboBox.Items.Count == 0)
        {
            PrivacyPausePolicyComboBox.ItemsSource = new[]
            {
                new OptionItem("pause", LocalizationManager.Instance["Option_PrivacyPause_Pause"]),
                new OptionItem("finish", LocalizationManager.Instance["Option_PrivacyPause_Finish"])
            };
        }

        var policyOptions = new[]
        {
            new OptionItem("seamless_switch", LocalizationManager.Instance["Option_DevicePolicy_SeamlessSwitch"]),
            new OptionItem("end_and_start_new", LocalizationManager.Instance["Option_DevicePolicy_EndAndStartNew"]),
            new OptionItem("ask", LocalizationManager.Instance["Option_DevicePolicy_Ask"])
        };

        if (OutputChangePolicyComboBox.Items.Count == 0)
        {
            OutputChangePolicyComboBox.ItemsSource = policyOptions;
            MicChangePolicyComboBox.ItemsSource = policyOptions;
            ActiveRecordingPolicyComboBox.ItemsSource = policyOptions;
        }

        if (ApplicationsAutoDiscoveryComboBox.Items.Count == 0)
        {
            ApplicationsAutoDiscoveryComboBox.ItemsSource = new[]
            {
                new OptionItem("auto_add", LocalizationManager.Instance["Option_AutoDiscovery_AutoAdd"]),
                new OptionItem("ask_to_add", LocalizationManager.Instance["Option_AutoDiscovery_AskToAdd"]),
                new OptionItem("off", LocalizationManager.Instance["Option_AutoDiscovery_Off"])
            };
        }

        if (TempRetentionComboBox.Items.Count == 0)
        {
            TempRetentionComboBox.ItemsSource = new[]
            {
                new OptionItem("1d", LocalizationManager.Instance["Option_TempRetention_1Day"]),
                new OptionItem("3d", LocalizationManager.Instance["Option_TempRetention_3Days"]),
                new OptionItem("7d", LocalizationManager.Instance["Option_TempRetention_7Days"]),
                new OptionItem("14d", LocalizationManager.Instance["Option_TempRetention_14Days"]),
                new OptionItem("30d", LocalizationManager.Instance["Option_TempRetention_30Days"]),
                new OptionItem("never", LocalizationManager.Instance["Option_TempRetention_Never"])
            };
        }

        if (TranscriptionModelComboBox.Items.Count == 0)
        {
            TranscriptionModelComboBox.ItemsSource = new[]
            {
                new OptionItem("whisper-v3-turbo", "whisper-v3-turbo"),
                new OptionItem("whisper-v3", "whisper-v3"),
                new OptionItem("whisper-v2", "whisper-v2")
            };
        }

        if (TranscriptionLanguageComboBox.Items.Count == 0)
        {
            TranscriptionLanguageComboBox.ItemsSource = new[]
            {
                new OptionItem("auto", LocalizationManager.Instance["Option_RecordingMode_Auto"]),
                new OptionItem("ru", "ru"),
                new OptionItem("en", "en"),
                new OptionItem("de", "de"),
                new OptionItem("es", "es"),
                new OptionItem("fr", "fr"),
                new OptionItem("it", "it"),
                new OptionItem("ja", "ja"),
                new OptionItem("ko", "ko"),
                new OptionItem("pl", "pl"),
                new OptionItem("pt", "pt"),
                new OptionItem("uk", "uk"),
                new OptionItem("zh", "zh")
            };
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
    private void LoadGeneralSection(GeneralSettings general)
    {
        _baselineGeneral = general;
        GeneralAutostartCheckBox.IsChecked = general.Autostart;
        GeneralMinimizeCheckBox.IsChecked = general.MinimizeToTrayOnClose;
        GeneralNotificationsCheckBox.IsChecked = general.Notifications;
        SelectOption(GeneralLanguageComboBox, general.Language);
        SelectOption(GeneralThemeComboBox, general.AppTheme);
        ForceRecordHotkeyTextBox.Text = general.Hotkeys.ForceRecordToggle ?? string.Empty;
        PrivacyPauseHotkeyTextBox.Text = general.Hotkeys.PrivacyPauseToggle ?? string.Empty;
        DiscardHotkeyTextBox.Text = general.Hotkeys.DiscardCurrent ?? string.Empty;
        OpenMainWindowHotkeyTextBox.Text = general.Hotkeys.OpenMainWindow ?? string.Empty;
        GeneralValidationText.Text = string.Empty;
        App.ApplyTheme(general.AppTheme);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#degraded
    private void LoadRecordingSection(ShellConfigurationSnapshot snapshot)
    {
        var recording = snapshot.Settings.Recording;
        if (snapshot.Capability.State == HostCapabilityState.Degraded
            && recording.DefaultSourcesAuto.Contains("process_output", StringComparer.OrdinalIgnoreCase))
        {
            recording = recording with
            {
                DefaultSourcesAuto = recording.DefaultSourcesAuto
                    .Where(static source => !string.Equals(source, "process_output", StringComparison.OrdinalIgnoreCase))
                    .Append("device_loopback")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        _baselineRecording = recording;
        SelectOption(RecordingModeComboBox, recording.Mode);
        SelectOption(PrebufferComboBox, recording.PrebufferSeconds.ToString());
        SilenceThresholdTextBox.Text = recording.SilenceThresholdDbfs.ToString();
        StartDelayTextBox.Text = recording.StartDelaySeconds.ToString();
        StopDelayTextBox.Text = recording.StopDelaySeconds.ToString();
        MergeWindowTextBox.Text = recording.MergeWindowSeconds.ToString();
        SelectOption(PrivacyPausePolicyComboBox, recording.PrivacyPausePolicy);
        AutoSourceProcessOutputCheckBox.IsChecked = recording.DefaultSourcesAuto.Contains("process_output", StringComparer.OrdinalIgnoreCase);
        AutoSourceDeviceLoopbackCheckBox.IsChecked = recording.DefaultSourcesAuto.Contains("device_loopback", StringComparer.OrdinalIgnoreCase);
        AutoSourceMicCheckBox.IsChecked = recording.DefaultSourcesAuto.Contains("mic", StringComparer.OrdinalIgnoreCase);
        ForceSourceDeviceLoopbackCheckBox.IsChecked = recording.DefaultSourcesForce.Contains("device_loopback", StringComparer.OrdinalIgnoreCase);
        ForceSourceMicCheckBox.IsChecked = recording.DefaultSourcesForce.Contains("mic", StringComparer.OrdinalIgnoreCase);
        AutoSourceProcessOutputCheckBox.IsEnabled = snapshot.Capability.State != HostCapabilityState.Degraded;
        RecordingValidationText.Text = string.Empty;
        RecordingNoteText.Text = _recordingSaveMessage ?? string.Empty;
        DetectRecordingPreset();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices
    private void LoadDevicesSection(ShellConfigurationSnapshot snapshot)
    {
        LoadDeviceCombo(DevicesOutputComboBox, snapshot.Devices.RenderDevices.Select(device => new OptionItem(device.Id, device.IsDefault ? $"{device.FriendlyName} ({LocalizationManager.Instance["Common_SystemDefault"]})" : device.FriendlyName)));
        LoadDeviceCombo(DevicesMicComboBox, snapshot.Devices.CaptureDevices.Select(device => new OptionItem(device.Id, device.IsDefault ? $"{device.FriendlyName} ({LocalizationManager.Instance["Common_SystemDefault"]})" : device.FriendlyName)));

        _baselineDevices = snapshot.Settings.Devices;
        DevicesFollowOutputCheckBox.IsChecked = _baselineDevices.FollowSystemDefaultOutput;
        DevicesFollowMicCheckBox.IsChecked = _baselineDevices.FollowSystemDefaultMic;
        DevicesAutoDiscoverOutputCheckBox.IsChecked = _baselineDevices.AutoDiscoverOutput;
        DevicesAutoDiscoverMicCheckBox.IsChecked = _baselineDevices.AutoDiscoverMic;
        SelectOption(DevicesOutputComboBox, _baselineDevices.OutputDeviceId ?? string.Empty);
        SelectOption(DevicesMicComboBox, _baselineDevices.MicrophoneDeviceId ?? string.Empty);
        SelectOption(OutputChangePolicyComboBox, _baselineDevices.OutputChangePolicy);
        SelectOption(MicChangePolicyComboBox, _baselineDevices.MicChangePolicy);
        SelectOption(ActiveRecordingPolicyComboBox, _baselineDevices.ActiveRecordingDevicePolicy);
        ApplyDeviceSelectionState();
        DevicesWarningText.Text = string.Empty;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void LoadApplicationsSection(ShellConfigurationSnapshot snapshot)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
        _baselineAutoDiscoveryPolicy = snapshot.Settings.Applications.AutoDiscoveryPolicy;
        _baselineAppRules = snapshot.AppRules.Select(AppRuleRecord.Normalize).ToArray();
        _baselineIgnoredAppSuggestions = snapshot.Settings.Applications.IgnoredAppSuggestions.ToArray();
        _baselineUserExclusions = snapshot.Settings.Applications.Exclusions.ToArray();

        SelectOption(ApplicationsAutoDiscoveryComboBox, _baselineAutoDiscoveryPolicy);
        _whitelist.Clear();
        foreach (var rule in _baselineAppRules)
        {
            _whitelist.Add(new EditableAppRuleItem(rule.Id, rule.DisplayName, rule.ProcessName, rule.Enabled));
        }

        LoadKnownApplicationSuggestions();

        _ignoredAppSuggestions.Clear();
        foreach (var ignoredApp in _baselineIgnoredAppSuggestions)
        {
            _ignoredAppSuggestions.Add(new EditableStringItem(ignoredApp));
        }

        _exclusions.Clear();
        foreach (var builtIn in ApplicationDiscoveryDefaults.BuiltInExclusions)
        {
            _exclusions.Add(new EditableStringItem(builtIn, isBuiltIn: true));
        }

        foreach (var exclusion in _baselineUserExclusions)
        {
            _exclusions.Add(new EditableStringItem(exclusion));
        }

        ApplicationsValidationText.Text = string.Empty;
        UpdateApplicationsUiSummary();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void LoadKnownApplicationSuggestions()
    {
        foreach (var existing in _knownApplications)
        {
            existing.PropertyChanged -= OnKnownApplicationPropertyChanged;
        }

        _knownApplications.Clear();
        foreach (var rule in InstalledMeetingAppCatalog.GetInstalledSuggestions())
        {
            var item = new SelectableAppSuggestionItem(rule.DisplayName, rule.ProcessName, LocalizationManager.Instance["Shell_Settings_KnownApp_Installed"]);
            item.PropertyChanged += OnKnownApplicationPropertyChanged;
            _knownApplications.Add(item);
        }

        SyncKnownApplicationsSelection();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnKnownApplicationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingKnownApplications)
        {
            return;
        }

        if (e.PropertyName != nameof(SelectableAppSuggestionItem.IsSelected) || sender is not SelectableAppSuggestionItem item)
        {
            return;
        }

        if (item.IsSelected)
        {
            UpsertWhitelistRule(AppRuleRecord.Create(item.DisplayName, item.ProcessName));
        }
        else
        {
            RemoveWhitelistRuleByProcess(item.ProcessName);
        }

        UpdateApplicationsState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void SyncKnownApplicationsSelection()
    {
        _syncingKnownApplications = true;
        try
        {
            foreach (var app in _knownApplications)
            {
                var selected = _whitelist.Any(item => string.Equals(item.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
                app.IsSelected = selected;
                app.StatusLabel = selected
                    ? LocalizationManager.Instance["Shell_Settings_KnownApp_InstalledAndWhitelisted"]
                    : LocalizationManager.Instance["Shell_Settings_KnownApp_Installed"];
            }
        }
        finally
        {
            _syncingKnownApplications = false;
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void UpdateApplicationsUiSummary()
    {
        KnownApplicationsEmptyText.Visibility = _knownApplications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KnownApplicationsItemsControl.Visibility = _knownApplications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WhitelistSummaryText.Text = _whitelist.Count == 0
            ? LocalizationManager.Instance["Shell_Settings_Applications_Whitelist_Empty"]
            : string.Format(LocalizationManager.Instance["Shell_Settings_Applications_Whitelist_Summary"], _whitelist.Count);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void UpsertWhitelistRule(AppRuleRecord rule)
    {
        var normalized = AppRuleRecord.Normalize(rule);
        var existing = _whitelist.FirstOrDefault(item => string.Equals(item.ProcessName, normalized.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.DisplayName = normalized.DisplayName;
            existing.Enabled = normalized.Enabled;
        }
        else
        {
            _whitelist.Add(new EditableAppRuleItem(normalized.Id, normalized.DisplayName, normalized.ProcessName, normalized.Enabled));
        }

        var ignored = _ignoredAppSuggestions.FirstOrDefault(item => string.Equals(item.Value, normalized.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (ignored is not null)
        {
            _ignoredAppSuggestions.Remove(ignored);
        }

        SyncKnownApplicationsSelection();
        UpdateApplicationsUiSummary();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void RemoveWhitelistRuleByProcess(string processName)
    {
        var existing = _whitelist.FirstOrDefault(item => string.Equals(item.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _whitelist.Remove(existing);
        }

        SyncKnownApplicationsSelection();
        UpdateApplicationsUiSummary();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private void LoadStorageSection(ShellConfigurationSnapshot snapshot)
    {
        _baselineStorage = snapshot.Settings.Storage;
        StorageRecordingsFolderTextBox.Text = ResolvePath(_baselineStorage.RecordingsFolder, snapshot.Paths.DefaultRecordingsDirectory);
        StorageTranscriptsFolderTextBox.Text = ResolvePath(_baselineStorage.TranscriptsFolder, snapshot.Paths.DefaultTranscriptsDirectory);
        FailedTempFolderTextBox.Text = ResolvePath(_baselineStorage.FailedTempFolder, snapshot.Paths.TempDirectory);
        FilenameTemplateTextBox.Text = _baselineStorage.FilenameTemplate;
        KeepRawAfterSuccessCheckBox.IsChecked = _baselineStorage.KeepRawAfterSuccess;
        AudioCompressionCheckBox.IsChecked = _baselineStorage.AudioCompressionEnabled;
        SelectOption(TempRetentionComboBox, _baselineStorage.TempRetentionPeriod);
        StorageValidationText.Text = string.Empty;
        StorageNoteText.Text = _storageSaveMessage ?? string.Empty;
        UpdateFilenamePreview();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
    private void LoadTranscriptionSection(TranscriptionSettings transcription, AppSecrets secrets)
    {
        _baselineTranscription = transcription;
        _baselineApiKey = secrets.FireworksApiKey;
        _transcriptionApiKeyEditMode = false;
        _transcriptionApiKeyRemoved = false;
        _showTranscriptionApiKey = false;
        SavedApiKeyTextBlock.Text = secrets.FireworksApiKey is null ? LocalizationManager.Instance["Shell_Settings_Transcription_ApiKey_NotConfigured"] : MaskApiKey(secrets.FireworksApiKey);
        SavedApiKeyPanel.Visibility = secrets.FireworksApiKey is null ? Visibility.Collapsed : Visibility.Visible;
        EditableApiKeyPanel.Visibility = secrets.FireworksApiKey is null ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionApiKeyPasswordBox.Password = string.Empty;
        TranscriptionApiKeyTextBox.Text = string.Empty;
        TranscriptionApiKeyPasswordBox.Visibility = Visibility.Visible;
        TranscriptionApiKeyTextBox.Visibility = Visibility.Collapsed;
        TranscriptionApiKeyToggleText.Text = LocalizationManager.Instance["Shell_Settings_Transcription_Show"];

        SelectOption(TranscriptionModelComboBox, transcription.Model);
        DiarizationCheckBox.IsChecked = transcription.Diarization;
        MinSpeakersTextBox.Text = transcription.MinSpeakers.ToString();
        MaxSpeakersTextBox.Text = transcription.MaxSpeakers.ToString();
        SelectOption(TranscriptionLanguageComboBox, transcription.Language);
        AutoRetryCheckBox.IsChecked = transcription.AutoRetry;
        RetryCountTextBox.Text = transcription.RetryCount.ToString();
        ApplyTranscriptionToggleState();
        UpdateDiarizationVisibility();
        UpdateRetryVisibility();
        TranscriptionValidationText.Text = string.Empty;
    }

    private static string ResolvePath(string? value, string defaultPath) => string.IsNullOrWhiteSpace(value) ? defaultPath : value;

    private static string MaskApiKey(string apiKey)
    {
        if (apiKey.Length <= 4)
        {
            return "fw_••••";
        }

        var suffix = apiKey[^4..];
        return $"fw_••••••••••••{suffix}";
    }

    private static void LoadDeviceCombo(ComboBox comboBox, IEnumerable<OptionItem> options)
    {
        comboBox.ItemsSource = new List<OptionItem>([new OptionItem(string.Empty, LocalizationManager.Instance["Common_SystemDefault"]), .. options]);
    }

    private static void SelectOption(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault();
    }

    private static string SelectedOptionValue(ComboBox comboBox) =>
        comboBox.SelectedItem is OptionItem option
            ? option.Value
            : string.Empty;

    private void UpdateSaveButtons()
    {
        if (_snapshot is null)
        {
            return;
        }

        UpdateGeneralState();
        UpdateRecordingState();
        UpdateDevicesState();
        UpdateApplicationsState();
        UpdateStorageState();
        UpdateTranscriptionState();
    }

    private void ApplyDeviceSelectionState()
    {
        DevicesOutputComboBox.IsEnabled = DevicesFollowOutputCheckBox.IsChecked != true;
        DevicesMicComboBox.IsEnabled = DevicesFollowMicCheckBox.IsChecked != true;
    }

    private void ApplyTranscriptionToggleState()
    {
        var diarizationEnabled = DiarizationCheckBox.IsChecked == true;
        MinSpeakersTextBox.IsEnabled = diarizationEnabled;
        MaxSpeakersTextBox.IsEnabled = diarizationEnabled;
        RetryCountTextBox.IsEnabled = AutoRetryCheckBox.IsChecked == true;
    }

    private void UpdateFilenamePreview()
    {
        var template = FilenameTemplateTextBox.Text;
        var preview = template
            .Replace("YYYY-MM-DD", "2026-04-06", StringComparison.Ordinal)
            .Replace("HH-mm", "11-42", StringComparison.Ordinal)
            .Replace("{SourceApp}", "Zoom", StringComparison.Ordinal)
            .Replace("{SessionId}", "8b32f3a6d2f14d8d9f2206ed4f018a55", StringComparison.Ordinal)
            .Replace("{Date}", "2026-04-06", StringComparison.Ordinal)
            .Replace("{Time}", "11-42", StringComparison.Ordinal)
            .Replace("{Mode}", "ask", StringComparison.Ordinal);
        FilenamePreviewTextBlock.Text = string.Format(LocalizationManager.Instance["Shell_Settings_Storage_FilenamePreview"], preview);
    }

    private static string BuildAvailabilityLabel(RecordingSourceAvailability availability, string? deviceName) => availability switch
    {
        RecordingSourceAvailability.Active when !string.IsNullOrWhiteSpace(deviceName) => $"{deviceName} ({LocalizationManager.Instance["Shell_Device_Active"]})",
        RecordingSourceAvailability.Active => LocalizationManager.Instance["Shell_Device_Active_Short"],
        RecordingSourceAvailability.Unavailable when !string.IsNullOrWhiteSpace(deviceName) => $"{deviceName} ({LocalizationManager.Instance["Shell_Device_Unavailable"]})",
        RecordingSourceAvailability.Unavailable => LocalizationManager.Instance["Shell_Device_Unavailable_Short"],
        _ => LocalizationManager.Instance["Shell_Device_NotInSession"]
    };
}
