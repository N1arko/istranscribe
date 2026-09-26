using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Theming;
using IsTranscribe.Desktop.ViewModels;
using System.Reflection;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task User_changes_auto_save_as_one_complete_release_v2_update()
    {
        var customApplication = new MeetingApplicationPreference(
            "internal-call",
            "Internal Call",
            MeetingApplicationPolicy.Ignore);
        var initial = Settings(applications: [customApplication]);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        var strings = new FakeLocalizationService();
        var themes = new FakeThemeService();
        using var viewModel = new SettingsViewModel(
            runtime,
            strings,
            themes,
            initial,
            [new RuntimeMicrophoneSnapshot("desk-mic", "Desk microphone", IsDefault: false)]);
        var updateArrived = runtime.NextSettingsUpdate.Task;

        viewModel.Autostart = false;
        viewModel.Notifications = false;
        viewModel.SelectedMicrophone = Assert.Single(
            viewModel.MicrophoneChoices,
            option => option.Id == "desk-mic");
        viewModel.SelectedTheme = Assert.Single(
            viewModel.ThemeChoices,
            option => option.Value == "dark");
        viewModel.SelectedLanguage = Assert.Single(
            viewModel.LanguageChoices,
            option => option.Value == "en");

        var update = await updateArrived.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(update.Autostart);
        Assert.False(update.Notifications);
        Assert.Equal("desk-mic", update.MicrophoneDeviceId);
        Assert.False(update.FollowSystemDefaultMicrophone);
        Assert.Equal("dark", update.Theme);
        Assert.Equal("en", update.Language);
        Assert.Equal(UiThemeMode.Dark, themes.CurrentMode);
        Assert.Equal(IsTranscribe.Desktop.Localization.UiLanguage.English, strings.CurrentLanguage);
        var preserved = Assert.Single(
            update.Applications,
            application => application.ProfileId == customApplication.ProfileId);
        Assert.Equal(MeetingApplicationPolicy.Ignore, preserved.Policy);
        Assert.True(viewModel.HasStatus);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Flush_persists_latest_value_without_waiting_for_debounce()
    {
        var initial = Settings(applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            initial);

        viewModel.ServiceEnabled = false;
        viewModel.Autostart = false;
        await viewModel.FlushAsync(CancellationToken.None);

        var update = Assert.Single(runtime.SettingsUpdates);
        Assert.False(update.ServiceEnabled);
        Assert.False(update.Autostart);
        Assert.False(viewModel.IsSaving);
    }

    [Fact]
    public async Task Failed_auto_save_keeps_dirty_state_available_for_later_flush()
    {
        var initial = Settings(applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            initial);
        runtime.ActionFailure = new IOException("settings unavailable");
        viewModel.Notifications = false;

        await viewModel.FlushAsync(CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.Equal("String.Error.SettingsSave", viewModel.ErrorMessage);
        Assert.False(viewModel.IsSaving);
        Assert.Empty(runtime.SettingsUpdates);

        runtime.ActionFailure = null;
        await viewModel.FlushAsync(CancellationToken.None);

        var update = Assert.Single(runtime.SettingsUpdates);
        Assert.False(update.Notifications);
        Assert.True(viewModel.HasStatus);
    }

    [Fact]
    public async Task External_policy_change_during_save_is_merged_into_follow_up_snapshot()
    {
        var zoom = new MeetingApplicationPreference(
            "zoom",
            "Zoom",
            MeetingApplicationPolicy.Ask);
        var initial = Settings(applications: [zoom]);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial))
        {
            SettingsUpdateEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueSettingsUpdate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            initial);
        viewModel.SelectedTheme = Assert.Single(
            viewModel.ThemeChoices,
            option => option.Value == "dark");

        var flush = viewModel.FlushAsync(CancellationToken.None);
        await runtime.SettingsUpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var externalSettings = initial with
        {
            ServiceEnabled = false,
            Applications = [zoom with { Policy = MeetingApplicationPolicy.Ignore }]
        };
        var externalSnapshot = SnapshotFactory.Create(userSettings: externalSettings);
        var apply = typeof(SettingsViewModel).GetMethod(
            "ApplyRuntimeSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);
        apply.Invoke(viewModel, [externalSnapshot]);
        runtime.ContinueSettingsUpdate.TrySetResult(true);

        await flush.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, runtime.SettingsUpdates.Count);
        var merged = runtime.SettingsUpdates[^1];
        Assert.False(merged.ServiceEnabled);
        Assert.Equal("dark", merged.Theme);
        Assert.Equal(
            MeetingApplicationPolicy.Ignore,
            Assert.Single(merged.Applications, application => application.ProfileId == "zoom").Policy);
    }

    [Fact]
    public async Task Unrelated_save_does_not_replace_an_unavailable_explicit_microphone()
    {
        var initial = Settings(
            applications: [],
            microphoneDeviceId: "missing-mic",
            followSystemDefaultMicrophone: false);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            initial,
            availableMicrophones: []);

        Assert.Equal("missing-mic", viewModel.SelectedMicrophone.Id);
        Assert.True(viewModel.SelectedMicrophone.IsUnavailable);

        viewModel.Notifications = false;
        await viewModel.FlushAsync(CancellationToken.None);

        var update = Assert.Single(runtime.SettingsUpdates);
        Assert.Equal("missing-mic", update.MicrophoneDeviceId);
        Assert.False(update.FollowSystemDefaultMicrophone);
    }

    [Fact]
    public async Task Missing_selected_microphone_is_explained_and_can_be_rechecked_inline()
    {
        var initial = Settings(
            applications: [],
            microphoneDeviceId: "missing-mic",
            followSystemDefaultMicrophone: false);
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            userSettings: initial,
            capability: capability));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            initial,
            availableMicrophones: []);

        Assert.True(viewModel.HasMicrophoneCapabilityNotice);
        Assert.Equal(
            "String.Setup.Capability.ConfiguredMicrophoneUnavailable",
            viewModel.MicrophoneCapabilityMessage);

        await viewModel.RetryCapabilitiesCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.RefreshCapabilitiesCalls);
    }

    [Fact]
    public async Task Pending_language_preview_resists_stale_snapshot_until_matching_snapshot_confirms_it()
    {
        var initial = Settings(applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        var strings = new FakeLocalizationService();
        using var viewModel = new SettingsViewModel(
            runtime,
            strings,
            new FakeThemeService(),
            initial);
        var apply = typeof(SettingsViewModel).GetMethod(
            "ApplyRuntimeSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);

        viewModel.SelectedLanguage = Assert.Single(
            viewModel.LanguageChoices,
            option => option.Value == "en");
        strings.SetLanguage(UiLanguage.Russian);
        apply.Invoke(viewModel, [SnapshotFactory.Create(userSettings: initial)]);

        Assert.Equal("en", viewModel.SelectedLanguage.Value);
        Assert.Equal(UiLanguage.English, strings.CurrentLanguage);

        apply.Invoke(viewModel, [SnapshotFactory.Create(userSettings: initial with { Language = "en" })]);
        await viewModel.FlushAsync(CancellationToken.None);
        apply.Invoke(viewModel, [SnapshotFactory.Create(userSettings: initial)]);

        Assert.Equal("ru", viewModel.SelectedLanguage.Value);
        Assert.Equal(UiLanguage.Russian, strings.CurrentLanguage);
    }

    [Fact]
    public async Task Language_switch_rebuilds_the_exact_settings_status_and_error_category()
    {
        var initial = Settings(applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new SettingsViewModel(
            runtime,
            strings,
            new FakeThemeService(),
            initial);

        runtime.ActionFailure = new IOException("capability unavailable");
        await viewModel.RetryCapabilitiesCommand.ExecuteAsync(null);
        Assert.Equal("ru:String.Setup.Capability.CheckFailed", viewModel.ErrorMessage);

        strings.SetLanguage(UiLanguage.English);
        Assert.Equal("en:String.Setup.Capability.CheckFailed", viewModel.ErrorMessage);

        runtime.ActionFailure = null;
        viewModel.Notifications = false;
        await viewModel.FlushAsync(CancellationToken.None);
        Assert.Equal("en:String.Status.AutoSaved", viewModel.StatusMessage);

        strings.SetLanguage(UiLanguage.Russian);
        Assert.Equal("ru:String.Status.AutoSaved", viewModel.StatusMessage);
        Assert.False(viewModel.HasError);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    /// </summary>
    [Fact]
    public async Task Remote_transcription_setup_keeps_the_key_transient_and_routes_explicit_actions()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = TranscriptionSettings(
                selectedEngineId: "remote.groq",
                groqHasCredential: false,
                groqDisclosureAccepted: false)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings))
        {
            TranscriptionModelDiscoveryResult =
            [
                new TranscriptionModelCapability("whisper-large-v3", "Whisper large v3", IsRecommended: true)
            ]
        };
        var shell = new FakeDesktopShell();
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings,
            shell: shell);

        Assert.Equal("remote.groq", viewModel.SelectedTranscriptionEngine?.EngineId);
        Assert.False(viewModel.SelectedTranscriptionEngineHasCredential);
        viewModel.TranscriptionCredential = "synthetic-test-key";
        await viewModel.SaveTranscriptionCredentialCommand.ExecuteAsync(null);

        Assert.Equal(
            [("remote.groq", "synthetic-test-key")],
            runtime.SavedTranscriptionCredentials);
        Assert.Equal(string.Empty, viewModel.TranscriptionCredential);
        Assert.True(viewModel.SelectedTranscriptionEngineHasCredential);

        await viewModel.DiscoverTranscriptionModelsCommand.ExecuteAsync(null);

        Assert.Equal(["remote.groq"], runtime.DiscoveredTranscriptionModelEngines);
        Assert.Equal("whisper-large-v3", viewModel.SelectedTranscriptionModel?.Id);
        viewModel.TranscriptionConsentAccepted = true;
        viewModel.AutomaticTranscriptionEnabled = true;
        viewModel.SelectedTranscriptionLanguage = Assert.Single(
            viewModel.TranscriptionLanguageChoices,
            option => option.Value == "ru");
        viewModel.TranscriptionModeIndex = viewModel.IsSelectedTranscriptionEngineLocal ? 1 : 2;
        await viewModel.SaveTranscriptionSettingsCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.TranscriptionSettingsUpdates);
        Assert.Equal("remote.groq", update.SelectedEngineId);
        Assert.True(update.AutomaticEnabled);
        Assert.Equal("auto", update.Language);
        Assert.Equal("whisper-large-v3", update.SelectedModelId);
        Assert.True(update.DisclosureAccepted);
        Assert.True(update.RequireZeroDataRetention);

        await viewModel.OpenTranscriptionPolicyCommand.ExecuteAsync(null);
        Assert.Equal([new Uri("https://example.test/groq-policy")], shell.OpenedUris);

        await viewModel.DeleteTranscriptionCredentialCommand.ExecuteAsync(null);
        Assert.Equal(["remote.groq"], runtime.DeletedTranscriptionCredentials);
        Assert.False(viewModel.SelectedTranscriptionEngineHasCredential);
        Assert.False(viewModel.AutomaticTranscriptionEnabled);
    }

    [Fact]
    public async Task Persisted_non_recommended_transcription_model_remains_selected_when_settings_open_and_save()
    {
        var transcription = TranscriptionSettings(
            selectedEngineId: "remote.groq",
            groqHasCredential: true,
            groqDisclosureAccepted: true);
        var groq = Assert.Single(
            transcription.Engines,
            engine => engine.EngineId == "remote.groq");
        var recommended = new TranscriptionModelCapability(
            "whisper-large-v3-turbo",
            "Whisper large v3 turbo",
            IsRecommended: true);
        var persisted = new TranscriptionModelCapability(
            "whisper-large-v3",
            "Whisper large v3",
            IsRecommended: false);
        transcription = transcription with
        {
            Engines = transcription.Engines
                .Select(engine => engine.EngineId == groq.EngineId
                    ? engine with
                    {
                        SelectedModelId = persisted.Id,
                        Models = [recommended, persisted]
                    }
                    : engine)
                .ToArray()
        };
        var settings = Settings(applications: []) with { Transcription = transcription };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.Equal(persisted.Id, viewModel.SelectedTranscriptionModel?.Id);

        viewModel.TranscriptionModeIndex = viewModel.IsSelectedTranscriptionEngineLocal ? 1 : 2;
        await viewModel.SaveTranscriptionSettingsCommand.ExecuteAsync(null);

        Assert.Equal(persisted.Id, Assert.Single(runtime.TranscriptionSettingsUpdates).SelectedModelId);
    }

    [Fact]
    public void Changing_transcription_service_clears_the_transient_key_and_uses_its_own_credential_state()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = TranscriptionSettings(
                selectedEngineId: "remote.groq",
                groqHasCredential: true,
                groqDisclosureAccepted: true)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);
        Assert.False(viewModel.ShowZeroDataRetentionOption);
        viewModel.TranscriptionCredential = "unsaved-transient-value";

        viewModel.SelectedTranscriptionEngine = Assert.Single(
            viewModel.TranscriptionEngineChoices,
            option => option.EngineId == "remote.openrouter");

        Assert.Equal(string.Empty, viewModel.TranscriptionCredential);
        Assert.False(viewModel.SelectedTranscriptionEngineHasCredential);
        Assert.False(viewModel.TranscriptionConsentAccepted);
        Assert.True(viewModel.ShowZeroDataRetentionOption);
        Assert.True(viewModel.RequireZeroDataRetention);
    }

    [Fact]
    public async Task Openrouter_zero_data_retention_choice_is_loaded_from_snapshot_and_included_in_update()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = TranscriptionSettings(
                selectedEngineId: "remote.openrouter",
                groqHasCredential: false,
                groqDisclosureAccepted: false)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.ShowZeroDataRetentionOption);
        Assert.True(viewModel.RequireZeroDataRetention);

        viewModel.RequireZeroDataRetention = false;
        viewModel.TranscriptionCredential = "synthetic-openrouter-key";
        await viewModel.SaveTranscriptionCredentialCommand.ExecuteAsync(null);
        viewModel.TranscriptionConsentAccepted = true;
        viewModel.TranscriptionModeIndex = viewModel.IsSelectedTranscriptionEngineLocal ? 1 : 2;
        await viewModel.SaveTranscriptionSettingsCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.TranscriptionSettingsUpdates);
        Assert.Equal("remote.openrouter", update.SelectedEngineId);
        Assert.False(update.RequireZeroDataRetention);
    }

    [Fact]
    public async Task Automatic_transcription_names_the_missing_setup_step_before_saving()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = TranscriptionSettings(
                selectedEngineId: "remote.openrouter",
                groqHasCredential: false,
                groqDisclosureAccepted: false)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);
        viewModel.TranscriptionConsentAccepted = true;
        viewModel.AutomaticTranscriptionEnabled = true;

        viewModel.TranscriptionModeIndex = viewModel.IsSelectedTranscriptionEngineLocal ? 1 : 2;
        await viewModel.SaveTranscriptionSettingsCommand.ExecuteAsync(null);

        Assert.Equal("String.Transcription.Error.KeyRequired", viewModel.TranscriptionErrorMessage);
        Assert.Empty(runtime.TranscriptionSettingsUpdates);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </summary>
    [Fact]
    public async Task Local_transcription_uses_model_actions_without_remote_credentials_or_consent()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(RuntimeLocalModelState.NotInstalled)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.IsSelectedTranscriptionEngineLocal);
        viewModel.TranscriptionModeIndex = 1;
        Assert.True(viewModel.ShowLocalTranscriptionSettings);
        Assert.False(viewModel.ShowRemoteTranscriptionSettings);
        Assert.False(viewModel.ShowZeroDataRetentionOption);
        Assert.False(viewModel.HasTranscriptionPolicyLink);
        Assert.False(viewModel.CanEnableAutomaticTranscription);
        Assert.True(viewModel.ShowInstallLocalModelAction);
        Assert.False(viewModel.ShowCancelLocalModelInstallAction);
        Assert.False(viewModel.ShowRemoveLocalModelAction);
        Assert.Equal("String.Transcription.Settings.Local.Title", viewModel.TranscriptionSettingsTitle);
        Assert.Equal("large-v3-turbo", viewModel.SelectedTranscriptionModel?.Id);

        await viewModel.InstallLocalModelCommand.ExecuteAsync(null);

        Assert.Equal([("local.whisper", "large-v3-turbo")], runtime.InstalledTranscriptionModels);
        Assert.Equal(
            "String.Transcription.Status.LocalModelInstalled",
            viewModel.TranscriptionStatusMessage);
        Assert.Empty(runtime.SavedTranscriptionCredentials);
    }

    [Fact]
    public async Task Local_model_progress_routes_cancel_and_recording_notice_from_runtime_snapshots()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(
                RuntimeLocalModelState.Downloading,
                progress: 0.42,
                resources: new RuntimeLocalResourceSnapshot(
                    IsLowPowerMode: true,
                    AutomaticDeferred: true,
                    AvailableMemoryBytes: 4_000_000_000,
                    RequiredMemoryBytes: 852_000_000,
                    AvailableDiskBytes: 10_000_000_000,
                    RequiredDiskBytes: 600_000_000,
                    StableBlockCode: "low_power"))
        };
        var snapshot = SnapshotFactory.Create(userSettings: settings) with
        {
            Activity = ApplicationActivityState.Recording
        };
        var runtime = new FakeApplicationRuntime(snapshot);
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.ShowLocalModelProgress);
        Assert.Equal(42, viewModel.LocalModelProgressPercent);
        Assert.True(viewModel.ShowCancelLocalModelInstallAction);
        Assert.True(viewModel.ShowLocalTranscriptionRecordingNotice);
        Assert.True(viewModel.ShowLocalResourceWarning);
        Assert.Equal(
            "String.Transcription.Settings.Local.Resource.LowPower",
            viewModel.LocalResourceWarningText);

        await viewModel.CancelLocalModelInstallCommand.ExecuteAsync(null);

        Assert.Equal([("local.whisper", "large-v3-turbo")], runtime.CancelledTranscriptionModelInstalls);
        Assert.Equal(
            "String.Transcription.Status.LocalModelDownloadCancelled",
            viewModel.TranscriptionStatusMessage);
    }

    [Fact]
    public async Task Verified_local_model_enables_automatic_transcription_and_can_be_removed()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(
                RuntimeLocalModelState.Installed,
                isVerified: true,
                installedSizeBytes: 487_601_967)
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings));
        using var viewModel = new SettingsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.IsSelectedLocalModelReady);
        Assert.True(viewModel.CanEnableAutomaticTranscription);
        Assert.True(viewModel.ShowRemoveLocalModelAction);
        viewModel.AutomaticTranscriptionEnabled = true;

        viewModel.TranscriptionModeIndex = viewModel.IsSelectedTranscriptionEngineLocal ? 1 : 2;
        await viewModel.SaveTranscriptionSettingsCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.TranscriptionSettingsUpdates);
        Assert.Equal("local.whisper", update.SelectedEngineId);
        Assert.Equal("large-v3-turbo", update.SelectedModelId);
        Assert.True(update.AutomaticEnabled);
        Assert.False(update.DisclosureAccepted);
        Assert.Empty(runtime.SavedTranscriptionCredentials);

        await viewModel.RemoveLocalModelCommand.ExecuteAsync(null);

        Assert.Equal([("local.whisper", "large-v3-turbo")], runtime.RemovedTranscriptionModels);
        Assert.False(viewModel.AutomaticTranscriptionEnabled);
        Assert.Equal("String.Transcription.Status.LocalModelRemoved", viewModel.TranscriptionStatusMessage);
    }

    [Theory]
    [InlineData(
        400_000_000L,
        852_000_000L,
        2_000_000_000L,
        600_000_000L,
        "String.Transcription.Settings.Local.Resource.Memory")]
    [InlineData(
        2_000_000_000L,
        852_000_000L,
        300_000_000L,
        600_000_000L,
        "String.Transcription.Settings.Local.Resource.Disk")]
    public void Local_resource_warning_names_memory_or_disk_shortage(
        long availableMemory,
        long requiredMemory,
        long availableDisk,
        long requiredDisk,
        string expectedResourceKey)
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(
                RuntimeLocalModelState.Installed,
                isVerified: true,
                resources: new RuntimeLocalResourceSnapshot(
                    IsLowPowerMode: false,
                    AutomaticDeferred: false,
                    availableMemory,
                    requiredMemory,
                    availableDisk,
                    requiredDisk,
                    StableBlockCode: expectedResourceKey))
        };
        using var viewModel = new SettingsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings)),
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.ShowLocalResourceWarning);
        Assert.Equal(expectedResourceKey, viewModel.LocalResourceWarningText);
    }

    [Fact]
    public void Local_transcription_copy_and_preset_labels_follow_the_active_language()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(RuntimeLocalModelState.NotInstalled)
        };
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new SettingsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings)),
            strings,
            new FakeThemeService(),
            settings);

        Assert.Equal("ru:String.Transcription.Settings.Local.Title", viewModel.TranscriptionSettingsTitle);
        Assert.Equal(
            "ru:String.Transcription.LocalModel.Preset.Generic",
            viewModel.SelectedTranscriptionModel?.DisplayName);
        Assert.Equal(
            "ru:String.Transcription.LocalModel.State.NotInstalled",
            viewModel.LocalModelStatusText);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.Transcription.Settings.Local.Title", viewModel.TranscriptionSettingsTitle);
        Assert.Equal(
            "en:String.Transcription.LocalModel.Preset.Generic",
            viewModel.SelectedTranscriptionModel?.DisplayName);
        Assert.Equal(
            "en:String.Transcription.LocalModel.State.NotInstalled",
            viewModel.LocalModelStatusText);
    }

    [Fact]
    public void Cancelled_local_model_download_is_resumable_without_generic_failure_copy()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(
                RuntimeLocalModelState.Failed,
                stableErrorCode: "model_download_cancelled")
        };
        using var viewModel = new SettingsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings)),
            new FakeLocalizationService(),
            new FakeThemeService(),
            settings);

        Assert.True(viewModel.ShowInstallLocalModelAction);
        Assert.True(viewModel.InstallLocalModelCommand.CanExecute(null));
        Assert.Equal(
            "String.Transcription.Status.LocalModelDownloadCancelled",
            viewModel.LocalModelStatusText);
        Assert.Equal(
            "String.Transcription.Action.ContinueModelDownload",
            viewModel.InstallLocalModelActionText);
        Assert.False(viewModel.HasTranscriptionError);
    }

    [Fact]
    public void Installed_model_size_uses_the_active_locale_decimal_separator()
    {
        var settings = Settings(applications: []) with
        {
            Transcription = LocalTranscriptionSettings(
                RuntimeLocalModelState.Installed,
                isVerified: true,
                installedSizeBytes: 487_601_967)
        };
        var strings = new FakeLocalizationService();
        using var viewModel = new SettingsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: settings)),
            strings,
            new FakeThemeService(),
            settings);

        Assert.Equal("465,0", viewModel.SelectedTranscriptionModel?.InstalledSizeLabel);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("465.0", viewModel.SelectedTranscriptionModel?.InstalledSizeLabel);
    }

    private static RuntimeUserSettingsSnapshot Settings(
        IReadOnlyList<MeetingApplicationPreference> applications,
        string? microphoneDeviceId = null,
        bool followSystemDefaultMicrophone = true) => new(
        OnboardingCompleted: true,
        ServiceEnabled: true,
        Autostart: true,
        Notifications: true,
        Language: "ru",
        Theme: "system",
        MicrophoneDeviceId: microphoneDeviceId,
        FollowSystemDefaultMicrophone: followSystemDefaultMicrophone,
        RecordingsFolder: null,
        applications);

    private static RuntimeTranscriptionSettingsSnapshot TranscriptionSettings(
        string selectedEngineId,
        bool groqHasCredential,
        bool groqDisclosureAccepted)
    {
        var model = new TranscriptionModelCapability(
            "whisper-large-v3-turbo",
            "Whisper large v3 turbo",
            IsRecommended: true);
        return new RuntimeTranscriptionSettingsSnapshot(
            selectedEngineId,
            AutomaticEnabled: false,
            Language: "auto",
            RequireZeroDataRetention: true,
            Engines:
            [
                new RuntimeTranscriptionEngineSnapshot(
                    "remote.groq",
                    "Groq",
                    TranscriptionExecutionKind.Remote,
                    RequiresNetwork: true,
                    "Audio is sent to Groq.",
                    new Uri("https://example.test/groq-policy"),
                    groqHasCredential,
                    model.Id,
                    [model],
                    SupportsModelDiscovery: true,
                    groqDisclosureAccepted,
                    "groq-v1"),
                new RuntimeTranscriptionEngineSnapshot(
                    "remote.openrouter",
                    "OpenRouter",
                    TranscriptionExecutionKind.Remote,
                    RequiresNetwork: true,
                    "Audio is sent to OpenRouter.",
                    new Uri("https://example.test/openrouter-policy"),
                    HasCredential: false,
                    model.Id,
                    [model],
                    SupportsModelDiscovery: true,
                    DisclosureAccepted: false,
                    "openrouter-v1")
            ]);
    }

    private static RuntimeTranscriptionSettingsSnapshot LocalTranscriptionSettings(
        RuntimeLocalModelState selectedState,
        bool isVerified = false,
        double progress = 0,
        long? installedSizeBytes = null,
        RuntimeLocalResourceSnapshot? resources = null,
        string? stableErrorCode = null)
    {
        var models = new[]
        {
            new TranscriptionModelCapability("base", "Base", IsRecommended: false),
            new TranscriptionModelCapability("large-v3-turbo", "Small", IsRecommended: true),
            new TranscriptionModelCapability("medium", "Medium", IsRecommended: false)
        };
        return new RuntimeTranscriptionSettingsSnapshot(
            "local.whisper",
            AutomaticEnabled: true,
            Language: "auto",
            RequireZeroDataRetention: true,
            Engines:
            [
                new RuntimeTranscriptionEngineSnapshot(
                    "local.whisper",
                    "Local Whisper",
                    TranscriptionExecutionKind.Local,
                    RequiresNetwork: false,
                    "Audio stays on this device.",
                    PolicyUri: null,
                    HasCredential: false,
                    SelectedModelId: "large-v3-turbo",
                    Models: models,
                    SupportsModelDiscovery: false,
                    DisclosureAccepted: false,
                    RequiredDisclosureRevision: null)
                {
                    LocalModels =
                    [
                        LocalModel("base", models[0], RuntimeLocalModelState.NotInstalled),
                        LocalModel(
                            "large-v3-turbo",
                            models[1],
                            selectedState,
                            isVerified,
                            progress,
                            installedSizeBytes,
                            stableErrorCode),
                        LocalModel("medium", models[2], RuntimeLocalModelState.NotInstalled)
                    ],
                    LocalResources = resources
                }
            ]);
    }

    private static RuntimeLocalModelSnapshot LocalModel(
        string modelId,
        TranscriptionModelCapability model,
        RuntimeLocalModelState state,
        bool isVerified = false,
        double progress = 0,
        long? installedSizeBytes = null,
        string? stableErrorCode = null) => new(
        modelId,
        model.DisplayName,
        modelId switch
        {
            "base" => 147_951_465,
            "large-v3-turbo" => 487_601_967,
            _ => 1_533_763_059
        },
        installedSizeBytes,
        model.IsRecommended,
        state,
        progress,
        isVerified,
        stableErrorCode);
}
