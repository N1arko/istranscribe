using System.Globalization;
using Avalonia;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.Services;
using IsTranscribe.Desktop.Theming;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// </remarks>
internal sealed class FakeApplicationRuntime(ApplicationRuntimeSnapshot snapshot) : IApplicationRuntime
{
    public event EventHandler<ApplicationRuntimeSnapshot>? SnapshotChanged;

    public ApplicationRuntimeSnapshot Snapshot { get; private set; } = snapshot;

    public int InitializeCalls { get; private set; }

    public List<bool> ServiceEnabledUpdates { get; } = [];

    public List<RuntimeUserSettingsUpdate> CompletedOnboardingUpdates { get; } = [];

    public List<RuntimeUserSettingsUpdate> SettingsUpdates { get; } = [];

    public List<RuntimeTranscriptionSettingsUpdate> TranscriptionSettingsUpdates { get; } = [];

    public List<(string EngineId, string Credential)> SavedTranscriptionCredentials { get; } = [];

    public List<string> DeletedTranscriptionCredentials { get; } = [];

    public List<string> DiscoveredTranscriptionModelEngines { get; } = [];

    public IReadOnlyList<TranscriptionModelCapability> TranscriptionModelDiscoveryResult { get; set; } = [];

    public List<(string EngineId, string ModelId)> InstalledTranscriptionModels { get; } = [];

    public List<(string EngineId, string ModelId)> CancelledTranscriptionModelInstalls { get; } = [];

    public List<(string EngineId, string ModelId)> RemovedTranscriptionModels { get; } = [];

    public List<Guid> ConfirmedLocalTranscriptionPowerOverrides { get; } = [];

    public List<(Guid SessionId, bool ReplaceExisting)> TranscriptionRequests { get; } = [];

    public List<Guid> CancelledTranscriptions { get; } = [];

    public List<Guid> RetriedTranscriptions { get; } = [];

    public List<(string CandidateId, MeetingPromptUserAction Action)> PromptResolutions { get; } = [];

    public Func<string, MeetingPromptUserAction, CancellationToken, ValueTask>?
    PromptResolutionHandler
    { get; set; }

    public int ManualRecordingCalls { get; private set; }

    public int PauseOrResumeCalls { get; private set; }

    public int FinishRecordingCalls { get; private set; }

    public int DiscardRecordingCalls { get; private set; }

    public int RefreshCapabilitiesCalls { get; private set; }

    public ApplicationRuntimeSnapshot? SnapshotAfterCapabilitiesRefresh { get; set; }

    public List<(Guid SessionId, bool DeleteAudioFile)> RemovedRecordings { get; } = [];

    public List<(Guid SessionId, string DisplayTitle)> RenamedRecordings { get; } = [];

    public List<Guid> AcknowledgedAttentionSessions { get; } = [];

    public TaskCompletionSource<RuntimeUserSettingsUpdate> NextSettingsUpdate { get; private set; } =
        NewSettingsUpdateSource();

    public TaskCompletionSource<bool>? SettingsUpdateEntered { get; set; }

    public TaskCompletionSource<bool>? ContinueSettingsUpdate { get; set; }

    public Exception? ActionFailure { get; set; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCalls++;
        ThrowIfConfigured();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetServiceEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        ServiceEnabledUpdates.Add(enabled);
        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        RefreshCapabilitiesCalls++;
        if (SnapshotAfterCapabilitiesRefresh is { } refreshed)
        {
            Publish(refreshed);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteOnboardingAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        CompletedOnboardingUpdates.Add(settings);
        return ValueTask.CompletedTask;
    }

    public async ValueTask UpdateSettingsAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        SettingsUpdateEntered?.TrySetResult(true);
        if (ContinueSettingsUpdate is { } continuation)
        {
            await continuation.Task.WaitAsync(cancellationToken);
        }

        SettingsUpdates.Add(settings);
        NextSettingsUpdate.TrySetResult(settings);
        NextSettingsUpdate = NewSettingsUpdateSource();
    }

    public ValueTask UpdateTranscriptionSettingsAsync(
        RuntimeTranscriptionSettingsUpdate settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        TranscriptionSettingsUpdates.Add(settings);
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveTranscriptionCredentialAsync(
        string engineId,
        string credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        SavedTranscriptionCredentials.Add((engineId, credential));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteTranscriptionCredentialAsync(
        string engineId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        DeletedTranscriptionCredentials.Add(engineId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverTranscriptionModelsAsync(
        string engineId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        DiscoveredTranscriptionModelEngines.Add(engineId);
        return ValueTask.FromResult(TranscriptionModelDiscoveryResult);
    }

    public ValueTask InstallDiarizationAssetsAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask InstallTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        InstalledTranscriptionModels.Add((engineId, modelId));
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelTranscriptionModelInstallAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        CancelledTranscriptionModelInstalls.Add((engineId, modelId));
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        RemovedTranscriptionModels.Add((engineId, modelId));
        return ValueTask.CompletedTask;
    }

    public ValueTask ConfirmLocalTranscriptionPowerOverrideAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        ConfirmedLocalTranscriptionPowerOverrides.Add(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask TranscribeRecentRecordingAsync(
        Guid sessionId,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        TranscriptionRequests.Add((sessionId, replaceExisting));
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelTranscriptionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        CancelledTranscriptions.Add(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask RetryTranscriptionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        RetriedTranscriptions.Add(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResolveMeetingPromptAsync(
        string candidateId,
        MeetingPromptUserAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        PromptResolutions.Add((candidateId, action));
        return PromptResolutionHandler?.Invoke(candidateId, action, cancellationToken)
               ?? ValueTask.CompletedTask;
    }

    public ValueTask StartManualRecordingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        ManualRecordingCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseOrResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        PauseOrResumeCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask FinishRecordingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        FinishRecordingCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DiscardRecordingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        DiscardRecordingCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveRecentRecordingAsync(
        Guid sessionId,
        bool deleteAudioFile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        RemovedRecordings.Add((sessionId, deleteAudioFile));
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameRecentRecordingAsync(
        Guid sessionId,
        string displayTitle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        RenamedRecordings.Add((sessionId, displayTitle));
        return ValueTask.CompletedTask;
    }

    public ValueTask AcknowledgeAttentionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        AcknowledgedAttentionSessions.Add(sessionId);
        return ValueTask.CompletedTask;
    }

    public void Publish(ApplicationRuntimeSnapshot next)
    {
        Snapshot = next;
        SnapshotChanged?.Invoke(this, next);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TaskCompletionSource<RuntimeUserSettingsUpdate> NewSettingsUpdateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfConfigured()
    {
        if (ActionFailure is { } exception)
        {
            throw exception;
        }
    }
}

internal sealed class FakeDesktopShell : IDesktopShell
{
    public List<string> OpenedFiles { get; } = [];

    public List<string> OpenedFolders { get; } = [];

    public List<Uri> OpenedUris { get; } = [];

    public Exception? Failure { get; set; }

    public ValueTask OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        OpenedFiles.Add(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenContainingFolderAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        OpenedFolders.Add(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConfigured();
        OpenedUris.Add(uri);
        return ValueTask.CompletedTask;
    }

    private void ThrowIfConfigured()
    {
        if (Failure is { } exception)
        {
            throw exception;
        }
    }
}

internal sealed class FakeLocalizationService(
    UiLanguage initialLanguage = UiLanguage.Russian,
    bool includeLanguageInText = false)
    : ILocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> Formats =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["String.Ask.AppLine.Format"] = "{0}",
            ["String.Ask.Countdown.Format"] = "{0}",
            ["String.Ask.Countdown.Accessible.One"] = "{0}",
            ["String.Ask.Countdown.Accessible.Few"] = "{0}",
            ["String.Ask.Countdown.Accessible.Many"] = "{0}",
            ["String.Notification.RecordingReady.Body.Format"] = "{0}",
            ["String.Diagnostics.Transcription.Chunks.Current.Format"] = "{0}/{1}",
            ["String.Diagnostics.Transcription.Duration.Hours.Format"] = "{0}:{1:00}:{2:00}",
            ["String.Diagnostics.Transcription.Duration.Minutes.Format"] = "{0}:{1:00}",
            ["String.Transcription.LocalModel.Size.Megabytes.Format"] = "{0:0.0}",
            ["String.Transcription.LocalModel.Size.Gigabytes.Format"] = "{0:0.0}"
        };

    public UiLanguage CurrentLanguage { get; private set; } = initialLanguage;

    public CultureInfo CurrentCulture => CurrentLanguage == UiLanguage.English
        ? CultureInfo.GetCultureInfo("en-US")
        : CultureInfo.GetCultureInfo("ru-RU");

    public event EventHandler? LanguageChanged;

    public void Attach(Avalonia.Application application) => ArgumentNullException.ThrowIfNull(application);

    public void SetLanguage(UiLanguage language)
    {
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string resourceKey)
    {
        var value = Formats.GetValueOrDefault(resourceKey, resourceKey);
        if (!includeLanguageInText || Formats.ContainsKey(resourceKey))
        {
            return value;
        }

        var language = CurrentLanguage == UiLanguage.English ? "en" : "ru";
        return $"{language}:{value}";
    }

    public string Format(string resourceKey, params object?[] arguments) =>
        string.Format(CurrentCulture, Get(resourceKey), arguments);
}

internal sealed class FakeThemeService : IThemeService
{
    public UiThemeMode CurrentMode { get; private set; } = UiThemeMode.System;

    public event EventHandler? ThemeChanged;

    public void Attach(Avalonia.Application application) => ArgumentNullException.ThrowIfNull(application);

    public void SetTheme(UiThemeMode mode)
    {
        if (CurrentMode == mode)
        {
            return;
        }

        CurrentMode = mode;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class SnapshotFactory
{
    public static ApplicationRuntimeSnapshot Create(
        ApplicationActivityState activity = ApplicationActivityState.Listening,
        bool serviceEnabled = true,
        ActiveMeetingSnapshot? activeMeeting = null,
        IReadOnlyList<RecentRecordingSnapshot>? recentRecordings = null,
        MeetingPromptSnapshot? pendingPrompt = null,
        RecordingFinalizationSnapshot? finalization = null,
        RuntimeUserSettingsSnapshot? userSettings = null,
        IReadOnlyList<RuntimeMicrophoneSnapshot>? microphones = null,
        RuntimeCapabilitySnapshot? capability = null,
        string? attentionMessage = null) =>
        new(
            activity,
            serviceEnabled,
            Theme: userSettings?.Theme ?? "system",
            activeMeeting,
            recentRecordings ?? [],
            attentionMessage,
            pendingPrompt,
            finalization)
        {
            UserSettings = userSettings ?? RuntimeUserSettingsSnapshot.Initial with
            {
                OnboardingCompleted = true,
                ServiceEnabled = serviceEnabled
            },
            AvailableMicrophones = microphones ?? [],
            Capability = capability ?? RuntimeCapabilitySnapshot.Unknown
        };
}
