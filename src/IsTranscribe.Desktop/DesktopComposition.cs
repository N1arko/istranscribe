using IsTranscribe.Application.Platform;
using IsTranscribe.Core.Platform;
using IsTranscribe.Application.Runtime;

namespace IsTranscribe.Desktop;

/// <summary>
/// Immutable platform context supplied by a thin executable composition root.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#desktop
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#entrypoints-and-windows-identity
/// </remarks>
public sealed record DesktopComposition(
    Func<IReadOnlyList<string>, IApplicationRuntime> CreateRuntime,
    Func<IApplicationInstanceCoordinator> CreateInstanceCoordinator,
    IPlatformDescriptor PlatformDescriptor,
    IPlatformShell Shell,
    IActiveWorkAreaProvider ActiveWorkAreaProvider,
    ISystemNotificationService? SystemNotifications = null,
    IPermissionService? Permissions = null,
    Action<nint>? PresentAskPromptWindow = null)
{
    private static DesktopComposition? s_current;

    public static DesktopComposition Current =>
        Volatile.Read(ref s_current)
        ?? throw new InvalidOperationException(
            "A desktop platform composition must be configured before Avalonia starts.");

    public static void Configure(DesktopComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (Interlocked.CompareExchange(ref s_current, composition, null) is not null)
        {
            throw new InvalidOperationException("The desktop platform composition is already configured.");
        }
    }
}
