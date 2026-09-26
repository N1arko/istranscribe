namespace IsTranscribe.Host.Capabilities;

public sealed class WindowsCapabilityAssessor(HostOperatingSystemDetector detector) : IHostCapabilityAssessor
{
    private const int FullSupportWindows10Build = 20348;

    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.states
    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#capability-model.rules
    public HostCapabilitySnapshot Assess() => Assess(detector.Detect());

    public static HostCapabilitySnapshot Assess(HostOperatingSystemInfo os)
    {
        if (!os.IsWindows)
        {
            return new HostCapabilitySnapshot(
                HostCapabilityState.Blocked,
                ProcessLoopbackAvailable: false,
                Summary: "This host is not running on Windows.",
                BlockingReason: "isTranscribe MVP host supports Windows only.");
        }

        if (os.Major < 10)
        {
            return new HostCapabilitySnapshot(
                HostCapabilityState.Blocked,
                ProcessLoopbackAvailable: false,
                Summary: $"Unsupported Windows version: {os.Description}.",
                BlockingReason: "Windows 10 or later is required.");
        }

        if (os.IsWindows11 || os.Build >= FullSupportWindows10Build)
        {
            return new HostCapabilitySnapshot(
                HostCapabilityState.Full,
                ProcessLoopbackAvailable: true,
                Summary: "Full MVP capture contour is available.");
        }

        return new HostCapabilitySnapshot(
            HostCapabilityState.Degraded,
            ProcessLoopbackAvailable: false,
            Summary: "Process loopback is unavailable on this Windows build. Device loopback, microphone capture, tray controls and force record remain available.");
    }
}
