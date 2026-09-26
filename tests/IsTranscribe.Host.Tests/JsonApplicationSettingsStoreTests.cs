using System.Text.Json;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class JsonApplicationSettingsStoreTests
{
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public void WithReleaseV2ProjectionKeepsLegacyUserFacingSectionsAligned()
    {
        var releaseV2 = ReleaseV2Settings.Default with
        {
            OnboardingCompleted = true,
            ServiceEnabled = false,
            Autostart = false,
            Notifications = false,
            Language = "en",
            Theme = "dark",
            MicrophoneDeviceId = "mic-7",
            FollowSystemDefaultMicrophone = false,
            RecordingsFolder = "recordings"
        };

        var projected = ApplicationSettings.Default.WithReleaseV2Projection(releaseV2);

        Assert.True(projected.OnboardingCompleted);
        Assert.False(projected.General.Autostart);
        Assert.False(projected.General.Notifications);
        Assert.Equal("en", projected.General.Language);
        Assert.Equal("dark", projected.General.AppTheme);
        Assert.Equal("off", projected.Recording.Mode);
        Assert.Equal("mic-7", projected.Devices.MicrophoneDeviceId);
        Assert.False(projected.Devices.FollowSystemDefaultMic);
        Assert.Equal("recordings", projected.Storage.RecordingsFolder);
        Assert.Equal(releaseV2.Canonicalize(), projected.ReleaseV2);
    }

    [Fact]
    public async Task LoadAsync_CreatesDefaults_WhenFileIsMissing()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);

            var settings = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.Version);
            Assert.False(settings.OnboardingCompleted);
            Assert.True(settings.General.MinimizeToTrayOnClose);
            Assert.Equal("ask", settings.Recording.Mode);
            Assert.Equal(["process_output", "mic"], settings.Recording.DefaultSourcesAuto);
            Assert.True(File.Exists(paths.SettingsFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyBootstrapFields_IntoCanonicalGeneralSection()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllTextAsync(
                paths.SettingsFilePath,
                """
                {
                  "onboarding_completed": true,
                  "minimize_to_tray_on_close": false,
                  "notifications_enabled": false
                }
                """);

            var store = new JsonApplicationSettingsStore(paths);
            var settings = await store.LoadAsync(CancellationToken.None);

            Assert.True(settings.OnboardingCompleted);
            Assert.False(settings.General.MinimizeToTrayOnClose);
            Assert.False(settings.General.Notifications);
            Assert.Equal("ru", settings.General.Language);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WritesCanonicalNestedSchema()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            var settings = ApplicationSettings.Default with
            {
                OnboardingCompleted = true,
                General = ApplicationSettings.Default.General with
                {
                    MinimizeToTrayOnClose = false,
                    Notifications = false
                }
            };

            await store.SaveAsync(settings, CancellationToken.None);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFilePath));
            var rootElement = document.RootElement;
            Assert.True(rootElement.GetProperty("onboarding_completed").GetBoolean());
            Assert.False(rootElement.GetProperty("general").GetProperty("minimize_to_tray_on_close").GetBoolean());
            Assert.False(rootElement.GetProperty("general").GetProperty("notifications").GetBoolean());
            Assert.Equal("7d", rootElement.GetProperty("storage").GetProperty("temp_retention_period").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesLegacySectionsAndReleaseV2Projection()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            var releaseV2 = ReleaseV2Settings.Default with
            {
                OnboardingCompleted = true,
                ServiceEnabled = false,
                Autostart = false,
                Notifications = false,
                Language = "en",
                Theme = "dark",
                MicrophoneDeviceId = "mic-7",
                FollowSystemDefaultMicrophone = false,
                RecordingsFolder = Path.Combine(root, "recordings"),
                Applications =
                [
                    new MeetingApplicationPreference("zoom", "Zoom", MeetingApplicationPolicy.Ask)
                ]
            };
            var settings = ApplicationSettings.Default.WithReleaseV2Projection(releaseV2);

            await store.SaveAsync(settings, CancellationToken.None);
            var reloaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal("en", reloaded.General.Language);
            Assert.NotNull(reloaded.ReleaseV2);
            Assert.False(reloaded.ReleaseV2.ServiceEnabled);
            Assert.False(reloaded.ReleaseV2.Autostart);
            Assert.False(reloaded.ReleaseV2.Notifications);
            Assert.Equal("dark", reloaded.ReleaseV2.Theme);
            Assert.Equal("mic-7", reloaded.ReleaseV2.MicrophoneDeviceId);
            Assert.False(reloaded.ReleaseV2.FollowSystemDefaultMicrophone);
            Assert.Equal(Path.Combine(root, "recordings"), reloaded.ReleaseV2.RecordingsFolder);
            Assert.Equal("zoom", Assert.Single(reloaded.ReleaseV2.Applications).ProfileId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </summary>
    [Fact]
    public async Task ReleaseV2SavePreservesButDoesNotValidateInactiveTranscriptionConfiguration()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var blockedTranscriptFolder = Path.Combine(root, "legacy-transcripts-blocked");
            await File.WriteAllTextAsync(blockedTranscriptFolder, "this path is a file");
            var store = new JsonApplicationSettingsStore(paths);
            var settings = ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with { OnboardingCompleted = true }) with
            {
                Storage = ApplicationSettings.Default.Storage with
                {
                    TranscriptsFolder = blockedTranscriptFolder
                },
                Transcription = ApplicationSettings.Default.Transcription with
                {
                    MinSpeakers = 0,
                    MaxSpeakers = 99,
                    RetryCount = 0
                }
            };

            await store.SaveReleaseV2Async(settings, CancellationToken.None);
            var reloaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(blockedTranscriptFolder, reloaded.Storage.TranscriptsFolder);
            Assert.Equal(0, reloaded.Transcription.MinSpeakers);
            Assert.Equal(99, reloaded.Transcription.MaxSpeakers);
            Assert.Equal(0, reloaded.Transcription.RetryCount);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveAsync(settings, CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#settings.validation
    /// </summary>
    [Fact]
    public async Task ReleaseV2SaveStillValidatesRecordingAndActiveStorageConfiguration()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            var invalidRecording = ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default) with
            {
                Recording = ApplicationSettings.Default.Recording with { PrebufferSeconds = 1 }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveReleaseV2Async(invalidRecording, CancellationToken.None).AsTask());

            var blockedRecordingsFolder = Path.Combine(root, "recordings-blocked");
            await File.WriteAllTextAsync(blockedRecordingsFolder, "this path is a file");
            var invalidStorage = ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with { RecordingsFolder = blockedRecordingsFolder });

            await Assert.ThrowsAnyAsync<Exception>(
                () => store.SaveReleaseV2Async(invalidStorage, CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal static class JsonApplicationSettingsStoreTestsHelpers
{
    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
