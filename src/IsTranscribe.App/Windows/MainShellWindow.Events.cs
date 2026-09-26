using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IsTranscribe.App.Configuration;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using IsTranscribe.App.Strings;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace IsTranscribe.App.Windows;

public partial class MainShellWindow
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.rules
    private async void OnSettingsSectionTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot || _suppressSettingsTabSelection || e.Source != SettingsSectionTabControl)
        {
            return;
        }

        var selectedSection = (SettingsSectionTabControl.SelectedItem as TabItem)?.Tag?.ToString() ?? "General";
        if (selectedSection == _activeSettingsSection)
        {
            return;
        }

        if (!await EnsureSectionCanChangeAsync(_activeSettingsSection))
        {
            _suppressSettingsTabSelection = true;
            SettingsSectionTabControl.SelectedItem = SettingsSectionTabControl.Items.Cast<TabItem>().First(item => string.Equals(item.Tag?.ToString(), _activeSettingsSection, StringComparison.Ordinal));
            _suppressSettingsTabSelection = false;
            return;
        }

        _activeSettingsSection = selectedSection;
        if ((selectedSection is "Devices" or "Applications") && RefreshSnapshotAsync is not null)
        {
            LoadSnapshot(await RefreshSnapshotAsync());
        }
    }

    private void OnGeneralInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        UpdateGeneralState();
    }

    private void OnRecordingInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        DetectRecordingPreset();
        UpdateRecordingState();
    }

    private void OnRecordingPresetChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        ApplyRecordingPreset();
        UpdateRecordingState();
    }

    private void OnDevicesInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        ApplyDeviceSelectionState();
        UpdateDevicesState();
    }

    private void OnApplicationsInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        UpdateApplicationsState();
    }

    private void OnStorageInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        UpdateStorageState();
    }

    private void OnTranscriptionInputChanged(object sender, EventArgs e)
    {
        if (_snapshot is null || _isLoadingSnapshot)
        {
            return;
        }

        if (_syncingTranscriptionApiKey)
        {
            return;
        }

        if (_showTranscriptionApiKey && sender == TranscriptionApiKeyTextBox)
        {
            _syncingTranscriptionApiKey = true;
            TranscriptionApiKeyPasswordBox.Password = TranscriptionApiKeyTextBox.Text;
            _syncingTranscriptionApiKey = false;
        }
        else if (!_showTranscriptionApiKey && sender == TranscriptionApiKeyPasswordBox)
        {
            _syncingTranscriptionApiKey = true;
            TranscriptionApiKeyTextBox.Text = TranscriptionApiKeyPasswordBox.Password;
            _syncingTranscriptionApiKey = false;
        }

        ApplyTranscriptionToggleState();
        UpdateDiarizationVisibility();
        UpdateRetryVisibility();
        UpdateTranscriptionState();
    }

    private async void OnGeneralSaveClick(object sender, RoutedEventArgs e) => await SaveGeneralSectionAsync();

    private async void OnRecordingSaveClick(object sender, RoutedEventArgs e) => await SaveRecordingSectionAsync();

    private async void OnDevicesSaveClick(object sender, RoutedEventArgs e) => await SaveDevicesSectionAsync();

    private async void OnApplicationsSaveClick(object sender, RoutedEventArgs e) => await SaveApplicationsSectionAsync();

    private async void OnStorageSaveClick(object sender, RoutedEventArgs e) => await SaveStorageSectionAsync();

    private async void OnTranscriptionSaveClick(object sender, RoutedEventArgs e) => await SaveTranscriptionSectionAsync();

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private async void OnAddRunningApplicationsClick(object sender, RoutedEventArgs e)
    {
        var snapshot = RefreshSnapshotAsync is null ? _snapshot : await RefreshSnapshotAsync();
        if (snapshot is null)
        {
            return;
        }

        var dialog = new RunningProcessPickerWindow(snapshot.RunningProcesses.Select(process => new SelectableProcessItem(process.DisplayName, process.ProcessName, process.HasAudioActivity, process.WasRecentlyActive)))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var rule in dialog.SelectedRules)
        {
            UpsertWhitelistRule(rule);
        }

        UpdateApplicationsState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnAddManualApplicationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualApplicationRuleWindow
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            UpsertWhitelistRule(dialog.Result);
            UpdateApplicationsState();
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnRemoveWhitelistRuleClick(object sender, RoutedEventArgs e)
    {
        if (WhitelistListBox.SelectedItem is EditableAppRuleItem selected)
        {
            RemoveWhitelistRuleByProcess(selected.ProcessName);
            UpdateApplicationsState();
        }
    }

    private void OnMoveWhitelistRuleUpClick(object sender, RoutedEventArgs e)
    {
        if (WhitelistListBox.SelectedItem is not EditableAppRuleItem selected)
        {
            return;
        }

        var index = _whitelist.IndexOf(selected);
        if (index <= 0)
        {
            return;
        }

        _whitelist.Move(index, index - 1);
        UpdateApplicationsState();
    }

    private void OnMoveWhitelistRuleDownClick(object sender, RoutedEventArgs e)
    {
        if (WhitelistListBox.SelectedItem is not EditableAppRuleItem selected)
        {
            return;
        }

        var index = _whitelist.IndexOf(selected);
        if (index < 0 || index >= _whitelist.Count - 1)
        {
            return;
        }

        _whitelist.Move(index, index + 1);
        UpdateApplicationsState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnAddExclusionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SingleValuePromptWindow(LocalizationManager.Instance["Dialog_AddExclusion_Title"], LocalizationManager.Instance["Dialog_AddExclusion_Prompt"])
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
        {
            _exclusions.Add(new EditableStringItem(dialog.Result!));
            UpdateApplicationsState();
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnRemoveExclusionClick(object sender, RoutedEventArgs e)
    {
        if (ExclusionsListBox.SelectedItem is EditableStringItem selected && !selected.IsBuiltIn)
        {
            _exclusions.Remove(selected);
            UpdateApplicationsState();
        }
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    private void OnRemoveIgnoredSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (IgnoredSuggestionsListBox.SelectedItem is EditableStringItem selected)
        {
            _ignoredAppSuggestions.Remove(selected);
            UpdateApplicationsState();
        }
    }

    private void OnBrowseStorageRecordingsClick(object sender, RoutedEventArgs e) => BrowseInto(StorageRecordingsFolderTextBox);

    private void OnBrowseStorageTranscriptsClick(object sender, RoutedEventArgs e) => BrowseInto(StorageTranscriptsFolderTextBox);

    private void OnBrowseStorageTempClick(object sender, RoutedEventArgs e) => BrowseInto(FailedTempFolderTextBox);

    private void BrowseInto(TextBox textBox)
    {
        using var dialog = new FolderBrowserDialog
        {
            InitialDirectory = Directory.Exists(textBox.Text) ? textBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            textBox.Text = dialog.SelectedPath;
        }
    }

    private void OnChangeApiKeyClick(object sender, RoutedEventArgs e)
    {
        _transcriptionApiKeyEditMode = true;
        _transcriptionApiKeyRemoved = false;
        SavedApiKeyPanel.Visibility = Visibility.Collapsed;
        EditableApiKeyPanel.Visibility = Visibility.Visible;
        TranscriptionApiKeyPasswordBox.Password = string.Empty;
        TranscriptionApiKeyTextBox.Text = string.Empty;
        UpdateTranscriptionState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.transcription
    private async void OnRemoveApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (SaveTranscriptionAsync is null)
        {
            return;
        }

        var result = MessageBox.Show(this, LocalizationManager.Instance["Dialog_RemoveApiKey_Message"], LocalizationManager.Instance["Dialog_RemoveApiKey_Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var request = new TranscriptionSectionSaveRequest(_baselineTranscription, null, true);
        var saveResult = await SaveTranscriptionAsync(request);
        if (!saveResult.Success)
        {
            TranscriptionValidationText.Text = saveResult.Message ?? LocalizationManager.Instance["Dialog_RemoveApiKey_Failed"];
            return;
        }

        await RefreshFromHostAsync();
    }

    private void OnCancelApiKeyEditClick(object sender, RoutedEventArgs e)
    {
        _transcriptionApiKeyEditMode = false;
        _transcriptionApiKeyRemoved = false;
        LoadTranscriptionSection(_baselineTranscription, new IsTranscribe.Host.Secrets.AppSecrets(_baselineApiKey));
        UpdateTranscriptionState();
    }

    private void OnToggleTranscriptionApiKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        _showTranscriptionApiKey = !_showTranscriptionApiKey;
        if (_showTranscriptionApiKey)
        {
            TranscriptionApiKeyTextBox.Text = TranscriptionApiKeyPasswordBox.Password;
        }
        else
        {
            TranscriptionApiKeyPasswordBox.Password = TranscriptionApiKeyTextBox.Text;
        }

        TranscriptionApiKeyPasswordBox.Visibility = _showTranscriptionApiKey ? Visibility.Collapsed : Visibility.Visible;
        TranscriptionApiKeyTextBox.Visibility = _showTranscriptionApiKey ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionApiKeyToggleText.Text = _showTranscriptionApiKey ? LocalizationManager.Instance["Shell_Settings_Transcription_Hide"] : LocalizationManager.Instance["Shell_Settings_Transcription_Show"];
    }

    private void OnStartForceRecordClick(object sender, RoutedEventArgs e) => ForceRecordRequested?.Invoke(this, EventArgs.Empty);

    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);

    private void OnResumeClick(object sender, RoutedEventArgs e) => ResumeRequested?.Invoke(this, EventArgs.Empty);

    private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    private void OnDiscardClick(object sender, RoutedEventArgs e) => DiscardRequested?.Invoke(this, EventArgs.Empty);

    private void OnTogglePrivacyPauseClick(object sender, RoutedEventArgs e) => TogglePrivacyPauseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnHomeMeetingsSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHomeMeetingActionButtons();

    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#actions.open-folder
    private void OnHomeOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (HomeMeetingsListBox.SelectedItem is not HomeMeetingSessionItem item || !item.CanOpenFolder || item.SessionDirectoryPath is null)
        {
            return;
        }

        OpenPath(item.SessionDirectoryPath);
    }

    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#actions.open-markdown
    private void OnHomeOpenMarkdownClick(object sender, RoutedEventArgs e)
    {
        if (HomeMeetingsListBox.SelectedItem is not HomeMeetingSessionItem item || !item.CanOpenMarkdown || item.TranscriptMarkdownPath is null)
        {
            return;
        }

        OpenPath(item.TranscriptMarkdownPath);
    }

    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#actions.retry
    private async void OnHomeRetryTranscriptionClick(object sender, RoutedEventArgs e)
    {
        if (RetryTranscriptionAsync is null || HomeMeetingsListBox.SelectedItem is not HomeMeetingSessionItem item || !item.CanRetryTranscription)
        {
            return;
        }

        var result = await RetryTranscriptionAsync(item.Id);
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message ?? LocalizationManager.Instance["Dialog_RetryFailed"], LocalizationManager.Instance["Dialog_RetryTranscription_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RefreshFromHostAsync();
    }

    private async void OnHomeLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _homeMeetingsVisibleCount += HomeMeetingsPageSize;
        await RefreshFromHostAsync();
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        GetHotkeyTextBox(tag).Text = string.Empty;
        UpdateGeneralState();
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general
    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            textBox.Text = string.Empty;
            UpdateGeneralState();
            e.Handled = true;
            return;
        }

        if (GlobalHotkeyProbe.TryBuildGesture(e, out var gesture))
        {
            textBox.Text = gesture;
            UpdateGeneralState();
        }

        e.Handled = true;
    }

    private TextBox GetHotkeyTextBox(string tag) => tag switch
    {
        "force_record_toggle" => ForceRecordHotkeyTextBox,
        "privacy_pause_toggle" => PrivacyPauseHotkeyTextBox,
        "discard_current" => DiscardHotkeyTextBox,
        "open_main_window" => OpenMainWindowHotkeyTextBox,
        _ => throw new InvalidOperationException($"Unknown hotkey tag '{tag}'.")
    };
}
