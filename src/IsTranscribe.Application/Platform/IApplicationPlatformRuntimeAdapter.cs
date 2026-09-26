using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Recording;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Application.Platform;

/// <summary>
/// Supplies native capabilities to the shared product runtime.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime
/// </remarks>
public interface IApplicationPlatformRuntimeAdapter
{
    LocalAppPaths CreateAppPaths(string applicationName, string? rootDirectoryOverride);

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    ISecretVault CreateSecretVault(LocalAppPaths paths);

    IAudioPlatform CreateAudioPlatform(BootstrapFileLogger logger);

    IAutostartService CreateAutostartService();

    AudioArtifactEncoderAvailability ProbeRecordingEncoder();

    IRecordingSessionCoordinator CreateRecordingCoordinator(
        IAudioPlatform audioPlatform,
        MeetingSessionRepository sessionRepository,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor,
        BootstrapFileLogger logger);

    IRecordingArtifactDeletionService CreateRecordingArtifactDeletionService(
        LocalAppPaths paths,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor);

    MeetingDetectionCoordinator CreateMeetingDetectionCoordinator(
        IAudioPlatform audioPlatform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> preferences,
        MeetingDetectionMode mode,
        BootstrapFileLogger logger);
}

/// <summary>
/// Platform-owned recording infrastructure controlled by <see cref="ApplicationRuntime"/>.
/// </summary>
public interface IRecordingSessionCoordinator : IAsyncDisposable
{
    event EventHandler<RecordingCoordinatorSnapshot>? SnapshotChanged;

    bool IsBusy { get; }

    Guid? ActiveSessionId { get; }

    ValueTask<bool> StartAskAsync(
        string sourceLabel,
        int rootProcessId,
        string processName,
        CancellationToken cancellationToken);

    ValueTask<bool> StartManualAsync(CancellationToken cancellationToken);

    ValueTask PauseOrResumeAsync(CancellationToken cancellationToken);

    ValueTask<bool> FinishForRuntimeAsync(CancellationToken cancellationToken);

    ValueTask<bool> DiscardAsync(CancellationToken cancellationToken);

    Task RecoverPendingAsync(CancellationToken cancellationToken);
}

public interface IRecordingArtifactDeletionService
{
    void DeleteRecentArtifacts(RecentRecordingRemovalWorkItem workItem);
}

public sealed record RecordingCoordinatorSnapshot(
    ApplicationActivityState Activity,
    ActiveMeetingSnapshot? ActiveMeeting,
    RecordingFinalizationSnapshot? Finalization,
    string? AttentionMessage,
    bool RefreshRecentRecordings,
    RuntimeCapabilityIssue? CapabilityIssue = null);
