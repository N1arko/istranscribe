using IsTranscribe.Host.Capabilities;

namespace IsTranscribe.Host.Audio;

public sealed record AudioFoundationCapabilitySnapshot(
    bool ProcessLoopbackSupported,
    bool DeviceLoopbackSupported,
    bool MicrophoneCaptureSupported,
    bool AudioSessionObservationSupported,
    bool DeviceNotificationsSupported)
{
    public static AudioFoundationCapabilitySnapshot FromHostCapability(HostCapabilitySnapshot capability)
    {
        var foundationAvailable = capability.State is HostCapabilityState.Full or HostCapabilityState.Degraded;

        return new AudioFoundationCapabilitySnapshot(
            ProcessLoopbackSupported: capability.ProcessLoopbackAvailable,
            DeviceLoopbackSupported: foundationAvailable,
            MicrophoneCaptureSupported: foundationAvailable,
            AudioSessionObservationSupported: foundationAvailable,
            DeviceNotificationsSupported: foundationAvailable);
    }
}
