using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Recording;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows.Audio.Encoding;
using IsTranscribe.Platform.Windows.Audio.Finalization;
using IsTranscribe.Platform.Windows.Audio.Recording;
using IsTranscribe.Platform.Windows.Secrets;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Windows composition adapter for the shared application runtime.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#entrypoints-and-windows-identity
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationPlatformRuntimeAdapter :
    IApplicationPlatformRuntimeAdapter,
    ITranscriptionAudioDecoderPlatformAdapter,
    IPlatformCapabilityService
{
    public LocalAppPaths CreateAppPaths(string applicationName, string? rootDirectoryOverride) =>
        new(applicationName, rootDirectoryOverride);

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    public ISecretVault CreateSecretVault(LocalAppPaths paths) =>
        new DpapiSecretVault(paths, new DpapiSecretProtector());

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
    public IPlatformAudioChunkDecoder CreateTranscriptionAudioChunkDecoder() =>
        new WindowsMediaFoundationAudioChunkDecoder();

    public IAudioPlatform CreateAudioPlatform(BootstrapFileLogger logger) =>
        new WindowsAudioPlatform(logger);

    public IAutostartService CreateAutostartService() =>
        WindowsAutostartServiceFactory.Create();

    public AudioArtifactEncoderAvailability ProbeRecordingEncoder() =>
        new WindowsMediaFoundationMp3Encoder().ProbeAvailability();

    public IRecordingSessionCoordinator CreateRecordingCoordinator(
        IAudioPlatform audioPlatform,
        MeetingSessionRepository sessionRepository,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor,
        BootstrapFileLogger logger)
    {
        var encoder = new WindowsMediaFoundationMp3Encoder();
        return new WindowsRecordingSessionCoordinator(
            audioPlatform,
            sessionRepository,
            artifactPathResolver,
            settingsAccessor,
            new AudioMixArtifactBuilder(),
            new WindowsRecordingArtifactFinalizer(encoder, logger),
            logger);
    }

    public IRecordingArtifactDeletionService CreateRecordingArtifactDeletionService(
        LocalAppPaths paths,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor) =>
        new RecordingArtifactDeletionService(paths, artifactPathResolver, settingsAccessor);

    public MeetingDetectionCoordinator CreateMeetingDetectionCoordinator(
        IAudioPlatform audioPlatform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> preferences,
        MeetingDetectionMode mode,
        BootstrapFileLogger logger) =>
        new(
            audioPlatform,
            new WindowsWindowEvidenceProvider(logger),
            new WindowsMeetingSpeechActivityProvider(logger),
            profiles,
            preferences,
            new MeetingDetectionEngine(mode: mode));

    public IReadOnlyList<PlatformCapability> GetCapabilities()
    {
        var windows = OperatingSystem.IsWindows();
        return
        [
            new(
                "audio_observation",
                windows ? PlatformCapabilityState.Available : PlatformCapabilityState.Failed,
                windows
                    ? "Windows audio observation adapter is available."
                    : "The Windows adapter is running outside Windows."),
            new(
                "audio_capture",
                windows ? PlatformCapabilityState.Available : PlatformCapabilityState.Failed,
                windows
                    ? "Windows WASAPI capture adapter is available."
                    : "The Windows adapter is running outside Windows."),
            new(
                "meeting_observation",
                windows ? PlatformCapabilityState.Available : PlatformCapabilityState.Failed,
                windows
                    ? "Windows process and UI Automation evidence is available."
                    : "The Windows adapter is running outside Windows."),
            new(
                "permissions",
                windows
                    ? PlatformCapabilityState.Available
                    : PlatformCapabilityState.Failed,
                windows
                    ? "Windows capability checks are available."
                    : "The Windows adapter is running outside Windows.")
        ];
    }
}
