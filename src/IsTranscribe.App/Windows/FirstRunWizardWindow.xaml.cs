using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IsTranscribe.App.Configuration;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Persistence;
using ComboBox = System.Windows.Controls.ComboBox;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using IsTranscribe.App.Strings;
using MessageBox = System.Windows.MessageBox;

namespace IsTranscribe.App.Windows;

 public partial class FirstRunWizardWindow : Wpf.Ui.Controls.FluentWindow
 {
     private const int LastStepIndex = 5;
     private const string FireworksDocsUrl = "https://docs.fireworks.ai/api-reference/audio-transcriptions";
 
     private readonly EditableCollection<SelectableAppSuggestionItem> _knownApplications = [];
     private readonly EditableCollection<EditableAppRuleItem> _selectedApplications = [];
     private ShellConfigurationSnapshot? _snapshot;
     private bool _initialized;
     private bool _apiKeyTestInFlight;
     private bool _showApiKey;
     private bool _syncingApiKey;
     private bool _syncingKnownApplications;
     private int _currentStepIndex;
 
     public FirstRunWizardWindow()
     {
         InitializeComponent();
         KnownApplicationsItemsControl.ItemsSource = _knownApplications;
         SelectedApplicationsListBox.ItemsSource = _selectedApplications;
         InitializeStaticOptions();
         ApiKeyDocsLink.Inlines.Clear();
         ApiKeyDocsLink.Inlines.Add(new System.Windows.Documents.Run(LocalizationManager.Instance["Wizard_Step3_ApiKey_DocsLink"]));
         LocalizationManager.Instance.LanguageChanged += (_, _) => Dispatcher.Invoke(() =>
         {
             InitializeStaticOptions();
             ApiKeyDocsLink.Inlines.Clear();
             ApiKeyDocsLink.Inlines.Add(new System.Windows.Documents.Run(LocalizationManager.Instance["Wizard_Step3_ApiKey_DocsLink"]));
             UpdateStepState();
         });
         UpdateDeviceSelectionMode();
         UpdateApplicationsStepState();
         UpdateStepState();
     }

    public bool AllowClose { get; set; }

    public Func<WizardCompletionRequest, Task<WindowOperationResult>>? CompleteAsync { get; set; }

    public event EventHandler? CloseRequested;

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard
     public void LoadSnapshot(ShellConfigurationSnapshot snapshot)
     {
         _snapshot = snapshot;
         ApplyCapabilityState(snapshot.Capability);
         LoadDeviceOptions(snapshot);

        if (!_initialized)
         {
             SeedInitialValues(snapshot);
             _initialized = true;
         }
         else
         {
             LoadKnownApplicationSuggestions();
         }
 
         UpdateDeviceSelectionMode();
         UpdateWarnings();
         UpdateApplicationsStepState();
         UpdateStepState();
     }

     private void SeedInitialValues(ShellConfigurationSnapshot snapshot)
     {
         // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps
         SelectOption(LanguageComboBox, snapshot.Settings.General.Language);
        RecordingsFolderTextBox.Text = snapshot.Settings.Storage.RecordingsFolder ?? snapshot.Paths.DefaultRecordingsDirectory;
        TranscriptsFolderTextBox.Text = snapshot.Settings.Storage.TranscriptsFolder ?? snapshot.Paths.DefaultTranscriptsDirectory;

        AutomaticDeviceSelectionCheckBox.IsChecked = snapshot.Settings.Devices.AutoDiscoverOutput && snapshot.Settings.Devices.AutoDiscoverMic;
        ApplyDeviceSelection(OutputDeviceComboBox, snapshot.Settings.Devices.FollowSystemDefaultOutput, snapshot.Settings.Devices.OutputDeviceId);
        ApplyDeviceSelection(MicrophoneComboBox, snapshot.Settings.Devices.FollowSystemDefaultMic, snapshot.Settings.Devices.MicrophoneDeviceId);

        var apiKey = snapshot.Secrets.FireworksApiKey ?? string.Empty;
        ApiKeyPasswordBox.Password = apiKey;
        ApiKeyTextBox.Text = apiKey;
        SetApiKeyTestStatus(string.Empty);
        SuggestAppsAutomaticallyCheckBox.IsChecked = !string.Equals(snapshot.Settings.Applications.AutoDiscoveryPolicy, "off", StringComparison.OrdinalIgnoreCase);

         switch (snapshot.Settings.Recording.Mode)
         {
             case "off":
                 ModeOffRadio.IsChecked = true;
                break;
            case "auto":
                ModeAutoRadio.IsChecked = true;
                break;
             default:
                 ModeAskRadio.IsChecked = true;
                 break;
         }

         SeedSelectedApplications(snapshot);
         LoadKnownApplicationSuggestions();
     }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#degraded
    private void ApplyCapabilityState(HostCapabilitySnapshot capability)
    {
        var degraded = capability.State == HostCapabilityState.Degraded;
        DegradedBanner.Visibility = degraded ? Visibility.Visible : Visibility.Collapsed;
        CapabilityBannerText.Text = degraded
            ? LocalizationManager.Instance["Wizard_DegradedBanner_Degraded"]
            : LocalizationManager.Instance["Wizard_DegradedBanner_Full"];
        AutoModeDescriptionText.Text = degraded
            ? LocalizationManager.Instance["Wizard_Warning_AutoCaptureDegraded"]
            : LocalizationManager.Instance["Wizard_Step4_Auto_Desc_Full"];
    }

    private void LoadDeviceOptions(ShellConfigurationSnapshot snapshot)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.devices
        LoadDeviceOptionsForCombo(
            OutputDeviceComboBox,
            snapshot.Devices.RenderDevices.Select(device => new OptionItem(device.Id, device.IsDefault ? device.FriendlyName + " (" + LocalizationManager.Instance["Common_SystemDefault"] + ")" : device.FriendlyName)),
            LocalizationManager.Instance["Wizard_Step2_SystemDefaultFollow"]);
        LoadDeviceOptionsForCombo(
            MicrophoneComboBox,
            snapshot.Devices.CaptureDevices.Select(device => new OptionItem(device.Id, device.IsDefault ? device.FriendlyName + " (" + LocalizationManager.Instance["Common_SystemDefault"] + ")" : device.FriendlyName)),
            LocalizationManager.Instance["Wizard_Step2_SystemDefaultFollow"]);

        DevicesAvailabilityText.Text = snapshot.Devices.RenderDevices.Count == 0 || snapshot.Devices.CaptureDevices.Count == 0
            ? LocalizationManager.Instance["Wizard_Step2_DevicesUnavailable"]
            : LocalizationManager.Instance["Wizard_Step2_DevicesAvailable"];

        if (_initialized)
        {
            ApplyDeviceSelection(OutputDeviceComboBox, GetSelectedOptionValue(OutputDeviceComboBox) is null, GetSelectedOptionValue(OutputDeviceComboBox));
            ApplyDeviceSelection(MicrophoneComboBox, GetSelectedOptionValue(MicrophoneComboBox) is null, GetSelectedOptionValue(MicrophoneComboBox));
        }
    }

     private void InitializeStaticOptions()
     {
         LanguageComboBox.ItemsSource = new[]
         {
            new OptionItem("ru", LocalizationManager.Instance["Option_Language_Russian"]),
            new OptionItem("en", LocalizationManager.Instance["Option_Language_English"])
         };
         LanguageComboBox.SelectedIndex = 0;
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void SeedSelectedApplications(ShellConfigurationSnapshot snapshot)
     {
         _selectedApplications.Clear();
         foreach (var rule in WizardApplicationStepModel.CreateInitialSelectedApplications(
                      snapshot.AppRules,
                      InstalledMeetingAppCatalog.GetInstalledSuggestions()))
         {
             _selectedApplications.Add(new EditableAppRuleItem(rule.Id, rule.DisplayName, rule.ProcessName, rule.Enabled));
         }

         UpdateApplicationsStepState();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void LoadKnownApplicationSuggestions()
     {
         foreach (var existing in _knownApplications)
         {
             existing.PropertyChanged -= OnKnownApplicationPropertyChanged;
         }

         _knownApplications.Clear();
         foreach (var rule in InstalledMeetingAppCatalog.GetInstalledSuggestions())
         {
             var item = new SelectableAppSuggestionItem(rule.DisplayName, rule.ProcessName, LocalizationManager.Instance["Wizard_KnownApp_Recommended"]);
             item.PropertyChanged += OnKnownApplicationPropertyChanged;
             _knownApplications.Add(item);
         }

         SyncKnownApplicationSelection();
         UpdateApplicationsStepState();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void OnKnownApplicationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
     {
         if (_syncingKnownApplications
             || e.PropertyName != nameof(SelectableAppSuggestionItem.IsSelected)
             || sender is not SelectableAppSuggestionItem item)
         {
             return;
         }

         if (item.IsSelected)
         {
             UpsertSelectedApplication(AppRuleRecord.Create(item.DisplayName, item.ProcessName));
         }
         else
         {
             RemoveSelectedApplicationByProcess(item.ProcessName);
         }

         UpdateApplicationsStepState();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void SyncKnownApplicationSelection()
     {
         _syncingKnownApplications = true;
         try
         {
             foreach (var app in _knownApplications)
             {
                 var selected = _selectedApplications.Any(item => string.Equals(item.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
                 app.IsSelected = selected;
                 app.StatusLabel = selected
                     ? LocalizationManager.Instance["Wizard_KnownApp_RecommendedAndIncluded"]
                     : LocalizationManager.Instance["Wizard_KnownApp_Recommended"];
             }
         }
         finally
         {
             _syncingKnownApplications = false;
         }
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void UpsertSelectedApplication(AppRuleRecord rule)
     {
         var normalized = AppRuleRecord.Normalize(rule);
         var existing = _selectedApplications.FirstOrDefault(item => string.Equals(item.ProcessName, normalized.ProcessName, StringComparison.OrdinalIgnoreCase));
         if (existing is not null)
         {
             existing.DisplayName = normalized.DisplayName;
             existing.Enabled = normalized.Enabled;
             SyncKnownApplicationSelection();
             return;
         }

         _selectedApplications.Add(new EditableAppRuleItem(normalized.Id, normalized.DisplayName, normalized.ProcessName, normalized.Enabled));
         SyncKnownApplicationSelection();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void RemoveSelectedApplicationByProcess(string processName)
     {
         var existing = _selectedApplications.FirstOrDefault(item => string.Equals(item.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
         if (existing is not null)
         {
             _selectedApplications.Remove(existing);
         }

         SyncKnownApplicationSelection();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void UpdateApplicationsStepState()
     {
         KnownApplicationsEmptyText.Visibility = _knownApplications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
         KnownApplicationsItemsControl.Visibility = _knownApplications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
         SelectedApplicationsEmptyText.Visibility = _selectedApplications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
         SelectedApplicationsListBox.Visibility = _selectedApplications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
         RemoveSelectedApplicationButton.IsEnabled = SelectedApplicationsListBox.SelectedItem is EditableAppRuleItem;
         ApplicationsSummaryText.Text = WizardApplicationStepModel.BuildSelectionSummary(_selectedApplications.Count);
     }

    private static void LoadDeviceOptionsForCombo(ComboBox comboBox, IEnumerable<OptionItem> items, string defaultLabel)
    {
        var previousValue = GetSelectedOptionValue(comboBox);
        comboBox.ItemsSource = new List<OptionItem>([new OptionItem(string.Empty, defaultLabel), .. items]);
        comboBox.SelectedItem = comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, previousValue ?? string.Empty, StringComparison.Ordinal))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault(static item => string.IsNullOrEmpty(item.Value));
        comboBox.SelectedIndex = comboBox.SelectedIndex >= 0 ? comboBox.SelectedIndex : 0;
    }

    private static string? GetSelectedOptionValue(ComboBox comboBox) =>
        comboBox.SelectedItem is OptionItem option && !string.IsNullOrWhiteSpace(option.Value)
            ? option.Value
            : null;

    private static void SelectOption(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault();
    }

    private static void ApplyDeviceSelection(ComboBox comboBox, bool followSystemDefault, string? deviceId)
    {
        var selectedValue = followSystemDefault ? string.Empty : deviceId ?? string.Empty;
        comboBox.SelectedItem = comboBox.Items.Cast<OptionItem>().FirstOrDefault(item => string.Equals(item.Value, selectedValue, StringComparison.Ordinal))
            ?? comboBox.Items.Cast<OptionItem>().FirstOrDefault(static item => string.IsNullOrEmpty(item.Value));
    }

    private void UpdateDeviceSelectionMode()
    {
        var automatic = AutomaticDeviceSelectionCheckBox.IsChecked == true;
        OutputDeviceComboBox.IsEnabled = !automatic;
        MicrophoneComboBox.IsEnabled = !automatic;
        DevicesModeDescriptionText.Text = automatic
            ? LocalizationManager.Instance["Wizard_Step2_DeviceMode_Automatic"]
            : LocalizationManager.Instance["Wizard_Step2_DeviceMode_Manual"];
    }

    private void UpdateStepState()
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.navigation
        StepCounterText.Text = string.Format(LocalizationManager.Instance["Wizard_StepCounter"], _currentStepIndex + 1, LastStepIndex + 1);
        StepTitleText.Text = _currentStepIndex switch
        {
            0 => LocalizationManager.Instance["Wizard_Step0_Title"],
            1 => LocalizationManager.Instance["Wizard_Step1_Title"],
            2 => LocalizationManager.Instance["Wizard_Step2_Title"],
            3 => LocalizationManager.Instance["Wizard_Step3_Title"],
            4 => LocalizationManager.Instance["Wizard_Step4_Title"],
            _ => LocalizationManager.Instance["Wizard_Step5_Title"]
        };

        StepDescriptionText.Text = _currentStepIndex switch
        {
            0 => LocalizationManager.Instance["Wizard_Step0_Desc"],
            1 => LocalizationManager.Instance["Wizard_Step1_Desc"],
            2 => LocalizationManager.Instance["Wizard_Step2_Desc"],
            3 => LocalizationManager.Instance["Wizard_Step3_Desc"],
            4 => LocalizationManager.Instance["Wizard_Step4_Desc"],
            _ => LocalizationManager.Instance["Wizard_Step5_Desc"]
        };

        LanguageStepPanel.Visibility = _currentStepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageStepPanel.Visibility = _currentStepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        DevicesStepPanel.Visibility = _currentStepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyStepPanel.Visibility = _currentStepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        RecordingModeStepPanel.Visibility = _currentStepIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsStepPanel.Visibility = _currentStepIndex == 5 ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = (System.Windows.Media.Brush)FindResource("AppSidebarTextBrush");
        var inactiveBrush = (System.Windows.Media.Brush)FindResource("AppSidebarTextMutedBrush");
        StepOneLabel.Foreground = _currentStepIndex == 0 ? activeBrush : inactiveBrush;
        StepTwoLabel.Foreground = _currentStepIndex == 1 ? activeBrush : inactiveBrush;
        StepThreeLabel.Foreground = _currentStepIndex == 2 ? activeBrush : inactiveBrush;
        StepFourLabel.Foreground = _currentStepIndex == 3 ? activeBrush : inactiveBrush;
        StepFiveLabel.Foreground = _currentStepIndex == 4 ? activeBrush : inactiveBrush;
        StepSixLabel.Foreground = _currentStepIndex == 5 ? activeBrush : inactiveBrush;

        BackButton.IsEnabled = _currentStepIndex > 0;
        NextButton.Visibility = _currentStepIndex < LastStepIndex ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _currentStepIndex == LastStepIndex ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = IsCurrentStepValid();
        FinishButton.IsEnabled = IsCurrentStepValid();
        ApiKeyTestButton.IsEnabled = !_apiKeyTestInFlight && !string.IsNullOrWhiteSpace(GetApiKey());
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.recording-mode
     private void UpdateWarnings()
     {
         var warning = string.Empty;
         if (_currentStepIndex == 4 && _snapshot?.Capability.State == HostCapabilityState.Degraded && ModeAutoRadio.IsChecked == true)
         {
             warning = LocalizationManager.Instance["Wizard_Warning_AutoCaptureDegraded"];
         }
         else if (_currentStepIndex == 5)
         {
             warning = WizardApplicationStepModel.BuildWhitelistWarning(GetSelectedRecordingMode(), _selectedApplications.Count);
         }
 
         InlineWarningText.Text = warning;
         InlineWarningText.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool IsCurrentStepValid() => _currentStepIndex switch
    {
        0 => LanguageComboBox.SelectedItem is not null,
        1 => !string.IsNullOrWhiteSpace(RecordingsFolderTextBox.Text) && !string.IsNullOrWhiteSpace(TranscriptsFolderTextBox.Text),
        2 => AutomaticDeviceSelectionCheckBox.IsChecked == true || (OutputDeviceComboBox.SelectedItem is not null && MicrophoneComboBox.SelectedItem is not null),
        3 => !string.IsNullOrWhiteSpace(GetApiKey()),
        4 => ModeOffRadio.IsChecked == true || ModeAskRadio.IsChecked == true || ModeAutoRadio.IsChecked == true,
        _ => true
    };

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (!await ValidateStepAsync(_currentStepIndex))
        {
            return;
        }

        _currentStepIndex = Math.Min(LastStepIndex, _currentStepIndex + 1);
        UpdateWarnings();
        UpdateStepState();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _currentStepIndex = Math.Max(0, _currentStepIndex - 1);
        UpdateWarnings();
        UpdateStepState();
    }

    private async void OnFinishClick(object sender, RoutedEventArgs e)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.completion
        if (!await ValidateStepAsync(_currentStepIndex))
        {
            return;
        }

        if (CompleteAsync is null)
        {
            MessageBox.Show(this, LocalizationManager.Instance["Wizard_Error_CompletionUnavailable"], LocalizationManager.Instance["Wizard_Error_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        FinishButton.IsEnabled = false;
        try
        {
            var result = await CompleteAsync(BuildCompletionRequest());
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message ?? LocalizationManager.Instance["Wizard_Error_SaveFailed"], LocalizationManager.Instance["Wizard_Error_MessageBox_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }

     private WizardCompletionRequest BuildCompletionRequest() =>
         // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps
         new(
             UiLanguage: GetSelectedOptionValue(LanguageComboBox) ?? "ru",
            RecordingsFolder: RecordingsFolderTextBox.Text.Trim(),
            TranscriptsFolder: TranscriptsFolderTextBox.Text.Trim(),
            DetermineDevicesAutomatically: AutomaticDeviceSelectionCheckBox.IsChecked == true,
            OutputDeviceId: GetSelectedOptionValue(OutputDeviceComboBox),
            FollowSystemDefaultOutput: AutomaticDeviceSelectionCheckBox.IsChecked == true || GetSelectedOptionValue(OutputDeviceComboBox) is null,
             MicrophoneDeviceId: GetSelectedOptionValue(MicrophoneComboBox),
             FollowSystemDefaultMic: AutomaticDeviceSelectionCheckBox.IsChecked == true || GetSelectedOptionValue(MicrophoneComboBox) is null,
             FireworksApiKey: GetApiKey(),
             RecordingMode: GetSelectedRecordingMode(),
             SuggestAppsAutomatically: SuggestAppsAutomaticallyCheckBox.IsChecked == true,
             SelectedApplications: _selectedApplications.Select(static item => item.ToRecord()).ToArray());

    private async Task<bool> ValidateStepAsync(int stepIndex)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.navigation
        if (!IsCurrentStepValid())
        {
            MessageBox.Show(this, LocalizationManager.Instance["Wizard_Validation_IncompleteFields"], LocalizationManager.Instance["Wizard_Validation_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (stepIndex != 1)
        {
            return true;
        }

        return await EnsureDirectoryExistsAsync(RecordingsFolderTextBox.Text.Trim(), "Recordings folder")
            && await EnsureDirectoryExistsAsync(TranscriptsFolderTextBox.Text.Trim(), "Transcripts folder");
    }

    private static Task<bool> EnsureDirectoryExistsAsync(string path, string label)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.storage
        if (Directory.Exists(path))
        {
            return Task.FromResult(true);
        }

        var result = MessageBox.Show(
            string.Format(LocalizationManager.Instance["Dialog_CreateFolder_Prompt"], label, path),
            LocalizationManager.Instance["Dialog_CreateFolder_Title"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return Task.FromResult(false);
        }

        try
        {
            Directory.CreateDirectory(path);
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                string.Format(LocalizationManager.Instance["Dialog_CreateFolder_Error"], exception.Message),
                LocalizationManager.Instance["Dialog_CreateFolder_Title"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return Task.FromResult(false);
        }
    }

    private string GetSelectedRecordingMode()
    {
        if (ModeOffRadio.IsChecked == true)
        {
            return "off";
        }

        if (ModeAutoRadio.IsChecked == true)
        {
            return "auto";
        }

        return "ask";
    }

    private string GetApiKey() => _showApiKey ? ApiKeyTextBox.Text.Trim() : ApiKeyPasswordBox.Password.Trim();

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.api-key
    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingApiKey || _showApiKey)
        {
            return;
        }

        _syncingApiKey = true;
        ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
        _syncingApiKey = false;
        SetApiKeyTestStatus(string.Empty);
        UpdateStepState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.api-key
    private void OnApiKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingApiKey || !_showApiKey)
        {
            return;
        }

        _syncingApiKey = true;
        ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
        _syncingApiKey = false;
        SetApiKeyTestStatus(string.Empty);
        UpdateStepState();
    }

    private void OnToggleApiKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.api-key
        _showApiKey = !_showApiKey;
        if (_showApiKey)
        {
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
        }
        else
        {
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
        }

        ApiKeyPasswordBox.Visibility = _showApiKey ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyTextBox.Visibility = _showApiKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyToggleText.Text = _showApiKey ? LocalizationManager.Instance["Shell_Settings_Transcription_Hide"] : LocalizationManager.Instance["Shell_Settings_Transcription_Show"];
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.navigation
     private void OnWizardInputChanged(object? sender, EventArgs e)
     {
         UpdateDeviceSelectionMode();
         UpdateApplicationsStepState();
         UpdateWarnings();
         UpdateStepState();
     }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.api-key
    private async void OnTestApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (_apiKeyTestInFlight)
        {
            return;
        }

        _apiKeyTestInFlight = true;
        ApiKeyTestButton.IsEnabled = false;
        SetApiKeyTestStatus(LocalizationManager.Instance["Wizard_Step3_Testing"], (System.Windows.Media.Brush)FindResource("AppAccentBrush"));

        try
        {
            var result = await FireworksApiKeyProbe.TestAsync(GetApiKey());
            SetApiKeyTestStatus(
                result.Message ?? (result.Success ? LocalizationManager.Instance["Wizard_Step3_TestSuccess"] : LocalizationManager.Instance["Wizard_Step3_TestFailed"]),
                result.Success ? (System.Windows.Media.Brush)FindResource("AppSuccessBrush") : (System.Windows.Media.Brush)FindResource("AppErrorBrush"));
        }
        finally
        {
            _apiKeyTestInFlight = false;
            UpdateStepState();
        }
    }

    private void OnBrowseRecordingsClick(object sender, RoutedEventArgs e)
    {
        var selectedPath = BrowseFolder(RecordingsFolderTextBox.Text);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RecordingsFolderTextBox.Text = selectedPath;
        }
    }

    private void OnBrowseTranscriptsClick(object sender, RoutedEventArgs e)
    {
        var selectedPath = BrowseFolder(TranscriptsFolderTextBox.Text);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            TranscriptsFolderTextBox.Text = selectedPath;
        }
    }

    private string? BrowseFolder(string currentPath)
    {
        using var dialog = new FolderBrowserDialog
        {
            InitialDirectory = Directory.Exists(currentPath) ? currentPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

     private void OnOpenFireworksDocsClick(object sender, RoutedEventArgs e)
     {
         Process.Start(new ProcessStartInfo
         {
            FileName = FireworksDocsUrl,
            UseShellExecute = true
        });
    }

     private void SetApiKeyTestStatus(string message, System.Windows.Media.Brush? brush = null)
     {
         ApiKeyTestStatusText.Text = message;
         ApiKeyTestStatusText.Foreground = brush ?? (System.Windows.Media.Brush)FindResource("AppTextTertiaryBrush");
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void OnAddFromRunningClick(object sender, RoutedEventArgs e)
     {
         if (_snapshot is null)
         {
             return;
         }

         var dialog = new RunningProcessPickerWindow(_snapshot.RunningProcesses.Select(process =>
             new SelectableProcessItem(process.DisplayName, process.ProcessName, process.HasAudioActivity, process.WasRecentlyActive)))
         {
             Owner = this
         };

         if (dialog.ShowDialog() != true)
         {
             return;
         }

         foreach (var rule in dialog.SelectedRules)
         {
             UpsertSelectedApplication(rule);
         }

         UpdateApplicationsStepState();
         UpdateWarnings();
         UpdateStepState();
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void OnAddApplicationManuallyClick(object sender, RoutedEventArgs e)
     {
         var dialog = new ManualApplicationRuleWindow
         {
             Owner = this
         };

         if (dialog.ShowDialog() == true && dialog.Result is not null)
         {
             UpsertSelectedApplication(dialog.Result);
             UpdateApplicationsStepState();
             UpdateWarnings();
             UpdateStepState();
         }
     }

     // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
     private void OnRemoveSelectedApplicationClick(object sender, RoutedEventArgs e)
     {
         if (SelectedApplicationsListBox.SelectedItem is not EditableAppRuleItem selected)
         {
             return;
         }

         RemoveSelectedApplicationByProcess(selected.ProcessName);
         UpdateApplicationsStepState();
         UpdateWarnings();
         UpdateStepState();
     }

     private void OnSelectedApplicationSelectionChanged(object sender, SelectionChangedEventArgs e) =>
         RemoveSelectedApplicationButton.IsEnabled = SelectedApplicationsListBox.SelectedItem is EditableAppRuleItem;
 
     private void OnQuitClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
 }
