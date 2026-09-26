using System.Text.Json;
using IsTranscribe.Core.Audio;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#migration
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </summary>
public sealed class ReleaseV2SettingsTests
{
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    [Fact]
    public void TranscriptionPreferencesDefaultToExplicitOptInAndRequireZeroDataRetention()
    {
        var transcription = Assert.IsType<TranscriptionPreferences>(
            ReleaseV2Settings.Default.Transcription);

        Assert.Equal(3, ReleaseV2Settings.CurrentVersion);
        Assert.Null(transcription.SelectedEngineId);
        Assert.False(transcription.AutomaticEnabled);
        Assert.Equal("auto", transcription.Language);
        Assert.Empty(transcription.Engines);
        Assert.True(transcription.RequireZeroDataRetention);
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
    [Fact]
    public void TranscriptionCanonicalizationSupportsKnownAndFutureRegisteredEngines()
    {
        var settings = ReleaseV2Settings.Default with
        {
            Transcription = new TranscriptionPreferences(
                SelectedEngineId: " Vendor.Acme:Fast_STT-v2 ",
                AutomaticEnabled: true,
                Language: " PT_br ",
                Engines:
                [
                    new TranscriptionEnginePreference(
                        " REMOTE.GROQ ",
                        " whisper-large-v3-turbo ",
                        DisclosureAccepted: true,
                        DisclosureRevision: " FEAT015-V1 "),
                    new TranscriptionEnginePreference(
                        "remote.openrouter",
                        " openai/whisper-large-v3 ",
                        DisclosureAccepted: false),
                    new TranscriptionEnginePreference(
                        "LOCAL.WHISPER",
                        " ggml-small.bin ",
                        DisclosureAccepted: false),
                    new TranscriptionEnginePreference(
                        "Vendor.Acme:Fast_STT-v2",
                        " vendor/model-v2 ",
                        DisclosureAccepted: true),
                    new TranscriptionEnginePreference(
                        "https://invalid-engine",
                        "ignored",
                        DisclosureAccepted: true)
                ],
                RequireZeroDataRetention: false)
        };

        var canonical = settings.Canonicalize().Transcription!;

        Assert.Equal("vendor.acme:fast_stt-v2", canonical.SelectedEngineId);
        Assert.True(canonical.AutomaticEnabled);
        Assert.Equal("auto", canonical.Language);
        Assert.False(canonical.RequireZeroDataRetention);
        Assert.Equal(
            ["local.whisper", "remote.groq", "remote.openrouter", "vendor.acme:fast_stt-v2"],
            canonical.Engines.Select(static preference => preference.EngineId));
        Assert.Equal("whisper-large-v3-turbo", canonical.GetModelId("REMOTE.GROQ"));
        Assert.Equal("openai/whisper-large-v3-turbo", canonical.GetModelId("remote.openrouter"));
        Assert.Equal("large-v3-turbo", canonical.GetModelId("local.whisper"));
        Assert.Equal("vendor/model-v2", canonical.GetModelId("vendor.acme:fast_stt-v2"));
        Assert.True(canonical.HasAcceptedDisclosure("remote.groq"));
        Assert.True(canonical.HasAcceptedDisclosure("remote.groq", "feat015-v1"));
        Assert.False(canonical.HasAcceptedDisclosure("remote.groq", "feat015-v2"));
        Assert.False(canonical.HasAcceptedDisclosure("remote.openrouter"));
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
    [Fact]
    public void TranscriptionSettingsContractContainsNoCredentialFields()
    {
        var propertyNames = typeof(TranscriptionPreferences)
            .GetProperties()
            .Concat(typeof(TranscriptionEnginePreference).GetProperties())
            .Select(static property => property.Name)
            .ToArray();
        var json = JsonSerializer.Serialize(ReleaseV2Settings.Default);

        Assert.DoesNotContain(
            propertyNames,
            static name => name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    public void CanonicalizeFallsBackToRussianForUnsupportedLanguage(string? language)
    {
        var canonical = (ReleaseV2Settings.Default with { Language = language! }).Canonicalize();

        Assert.Equal("ru", canonical.Language);
    }

    [Fact]
    public void CanonicalizeNormalizesChoicesAndApplicationProfiles()
    {
        var settings = ReleaseV2Settings.Default with
        {
            Version = 1,
            Language = "RU",
            Theme = "UNKNOWN",
            Applications =
            [
                new MeetingApplicationPreference(" Zoom ", " Zoom Workplace ", MeetingApplicationPolicy.Ask),
                new MeetingApplicationPreference("zoom", "Duplicate", MeetingApplicationPolicy.Ignore),
                new MeetingApplicationPreference(" ", "Invalid", MeetingApplicationPolicy.Ask)
            ]
        };

        var canonical = settings.Canonicalize();

        Assert.Equal(ReleaseV2Settings.CurrentVersion, canonical.Version);
        Assert.Equal("ru", canonical.Language);
        Assert.Equal("system", canonical.Theme);
        var application = Assert.Single(canonical.Applications);
        Assert.Equal("zoom", application.ProfileId);
        Assert.Equal("Zoom Workplace", application.DisplayName);
    }

    // @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanonicalizeBackfillsAutostartPreferenceFromOlderSettings(bool effectiveState)
    {
        var canonical = (ReleaseV2Settings.Default with
        {
            Autostart = effectiveState,
            AutostartPreference = null
        }).Canonicalize();

        Assert.Equal(effectiveState, canonical.AutostartPreference);
    }

    [Fact]
    public void InitialRuntimeSnapshotIsPausedAndEmpty()
    {
        var snapshot = ApplicationRuntimeSnapshot.Initial;

        Assert.Equal(ApplicationActivityState.Paused, snapshot.Activity);
        Assert.False(snapshot.ServiceEnabled);
        Assert.Equal("system", snapshot.Theme);
        Assert.Null(snapshot.ActiveMeeting);
        Assert.Empty(snapshot.RecentRecordings);
        Assert.Empty(snapshot.AvailableMicrophones);
        Assert.False(snapshot.UserSettings.OnboardingCompleted);
        Assert.False(snapshot.UserSettings.ServiceEnabled);
        Assert.Equal("ru", snapshot.UserSettings.Language);
        Assert.Equal("system", snapshot.UserSettings.Theme);
    }

    [Fact]
    public void RuntimeUserSettingsSnapshotCreatesEditableUpdateWithoutOnboardingState()
    {
        var snapshot = RuntimeUserSettingsSnapshot.Initial with
        {
            OnboardingCompleted = true,
            ServiceEnabled = true,
            Language = "en",
            Applications =
            [
                new MeetingApplicationPreference("zoom", "Zoom", MeetingApplicationPolicy.Ask)
            ]
        };

        var update = snapshot.ToUpdate();

        Assert.True(update.ServiceEnabled);
        Assert.Equal("en", update.Language);
        Assert.Equal("zoom", Assert.Single(update.Applications).ProfileId);
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceWindowsUiOrAudioImplementationAssemblies()
    {
        var references = typeof(ReleaseV2Settings).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, static name => name.StartsWith("Presentation", StringComparison.Ordinal));
        Assert.DoesNotContain("System.Windows.Forms", references);
        Assert.DoesNotContain("NAudio", references);
        Assert.DoesNotContain("Wpf.Ui", references);
    }

    [Fact]
    public void CaptureRequestRejectsTwoOutputSources()
    {
        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Ask,
            [
                AudioCaptureSource.ProcessOutput(42, "zoom"),
                AudioCaptureSource.DeviceLoopback("default-output"),
                AudioCaptureSource.Microphone("default-microphone")
            ],
            Path.GetTempPath(),
            PrebufferSeconds: 15,
            CreateMixedArtifact: true);

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void CaptureRequestAcceptsPlatformNeutralProcessAndMicrophonePlan()
    {
        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Ask,
            [
                AudioCaptureSource.ProcessOutput(42, "zoom"),
                AudioCaptureSource.Microphone("default-microphone")
            ],
            Path.GetTempPath(),
            PrebufferSeconds: 15,
            CreateMixedArtifact: true);

        request.Validate();
    }
}
