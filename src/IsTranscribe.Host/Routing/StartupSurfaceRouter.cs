using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Recovery;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Routing;

public sealed class StartupSurfaceRouter
{
    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#entrypoints.first-run
    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#entrypoints.normal
    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#entrypoints.recovery
    public StartupRoutingDecision Decide(
        HostCapabilitySnapshot capability,
        BootstrapSettingsSnapshot settings,
        RecoveryLaunchDecision recovery,
        HostLaunchContext launch)
    {
        if (capability.State == HostCapabilityState.Blocked)
        {
            throw new InvalidOperationException("Blocked hosts must not be routed into interactive startup surfaces.");
        }

        if (!settings.OnboardingCompleted)
        {
            return new StartupRoutingDecision(
                StartupSurface.FirstRunWizard,
                StartHiddenInTray: false,
                Reason: "Onboarding is incomplete, so the wizard is the canonical entrypoint.");
        }

        if (recovery.RequiresAttention)
        {
            return new StartupRoutingDecision(
                StartupSurface.Recovery,
                StartHiddenInTray: false,
                Reason: "Recovery requires explicit user attention.");
        }

        if (launch.Origin == HostLaunchOrigin.AutoStart)
        {
            return new StartupRoutingDecision(
                StartupSurface.None,
                StartHiddenInTray: true,
                Reason: "Autostart uses tray-first mode after onboarding.");
        }

        return new StartupRoutingDecision(
            StartupSurface.MainShell,
            StartHiddenInTray: false,
            Reason: "Explicit user launch opens the app shell.");
    }
}
