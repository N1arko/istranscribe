using IsTranscribe.Application.Platform;
using IsTranscribe.Desktop.ViewModels;
using Xunit;

namespace IsTranscribe.Desktop.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions
/// </summary>
public sealed class PlatformPermissionOptionViewModelTests
{
    [Theory]
    [InlineData(PlatformCapabilityState.NotRequested, false, false)]
    [InlineData(PlatformCapabilityState.PermissionRequired, false, true)]
    [InlineData(PlatformCapabilityState.NeedsRestart, false, true)]
    [InlineData(PlatformCapabilityState.Available, true, false)]
    public async Task CanonicalPermissionStatesRemainUserActionable(
        PlatformCapabilityState state,
        bool isGranted,
        bool showOpenSettings)
    {
        var service = new FakePermissionService { State = state };
        using var viewModel = new PlatformPermissionOptionViewModel(
            "microphone",
            "String.Permission.Microphone",
            service,
            new FakeLocalizationService());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(state, viewModel.State);
        Assert.Equal(isGranted, viewModel.IsGranted);
        Assert.Equal(showOpenSettings, viewModel.ShowOpenSettings);
        Assert.Equal(state != PlatformCapabilityState.Available, viewModel.RequestCommand.CanExecute(null));
        Assert.True(viewModel.OpenSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task PermissionPromptRunsOnlyFromExplicitCommand()
    {
        var service = new FakePermissionService
        {
            State = PlatformCapabilityState.NotRequested,
            RequestedState = PlatformCapabilityState.NeedsRestart
        };
        using var viewModel = new PlatformPermissionOptionViewModel(
            "system_audio",
            "String.Permission.SystemAudio",
            service,
            new FakeLocalizationService());

        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, service.RequestCount);

        await viewModel.RequestCommand.ExecuteAsync(null);

        Assert.Equal(1, service.RequestCount);
        Assert.Equal(PlatformCapabilityState.NeedsRestart, viewModel.State);
        Assert.True(viewModel.ShowOpenSettings);
    }

    private sealed class FakePermissionService : IPermissionService
    {
        public PlatformCapabilityState State { get; set; }

        public PlatformCapabilityState RequestedState { get; set; } = PlatformCapabilityState.Available;

        public int RequestCount { get; private set; }

        public ValueTask<PlatformCapability> GetStatusAsync(string permission, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlatformCapability(permission, State, string.Empty));

        public ValueTask<PlatformCapability> RequestAsync(string permission, CancellationToken cancellationToken)
        {
            RequestCount++;
            State = RequestedState;
            return ValueTask.FromResult(new PlatformCapability(permission, State, string.Empty));
        }

        public ValueTask OpenSystemSettingsAsync(string permission, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
