using System.Text.Json;
using System.Runtime.Versioning;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Core.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration
/// </summary>
public sealed class ReleaseV2SettingsMigrationTests
{
    [Fact]
    public void MigratePreservesUserChoicesAndMapsAutoModeToAskOnlyService()
    {
        var legacy = ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            General = ApplicationSettings.Default.General with
            {
                Autostart = false,
                Notifications = false,
                Language = "en",
                AppTheme = "dark"
            },
            Recording = ApplicationSettings.Default.Recording with { Mode = "auto" },
            Devices = ApplicationSettings.Default.Devices with
            {
                MicrophoneDeviceId = "mic-7",
                FollowSystemDefaultMic = false
            },
            Storage = ApplicationSettings.Default.Storage with { RecordingsFolder = "D:\\Meetings" }
        };
        AppRuleRecord[] rules =
        [
            new("zoom-id", "Zoom", "zoom.exe", Enabled: true),
            new("teams-id", "Microsoft Teams", "ms-teams.exe", Enabled: false)
        ];

        var migrated = ReleaseV2SettingsMigration.Migrate(legacy, rules);

        Assert.Equal(ReleaseV2Settings.CurrentVersion, migrated.Version);
        Assert.True(migrated.OnboardingCompleted);
        Assert.True(migrated.ServiceEnabled);
        Assert.False(migrated.Autostart);
        Assert.False(migrated.Notifications);
        Assert.Equal("en", migrated.Language);
        Assert.Equal("dark", migrated.Theme);
        Assert.Equal("mic-7", migrated.MicrophoneDeviceId);
        Assert.False(migrated.FollowSystemDefaultMicrophone);
        Assert.Equal("D:\\Meetings", migrated.RecordingsFolder);
        Assert.Collection(
            migrated.Applications,
            zoom => Assert.Equal(MeetingApplicationPolicy.Ask, zoom.Policy),
            teams => Assert.Equal(MeetingApplicationPolicy.Ignore, teams.Policy));
    }

    [Fact]
    public void MigrateMapsOffModeToPausedService()
    {
        var legacy = ApplicationSettings.Default with
        {
            Recording = ApplicationSettings.Default.Recording with { Mode = "off" }
        };

        var migrated = ReleaseV2SettingsMigration.Migrate(legacy, []);

        Assert.False(migrated.ServiceEnabled);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public void MigrateSkipsBrowserHostRulesAndKeepsDedicatedUserApplications()
    {
        AppRuleRecord[] rules =
        [
            AppRuleRecord.Create("Zen Browser", "ZEN"),
            AppRuleRecord.Create("Chrome", "chrome.exe"),
            AppRuleRecord.Create("My Call", "my-call.exe")
        ];

        var migrated = ReleaseV2SettingsMigration.Migrate(ApplicationSettings.Default, rules);

        var application = Assert.Single(migrated.Applications);
        Assert.Equal("legacy:my-call.exe", application.ProfileId);
        Assert.Equal("My Call", application.DisplayName);
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task RuntimeRemovesLegacyBrowserHostRulesFromEffectiveAndPersistedSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-browser-rule-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            var releaseV2 = ReleaseV2Settings.Default with
            {
                OnboardingCompleted = false,
                ServiceEnabled = false,
                Autostart = false,
                Applications =
                [
                    new MeetingApplicationPreference("google-meet", "Google Meet", MeetingApplicationPolicy.Ask),
                    new MeetingApplicationPreference("generic-browser", "Other browser meeting", MeetingApplicationPolicy.Ask),
                    new MeetingApplicationPreference("legacy:zen.exe", "Zen Browser", MeetingApplicationPolicy.Ignore),
                    new MeetingApplicationPreference("user:legacy:chrome.exe", "Chrome", MeetingApplicationPolicy.Ask),
                    new MeetingApplicationPreference("user:ZEN", "Zen duplicate", MeetingApplicationPolicy.Ask),
                    new MeetingApplicationPreference("legacy:my-call.exe", "My Call", MeetingApplicationPolicy.Ignore)
                ]
            };
            await store.SaveReleaseV2Async(
                ApplicationSettings.Default.WithReleaseV2Projection(releaseV2),
                CancellationToken.None);

            await using (var database = await new SqliteDatabaseInitializer(paths)
                             .InitializeAsync(CancellationToken.None))
            {
                await new AppRuleRepository(database).ReplaceAllAsync(
                    [
                        AppRuleRecord.Create("Zen Browser", "zen.exe"),
                        AppRuleRecord.Create("Chrome", "chrome.exe"),
                        AppRuleRecord.Create("My Call", "my-call.exe")
                    ],
                    CancellationToken.None);
            }

            await using (var runtime = new WindowsReleaseV2Runtime(root, new FakeAudioPlatform()))
            {
                await runtime.InitializeAsync(CancellationToken.None);

                Assert.Equal(
                    ["generic-browser", "google-meet", "user:legacy:my-call.exe"],
                    runtime.Snapshot.UserSettings.Applications
                        .Select(static application => application.ProfileId)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
            }

            var persisted = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(
                ["generic-browser", "google-meet", "legacy:my-call.exe"],
                persisted.ReleaseV2!.Applications
                    .Select(static application => application.ProfileId)
                    .Order(StringComparer.Ordinal)
                    .ToArray());

            await using (var database = await new SqliteDatabaseInitializer(paths)
                             .InitializeAsync(CancellationToken.None))
            {
                var rule = Assert.Single(await new AppRuleRepository(database)
                    .ListAsync(CancellationToken.None));
                Assert.Equal("my-call.exe", rule.ProcessName);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task RuntimeSettingsUpdateDropsBrowserHostPreferencesBeforeSnapshotAndPersistence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-browser-update-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveReleaseV2Async(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with
                    {
                        ServiceEnabled = false,
                        Autostart = false
                    }),
                CancellationToken.None);

            await using (var runtime = new WindowsReleaseV2Runtime(root, new FakeAudioPlatform()))
            {
                await runtime.InitializeAsync(CancellationToken.None);
                await runtime.UpdateSettingsAsync(
                    runtime.Snapshot.UserSettings.ToUpdate() with
                    {
                        Applications =
                        [
                            new MeetingApplicationPreference("google-meet", "Google Meet", MeetingApplicationPolicy.Ask),
                            new MeetingApplicationPreference("generic-browser", "Other browser meeting", MeetingApplicationPolicy.Ask),
                            new MeetingApplicationPreference("legacy:zen.exe", "Zen Browser", MeetingApplicationPolicy.Ignore),
                            new MeetingApplicationPreference("user:firefox.exe", "Firefox", MeetingApplicationPolicy.Ask)
                        ]
                    },
                    CancellationToken.None);

                Assert.Equal(
                    ["generic-browser", "google-meet"],
                    runtime.Snapshot.UserSettings.Applications
                        .Select(static application => application.ProfileId)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
            }

            var persisted = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(
                ["generic-browser", "google-meet"],
                persisted.ReleaseV2!.Applications
                    .Select(static application => application.ProfileId)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeMigratesV1CopyAndKeepsExistingRecordingVisible()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            await File.WriteAllTextAsync(
                paths.SettingsFilePath,
                """
                {
                  "version": 1,
                  "onboarding_completed": true,
                  "general": {
                    "autostart": false,
                    "notifications": false,
                    "language": "ru",
                    "app_theme": "dark"
                  },
                  "recording": {
                    "mode": "auto"
                  }
                }
                """);

            var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var sessionId = Guid.NewGuid();
            var audioPath = Path.Combine(root, "existing-mix.wav");
            await File.WriteAllBytesAsync(audioPath, [1, 2, 3, 4]);
            var session = MeetingSessionRecord.Create(
                sessionId,
                DateTimeOffset.UtcNow.AddHours(-1),
                "ask",
                "process_output+mic",
                "output-1",
                "mic-1") with
            {
                EndedAtUtc = DateTimeOffset.UtcNow,
                Status = "saved",
                SourceApp = "Zoom",
                AudioMixPath = audioPath,
                DurationSeconds = 3600,
                ErrorCode = "legacy_transcription_failed"
            };
            await new MeetingSessionRepository(database).UpsertAsync(session, CancellationToken.None);
            await new AppRuleRepository(database).UpsertAsync(
                AppRuleRecord.Create("Zoom", "zoom.exe"),
                CancellationToken.None);
            await database.DisposeAsync();

            await using var runtime = new WindowsReleaseV2Runtime(root, new FakeAudioPlatform());
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.True(runtime.Snapshot.ServiceEnabled);
            var migratedRecording = Assert.Single(runtime.Snapshot.RecentRecordings);
            Assert.Equal(sessionId, migratedRecording.SessionId);
            Assert.Equal("Zoom", migratedRecording.SourceLabel);
            Assert.Equal(audioPath, migratedRecording.PrimaryAudioPath);
            Assert.Equal(RecentRecordingState.Ready, migratedRecording.State);
            var migratedApplication = Assert.Single(runtime.Snapshot.UserSettings.Applications);
            Assert.Equal("zoom", migratedApplication.ProfileId);
            Assert.Equal(MeetingApplicationPolicy.Ask, migratedApplication.Policy);
            Assert.DoesNotContain(
                runtime.Snapshot.UserSettings.Applications,
                static application => application.ProfileId.StartsWith(
                    "legacy:",
                    StringComparison.OrdinalIgnoreCase));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFilePath));
            Assert.Equal(ApplicationSettings.CurrentSchemaVersion, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("ask", document.RootElement.GetProperty("recording").GetProperty("mode").GetString());
            Assert.True(document.RootElement.GetProperty("release_v2").GetProperty("service_enabled").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    /// </summary>
    [Fact]
    public async Task RuntimeKeepsTranscriptOnlyLegacyRecordingReadyAndBrowsable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var settings = ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with
                {
                    OnboardingCompleted = false,
                    ServiceEnabled = false,
                    Autostart = false
                });
            await new JsonApplicationSettingsStore(paths)
                .SaveReleaseV2Async(settings, CancellationToken.None);

            var sessionId = Guid.NewGuid();
            var transcriptMarkdownPath = Path.Combine(root, "legacy-transcript.md");
            var transcriptJsonPath = Path.Combine(root, "legacy-transcript.json");
            await File.WriteAllTextAsync(transcriptMarkdownPath, "# Legacy transcript");
            await File.WriteAllTextAsync(transcriptJsonPath, "{\"text\":\"Legacy transcript\"}");
            await using (var database = await new SqliteDatabaseInitializer(paths)
                             .InitializeAsync(CancellationToken.None))
            {
                await new MeetingSessionRepository(database).UpsertAsync(
                    MeetingSessionRecord.Create(
                        sessionId,
                        DateTimeOffset.UtcNow.AddHours(-1),
                        "ask",
                        "process_output+mic",
                        "output-1",
                        "mic-1") with
                    {
                        EndedAtUtc = DateTimeOffset.UtcNow,
                        Status = "saved",
                        SourceApp = "Zoom",
                        DurationSeconds = 3600,
                        TranscriptionStatus = "completed",
                        TranscriptMarkdownPath = transcriptMarkdownPath,
                        TranscriptJsonPath = transcriptJsonPath
                    },
                    CancellationToken.None);
            }

            await using var runtime = new WindowsReleaseV2Runtime(root, new FakeAudioPlatform());
            await runtime.InitializeAsync(CancellationToken.None);

            var recording = Assert.Single(runtime.Snapshot.RecentRecordings);
            Assert.Equal(sessionId, recording.SessionId);
            Assert.Equal(RecentRecordingState.Ready, recording.State);
            Assert.False(recording.RequiresAttention);
            Assert.Null(recording.PrimaryAudioPath);
            Assert.Equal(transcriptMarkdownPath, recording.LegacyTranscriptMarkdownPath);
            Assert.Equal(transcriptJsonPath, recording.LegacyTranscriptJsonPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task RuntimeCompletesOnboardingAndPersistsUserFacingSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-user-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveAsync(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with { ServiceEnabled = false }),
                CancellationToken.None);

            var recordingsFolder = Path.Combine(root, "recordings");
            var autostart = new TestAutostartService(enabled: true);
            await using (var runtime = new WindowsReleaseV2Runtime(
                             root,
                             new FakeAudioPlatform(),
                             MeetingDetectionMode.Live,
                             detectionCoordinatorFactory: null,
                             autostartService: autostart))
            {
                await runtime.InitializeAsync(CancellationToken.None);
                Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);

                var setup = new RuntimeUserSettingsUpdate(
                    ServiceEnabled: false,
                    Autostart: false,
                    Notifications: false,
                    Language: "EN",
                    Theme: "DARK",
                    MicrophoneDeviceId: "mic-7",
                    FollowSystemDefaultMicrophone: false,
                    RecordingsFolder: recordingsFolder,
                    Applications:
                    [
                        new MeetingApplicationPreference(" Zoom ", " Zoom Workplace ", MeetingApplicationPolicy.Ask)
                    ]);

                await runtime.CompleteOnboardingAsync(setup, CancellationToken.None);

                var completed = runtime.Snapshot.UserSettings;
                Assert.True(completed.OnboardingCompleted);
                Assert.False(completed.ServiceEnabled);
                Assert.False(completed.Autostart);
                Assert.False(autostart.IsEnabled);
                Assert.False(completed.Notifications);
                Assert.Equal("en", completed.Language);
                Assert.Equal("dark", completed.Theme);
                Assert.Equal("dark", runtime.Snapshot.Theme);
                Assert.Equal("mic-7", completed.MicrophoneDeviceId);
                Assert.False(completed.FollowSystemDefaultMicrophone);
                Assert.Equal(recordingsFolder, completed.RecordingsFolder);
                Assert.Equal("zoom", Assert.Single(completed.Applications).ProfileId);

                await runtime.UpdateSettingsAsync(
                    completed.ToUpdate() with
                    {
                        Notifications = true,
                        Theme = "light",
                        MicrophoneDeviceId = null,
                        FollowSystemDefaultMicrophone = true
                    },
                    CancellationToken.None);

                Assert.True(runtime.Snapshot.UserSettings.OnboardingCompleted);
                Assert.True(runtime.Snapshot.UserSettings.Notifications);
                Assert.Equal("light", runtime.Snapshot.Theme);
                Assert.Null(runtime.Snapshot.UserSettings.MicrophoneDeviceId);
                Assert.True(runtime.Snapshot.UserSettings.FollowSystemDefaultMicrophone);
            }

            var persisted = await store.LoadAsync(CancellationToken.None);
            Assert.True(persisted.OnboardingCompleted);
            Assert.False(persisted.General.Autostart);
            Assert.True(persisted.General.Notifications);
            Assert.Equal("en", persisted.General.Language);
            Assert.Equal("light", persisted.General.AppTheme);
            Assert.Equal("off", persisted.Recording.Mode);
            Assert.Null(persisted.Devices.MicrophoneDeviceId);
            Assert.True(persisted.Devices.FollowSystemDefaultMic);
            Assert.Equal(recordingsFolder, persisted.Storage.RecordingsFolder);
            Assert.NotNull(persisted.ReleaseV2);
            Assert.True(persisted.ReleaseV2.OnboardingCompleted);
            Assert.Equal("light", persisted.ReleaseV2.Theme);

            await using var reloadedRuntime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart);
            await reloadedRuntime.InitializeAsync(CancellationToken.None);
            Assert.Equal(persisted.ReleaseV2.Language, reloadedRuntime.Snapshot.UserSettings.Language);
            Assert.Equal("light", reloadedRuntime.Snapshot.UserSettings.Theme);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PackagedInitializationReconcilesThePersistedAutostartPreference(
        bool persistedPreference)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-autostart-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveAsync(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with
                    {
                        OnboardingCompleted = true,
                        ServiceEnabled = false,
                        Autostart = persistedPreference
                    }),
                CancellationToken.None);
            var autostart = new TestAutostartReconciler(initiallyEnabled: !persistedPreference);

            await using var runtime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Equal(persistedPreference, autostart.LastDesiredState);
            Assert.Equal(persistedPreference, runtime.Snapshot.UserSettings.Autostart);
            Assert.Equal(1, autostart.ReconcileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    [Fact]
    public async Task PackagedAutostartKeepsDesiredPreferenceAcrossWindowsDenialAndExternalReEnable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-autostart-observed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveAsync(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with
                    {
                        OnboardingCompleted = true,
                        ServiceEnabled = false,
                        Autostart = true,
                        AutostartPreference = true
                    }),
                CancellationToken.None);
            var deniedByWindows = new ObservedAutostartReconciler(effectiveState: false);

            await using (var runtime = new WindowsReleaseV2Runtime(
                             root,
                             new FakeAudioPlatform(),
                             MeetingDetectionMode.Live,
                             detectionCoordinatorFactory: null,
                             autostartService: deniedByWindows))
            {
                await runtime.InitializeAsync(CancellationToken.None);
                Assert.False(runtime.Snapshot.UserSettings.Autostart);
                Assert.True(deniedByWindows.LastDesiredState);

                await runtime.UpdateSettingsAsync(
                    runtime.Snapshot.UserSettings.ToUpdate() with { Theme = "dark" },
                    CancellationToken.None);

                Assert.Equal("dark", runtime.Snapshot.UserSettings.Theme);
                Assert.Equal(0, deniedByWindows.SetCount);
            }

            var deniedState = await store.LoadAsync(CancellationToken.None);
            Assert.False(deniedState.ReleaseV2!.Autostart);
            Assert.True(deniedState.ReleaseV2.AutostartPreference);
            Assert.True(deniedState.General.Autostart);

            var enabledInWindows = new ObservedAutostartReconciler(effectiveState: true);
            await using var reloaded = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: enabledInWindows);
            await reloaded.InitializeAsync(CancellationToken.None);

            Assert.True(enabledInWindows.LastDesiredState);
            Assert.True(reloaded.Snapshot.UserSettings.Autostart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    [Fact]
    public async Task ExternalWindowsEnableReplacesAPreviouslyDisabledAppPreference()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-autostart-external-enable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveAsync(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with
                    {
                        OnboardingCompleted = true,
                        ServiceEnabled = false,
                        Autostart = false,
                        AutostartPreference = false,
                        PackagedAutostartObserved = false
                    }),
                CancellationToken.None);
            var autostart = new ObservedAutostartReconciler(
                effectiveState: true,
                acceptEffectiveAsDesired: true);

            await using (var runtime = new WindowsReleaseV2Runtime(
                             root,
                             new FakeAudioPlatform(),
                             MeetingDetectionMode.Live,
                             detectionCoordinatorFactory: null,
                             autostartService: autostart))
            {
                await runtime.InitializeAsync(CancellationToken.None);
                Assert.True(runtime.Snapshot.UserSettings.Autostart);
                Assert.False(autostart.LastDesiredState);
                Assert.False(autostart.LastObservedPackagedState);
            }

            var persisted = await store.LoadAsync(CancellationToken.None);
            Assert.True(persisted.ReleaseV2!.Autostart);
            Assert.True(persisted.ReleaseV2.AutostartPreference);
            Assert.True(persisted.ReleaseV2.PackagedAutostartObserved);
            Assert.True(persisted.General.Autostart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
    [Fact]
    public async Task PackagedInitializationCleansLegacyAutostartBeforeOnboardingCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-autostart-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var autostart = new ObservedAutostartReconciler(effectiveState: false);
            await using var runtime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart);

            await runtime.InitializeAsync(CancellationToken.None);

            Assert.False(runtime.Snapshot.UserSettings.OnboardingCompleted);
            Assert.Equal(1, autostart.CleanupCount);
            Assert.Equal(0, autostart.ReconcileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SettingsSaveRollbackRestoresEffectiveAutostartAfterLegacyFallbackCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string legacyPath = @"C:\Legacy\IsTranscribe.App.exe";
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-autostart-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var initial = ApplicationSettings.Default.WithReleaseV2Projection(
                ReleaseV2Settings.Default with
                {
                    OnboardingCompleted = true,
                    ServiceEnabled = false,
                    Autostart = true,
                    AutostartPreference = true,
                    PackagedAutostartObserved = null
                });
            var settingsStore = new FaultInjectingSettingsStore(initial);
            var packaged = new FailFirstEnableAutostartService();
            var legacy = new InMemoryLegacyRunKeyStore
            {
                Command = "\"C:\\Legacy\\IsTranscribe.App.exe\" --autostart"
            };
            var autostart = new PackagedWindowsAutostartMigrationService(
                packaged,
                legacy,
                path => string.Equals(path, legacyPath, StringComparison.OrdinalIgnoreCase));

            await using var runtime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart,
                settingsStore: settingsStore);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.True(runtime.Snapshot.UserSettings.Autostart);
            Assert.False(packaged.IsEnabled);
            Assert.NotNull(legacy.Command);

            settingsStore.FailNextSave = true;
            await Assert.ThrowsAsync<IOException>(() =>
                runtime.UpdateSettingsAsync(
                    runtime.Snapshot.UserSettings.ToUpdate() with { Autostart = false },
                    CancellationToken.None).AsTask());

            Assert.True(runtime.Snapshot.UserSettings.Autostart);
            Assert.True(settingsStore.Settings.ReleaseV2!.Autostart);
            Assert.True(packaged.IsEnabled);
            Assert.Null(legacy.Command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RuntimeRollsBackAutostartWhenSettingsPersistenceFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-autostart-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var releaseV2 = ReleaseV2Settings.Default with
            {
                OnboardingCompleted = true,
                ServiceEnabled = false,
                Autostart = true
            };
            var settingsStore = new FaultInjectingSettingsStore(
                ApplicationSettings.Default.WithReleaseV2Projection(releaseV2));
            var autostart = new TestAutostartService(enabled: false);
            await using var runtime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart,
                settingsStore: settingsStore);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.False(runtime.Snapshot.UserSettings.Autostart);
            Assert.False(settingsStore.Settings.ReleaseV2!.Autostart);
            settingsStore.FailNextSave = true;

            await Assert.ThrowsAsync<IOException>(() => runtime.UpdateSettingsAsync(
                    runtime.Snapshot.UserSettings.ToUpdate() with { Autostart = true },
                    CancellationToken.None)
                .AsTask());

            Assert.False(autostart.IsEnabled);
            Assert.Equal(2, autostart.SetCallCount);
            Assert.False(runtime.Snapshot.UserSettings.Autostart);
            Assert.False(settingsStore.Settings.ReleaseV2!.Autostart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RuntimeRollsBackAPartiallyAppliedAutostartFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-autostart-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var releaseV2 = ReleaseV2Settings.Default with
            {
                OnboardingCompleted = true,
                ServiceEnabled = false,
                Autostart = false
            };
            var settingsStore = new FaultInjectingSettingsStore(
                ApplicationSettings.Default.WithReleaseV2Projection(releaseV2));
            var autostart = new TestAutostartService(enabled: false)
            {
                FailNextSet = true,
                MutateBeforeFailure = true
            };
            await using var runtime = new WindowsReleaseV2Runtime(
                root,
                new FakeAudioPlatform(),
                MeetingDetectionMode.Live,
                detectionCoordinatorFactory: null,
                autostartService: autostart,
                settingsStore: settingsStore);
            await runtime.InitializeAsync(CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.UpdateSettingsAsync(
                    runtime.Snapshot.UserSettings.ToUpdate() with { Autostart = true },
                    CancellationToken.None)
                .AsTask());

            Assert.False(autostart.IsEnabled);
            Assert.Equal(2, autostart.SetCallCount);
            Assert.False(runtime.Snapshot.UserSettings.Autostart);
            Assert.False(settingsStore.Settings.ReleaseV2!.Autostart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
    [Fact]
    public async Task RuntimePublishesActiveMicrophoneCatalogAndKeepsItSynced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-v2-microphones-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var store = new JsonApplicationSettingsStore(paths);
            await store.SaveAsync(
                ApplicationSettings.Default.WithReleaseV2Projection(
                    ReleaseV2Settings.Default with { ServiceEnabled = false }),
                CancellationToken.None);
            var audio = new FakeAudioPlatform(
            [
                new AudioEndpointSnapshot("mic-secondary", "Studio mic", false, true),
                new AudioEndpointSnapshot("mic-default", "Laptop mic", true, true),
                new AudioEndpointSnapshot("mic-offline", "Offline mic", false, false)
            ]);

            await using var runtime = new WindowsReleaseV2Runtime(root, audio);
            await runtime.InitializeAsync(CancellationToken.None);

            Assert.Collection(
                runtime.Snapshot.AvailableMicrophones,
                microphone =>
                {
                    Assert.Equal("mic-default", microphone.Id);
                    Assert.Equal("Laptop mic", microphone.DisplayName);
                    Assert.True(microphone.IsDefault);
                },
                microphone => Assert.Equal("mic-secondary", microphone.Id));

            audio.PublishMicrophones(
            [
                new AudioEndpointSnapshot("mic-usb", "USB mic", true, true)
            ]);

            var updated = Assert.Single(runtime.Snapshot.AvailableMicrophones);
            Assert.Equal("mic-usb", updated.Id);
            Assert.Equal("USB mic", updated.DisplayName);
            Assert.True(updated.IsDefault);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeAudioPlatform(
        IReadOnlyList<AudioEndpointSnapshot>? initialMicrophones = null) : IAudioPlatform
    {
        public event EventHandler<AudioPlatformSnapshot>? SnapshotChanged;

        public AudioPlatformSnapshot Snapshot { get; private set; } = AudioPlatformSnapshot.Empty;

        public ValueTask StartAsync(
            IReadOnlyCollection<string> watchedProcessNames,
            CancellationToken cancellationToken)
        {
            Snapshot = AudioPlatformSnapshot.Empty with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Capabilities = new AudioPlatformCapabilities(
                    IsSupported: true,
                    SupportsProcessOutputCapture: true,
                    Summary: "Test audio platform"),
                Microphones = initialMicrophones ?? []
            };
            SnapshotChanged?.Invoke(this, Snapshot);
            return ValueTask.CompletedTask;
        }

        public void UpdateWatchedProcessNames(IReadOnlyCollection<string> processNames)
        {
        }

        public void PublishMicrophones(IReadOnlyList<AudioEndpointSnapshot> microphones)
        {
            Snapshot = Snapshot with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Microphones = microphones
            };
            SnapshotChanged?.Invoke(this, Snapshot);
        }

        public ValueTask<IAudioCaptureSession> StartCaptureAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IAudioCaptureSession>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestAutostartReconciler(bool initiallyEnabled) : IAutostartStateReconciler
    {
        private bool _enabled = initiallyEnabled;

        public bool? LastDesiredState { get; private set; }

        public int ReconcileCount { get; private set; }

        public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AutostartRegistrationState(_enabled));
        }

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _enabled = enabled;
            return ValueTask.FromResult(new AutostartRegistrationState(_enabled));
        }

        public ValueTask<AutostartReconciliationResult> ReconcileDesiredStateAsync(
            bool desiredEnabled,
            bool? lastObservedPackagedState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDesiredState = desiredEnabled;
            ReconcileCount++;
            _enabled = desiredEnabled;
            return ValueTask.FromResult(
                new AutostartReconciliationResult(
                    _enabled,
                    desiredEnabled,
                    _enabled));
        }

        public ValueTask CleanupMigrationArtifactsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ObservedAutostartReconciler(
        bool effectiveState,
        bool acceptEffectiveAsDesired = false) : IAutostartStateReconciler
    {
        public bool? LastDesiredState { get; private set; }

        public int ReconcileCount { get; private set; }

        public int CleanupCount { get; private set; }

        public int SetCount { get; private set; }

        public bool? LastObservedPackagedState { get; private set; }

        public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AutostartRegistrationState(effectiveState));
        }

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCount++;
            return ValueTask.FromResult(new AutostartRegistrationState(enabled));
        }

        public ValueTask<AutostartReconciliationResult> ReconcileDesiredStateAsync(
            bool desiredEnabled,
            bool? lastObservedPackagedState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDesiredState = desiredEnabled;
            LastObservedPackagedState = lastObservedPackagedState;
            ReconcileCount++;
            return ValueTask.FromResult(
                new AutostartReconciliationResult(
                    effectiveState,
                    acceptEffectiveAsDesired ? effectiveState : desiredEnabled,
                    effectiveState));
        }

        public ValueTask CleanupMigrationArtifactsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailFirstEnableAutostartService : IAutostartService
    {
        private bool _failNextEnable = true;

        public bool IsEnabled { get; private set; }

        public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AutostartRegistrationState(IsEnabled));
        }

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled && _failNextEnable)
            {
                _failNextEnable = false;
                return ValueTask.FromException<AutostartRegistrationState>(
                    new InvalidOperationException("Injected first packaged enable failure."));
            }

            IsEnabled = enabled;
            return ValueTask.FromResult(new AutostartRegistrationState(IsEnabled));
        }
    }

    private sealed class InMemoryLegacyRunKeyStore : IWindowsRunKeyStore
    {
        public string? Command { get; set; }

        public string? Read(string valueName) => Command;

        public void Write(string valueName, string command) => Command = command;

        public void Delete(string valueName) => Command = null;
    }

    private sealed class FaultInjectingSettingsStore(ApplicationSettings settings) : IApplicationSettingsStore
    {
        public ApplicationSettings Settings { get; private set; } = settings;

        public bool FailNextSave { get; set; }

        public ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Settings);

        public ValueTask SaveAsync(ApplicationSettings updatedSettings, CancellationToken cancellationToken)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                return ValueTask.FromException(new IOException("Injected settings persistence failure."));
            }

            Settings = updatedSettings;
            return ValueTask.CompletedTask;
        }
    }
}
