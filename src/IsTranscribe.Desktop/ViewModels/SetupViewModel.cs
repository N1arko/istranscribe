using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Desktop.ViewModels;

/// <summary>
/// One-surface first-run setup with safe release defaults.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class SetupViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationRuntime _runtime;
    private readonly ILocalizationService _strings;
    private readonly RuntimeUserSettingsSnapshot _initialSettings;
    private bool _serviceEnabled;
    private bool _autostart;
    private bool _notifications;
    private string? _recordingsFolder;
    private MicrophoneOptionViewModel _selectedMicrophone;
    private bool _isBusy;
    private bool _isCheckingCapabilities;
    private RuntimeCapabilitySnapshot _capability;
    private RuntimeCapabilityIssue? _limitedAudioConfirmationIssue;
    private string? _errorMessage;
    private string? _errorMessageKey;
    private bool _disposed;

    public SetupViewModel(
        IApplicationRuntime runtime,
        ILocalizationService strings,
        RuntimeUserSettingsSnapshot settings,
        IReadOnlyList<RuntimeMicrophoneSnapshot>? availableMicrophones = null,
        MeetingProfileRegistry? profiles = null,
        IPermissionService? permissionService = null)
    {
        _runtime = runtime;
        _strings = strings;
        _initialSettings = settings;
        _serviceEnabled = settings.ServiceEnabled;
        _autostart = settings.Autostart;
        _notifications = settings.Notifications;
        _recordingsFolder = settings.RecordingsFolder;
        _capability = runtime.Snapshot.Capability;

        MicrophoneChoices.Add(MicrophoneOptionViewModel.SystemDefault(strings));
        foreach (var microphone in availableMicrophones ?? [])
        {
            MicrophoneChoices.Add(new MicrophoneOptionViewModel(microphone.Id, microphone.DisplayName, strings));
        }

        EnsureUnavailableMicrophoneChoice(
            MicrophoneChoices,
            settings.FollowSystemDefaultMicrophone ? null : settings.MicrophoneDeviceId,
            strings);

        _selectedMicrophone = settings.FollowSystemDefaultMicrophone
            ? MicrophoneChoices[0]
            : MicrophoneChoices.FirstOrDefault(option => option.Id == settings.MicrophoneDeviceId)
                ?? MicrophoneChoices[0];

        var registry = profiles ?? new MeetingProfileRegistry();
        var preferences = settings.Applications.ToDictionary(
            static preference => preference.ProfileId,
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in registry.Profiles)
        {
            var isMonitored = !preferences.TryGetValue(profile.Id, out var preference)
                || preference.Policy == MeetingApplicationPolicy.Ask;
            Applications.Add(new MeetingApplicationOptionViewModel(
                profile.Id,
                profile.DisplayName,
                isMonitored,
                strings));
        }

        foreach (var preference in settings.Applications.Where(preference => registry.FindById(preference.ProfileId) is null))
        {
            var option = new MeetingApplicationOptionViewModel(
                preference.ProfileId,
                preference.DisplayName,
                preference.Policy != MeetingApplicationPolicy.Ignore,
                strings);
            option.ApplyPreference(preference);
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

        CompleteCommand = new AsyncRelayCommand(CompleteAsync, CanComplete);
        RetryCapabilitiesCommand = new AsyncRelayCommand(
            RetryCapabilitiesAsync,
            () => !IsBusy && !IsCheckingCapabilities);
        ChooseFolderCommand = new RelayCommand(() => FolderPickerRequested?.Invoke(this, EventArgs.Empty));
        _strings.LanguageChanged += HandleLanguageChanged;
        _runtime.SnapshotChanged += Runtime_OnSnapshotChanged;
    }

    public event EventHandler? Completed;

    public event EventHandler? FolderPickerRequested;

    public ObservableCollection<MeetingApplicationOptionViewModel> Applications { get; } = [];

    public ObservableCollection<MicrophoneOptionViewModel> MicrophoneChoices { get; } = [];

    public ObservableCollection<PlatformPermissionOptionViewModel> PlatformPermissions { get; } = [];

    public bool HasPlatformPermissions => PlatformPermissions.Count > 0;

    public IAsyncRelayCommand CompleteCommand { get; }

    public IAsyncRelayCommand RetryCapabilitiesCommand { get; }

    public IRelayCommand ChooseFolderCommand { get; }

    public bool ServiceEnabled
    {
        get => _serviceEnabled;
        set
        {
            if (SetProperty(ref _serviceEnabled, value))
            {
                ResetLimitedAudioConfirmation();
                CompleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool Autostart
    {
        get => _autostart;
        set => SetProperty(ref _autostart, value);
    }

    public bool Notifications
    {
        get => _notifications;
        set => SetProperty(ref _notifications, value);
    }

    public MicrophoneOptionViewModel SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (SetProperty(ref _selectedMicrophone, value))
            {
                ResetLimitedAudioConfirmation();
            }
        }
    }

    public string? RecordingsFolder
    {
        get => _recordingsFolder;
        private set
        {
            if (SetProperty(ref _recordingsFolder, value))
            {
                OnPropertyChanged(nameof(RecordingsFolderLabel));
            }
        }
    }

    public string RecordingsFolderLabel => string.IsNullOrWhiteSpace(RecordingsFolder)
        ? _strings.Get("String.Setup.Folder.AppOwned")
        : RecordingsFolder;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CompleteActionText));
                CompleteCommand.NotifyCanExecuteChanged();
                RetryCapabilitiesCommand.NotifyCanExecuteChanged();
            }
        }
    }

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

    public bool IsCheckingCapabilities
    {
        get => _isCheckingCapabilities;
        private set
        {
            if (SetProperty(ref _isCheckingCapabilities, value))
            {
                OnPropertyChanged(nameof(CapabilityActionText));
                CompleteCommand.NotifyCanExecuteChanged();
                RetryCapabilitiesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCapabilityNotice => _capability.State != RuntimeCapabilityState.Full
        || _capability.Issue != RuntimeCapabilityIssue.None;

    public string CapabilityMessage => _strings.Get(_capability.Issue switch
    {
        RuntimeCapabilityIssue.PlatformBlocked => "String.Setup.Capability.Blocked",
        RuntimeCapabilityIssue.ProcessOutputCaptureUnavailable => "String.Setup.Capability.Degraded",
        RuntimeCapabilityIssue.NoActiveAudioEndpoints => "String.Setup.Capability.AudioMissing",
        RuntimeCapabilityIssue.NoActiveOutput => "String.Setup.Capability.OutputMissing",
        RuntimeCapabilityIssue.NoActiveMicrophone => "String.Setup.Capability.MicrophoneMissing",
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable => "String.Setup.Capability.ConfiguredMicrophoneUnavailable",
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable => "String.Setup.Capability.MicrophoneCaptureUnavailable",
        RuntimeCapabilityIssue.OutputCaptureUnavailable => "String.Setup.Capability.OutputCaptureUnavailable",
        _ => "String.Setup.Capability.Checking"
    });

    public string CapabilityActionText => _strings.Get(IsCheckingCapabilities
        ? "String.Setup.CheckingPermissions"
        : "String.Setup.RetryPermissions");

    public string CompleteActionText => _strings.Get(IsBusy
        ? "String.Setup.CheckingPermissions"
        : _limitedAudioConfirmationIssue switch
        {
            RuntimeCapabilityIssue.NoActiveMicrophone
                or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
                or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable =>
                "String.Setup.ContinueWithoutMicrophone",
            RuntimeCapabilityIssue.NoActiveOutput
                or RuntimeCapabilityIssue.OutputCaptureUnavailable =>
                "String.Setup.ContinueWithoutMeetingAudio",
            _ => "String.Setup.Complete"
        });

    public void SetRecordingsFolder(string? path) =>
        RecordingsFolder = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= HandleLanguageChanged;
        _runtime.SnapshotChanged -= Runtime_OnSnapshotChanged;
        foreach (var application in Applications)
        {
            application.Dispose();
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

    private async Task CompleteAsync()
    {
        IsBusy = true;
        SetErrorMessage(null);
        try
        {
            if (ServiceEnabled && _limitedAudioConfirmationIssue is null)
            {
                await _runtime.RefreshCapabilitiesAsync(CancellationToken.None);
                ApplyRuntimeSnapshot(_runtime.Snapshot);
                if (_capability.State == RuntimeCapabilityState.Blocked)
                {
                    return;
                }

                if (IsLimitedAudioIssue(_capability.Issue))
                {
                    SetLimitedAudioConfirmation(_capability.Issue);
                    return;
                }
            }

            await _runtime.CompleteOnboardingAsync(BuildUpdate(), CancellationToken.None);
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            SetErrorMessage("String.Error.SettingsSave");
        }
        finally
        {
            IsBusy = false;
        }
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

    private async Task RefreshPlatformPermissionsAsync()
    {
        foreach (var permission in PlatformPermissions)
        {
            await permission.RefreshAsync(CancellationToken.None);
        }
    }

    private RuntimeUserSettingsUpdate BuildUpdate() => new(
        ServiceEnabled,
        Autostart,
        Notifications,
        _initialSettings.Language,
        _initialSettings.Theme,
        SelectedMicrophone.Id,
        SelectedMicrophone.Id is null,
        RecordingsFolder,
        Applications.Select(static application => application.BuildPreference()).ToArray());

    private bool CanComplete() => !IsBusy
        && !IsCheckingCapabilities
        && (!ServiceEnabled || _capability.State != RuntimeCapabilityState.Blocked);

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
        ApplyCapability(snapshot.Capability);
        var current = MicrophoneChoices
            .Skip(1)
            .Where(static microphone => !microphone.IsUnavailable)
            .Select(static microphone => (microphone.Id, microphone.DisplayName))
            .ToArray();
        var available = snapshot.AvailableMicrophones
            .Select(static microphone => ((string?)microphone.Id, microphone.DisplayName))
            .ToArray();
        if (current.SequenceEqual(available))
        {
            return;
        }

        var selectedId = SelectedMicrophone.Id;
        foreach (var microphone in MicrophoneChoices.Skip(1).ToArray())
        {
            MicrophoneChoices.Remove(microphone);
            microphone.Dispose();
        }

        foreach (var microphone in snapshot.AvailableMicrophones)
        {
            MicrophoneChoices.Add(new MicrophoneOptionViewModel(
                microphone.Id,
                microphone.DisplayName,
                _strings));
        }


        EnsureUnavailableMicrophoneChoice(MicrophoneChoices, selectedId, _strings);

        SelectedMicrophone = selectedId is null
            ? MicrophoneChoices[0]
            : MicrophoneChoices.FirstOrDefault(option => option.Id == selectedId)
                ?? MicrophoneChoices[0];
    }

    private void ApplyCapability(RuntimeCapabilitySnapshot capability)
    {
        _capability = capability;
        if (_limitedAudioConfirmationIssue is { } confirmationIssue
            && (confirmationIssue != capability.Issue || !IsLimitedAudioIssue(capability.Issue)))
        {
            ResetLimitedAudioConfirmation();
        }

        OnPropertyChanged(nameof(HasCapabilityNotice));
        OnPropertyChanged(nameof(CapabilityMessage));
        CompleteCommand.NotifyCanExecuteChanged();
    }

    private void SetLimitedAudioConfirmation(RuntimeCapabilityIssue issue)
    {
        _limitedAudioConfirmationIssue = issue;
        OnPropertyChanged(nameof(CompleteActionText));
    }

    private void ResetLimitedAudioConfirmation()
    {
        if (_limitedAudioConfirmationIssue is null)
        {
            return;
        }

        _limitedAudioConfirmationIssue = null;
        OnPropertyChanged(nameof(CompleteActionText));
    }

    private static bool IsLimitedAudioIssue(RuntimeCapabilityIssue issue) =>
        issue is RuntimeCapabilityIssue.NoActiveOutput
            or RuntimeCapabilityIssue.NoActiveMicrophone
            or RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable
            or RuntimeCapabilityIssue.MicrophoneCaptureUnavailable
            or RuntimeCapabilityIssue.OutputCaptureUnavailable;

    internal static void EnsureUnavailableMicrophoneChoice(
        ICollection<MicrophoneOptionViewModel> choices,
        string? configuredId,
        ILocalizationService strings)
    {
        if (string.IsNullOrWhiteSpace(configuredId)
            || choices.Any(option => string.Equals(option.Id, configuredId, StringComparison.Ordinal)))
        {
            return;
        }

        choices.Add(MicrophoneOptionViewModel.Unavailable(configuredId, strings));
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(RecordingsFolderLabel));
        OnPropertyChanged(nameof(CapabilityMessage));
        OnPropertyChanged(nameof(CapabilityActionText));
        OnPropertyChanged(nameof(CompleteActionText));
        ErrorMessage = _errorMessageKey is null ? null : _strings.Get(_errorMessageKey);
    }

    private void SetErrorMessage(string? resourceKey)
    {
        _errorMessageKey = resourceKey;
        ErrorMessage = resourceKey is null ? null : _strings.Get(resourceKey);
    }
}

/// <summary>
/// Friendly microphone option with a stable system-default choice.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed class MicrophoneOptionViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _strings;
    private readonly bool _isSystemDefault;
    private readonly bool _isUnavailable;
    private bool _disposed;

    public MicrophoneOptionViewModel(
        string? id,
        string displayName,
        ILocalizationService strings,
        bool isSystemDefault = false,
        bool isUnavailable = false)
    {
        Id = id;
        DisplayName = displayName;
        _strings = strings;
        _isSystemDefault = isSystemDefault;
        _isUnavailable = isUnavailable;
        _strings.LanguageChanged += HandleLanguageChanged;
    }

    public string? Id { get; }

    public string DisplayName { get; }

    public bool IsUnavailable => _isUnavailable;

    public string Label => _isSystemDefault
        ? _strings.Get("String.Setup.Microphone.SystemDefault")
        : _isUnavailable
            ? _strings.Get("String.Setup.Microphone.Unavailable")
            : DisplayName;

    public static MicrophoneOptionViewModel SystemDefault(ILocalizationService strings) =>
        new(null, string.Empty, strings, isSystemDefault: true);

    public static MicrophoneOptionViewModel Unavailable(string id, ILocalizationService strings) =>
        new(id, string.Empty, strings, isUnavailable: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= HandleLanguageChanged;
    }

    public override string ToString() => Label;

    private void HandleLanguageChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// One editable meeting-application policy row shared by setup and settings.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed class MeetingApplicationOptionViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _strings;
    private bool _isMonitored;
    private bool _disposed;
    private MeetingApplicationPolicy _monitoredPolicy;

    public MeetingApplicationOptionViewModel(
        string profileId,
        string fallbackDisplayName,
        bool isMonitored,
        ILocalizationService strings)
    {
        ProfileId = profileId;
        FallbackDisplayName = fallbackDisplayName;
        _isMonitored = isMonitored;
        _monitoredPolicy = MeetingApplicationPolicy.Ask;
        _strings = strings;
        _strings.LanguageChanged += HandleLanguageChanged;
    }

    public string ProfileId { get; }

    public string FallbackDisplayName { get; }

    public string DisplayName
        => MeetingSourceLabelLocalizer.Localize(_strings, FallbackDisplayName, ProfileId);

    public bool IsBrowserFallback
        => ProfileId.Equals("generic-browser", StringComparison.OrdinalIgnoreCase);

    public bool IsMonitored
    {
        get => _isMonitored;
        set => SetProperty(ref _isMonitored, value);
    }

    public MeetingApplicationPreference BuildPreference() => new(
        ProfileId,
        FallbackDisplayName,
        IsMonitored ? _monitoredPolicy : MeetingApplicationPolicy.Ignore);

    public void ApplyPreference(MeetingApplicationPreference? preference)
    {
        if (preference is null)
        {
            _monitoredPolicy = MeetingApplicationPolicy.Ask;
            IsMonitored = true;
            return;
        }

        if (preference.Policy != MeetingApplicationPolicy.Ignore)
        {
            _monitoredPolicy = preference.Policy;
        }

        IsMonitored = preference.Policy != MeetingApplicationPolicy.Ignore;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strings.LanguageChanged -= HandleLanguageChanged;
    }

    private void HandleLanguageChanged(object? sender, EventArgs args) =>
        OnPropertyChanged(nameof(DisplayName));
}

/// <summary>
/// One contextual, user-initiated platform permission row used during first run.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions
/// </remarks>
public sealed class PlatformPermissionOptionViewModel : ObservableObject, IDisposable
{
    private readonly string _titleKey;
    private readonly IPermissionService _service;
    private readonly ILocalizationService _strings;
    private PlatformCapabilityState _state = PlatformCapabilityState.NotRequested;
    private bool _busy;
    private bool _disposed;

    public PlatformPermissionOptionViewModel(
        string id,
        string titleKey,
        IPermissionService service,
        ILocalizationService strings)
    {
        Id = id;
        _titleKey = titleKey;
        _service = service;
        _strings = strings;
        RequestCommand = new AsyncRelayCommand(RequestAsync, () => !Busy && State != PlatformCapabilityState.Available);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => !Busy);
        _strings.LanguageChanged += OnLanguageChanged;
    }

    public string Id { get; }

    public IAsyncRelayCommand RequestCommand { get; }

    public IAsyncRelayCommand OpenSettingsCommand { get; }

    public string Title => _strings.Get(_titleKey);

    public PlatformCapabilityState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(IsGranted));
                OnPropertyChanged(nameof(ShowOpenSettings));
                RequestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StateLabel => _strings.Get(State switch
    {
        PlatformCapabilityState.Available => "String.Permission.State.Granted",
        PlatformCapabilityState.PermissionRequired => "String.Permission.State.Denied",
        PlatformCapabilityState.NeedsRestart => "String.Permission.State.NeedsRestart",
        _ => "String.Permission.State.NotRequested"
    });

    public string RequestActionText => _strings.Get("String.Permission.Action.Request");

    public string OpenSettingsActionText => _strings.Get("String.Permission.Action.OpenSettings");

    public bool IsGranted => State == PlatformCapabilityState.Available;

    public bool ShowOpenSettings => State is PlatformCapabilityState.PermissionRequired or PlatformCapabilityState.NeedsRestart;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RequestCommand.NotifyCanExecuteChanged();
                OpenSettingsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var capability = await _service.GetStatusAsync(Id, cancellationToken);
        State = capability.State;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _strings.LanguageChanged -= OnLanguageChanged;
    }

    private async Task RequestAsync()
    {
        Busy = true;
        try
        {
            State = (await _service.RequestAsync(Id, CancellationToken.None)).State;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        Busy = true;
        try
        {
            await _service.OpenSystemSettingsAsync(Id, CancellationToken.None);
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(RequestActionText));
        OnPropertyChanged(nameof(OpenSettingsActionText));
    }
}
