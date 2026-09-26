using System.Runtime.InteropServices;

namespace IsTranscribe.Core.Platform;

/// <summary>
/// Describes the platform adapter selected by the desktop composition root.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#target-structure
/// </remarks>
public interface IPlatformDescriptor
{
    string OperatingSystem { get; }

    Architecture Architecture { get; }

    bool IsReleaseArchitectureSupported { get; }
}
