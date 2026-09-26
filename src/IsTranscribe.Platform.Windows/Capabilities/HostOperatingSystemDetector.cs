using System.Runtime.InteropServices;

namespace IsTranscribe.Host.Capabilities;

public sealed class HostOperatingSystemDetector
{
    public HostOperatingSystemInfo Detect()
    {
        var version = Environment.OSVersion.Version;
        return new HostOperatingSystemInfo(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            version,
            RuntimeInformation.OSDescription);
    }
}
