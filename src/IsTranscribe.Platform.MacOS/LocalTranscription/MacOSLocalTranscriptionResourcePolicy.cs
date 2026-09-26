using System.Runtime.InteropServices;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Captures macOS memory, storage and Low Power Mode evidence before local inference starts.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#parity
/// </remarks>
public sealed class MacOSLocalTranscriptionResourcePolicy : ILocalTranscriptionResourcePolicy
{
    private readonly IMacOSLocalTranscriptionResourceProbe _probe;

    public MacOSLocalTranscriptionResourcePolicy(string storagePath)
        : this(new SystemMacOSLocalTranscriptionResourceProbe(storagePath))
    {
    }

    internal MacOSLocalTranscriptionResourcePolicy(IMacOSLocalTranscriptionResourceProbe probe)
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

        if (!state.IsLowPowerMode || request.HasOneShotPowerOverride)
        {
            return Result(LocalTranscriptionResourceDisposition.Allowed, state);
        }

        if (request.TriggerKind == TranscriptionTriggerKind.Automatic)
        {
            return Result(
                LocalTranscriptionResourceDisposition.Deferred,
                state,
                "low_power",
                "Automatic local transcription is waiting for Low Power Mode to end.");
        }

        return Result(
            LocalTranscriptionResourceDisposition.AttentionRequired,
            state,
            "low_power_override_required",
            "Low Power Mode is active. Confirm this run to continue local transcription.",
            canUseOneShotManualOverride: true);
    }

    private LocalTranscriptionResourceState CaptureState()
    {
        try
        {
            var installedMemory = _probe.GetInstalledMemoryBytes();
            var availableMemory = _probe.GetAvailableMemoryBytes();
            var availableDisk = _probe.GetAvailableDiskBytes();
            if (installedMemory <= 0 || availableMemory <= 0 || availableDisk <= 0)
            {
                throw new InvalidOperationException("macOS returned an invalid resource measurement.");
            }

            availableMemory = Math.Min(availableMemory, installedMemory);

            return new LocalTranscriptionResourceState(
                _probe.IsLowPowerModeEnabled(),
                availableMemory,
                availableDisk)
            {
                InstalledMemoryBytes = installedMemory
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or DllNotFoundException
                                          or EntryPointNotFoundException)
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

internal interface IMacOSLocalTranscriptionResourceProbe
{
    long GetInstalledMemoryBytes();

    long GetAvailableMemoryBytes();

    long GetAvailableDiskBytes();

    bool IsLowPowerModeEnabled();
}

internal sealed class SystemMacOSLocalTranscriptionResourceProbe : IMacOSLocalTranscriptionResourceProbe
{
    private readonly string _storagePath;

    public SystemMacOSLocalTranscriptionResourceProbe(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _storagePath = Path.GetFullPath(storagePath);
    }

    public long GetInstalledMemoryBytes()
    {
        var processInfoClass = NativeMethods.ObjectiveCGetClass("NSProcessInfo");
        var processInfoSelector = NativeMethods.ObjectiveCRegisterSelector("processInfo");
        var physicalMemorySelector = NativeMethods.ObjectiveCRegisterSelector("physicalMemory");
        if (processInfoClass == 0 || processInfoSelector == 0 || physicalMemorySelector == 0)
        {
            throw new InvalidOperationException("NSProcessInfo physical memory is unavailable.");
        }

        var processInfo = NativeMethods.ObjectiveCSendObject(processInfoClass, processInfoSelector);
        if (processInfo == 0)
        {
            throw new InvalidOperationException("NSProcessInfo could not be created.");
        }

        var physicalMemory = NativeMethods.ObjectiveCSendUnsignedLongLong(
            processInfo,
            physicalMemorySelector);
        return physicalMemory > long.MaxValue ? long.MaxValue : checked((long)physicalMemory);
    }

    public long GetAvailableMemoryBytes()
    {
        var statistics = new NativeMethods.VmStatistics64();
        var count = checked((uint)(Marshal.SizeOf<NativeMethods.VmStatistics64>() / sizeof(int)));
        var host = NativeMethods.HostPort;
        if (host == 0
            || NativeMethods.HostPageSize(host, out var pageSize) != 0
            || pageSize == 0
            || NativeMethods.HostStatistics64(host, 4, ref statistics, ref count) != 0)
        {
            throw new InvalidOperationException("macOS virtual-memory statistics are unavailable.");
        }

        var availableBytes = CalculateAvailableMemoryBytes(
            statistics.FreeCount,
            statistics.InactiveCount,
            pageSize);
        return availableBytes > long.MaxValue ? long.MaxValue : checked((long)availableBytes);
    }

    internal static ulong CalculateAvailableMemoryBytes(
        uint freePages,
        uint inactivePages,
        uint pageSize)
    {
        if (pageSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        // Darwin reports speculative and purgeable pages as classifications that can
        // overlap the free/inactive pools. Count only the disjoint free and inactive
        // page buckets so the admission estimate stays conservative.
        return checked(((ulong)freePages + inactivePages) * pageSize);
    }

    public long GetAvailableDiskBytes()
    {
        var root = Path.GetPathRoot(_storagePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The local model storage volume is unavailable.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    public bool IsLowPowerModeEnabled()
    {
        var processInfoClass = NativeMethods.ObjectiveCGetClass("NSProcessInfo");
        var processInfoSelector = NativeMethods.ObjectiveCRegisterSelector("processInfo");
        var lowPowerSelector = NativeMethods.ObjectiveCRegisterSelector("isLowPowerModeEnabled");
        if (processInfoClass == 0 || processInfoSelector == 0 || lowPowerSelector == 0)
        {
            throw new InvalidOperationException("NSProcessInfo Low Power Mode is unavailable.");
        }

        var processInfo = NativeMethods.ObjectiveCSendObject(processInfoClass, processInfoSelector);
        if (processInfo == 0)
        {
            throw new InvalidOperationException("NSProcessInfo could not be created.");
        }

        return NativeMethods.ObjectiveCSendBoolean(processInfo, lowPowerSelector) != 0;
    }

    private static class NativeMethods
    {
        private const string LibSystem = "libSystem.B.dylib";
        private const string LibObjectiveC = "libobjc.A.dylib";

        internal static readonly uint HostPort = MachHostSelf();

        [DllImport(LibSystem, EntryPoint = "mach_host_self")]
        private static extern uint MachHostSelf();

        [DllImport(LibSystem, EntryPoint = "host_page_size")]
        internal static extern int HostPageSize(uint host, out uint pageSize);

        [DllImport(LibSystem, EntryPoint = "host_statistics64")]
        internal static extern int HostStatistics64(
            uint host,
            int flavor,
            ref VmStatistics64 statistics,
            ref uint count);

        [DllImport(LibObjectiveC, EntryPoint = "objc_getClass")]
        internal static extern nint ObjectiveCGetClass(
            [MarshalAs(UnmanagedType.LPStr)] string className);

        [DllImport(LibObjectiveC, EntryPoint = "sel_registerName")]
        internal static extern nint ObjectiveCRegisterSelector(
            [MarshalAs(UnmanagedType.LPStr)] string selectorName);

        [DllImport(LibObjectiveC, EntryPoint = "objc_msgSend")]
        internal static extern nint ObjectiveCSendObject(nint receiver, nint selector);

        [DllImport(LibObjectiveC, EntryPoint = "objc_msgSend")]
        internal static extern byte ObjectiveCSendBoolean(nint receiver, nint selector);

        [DllImport(LibObjectiveC, EntryPoint = "objc_msgSend")]
        internal static extern ulong ObjectiveCSendUnsignedLongLong(nint receiver, nint selector);

        [StructLayout(LayoutKind.Sequential)]
        internal struct VmStatistics64
        {
            internal uint FreeCount;
            internal uint ActiveCount;
            internal uint InactiveCount;
            internal uint WireCount;
            internal ulong ZeroFillCount;
            internal ulong Reactivations;
            internal ulong PageIns;
            internal ulong PageOuts;
            internal ulong Faults;
            internal ulong CopyOnWriteFaults;
            internal ulong Lookups;
            internal ulong Hits;
            internal ulong Purges;
            internal uint PurgeableCount;
            internal uint SpeculativeCount;
            internal ulong Decompressions;
            internal ulong Compressions;
            internal ulong SwapIns;
            internal ulong SwapOuts;
            internal uint CompressorPageCount;
            internal uint ThrottledCount;
            internal uint ExternalPageCount;
            internal uint InternalPageCount;
            internal ulong TotalUncompressedPagesInCompressor;
        }
    }
}
