using System.Runtime.InteropServices;

namespace IsTranscribe.Platform.MacOS;

// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
internal enum MacOSMicrophonePermission
{
    Undetermined,
    Denied,
    Granted
}

internal sealed record MacOSNativeProcess(
    uint ObjectId,
    int ProcessId,
    string Name,
    string BundleId,
    bool IsRunningOutput,
    bool IsRunningInput);

internal sealed record MacOSNativeDevice(
    uint ObjectId,
    string Id,
    string Name,
    bool HasOutput,
    bool HasInput,
    bool IsDefaultOutput,
    bool IsDefaultInput);

internal interface IMacOSAudioNative
{
    double HostTicksPerSecond { get; }

    MacOSMicrophonePermission MicrophonePermission { get; }

    IReadOnlyList<MacOSNativeProcess> EnumerateProcesses();

    IReadOnlyList<MacOSNativeDevice> EnumerateDevices();

    IMacOSNativeCapture StartProcessCapture(int processId, Action<MacOSPcmFrame> onFrame);

    IMacOSNativeCapture StartSystemOutputCapture(Action<MacOSPcmFrame> onFrame);

    IMacOSNativeCapture StartMicrophoneCapture(string deviceId, Action<MacOSPcmFrame> onFrame);
}

internal interface IMacOSNativeCapture : IDisposable;

internal sealed class MacOSAudioNative : IMacOSAudioNative
{
    private const string LibraryName = "istranscribe_audio";

    public double HostTicksPerSecond => NativeMethods.HostTicksPerSecond();

    public MacOSMicrophonePermission MicrophonePermission =>
        (MacOSMicrophonePermission)NativeMethods.AudioPermission();

    public IReadOnlyList<MacOSNativeProcess> EnumerateProcesses()
    {
        var capacity = Math.Max(NativeMethods.EnumerateProcesses(null, 0), 0);
        if (capacity == 0)
        {
            return [];
        }

        var buffer = new NativeProcess[capacity];
        var count = Math.Clamp(NativeMethods.EnumerateProcesses(buffer, buffer.Length), 0, buffer.Length);
        return buffer.Take(count).Select(static process => new MacOSNativeProcess(
            process.ObjectId,
            process.ProcessId,
            process.Name ?? string.Empty,
            process.BundleId ?? string.Empty,
            process.IsRunningOutput != 0,
            process.IsRunningInput != 0)).ToArray();
    }

    public IReadOnlyList<MacOSNativeDevice> EnumerateDevices()
    {
        var capacity = Math.Max(NativeMethods.EnumerateDevices(null, 0), 0);
        if (capacity == 0)
        {
            return [];
        }

        var buffer = new NativeDevice[capacity];
        var count = Math.Clamp(NativeMethods.EnumerateDevices(buffer, buffer.Length), 0, buffer.Length);
        return buffer.Take(count).Select(static device => new MacOSNativeDevice(
            device.ObjectId,
            device.Id ?? string.Empty,
            device.Name ?? string.Empty,
            device.HasOutput != 0,
            device.HasInput != 0,
            device.IsDefaultOutput != 0,
            device.IsDefaultInput != 0)).ToArray();
    }

    public IMacOSNativeCapture StartProcessCapture(int processId, Action<MacOSPcmFrame> onFrame) =>
        NativeCapture.StartProcess(processId, onFrame);

    public IMacOSNativeCapture StartSystemOutputCapture(Action<MacOSPcmFrame> onFrame) =>
        NativeCapture.StartSystemOutput(onFrame);

    public IMacOSNativeCapture StartMicrophoneCapture(string deviceId, Action<MacOSPcmFrame> onFrame) =>
        NativeCapture.StartMicrophone(deviceId, onFrame);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeProcess
    {
        public uint ObjectId;
        public int ProcessId;
        public byte IsRunningOutput;
        public byte IsRunningInput;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? BundleId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeDevice
    {
        public uint ObjectId;
        public byte HasOutput;
        public byte HasInput;
        public byte IsDefaultOutput;
        public byte IsDefaultInput;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Id;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Name;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativePcmCallback(
        nint context,
        nint samples,
        uint frames,
        uint channels,
        double sampleRate,
        ulong hostTime);

    private sealed class NativeCapture : IMacOSNativeCapture
    {
        private readonly NativePcmCallback _callback;
        private nint _handle;

        private NativeCapture(Action<MacOSPcmFrame> onFrame, Func<NativePcmCallback, (nint Handle, int Status)> start)
        {
            ArgumentNullException.ThrowIfNull(onFrame);
            _callback = (_, samples, frames, channels, sampleRate, hostTime) =>
            {
                try
                {
                    var sampleCount = checked((int)(frames * channels));
                    var managed = new float[sampleCount];
                    Marshal.Copy(samples, managed, 0, sampleCount);
                    onFrame(new MacOSPcmFrame(
                        DateTimeOffset.UtcNow,
                        hostTime,
                        checked((int)frames),
                        checked((int)channels),
                        checked((int)Math.Round(sampleRate)),
                        managed));
                }
                catch (Exception)
                {
                    // Exceptions cannot cross a native realtime callback boundary.
                }
            };

            var result = start(_callback);
            _handle = result.Handle;
            if (_handle == nint.Zero)
            {
                throw new MacOSAudioException(result.Status);
            }
        }

        public static NativeCapture StartProcess(int processId, Action<MacOSPcmFrame> onFrame) =>
            new(onFrame, callback =>
            {
                var handle = NativeMethods.StartProcessCapture(processId, callback, nint.Zero, out var status);
                return (handle, status);
            });

        public static NativeCapture StartMicrophone(string deviceId, Action<MacOSPcmFrame> onFrame) =>
            new(onFrame, callback =>
            {
                var handle = NativeMethods.StartMicrophoneCapture(deviceId, callback, nint.Zero, out var status);
                return (handle, status);
            });

        public static NativeCapture StartSystemOutput(Action<MacOSPcmFrame> onFrame) =>
            new(onFrame, callback =>
            {
                var handle = NativeMethods.StartSystemCapture(callback, nint.Zero, out var status);
                return (handle, status);
            });

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, nint.Zero);
            if (handle != nint.Zero)
            {
                NativeMethods.StopCapture(handle);
            }

            GC.KeepAlive(_callback);
        }
    }

    private static class NativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "ist_audio_permission", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioPermission();

        [DllImport(LibraryName, EntryPoint = "ist_host_ticks_per_second", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double HostTicksPerSecond();

        [DllImport(LibraryName, EntryPoint = "ist_enumerate_processes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EnumerateProcesses([Out] NativeProcess[]? destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "ist_enumerate_devices", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EnumerateDevices([Out] NativeDevice[]? destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "ist_start_process_capture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint StartProcessCapture(
            int processId,
            NativePcmCallback callback,
            nint context,
            out int status);

        [DllImport(LibraryName, EntryPoint = "ist_start_system_capture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint StartSystemCapture(
            NativePcmCallback callback,
            nint context,
            out int status);

        [DllImport(LibraryName, EntryPoint = "ist_start_microphone_capture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint StartMicrophoneCapture(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceId,
            NativePcmCallback callback,
            nint context,
            out int status);

        [DllImport(LibraryName, EntryPoint = "ist_stop_capture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void StopCapture(nint handle);
    }
}

public sealed class MacOSAudioException(int status) : InvalidOperationException(
    $"The macOS audio operation failed with OSStatus {status}.")
{
    public int Status { get; } = status;
}
