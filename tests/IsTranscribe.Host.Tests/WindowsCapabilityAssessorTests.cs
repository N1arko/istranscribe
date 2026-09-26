using IsTranscribe.Host.Capabilities;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class WindowsCapabilityAssessorTests
{
    [Fact]
    public void Windows11IsFull()
    {
        var snapshot = WindowsCapabilityAssessor.Assess(new HostOperatingSystemInfo(true, new Version(10, 0, 22621), "Windows 11"));

        Assert.Equal(HostCapabilityState.Full, snapshot.State);
        Assert.True(snapshot.ProcessLoopbackAvailable);
    }

    [Fact]
    public void OlderWindows10IsDegraded()
    {
        var snapshot = WindowsCapabilityAssessor.Assess(new HostOperatingSystemInfo(true, new Version(10, 0, 19045), "Windows 10"));

        Assert.Equal(HostCapabilityState.Degraded, snapshot.State);
        Assert.False(snapshot.ProcessLoopbackAvailable);
    }

    [Fact]
    public void NonWindowsIsBlocked()
    {
        var snapshot = WindowsCapabilityAssessor.Assess(new HostOperatingSystemInfo(false, new Version(6, 8), "Linux"));

        Assert.Equal(HostCapabilityState.Blocked, snapshot.State);
        Assert.NotNull(snapshot.BlockingReason);
    }
}
