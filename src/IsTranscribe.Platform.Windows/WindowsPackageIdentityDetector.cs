using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace IsTranscribe.Platform.Windows;

internal interface IWindowsPackageIdentityDetector
{
    bool HasPackageIdentity();
}

/// <summary>
/// Detects whether the current Win32 process was activated with Windows package identity.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#package-decision
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPackageIdentityDetector : IWindowsPackageIdentityDetector
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public bool HasPackageIdentity()
    {
        uint packageFullNameLength = 0;
        var result = GetCurrentPackageFullName(ref packageFullNameLength, packageFullName: null);
        return result switch
        {
            ErrorSuccess or ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,
            _ => throw new Win32Exception(result, "Windows could not determine the current package identity.")
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        [Out] char[]? packageFullName);
}
