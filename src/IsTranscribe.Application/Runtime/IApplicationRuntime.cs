namespace IsTranscribe.Application.Runtime;

using IsTranscribe.Core.Transcription;

/// <summary>
/// Application boundary used by desktop view models; implementations own platform services.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#user-policy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// </remarks>
public interface IApplicationRuntime : IAsyncDisposable
{
    event EventHandler<ApplicationRuntimeSnapshot>? SnapshotChanged;

    ApplicationRuntimeSnapshot Snapshot { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    ValueTask RefreshCapabilitiesAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask SetServiceEnabledAsync(bool enabled, CancellationToken cancellationToken);

    ValueTask CompleteOnboardingAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken);

    ValueTask UpdateSettingsAsync(
        RuntimeUserSettingsUpdate settings,
        CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    ValueTask UpdateTranscriptionSettingsAsync(
        RuntimeTranscriptionSettingsUpdate settings,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription settings are unavailable in this runtime."));

    ValueTask SaveTranscriptionCredentialAsync(
        string engineId,
        string credential,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription credentials are unavailable in this runtime."));

    ValueTask DeleteTranscriptionCredentialAsync(
        string engineId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription credentials are unavailable in this runtime."));

    ValueTask<IReadOnlyList<TranscriptionModelCapability>> DiscoverTranscriptionModelsAsync(
        string engineId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<IReadOnlyList<TranscriptionModelCapability>>(
            new NotSupportedException("Transcription model discovery is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
    ValueTask InstallTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Local transcription model installation is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
    ValueTask InstallDiarizationAssetsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("Speaker model installation is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
    ValueTask CancelTranscriptionModelInstallAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Local transcription model installation is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    ValueTask RemoveTranscriptionModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Local transcription model removal is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
    ValueTask ConfirmLocalTranscriptionPowerOverrideAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "A local transcription power override is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
    ValueTask TranscribeRecentRecordingAsync(
        Guid sessionId,
        bool replaceExisting,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription is unavailable in this runtime."));

    ValueTask CancelTranscriptionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription cancellation is unavailable in this runtime."));

    ValueTask RetryTranscriptionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Transcription retry is unavailable in this runtime."));

    ValueTask ResolveMeetingPromptAsync(
        string candidateId,
        MeetingPromptUserAction action,
        CancellationToken cancellationToken);

    ValueTask StartManualRecordingAsync(CancellationToken cancellationToken);

    ValueTask PauseOrResumeAsync(CancellationToken cancellationToken);

    ValueTask FinishRecordingAsync(CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    ValueTask DiscardRecordingAsync(CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    ValueTask RemoveRecentRecordingAsync(
        Guid sessionId,
        bool deleteAudioFile,
        CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    ValueTask RenameRecentRecordingAsync(
        Guid sessionId,
        string displayTitle,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "Renaming recent recordings is unavailable in this runtime."));

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
    ValueTask AcknowledgeAttentionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
