namespace IsTranscribe.Host.Settings;

public sealed record BootstrapSettingsSnapshot(
    bool OnboardingCompleted,
    bool MinimizeToTrayOnClose,
    bool NotificationsEnabled)
{
    public static BootstrapSettingsSnapshot Default { get; } = new(
        OnboardingCompleted: false,
        MinimizeToTrayOnClose: true,
        NotificationsEnabled: true);

    public static BootstrapSettingsSnapshot FromSettings(ApplicationSettings settings) =>
        settings.ToBootstrapSnapshot();
}

public interface IApplicationSettingsStore
{
    ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the release-v2 projection without making obsolete transcription settings a
    /// prerequisite for recording configuration changes.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </remarks>
    ValueTask SaveReleaseV2Async(ApplicationSettings settings, CancellationToken cancellationToken) =>
        SaveAsync(settings, cancellationToken);
}
