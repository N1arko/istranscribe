using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Recovery;
using IsTranscribe.Host.Routing;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class StartupSurfaceRouterTests
{
    private readonly StartupSurfaceRouter _router = new();

    [Fact]
    public void IncompleteOnboardingAlwaysRoutesToWizard()
    {
        var decision = _router.Decide(
            capability: new HostCapabilitySnapshot(HostCapabilityState.Full, true, "full"),
            settings: BootstrapSettingsSnapshot.Default,
            recovery: RecoveryLaunchDecision.None,
            launch: new HostLaunchContext(HostLaunchOrigin.AutoStart));

        Assert.Equal(StartupSurface.FirstRunWizard, decision.Surface);
        Assert.False(decision.StartHiddenInTray);
    }

    [Fact]
    public void RecoveryOverridesNormalLaunchAfterOnboarding()
    {
        var decision = _router.Decide(
            capability: new HostCapabilitySnapshot(HostCapabilityState.Degraded, false, "degraded"),
            settings: new BootstrapSettingsSnapshot(true, true, true),
            recovery: new RecoveryLaunchDecision(true, "recovery"),
            launch: new HostLaunchContext(HostLaunchOrigin.UserLaunch));

        Assert.Equal(StartupSurface.Recovery, decision.Surface);
    }

    [Fact]
    public void AutostartUsesTrayOnlyWhenOnboardingIsComplete()
    {
        var decision = _router.Decide(
            capability: new HostCapabilitySnapshot(HostCapabilityState.Full, true, "full"),
            settings: new BootstrapSettingsSnapshot(true, true, true),
            recovery: RecoveryLaunchDecision.None,
            launch: new HostLaunchContext(HostLaunchOrigin.AutoStart));

        Assert.Equal(StartupSurface.None, decision.Surface);
        Assert.True(decision.StartHiddenInTray);
    }
}
