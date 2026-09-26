using System.Runtime.InteropServices;
using System.Text;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Runtime;
using IsTranscribe.Transcription.Worker.Runtime.Native;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// Loader, ABI, normalized-audio, cancellation and result-mapping coverage for the
/// pinned native worker backend.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
[Collection(WorkerLifecycleCollection.Name)]
public sealed class NativeWhisperBackendTests
{
    [Fact]
    public void ManagedStructLayoutMatchesPinned64BitAbi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<NativeUtf8View>());
        Assert.Equal(112, Marshal.SizeOf<NativeBackendProbe>());
        Assert.Equal(72, Marshal.SizeOf<NativeContextOptions>());
        Assert.Equal(80, Marshal.SizeOf<NativeTranscribeOptions>());
        Assert.Equal(80, Marshal.SizeOf<NativeSegment>());
        Assert.Equal(80, Marshal.SizeOf<NativeTimings>());
        Assert.Equal(96, Marshal.SizeOf<NativeContextInfo>());
        Assert.Equal(64, Marshal.SizeOf<NativeError>());
    }

    [Fact]
    public async Task ProbeLoadsOnlyFixedAbsoluteAppNativePathAndVerifiesFullModelLoad()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        var nativePath = files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var result = await backend.ProbeAsync(files.Probe(), CancellationToken.None);

        Assert.Equal("cpu", result.Backend);
        Assert.Equal(8_000_000_000, result.AvailableMemoryBytes);
        Assert.Equal(Path.GetFullPath(nativePath), Assert.Single(runtime.LoadPaths));
        Assert.True(Path.IsPathFullyQualified(runtime.LoadPaths[0]));
        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(1, runtime.DestroyCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task MissingFixedRuntimeFileDoesNotFallBackToSystemSearchPath()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.ProbeAsync(files.Probe(), CancellationToken.None));

        Assert.Equal("native_runtime_missing", failure.StableCode);
        Assert.Empty(runtime.LoadPaths);
    }

    [Fact]
    public async Task MissingRequiredSymbolFailsClosedAndUnloadsModule()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        runtime.MissingSymbols.Add(NativeWhisperAbi.SegmentGetSymbol);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.ProbeAsync(files.Probe(), CancellationToken.None));

        Assert.Equal("native_failure", failure.Category);
        Assert.Equal("native_symbol_missing", failure.StableCode);
        Assert.False(failure.Retryable);
        Assert.Equal(1, runtime.FreeCount);
        Assert.Equal(0, runtime.CreateCount);
    }

    [Fact]
    public async Task AbiVersionMismatchFailsClosedBeforeModelLoad()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime
        {
            AbiVersion = static () => NativeWhisperAbi.Version + 1,
        };
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.ProbeAsync(files.Probe(), CancellationToken.None));

        Assert.Equal("native_abi_mismatch", failure.StableCode);
        Assert.Equal(1, runtime.FreeCount);
        Assert.Equal(0, runtime.CreateCount);
    }

    [Fact]
    public async Task PinnedRuntimeVersionMismatchFailsClosed()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime("1.9.0");
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.ProbeAsync(files.Probe(), CancellationToken.None));

        Assert.Equal("native_runtime_version_mismatch", failure.StableCode);
        Assert.Equal(0, runtime.CreateCount);
    }

    [Fact]
    public async Task ModelLoadFailureUsesBoundedStableFailureAndUnloadsModule()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime
        {
            ContextCreateResult = NativeWhisperAbi.ModelLoadFailed,
        };
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.ProbeAsync(files.Probe(), CancellationToken.None));

        Assert.Equal("model_invalid", failure.Category);
        Assert.Equal("model_load_failed", failure.StableCode);
        Assert.DoesNotContain(files.ModelPath, failure.Message, StringComparison.Ordinal);
        Assert.False(failure.Retryable);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task ModelHashMutationFailsInsideWorkerBeforeNativeLoad()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        var request = files.Start();
        File.WriteAllBytes(files.ModelPath, [4, 3, 2, 1]);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.TranscribeAsync(
                request,
                new ProgressCollector(),
                CancellationToken.None));

        Assert.Equal("model_invalid", failure.Category);
        Assert.Equal("model_hash_mismatch", failure.StableCode);
        Assert.Empty(runtime.LoadPaths);
        Assert.Equal(0, runtime.CreateCount);
    }

    [Fact]
    public async Task AtomicModelReplacementDuringContextLoadFailsClosedBeforeInferenceOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        var replacementPath = Path.Combine(files.Root, "replacement-model.bin");
        File.WriteAllBytes(replacementPath, [9, 8, 7, 6]);
        string? nativeModelPath = null;
        var inferenceCalled = false;
        runtime.ContextCreate = (ref NativeContextOptions options, out nint context) =>
        {
            nativeModelPath = ReadUtf8(options.ModelPath);
            File.Move(replacementPath, files.ModelPath, overwrite: true);
            return runtime.PopulateContext(ref options, out context);
        };
        runtime.ContextTranscribe = (
            nint context,
            ref NativeTranscribeOptions options,
            NativeProgressFunction? progress,
            nint progressUserData) =>
        {
            _ = context;
            _ = options;
            _ = progress;
            _ = progressUserData;
            inferenceCalled = true;
            return NativeWhisperAbi.Ok;
        };
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.TranscribeAsync(
                files.Start(),
                new ProgressCollector(),
                CancellationToken.None));

        Assert.Equal("model_invalid", failure.Category);
        Assert.Equal("model_identity_changed", failure.StableCode);
        Assert.False(failure.Retryable);
        Assert.DoesNotContain(files.ModelPath, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(files.ModelSha256, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(failure.Message.Length, 1, 256);
        Assert.NotNull(nativeModelPath);
        Assert.StartsWith("/dev/fd/", nativeModelPath, StringComparison.Ordinal);
        Assert.False(inferenceCalled);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Equal(1, runtime.DestroyCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task WindowsVerifiedModelLeaseDeniesAtomicReplacementDuringContextLoad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.WindowsCpuLibraryName);
        var replacementPath = Path.Combine(files.Root, "replacement-model.bin");
        File.WriteAllBytes(replacementPath, [9, 8, 7, 6]);
        Exception? replacementFailure = null;
        string? nativeModelPath = null;
        runtime.ContextCreate = (ref NativeContextOptions options, out nint context) =>
        {
            nativeModelPath = ReadUtf8(options.ModelPath);
            try
            {
                File.Move(replacementPath, files.ModelPath, overwrite: true);
            }
            catch (Exception exception)
            {
                replacementFailure = exception;
            }

            return runtime.PopulateContext(ref options, out context);
        };
        using var backend = new NativeWhisperBackend(
            files.NativeDirectory,
            NativeWhisperHost.WindowsX64,
            runtime);

        var result = await backend.TranscribeAsync(
            files.Start(),
            new ProgressCollector(),
            CancellationToken.None);

        Assert.True(
            replacementFailure is IOException or UnauthorizedAccessException,
            $"Expected Windows to deny model replacement, received {replacementFailure?.GetType().Name ?? "no failure"}.");
        Assert.Equal(Path.GetFullPath(files.ModelPath), nativeModelPath);
        Assert.Equal("cpu", result.Backend);
        Assert.Equal(1, runtime.CreateCount);
    }

    [Fact]
    public async Task TranscribeMapsChunkRelativeSegmentsTimingsAndProgress()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        runtime.SetSegments(
            new NativeWhisperSegmentResult(0, 0, " \t"),
            new NativeWhisperSegmentResult(0, 250, " first"),
            new NativeWhisperSegmentResult(250, 1_100, "second"));
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);
        var progress = new ProgressCollector();

        var result = await backend.TranscribeAsync(
            files.Start(),
            progress,
            CancellationToken.None);

        Assert.Equal("ru", result.Language);
        Assert.Equal(NativeWhisperLibrary.VerifiedRuntimeVersion, result.RuntimeVersion);
        Assert.Equal(files.ModelSha256, result.ModelSha256);
        Assert.Equal("cpu", result.Backend);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(new WorkerSegmentPayload(0, 250, " first"), segment),
            segment => Assert.Equal(new WorkerSegmentPayload(250, 1_000, "second"), segment));
        Assert.Contains(progress.Values, value => value.Stage == "preparing");
        Assert.Contains(progress.Values, value =>
            value.Stage == "inferencing" && value.CompletedMilliseconds == 500);
        Assert.Contains(progress.Values, value =>
            value.Stage == "finalizing" && value.CompletedMilliseconds == 1_000);
        Assert.Equal(1, runtime.TimingsCount);
        Assert.Equal(1, runtime.DestroyCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task Float32NormalizedWaveIsAccepted()
    {
        using var files = new NativeBackendTestDirectory();
        files.WriteFloatWave();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var result = await backend.TranscribeAsync(
            files.Start(),
            new ProgressCollector(),
            CancellationToken.None);

        Assert.Empty(result.Segments);
        Assert.Equal("cpu", result.Backend);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public async Task IntegerPcmNormalizedWaveIsAccepted(int bitsPerSample)
    {
        using var files = new NativeBackendTestDirectory();
        files.WritePcmWave(bitsPerSample);
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var result = await backend.TranscribeAsync(
            files.Start(),
            new ProgressCollector(),
            CancellationToken.None);

        Assert.Empty(result.Segments);
        Assert.Equal("cpu", result.Backend);
    }

    [Fact]
    public async Task CancelCallsNativeContextCancelAndReleasesContextAndModule()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var started = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        runtime.ContextTranscribe = (
            nint context,
            ref NativeTranscribeOptions options,
            NativeProgressFunction? progress,
            nint progressUserData) =>
        {
            _ = context;
            _ = options;
            _ = progress;
            _ = progressUserData;
            started.Set();
            Assert.True(cancelled.Wait(TimeSpan.FromSeconds(2)));
            return NativeWhisperAbi.Cancelled;
        };
        runtime.ContextCancel = context =>
        {
            _ = context;
            cancelled.Set();
            return NativeWhisperAbi.Ok;
        };
        using var backend = CreateMacBackend(files, runtime);
        using var cancellation = new CancellationTokenSource();

        var task = backend.TranscribeAsync(
            files.Start(),
            new ProgressCollector(),
            cancellation.Token).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(1, runtime.DestroyCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task CancellationDuringModelLoadStopsBeforeNativeTranscribe()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var createStarted = new ManualResetEventSlim();
        using var releaseCreate = new ManualResetEventSlim();
        var transcribeCalled = false;
        runtime.ContextCreate = (ref NativeContextOptions options, out nint context) =>
        {
            createStarted.Set();
            Assert.True(releaseCreate.Wait(TimeSpan.FromSeconds(2)));
            return runtime.PopulateContext(ref options, out context);
        };
        runtime.ContextTranscribe = (
            nint context,
            ref NativeTranscribeOptions options,
            NativeProgressFunction? progress,
            nint progressUserData) =>
        {
            _ = context;
            _ = options;
            _ = progress;
            _ = progressUserData;
            transcribeCalled = true;
            return NativeWhisperAbi.Ok;
        };
        using var backend = CreateMacBackend(files, runtime);
        using var cancellation = new CancellationTokenSource();

        var task = backend.TranscribeAsync(
            files.Start(),
            new ProgressCollector(),
            cancellation.Token).AsTask();
        Assert.True(createStarted.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        releaseCreate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(transcribeCalled);
        Assert.Equal(1, runtime.CancelCount);
        Assert.Equal(1, runtime.DestroyCount);
        Assert.Equal(1, runtime.FreeCount);
    }

    [Fact]
    public async Task MacMetalAndWindowsVulkanFailuresSelectCpuBaseline()
    {
        using var macFiles = new NativeBackendTestDirectory();
        using var macRuntime = new FakeNativeWhisperRuntime();
        macFiles.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        macRuntime.ProbeBackend = (uint backend, ref NativeBackendProbe probe) =>
        {
            if (backend == NativeWhisperAbi.MetalBackend)
            {
                return NativeWhisperAbi.UnsupportedBackend;
            }

            return macRuntime.PopulateProbe(backend, ref probe);
        };
        using var macBackend = CreateMacBackend(macFiles, macRuntime);

        var macResult = await macBackend.ProbeAsync(
            macFiles.Probe("auto"),
            CancellationToken.None);

        Assert.Equal("cpu", macResult.Backend);

        using var windowsFiles = new NativeBackendTestDirectory();
        using var windowsRuntime = new FakeNativeWhisperRuntime();
        windowsFiles.AddNativeFile(NativeWhisperBackend.WindowsCpuLibraryName);
        using var windowsBackend = new NativeWhisperBackend(
            windowsFiles.NativeDirectory,
            NativeWhisperHost.WindowsX64,
            windowsRuntime);

        var windowsResult = await windowsBackend.ProbeAsync(
            windowsFiles.Probe("auto"),
            CancellationToken.None);

        Assert.Equal("cpu", windowsResult.Backend);
        Assert.Equal(
            Path.Combine(windowsFiles.NativeDirectory, NativeWhisperBackend.WindowsCpuLibraryName),
            Assert.Single(windowsRuntime.LoadPaths));
    }

    [Fact]
    public async Task SilentAcceleratorFallbackIsRejectedBeforeReportingResolvedBackend()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime
        {
            ResolvedBackendOverride = NativeWhisperAbi.CpuBackend,
        };
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var result = await backend.ProbeAsync(
            files.Probe("auto"),
            CancellationToken.None);

        Assert.Equal("cpu", result.Backend);
        Assert.Equal(2, runtime.CreateCount);
        Assert.Equal(2, runtime.DestroyCount);
        Assert.Equal(2, runtime.FreeCount);
    }

    [Fact]
    public async Task NonNormalizedInputFailsBeforeNativeLoad()
    {
        using var files = new NativeBackendTestDirectory();
        File.WriteAllBytes(files.InputPath, [1, 2, 3, 4]);
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.TranscribeAsync(
                files.Start(),
                new ProgressCollector(),
                CancellationToken.None));

        Assert.Equal("input_invalid", failure.Category);
        Assert.Empty(runtime.LoadPaths);
    }

    [Fact]
    public async Task InputHashMismatchFailsBeforeNativeLoad()
    {
        using var files = new NativeBackendTestDirectory();
        using var runtime = new FakeNativeWhisperRuntime();
        files.AddNativeFile(NativeWhisperBackend.MacOsLibraryName);
        using var backend = CreateMacBackend(files, runtime);
        var request = files.Start() with { InputSha256 = WorkerTestData.OtherHash };

        var failure = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
            await backend.TranscribeAsync(
                request,
                new ProgressCollector(),
                CancellationToken.None));

        Assert.Equal("input_invalid", failure.Category);
        Assert.Equal("input_hash_mismatch", failure.StableCode);
        Assert.Empty(runtime.LoadPaths);
    }

    private static NativeWhisperBackend CreateMacBackend(
        NativeBackendTestDirectory files,
        FakeNativeWhisperRuntime runtime) => new(
            files.NativeDirectory,
            NativeWhisperHost.MacOsArm64,
            runtime);

    private static string ReadUtf8(NativeUtf8View view)
    {
        var bytes = new byte[checked((int)view.Length)];
        Marshal.Copy(view.Data, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class ProgressCollector : IProgress<WorkerProgressPayload>
    {
        public List<WorkerProgressPayload> Values { get; } = [];

        public void Report(WorkerProgressPayload value) => Values.Add(value);
    }
}
