namespace IsTranscribe.Host.Capabilities;

public enum HostCapabilityState
{
    Full,
    Degraded,
    Blocked
}

public sealed record HostCapabilitySnapshot(
    HostCapabilityState State,
    bool ProcessLoopbackAvailable,
    string Summary,
    string? BlockingReason = null);

public sealed record HostOperatingSystemInfo(
    bool IsWindows,
    Version Version,
    string Description)
{
    public int Major => Version.Major;

    public int Build => Version.Build;

    public bool IsWindows11 => IsWindows && Major >= 10 && Build >= 22000;
}
