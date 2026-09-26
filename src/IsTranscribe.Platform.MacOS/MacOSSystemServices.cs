using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IsTranscribe.Application.Platform;

namespace IsTranscribe.Platform.MacOS;

public enum MacOSPermissionState
{
    NotRequested,
    Granted,
    Denied,
    NeedsRestart
}

internal interface IMacOSSystemNative
{
    int MicrophonePermission { get; }
    int SystemAudioPermission { get; }
    int ScreenCapturePermission { get; }
    int AccessibilityPermission { get; }
    int NotificationPermission { get; }
    int RequestMicrophone();
    int RequestSystemAudio();
    int RequestScreenCapture();
    int RequestAccessibility();
    int RequestNotifications();
    int ShowNotification(string title, string message);
    byte[]? ReadSecret(string key);
    void WriteSecret(string key, byte[]? value);
}

/// <summary>
/// Publishes contextual macOS TCC states and exact System Settings actions.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions
/// </remarks>
public sealed class MacOSPermissionService : IPermissionService
{
    private readonly IMacOSSystemNative _native;
    private readonly HashSet<string> _needsRestart = new(StringComparer.OrdinalIgnoreCase);

    public MacOSPermissionService() : this(new MacOSSystemNative())
    {
    }

    internal MacOSPermissionService(IMacOSSystemNative native) => _native = native;

    public MacOSPermissionState GetDetailedStatus(string permission)
    {
        var key = Normalize(permission);
        if (_needsRestart.Contains(key)) return MacOSPermissionState.NeedsRestart;
        return Map(Read(key));
    }

    public ValueTask<PlatformCapability> GetStatusAsync(string permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToCapability(Normalize(permission), GetDetailedStatus(permission)));
    }

    public ValueTask<PlatformCapability> RequestAsync(string permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(permission);
        var before = Map(Read(key));
        var result = key switch
        {
            "microphone" => _native.RequestMicrophone(),
            "system_audio" => _native.RequestSystemAudio(),
            "screen_capture" => _native.RequestScreenCapture(),
            "accessibility" => _native.RequestAccessibility(),
            "notifications" => _native.RequestNotifications(),
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Unknown macOS permission.")
        };
        var after = Map(result);
        if (before != MacOSPermissionState.Granted && after == MacOSPermissionState.Granted
            && key is "system_audio" or "screen_capture" or "accessibility")
        {
            _needsRestart.Add(key);
            after = MacOSPermissionState.NeedsRestart;
        }
        return ValueTask.FromResult(ToCapability(key, after));
    }

    public ValueTask OpenSystemSettingsAsync(string permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(permission);
        var pane = key switch
        {
            "microphone" => "Privacy_Microphone",
            "system_audio" or "screen_capture" => "Privacy_ScreenCapture",
            "accessibility" => "Privacy_Accessibility",
            "notifications" => "Notifications",
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Unknown macOS permission.")
        };
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            UseShellExecute = false,
            ArgumentList = { $"x-apple.systempreferences:com.apple.preference.security?{pane}" }
        });
        if (process is null) throw new InvalidOperationException("macOS did not open System Settings.");
        return ValueTask.CompletedTask;
    }

    private int Read(string key) => key switch
    {
        "microphone" => _native.MicrophonePermission,
        "system_audio" => _native.SystemAudioPermission,
        "screen_capture" => _native.ScreenCapturePermission,
        "accessibility" => _native.AccessibilityPermission,
        "notifications" => _native.NotificationPermission,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown macOS permission.")
    };

    private static MacOSPermissionState Map(int value) => value switch
    {
        2 => MacOSPermissionState.Granted,
        1 => MacOSPermissionState.Denied,
        _ => MacOSPermissionState.NotRequested
    };

    private static PlatformCapability ToCapability(string key, MacOSPermissionState state) => new(
        key,
        state switch
        {
            MacOSPermissionState.Granted => PlatformCapabilityState.Available,
            MacOSPermissionState.NotRequested => PlatformCapabilityState.NotRequested,
            MacOSPermissionState.NeedsRestart => PlatformCapabilityState.NeedsRestart,
            _ => PlatformCapabilityState.PermissionRequired
        },
        state switch
        {
            MacOSPermissionState.Granted => "Permission granted.",
            MacOSPermissionState.Denied => "Permission denied. Open System Settings to change it.",
            MacOSPermissionState.NeedsRestart => "Permission granted. Restart is required before it becomes active.",
            _ => "Permission has not been requested."
        });

    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('-', '_');
}

/// <summary>
/// Stores provider secrets as generic-password Keychain items.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// </remarks>
public sealed class MacOSKeychainVault : ISecretVault
{
    private readonly IMacOSSystemNative _native;

    public MacOSKeychainVault() : this(new MacOSSystemNative())
    {
    }

    internal MacOSKeychainVault(IMacOSSystemNative native) => _native = native;

    public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var bytes = _native.ReadSecret(key);
        return ValueTask.FromResult(bytes is null ? null : Encoding.UTF8.GetString(bytes));
    }

    public ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _native.WriteSecret(key, value is null ? null : Encoding.UTF8.GetBytes(value));
        return ValueTask.CompletedTask;
    }
}

public sealed class MacOSSystemNotificationService : ISystemNotificationService
{
    private readonly IMacOSSystemNative _native;

    public MacOSSystemNotificationService() : this(new MacOSSystemNative())
    {
    }

    internal MacOSSystemNotificationService(IMacOSSystemNative native) => _native = native;

    public ValueTask ShowAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_native.NotificationPermission != 2) throw new UnauthorizedAccessException("Notification permission is required.");
        var status = _native.ShowNotification(title, message);
        if (status != 0) throw new InvalidOperationException($"macOS rejected the notification ({status}).");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Promotes the non-modal Ask window into the active macOS Space without activating
/// the application or taking keyboard focus from the meeting client.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#ask-prompt
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// </remarks>
public sealed class MacOSAskPromptWindowPresenter
{
    private readonly IMacOSAskPromptWindowNative _native;

    public MacOSAskPromptWindowPresenter() : this(new NativeBridge())
    {
    }

    internal MacOSAskPromptWindowPresenter(IMacOSAskPromptWindowNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public void Present(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A native macOS window handle is required.", nameof(windowHandle));
        }

        var status = _native.Present(windowHandle);
        if (status != 0)
        {
            throw new InvalidOperationException($"macOS rejected Ask window presentation ({status}).");
        }
    }

    private sealed class NativeBridge : IMacOSAskPromptWindowNative
    {
        public int Present(nint windowHandle) => Native.Present(windowHandle);
    }

    private static class Native
    {
        private const string Library = "istranscribe_audio";

        [DllImport(Library, EntryPoint = "ist_present_ask_prompt_window")]
        internal static extern int Present(nint windowHandle);
    }
}

internal interface IMacOSAskPromptWindowNative
{
    int Present(nint windowHandle);
}

internal sealed class MacOSSystemNative : IMacOSSystemNative
{
    public int MicrophonePermission => Native.MicrophonePermission();
    public int SystemAudioPermission => Native.SystemAudioPermission();
    public int ScreenCapturePermission => Native.ScreenCapturePermission();
    public int AccessibilityPermission => Native.AccessibilityPermission();
    public int NotificationPermission => Native.NotificationPermission();
    public int RequestMicrophone() => Native.RequestMicrophone();
    public int RequestSystemAudio() => Native.RequestSystemAudio();
    public int RequestScreenCapture() => Native.RequestScreenCapture();
    public int RequestAccessibility() => Native.RequestAccessibility();
    public int RequestNotifications() => Native.RequestNotifications();
    public int ShowNotification(string title, string message) => Native.ShowNotification(title, message);

    public byte[]? ReadSecret(string key)
    {
        var length = Native.KeychainRead(key, null, 0);
        if (length == -25300) return null;
        if (length < 0) throw new InvalidOperationException($"Keychain read failed ({length}).");
        var value = new byte[length];
        var read = Native.KeychainRead(key, value, value.Length);
        if (read < 0) throw new InvalidOperationException($"Keychain read failed ({read}).");
        return value;
    }

    public void WriteSecret(string key, byte[]? value)
    {
        var status = value is null ? Native.KeychainDelete(key) : Native.KeychainWrite(key, value, value.Length);
        if (status != 0) throw new InvalidOperationException($"Keychain update failed ({status}).");
    }

    private static class Native
    {
        private const string Library = "istranscribe_audio";
        [DllImport(Library, EntryPoint = "ist_audio_permission")] internal static extern int MicrophonePermission();
        [DllImport(Library, EntryPoint = "ist_system_audio_permission")] internal static extern int SystemAudioPermission();
        [DllImport(Library, EntryPoint = "ist_screen_capture_permission")] internal static extern int ScreenCapturePermission();
        [DllImport(Library, EntryPoint = "ist_accessibility_permission")] internal static extern int AccessibilityPermission();
        [DllImport(Library, EntryPoint = "ist_notification_permission")] internal static extern int NotificationPermission();
        [DllImport(Library, EntryPoint = "ist_request_microphone_permission")] internal static extern int RequestMicrophone();
        [DllImport(Library, EntryPoint = "ist_request_system_audio_permission")] internal static extern int RequestSystemAudio();
        [DllImport(Library, EntryPoint = "ist_request_screen_capture_permission")] internal static extern int RequestScreenCapture();
        [DllImport(Library, EntryPoint = "ist_request_accessibility_permission")] internal static extern int RequestAccessibility();
        [DllImport(Library, EntryPoint = "ist_request_notification_permission")] internal static extern int RequestNotifications();
        [DllImport(Library, EntryPoint = "ist_show_notification")] internal static extern int ShowNotification([MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
        [DllImport(Library, EntryPoint = "ist_keychain_read")] internal static extern int KeychainRead([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [Out] byte[]? destination, int capacity);
        [DllImport(Library, EntryPoint = "ist_keychain_write")] internal static extern int KeychainWrite([MarshalAs(UnmanagedType.LPUTF8Str)] string key, byte[] value, int length);
        [DllImport(Library, EntryPoint = "ist_keychain_delete")] internal static extern int KeychainDelete([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    }
}
