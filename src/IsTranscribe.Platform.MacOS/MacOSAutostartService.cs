using System.Runtime.InteropServices;
using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.MacOS;

public enum MacOSLoginItemStatus
{
    NotRegistered = 0,
    Enabled = 1,
    RequiresApproval = 2,
    NotFound = 3
}

internal interface IMacOSLoginItemNative
{
    MacOSLoginItemStatus GetStatus();

    int SetEnabled(bool enabled);
}

/// <summary>
/// Observable main-app login item backed by SMAppService.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// </remarks>
public sealed class MacOSAutostartService : IAutostartService
{
    private readonly IMacOSLoginItemNative _native;

    public MacOSAutostartService() : this(new NativeBridge())
    {
    }

    internal MacOSAutostartService(IMacOSLoginItemNative native) => _native = native;

    public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Map(_native.GetStatus()));
    }

    public ValueTask<AutostartRegistrationState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = _native.SetEnabled(enabled);
        if (status != 0)
        {
            throw new InvalidOperationException($"SMAppService rejected the login-item change ({status}).");
        }
        return ValueTask.FromResult(Map(_native.GetStatus()));
    }

    private static AutostartRegistrationState Map(MacOSLoginItemStatus status) => status switch
    {
        MacOSLoginItemStatus.Enabled => new(true),
        MacOSLoginItemStatus.RequiresApproval => new(false, AutostartControlConstraint.DisabledByUser),
        _ => new(false)
    };

    private sealed class NativeBridge : IMacOSLoginItemNative
    {
        public MacOSLoginItemStatus GetStatus() => (MacOSLoginItemStatus)NativeMethods.GetStatus();

        public int SetEnabled(bool enabled) => NativeMethods.SetEnabled(enabled ? (byte)1 : (byte)0);
    }

    private static class NativeMethods
    {
        [DllImport("istranscribe_audio", EntryPoint = "ist_login_item_status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetStatus();

        [DllImport("istranscribe_audio", EntryPoint = "ist_set_login_item_enabled", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetEnabled(byte enabled);
    }
}
