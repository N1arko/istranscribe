using System.Runtime.InteropServices;
using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Windows implementation of the platform descriptor consumed by the desktop shell.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// </remarks>
public sealed class WindowsPlatformDescriptor : IPlatformDescriptor
{
    public string OperatingSystem => "Windows";

    public Architecture Architecture => RuntimeInformation.OSArchitecture;

    public bool IsReleaseArchitectureSupported =>
        System.OperatingSystem.IsWindows() && Architecture == Architecture.X64;
}
