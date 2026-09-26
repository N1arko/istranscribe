using System.Diagnostics;
using System.Runtime.InteropServices;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Recording;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Buildable pre-parity adapter that reports every native feature honestly.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#entrypoints-and-windows-identity
/// </remarks>
public sealed class MacOSApplicationPlatformRuntimeAdapter :
    IApplicationPlatformRuntimeAdapter,
    ITranscriptionAudioDecoderPlatformAdapter,
    IPlatformCapabilityService
{
    public LocalAppPaths CreateAppPaths(string applicationName, string? rootDirectoryOverride) =>
        new(
            applicationName,
            rootDirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                applicationName));

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    public ISecretVault CreateSecretVault(LocalAppPaths paths) =>
        new MacOSKeychainSecretVault();

    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
    public IPlatformAudioChunkDecoder CreateTranscriptionAudioChunkDecoder() =>
        new MacOSAudioToolboxChunkDecoder();

    public IAudioPlatform CreateAudioPlatform(BootstrapFileLogger logger) =>
        new MacOSAudioPlatform();

    public IAutostartService CreateAutostartService() =>
        new MacOSAutostartService();

    public AudioArtifactEncoderAvailability ProbeRecordingEncoder() =>
        new(
            IsAvailable: OperatingSystem.IsMacOS(),
            Codec: "mp3",
            SampleRate: 48_000,
            Channels: 2,
            BitsPerSample: 16,
            BitRate: 128_000,
            ReasonCode: OperatingSystem.IsMacOS() ? null : "macos_required",
            Detail: OperatingSystem.IsMacOS()
                ? "Bundled LAME 3.100 encoder with AudioToolbox verification."
                : "The macOS encoder is available only on macOS.");

    public IRecordingSessionCoordinator CreateRecordingCoordinator(
        IAudioPlatform audioPlatform,
        MeetingSessionRepository sessionRepository,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor,
        BootstrapFileLogger logger) =>
        new MacOSRecordingSessionCoordinator(
            audioPlatform,
            sessionRepository,
            artifactPathResolver,
            settingsAccessor,
            logger);

    public IRecordingArtifactDeletionService CreateRecordingArtifactDeletionService(
        LocalAppPaths paths,
        ArtifactPathResolver artifactPathResolver,
        Func<ApplicationSettings> settingsAccessor) =>
        new MacOSRecordingArtifactDeletionService(paths, artifactPathResolver, settingsAccessor);

    public MeetingDetectionCoordinator CreateMeetingDetectionCoordinator(
        IAudioPlatform audioPlatform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MeetingApplicationPreference> preferences,
        MeetingDetectionMode mode,
        BootstrapFileLogger logger) =>
        audioPlatform is MacOSAudioPlatform macOSAudioPlatform
            ? new MeetingDetectionCoordinator(
                audioPlatform,
                new MacOSWindowEvidenceProvider(),
                new MacOSMeetingSpeechActivityProvider(macOSAudioPlatform, logger),
                profiles,
                preferences,
                new MeetingDetectionEngine(mode: mode))
            : throw new ArgumentException("The macOS detection coordinator requires MacOSAudioPlatform.", nameof(audioPlatform));

    public IReadOnlyList<PlatformCapability> GetCapabilities() =>
    [
        new("audio_observation", PlatformCapabilityState.Available, "Core Audio process and device observation."),
        new("audio_capture", PlatformCapabilityState.PermissionRequired, "System Audio and microphone permissions are required."),
        new("meeting_observation", PlatformCapabilityState.PermissionRequired, "Accessibility and System Audio permissions may be required."),
        new("permissions", PlatformCapabilityState.PermissionRequired, "macOS permissions are requested contextually and remain independently observable.")
    ];

}

public sealed class MacOSPlatformDescriptor : IPlatformDescriptor
{
    public string OperatingSystem => "macOS";

    public Architecture Architecture => RuntimeInformation.OSArchitecture;

    public bool IsReleaseArchitectureSupported =>
        System.OperatingSystem.IsMacOS() && Architecture == Architecture.Arm64;
}

public sealed class MacOSPlatformShell : IPlatformShell
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    public MacOSPlatformShell() : this(Process.Start)
    {
    }

    internal MacOSPlatformShell(Func<ProcessStartInfo, Process?> start) => _start = start;

    public ValueTask OpenFileAsync(string path, CancellationToken cancellationToken) =>
        OpenFileCoreAsync(path, cancellationToken);

    public ValueTask OpenContainingFolderAsync(string path, CancellationToken cancellationToken) =>
        RevealCoreAsync(path, cancellationToken);

    public ValueTask OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only HTTP and HTTPS help links are supported.", nameof(uri));
        }
        return OpenAsync([uri.AbsoluteUri], cancellationToken);
    }

    private ValueTask OpenFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The recording file is no longer available.", fullPath);
        return OpenAsync([fullPath], cancellationToken);
    }

    private ValueTask RevealCoreAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath)) return OpenAsync(["-R", fullPath], cancellationToken);
        if (Directory.Exists(fullPath)) return OpenAsync([fullPath], cancellationToken);
        throw new DirectoryNotFoundException("The recording folder is no longer available.");
    }

    private ValueTask OpenAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = _start(startInfo);
        if (process is null) throw new InvalidOperationException("macOS did not accept the shell request.");
        return ValueTask.CompletedTask;
    }
}

public sealed class MacOSActiveWorkAreaProvider : IActiveWorkAreaProvider
{
    public PlatformPoint? TryGetForegroundWindowCenter() =>
        NativeMethods.ForegroundWindowCenter(out var x, out var y) != 0 ? new PlatformPoint(x, y) : null;

    private static class NativeMethods
    {
        [DllImport("istranscribe_audio", EntryPoint = "ist_foreground_window_center", CallingConvention = CallingConvention.Cdecl)]
        internal static extern byte ForegroundWindowCenter(out int x, out int y);
    }
}
