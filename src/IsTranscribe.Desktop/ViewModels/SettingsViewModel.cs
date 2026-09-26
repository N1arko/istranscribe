using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Theming;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// Compact auto-saving settings surface for user-facing choices.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// </remarks>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(300);
    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _strings;
    private readonly IThemeService _themes;
    private readonly IPlatformShell? _shell;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<SettingsDirtyField, long> _dirtyFieldVersions = [];
    private readonly Dictionary<string, long> _dirtyApplicationVersions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _saveCancellation;
    private bool _serviceEnabled;
    private bool _autostart;
    private bool _notifications;
    private bool _followSystemDefaultMicrophone;
    private string? _microphoneDeviceId;
    private string? _recordingsFolder;
    private MicrophoneOptionViewModel _selectedMicrophone;
    private SettingChoiceOptionViewModel _selectedTheme;
    private SettingChoiceOptionViewModel _selectedLanguage;
    private bool _isSaving;
    private bool _isCheckingCapabilities;
    private RuntimeCapabilitySnapshot _capability;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _statusMessageKey;
    private string? _errorMessageKey;
    private string? _pendingLanguageValue;
    private bool _suppressSave = true;
    private bool _isDirty;
    private long _editGeneration;
    private bool _disposed;
    private TranscriptionEngineOptionViewModel? _selectedTranscriptionEngine;
    private TranscriptionModelOptionViewModel? _selectedTranscriptionModel;
    private SettingChoiceOptionViewModel _selectedTranscriptionLanguage;
    private string _transcriptionCredential = string.Empty;
    private bool _automaticTranscriptionEnabled;
    private bool _requireZeroDataRetention;
    private bool _transcriptionConsentAccepted;
    private bool _isTranscriptionBusy;
    private bool _transcriptionDirty;
    private bool _suppressTranscriptionChanges = true;
    private string? _transcriptionStatusMessage;
    private string? _transcriptionErrorMessage;
    private string? _transcriptionStatusMessageKey;
    private string? _transcriptionErrorMessageKey;
    private bool _isLocalModelInstallRunning;
    private bool _isRecordingActive;
    private bool _transcriptionCommandsInitialized;

    public SettingsViewModel(
        IApplicationRuntime runtime,
        ILocalizationService strings,
        IThemeService themes,
        RuntimeUserSettingsSnapshot settings,
        IReadOnlyList<RuntimeMicrophoneSnapshot>? availableMicrophones = null,
        TimeProvider? timeProvider = null,
        MeetingProfileRegistry? profiles = null,
        IPlatformShell? shell = null,
        IPermissionService? permissionService = null)
    {
        _runtime = runtime;
        _strings = strings;
        _themes = themes;
        _shell = shell;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serviceEnabled = settings.ServiceEnabled;
        _autostart = settings.Autostart;
        _notifications = settings.Notifications;
        _followSystemDefaultMicrophone = settings.FollowSystemDefaultMicrophone;
        _microphoneDeviceId = settings.MicrophoneDeviceId;
        _recordingsFolder = settings.RecordingsFolder;
        _capability = runtime.Snapshot.Capability;
        _isRecordingActive = runtime.Snapshot.Activity == ApplicationActivityState.Recording;

        MicrophoneChoices.Add(MicrophoneOptionViewModel.SystemDefault(strings));
        foreach (var microphone in availableMicrophones ?? [])
        {
            MicrophoneChoices.Add(new MicrophoneOptionViewModel(microphone.Id, microphone.DisplayName, strings));
        }

        SetupViewModel.EnsureUnavailableMicrophoneChoice(
            MicrophoneChoices,
            settings.FollowSystemDefaultMicrophone ? null : settings.MicrophoneDeviceId,
            strings);

        _selectedMicrophone = settings.FollowSystemDefaultMicrophone
            ? MicrophoneChoices[0]
            : MicrophoneChoices.FirstOrDefault(option => option.Id == settings.MicrophoneDeviceId)
                ?? MicrophoneChoices[0];

        ThemeChoices =
        [
            new("system", "String.Settings.Theme.System", strings),
            new("light", "String.Settings.Theme.Light", strings),
            new("dark", "String.Settings.Theme.Dark", strings)
        ];
        LanguageChoices =
        [
            new("ru", "String.Settings.Language.Russian", strings),
            new("en", "String.Settings.Language.English", strings)
        ];
        _selectedTheme = ThemeChoices.FirstOrDefault(option => option.Value == settings.Theme)
            ?? ThemeChoices[0];
        _selectedLanguage = LanguageChoices.FirstOrDefault(option => option.Value == settings.Language)
            ?? LanguageChoices[0];

        TranscriptionLanguageChoices =
        [
            new("auto", "String.Transcription.Settings.Language.Auto", strings),
            new("ru", "String.Transcription.Settings.Language.Russian", strings),
            new("en", "String.Transcription.Settings.Language.English", strings)
        ];
        _selectedTranscriptionLanguage = TranscriptionLanguageChoices.FirstOrDefault(
            option => string.Equals(option.Value, settings.Transcription.Language, StringComparison.Ordinal))
            ?? TranscriptionLanguageChoices[0];
        _automaticTranscriptionEnabled = settings.Transcription.AutomaticEnabled;
        _requireZeroDataRetention = settings.Transcription.RequireZeroDataRetention;
        foreach (var engine in settings.Transcription.Engines)
        {
            TranscriptionEngineChoices.Add(new TranscriptionEngineOptionViewModel(engine, strings));
        }

        _selectedTranscriptionEngine = TranscriptionEngineChoices.FirstOrDefault(option => string.Equals(
            option.EngineId,
            settings.Transcription.SelectedEngineId,
            StringComparison.Ordinal));
        _transcriptionModeIndex = !settings.Transcription.AutomaticEnabled ? 0
            : _selectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Local ? 1
            : _selectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Remote ? 2 : 0;
        ApplySelectedTranscriptionEngine(_selectedTranscriptionEngine?.SelectedModelId);

        var registry = profiles ?? new MeetingProfileRegistry();
        var preferences = settings.Applications.ToDictionary(
            static preference => preference.ProfileId,
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in registry.Profiles)
        {
            var monitored = !preferences.TryGetValue(profile.Id, out var preference)
                || preference.Policy == MeetingApplicationPolicy.Ask;
            var option = new MeetingApplicationOptionViewModel(
                profile.Id,
                profile.DisplayName,
                monitored,
                strings);
            option.PropertyChanged += Application_OnPropertyChanged;
            Applications.Add(option);
        }

        foreach (var preference in settings.Applications.Where(preference => registry.FindById(preference.ProfileId) is null))
        {
            var option = new MeetingApplicationOptionViewModel(
                preference.ProfileId,
                preference.DisplayName,
                preference.Policy != MeetingApplicationPolicy.Ignore,
                strings);
            option.ApplyPreference(preference);
            option.PropertyChanged += Application_OnPropertyChanged;
            Applications.Add(option);
        }

        if (permissionService is not null)
        {
            PlatformPermissions.Add(new PlatformPermissionOptionViewModel("microphone", "String.Permission.Microphone", permissionService, strings));
            PlatformPermissions.Add(new PlatformPermissionOptionViewModel("system_audio", "String.Permission.SystemAudio", permissionService, strings));
            PlatformPermissions.Add(new PlatformPermissionOptionViewModel("accessibility", "String.Permission.Accessibility", permissionService, strings));
            PlatformPermissions.Add(new PlatformPermissionOptionViewModel("notifications", "String.Permission.Notifications", permissionService, strings));
            _ = RefreshPlatformPermissionsAsync();
        }

        ChooseFolderCommand = new RelayCommand(() => FolderPickerRequested?.Invoke(this, EventArgs.Empty));
        RetryCapabilitiesCommand = new AsyncRelayCommand(
            RetryCapabilitiesAsync,
            () => !IsSaving && !IsCheckingCapabilities);
        OpenDiagnosticsCommand = new RelayCommand(() => DiagnosticsRequested?.Invoke(this, EventArgs.Empty));
        SaveTranscriptionCredentialCommand = new AsyncRelayCommand(
            SaveTranscriptionCredentialAsync,
            CanSaveTranscriptionCredential);
        DeleteTranscriptionCredentialCommand = new AsyncRelayCommand(
            DeleteTranscriptionCredentialAsync,
            CanDeleteTranscriptionCredential);
        DiscoverTranscriptionModelsCommand = new AsyncRelayCommand(
            DiscoverTranscriptionModelsAsync,
            CanDiscoverTranscriptionModels);
        SaveTranscriptionSettingsCommand = new AsyncRelayCommand(
            SaveTranscriptionSettingsAsync,
            CanSaveTranscriptionSettings);
        OpenTranscriptionPolicyCommand = new AsyncRelayCommand(
            OpenTranscriptionPolicyAsync,
            CanOpenTranscriptionPolicy);
        InstallLocalModelCommand = new AsyncRelayCommand(
            InstallLocalModelAsync,
            CanInstallLocalModel);
        InstallDiarizationCommand = new AsyncRelayCommand(async () =>
        {
            await ExecuteTranscriptionActionAsync(
                async token =>
                {
                    await _runtime.InstallDiarizationAssetsAsync(token);
                    SetTranscriptionStatus("String.Transcription.Status.SpeakersInstalled");
                },
                "String.Transcription.Error.LocalModelInstall");
        });
        CancelLocalModelInstallCommand = new AsyncRelayCommand(
            CancelLocalModelInstallAsync,
            CanCancelLocalModelInstall);
        RemoveLocalModelCommand = new AsyncRelayCommand(
            RemoveLocalModelAsync,
            CanRemoveLocalModel);
        _transcriptionCommandsInitialized = true;
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        _strings.LanguageChanged += HandleLanguageChanged;
        _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
        _suppressSave = false;
        _suppressTranscriptionChanges = false;
    }

    public event EventHandler? FolderPickerRequested;

    public event EventHandler? DiagnosticsRequested;

    public event EventHandler? CloseRequested;

    public ObservableCollection<MeetingApplicationOptionViewModel> Applications { get; } = [];

    public IReadOnlyList<SettingChoiceOptionViewModel> ThemeChoices { get; }

    public IReadOnlyList<SettingChoiceOptionViewModel> LanguageChoices { get; }

    public ObservableCollection<MicrophoneOptionViewModel> MicrophoneChoices { get; } = [];

    public ObservableCollection<TranscriptionEngineOptionViewModel> TranscriptionEngineChoices { get; } = [];

    public ObservableCollection<TranscriptionModelOptionViewModel> TranscriptionModelChoices { get; } = [];

    public IReadOnlyList<SettingChoiceOptionViewModel> TranscriptionLanguageChoices { get; }

    public ObservableCollection<PlatformPermissionOptionViewModel> PlatformPermissions { get; } = [];

    public bool HasPlatformPermissions => PlatformPermissions.Count > 0;

    public IRelayCommand ChooseFolderCommand { get; }

    public IAsyncRelayCommand RetryCapabilitiesCommand { get; }

    public IRelayCommand OpenDiagnosticsCommand { get; }

    public IAsyncRelayCommand SaveTranscriptionCredentialCommand { get; }

    public IAsyncRelayCommand DeleteTranscriptionCredentialCommand { get; }

    public IAsyncRelayCommand DiscoverTranscriptionModelsCommand { get; }

    public IAsyncRelayCommand SaveTranscriptionSettingsCommand { get; }

    public IAsyncRelayCommand OpenTranscriptionPolicyCommand { get; }

    public IAsyncRelayCommand InstallLocalModelCommand { get; }
    public IAsyncRelayCommand InstallDiarizationCommand { get; }

    public IAsyncRelayCommand CancelLocalModelInstallCommand { get; }

    public IAsyncRelayCommand RemoveLocalModelCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public bool ServiceEnabled
    {
        get => _serviceEnabled;
        set
        {
            if (SetProperty(ref _serviceEnabled, value))
            {
                ScheduleSave(SettingsDirtyField.Service);
            }
        }
    }

    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (SetProperty(ref _autostart, value))
            {
                ScheduleSave(SettingsDirtyField.Autostart);
            }
        }
    }

    public bool Notifications
    {
        get => _notifications;
        set
        {
            if (SetProperty(ref _notifications, value))
            {
                ScheduleSave(SettingsDirtyField.Notifications);
            }
        }
    }

    private int _transcriptionModeIndex;

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#modes
    public int TranscriptionModeIndex
    {
        get => _transcriptionModeIndex;
        set
        {
            if (value is < 0 or > 2 || !SetProperty(ref _transcriptionModeIndex, value)) return;
            if (!_suppressTranscriptionChanges)
            {
                SelectedTranscriptionEngine = value switch
                {
                    1 => SelectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Local ? SelectedTranscriptionEngine
                        : TranscriptionEngineChoices.FirstOrDefault(x => x.ExecutionKind == TranscriptionExecutionKind.Local),
                    2 => SelectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Remote ? SelectedTranscriptionEngine
                        : TranscriptionEngineChoices.FirstOrDefault(x => x.ExecutionKind == TranscriptionExecutionKind.Remote),
                    _ => null
                };
                AutomaticTranscriptionEnabled = value != 0;
                _transcriptionDirty = true;
            }
            OnPropertyChanged(nameof(ShowOnlineProviderChoice));
            NotifyTranscriptionPresentationChanged();
        }
    }

    public bool ShowOnlineProviderChoice => TranscriptionModeIndex == 2;
    public IEnumerable<TranscriptionEngineOptionViewModel> OnlineProviderChoices =>
        TranscriptionEngineChoices.Where(x => x.ExecutionKind == TranscriptionExecutionKind.Remote);

    public TranscriptionEngineOptionViewModel? SelectedTranscriptionEngine
    {
        get => _selectedTranscriptionEngine;
        set
        {
            if (!SetProperty(ref _selectedTranscriptionEngine, value))
            {
                return;
            }

            TranscriptionCredential = string.Empty;
            ApplySelectedTranscriptionEngine(value?.SelectedModelId);
            if (!_suppressTranscriptionChanges)
            {
                AutomaticTranscriptionEnabled = TranscriptionModeIndex != 0;
                _transcriptionDirty = true;
            }

            NotifyTranscriptionPresentationChanged();
        }
    }

    public TranscriptionModelOptionViewModel? SelectedTranscriptionModel
    {
        get => _selectedTranscriptionModel;
        set
        {
            if (!SetProperty(ref _selectedTranscriptionModel, value))
            {
                return;
            }

            if (!_suppressTranscriptionChanges)
            {
                _transcriptionDirty = true;
                if (IsSelectedTranscriptionEngineLocal && !IsSelectedLocalModelReady)
                {
                    AutomaticTranscriptionEnabled = false;
                }
            }

            NotifySelectedTranscriptionModelChanged();
        }
    }

    public SettingChoiceOptionViewModel SelectedTranscriptionLanguage
    {
        get => _selectedTranscriptionLanguage;
        set
        {
            if (SetProperty(ref _selectedTranscriptionLanguage, value)
                && !_suppressTranscriptionChanges)
            {
                _transcriptionDirty = true;
            }
        }
    }

    public string TranscriptionCredential
    {
        get => _transcriptionCredential;
        set
        {
            if (SetProperty(ref _transcriptionCredential, value ?? string.Empty))
            {
                SaveTranscriptionCredentialCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AutomaticTranscriptionEnabled
    {
        get => _automaticTranscriptionEnabled;
        set
        {
            if (SetProperty(ref _automaticTranscriptionEnabled, value)
                && !_suppressTranscriptionChanges)
            {
                _transcriptionDirty = true;
            }
        }
    }

    public bool RequireZeroDataRetention
    {
        get => _requireZeroDataRetention;
        set
        {
            if (SetProperty(ref _requireZeroDataRetention, value)
                && !_suppressTranscriptionChanges)
            {
                _transcriptionDirty = true;
            }
        }
    }

    public bool TranscriptionConsentAccepted
    {
        get => _transcriptionConsentAccepted;
        set
        {
            if (SetProperty(ref _transcriptionConsentAccepted, value)
                && !_suppressTranscriptionChanges)
            {
                _transcriptionDirty = true;
            }
        }
    }

    public bool IsTranscriptionBusy
    {
        get => _isTranscriptionBusy;
        private set
        {
            if (SetProperty(ref _isTranscriptionBusy, value))
            {
                NotifyTranscriptionCommandStates();
            }
        }
    }

    public bool HasSelectedTranscriptionEngine => TranscriptionModeIndex != 0 && SelectedTranscriptionEngine is not null;

    public bool IsSelectedTranscriptionEngineLocal =>
        SelectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Local;

    public bool ShowRemoteTranscriptionSettings =>
        HasSelectedTranscriptionEngine && SelectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Remote;

    public bool ShowLocalTranscriptionSettings => HasSelectedTranscriptionEngine && IsSelectedTranscriptionEngineLocal;

    public string TranscriptionSettingsTitle => _strings.Get(IsSelectedTranscriptionEngineLocal
        ? "String.Transcription.Settings.Local.Title"
        : "String.Transcription.Settings.Title");

    public string TranscriptionSettingsDescription => _strings.Get(IsSelectedTranscriptionEngineLocal
        ? "String.Transcription.Settings.Local.Description"
        : "String.Transcription.Settings.Description");

    public string AutomaticTranscriptionDescription => _strings.Get(IsSelectedTranscriptionEngineLocal
        && !IsSelectedLocalModelReady
            ? "String.Transcription.Settings.Automatic.LocalUnavailable"
            : "String.Transcription.Settings.Automatic.Description");

    public bool SelectedTranscriptionEngineHasCredential => SelectedTranscriptionEngine?.HasCredential == true;

    public bool HasTranscriptionPolicyLink => ShowRemoteTranscriptionSettings
        && SelectedTranscriptionEngine?.PolicyUri is not null
        && _shell is not null;

    public bool ShowTranscriptionModelDiscovery =>
        SelectedTranscriptionEngine?.SupportsModelDiscovery == true;

    public bool ShowZeroDataRetentionOption => string.Equals(
        SelectedTranscriptionEngine?.EngineId,
        OpenRouterTranscriptionEngine.EngineId,
        StringComparison.Ordinal);

    public RuntimeLocalModelSnapshot? SelectedLocalModel => SelectedTranscriptionModel?.LocalModel;

    public RuntimeLocalResourceSnapshot? SelectedLocalResources =>
        SelectedTranscriptionEngine is { } engine
        && SelectedTranscriptionModel is { } model
        && string.Equals(engine.SelectedModelId, model.Id, StringComparison.Ordinal)
            ? engine.LocalResources
            : null;

    public bool HasSelectedLocalModel => SelectedLocalModel is not null;

    public bool IsSelectedLocalModelReady => SelectedLocalModel is
    {
        State: RuntimeLocalModelState.Installed,
        IsVerified: true
    };

    public bool CanEnableAutomaticTranscription => ShowRemoteTranscriptionSettings
        || IsSelectedLocalModelReady;

    public bool ShowLocalModelProgress => SelectedLocalModel?.State is
        RuntimeLocalModelState.Downloading or RuntimeLocalModelState.Verifying;

    public bool ShowInstallLocalModelAction => ShowLocalTranscriptionSettings
        && SelectedLocalModel?.State is (
            RuntimeLocalModelState.NotInstalled or RuntimeLocalModelState.Failed);

    public string InstallLocalModelActionText => _strings.Get(
        SelectedLocalModel?.StableErrorCode == "model_download_cancelled"
            ? "String.Transcription.Action.ContinueModelDownload"
            : "String.Transcription.Action.InstallModel");

    public bool ShowCancelLocalModelInstallAction => ShowLocalTranscriptionSettings
        && (_isLocalModelInstallRunning
            || SelectedLocalModel?.State is (
                RuntimeLocalModelState.Downloading or RuntimeLocalModelState.Verifying));

    public bool ShowRemoveLocalModelAction => ShowLocalTranscriptionSettings
        && SelectedLocalModel?.State == RuntimeLocalModelState.Installed;

    public bool IsLocalModelProgressIndeterminate =>
        SelectedLocalModel?.State == RuntimeLocalModelState.Verifying;

    public double LocalModelProgressPercent => Math.Clamp(
        (SelectedLocalModel?.Progress ?? 0) * 100,
        0,
        100);

    public string SelectedLocalModelDescription => SelectedTranscriptionModel?.Description ?? string.Empty;

    public string LocalModelStatusText => LocalizeLocalModelStatus(SelectedLocalModel);

    public bool ShowLocalResourceWarning => ShowLocalTranscriptionSettings
        && (SelectedLocalResources?.IsLowPowerMode == true
            || HasInsufficientLocalMemory
            || HasInsufficientLocalDisk
            || !string.IsNullOrWhiteSpace(SelectedLocalResources?.StableBlockCode));

    public string LocalResourceWarningText => _strings.Get(
        SelectedLocalResources?.IsLowPowerMode == true
            ? "String.Transcription.Settings.Local.Resource.LowPower"
            : HasInsufficientLocalMemory
                ? "String.Transcription.Settings.Local.Resource.Memory"
                : HasInsufficientLocalDisk
                    ? "String.Transcription.Settings.Local.Resource.Disk"
                    : "String.Transcription.Settings.Local.Resource.Unavailable");

    public bool ShowLocalTranscriptionRecordingNotice => ShowLocalTranscriptionSettings
        && _isRecordingActive;

    public string TranscriptionCredentialState => _strings.Get(SelectedTranscriptionEngineHasCredential
        ? "String.Transcription.Settings.Key.Saved"
        : "String.Transcription.Settings.Key.Missing");

    public string TranscriptionConsentText => _strings.Format(
        "String.Transcription.Settings.Consent.Format",
        SelectedTranscriptionEngine?.DisplayName ?? _strings.Get("String.Transcription.Settings.Service.Generic"));

    public string? TranscriptionStatusMessage
    {
        get => _transcriptionStatusMessage;
        private set
        {
            if (SetProperty(ref _transcriptionStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasTranscriptionStatus));
            }
        }
    }

    public bool HasTranscriptionStatus => !string.IsNullOrWhiteSpace(TranscriptionStatusMessage);

    public string? TranscriptionErrorMessage
    {
        get => _transcriptionErrorMessage;
        private set
        {
            if (SetProperty(ref _transcriptionErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasTranscriptionError));
            }
        }
    }

    public bool HasTranscriptionError => !string.IsNullOrWhiteSpace(TranscriptionErrorMessage);

    public bool FollowSystemDefaultMicrophone
    {
        get => _followSystemDefaultMicrophone;
        set
        {
            if (SetProperty(ref _followSystemDefaultMicrophone, value))
            {
                ScheduleSave(SettingsDirtyField.Microphone);
            }
        }
    }

    public MicrophoneOptionViewModel SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (!SetProperty(ref _selectedMicrophone, value))
            {
                return;
            }

            _microphoneDeviceId = value.Id;
            _followSystemDefaultMicrophone = value.Id is null;
            OnPropertyChanged(nameof(FollowSystemDefaultMicrophone));
            ScheduleSave(SettingsDirtyField.Microphone);
        }
    }

    public SettingChoiceOptionViewModel SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value))
            {
                return;
            }

            _themes.SetTheme(value.Value switch
            {
                "light" => UiThemeMode.Light,
                "dark" => UiThemeMode.Dark,
                _ => UiThemeMode.System
            });
            ScheduleSave(SettingsDirtyField.Theme);
        }
    }

    public SettingChoiceOptionViewModel SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            if (!_suppressSave)
            {
                _pendingLanguageValue = value.Value;
            }

            _strings.SetLanguage(value.Value == "en" ? UiLanguage.English : UiLanguage.Russian);
            ScheduleSave(SettingsDirtyField.Language);
        }
    }

    // @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
    internal string LanguagePreviewValue => SelectedLanguage.Value;

    public string? RecordingsFolder
    {
        get => _recordingsFolder;
        private set
        {
            if (!SetProperty(ref _recordingsFolder, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RecordingsFolderLabel));
            ScheduleSave(SettingsDirtyField.RecordingsFolder);
        }
    }

    public string RecordingsFolderLabel => string.IsNullOrWhiteSpace(RecordingsFolder)
        ? _strings.Get("String.Settings.RecordingsFolder.AppOwned")
        : RecordingsFolder;

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                RetryCapabilitiesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsCheckingCapabilities
    {
        get => _isCheckingCapabilities;
        private set
        {
            if (SetProperty(ref _isCheckingCapabilities, value))
            {
                OnPropertyChanged(nameof(MicrophoneCapabilityActionText));
                RetryCapabilitiesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasMicrophoneCapabilityNotice => _capability.Issue is
        RuntimeCapabilityIssue.NoActiveMicrophone
        or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
        or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable;

    public string MicrophoneCapabilityMessage => _strings.Get(_capability.Issue switch
    {
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable =>
            "String.Setup.Capability.ConfiguredMicrophoneUnavailable",
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable =>
            "String.Setup.Capability.MicrophoneCaptureUnavailable",
        _ => "String.Setup.Capability.MicrophoneMissing"
    });

    public string MicrophoneCapabilityActionText => _strings.Get(IsCheckingCapabilities
        ? "String.Setup.CheckingPermissions"
        : "String.Setup.RetryPermissions");

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void SetRecordingsFolder(string? path) =>
        RecordingsFolder = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        _saveCancellation?.Cancel();
        await SavePendingAsync(cancellationToken);
    }

    private async Task RetryCapabilitiesAsync()
    {
        IsCheckingCapabilities = true;
        SetErrorMessage(null);
        try
        {
            await _runtime.RefreshCapabilitiesAsync(CancellationToken.None);
            await RefreshPlatformPermissionsAsync();
            ApplyRuntimeSnapshot(_runtime.Snapshot);
        }
        catch
        {
            SetErrorMessage("String.Setup.Capability.CheckFailed");
        }
        finally
        {
            IsCheckingCapabilities = false;
        }
    }

    private bool CanSaveTranscriptionCredential() =>
        !IsTranscriptionBusy
        && ShowRemoteTranscriptionSettings
        && !string.IsNullOrWhiteSpace(TranscriptionCredential);

    private async Task SaveTranscriptionCredentialAsync()
    {
        if (SelectedTranscriptionEngine is not { } engine
            || string.IsNullOrWhiteSpace(TranscriptionCredential))
        {
            SetTranscriptionError("String.Transcription.Error.KeyRequired");
            return;
        }

        await ExecuteTranscriptionActionAsync(
            async cancellationToken =>
            {
                await _runtime.SaveTranscriptionCredentialAsync(
                    engine.EngineId,
                    TranscriptionCredential,
                    cancellationToken);
                TranscriptionCredential = string.Empty;
                engine.SetCredentialPresence(hasCredential: true);
                NotifyTranscriptionPresentationChanged();
                SetTranscriptionStatus("String.Transcription.Status.KeySaved");
            },
            "String.Transcription.Error.KeySave");
    }

    private bool CanDeleteTranscriptionCredential() =>
        !IsTranscriptionBusy
        && ShowRemoteTranscriptionSettings
        && SelectedTranscriptionEngineHasCredential;

    private async Task DeleteTranscriptionCredentialAsync()
    {
        if (SelectedTranscriptionEngine is not { } engine)
        {
            return;
        }

        await ExecuteTranscriptionActionAsync(
            async cancellationToken =>
            {
                await _runtime.DeleteTranscriptionCredentialAsync(engine.EngineId, cancellationToken);
                TranscriptionCredential = string.Empty;
                engine.SetCredentialPresence(hasCredential: false);
                AutomaticTranscriptionEnabled = false;
                NotifyTranscriptionPresentationChanged();
                SetTranscriptionStatus("String.Transcription.Status.KeyDeleted");
            },
            "String.Transcription.Error.KeyDelete");
    }

    private bool CanDiscoverTranscriptionModels() =>
        !IsTranscriptionBusy
        && SelectedTranscriptionEngine is { SupportsModelDiscovery: true, HasCredential: true };

    private async Task DiscoverTranscriptionModelsAsync()
    {
        if (SelectedTranscriptionEngine is not { } engine)
        {
            return;
        }

        await ExecuteTranscriptionActionAsync(
            async cancellationToken =>
            {
                var models = await _runtime.DiscoverTranscriptionModelsAsync(
                    engine.EngineId,
                    cancellationToken);
                if (models.Count == 0)
                {
                    SetTranscriptionError("String.Transcription.Error.ModelsUnavailable");
                    return;
                }

                engine.SetModels(models);
                ApplySelectedTranscriptionEngine(SelectedTranscriptionModel?.Id);
                _transcriptionDirty = true;
                SetTranscriptionStatus("String.Transcription.Status.ModelsUpdated");
            },
            "String.Transcription.Error.ModelDiscovery");
    }

    private bool CanSaveTranscriptionSettings() =>
        !IsTranscriptionBusy;

    private async Task SaveTranscriptionSettingsAsync()
    {
        if (TranscriptionModeIndex == 0)
        {
            await ExecuteTranscriptionActionAsync(async cancellationToken =>
            {
                await _runtime.UpdateTranscriptionSettingsAsync(new RuntimeTranscriptionSettingsUpdate(
                    null, false, "auto", RequireZeroDataRetention, null, false), cancellationToken);
                _transcriptionDirty = false;
                SetTranscriptionStatus("String.Transcription.Status.SettingsSaved");
            }, "String.Transcription.Error.SettingsSave");
            return;
        }
        AutomaticTranscriptionEnabled = true;
        if (SelectedTranscriptionEngine is not { } engine)
        {
            SetTranscriptionError("String.Transcription.Error.ServiceRequired");
            return;
        }

        if (engine.RequiresNetwork && !TranscriptionConsentAccepted)
        {
            SetTranscriptionError("String.Transcription.Error.ConsentRequired");
            return;
        }

        if (AutomaticTranscriptionEnabled
            && engine.ExecutionKind == TranscriptionExecutionKind.Remote
            && !engine.HasCredential)
        {
            SetTranscriptionError("String.Transcription.Error.KeyRequired");
            return;
        }

        if (engine.Models.Count == 0 && engine.SupportsModelDiscovery && engine.HasCredential)
            await DiscoverTranscriptionModelsAsync();
        if (SelectedTranscriptionModel is null)
        {
            SetTranscriptionError("String.Transcription.Error.ModelRequired");
            return;
        }

        await ExecuteTranscriptionActionAsync(
            async cancellationToken =>
            {
                await _runtime.UpdateTranscriptionSettingsAsync(
                    new RuntimeTranscriptionSettingsUpdate(
                        engine.EngineId,
                        AutomaticTranscriptionEnabled,
                        "auto",
                        RequireZeroDataRetention,
                        SelectedTranscriptionModel.Id,
                        engine.ExecutionKind == TranscriptionExecutionKind.Remote
                            && TranscriptionConsentAccepted),
                    cancellationToken);
                _transcriptionDirty = false;
                SetTranscriptionStatus("String.Transcription.Status.SettingsSaved");
            },
            "String.Transcription.Error.SettingsSave");
    }

    private bool CanOpenTranscriptionPolicy() =>
        !IsTranscriptionBusy && HasTranscriptionPolicyLink;

    private async Task OpenTranscriptionPolicyAsync()
    {
        if (_shell is null || SelectedTranscriptionEngine?.PolicyUri is not { } policyUri)
        {
            return;
        }

        await ExecuteTranscriptionActionAsync(
            cancellationToken => _shell.OpenUriAsync(policyUri, cancellationToken),
            "String.Transcription.Error.PolicyOpen");
    }

    private bool CanInstallLocalModel() =>
        !IsTranscriptionBusy
        && !_isLocalModelInstallRunning
        && SelectedLocalModel?.State is (
            RuntimeLocalModelState.NotInstalled or RuntimeLocalModelState.Failed);

    private async Task InstallLocalModelAsync()
    {
        if (SelectedTranscriptionEngine is not { ExecutionKind: TranscriptionExecutionKind.Local } engine
            || SelectedLocalModel is not { } model)
        {
            SetTranscriptionError("String.Transcription.Error.LocalModelRequired");
            return;
        }

        _isLocalModelInstallRunning = true;
        NotifyTranscriptionCommandStates();
        SetTranscriptionError(null);
        SetTranscriptionStatus(null);
        try
        {
            await _runtime.InstallDiarizationAssetsAsync(CancellationToken.None);
            await _runtime.InstallTranscriptionModelAsync(
                engine.EngineId,
                model.ModelId,
                CancellationToken.None);
            SetTranscriptionStatus("String.Transcription.Status.LocalModelInstalled");
        }
        catch (TranscriptionCommandException exception)
        {
            SetTranscriptionError(MapLocalModelCommandError(
                exception.Code,
                "String.Transcription.Error.LocalModelInstall"));
        }
        catch (OperationCanceledException)
        {
            SetTranscriptionStatus("String.Transcription.Status.LocalModelDownloadCancelled");
        }
        catch
        {
            SetTranscriptionError("String.Transcription.Error.LocalModelInstall");
        }
        finally
        {
            _isLocalModelInstallRunning = false;
            NotifyTranscriptionCommandStates();
        }
    }

    private bool CanCancelLocalModelInstall() =>
        !IsTranscriptionBusy
        && SelectedLocalModel is not null
        && (_isLocalModelInstallRunning
            || SelectedLocalModel.State is (
                RuntimeLocalModelState.Downloading or RuntimeLocalModelState.Verifying));

    private async Task CancelLocalModelInstallAsync()
    {
        if (SelectedTranscriptionEngine is not { ExecutionKind: TranscriptionExecutionKind.Local } engine
            || SelectedLocalModel is not { } model)
        {
            return;
        }

        try
        {
            await _runtime.CancelTranscriptionModelInstallAsync(
                engine.EngineId,
                model.ModelId,
                CancellationToken.None);
            SetTranscriptionStatus("String.Transcription.Status.LocalModelDownloadCancelled");
        }
        catch (TranscriptionCommandException exception)
        {
            SetTranscriptionError(MapLocalModelCommandError(
                exception.Code,
                "String.Transcription.Error.LocalModelCancel"));
        }
        catch
        {
            SetTranscriptionError("String.Transcription.Error.LocalModelCancel");
        }
    }

    private bool CanRemoveLocalModel() =>
        !IsTranscriptionBusy
        && !_isLocalModelInstallRunning
        && SelectedLocalModel?.State == RuntimeLocalModelState.Installed;

    private async Task RemoveLocalModelAsync()
    {
        if (SelectedTranscriptionEngine is not { ExecutionKind: TranscriptionExecutionKind.Local } engine
            || SelectedLocalModel is not { } model)
        {
            return;
        }

        await ExecuteTranscriptionActionAsync(
            async cancellationToken =>
            {
                await _runtime.RemoveTranscriptionModelAsync(
                    engine.EngineId,
                    model.ModelId,
                    cancellationToken);
                AutomaticTranscriptionEnabled = false;
                SetTranscriptionStatus("String.Transcription.Status.LocalModelRemoved");
            },
            "String.Transcription.Error.LocalModelRemove");
    }

    private async Task ExecuteTranscriptionActionAsync(
        Func<CancellationToken, ValueTask> action,
        string fallbackErrorKey)
    {
        IsTranscriptionBusy = true;
        SetTranscriptionError(null);
        SetTranscriptionStatus(null);
        try
        {
            await action(CancellationToken.None);
        }
        catch (TranscriptionCommandException exception)
        {
            SetTranscriptionError(MapTranscriptionCommandError(exception.Code, fallbackErrorKey));
        }
        catch
        {
            SetTranscriptionError(fallbackErrorKey);
        }
        finally
        {
            IsTranscriptionBusy = false;
        }
    }

    private static string MapTranscriptionCommandError(string code, string fallbackErrorKey) =>
        code switch
        {
            "engine_not_selected" => "String.Transcription.Error.ServiceRequired",
            "credential_required" => "String.Transcription.Error.KeyRequired",
            "remote_consent_required" => "String.Transcription.Error.ConsentRequired",
            "model_unavailable" or "model_discovery_required" => "String.Transcription.Error.ModelRequired",
            "model_download_storage" => "String.Transcription.Error.LocalModelStorage",
            "model_download_network" => "String.Transcription.Error.LocalModelNetwork",
            "model_download_integrity" => "String.Transcription.Error.LocalModelIntegrity",
            "model_download_activation" => "String.Transcription.Error.LocalModelActivation",
            "model_in_use" => "String.Transcription.Error.LocalModelInUse",
            "model_operation_in_progress" => "String.Transcription.Error.LocalModelBusy",
            _ => fallbackErrorKey
        };

    private static string MapLocalModelCommandError(string code, string fallbackErrorKey) =>
        code switch
        {
            "model_download_cancelled" => "String.Transcription.Status.LocalModelDownloadCancelled",
            "model_download_storage" => "String.Transcription.Error.LocalModelStorage",
            "model_download_network" => "String.Transcription.Error.LocalModelNetwork",
            "model_download_integrity" => "String.Transcription.Error.LocalModelIntegrity",
            "model_download_activation" => "String.Transcription.Error.LocalModelActivation",
            "model_in_use" => "String.Transcription.Error.LocalModelInUse",
            "model_operation_in_progress" => "String.Transcription.Error.LocalModelBusy",
            _ => fallbackErrorKey
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transcriptionCredential = string.Empty;
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        _strings.LanguageChanged -= HandleLanguageChanged;
        _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
        foreach (var application in Applications)
        {
            application.PropertyChanged -= Application_OnPropertyChanged;
            application.Dispose();
        }

        foreach (var option in ThemeChoices.Concat(LanguageChoices).Concat(TranscriptionLanguageChoices))
        {
            option.Dispose();
        }
        foreach (var microphone in MicrophoneChoices)
        {
            microphone.Dispose();
        }
        foreach (var permission in PlatformPermissions)
        {
            permission.Dispose();
        }
    }

    private async Task RefreshPlatformPermissionsAsync()
    {
        foreach (var permission in PlatformPermissions)
        {
            await permission.RefreshAsync(CancellationToken.None);
        }
    }

    private void ScheduleSave(SettingsDirtyField field, string? applicationId = null)
    {
        if (_suppressSave || _disposed)
        {
            return;
        }

        MarkDirty(field, applicationId);
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        _saveCancellation = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveCancellation.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounce, _timeProvider, cancellationToken);
            await SavePendingAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            SetErrorMessage("String.Error.SettingsSave");
        }
    }

    private async Task SavePendingAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            while (_isDirty)
            {
                var saveGeneration = _editGeneration;
                var update = BuildUpdate();
                _isDirty = false;
                IsSaving = true;
                SetErrorMessage(null);
                SetStatusMessage(null);
                try
                {
                    await _runtime.UpdateSettingsAsync(update, cancellationToken);
                    ClearSavedDirtyState(saveGeneration);
                    _isDirty = HasDirtyState;
                    SetStatusMessage("String.Status.AutoSaved");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _isDirty = HasDirtyState;
                    throw;
                }
                catch
                {
                    _isDirty = HasDirtyState;
                    SetErrorMessage("String.Error.SettingsSave");
                    return;
                }
                finally
                {
                    IsSaving = false;
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private RuntimeUserSettingsUpdate BuildUpdate() => new(
        ServiceEnabled,
        Autostart,
        Notifications,
        SelectedLanguage.Value,
        SelectedTheme.Value,
        SelectedMicrophone.Id,
        SelectedMicrophone.Id is null,
        RecordingsFolder,
        Applications.Select(static application => application.BuildPreference()).ToArray());

    private void Runtime_OnSnapshotChanged(object? sender, ApplicationRuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyRuntimeSnapshot(snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyRuntimeSnapshot(snapshot));
    }

    private void ApplyRuntimeSnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        ApplyCapability(snapshot.Capability);
        var isRecordingActive = snapshot.Activity == ApplicationActivityState.Recording;
        if (_isRecordingActive != isRecordingActive)
        {
            _isRecordingActive = isRecordingActive;
            OnPropertyChanged(nameof(ShowLocalTranscriptionRecordingNotice));
        }

        var settings = snapshot.UserSettings;
        _suppressSave = true;
        try
        {
            ApplyExternalField(
                SettingsDirtyField.Service,
                _serviceEnabled != settings.ServiceEnabled,
                () => ServiceEnabled = settings.ServiceEnabled);
            ApplyExternalField(
                SettingsDirtyField.Autostart,
                _autostart != settings.Autostart,
                () => Autostart = settings.Autostart);
            ApplyExternalField(
                SettingsDirtyField.Notifications,
                _notifications != settings.Notifications,
                () => Notifications = settings.Notifications);
            ApplyExternalField(
                SettingsDirtyField.RecordingsFolder,
                !string.Equals(RecordingsFolder, settings.RecordingsFolder, StringComparison.Ordinal),
                () => RecordingsFolder = settings.RecordingsFolder);

            var theme = ThemeChoices.FirstOrDefault(option => option.Value == settings.Theme) ?? ThemeChoices[0];
            ApplyExternalField(
                SettingsDirtyField.Theme,
                !ReferenceEquals(SelectedTheme, theme),
                () => SelectedTheme = theme);
            var language = LanguageChoices.FirstOrDefault(option => option.Value == settings.Language)
                ?? LanguageChoices[0];
            if (_pendingLanguageValue is not null
                && string.Equals(_pendingLanguageValue, language.Value, StringComparison.Ordinal))
            {
                _pendingLanguageValue = null;
            }

            if (_pendingLanguageValue is null)
            {
                ApplyExternalField(
                    SettingsDirtyField.Language,
                    !ReferenceEquals(SelectedLanguage, language),
                    () => SelectedLanguage = language);
            }
            else
            {
                _strings.SetLanguage(_pendingLanguageValue == "en"
                    ? UiLanguage.English
                    : UiLanguage.Russian);
            }

            RefreshMicrophoneChoices(
                snapshot.AvailableMicrophones,
                settings,
                preserveSelection: IsFieldDirty(SettingsDirtyField.Microphone));

            var preferences = settings.Applications.ToDictionary(
                static preference => preference.ProfileId,
                StringComparer.OrdinalIgnoreCase);
            foreach (var application in Applications)
            {
                if (_dirtyApplicationVersions.ContainsKey(application.ProfileId))
                {
                    continue;
                }

                var preference = preferences.GetValueOrDefault(application.ProfileId);
                var changed = application.BuildPreference().Policy
                    != (preference?.Policy ?? MeetingApplicationPolicy.Ask);
                application.ApplyPreference(preference);
                if (changed && IsSaving)
                {
                    MarkDirty(SettingsDirtyField.Applications, application.ProfileId);
                }
            }

            var representedIds = Applications
                .Select(static application => application.ProfileId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var preference in settings.Applications.Where(preference => !representedIds.Contains(preference.ProfileId)))
            {
                var option = new MeetingApplicationOptionViewModel(
                    preference.ProfileId,
                    preference.DisplayName,
                    preference.Policy != MeetingApplicationPolicy.Ignore,
                    _strings);
                option.ApplyPreference(preference);
                option.PropertyChanged += Application_OnPropertyChanged;
                Applications.Add(option);
                if (IsSaving)
                {
                    MarkDirty(SettingsDirtyField.Applications, option.ProfileId);
                }
            }

            ApplyTranscriptionSnapshot(settings.Transcription);
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private void ApplyTranscriptionSnapshot(RuntimeTranscriptionSettingsSnapshot settings)
    {
        OnPropertyChanged(nameof(OnlineProviderChoices));
        foreach (var snapshot in settings.Engines)
        {
            var option = TranscriptionEngineChoices.FirstOrDefault(existing => string.Equals(
                existing.EngineId,
                snapshot.EngineId,
                StringComparison.Ordinal));
            if (option is null)
            {
                TranscriptionEngineChoices.Add(new TranscriptionEngineOptionViewModel(snapshot, _strings));
            }
            else
            {
                option.Apply(snapshot);
            }
        }

        foreach (var stale in TranscriptionEngineChoices
                     .Where(option => settings.Engines.All(snapshot => !string.Equals(
                         snapshot.EngineId,
                         option.EngineId,
                         StringComparison.Ordinal)))
                     .ToArray())
        {
            TranscriptionEngineChoices.Remove(stale);
        }

        if (_transcriptionDirty)
        {
            ApplySelectedTranscriptionEngine(SelectedTranscriptionModel?.Id);
            NotifyTranscriptionPresentationChanged();
            return;
        }

        _suppressTranscriptionChanges = true;
        try
        {
            SelectedTranscriptionEngine = TranscriptionEngineChoices.FirstOrDefault(option => string.Equals(
                option.EngineId,
                settings.SelectedEngineId,
                StringComparison.Ordinal));
            AutomaticTranscriptionEnabled = settings.AutomaticEnabled;
            TranscriptionModeIndex = !settings.AutomaticEnabled ? 0
                : SelectedTranscriptionEngine?.ExecutionKind == TranscriptionExecutionKind.Local ? 1 : 2;
            RequireZeroDataRetention = settings.RequireZeroDataRetention;
            SelectedTranscriptionLanguage = TranscriptionLanguageChoices.FirstOrDefault(option => string.Equals(
                option.Value,
                settings.Language,
                StringComparison.Ordinal)) ?? TranscriptionLanguageChoices[0];
            ApplySelectedTranscriptionEngine(SelectedTranscriptionEngine?.SelectedModelId);
        }
        finally
        {
            _suppressTranscriptionChanges = false;
        }

        NotifyTranscriptionPresentationChanged();
    }

    private void ApplySelectedTranscriptionEngine(string? preferredModelId)
    {
        TranscriptionModelChoices.Clear();
        if (SelectedTranscriptionEngine is not { } engine)
        {
            _selectedTranscriptionModel = null;
            _transcriptionConsentAccepted = false;
            OnPropertyChanged(nameof(SelectedTranscriptionModel));
            OnPropertyChanged(nameof(TranscriptionConsentAccepted));
            return;
        }

        var capabilities = engine.Models.ToDictionary(static model => model.Id, StringComparer.Ordinal);
        foreach (var localModel in engine.LocalModels)
        {
            capabilities.TryAdd(
                localModel.ModelId,
                new TranscriptionModelCapability(
                    localModel.ModelId,
                    localModel.DisplayName,
                    localModel.IsRecommended));
        }

        foreach (var model in capabilities.Values)
        {
            var localModel = engine.LocalModels.FirstOrDefault(candidate => string.Equals(
                candidate.ModelId,
                model.Id,
                StringComparison.Ordinal));
            TranscriptionModelChoices.Add(new TranscriptionModelOptionViewModel(
                model,
                localModel,
                engine.ExecutionKind == TranscriptionExecutionKind.Local,
                _strings));
        }

        var selectedModel = TranscriptionModelChoices.FirstOrDefault(model => string.Equals(
                model.Id,
                preferredModelId,
                StringComparison.Ordinal))
            ?? TranscriptionModelChoices.FirstOrDefault(static model => model.IsRecommended)
            ?? TranscriptionModelChoices.FirstOrDefault();
        _selectedTranscriptionModel = selectedModel;
        _transcriptionConsentAccepted = engine.DisclosureAccepted;
        if (engine.ExecutionKind == TranscriptionExecutionKind.Local
            && !IsSelectedLocalModelReady
            && _automaticTranscriptionEnabled)
        {
            _automaticTranscriptionEnabled = false;
            OnPropertyChanged(nameof(AutomaticTranscriptionEnabled));
        }

        OnPropertyChanged(nameof(SelectedTranscriptionModel));
        OnPropertyChanged(nameof(TranscriptionConsentAccepted));
        NotifySelectedTranscriptionModelChanged();
    }

    private void NotifyTranscriptionPresentationChanged()
    {
        OnPropertyChanged(nameof(HasSelectedTranscriptionEngine));
        OnPropertyChanged(nameof(IsSelectedTranscriptionEngineLocal));
        OnPropertyChanged(nameof(ShowRemoteTranscriptionSettings));
        OnPropertyChanged(nameof(ShowLocalTranscriptionSettings));
        OnPropertyChanged(nameof(ShowLocalTranscriptionRecordingNotice));
        OnPropertyChanged(nameof(TranscriptionSettingsTitle));
        OnPropertyChanged(nameof(TranscriptionSettingsDescription));
        OnPropertyChanged(nameof(AutomaticTranscriptionDescription));
        OnPropertyChanged(nameof(CanEnableAutomaticTranscription));
        OnPropertyChanged(nameof(SelectedTranscriptionEngineHasCredential));
        OnPropertyChanged(nameof(HasTranscriptionPolicyLink));
        OnPropertyChanged(nameof(ShowTranscriptionModelDiscovery));
        OnPropertyChanged(nameof(ShowZeroDataRetentionOption));
        OnPropertyChanged(nameof(TranscriptionCredentialState));
        OnPropertyChanged(nameof(TranscriptionConsentText));
        NotifyTranscriptionCommandStates();
    }

    private void NotifySelectedTranscriptionModelChanged()
    {
        OnPropertyChanged(nameof(SelectedLocalModel));
        OnPropertyChanged(nameof(SelectedLocalResources));
        OnPropertyChanged(nameof(HasSelectedLocalModel));
        OnPropertyChanged(nameof(IsSelectedLocalModelReady));
        OnPropertyChanged(nameof(CanEnableAutomaticTranscription));
        OnPropertyChanged(nameof(ShowLocalModelProgress));
        OnPropertyChanged(nameof(ShowInstallLocalModelAction));
        OnPropertyChanged(nameof(InstallLocalModelActionText));
        OnPropertyChanged(nameof(ShowCancelLocalModelInstallAction));
        OnPropertyChanged(nameof(ShowRemoveLocalModelAction));
        OnPropertyChanged(nameof(IsLocalModelProgressIndeterminate));
        OnPropertyChanged(nameof(LocalModelProgressPercent));
        OnPropertyChanged(nameof(SelectedLocalModelDescription));
        OnPropertyChanged(nameof(LocalModelStatusText));
        OnPropertyChanged(nameof(ShowLocalResourceWarning));
        OnPropertyChanged(nameof(LocalResourceWarningText));
        OnPropertyChanged(nameof(AutomaticTranscriptionDescription));
        NotifyTranscriptionCommandStates();
    }

    private void NotifyTranscriptionCommandStates()
    {
        if (!_transcriptionCommandsInitialized)
        {
            return;
        }

        SaveTranscriptionCredentialCommand.NotifyCanExecuteChanged();
        DeleteTranscriptionCredentialCommand.NotifyCanExecuteChanged();
        DiscoverTranscriptionModelsCommand.NotifyCanExecuteChanged();
        SaveTranscriptionSettingsCommand.NotifyCanExecuteChanged();
        OpenTranscriptionPolicyCommand.NotifyCanExecuteChanged();
        InstallLocalModelCommand.NotifyCanExecuteChanged();
        CancelLocalModelInstallCommand.NotifyCanExecuteChanged();
        RemoveLocalModelCommand.NotifyCanExecuteChanged();
    }

    private string LocalizeLocalModelStatus(RuntimeLocalModelSnapshot? model)
    {
        if (model is null)
        {
            return string.Empty;
        }

        if (model.State == RuntimeLocalModelState.Failed)
        {
            return _strings.Get(MapLocalModelCommandError(
                model.StableErrorCode ?? string.Empty,
                "String.Transcription.LocalModel.State.Failed"));
        }

        return model.State switch
        {
            RuntimeLocalModelState.NotInstalled =>
                _strings.Get("String.Transcription.LocalModel.State.NotInstalled"),
            RuntimeLocalModelState.Downloading => _strings.Format(
                "String.Transcription.LocalModel.State.Downloading.Format",
                Math.Clamp(model.Progress * 100, 0, 100)),
            RuntimeLocalModelState.Verifying =>
                _strings.Get("String.Transcription.LocalModel.State.Verifying"),
            RuntimeLocalModelState.Installed when model.IsVerified
                && SelectedTranscriptionModel?.InstalledSizeLabel is { } installedSize =>
                _strings.Format(
                    "String.Transcription.LocalModel.State.ReadyWithSize.Format",
                    installedSize),
            RuntimeLocalModelState.Installed when model.IsVerified =>
                _strings.Get("String.Transcription.LocalModel.State.Ready"),
            RuntimeLocalModelState.Installed =>
                _strings.Get("String.Transcription.LocalModel.State.VerificationRequired"),
            RuntimeLocalModelState.Removing =>
                _strings.Get("String.Transcription.LocalModel.State.Removing"),
            _ => _strings.Get("String.Transcription.LocalModel.State.Failed")
        };
    }

    private bool HasInsufficientLocalMemory => SelectedLocalResources is
    {
        AvailableMemoryBytes: { } available,
        RequiredMemoryBytes: { } required
    } && available < required;

    private bool HasInsufficientLocalDisk => SelectedLocalResources is
    {
        AvailableDiskBytes: { } available,
        RequiredDiskBytes: { } required
    } && available < required;

    private void ApplyCapability(RuntimeCapabilitySnapshot capability)
    {
        _capability = capability;
        OnPropertyChanged(nameof(HasMicrophoneCapabilityNotice));
        OnPropertyChanged(nameof(MicrophoneCapabilityMessage));
    }

    private void RefreshMicrophoneChoices(
        IReadOnlyList<RuntimeMicrophoneSnapshot> microphones,
        RuntimeUserSettingsSnapshot settings,
        bool preserveSelection)
    {
        var selectedDeviceId = preserveSelection ? SelectedMicrophone.Id : settings.MicrophoneDeviceId;
        var followSystemDefault = preserveSelection
            ? SelectedMicrophone.Id is null
            : settings.FollowSystemDefaultMicrophone;
        var current = MicrophoneChoices
            .Skip(1)
            .Where(static microphone => !microphone.IsUnavailable)
            .Select(static microphone => (microphone.Id, microphone.DisplayName))
            .ToArray();
        var available = microphones
            .Select(static microphone => ((string?)microphone.Id, microphone.DisplayName))
            .ToArray();
        if (!current.SequenceEqual(available))
        {
            foreach (var microphone in MicrophoneChoices.Skip(1).ToArray())
            {
                MicrophoneChoices.Remove(microphone);
                microphone.Dispose();
            }

            foreach (var microphone in microphones)
            {
                MicrophoneChoices.Add(new MicrophoneOptionViewModel(
                    microphone.Id,
                    microphone.DisplayName,
                    _strings));
            }

        }

        foreach (var unavailable in MicrophoneChoices
                     .Where(static option => option.IsUnavailable)
                     .Where(option => followSystemDefault
                         || !string.Equals(option.Id, selectedDeviceId, StringComparison.Ordinal)
                         || microphones.Any(microphone => string.Equals(
                             microphone.Id,
                             option.Id,
                             StringComparison.Ordinal)))
                     .ToArray())
        {
            MicrophoneChoices.Remove(unavailable);
            unavailable.Dispose();
        }

        SetupViewModel.EnsureUnavailableMicrophoneChoice(
            MicrophoneChoices,
            followSystemDefault ? null : selectedDeviceId,
            _strings);

        var selected = followSystemDefault
            ? MicrophoneChoices[0]
            : MicrophoneChoices.FirstOrDefault(option => option.Id == selectedDeviceId)
                ?? MicrophoneChoices[0];
        var changed = !string.Equals(SelectedMicrophone.Id, selected.Id, StringComparison.Ordinal);
        SelectedMicrophone = selected;
        if (changed && IsSaving && !preserveSelection)
        {
            MarkDirty(SettingsDirtyField.Microphone);
        }
    }

    private void Application_OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MeetingApplicationOptionViewModel.IsMonitored))
        {
            ScheduleSave(
                SettingsDirtyField.Applications,
                (sender as MeetingApplicationOptionViewModel)?.ProfileId);
        }
    }

    private bool HasDirtyState => _dirtyFieldVersions.Count > 0 || _dirtyApplicationVersions.Count > 0;

    private bool IsFieldDirty(SettingsDirtyField field) => _dirtyFieldVersions.ContainsKey(field);

    private void ApplyExternalField(SettingsDirtyField field, bool changed, Action apply)
    {
        if (!changed || IsFieldDirty(field))
        {
            return;
        }

        apply();
        if (IsSaving)
        {
            MarkDirty(field);
        }
    }

    private void MarkDirty(SettingsDirtyField field, string? applicationId = null)
    {
        var generation = ++_editGeneration;
        if (field == SettingsDirtyField.Applications && !string.IsNullOrWhiteSpace(applicationId))
        {
            _dirtyApplicationVersions[applicationId] = generation;
        }
        else
        {
            _dirtyFieldVersions[field] = generation;
        }

        _isDirty = true;
    }

    private void ClearSavedDirtyState(long savedGeneration)
    {
        foreach (var field in _dirtyFieldVersions
                     .Where(entry => entry.Value <= savedGeneration)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _dirtyFieldVersions.Remove(field);
        }

        foreach (var applicationId in _dirtyApplicationVersions
                     .Where(entry => entry.Value <= savedGeneration)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _dirtyApplicationVersions.Remove(applicationId);
        }
    }

    private void SetStatusMessage(string? resourceKey)
    {
        _statusMessageKey = resourceKey;
        StatusMessage = resourceKey is null ? null : _strings.Get(resourceKey);
    }

    private void SetErrorMessage(string? resourceKey)
    {
        _errorMessageKey = resourceKey;
        ErrorMessage = resourceKey is null ? null : _strings.Get(resourceKey);
    }

    private void SetTranscriptionStatus(string? resourceKey)
    {
        _transcriptionStatusMessageKey = resourceKey;
        TranscriptionStatusMessage = resourceKey is null ? null : _strings.Get(resourceKey);
        if (resourceKey is not null)
        {
            SetTranscriptionError(null);
        }
    }

    private void SetTranscriptionError(string? resourceKey)
    {
        _transcriptionErrorMessageKey = resourceKey;
        TranscriptionErrorMessage = resourceKey is null ? null : _strings.Get(resourceKey);
        if (resourceKey is not null)
        {
            _transcriptionStatusMessageKey = null;
            TranscriptionStatusMessage = null;
        }
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var engine in TranscriptionEngineChoices)
        {
            engine.RefreshLocalization();
        }

        foreach (var model in TranscriptionModelChoices)
        {
            model.RefreshLocalization();
        }

        OnPropertyChanged(nameof(RecordingsFolderLabel));
        OnPropertyChanged(nameof(MicrophoneCapabilityMessage));
        OnPropertyChanged(nameof(MicrophoneCapabilityActionText));
        OnPropertyChanged(nameof(TranscriptionSettingsTitle));
        OnPropertyChanged(nameof(TranscriptionSettingsDescription));
        OnPropertyChanged(nameof(AutomaticTranscriptionDescription));
        OnPropertyChanged(nameof(SelectedLocalModelDescription));
        OnPropertyChanged(nameof(LocalModelStatusText));
        OnPropertyChanged(nameof(LocalResourceWarningText));
        OnPropertyChanged(nameof(InstallLocalModelActionText));
        OnPropertyChanged(nameof(TranscriptionCredentialState));
        OnPropertyChanged(nameof(TranscriptionConsentText));
        StatusMessage = _statusMessageKey is null ? null : _strings.Get(_statusMessageKey);
        ErrorMessage = _errorMessageKey is null ? null : _strings.Get(_errorMessageKey);
        TranscriptionStatusMessage = _transcriptionStatusMessageKey is null
            ? null
            : _strings.Get(_transcriptionStatusMessageKey);
        TranscriptionErrorMessage = _transcriptionErrorMessageKey is null
            ? null
            : _strings.Get(_transcriptionErrorMessageKey);
    }
}

internal enum SettingsDirtyField
{
    Service,
    Autostart,
    Notifications,
    Microphone,
    RecordingsFolder,
    Theme,
    Language,
    Applications
}

/// <summary>
/// Mutable presentation wrapper for credential-free engine capabilities.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed class TranscriptionEngineOptionViewModel : ObservableObject
{
    private RuntimeTranscriptionEngineSnapshot _snapshot;
    private readonly ILocalizationService _strings;

    public TranscriptionEngineOptionViewModel(
        RuntimeTranscriptionEngineSnapshot snapshot,
        ILocalizationService strings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(strings);
        _snapshot = snapshot;
        _strings = strings;
    }

    public string EngineId => _snapshot.EngineId;

    public string DisplayName => ExecutionKind == TranscriptionExecutionKind.Local
        ? _strings.Get("String.Transcription.Settings.Local.Engine")
        : _snapshot.DisplayName;

    public TranscriptionExecutionKind ExecutionKind => _snapshot.ExecutionKind;

    public bool RequiresNetwork => _snapshot.RequiresNetwork;

    public Uri? PolicyUri => _snapshot.PolicyUri;

    public bool HasCredential => _snapshot.HasCredential;

    public string? SelectedModelId => _snapshot.SelectedModelId;

    public IReadOnlyList<TranscriptionModelCapability> Models => _snapshot.Models;

    public IReadOnlyList<RuntimeLocalModelSnapshot> LocalModels => _snapshot.LocalModels;

    public RuntimeLocalResourceSnapshot? LocalResources => _snapshot.LocalResources;

    public bool SupportsModelDiscovery => _snapshot.SupportsModelDiscovery;

    public bool DisclosureAccepted => _snapshot.DisclosureAccepted;

    public void RefreshLocalization() => OnPropertyChanged(nameof(DisplayName));

    public void SetCredentialPresence(bool hasCredential) =>
        Apply(_snapshot with { HasCredential = hasCredential });

    public void SetModels(IReadOnlyList<TranscriptionModelCapability> models) =>
        Apply(_snapshot with { Models = models });

    public void Apply(RuntimeTranscriptionEngineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.EngineId, EngineId, StringComparison.Ordinal))
        {
            throw new ArgumentException("An engine option cannot change identity.", nameof(snapshot));
        }

        _snapshot = snapshot;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ExecutionKind));
        OnPropertyChanged(nameof(RequiresNetwork));
        OnPropertyChanged(nameof(PolicyUri));
        OnPropertyChanged(nameof(HasCredential));
        OnPropertyChanged(nameof(SelectedModelId));
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(LocalModels));
        OnPropertyChanged(nameof(LocalResources));
        OnPropertyChanged(nameof(SupportsModelDiscovery));
        OnPropertyChanged(nameof(DisclosureAccepted));
    }
}

/// <summary>
/// Localized provider-aware model choice. Local presets keep lifecycle data while remote
/// providers retain their discovered display names.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed class TranscriptionModelOptionViewModel : ObservableObject
{
    private readonly TranscriptionModelCapability _capability;
    private readonly bool _isLocal;
    private readonly ILocalizationService _strings;

    public TranscriptionModelOptionViewModel(
        TranscriptionModelCapability capability,
        RuntimeLocalModelSnapshot? localModel,
        bool isLocal,
        ILocalizationService strings)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(strings);
        _capability = capability;
        LocalModel = localModel;
        _isLocal = isLocal;
        _strings = strings;
    }

    public string Id => _capability.Id;

    public bool IsRecommended => LocalModel?.IsRecommended ?? _capability.IsRecommended;

    public RuntimeLocalModelSnapshot? LocalModel { get; }

    public string DisplayName => _isLocal ? LocalizedPresetName : _capability.DisplayName;

    public string SelectorLabel
    {
        get
        {
            if (!_isLocal || LocalModel is null)
            {
                return _capability.DisplayName;
            }

            return _strings.Format(
                IsRecommended
                    ? "String.Transcription.LocalModel.Option.Recommended.Format"
                    : "String.Transcription.LocalModel.Option.Format",
                LocalizedPresetName,
                DownloadSizeLabel);
        }
    }

    public string Description => !_isLocal
        ? string.Empty
        : _strings.Get(Id switch
        {
            "base" => "String.Transcription.LocalModel.Preset.Compact.Description",
            "small" => "String.Transcription.LocalModel.Preset.Balanced.Description",
            "medium" => "String.Transcription.LocalModel.Preset.Accurate.Description",
            _ => "String.Transcription.LocalModel.Preset.Generic.Description"
        });

    public string? InstalledSizeLabel => LocalModel?.InstalledSizeBytes is > 0 and var installedSize
        ? FormatSize(installedSize)
        : null;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SelectorLabel));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(InstalledSizeLabel));
    }

    private string LocalizedPresetName => _strings.Get(Id switch
    {
        "base" => "String.Transcription.LocalModel.Preset.Compact",
        "small" => "String.Transcription.LocalModel.Preset.Balanced",
        "medium" => "String.Transcription.LocalModel.Preset.Accurate",
        _ => "String.Transcription.LocalModel.Preset.Generic"
    });

    private string DownloadSizeLabel => Id switch
    {
        "base" => _strings.Get("String.Transcription.LocalModel.Size.Base"),
        "small" => _strings.Get("String.Transcription.LocalModel.Size.Small"),
        "medium" => _strings.Get("String.Transcription.LocalModel.Size.Medium"),
        _ => FormatSize(LocalModel?.DownloadSizeBytes ?? 0)
    };

    private string FormatSize(long bytes)
    {
        const double bytesPerGibibyte = 1024d * 1024d * 1024d;
        const double bytesPerMebibyte = 1024d * 1024d;
        return bytes >= bytesPerGibibyte
            ? _strings.Format(
                "String.Transcription.LocalModel.Size.Gigabytes.Format",
                bytes / bytesPerGibibyte)
            : _strings.Format(
                "String.Transcription.LocalModel.Size.Megabytes.Format",
                bytes / bytesPerMebibyte);
    }
}

/// <summary>
/// Localized value/label pair for compact settings selectors.
/// </summary>
public sealed class SettingChoiceOptionViewModel : ObservableObject, IDisposable
{
    private readonly string _labelKey;
    private readonly ILocalizationService _strings;
    private bool _disposed;

    public SettingChoiceOptionViewModel(string value, string labelKey, ILocalizationService strings)
    {
        Value = value;
        _labelKey = labelKey;
        _strings = strings;
        _strings.LanguageChanged += HandleLanguageChanged;
    }

    public string Value { get; }

    public string Label => _strings.Get(_labelKey);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= HandleLanguageChanged;
    }

    private void HandleLanguageChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}
