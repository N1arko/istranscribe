using System.Text.Json;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Persistence.Tests;

/// <summary>
/// Provider-neutral release-v2 transcription settings persistence contracts.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public sealed class ReleaseV2TranscriptionSettingsTests
{
    [Fact]
    public async Task VersionTwoJsonMigratesToDisabledVersionThreePreferencesAndPreservesLegacyTranscription()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllTextAsync(
                paths.SettingsFilePath,
                """
                {
                  "version": 2,
                  "transcription": {
                    "model": "legacy-model",
                    "diarization": true,
                    "min_speakers": 0,
                    "max_speakers": 99,
                    "language": "ru",
                    "auto_retry": false,
                    "retry_count": 0
                  },
                  "release_v2": {
                    "version": 2,
                    "onboarding_completed": true,
                    "service_enabled": true,
                    "autostart": false,
                    "notifications": true,
                    "language": "en",
                    "theme": "dark",
                    "microphone_device_id": "mic-7",
                    "follow_system_default_microphone": false,
                    "recordings_folder": null,
                    "applications": [],
                    "autostart_preference": false,
                    "packaged_autostart_observed": null
                  }
                }
                """);
            var store = new JsonApplicationSettingsStore(paths);

            var loaded = await store.LoadAsync(CancellationToken.None);
            var releaseV2 = Assert.IsType<ReleaseV2Settings>(loaded.ReleaseV2);
            var transcription = Assert.IsType<TranscriptionPreferences>(releaseV2.Transcription);

            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, loaded.Version);
            Assert.Equal(ReleaseV2Settings.CurrentVersion, releaseV2.Version);
            Assert.Null(transcription.SelectedEngineId);
            Assert.False(transcription.AutomaticEnabled);
            Assert.Equal("auto", transcription.Language);
            Assert.Empty(transcription.Engines);
            Assert.True(transcription.RequireZeroDataRetention);
            Assert.Equal("legacy-model", loaded.Transcription.Model);
            Assert.True(loaded.Transcription.Diarization);
            Assert.Equal(0, loaded.Transcription.MinSpeakers);
            Assert.Equal(99, loaded.Transcription.MaxSpeakers);
            Assert.Equal(0, loaded.Transcription.RetryCount);

            await store.SaveReleaseV2Async(
                loaded.WithReleaseV2Projection(releaseV2 with { Notifications = false }),
                CancellationToken.None);

            using var persisted = JsonDocument.Parse(
                await File.ReadAllTextAsync(paths.SettingsFilePath));
            var document = persisted.RootElement;
            var legacy = document.GetProperty("transcription");
            var persistedReleaseV2 = document.GetProperty("release_v2");
            var persistedPreferences = persistedReleaseV2.GetProperty("transcription");

            Assert.Equal(3, document.GetProperty("version").GetInt32());
            Assert.Equal(3, persistedReleaseV2.GetProperty("version").GetInt32());
            Assert.False(persistedReleaseV2.GetProperty("notifications").GetBoolean());
            Assert.Equal("legacy-model", legacy.GetProperty("model").GetString());
            Assert.True(legacy.GetProperty("diarization").GetBoolean());
            Assert.Equal(0, legacy.GetProperty("min_speakers").GetInt32());
            Assert.Equal(99, legacy.GetProperty("max_speakers").GetInt32());
            Assert.Equal(0, legacy.GetProperty("retry_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, persistedPreferences.GetProperty("selected_engine_id").ValueKind);
            Assert.False(persistedPreferences.GetProperty("automatic_enabled").GetBoolean());
            Assert.Equal("auto", persistedPreferences.GetProperty("language").GetString());
            Assert.Empty(persistedPreferences.GetProperty("engines").EnumerateArray());
            Assert.True(persistedPreferences.GetProperty("require_zero_data_retention").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnrelatedReleaseV2SavePreservesEnginePreferencesAndContainsNoApiKeyFields()
    {
        var root = CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            var transcription = new TranscriptionPreferences(
                SelectedEngineId: "remote.openrouter",
                AutomaticEnabled: true,
                Language: "en",
                Engines:
                [
                    new TranscriptionEnginePreference(
                        "remote.groq",
                        "whisper-large-v3-turbo",
                        DisclosureAccepted: true),
                    new TranscriptionEnginePreference(
                        "remote.openrouter",
                        "openai/whisper-large-v3",
                        DisclosureAccepted: true),
                    new TranscriptionEnginePreference(
                        "local.whisper",
                        "ggml-small.bin",
                        DisclosureAccepted: false)
                ],
                RequireZeroDataRetention: true);
            var legacyTranscription = ApplicationSettings.Default.Transcription with
            {
                Model = "legacy-provider-model",
                MinSpeakers = 0,
                MaxSpeakers = 99,
                RetryCount = 0
            };
            var settings = (ApplicationSettings.Default with
            {
                Transcription = legacyTranscription
            }).WithReleaseV2Projection(ReleaseV2Settings.Default with
            {
                Transcription = transcription
            });

            await store.SaveReleaseV2Async(settings, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            await store.SaveReleaseV2Async(
                loaded.WithReleaseV2Projection(loaded.ReleaseV2! with { Theme = "dark" }),
                CancellationToken.None);
            var reloaded = await store.LoadAsync(CancellationToken.None);
            var persistedPreferences = Assert.IsType<TranscriptionPreferences>(
                reloaded.ReleaseV2!.Transcription);

            Assert.Equal("dark", reloaded.ReleaseV2.Theme);
            Assert.Equal("remote.openrouter", persistedPreferences.SelectedEngineId);
            Assert.True(persistedPreferences.AutomaticEnabled);
            Assert.Equal("auto", persistedPreferences.Language);
            Assert.True(persistedPreferences.RequireZeroDataRetention);
            Assert.Equal("whisper-large-v3-turbo", persistedPreferences.GetModelId("remote.groq"));
            Assert.Equal("openai/whisper-large-v3-turbo", persistedPreferences.GetModelId("remote.openrouter"));
            Assert.Equal("large-v3-turbo", persistedPreferences.GetModelId("local.whisper"));
            Assert.True(persistedPreferences.HasAcceptedDisclosure("remote.groq"));
            Assert.True(persistedPreferences.HasAcceptedDisclosure("remote.openrouter"));
            Assert.Equal(legacyTranscription, reloaded.Transcription);

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(paths.SettingsFilePath));
            Assert.DoesNotContain(
                EnumeratePropertyNames(document.RootElement),
                static name => string.Equals(name, "api_key", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("credential", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-transcription-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
