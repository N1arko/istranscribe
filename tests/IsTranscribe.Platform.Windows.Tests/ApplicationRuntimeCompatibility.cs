global using WindowsRecordingCoordinatorSnapshot =
    IsTranscribe.Application.Platform.RecordingCoordinatorSnapshot;
global using IsTranscribe.Application.Settings;

using System.Runtime.Versioning;
using IsTranscribe.Application;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Test-only constructor compatibility for the pre-cutover Windows characterization suite.
/// The release graph contains only <see cref="ApplicationRuntime"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsReleaseV2Runtime : ApplicationRuntime
{
    public WindowsReleaseV2Runtime(
        string? rootDirectoryOverride = null,
        IAudioPlatform? audioPlatform = null,
        MeetingDetectionMode detectionMode = MeetingDetectionMode.Live)
        : base(
            new WindowsApplicationPlatformRuntimeAdapter(),
            rootDirectoryOverride,
            audioPlatform,
            detectionMode)
    {
    }

    internal WindowsReleaseV2Runtime(
        string? rootDirectoryOverride,
        IAudioPlatform? audioPlatform,
        MeetingDetectionMode detectionMode,
        Func<
            IAudioPlatform,
            MeetingProfileRegistry,
            IReadOnlyList<MeetingApplicationPreference>,
            MeetingDetectionMode,
            MeetingDetectionCoordinator>? detectionCoordinatorFactory,
        IAutostartService? autostartService = null,
        IApplicationSettingsStore? settingsStore = null,
        TimeSpan? readyFinalizationHold = null,
        TimeSpan? onboardingMicrophoneHealthTimeout = null,
        TimeSpan? activeMeetingLossDelay = null)
        : base(
            new WindowsApplicationPlatformRuntimeAdapter(),
            rootDirectoryOverride,
            audioPlatform,
            detectionMode,
            detectionCoordinatorFactory,
            autostartService,
            settingsStore,
            readyFinalizationHold,
            onboardingMicrophoneHealthTimeout,
            activeMeetingLossDelay)
    {
    }
}
