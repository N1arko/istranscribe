namespace IsTranscribe.Host.Settings;

public static class ApplicationSettingsValidator
{
    private static readonly HashSet<string> AllowedRetentionPeriods = ["1d", "3d", "7d", "14d", "30d", "never"];
    private static readonly HashSet<string> AllowedAutoDiscoveryPolicies = ["auto_add", "ask_to_add", "off"];

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#settings.validation
    public static void ValidateForSave(ApplicationSettings settings) =>
        ValidateForSave(settings, includeLegacyTranscription: true);

    /// <summary>
    /// Validates settings used by the release-v2 recording runtime while treating the inactive
    /// legacy transcription section and transcript folder as preserved rollback data.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </remarks>
    public static void ValidateForReleaseV2Save(ApplicationSettings settings) =>
        ValidateForSave(settings, includeLegacyTranscription: false);

    private static void ValidateForSave(
        ApplicationSettings settings,
        bool includeLegacyTranscription)
    {
        if (!settings.Storage.FilenameTemplate.Contains("{SessionId}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage.filename_template must contain {SessionId}.");
        }

        if (!AllowedRetentionPeriods.Contains(settings.Storage.TempRetentionPeriod))
        {
            throw new InvalidOperationException("storage.temp_retention_period is not supported.");
        }

        if (!AllowedAutoDiscoveryPolicies.Contains(settings.Applications.AutoDiscoveryPolicy))
        {
            throw new InvalidOperationException("applications.auto_discovery_policy is not supported.");
        }

        ValidateRange(settings.Recording.PrebufferSeconds, 5, 30, "recording.prebuffer_seconds");
        ValidateAllowed(settings.Recording.PrebufferSeconds, [5, 10, 15, 30], "recording.prebuffer_seconds");
        ValidateRange(settings.Recording.SilenceThresholdDbfs, -60, -20, "recording.silence_threshold_dbfs");
        ValidateRange(settings.Recording.StartDelaySeconds, 1, 10, "recording.start_delay_seconds");
        ValidateRange(settings.Recording.StopDelaySeconds, 5, 120, "recording.stop_delay_seconds");
        ValidateRange(settings.Recording.MergeWindowSeconds, 0, 300, "recording.merge_window_seconds");
        if (includeLegacyTranscription)
        {
            ValidateRange(settings.Transcription.MinSpeakers, 1, 20, "transcription.min_speakers");
            ValidateRange(settings.Transcription.MaxSpeakers, 1, 20, "transcription.max_speakers");
            ValidateRange(settings.Transcription.RetryCount, 1, 10, "transcription.retry_count");

            if (settings.Transcription.MinSpeakers > settings.Transcription.MaxSpeakers)
            {
                throw new InvalidOperationException(
                    "transcription.min_speakers must be less than or equal to transcription.max_speakers.");
            }
        }

        ValidateWritablePath(settings.Storage.RecordingsFolder, "storage.recordings_folder");
        if (includeLegacyTranscription)
        {
            ValidateWritablePath(settings.Storage.TranscriptsFolder, "storage.transcripts_folder");
        }

        ValidateWritablePath(settings.Storage.FailedTempFolder, "storage.failed_temp_folder");
    }

    private static void ValidateAllowed<T>(T value, IReadOnlyCollection<T> allowedValues, string fieldName)
    {
        if (!allowedValues.Contains(value))
        {
            throw new InvalidOperationException($"{fieldName} has an unsupported value.");
        }
    }

    private static void ValidateRange(int value, int minInclusive, int maxInclusive, string fieldName)
    {
        if (value < minInclusive || value > maxInclusive)
        {
            throw new InvalidOperationException($"{fieldName} is outside the supported range.");
        }
    }

    private static void ValidateWritablePath(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Directory.CreateDirectory(value);
        var probePath = Path.Combine(value, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "ok");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{fieldName} must point to a writable directory.", exception);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }
}
