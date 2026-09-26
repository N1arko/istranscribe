using System.ComponentModel;
using System.Runtime.InteropServices;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Captures Windows physical-memory, model-store volume and Energy Saver evidence before local
/// inference starts.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters
/// </remarks>
public sealed class WindowsLocalTranscriptionResourcePolicy : ILocalTranscriptionResourcePolicy
{
    private readonly IWindowsLocalTranscriptionResourceProbe _probe;

    public WindowsLocalTranscriptionResourcePolicy(string modelStorePath)
        : this(new SystemWindowsLocalTranscriptionResourceProbe(modelStorePath))
    {
    }

    internal WindowsLocalTranscriptionResourcePolicy(
        IWindowsLocalTranscriptionResourceProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public LocalTranscriptionResourceState CurrentState => CaptureState();

    public ValueTask<LocalTranscriptionResourceAssessment> AssessAsync(
        LocalTranscriptionResourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequiredMemoryBytes <= 0 || request.RequiredDiskBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Local transcription resource requirements must be positive.");
        }

        var state = CaptureState();
        if (state.StableBlockCode is not null)
        {
            return Result(
                LocalTranscriptionResourceDisposition.AttentionRequired,
                state,
                state.StableBlockCode,
                "Local transcription resource availability could not be verified.");
        }

        if (state.InstalledMemoryBytes is not { } installedMemory
            || installedMemory < request.RequiredMemoryBytes)
        {
            return Result(
                LocalTranscriptionResourceDisposition.AttentionRequired,
                state,
                "insufficient_memory",
                "Installed memory is below the selected local model requirement.");
        }

        if (state.AvailableMemoryBytes is not { } availableMemory
            || availableMemory < request.RequiredMemoryBytes)
        {
            return Result(
                LocalTranscriptionResourceDisposition.AttentionRequired,
                state,
                "insufficient_memory",
                "Available memory is below the selected local model requirement.");
        }

        if (state.AvailableDiskBytes is not { } availableDisk
            || availableDisk < request.RequiredDiskBytes)
        {
            return Result(
                LocalTranscriptionResourceDisposition.AttentionRequired,
                state,
                "insufficient_disk",
                "Available disk space is below the local transcription working requirement.");
        }

        if (!state.IsLowPowerMode)
        {
            return Result(LocalTranscriptionResourceDisposition.Allowed, state);
        }

        if (request.TriggerKind == TranscriptionTriggerKind.Automatic)
        {
            return Result(
                LocalTranscriptionResourceDisposition.Deferred,
                state,
                "energy_saver",
                "Automatic local transcription is waiting for Energy Saver to end.");
        }

        if (request.HasOneShotPowerOverride)
        {
            return Result(LocalTranscriptionResourceDisposition.Allowed, state);
        }

        return Result(
            LocalTranscriptionResourceDisposition.AttentionRequired,
            state,
            "low_power_override_required",
            "Energy Saver is active. Confirm this run to continue local transcription.",
            canUseOneShotManualOverride: true);
    }

    private LocalTranscriptionResourceState CaptureState()
    {
        try
        {
            var memory = _probe.GetPhysicalMemory();
            var availableDisk = _probe.GetAvailableDiskBytes();
            if (memory.TotalBytes <= 0
                || memory.AvailableBytes < 0
                || memory.AvailableBytes > memory.TotalBytes
                || availableDisk < 0)
            {
                throw new InvalidOperationException(
                    "Windows returned an invalid local-transcription resource measurement.");
            }

            return new LocalTranscriptionResourceState(
                _probe.IsEnergySaverEnabled(),
                memory.AvailableBytes,
                availableDisk)
            {
                InstalledMemoryBytes = memory.TotalBytes
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or Win32Exception
                                          or DllNotFoundException
                                          or EntryPointNotFoundException
                                          or PlatformNotSupportedException)
        {
            return new LocalTranscriptionResourceState(
                IsLowPowerMode: false,
                AvailableMemoryBytes: null,
                AvailableDiskBytes: null,
                StableBlockCode: "resource_probe_failed");
        }
    }

    private static ValueTask<LocalTranscriptionResourceAssessment> Result(
        LocalTranscriptionResourceDisposition disposition,
        LocalTranscriptionResourceState state,
        string? code = null,
        string? message = null,
        bool canUseOneShotManualOverride = false) =>
        ValueTask.FromResult(new LocalTranscriptionResourceAssessment(
            disposition,
            state,
            code,
            message,
            canUseOneShotManualOverride));
}

internal readonly record struct WindowsPhysicalMemorySnapshot(
    long TotalBytes,
    long AvailableBytes);

internal interface IWindowsLocalTranscriptionResourceProbe
{
    WindowsPhysicalMemorySnapshot GetPhysicalMemory();

    long GetAvailableDiskBytes();

    bool IsEnergySaverEnabled();
}

/// <summary>
/// Reads bounded Windows resource evidence through kernel32 APIs. The model-store lookup walks
/// only the supplied path's ancestors so a not-yet-created version directory uses its real volume.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
internal sealed class SystemWindowsLocalTranscriptionResourceProbe
    : IWindowsLocalTranscriptionResourceProbe
{
    private readonly string _modelStorePath;

    public SystemWindowsLocalTranscriptionResourceProbe(string modelStorePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelStorePath);
        _modelStorePath = Path.GetFullPath(modelStorePath);
    }

    public WindowsPhysicalMemorySnapshot GetPhysicalMemory()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>())
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new WindowsPhysicalMemorySnapshot(
            ToBoundedInt64(status.TotalPhysicalBytes),
            ToBoundedInt64(status.AvailablePhysicalBytes));
    }

    public long GetAvailableDiskBytes()
    {
        var existingDirectory = FindNearestExistingDirectory(_modelStorePath);
        if (!NativeMethods.GetDiskFreeSpaceEx(
                existingDirectory,
                out var availableBytes,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return ToBoundedInt64(availableBytes);
    }

    public bool IsEnergySaverEnabled()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return status.SystemStatusFlag switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException(
                "Windows returned an unknown Energy Saver state.")
        };
    }

    private static string FindNearestExistingDirectory(string path)
    {
        var candidate = path;
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate);
            if (parent is null)
            {
                throw new InvalidOperationException(
                    "The local model storage volume is unavailable.");
            }

            candidate = parent.FullName;
        }

        return candidate;
    }

    private static long ToBoundedInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : checked((long)value);

    private static class NativeMethods
    {
        private const string Kernel32 = "kernel32.dll";

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport(
            Kernel32,
            EntryPoint = "GetDiskFreeSpaceExW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailableToCaller,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysicalBytes;
            internal ulong AvailablePhysicalBytes;
            internal ulong TotalPageFileBytes;
            internal ulong AvailablePageFileBytes;
            internal ulong TotalVirtualBytes;
            internal ulong AvailableVirtualBytes;
            internal ulong AvailableExtendedVirtualBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemPowerStatus
        {
            internal byte AcLineStatus;
            internal byte BatteryFlag;
            internal byte BatteryLifePercent;
            internal byte SystemStatusFlag;
            internal uint BatteryLifeTimeSeconds;
            internal uint BatteryFullLifeTimeSeconds;
        }
    }
}
