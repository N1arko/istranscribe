using System.Runtime.InteropServices;
using System.Text;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Worker.Runtime.Native;

internal interface INativeLibraryLoader
{
    nint Load(string absolutePath);

    bool TryGetExport<TDelegate>(nint handle, string symbol, out TDelegate? function)
        where TDelegate : Delegate;

    void Free(nint handle);
}

internal sealed class SystemNativeLibraryLoader : INativeLibraryLoader
{
    public static SystemNativeLibraryLoader Instance { get; } = new();

    private SystemNativeLibraryLoader()
    {
    }

    public nint Load(string absolutePath) => NativeLibrary.Load(absolutePath);

    public bool TryGetExport<TDelegate>(nint handle, string symbol, out TDelegate? function)
        where TDelegate : Delegate
    {
        function = null;
        if (!NativeLibrary.TryGetExport(handle, symbol, out var address))
        {
            return false;
        }

        function = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        return true;
    }

    public void Free(nint handle) => NativeLibrary.Free(handle);
}

internal sealed class NativeWhisperLoadException : Exception
{
    public NativeWhisperLoadException(string stableCode)
        : base(stableCode)
    {
        StableCode = stableCode;
    }

    public string StableCode { get; }
}

internal sealed class NativeWhisperCallException : Exception
{
    public NativeWhisperCallException(int resultCode, string operation)
        : base(operation)
    {
        ResultCode = resultCode;
        Operation = operation;
    }

    public int ResultCode { get; }

    public string Operation { get; }
}

internal sealed record NativeBackendProbeResult(
    uint Backend,
    bool Available,
    ulong FreeMemoryBytes,
    string RuntimeVersion,
    string DeviceName);

internal sealed record NativeWhisperSegmentResult(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

internal sealed record NativeContextInfoResult(
    uint ResolvedBackend,
    string DetectedLanguage,
    string BackendDeviceName);

/// <summary>
/// Owns one absolute-path native module and its complete v1 function table.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
internal sealed class NativeWhisperLibrary : IDisposable
{
    public const string VerifiedRuntimeVersion = "whisper.cpp-v1.9.1";

    private const int MaximumRuntimeVersionBytes = 128;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly INativeLibraryLoader _loader;
    private readonly nint _handle;
    private readonly NativeProbeBackendFunction _probeBackend;
    private readonly NativeContextCreateFunction _contextCreate;
    private readonly NativeContextTranscribeFunction _contextTranscribe;
    private readonly NativeContextCancelFunction _contextCancel;
    private readonly NativeSegmentCountFunction _segmentCount;
    private readonly NativeSegmentGetFunction _segmentGet;
    private readonly NativeTimingsFunction _timings;
    private readonly NativeContextInfoFunction _contextInfo;
    private readonly NativeLastErrorFunction _lastError;
    private readonly NativeContextDestroyFunction _contextDestroy;
    private bool _disposed;

    private NativeWhisperLibrary(
        INativeLibraryLoader loader,
        nint handle,
        NativeProbeBackendFunction probeBackend,
        NativeContextCreateFunction contextCreate,
        NativeContextTranscribeFunction contextTranscribe,
        NativeContextCancelFunction contextCancel,
        NativeSegmentCountFunction segmentCount,
        NativeSegmentGetFunction segmentGet,
        NativeTimingsFunction timings,
        NativeContextInfoFunction contextInfo,
        NativeLastErrorFunction lastError,
        NativeContextDestroyFunction contextDestroy)
    {
        _loader = loader;
        _handle = handle;
        _probeBackend = probeBackend;
        _contextCreate = contextCreate;
        _contextTranscribe = contextTranscribe;
        _contextCancel = contextCancel;
        _segmentCount = segmentCount;
        _segmentGet = segmentGet;
        _timings = timings;
        _contextInfo = contextInfo;
        _lastError = lastError;
        _contextDestroy = contextDestroy;
    }

    public static NativeWhisperLibrary Open(string absolutePath, INativeLibraryLoader loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentNullException.ThrowIfNull(loader);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new NativeWhisperLoadException("native_path_not_absolute");
        }

        nint handle = 0;
        try
        {
            handle = loader.Load(absolutePath);
            var abiVersion = GetRequired<NativeAbiVersionFunction>(
                loader,
                handle,
                NativeWhisperAbi.AbiVersionSymbol);
            var probeBackend = GetRequired<NativeProbeBackendFunction>(
                loader,
                handle,
                NativeWhisperAbi.ProbeBackendSymbol);
            var contextCreate = GetRequired<NativeContextCreateFunction>(
                loader,
                handle,
                NativeWhisperAbi.ContextCreateSymbol);
            var contextTranscribe = GetRequired<NativeContextTranscribeFunction>(
                loader,
                handle,
                NativeWhisperAbi.ContextTranscribeSymbol);
            var contextCancel = GetRequired<NativeContextCancelFunction>(
                loader,
                handle,
                NativeWhisperAbi.ContextCancelSymbol);
            var segmentCount = GetRequired<NativeSegmentCountFunction>(
                loader,
                handle,
                NativeWhisperAbi.SegmentCountSymbol);
            var segmentGet = GetRequired<NativeSegmentGetFunction>(
                loader,
                handle,
                NativeWhisperAbi.SegmentGetSymbol);
            var timings = GetRequired<NativeTimingsFunction>(
                loader,
                handle,
                NativeWhisperAbi.TimingsSymbol);
            var contextInfo = GetRequired<NativeContextInfoFunction>(
                loader,
                handle,
                NativeWhisperAbi.ContextInfoSymbol);
            var lastError = GetRequired<NativeLastErrorFunction>(
                loader,
                handle,
                NativeWhisperAbi.LastErrorSymbol);
            var contextDestroy = GetRequired<NativeContextDestroyFunction>(
                loader,
                handle,
                NativeWhisperAbi.ContextDestroySymbol);

            if (abiVersion() != NativeWhisperAbi.Version)
            {
                throw new NativeWhisperLoadException("native_abi_mismatch");
            }

            return new NativeWhisperLibrary(
                loader,
                handle,
                probeBackend,
                contextCreate,
                contextTranscribe,
                contextCancel,
                segmentCount,
                segmentGet,
                timings,
                contextInfo,
                lastError,
                contextDestroy);
        }
        catch
        {
            if (handle != 0)
            {
                loader.Free(handle);
            }

            throw;
        }
    }

    public NativeBackendProbeResult Probe(uint backend)
    {
        ThrowIfDisposed();
        var probe = new NativeBackendProbe
        {
            StructSize = SizeOf<NativeBackendProbe>(),
        };
        var result = _probeBackend(backend, ref probe);
        if (result != NativeWhisperAbi.Ok)
        {
            ObserveLastError(0);
            throw new NativeWhisperCallException(result, "probe");
        }

        if (probe.StructSize < SizeOf<NativeBackendProbe>()
            || probe.AbiVersion != NativeWhisperAbi.Version
            || probe.RequestedBackend != backend
            || probe.Available > 1)
        {
            throw new NativeWhisperLoadException("native_probe_abi_mismatch");
        }

        var runtimeVersion = ReadUtf8(
            probe.RuntimeVersion,
            MaximumRuntimeVersionBytes,
            allowEmpty: false);
        var deviceName = ReadUtf8(
            probe.DeviceName,
            maximumBytes: 256,
            allowEmpty: true);
        if (runtimeVersion is not ("1.9.1" or "v1.9.1"))
        {
            throw new NativeWhisperLoadException("native_runtime_version_mismatch");
        }

        return new NativeBackendProbeResult(
            backend,
            probe.Available == 1,
            probe.MemoryFreeBytes,
            VerifiedRuntimeVersion,
            deviceName);
    }

    public nint CreateContext(string modelPath, uint backend, int maximumThreads)
    {
        ThrowIfDisposed();
        using var modelPathBuffer = new Utf8NativeBuffer(modelPath);
        var options = new NativeContextOptions
        {
            StructSize = SizeOf<NativeContextOptions>(),
            AbiVersion = NativeWhisperAbi.Version,
            Backend = backend,
            ThreadCount = checked((uint)maximumThreads),
            ModelPath = modelPathBuffer.View,
        };
        var result = _contextCreate(ref options, out var context);
        if (result != NativeWhisperAbi.Ok || context == 0)
        {
            ObserveLastError(0);
            throw new NativeWhisperCallException(
                result == NativeWhisperAbi.Ok ? NativeWhisperAbi.InternalError : result,
                "context_create");
        }

        return context;
    }

    public int Transcribe(
        nint context,
        float[] samples,
        string language,
        NativeProgressFunction progress,
        nint progressUserData)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(progress);
        using var languageBuffer = new Utf8NativeBuffer(language);
        var samplesHandle = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            var options = new NativeTranscribeOptions
            {
                StructSize = SizeOf<NativeTranscribeOptions>(),
                AbiVersion = NativeWhisperAbi.Version,
                PcmF32Mono16Khz = samplesHandle.AddrOfPinnedObject(),
                SampleCount = checked((ulong)samples.LongLength),
                Language = languageBuffer.View,
            };
            var result = _contextTranscribe(context, ref options, progress, progressUserData);
            if (result != NativeWhisperAbi.Ok)
            {
                ObserveLastError(context);
            }

            return result;
        }
        finally
        {
            samplesHandle.Free();
        }
    }

    public void Cancel(nint context)
    {
        if (_disposed || context == 0)
        {
            return;
        }

        _ = _contextCancel(context);
    }

    public IReadOnlyList<NativeWhisperSegmentResult> ReadSegments(nint context)
    {
        ThrowIfDisposed();
        var countResult = _segmentCount(context, out var count);
        if (countResult != NativeWhisperAbi.Ok)
        {
            ObserveLastError(context);
            throw new NativeWhisperCallException(countResult, "segment_count");
        }

        if (count > WorkerProtocol.MaximumSegmentsPerChunk)
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.OutOfRange, "segment_count");
        }

        var segments = new List<NativeWhisperSegmentResult>(checked((int)count));
        var remainingTextBytes = WorkerProtocol.MaximumStringBytes;
        for (uint index = 0; index < count; index++)
        {
            var segment = new NativeSegment
            {
                StructSize = SizeOf<NativeSegment>(),
            };
            var segmentResult = _segmentGet(context, index, ref segment);
            if (segmentResult != NativeWhisperAbi.Ok)
            {
                ObserveLastError(context);
                throw new NativeWhisperCallException(segmentResult, "segment_get");
            }

            if (segment.StructSize < SizeOf<NativeSegment>()
                || segment.Reserved != 0
                || segment.StartMilliseconds < 0
                || segment.EndMilliseconds < segment.StartMilliseconds)
            {
                throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "segment_get");
            }

            var text = ReadUtf8(segment.Text, remainingTextBytes, allowEmpty: true);
            remainingTextBytes -= Encoding.UTF8.GetByteCount(text);
            segments.Add(new NativeWhisperSegmentResult(
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                text));
        }

        return segments;
    }

    public void VerifyTimings(nint context)
    {
        ThrowIfDisposed();
        var timings = new NativeTimings
        {
            StructSize = SizeOf<NativeTimings>(),
        };
        var result = _timings(context, ref timings);
        if (result != NativeWhisperAbi.Ok)
        {
            ObserveLastError(context);
            throw new NativeWhisperCallException(result, "timings");
        }

        if (timings.StructSize < SizeOf<NativeTimings>()
            || timings.Reserved != 0
            || !IsValidTiming(timings.SampleMilliseconds)
            || !IsValidTiming(timings.EncodeMilliseconds)
            || !IsValidTiming(timings.DecodeMilliseconds)
            || !IsValidTiming(timings.BatchMilliseconds)
            || !IsValidTiming(timings.PromptMilliseconds))
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "timings");
        }
    }

    public NativeContextInfoResult ReadContextInfo(nint context, bool requireDetectedLanguage)
    {
        ThrowIfDisposed();
        var info = new NativeContextInfo
        {
            StructSize = SizeOf<NativeContextInfo>(),
        };
        var result = _contextInfo(context, ref info);
        if (result != NativeWhisperAbi.Ok)
        {
            ObserveLastError(context);
            throw new NativeWhisperCallException(result, "context_info");
        }

        if (info.StructSize < SizeOf<NativeContextInfo>()
            || info.AbiVersion != NativeWhisperAbi.Version
            || info.ResolvedBackend is < NativeWhisperAbi.CpuBackend or > NativeWhisperAbi.VulkanBackend
            || info.Reserved != 0)
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "context_info");
        }

        var detectedLanguage = ReadUtf8(
            info.DetectedLanguage,
            maximumBytes: 16,
            allowEmpty: !requireDetectedLanguage);
        if ((requireDetectedLanguage && !IsLanguageToken(detectedLanguage))
            || (!requireDetectedLanguage && detectedLanguage.Length != 0))
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "context_info");
        }

        var backendDeviceName = ReadUtf8(
            info.BackendDeviceName,
            maximumBytes: 256,
            allowEmpty: true);
        return new NativeContextInfoResult(
            info.ResolvedBackend,
            detectedLanguage,
            backendDeviceName);
    }

    public void DestroyContext(nint context)
    {
        if (!_disposed && context != 0)
        {
            _contextDestroy(context);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loader.Free(_handle);
    }

    private static TDelegate GetRequired<TDelegate>(
        INativeLibraryLoader loader,
        nint handle,
        string symbol)
        where TDelegate : Delegate
    {
        if (!loader.TryGetExport<TDelegate>(handle, symbol, out var function) || function is null)
        {
            throw new NativeWhisperLoadException("native_symbol_missing");
        }

        return function;
    }

    private static uint SizeOf<T>() where T : struct => checked((uint)Marshal.SizeOf<T>());

    private static bool IsValidTiming(double value) => double.IsFinite(value) && value >= 0;

    private static bool IsLanguageToken(string value) =>
        value.Length is >= 1 and <= 16
        && value.All(static character => character is >= 'a' and <= 'z');

    private static string ReadUtf8(NativeUtf8View view, int maximumBytes, bool allowEmpty)
    {
        if (view.StructSize < SizeOf<NativeUtf8View>()
            || view.Reserved != 0
            || view.Length > checked((ulong)maximumBytes)
            || (view.Length > 0 && view.Data == 0)
            || (view.Length == 0 && view.Data != 0))
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "utf8_view");
        }

        if (view.Length == 0)
        {
            if (!allowEmpty)
            {
                throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "utf8_view");
            }

            return string.Empty;
        }

        var bytes = new byte[checked((int)view.Length)];
        Marshal.Copy(view.Data, bytes, 0, bytes.Length);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new NativeWhisperCallException(NativeWhisperAbi.AbiMismatch, "utf8_view");
        }
    }

    private void ObserveLastError(nint context)
    {
        var error = new NativeError
        {
            StructSize = SizeOf<NativeError>(),
        };
        try
        {
            _ = _lastError(context, ref error);
            if (error.Message.Length <= 2048)
            {
                _ = ReadUtf8(error.Message, 2048, allowEmpty: true);
            }
        }
        catch
        {
            // Native diagnostics never replace the stable managed failure taxonomy.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Utf8NativeBuffer : IDisposable
    {
        private readonly byte[] _bytes;
        private GCHandle _handle;

        public Utf8NativeBuffer(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            try
            {
                _bytes = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                throw new NativeWhisperCallException(
                    NativeWhisperAbi.InvalidArgument,
                    "utf8_input");
            }
            if (_bytes.Length == 0)
            {
                View = new NativeUtf8View
                {
                    StructSize = SizeOf<NativeUtf8View>(),
                };
                return;
            }

            _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
            View = new NativeUtf8View
            {
                StructSize = SizeOf<NativeUtf8View>(),
                Data = _handle.AddrOfPinnedObject(),
                Length = checked((ulong)_bytes.LongLength),
            };
        }

        public NativeUtf8View View { get; }

        public void Dispose()
        {
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }
}
