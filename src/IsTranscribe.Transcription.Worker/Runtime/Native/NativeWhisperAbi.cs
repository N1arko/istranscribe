using System.Runtime.InteropServices;

namespace IsTranscribe.Transcription.Worker.Runtime.Native;

/// <summary>
/// Managed mirror of the pinned, app-owned C ABI. Upstream whisper.cpp types never
/// cross this boundary.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
internal static class NativeWhisperAbi
{
    public const uint Version = 1;
    public const uint SampleRate = 16_000;
    public const ulong MaximumSamples = 4_800_000;

    public const int Ok = 0;
    public const int InvalidArgument = 1;
    public const int AbiMismatch = 2;
    public const int UnsupportedBackend = 3;
    public const int ModelLoadFailed = 4;
    public const int TranscriptionFailed = 5;
    public const int Cancelled = 6;
    public const int OutOfRange = 7;
    public const int InternalError = 100;

    public const uint CpuBackend = 1;
    public const uint MetalBackend = 2;
    public const uint VulkanBackend = 3;

    public const string AbiVersionSymbol = "istranscribe_whisper_abi_version_v1";
    public const string ProbeBackendSymbol = "istranscribe_whisper_probe_backend_v1";
    public const string ContextCreateSymbol = "istranscribe_whisper_context_create_v1";
    public const string ContextTranscribeSymbol = "istranscribe_whisper_context_transcribe_v1";
    public const string ContextCancelSymbol = "istranscribe_whisper_context_cancel_v1";
    public const string SegmentCountSymbol = "istranscribe_whisper_context_segment_count_v1";
    public const string SegmentGetSymbol = "istranscribe_whisper_context_segment_get_v1";
    public const string TimingsSymbol = "istranscribe_whisper_context_timings_v1";
    public const string ContextInfoSymbol = "istranscribe_whisper_context_get_info_v1";
    public const string LastErrorSymbol = "istranscribe_whisper_last_error_v1";
    public const string ContextDestroySymbol = "istranscribe_whisper_context_destroy_v1";
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeUtf8View
{
    public uint StructSize;
    public uint Reserved;
    public nint Data;
    public ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBackendProbe
{
    public uint StructSize;
    public uint AbiVersion;
    public uint RequestedBackend;
    public uint Available;
    public ulong MemoryFreeBytes;
    public ulong MemoryTotalBytes;
    public NativeUtf8View DeviceName;
    public NativeUtf8View RuntimeVersion;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeContextOptions
{
    public uint StructSize;
    public uint AbiVersion;
    public uint Backend;
    public uint ThreadCount;
    public NativeUtf8View ModelPath;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTranscribeOptions
{
    public uint StructSize;
    public uint AbiVersion;
    public nint PcmF32Mono16Khz;
    public ulong SampleCount;
    public NativeUtf8View Language;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSegment
{
    public uint StructSize;
    public uint Reserved;
    public long StartMilliseconds;
    public long EndMilliseconds;
    public NativeUtf8View Text;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTimings
{
    public uint StructSize;
    public uint Reserved;
    public double SampleMilliseconds;
    public double EncodeMilliseconds;
    public double DecodeMilliseconds;
    public double BatchMilliseconds;
    public double PromptMilliseconds;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeContextInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public uint ResolvedBackend;
    public uint Reserved;
    public NativeUtf8View DetectedLanguage;
    public NativeUtf8View BackendDeviceName;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeError
{
    public uint StructSize;
    public int Code;
    public NativeUtf8View Message;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint NativeAbiVersionFunction();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeProbeBackendFunction(uint requestedBackend, ref NativeBackendProbe probe);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeContextCreateFunction(ref NativeContextOptions options, out nint context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeContextTranscribeFunction(
    nint context,
    ref NativeTranscribeOptions options,
    NativeProgressFunction? progress,
    nint progressUserData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeContextCancelFunction(nint context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeSegmentCountFunction(nint context, out uint segmentCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeSegmentGetFunction(nint context, uint segmentIndex, ref NativeSegment segment);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeTimingsFunction(nint context, ref NativeTimings timings);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeContextInfoFunction(nint context, ref NativeContextInfo info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeLastErrorFunction(nint context, ref NativeError error);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeContextDestroyFunction(nint context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeProgressFunction(nint userData, uint percent);
