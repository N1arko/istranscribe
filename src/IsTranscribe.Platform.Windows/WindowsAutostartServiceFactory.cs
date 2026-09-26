using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Selects the Windows launch-at-login integration that matches the current activation contour.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#scope.in
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsAutostartServiceFactory
{
    public static IAutostartService Create() => Create(
        new WindowsPackageIdentityDetector(),
        static () => new PackagedWindowsAutostartMigrationService(
            new WindowsStartupTaskAutostartService(),
            new WindowsRegistryRunKeyStore()),
        static () => new WindowsRunKeyAutostartService());

    internal static IAutostartService Create(
        IWindowsPackageIdentityDetector packageIdentityDetector,
        Func<IAutostartService> packagedFactory,
        Func<IAutostartService> unpackagedFactory)
    {
        ArgumentNullException.ThrowIfNull(packageIdentityDetector);
        ArgumentNullException.ThrowIfNull(packagedFactory);
        ArgumentNullException.ThrowIfNull(unpackagedFactory);

        return packageIdentityDetector.HasPackageIdentity()
            ? packagedFactory()
            : unpackagedFactory();
    }
}
