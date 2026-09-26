using System.Runtime.InteropServices;
using System.Text;

namespace IsTranscribe.Platform.MacOS;

// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
internal sealed record MacOSNativeWindow(
    int ProcessId,
    uint WindowId,
    bool IsForeground,
    string OwnerName,
    string Title,
    IReadOnlyList<string> AccessibleControlNames);

internal interface IMacOSMeetingNative
{
    bool HasAccessibilityPermission { get; }

    bool HasScreenCapturePermission { get; }

    IReadOnlyList<MacOSNativeWindow> EnumerateWindows(bool includeAccessibility);

    bool HasZoomMeetingHost(int rootProcessId);

    string ReadAccessibleWindowTitle(int processId);

    IReadOnlyList<string> ReadAccessibleControlNames(int processId);
}

/// <summary>
/// Versioned native boundary for privacy-reduced macOS window and Accessibility evidence.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal sealed class MacOSMeetingNative : IMacOSMeetingNative
{
    private const string LibraryName = "istranscribe_audio";

    public bool HasAccessibilityPermission => NativeMethods.AccessibilityPermission() == 2;

    public bool HasScreenCapturePermission => NativeMethods.ScreenCapturePermission() == 2;

    public IReadOnlyList<MacOSNativeWindow> EnumerateWindows(bool includeAccessibility)
    {
        var capacity = Math.Max(NativeMethods.EnumerateWindows(null, 0, includeAccessibility ? (byte)1 : (byte)0), 0);
        if (capacity == 0)
        {
            return [];
        }

        var buffer = new NativeWindow[capacity];
        var count = Math.Clamp(
            NativeMethods.EnumerateWindows(buffer, buffer.Length, includeAccessibility ? (byte)1 : (byte)0),
            0,
            buffer.Length);
        return buffer.Take(count).Select(static window => new MacOSNativeWindow(
            window.ProcessId,
            window.WindowId,
            window.IsForeground != 0,
            window.OwnerName ?? string.Empty,
            window.Title ?? string.Empty,
            (window.AccessibleControls ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())).ToArray();
    }

    public IReadOnlyList<string> ReadAccessibleControlNames(int processId)
    {
        const int capacity = 2048;
        var buffer = new byte[capacity];
        var count = Math.Clamp(
            NativeMethods.AccessibleControlNames(processId, buffer, buffer.Length),
            0,
            buffer.Length);
        if (count == 0)
        {
            return [];
        }

        return Encoding.UTF8.GetString(buffer, 0, count)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool HasZoomMeetingHost(int rootProcessId) =>
        rootProcessId > 0 && NativeMethods.ZoomMeetingHostActive(rootProcessId) != 0;

    public string ReadAccessibleWindowTitle(int processId)
    {
        const int capacity = 2048;
        var buffer = new byte[capacity];
        var count = Math.Clamp(
            NativeMethods.AccessibleWindowTitle(processId, buffer, buffer.Length),
            0,
            buffer.Length);
        return count == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, count).Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeWindow
    {
        public int ProcessId;
        public uint WindowId;
        public byte IsForeground;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? OwnerName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string? Title;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
        public string? AccessibleControls;
    }

    private static class NativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "ist_accessibility_permission", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AccessibilityPermission();

        [DllImport(LibraryName, EntryPoint = "ist_screen_capture_permission", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ScreenCapturePermission();

        [DllImport(LibraryName, EntryPoint = "ist_enumerate_windows", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EnumerateWindows([Out] NativeWindow[]? destination, int capacity, byte includeAccessibility);

        [DllImport(LibraryName, EntryPoint = "ist_accessible_control_names", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AccessibleControlNames(int processId, [Out] byte[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "ist_accessible_window_title", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AccessibleWindowTitle(int processId, [Out] byte[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "ist_zoom_meeting_host_active", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ZoomMeetingHostActive(int rootProcessId);
    }
}
