namespace IsTranscribe.Host.Routing;

public enum StartupSurface
{
    None,
    FirstRunWizard,
    MainShell,
    Recovery
}

public sealed record StartupRoutingDecision(
    StartupSurface Surface,
    bool StartHiddenInTray,
    string Reason);
