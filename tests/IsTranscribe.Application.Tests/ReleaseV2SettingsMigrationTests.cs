using IsTranscribe.Application.Settings;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Release-v2 settings migration contracts owned by the shared application layer.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// </remarks>
public sealed class ReleaseV2SettingsMigrationTests
{
    [Fact]
    public void LegacyMigrationCreatesDisabledProviderNeutralTranscriptionPreferences()
    {
        var legacyTranscription = ApplicationSettings.Default.Transcription with
        {
            Model = "legacy-provider-model",
            Diarization = true,
            MinSpeakers = 3,
            MaxSpeakers = 8,
            Language = "ru",
            AutoRetry = true,
            RetryCount = 7
        };
        var legacy = ApplicationSettings.Default with
        {
            Version = 2,
            Transcription = legacyTranscription
        };

        var migrated = ReleaseV2SettingsMigration.Migrate(legacy, []);
        var transcription = Assert.IsType<TranscriptionPreferences>(migrated.Transcription);

        Assert.Equal(ReleaseV2Settings.CurrentVersion, migrated.Version);
        Assert.Null(transcription.SelectedEngineId);
        Assert.False(transcription.AutomaticEnabled);
        Assert.Equal("auto", transcription.Language);
        Assert.Empty(transcription.Engines);
        Assert.True(transcription.RequireZeroDataRetention);
        Assert.Equal(legacyTranscription, legacy.Transcription);
    }
}
