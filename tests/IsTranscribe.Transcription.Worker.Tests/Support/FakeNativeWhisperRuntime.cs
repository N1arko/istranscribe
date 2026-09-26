using System.Runtime.InteropServices;
using System.Text;
using IsTranscribe.Transcription.Worker.Runtime.Native;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

/// <summary>
/// Managed v1 function table used to verify the production loader and marshaling boundary.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
internal sealed class FakeNativeWhisperRuntime : INativeLibraryLoader, IDisposable
{
    private readonly NativeUtf8Allocation _runtimeVersion;
    private readonly NativeUtf8Allocation _detectedLanguage;
    private readonly NativeUtf8Allocation _deviceName;
    private readonly List<NativeUtf8Allocation> _segmentText = [];
    private readonly List<NativeWhisperSegmentResult> _segments = [];
    private int _nextHandle = 1;
    private uint _createdBackend;
    private bool _transcribed;

    public FakeNativeWhisperRuntime(string runtimeVersion = "1.9.1")
    {
        _runtimeVersion = new NativeUtf8Allocation(runtimeVersion);
        _detectedLanguage = new NativeUtf8Allocation("ru");
        _deviceName = new NativeUtf8Allocation("fake-device");
        AbiVersion = static () => NativeWhisperAbi.Version;
        ProbeBackend = PopulateProbe;
        ContextCreate = PopulateContext;
        ContextTranscribe = TranscribeCore;
        ContextCancel = CancelCore;
        SegmentCount = SegmentCountCore;
        SegmentGet = SegmentGetCore;
        Timings = TimingsCore;
        ContextInfo = ContextInfoCore;
        LastError = LastErrorCore;
        ContextDestroy = ContextDestroyCore;
    }

    public NativeAbiVersionFunction AbiVersion { get; set; }

    public NativeProbeBackendFunction ProbeBackend { get; set; }

    public NativeContextCreateFunction ContextCreate { get; set; }

    public NativeContextTranscribeFunction ContextTranscribe { get; set; }

    public NativeContextCancelFunction ContextCancel { get; set; }

    public NativeSegmentCountFunction SegmentCount { get; set; }

    public NativeSegmentGetFunction SegmentGet { get; set; }

    public NativeTimingsFunction Timings { get; set; }

    public NativeContextInfoFunction ContextInfo { get; set; }

    public NativeLastErrorFunction LastError { get; set; }

    public NativeContextDestroyFunction ContextDestroy { get; set; }

    public HashSet<string> MissingSymbols { get; } = new(StringComparer.Ordinal);

    public List<string> LoadPaths { get; } = [];

    public int ProbeResult { get; set; } = NativeWhisperAbi.Ok;

    public bool BackendAvailable { get; set; } = true;

    public int ContextCreateResult { get; set; } = NativeWhisperAbi.Ok;

    public uint? ResolvedBackendOverride { get; set; }

    public int CreateCount { get; private set; }

    public int DestroyCount { get; private set; }

    public int CancelCount { get; private set; }

    public int TimingsCount { get; private set; }

    public int FreeCount { get; private set; }

    public nint Load(string absolutePath)
    {
        LoadPaths.Add(absolutePath);
        return _nextHandle++;
    }

    public bool TryGetExport<TDelegate>(nint handle, string symbol, out TDelegate? function)
        where TDelegate : Delegate
    {
        _ = handle;
        function = null;
        if (MissingSymbols.Contains(symbol)
            || !Exports.TryGetValue(symbol, out var untyped)
            || untyped is not TDelegate typed)
        {
            return false;
        }

        function = typed;
        return true;
    }

    public void Free(nint handle)
    {
        _ = handle;
        FreeCount++;
    }

    public void SetSegments(params NativeWhisperSegmentResult[] segments)
    {
        foreach (var allocation in _segmentText)
        {
            allocation.Dispose();
        }

        _segmentText.Clear();
        _segments.Clear();
        foreach (var segment in segments)
        {
            _segments.Add(segment);
            _segmentText.Add(new NativeUtf8Allocation(segment.Text));
        }
    }

    public void Dispose()
    {
        _runtimeVersion.Dispose();
        _detectedLanguage.Dispose();
        _deviceName.Dispose();
        foreach (var allocation in _segmentText)
        {
            allocation.Dispose();
        }
    }

    private Dictionary<string, Delegate> Exports => new(StringComparer.Ordinal)
    {
        [NativeWhisperAbi.AbiVersionSymbol] = AbiVersion,
        [NativeWhisperAbi.ProbeBackendSymbol] = ProbeBackend,
        [NativeWhisperAbi.ContextCreateSymbol] = ContextCreate,
        [NativeWhisperAbi.ContextTranscribeSymbol] = ContextTranscribe,
        [NativeWhisperAbi.ContextCancelSymbol] = ContextCancel,
        [NativeWhisperAbi.SegmentCountSymbol] = SegmentCount,
        [NativeWhisperAbi.SegmentGetSymbol] = SegmentGet,
        [NativeWhisperAbi.TimingsSymbol] = Timings,
        [NativeWhisperAbi.ContextInfoSymbol] = ContextInfo,
        [NativeWhisperAbi.LastErrorSymbol] = LastError,
        [NativeWhisperAbi.ContextDestroySymbol] = ContextDestroy,
    };

    public int PopulateProbe(uint requestedBackend, ref NativeBackendProbe probe)
    {
        if (ProbeResult != NativeWhisperAbi.Ok)
        {
            return ProbeResult;
        }

        probe = new NativeBackendProbe
        {
            StructSize = SizeOf<NativeBackendProbe>(),
            AbiVersion = NativeWhisperAbi.Version,
            RequestedBackend = requestedBackend,
            Available = BackendAvailable ? 1U : 0U,
            MemoryFreeBytes = 8_000_000_000,
            MemoryTotalBytes = 16_000_000_000,
            RuntimeVersion = _runtimeVersion.View,
            DeviceName = EmptyView(),
        };
        return NativeWhisperAbi.Ok;
    }

    public int PopulateContext(ref NativeContextOptions options, out nint context)
    {
        _ = options;
        CreateCount++;
        _createdBackend = options.Backend;
        _transcribed = false;
        context = ContextCreateResult == NativeWhisperAbi.Ok ? 42 : 0;
        return ContextCreateResult;
    }

    private int TranscribeCore(
        nint context,
        ref NativeTranscribeOptions options,
        NativeProgressFunction? progress,
        nint progressUserData)
    {
        _ = context;
        _ = options;
        progress?.Invoke(progressUserData, 50);
        progress?.Invoke(progressUserData, 100);
        _transcribed = true;
        return NativeWhisperAbi.Ok;
    }

    private int CancelCore(nint context)
    {
        _ = context;
        CancelCount++;
        return NativeWhisperAbi.Ok;
    }

    private int SegmentCountCore(nint context, out uint segmentCount)
    {
        _ = context;
        segmentCount = checked((uint)_segments.Count);
        return NativeWhisperAbi.Ok;
    }

    private int SegmentGetCore(nint context, uint segmentIndex, ref NativeSegment segment)
    {
        _ = context;
        var source = _segments[checked((int)segmentIndex)];
        segment = new NativeSegment
        {
            StructSize = SizeOf<NativeSegment>(),
            StartMilliseconds = source.StartMilliseconds,
            EndMilliseconds = source.EndMilliseconds,
            Text = _segmentText[checked((int)segmentIndex)].View,
        };
        return NativeWhisperAbi.Ok;
    }

    private int TimingsCore(nint context, ref NativeTimings timings)
    {
        _ = context;
        TimingsCount++;
        timings = new NativeTimings
        {
            StructSize = SizeOf<NativeTimings>(),
            SampleMilliseconds = 1,
            EncodeMilliseconds = 2,
            DecodeMilliseconds = 3,
            BatchMilliseconds = 4,
            PromptMilliseconds = 5,
        };
        return NativeWhisperAbi.Ok;
    }

    private int ContextInfoCore(nint context, ref NativeContextInfo info)
    {
        _ = context;
        info = new NativeContextInfo
        {
            StructSize = SizeOf<NativeContextInfo>(),
            AbiVersion = NativeWhisperAbi.Version,
            ResolvedBackend = ResolvedBackendOverride ?? _createdBackend,
            DetectedLanguage = _transcribed ? _detectedLanguage.View : EmptyView(),
            BackendDeviceName = _deviceName.View,
        };
        return NativeWhisperAbi.Ok;
    }

    private static int LastErrorCore(nint context, ref NativeError error)
    {
        _ = context;
        error = new NativeError
        {
            StructSize = SizeOf<NativeError>(),
            Message = EmptyView(),
        };
        return NativeWhisperAbi.Ok;
    }

    private void ContextDestroyCore(nint context)
    {
        _ = context;
        DestroyCount++;
    }

    private static NativeUtf8View EmptyView() => new()
    {
        StructSize = SizeOf<NativeUtf8View>(),
    };

    private static uint SizeOf<T>() where T : struct => checked((uint)Marshal.SizeOf<T>());

    private sealed class NativeUtf8Allocation : IDisposable
    {
        private nint _data;

        public NativeUtf8Allocation(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length == 0)
            {
                View = EmptyView();
                return;
            }

            _data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _data, bytes.Length);
            View = new NativeUtf8View
            {
                StructSize = SizeOf<NativeUtf8View>(),
                Data = _data,
                Length = checked((ulong)bytes.LongLength),
            };
        }

        public NativeUtf8View View { get; }

        public void Dispose()
        {
            var data = Interlocked.Exchange(ref _data, 0);
            if (data != 0)
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }
}
