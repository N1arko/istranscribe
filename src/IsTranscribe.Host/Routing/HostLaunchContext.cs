namespace IsTranscribe.Host.Routing;

public enum HostLaunchOrigin
{
    UserLaunch,
    AutoStart
}

public enum HostActivationIntent
{
    OpenOrFocusApp
}

public sealed record HostLaunchContext(
    HostLaunchOrigin Origin,
    HostActivationIntent ActivationIntent = HostActivationIntent.OpenOrFocusApp);
