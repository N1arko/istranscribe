using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Capabilities;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class AudioFoundationCapabilitySnapshotTests
{
    [Fact]
    public void FullHostCapabilityEnablesAllFoundationFlags()
    {
        var snapshot = AudioFoundationCapabilitySnapshot.FromHostCapability(
            new HostCapabilitySnapshot(HostCapabilityState.Full, ProcessLoopbackAvailable: true, "full"));

        Assert.True(snapshot.ProcessLoopbackSupported);
        Assert.True(snapshot.DeviceLoopbackSupported);
        Assert.True(snapshot.MicrophoneCaptureSupported);
        Assert.True(snapshot.AudioSessionObservationSupported);
        Assert.True(snapshot.DeviceNotificationsSupported);
    }

    [Fact]
    public void DegradedHostCapabilityDisablesOnlyProcessLoopback()
    {
        var snapshot = AudioFoundationCapabilitySnapshot.FromHostCapability(
            new HostCapabilitySnapshot(HostCapabilityState.Degraded, ProcessLoopbackAvailable: false, "degraded"));

        Assert.False(snapshot.ProcessLoopbackSupported);
        Assert.True(snapshot.DeviceLoopbackSupported);
        Assert.True(snapshot.MicrophoneCaptureSupported);
        Assert.True(snapshot.AudioSessionObservationSupported);
        Assert.True(snapshot.DeviceNotificationsSupported);
    }

    [Fact]
    public void BlockedHostCapabilityDisablesAllFoundationFlags()
    {
        var snapshot = AudioFoundationCapabilitySnapshot.FromHostCapability(
            new HostCapabilitySnapshot(HostCapabilityState.Blocked, ProcessLoopbackAvailable: false, "blocked"));

        Assert.False(snapshot.ProcessLoopbackSupported);
        Assert.False(snapshot.DeviceLoopbackSupported);
        Assert.False(snapshot.MicrophoneCaptureSupported);
        Assert.False(snapshot.AudioSessionObservationSupported);
        Assert.False(snapshot.DeviceNotificationsSupported);
    }
}
