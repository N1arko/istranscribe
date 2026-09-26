using System.Diagnostics;
using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Platform;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions
/// </summary>
public sealed class MacOSSystemIntegrationTests
{
    [Fact]
    public async Task ConcurrentCoordinatorActivatesExactlyOnePrimary()
    {
        var identity = $"istranscribe-test-{Guid.NewGuid():N}";
        await using var primary = new MacOSApplicationInstanceCoordinator(identity);
        await using var secondary = new MacOSApplicationInstanceCoordinator(identity);
        Assert.Equal(ApplicationInstanceRole.Primary, await primary.RegisterAsync(CancellationToken.None));
        Assert.Equal(ApplicationInstanceRole.Secondary, await secondary.RegisterAsync(CancellationToken.None));
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => activated.TrySetResult();

        Assert.True(await secondary.NotifyPrimaryAsync(CancellationToken.None));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReleasedCoordinatorAllowsTheNextLaunchToBecomePrimary()
    {
        var identity = $"istranscribe-restart-{Guid.NewGuid():N}";
        var first = new MacOSApplicationInstanceCoordinator(identity);
        Assert.Equal(ApplicationInstanceRole.Primary, await first.RegisterAsync(CancellationToken.None));
        await first.DisposeAsync();

        await using var replacement = new MacOSApplicationInstanceCoordinator(identity);
        Assert.Equal(ApplicationInstanceRole.Primary, await replacement.RegisterAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(MacOSLoginItemStatus.Enabled, true, AutostartControlConstraint.None)]
    [InlineData(MacOSLoginItemStatus.NotRegistered, false, AutostartControlConstraint.None)]
    [InlineData(MacOSLoginItemStatus.RequiresApproval, false, AutostartControlConstraint.DisabledByUser)]
    public async Task LoginItemStateIsObservable(
        MacOSLoginItemStatus status,
        bool enabled,
        AutostartControlConstraint constraint)
    {
        var native = new FakeLoginItemNative { Status = status };
        var service = new MacOSAutostartService(native);

        var state = await service.GetStateAsync(CancellationToken.None);

        Assert.Equal(enabled, state.IsEnabled);
        Assert.Equal(constraint, state.Constraint);
    }

    [Fact]
    public async Task PermissionStatesIncludeNotRequestedDeniedGrantedAndNeedsRestart()
    {
        var native = new FakeSystemNative { SystemAudioPermission = 0, SystemAudioRequest = 2 };
        var service = new MacOSPermissionService(native);

        Assert.Equal(MacOSPermissionState.NotRequested, service.GetDetailedStatus("system_audio"));
        var requested = await service.RequestAsync("system_audio", CancellationToken.None);
        Assert.Equal(PlatformCapabilityState.NeedsRestart, requested.State);
        Assert.Contains("Restart", requested.Explanation, StringComparison.Ordinal);
        Assert.Equal(MacOSPermissionState.NeedsRestart, service.GetDetailedStatus("system_audio"));

        native.MicrophonePermission = 1;
        Assert.Equal(MacOSPermissionState.Denied, service.GetDetailedStatus("microphone"));
        native.MicrophonePermission = 2;
        Assert.Equal(MacOSPermissionState.Granted, service.GetDetailedStatus("microphone"));
    }

    [Fact]
    public async Task KeychainVaultCrudNeverUsesAPlaintextFile()
    {
        var native = new FakeSystemNative();
        var vault = new MacOSKeychainVault(native);

        await vault.WriteAsync("provider:token", "secret-value", CancellationToken.None);
        Assert.Equal("secret-value", await vault.ReadAsync("provider:token", CancellationToken.None));
        await vault.WriteAsync("provider:token", null, CancellationToken.None);
        Assert.Null(await vault.ReadAsync("provider:token", CancellationToken.None));
    }

    [Fact]
    public async Task FinderActionsValidatePathsAndUseRevealForARecording()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var recording = Path.Combine(root, "recording.mp3");
        await File.WriteAllBytesAsync(recording, [1, 2, 3]);
        ProcessStartInfo? observed = null;
        var shell = new MacOSPlatformShell(info =>
        {
            observed = info;
            return new Process();
        });
        try
        {
            await shell.OpenContainingFolderAsync(recording, CancellationToken.None);
            Assert.Equal("/usr/bin/open", observed!.FileName);
            Assert.Equal(["-R", recording], observed.ArgumentList);
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                shell.OpenFileAsync(Path.Combine(root, "missing.mp3"), CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DefaultPathsUseMacOSApplicationSupportAndDocuments()
    {
        var paths = new MacOSApplicationPlatformRuntimeAdapter().CreateAppPaths("isTranscribe", null);

        Assert.EndsWith(Path.Combine("Library", "Application Support", "isTranscribe"), paths.RootDirectory, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("Documents", "isTranscribe", "Recordings"), paths.DefaultRecordingsDirectory, StringComparison.Ordinal);
    }

    /// <summary>
    /// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
    /// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
    /// </summary>
    [Fact]
    public void AskPromptPresenterForwardsTheNativeWindowHandle()
    {
        var native = new FakeAskPromptWindowNative();
        var presenter = new MacOSAskPromptWindowPresenter(native);
        var handle = (nint)42;

        presenter.Present(handle);

        Assert.Equal(handle, native.PresentedHandle);
        Assert.Equal(1, native.CallCount);
    }

    [Fact]
    public void AskPromptPresenterRejectsMissingOrFailedNativePresentation()
    {
        var native = new FakeAskPromptWindowNative { Status = -2 };
        var presenter = new MacOSAskPromptWindowPresenter(native);

        Assert.Throws<ArgumentException>(() => presenter.Present(0));
        Assert.Equal(0, native.CallCount);
        Assert.Throws<InvalidOperationException>(() => presenter.Present((nint)42));
        Assert.Equal(1, native.CallCount);
    }

    private sealed class FakeLoginItemNative : IMacOSLoginItemNative
    {
        public MacOSLoginItemStatus Status { get; set; }
        public MacOSLoginItemStatus GetStatus() => Status;
        public int SetEnabled(bool enabled)
        {
            Status = enabled ? MacOSLoginItemStatus.Enabled : MacOSLoginItemStatus.NotRegistered;
            return 0;
        }
    }

    private sealed class FakeSystemNative : IMacOSSystemNative
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
        public int MicrophonePermission { get; set; }
        public int SystemAudioPermission { get; set; }
        public int ScreenCapturePermission { get; set; }
        public int AccessibilityPermission { get; set; }
        public int NotificationPermission { get; set; }
        public int SystemAudioRequest { get; set; }
        public int RequestMicrophone() => MicrophonePermission;
        public int RequestSystemAudio() => SystemAudioRequest;
        public int RequestScreenCapture() => ScreenCapturePermission;
        public int RequestAccessibility() => AccessibilityPermission;
        public int RequestNotifications() => NotificationPermission;
        public int ShowNotification(string title, string message) => 0;
        public byte[]? ReadSecret(string key) => _secrets.GetValueOrDefault(key);
        public void WriteSecret(string key, byte[]? value)
        {
            if (value is null) _secrets.Remove(key);
            else _secrets[key] = value;
        }
    }

    private sealed class FakeAskPromptWindowNative : IMacOSAskPromptWindowNative
    {
        public int Status { get; set; }
        public nint PresentedHandle { get; private set; }
        public int CallCount { get; private set; }

        public int Present(nint windowHandle)
        {
            PresentedHandle = windowHandle;
            CallCount++;
            return Status;
        }
    }
}
