using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class SetupViewModelTests
{
    [Fact]
    public void Generic_browser_is_the_only_application_option_marked_as_fallback()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SetupViewModel(runtime, new FakeLocalizationService(), initial);

        var fallback = Assert.Single(viewModel.Applications, static option => option.IsBrowserFallback);

        Assert.Equal("generic-browser", fallback.ProfileId);
        Assert.All(
            viewModel.Applications.Where(static option => option.ProfileId != "generic-browser"),
            static option => Assert.False(option.IsBrowserFallback));
    }

    [Fact]
    public async Task Language_switch_preserves_the_exact_setup_error_category()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial))
        {
            ActionFailure = new IOException("capability unavailable")
        };
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new SetupViewModel(runtime, strings, initial);

        await viewModel.RetryCapabilitiesCommand.ExecuteAsync(null);
        Assert.Equal("ru:String.Setup.Capability.CheckFailed", viewModel.ErrorMessage);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.Setup.Capability.CheckFailed", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Completion_preserves_migrated_choices_including_unknown_application()
    {
        var customApplication = new MeetingApplicationPreference(
            "company-call",
            "Company Call",
            MeetingApplicationPolicy.Ignore);
        var initial = Settings(
            language: "en",
            theme: "dark",
            microphoneDeviceId: "mic-2",
            followSystemDefault: false,
            applications: [customApplication]);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial,
            [new RuntimeMicrophoneSnapshot("mic-2", "Desk microphone", IsDefault: false)]);
        var completed = 0;
        viewModel.Completed += (_, _) => completed++;

        await viewModel.CompleteCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.CompletedOnboardingUpdates);
        Assert.Equal(1, completed);
        Assert.Equal("en", update.Language);
        Assert.Equal("dark", update.Theme);
        Assert.Equal("mic-2", update.MicrophoneDeviceId);
        Assert.False(update.FollowSystemDefaultMicrophone);
        var preserved = Assert.Single(
            update.Applications,
            application => application.ProfileId == customApplication.ProfileId);
        Assert.Equal(customApplication.DisplayName, preserved.DisplayName);
        Assert.Equal(MeetingApplicationPolicy.Ignore, preserved.Policy);
    }

    [Fact]
    public async Task System_default_microphone_and_app_owned_folder_are_submitted_as_explicit_defaults()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: "missing-device",
            followSystemDefault: true,
            applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial,
            availableMicrophones: []);

        await viewModel.CompleteCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.CompletedOnboardingUpdates);
        Assert.Null(update.MicrophoneDeviceId);
        Assert.True(update.FollowSystemDefaultMicrophone);
        Assert.Null(update.RecordingsFolder);
        Assert.True(update.ServiceEnabled);
        Assert.True(update.Autostart);
    }

    [Fact]
    public async Task Completion_failure_stays_on_setup_and_explains_save_problem()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial))
        {
            ActionFailure = new IOException("settings unavailable")
        };
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial);
        var completed = 0;
        viewModel.Completed += (_, _) => completed++;

        await viewModel.CompleteCommand.ExecuteAsync(null);

        Assert.Equal(0, completed);
        Assert.True(viewModel.HasError);
        Assert.Equal("String.Error.SettingsSave", viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(
        RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
        "String.Setup.Capability.ConfiguredMicrophoneUnavailable")]
    [InlineData(
        RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
        "String.Setup.Capability.MicrophoneCaptureUnavailable")]
    [InlineData(
        RuntimeCapabilityIssue.OutputCaptureUnavailable,
        "String.Setup.Capability.OutputCaptureUnavailable")]
    public void Actionable_capture_issue_is_explained_inline(
        RuntimeCapabilityIssue issue,
        string expectedMessage)
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = issue,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            userSettings: initial,
            capability: capability));
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial);

        Assert.True(viewModel.HasCapabilityNotice);
        Assert.Equal(expectedMessage, viewModel.CapabilityMessage);
    }

    [Fact]
    public async Task Completion_preserves_an_unavailable_explicit_microphone_until_user_changes_it()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: "missing-mic",
            followSystemDefault: false,
            applications: []);
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(userSettings: initial));
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial,
            availableMicrophones: []);

        Assert.Equal("missing-mic", viewModel.SelectedMicrophone.Id);
        Assert.True(viewModel.SelectedMicrophone.IsUnavailable);

        await viewModel.CompleteCommand.ExecuteAsync(null);

        var update = Assert.Single(runtime.CompletedOnboardingUpdates);
        Assert.Equal("missing-mic", update.MicrophoneDeviceId);
        Assert.False(update.FollowSystemDefaultMicrophone);
    }

    [Fact]
    public async Task Limited_microphone_capture_requires_one_explicit_confirmation_before_onboarding()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            userSettings: initial,
            capability: new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Full,
                SupportsProcessOutputCapture: true,
                Summary: string.Empty)
            {
                HasActiveOutput = true,
                HasActiveMicrophone = true
            }))
        {
            SnapshotAfterCapabilitiesRefresh = SnapshotFactory.Create(
                userSettings: initial,
                capability: capability)
        };
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial);
        var completed = 0;
        viewModel.Completed += (_, _) => completed++;

        await viewModel.CompleteCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.RefreshCapabilitiesCalls);
        Assert.Empty(runtime.CompletedOnboardingUpdates);
        Assert.Equal(0, completed);
        Assert.Equal("String.Setup.ContinueWithoutMicrophone", viewModel.CompleteActionText);

        await viewModel.CompleteCommand.ExecuteAsync(null);

        Assert.Single(runtime.CompletedOnboardingUpdates);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Fully_blocked_audio_requires_retry_or_disabling_the_service()
    {
        var initial = Settings(
            language: "ru",
            theme: "system",
            microphoneDeviceId: null,
            followSystemDefault: true,
            applications: []);
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Blocked,
            SupportsProcessOutputCapture: false,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.NoActiveAudioEndpoints
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            userSettings: initial,
            capability: capability));
        using var viewModel = new SetupViewModel(
            runtime,
            new FakeLocalizationService(),
            initial);

        Assert.False(viewModel.CompleteCommand.CanExecute(null));

        viewModel.ServiceEnabled = false;

        Assert.True(viewModel.CompleteCommand.CanExecute(null));
    }

    private static RuntimeUserSettingsSnapshot Settings(
        string language,
        string theme,
        string? microphoneDeviceId,
        bool followSystemDefault,
        IReadOnlyList<MeetingApplicationPreference> applications) => new(
        OnboardingCompleted: false,
        ServiceEnabled: true,
        Autostart: true,
        Notifications: true,
        language,
        theme,
        microphoneDeviceId,
        followSystemDefault,
        RecordingsFolder: null,
        applications);
}
