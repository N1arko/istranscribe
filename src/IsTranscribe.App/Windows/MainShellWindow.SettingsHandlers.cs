using System.IO;
using System.Windows;
using System.Windows.Controls;
using IsTranscribe.App.ManualControls;
using IsTranscribe.App.Configuration;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using ComboBox = System.Windows.Controls.ComboBox;
using IsTranscribe.App.Strings;
using MessageBox = System.Windows.MessageBox;

namespace IsTranscribe.App.Windows;

public partial class MainShellWindow
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
    private GeneralSettings ReadGeneralSettings() =>
        new(
            GeneralAutostartCheckBox.IsChecked == true,
            GeneralMinimizeCheckBox.IsChecked == true,
            GeneralNotificationsCheckBox.IsChecked == true,
            SelectedOptionValue(GeneralLanguageComboBox),
            SelectedOptionValue(GeneralThemeComboBox),
            new HotkeySettings(
                NormalizeHotkey(ForceRecordHotkeyTextBox.Text),
                NormalizeHotkey(PrivacyPauseHotkeyTextBox.Text),
                NormalizeHotkey(DiscardHotkeyTextBox.Text),
                NormalizeHotkey(OpenMainWindowHotkeyTextBox.Text)));

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording
    private RecordingSettings ReadRecordingSettings() =>
        new(
            SelectedOptionValue(RecordingModeComboBox),
            ParseRequiredInt(SelectedOptionValue(PrebufferComboBox)),
            ParseRequiredInt(SilenceThresholdTextBox.Text),
            ParseRequiredInt(StartDelayTextBox.Text),
            ParseRequiredInt(StopDelayTextBox.Text),
            ParseRequiredInt(MergeWindowTextBox.Text),
            SelectedOptionValue(PrivacyPausePolicyComboBox),
            ReadAutoSources(),
            ReadForceSources());

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices
    private DeviceSettings ReadDeviceSettings() =>
        new(
            DevicesFollowOutputCheckBox.IsChecked == true ? null : NormalizeNullable(SelectedOptionValue(DevicesOutputComboBox)),
            DevicesFollowMicCheckBox.IsChecked == true ? null : NormalizeNullable(SelectedOptionValue(DevicesMicComboBox)),
            DevicesFollowOutputCheckBox.IsChecked == true,
            DevicesFollowMicCheckBox.IsChecked == true,
            DevicesAutoDiscoverOutputCheckBox.IsChecked == true,
            DevicesAutoDiscoverMicCheckBox.IsChecked == true,
            SelectedOptionValue(OutputChangePolicyComboBox),
            SelectedOptionValue(MicChangePolicyComboBox),
            SelectedOptionValue(ActiveRecordingPolicyComboBox));

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private ApplicationSectionSaveRequest ReadApplicationsRequest() =>
        new(
            SelectedOptionValue(ApplicationsAutoDiscoveryComboBox),
            _ignoredAppSuggestions
                .Select(static item => item.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _exclusions
                .Where(static item => !item.IsBuiltIn)
                .Select(static item => item.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            _whitelist.Select(static item => item.ToRecord()).ToArray());

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private StorageSettings ReadStorageSettings()
    {
        var paths = _snapshot?.Paths ?? throw new InvalidOperationException("Shell snapshot is unavailable.");
        return new StorageSettings(
            NormalizeDefaultPath(StorageRecordingsFolderTextBox.Text, paths.DefaultRecordingsDirectory),
            NormalizeDefaultPath(StorageTranscriptsFolderTextBox.Text, paths.DefaultTranscriptsDirectory),
            NormalizeDefaultPath(FailedTempFolderTextBox.Text, paths.TempDirectory),
            FilenameTemplateTextBox.Text.Trim(),
            KeepRawAfterSuccessCheckBox.IsChecked == true,
            SelectedOptionValue(TempRetentionComboBox),
            AudioCompressionCheckBox.IsChecked == true);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
    private TranscriptionSectionSaveRequest ReadTranscriptionRequest()
    {
        var apiKey = _transcriptionApiKeyRemoved
            ? null
            : _transcriptionApiKeyEditMode
                ? NormalizeNullable(_showTranscriptionApiKey ? TranscriptionApiKeyTextBox.Text : TranscriptionApiKeyPasswordBox.Password)
                : _baselineApiKey;

        return new TranscriptionSectionSaveRequest(
            new TranscriptionSettings(
                SelectedOptionValue(TranscriptionModelComboBox),
                DiarizationCheckBox.IsChecked == true,
                ParseRequiredInt(MinSpeakersTextBox.Text),
                ParseRequiredInt(MaxSpeakersTextBox.Text),
                SelectedOptionValue(TranscriptionLanguageComboBox),
                AutoRetryCheckBox.IsChecked == true,
                ParseRequiredInt(RetryCountTextBox.Text)),
            apiKey,
            _transcriptionApiKeyRemoved);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
    private void UpdateGeneralState()
    {
        var current = ReadGeneralSettings();
        var dirty = current != _baselineGeneral;
        var validation = ValidateGeneral(current);
        GeneralValidationText.Text = validation;
        GeneralDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        GeneralSaveButton.IsEnabled = dirty && string.IsNullOrWhiteSpace(validation);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private void UpdateRecordingState()
    {
        var current = ReadRecordingSettings();
        var dirty = !RecordingEquals(current, _baselineRecording);
        var validation = ValidateRecording(current);
        RecordingValidationText.Text = validation;
        RecordingDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        RecordingSaveButton.IsEnabled = dirty && string.IsNullOrWhiteSpace(validation);
        RecordingNoteText.Text = BuildRecordingNote(dirty);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices
    private void UpdateDevicesState()
    {
        var current = ReadDeviceSettings();
        var dirty = current != _baselineDevices;
        DevicesDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        DevicesSaveButton.IsEnabled = dirty;

        var outputMissing = !current.FollowSystemDefaultOutput && !ComboContainsValue(DevicesOutputComboBox, current.OutputDeviceId);
        var micMissing = !current.FollowSystemDefaultMic && !ComboContainsValue(DevicesMicComboBox, current.MicrophoneDeviceId);
        DevicesWarningText.Text = outputMissing || micMissing
            ? LocalizationManager.Instance["Shell_Settings_Devices_UnavailableWarning"]
            : string.Empty;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void UpdateApplicationsState()
    {
        UpdateApplicationsUiSummary();
        var current = ReadApplicationsRequest();
        var dirty = IsApplicationsDirty(current);
        var validation = ValidateApplications(current);
        ApplicationsValidationText.Text = validation;
        ApplicationsDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        ApplicationsSaveButton.IsEnabled = dirty && string.IsNullOrWhiteSpace(validation);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private void UpdateStorageState()
    {
        var current = ReadStorageSettings();
        var dirty = current != _baselineStorage;
        var validation = ValidateStorage(current);
        StorageValidationText.Text = validation;
        StorageDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        StorageSaveButton.IsEnabled = dirty && string.IsNullOrWhiteSpace(validation);
        StorageNoteText.Text = BuildStorageNote(current, dirty);
        UpdateFilenamePreview();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
    private void UpdateTranscriptionState()
    {
        var current = ReadTranscriptionRequest();
        var dirty = IsTranscriptionDirty(current);
        var validation = ValidateTranscription(current);
        TranscriptionValidationText.Text = validation;
        TranscriptionDirtyText.Text = dirty ? LocalizationManager.Instance["Shell_Settings_Unsaved"] : string.Empty;
        TranscriptionSaveButton.IsEnabled = dirty && string.IsNullOrWhiteSpace(validation);
    }

    private string ValidateGeneral(GeneralSettings general)
    {
        if (string.IsNullOrWhiteSpace(general.Language))
        {
            return LocalizationManager.Instance["Validation_General_LanguageRequired"];
        }

        if (!GlobalHotkeyProbe.TryValidateRegistration(
                this,
                [
                    general.Hotkeys.ForceRecordToggle,
                    general.Hotkeys.PrivacyPauseToggle,
                    general.Hotkeys.DiscardCurrent,
                    general.Hotkeys.OpenMainWindow
                ],
                out var error))
        {
            return error;
        }

        return string.Empty;
    }

    private string ValidateRecording(RecordingSettings recording)
    {
        if (!new[] { 5, 10, 15, 30 }.Contains(recording.PrebufferSeconds))
        {
            return LocalizationManager.Instance["Validation_Recording_InvalidPrebuffer"];
        }

        if (recording.SilenceThresholdDbfs < -60 || recording.SilenceThresholdDbfs > -20)
        {
            return LocalizationManager.Instance["Validation_Recording_InvalidSilenceThreshold"];
        }

        if (recording.StartDelaySeconds < 1 || recording.StartDelaySeconds > 10)
        {
            return LocalizationManager.Instance["Validation_Recording_InvalidStartDelay"];
        }

        if (recording.StopDelaySeconds < 5 || recording.StopDelaySeconds > 120)
        {
            return LocalizationManager.Instance["Validation_Recording_InvalidStopDelay"];
        }

        if (recording.MergeWindowSeconds < 0 || recording.MergeWindowSeconds > 300)
        {
            return LocalizationManager.Instance["Validation_Recording_InvalidMergeWindow"];
        }

        if (recording.DefaultSourcesAuto.Length == 0 || recording.DefaultSourcesForce.Length == 0)
        {
            return LocalizationManager.Instance["Validation_Recording_NoSources"];
        }

        if (_snapshot?.Capability.State == HostCapabilityState.Degraded
            && recording.DefaultSourcesAuto.Contains("process_output", StringComparer.OrdinalIgnoreCase))
        {
            return LocalizationManager.Instance["Validation_Recording_ProcessOutputUnavailable"];
        }

        return string.Empty;
    }

    private static string ValidateApplications(ApplicationSectionSaveRequest request)
    {
        var duplicates = request.AppRules
            .GroupBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        return duplicates is null ? string.Empty : string.Format(LocalizationManager.Instance["Validation_Applications_DuplicateRule"], duplicates.Key);
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private string ValidateStorage(StorageSettings storage)
    {
        if (!storage.FilenameTemplate.Contains("{SessionId}", StringComparison.Ordinal))
        {
            return LocalizationManager.Instance["Validation_Storage_MissingSessionId"];
        }

        var paths = _snapshot?.Paths ?? throw new InvalidOperationException("Shell snapshot is unavailable.");
        var recordingsError = ValidateWritablePathForUi(ResolvePath(storage.RecordingsFolder, paths.DefaultRecordingsDirectory), "Recordings folder");
        if (!string.IsNullOrWhiteSpace(recordingsError))
        {
            return recordingsError;
        }

        var transcriptsError = ValidateWritablePathForUi(ResolvePath(storage.TranscriptsFolder, paths.DefaultTranscriptsDirectory), "Transcripts folder");
        if (!string.IsNullOrWhiteSpace(transcriptsError))
        {
            return transcriptsError;
        }

        var tempError = ValidateWritablePathForUi(ResolvePath(storage.FailedTempFolder, paths.TempDirectory), "Failed/temp folder");
        if (!string.IsNullOrWhiteSpace(tempError))
        {
            return tempError;
        }

        return string.Empty;
    }

    private static string ValidateTranscription(TranscriptionSectionSaveRequest request)
    {
        if (request.RemoveApiKey)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(request.FireworksApiKey))
        {
            return LocalizationManager.Instance["Validation_Transcription_ApiKeyRequired"];
        }

        if (request.Settings.MinSpeakers < 1 || request.Settings.MinSpeakers > 20 || request.Settings.MaxSpeakers < 1 || request.Settings.MaxSpeakers > 20)
        {
            return LocalizationManager.Instance["Validation_Transcription_InvalidSpeakerLimits"];
        }

        if (request.Settings.MinSpeakers > request.Settings.MaxSpeakers)
        {
            return LocalizationManager.Instance["Validation_Transcription_MinExceedsMax"];
        }

        if (request.Settings.RetryCount < 1 || request.Settings.RetryCount > 10)
        {
            return LocalizationManager.Instance["Validation_Transcription_InvalidRetryCount"];
        }

        return string.Empty;
    }

    private static bool ComboContainsValue(ComboBox comboBox, string? value) =>
        string.IsNullOrWhiteSpace(value) || comboBox.Items.Cast<OptionItem>().Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));

    private static bool RecordingEquals(RecordingSettings left, RecordingSettings right) =>
        left.Mode == right.Mode
        && left.PrebufferSeconds == right.PrebufferSeconds
        && left.SilenceThresholdDbfs == right.SilenceThresholdDbfs
        && left.StartDelaySeconds == right.StartDelaySeconds
        && left.StopDelaySeconds == right.StopDelaySeconds
        && left.MergeWindowSeconds == right.MergeWindowSeconds
        && left.PrivacyPausePolicy == right.PrivacyPausePolicy
        && SequenceEqual(left.DefaultSourcesAuto, right.DefaultSourcesAuto)
        && SequenceEqual(left.DefaultSourcesForce, right.DefaultSourcesForce);

    private static bool SequenceEqual<T>(IEnumerable<T> left, IEnumerable<T> right) where T : notnull =>
        left.SequenceEqual(right);

    private static int ParseRequiredInt(string value) =>
        int.TryParse(value, out var result) ? result : 0;

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeHotkey(string value) => NormalizeNullable(value);

    private static string? NormalizeDefaultPath(string value, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Path.GetFullPath(value.Trim());
        return string.Equals(normalized, Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private string[] ReadAutoSources()
    {
        var sources = new List<string>();
        if (AutoSourceProcessOutputCheckBox.IsChecked == true)
        {
            sources.Add("process_output");
        }

        if (AutoSourceDeviceLoopbackCheckBox.IsChecked == true)
        {
            sources.Add("device_loopback");
        }

        if (AutoSourceMicCheckBox.IsChecked == true)
        {
            sources.Add("mic");
        }

        return sources.ToArray();
    }

    private string[] ReadForceSources()
    {
        var sources = new List<string>();
        if (ForceSourceDeviceLoopbackCheckBox.IsChecked == true)
        {
            sources.Add("device_loopback");
        }

        if (ForceSourceMicCheckBox.IsChecked == true)
        {
            sources.Add("mic");
        }

        return sources.ToArray();
    }

    private async Task RefreshFromHostAsync()
    {
        if (RefreshSnapshotAsync is null)
        {
            return;
        }

        HomeMeetingsStatusText.Text = LocalizationManager.Instance["Shell_Home_RecentSessions_Loading"];
        try
        {
            LoadSnapshot(await RefreshSnapshotAsync());
        }
        catch (Exception exception)
        {
            _homeMeetings.Clear();
            HomeMeetingsEmptyText.Visibility = Visibility.Collapsed;
            HomeMeetingsStatusText.Text = string.Format(LocalizationManager.Instance["Shell_Home_RecentSessions_Error"], exception.Message);
            HomeOpenFolderButton.IsEnabled = false;
            HomeOpenMarkdownButton.IsEnabled = false;
            HomeRetryTranscriptionButton.IsEnabled = false;
            HomeLoadMoreButton.IsEnabled = false;
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.rules
    private async Task<bool> EnsureSectionCanChangeAsync(string fromSection)
    {
        var dirty = fromSection switch
        {
            "General" => ReadGeneralSettings() != _baselineGeneral,
            "Recording" => !RecordingEquals(ReadRecordingSettings(), _baselineRecording),
            "Devices" => ReadDeviceSettings() != _baselineDevices,
            "Applications" => IsApplicationsDirty(ReadApplicationsRequest()),
            "Storage" => ReadStorageSettings() != _baselineStorage,
            "Transcription" => IsTranscriptionDirty(ReadTranscriptionRequest()),
            _ => false
        };

        if (!dirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            LocalizationManager.Instance["Dialog_UnsavedChanges_Message"],
            LocalizationManager.Instance["Dialog_UnsavedChanges_Title"],
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            LoadSnapshot(_snapshot!);
            return true;
        }

        return await SaveSectionByNameAsync(fromSection);
    }

    private async Task<bool> SaveSectionByNameAsync(string sectionName)
    {
        return sectionName switch
        {
            "General" => await SaveGeneralSectionAsync(),
            "Recording" => await SaveRecordingSectionAsync(),
            "Devices" => await SaveDevicesSectionAsync(),
            "Applications" => await SaveApplicationsSectionAsync(),
            "Storage" => await SaveStorageSectionAsync(),
            "Transcription" => await SaveTranscriptionSectionAsync(),
            _ => true
        };
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
    private async Task<bool> SaveGeneralSectionAsync()
    {
        if (SaveGeneralAsync is null)
        {
            return false;
        }

        var current = ReadGeneralSettings();
        var validation = ValidateGeneral(current);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            GeneralValidationText.Text = validation;
            return false;
        }

        var result = await SaveGeneralAsync(current);
        if (!result.Success)
        {
            GeneralValidationText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_General"];
            return false;
        }

        await RefreshFromHostAsync();
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.recording
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private async Task<bool> SaveRecordingSectionAsync()
    {
        if (SaveRecordingAsync is null)
        {
            return false;
        }

        var current = ReadRecordingSettings();
        var validation = ValidateRecording(current);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            RecordingValidationText.Text = validation;
            return false;
        }

        var result = await SaveRecordingAsync(current);
        if (!result.Success)
        {
            RecordingValidationText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_Recording"];
            return false;
        }

        _recordingSaveMessage = result.Message;
        await RefreshFromHostAsync();
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.devices
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private async Task<bool> SaveDevicesSectionAsync()
    {
        if (SaveDevicesAsync is null)
        {
            return false;
        }

        var result = await SaveDevicesAsync(ReadDeviceSettings());
        if (!result.Success)
        {
            DevicesWarningText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_Devices"];
            return false;
        }

        await RefreshFromHostAsync();
        DevicesWarningText.Text = result.Message ?? DevicesWarningText.Text;
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private async Task<bool> SaveApplicationsSectionAsync()
    {
        if (SaveApplicationsAsync is null)
        {
            return false;
        }

        var request = ReadApplicationsRequest();
        var validation = ValidateApplications(request);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            ApplicationsValidationText.Text = validation;
            return false;
        }

        var result = await SaveApplicationsAsync(request);
        if (!result.Success)
        {
            ApplicationsValidationText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_Applications"];
            return false;
        }

        await RefreshFromHostAsync();
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private async Task<bool> SaveStorageSectionAsync()
    {
        if (SaveStorageAsync is null)
        {
            return false;
        }

        var current = ReadStorageSettings();
        var validation = ValidateStorage(current);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            StorageValidationText.Text = validation;
            return false;
        }

        var result = await SaveStorageAsync(current);
        if (!result.Success)
        {
            StorageValidationText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_Storage"];
            return false;
        }

        _storageSaveMessage = result.Message;
        await RefreshFromHostAsync();
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private async Task<bool> SaveTranscriptionSectionAsync()
    {
        if (SaveTranscriptionAsync is null)
        {
            return false;
        }

        var request = ReadTranscriptionRequest();
        var validation = ValidateTranscription(request);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            TranscriptionValidationText.Text = validation;
            return false;
        }

        var result = await SaveTranscriptionAsync(request);
        if (!result.Success)
        {
            TranscriptionValidationText.Text = result.Message ?? LocalizationManager.Instance["Dialog_SaveError_Transcription"];
            return false;
        }

        await RefreshFromHostAsync();
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private static string? ValidateWritablePathForUi(string path, string fieldLabel)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return null;
        }
        catch (Exception exception)
        {
            return string.Format(LocalizationManager.Instance["Validation_Storage_NotWritable"], fieldLabel, exception.Message);
        }
    }

    private bool IsApplicationsDirty(ApplicationSectionSaveRequest current) =>
        current.AutoDiscoveryPolicy != _baselineAutoDiscoveryPolicy
        || !SequenceEqual(current.IgnoredAppSuggestions, _baselineIgnoredAppSuggestions)
        || !SequenceEqual(current.Exclusions, _baselineUserExclusions)
        || !SequenceEqual(current.AppRules, _baselineAppRules);

    private bool IsTranscriptionDirty(TranscriptionSectionSaveRequest current) =>
        current.Settings != _baselineTranscription
        || !string.Equals(current.FireworksApiKey, _baselineApiKey, StringComparison.Ordinal)
        || current.RemoveApiKey;

    private bool IsRuntimeRecordingActive() =>
        _snapshot?.ManualControl.RecordingState is RecordingActivityState.Recording or RecordingActivityState.Paused;

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.apply
    private string BuildRecordingNote(bool dirty)
    {
        if (dirty && IsRuntimeRecordingActive())
        {
            return LocalizationManager.Instance["Shell_Settings_Recording_NoteNextSession"];
        }

        return _recordingSaveMessage ?? string.Empty;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.storage
    private string BuildStorageNote(StorageSettings current, bool dirty)
    {
        var notes = new List<string>();
        if (dirty && StoragePathsChanged(current))
        {
            notes.Add(LocalizationManager.Instance["Shell_Settings_Storage_NotePathsChange"]);
        }

        if (!string.IsNullOrWhiteSpace(_storageSaveMessage))
        {
            notes.Add(_storageSaveMessage!);
        }

        return string.Join(" ", notes);
    }

    private bool StoragePathsChanged(StorageSettings current) =>
        !string.Equals(current.RecordingsFolder, _baselineStorage.RecordingsFolder, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.TranscriptsFolder, _baselineStorage.TranscriptsFolder, StringComparison.OrdinalIgnoreCase);

    // @spec spec://modules/app/FEAT-009-settings-ux-simplification#behavior.presets
    private void ApplyRecordingPreset()
    {
        var preset = SelectedOptionValue(RecordingPresetComboBox);
        if (preset == "custom")
        {
            return;
        }

        _isLoadingSnapshot = true;
        try
        {
            switch (preset)
            {
                case "meetings":
                    SelectOption(PrebufferComboBox, "5");
                    SilenceThresholdTextBox.Text = "-40";
                    StartDelayTextBox.Text = "2";
                    StopDelayTextBox.Text = "8";
                    MergeWindowTextBox.Text = "30";
                    AutoSourceProcessOutputCheckBox.IsChecked = true;
                    AutoSourceDeviceLoopbackCheckBox.IsChecked = true;
                    AutoSourceMicCheckBox.IsChecked = false;
                    break;
                case "meetings_mic":
                    SelectOption(PrebufferComboBox, "5");
                    SilenceThresholdTextBox.Text = "-40";
                    StartDelayTextBox.Text = "2";
                    StopDelayTextBox.Text = "8";
                    MergeWindowTextBox.Text = "30";
                    AutoSourceProcessOutputCheckBox.IsChecked = true;
                    AutoSourceDeviceLoopbackCheckBox.IsChecked = true;
                    AutoSourceMicCheckBox.IsChecked = true;
                    break;
                case "quick_notes":
                    SelectOption(PrebufferComboBox, "5");
                    SilenceThresholdTextBox.Text = "-35";
                    StartDelayTextBox.Text = "0";
                    StopDelayTextBox.Text = "3";
                    MergeWindowTextBox.Text = "10";
                    AutoSourceProcessOutputCheckBox.IsChecked = false;
                    AutoSourceDeviceLoopbackCheckBox.IsChecked = false;
                    AutoSourceMicCheckBox.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _isLoadingSnapshot = false;
        }
    }

    private void DetectRecordingPreset()
    {
        var prebuffer = SelectedOptionValue(PrebufferComboBox);
        var silence = SilenceThresholdTextBox.Text.Trim();
        var startDelay = StartDelayTextBox.Text.Trim();
        var stopDelay = StopDelayTextBox.Text.Trim();
        var merge = MergeWindowTextBox.Text.Trim();
        var processOutput = AutoSourceProcessOutputCheckBox.IsChecked == true;
        var loopback = AutoSourceDeviceLoopbackCheckBox.IsChecked == true;
        var mic = AutoSourceMicCheckBox.IsChecked == true;

        string detected;
        if (prebuffer == "5" && silence == "-40" && startDelay == "2" && stopDelay == "8" && merge == "30"
            && processOutput && loopback && !mic)
        {
            detected = "meetings";
        }
        else if (prebuffer == "5" && silence == "-40" && startDelay == "2" && stopDelay == "8" && merge == "30"
            && processOutput && loopback && mic)
        {
            detected = "meetings_mic";
        }
        else if (silence == "-35" && startDelay == "0" && stopDelay == "3" && merge == "10"
            && !processOutput && !loopback && mic)
        {
            detected = "quick_notes";
        }
        else
        {
            detected = "custom";
        }

        _isLoadingSnapshot = true;
        try
        {
            SelectOption(RecordingPresetComboBox, detected);
        }
        finally
        {
            _isLoadingSnapshot = false;
        }
    }

    private void UpdateDiarizationVisibility()
    {
        DiarizationSpeakersPanel.Visibility = DiarizationCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateRetryVisibility()
    {
        RetryCountPanel.Visibility = AutoRetryCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
