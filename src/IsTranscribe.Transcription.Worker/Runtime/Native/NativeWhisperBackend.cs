using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Worker.Runtime.Native;

internal enum NativeWhisperHost
{
    MacOsArm64,
    WindowsX64,
}

/// <summary>
/// Creates the production native backend from the worker-owned application base.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration
/// </summary>
internal static class NativeWhisperBackendFactory
{
    public static NativeWhisperBackend CreateForAppBase(string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        if (!Path.IsPathFullyQualified(applicationBaseDirectory))
        {
            throw new InvalidOperationException("The worker application base directory must be absolute.");
        }

        var host = DetectHost();
        var nativeDirectory = Path.GetFullPath(Path.Combine(applicationBaseDirectory, "native"));
        return new NativeWhisperBackend(
            nativeDirectory,
            host,
            SystemNativeLibraryLoader.Instance);
    }

    private static NativeWhisperHost DetectHost()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return NativeWhisperHost.MacOsArm64;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return NativeWhisperHost.WindowsX64;
        }

        throw new LocalInferenceException(
            "backend_unavailable",
            "unsupported_native_platform",
            "Local transcription is unavailable on this platform.",
            retryable: false);
    }
}

/// <summary>
/// Short-lived worker backend for the pinned whisper C ABI. Every operation releases
/// its model context and native module before returning.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
internal sealed class NativeWhisperBackend : ILocalInferenceBackend, IDisposable
{
    internal const string MacOsLibraryName = "istranscribe_whisper_v1.dylib";
    internal const string WindowsCpuLibraryName = "istranscribe_whisper_v1.dll";
    internal const string WindowsVulkanLibraryName = "istranscribe_whisper_v1_vulkan.dll";
    private const long MaximumChunkMilliseconds = 5 * 60 * 1000;
    private const long DurationToleranceMilliseconds = 100;
    private static readonly NativeProgressFunction NativeProgressCallback = ReportNativeProgress;

    private readonly string _nativeDirectory;
    private readonly NativeWhisperHost _host;
    private readonly INativeLibraryLoader _loader;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private bool _disposed;

    internal NativeWhisperBackend(
        string nativeDirectory,
        NativeWhisperHost host,
        INativeLibraryLoader loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        ArgumentNullException.ThrowIfNull(loader);
        if (!Path.IsPathFullyQualified(nativeDirectory))
        {
            throw new ArgumentException("Native directory must be absolute.", nameof(nativeDirectory));
        }

        _nativeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nativeDirectory));
        _host = host;
        _loader = loader;
    }

    public async ValueTask<LocalWorkerProbeResult> ProbeAsync(
        WorkerProbePayload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operation.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ValidateModelPath(request.ModelPath);
            return await Task.Run(
                () => ProbeCore(request, cancellationToken),
                CancellationToken.None);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async ValueTask<WorkerResultPayload> TranscribeAsync(
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        await _operation.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ValidateModelPath(request.ModelPath);
            return await Task.Run(
                () => TranscribeCore(request, progress, cancellationToken),
                CancellationToken.None);
        }
        finally
        {
            _operation.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private LocalWorkerProbeResult ProbeCore(
        WorkerProbePayload request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var modelLease = OpenVerifiedModelPayload(request.ModelPath, request.ModelSha256);
        Exception? lastFailure = null;
        foreach (var candidate in GetCandidates(request.RequestedBackend))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var library = OpenLibrary(candidate);
                var probe = library.Probe(candidate.NativeBackend);
                if (!probe.Available)
                {
                    throw new NativeWhisperCallException(
                        NativeWhisperAbi.UnsupportedBackend,
                        "probe");
                }

                nint context = 0;
                try
                {
                    context = library.CreateContext(
                        modelLease.PrepareNativeModelPath(),
                        candidate.NativeBackend,
                        request.MaximumThreads);
                    VerifyCurrentModelIdentity(modelLease);
                    VerifyResolvedBackend(
                        library.ReadContextInfo(context, requireDetectedLanguage: false),
                        candidate);
                }
                finally
                {
                    library.DestroyContext(context);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new LocalWorkerProbeResult(
                    candidate.Token,
                    probe.FreeMemoryBytes > long.MaxValue
                        ? long.MaxValue
                        : checked((long)probe.FreeMemoryBytes));
            }
            catch (Exception exception) when (CanTryFallback(candidate, exception, cancellationToken))
            {
                lastFailure = exception;
            }
            catch (Exception exception)
            {
                throw ToLocalFailure(exception, candidate.Token);
            }
        }

        throw ToLocalFailure(
            lastFailure ?? new NativeWhisperCallException(
                NativeWhisperAbi.UnsupportedBackend,
                "probe"),
            request.RequestedBackend);
    }

    private WorkerResultPayload TranscribeCore(
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var modelLease = OpenVerifiedModelPayload(request.ModelPath, request.ModelSha256);
        var totalMilliseconds = checked(request.EndMilliseconds - request.StartMilliseconds);
        if (totalMilliseconds <= 0 || totalMilliseconds > MaximumChunkMilliseconds)
        {
            throw InputFailure("chunk_duration_invalid");
        }

        NormalizedWaveData wave;
        try
        {
            wave = NormalizedWaveReader.Read(request.InputPath, request.InputSha256);
        }
        catch (NormalizedWaveException exception)
        {
            throw InputFailure(exception.StableCode);
        }

        if (Math.Abs(wave.DurationMilliseconds - totalMilliseconds) > DurationToleranceMilliseconds)
        {
            throw InputFailure("normalized_wav_duration_mismatch");
        }

        var stopwatch = Stopwatch.StartNew();
        SafeReport(progress, CreateProgress("preparing", 0, totalMilliseconds));
        Exception? lastFailure = null;
        foreach (var candidate in GetCandidates(request.Backend))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var library = OpenLibrary(candidate);
                var probe = library.Probe(candidate.NativeBackend);
                if (!probe.Available)
                {
                    throw new NativeWhisperCallException(
                        NativeWhisperAbi.UnsupportedBackend,
                        "probe");
                }

                nint context = 0;
                try
                {
                    context = library.CreateContext(
                        modelLease.PrepareNativeModelPath(),
                        candidate.NativeBackend,
                        request.MaximumThreads);
                    VerifyCurrentModelIdentity(modelLease);
                    var callbackState = new NativeProgressState(
                        progress,
                        totalMilliseconds);
                    var callbackHandle = GCHandle.Alloc(callbackState);
                    int result;
                    try
                    {
                        using var cancellationRegistration = cancellationToken.Register(
                            static state => ((NativeCancellationState)state!).Cancel(),
                            new NativeCancellationState(library, context));
                        cancellationToken.ThrowIfCancellationRequested();
                        result = library.Transcribe(
                            context,
                            wave.Samples,
                            request.Language,
                            NativeProgressCallback,
                            GCHandle.ToIntPtr(callbackHandle));
                    }
                    finally
                    {
                        callbackHandle.Free();
                    }

                    if (result == NativeWhisperAbi.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (result != NativeWhisperAbi.Ok)
                    {
                        throw new NativeWhisperCallException(result, "transcribe");
                    }

                    var contextInfo = library.ReadContextInfo(
                        context,
                        requireDetectedLanguage: true);
                    VerifyResolvedBackend(contextInfo, candidate);
                    var nativeSegments = library.ReadSegments(context);
                    library.VerifyTimings(context);
                    var segments = MapSegments(
                        nativeSegments,
                        totalMilliseconds);
                    SafeReport(progress, CreateProgress(
                        "finalizing",
                        totalMilliseconds,
                        totalMilliseconds));
                    stopwatch.Stop();
                    return new WorkerResultPayload(
                        contextInfo.DetectedLanguage,
                        Math.Max(0, stopwatch.ElapsedMilliseconds),
                        probe.RuntimeVersion,
                        request.ModelSha256,
                        candidate.Token,
                        segments);
                }
                finally
                {
                    library.DestroyContext(context);
                }
            }
            catch (Exception exception) when (CanTryFallback(candidate, exception, cancellationToken))
            {
                lastFailure = exception;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ToLocalFailure(exception, candidate.Token);
            }
        }

        throw ToLocalFailure(
            lastFailure ?? new NativeWhisperCallException(
                NativeWhisperAbi.UnsupportedBackend,
                "transcribe"),
            request.Backend);
    }

    private NativeWhisperLibrary OpenLibrary(NativeRuntimeCandidate candidate)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(_nativeDirectory, candidate.FileName));
        if (!string.Equals(
                Path.GetDirectoryName(absolutePath),
                _nativeDirectory,
                PathComparison))
        {
            throw new NativeWhisperLoadException("native_path_outside_app");
        }

        if (!File.Exists(absolutePath))
        {
            throw new NativeWhisperLoadException("native_runtime_missing");
        }

        try
        {
            return NativeWhisperLibrary.Open(absolutePath, _loader);
        }
        catch (NativeWhisperLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                          or BadImageFormatException
                                          or FileLoadException
                                          or UnauthorizedAccessException)
        {
            throw new NativeWhisperLoadException("native_runtime_load_failed");
        }
    }

    private IReadOnlyList<NativeRuntimeCandidate> GetCandidates(string requestedBackend)
    {
        return (_host, requestedBackend) switch
        {
            (NativeWhisperHost.MacOsArm64, "auto") or
                (NativeWhisperHost.MacOsArm64, "metal") =>
            [
                new NativeRuntimeCandidate(MacOsLibraryName, "metal", NativeWhisperAbi.MetalBackend, true),
                new NativeRuntimeCandidate(MacOsLibraryName, "cpu", NativeWhisperAbi.CpuBackend, false),
            ],
            (NativeWhisperHost.MacOsArm64, "cpu") =>
            [
                new NativeRuntimeCandidate(MacOsLibraryName, "cpu", NativeWhisperAbi.CpuBackend, false),
            ],
            (NativeWhisperHost.WindowsX64, "auto") or
                (NativeWhisperHost.WindowsX64, "vulkan") =>
            [
                new NativeRuntimeCandidate(
                    WindowsVulkanLibraryName,
                    "vulkan",
                    NativeWhisperAbi.VulkanBackend,
                    true),
                new NativeRuntimeCandidate(
                    WindowsCpuLibraryName,
                    "cpu",
                    NativeWhisperAbi.CpuBackend,
                    false),
            ],
            (NativeWhisperHost.WindowsX64, "cpu") =>
            [
                new NativeRuntimeCandidate(
                    WindowsCpuLibraryName,
                    "cpu",
                    NativeWhisperAbi.CpuBackend,
                    false),
            ],
            _ => throw new LocalInferenceException(
                "backend_unavailable",
                "backend_not_supported",
                "The requested local transcription backend is unavailable.",
                retryable: false,
                requestedBackend),
        };
    }

    private static IReadOnlyList<WorkerSegmentPayload> MapSegments(
        IReadOnlyList<NativeWhisperSegmentResult> nativeSegments,
        long chunkDurationMilliseconds)
    {
        var mapped = new List<WorkerSegmentPayload>(nativeSegments.Count);
        foreach (var segment in nativeSegments)
        {
            // Word splitting can emit a leading empty/control-only segment.
            // It carries no recognized content and is not a timed word.
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;
            var localStart = Math.Clamp(segment.StartMilliseconds, 0, chunkDurationMilliseconds);
            var localEnd = Math.Clamp(segment.EndMilliseconds, localStart, chunkDurationMilliseconds);
            mapped.Add(new WorkerSegmentPayload(
                localStart,
                localEnd,
                segment.Text));
        }

        return mapped;
    }

    private static void VerifyResolvedBackend(
        NativeContextInfoResult contextInfo,
        NativeRuntimeCandidate candidate)
    {
        if (contextInfo.ResolvedBackend != candidate.NativeBackend)
        {
            throw new NativeWhisperCallException(
                NativeWhisperAbi.UnsupportedBackend,
                "context_info_backend");
        }
    }

    private static bool CanTryFallback(
        NativeRuntimeCandidate candidate,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!candidate.HasFallback || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            NativeWhisperCallException { ResultCode: NativeWhisperAbi.ModelLoadFailed } => false,
            OperationCanceledException => false,
            LocalInferenceException => false,
            _ => true,
        };
    }

    private static LocalInferenceException ToLocalFailure(Exception exception, string backend)
    {
        if (exception is LocalInferenceException localFailure)
        {
            return localFailure;
        }

        if (exception is NativeWhisperLoadException loadFailure)
        {
            var integrityFailure = loadFailure.StableCode is "native_abi_mismatch"
                or "native_probe_abi_mismatch"
                or "native_runtime_version_mismatch"
                or "native_symbol_missing";
            return new LocalInferenceException(
                integrityFailure ? "native_failure" : "backend_unavailable",
                loadFailure.StableCode,
                integrityFailure
                    ? "The local transcription runtime is incompatible with this application."
                    : "The local transcription runtime could not be loaded.",
                retryable: false,
                backend);
        }

        if (exception is NativeWhisperCallException callFailure)
        {
            return callFailure.ResultCode switch
            {
                NativeWhisperAbi.ModelLoadFailed => new LocalInferenceException(
                    "model_invalid",
                    "model_load_failed",
                    "The verified transcription model could not be loaded.",
                    retryable: false,
                    backend),
                NativeWhisperAbi.UnsupportedBackend => new LocalInferenceException(
                    "backend_unavailable",
                    "backend_probe_failed",
                    "The requested local transcription backend is unavailable.",
                    retryable: true,
                    backend),
                NativeWhisperAbi.AbiMismatch => new LocalInferenceException(
                    "native_failure",
                    "native_abi_mismatch",
                    "The local transcription runtime is incompatible with this application.",
                    retryable: false,
                    backend),
                _ => new LocalInferenceException(
                    "native_failure",
                    "native_transcription_failed",
                    "The local transcription runtime could not process this chunk.",
                    retryable: true,
                    backend),
            };
        }

        return new LocalInferenceException(
            "native_failure",
            "native_runtime_failure",
            "The local transcription runtime could not process this chunk.",
            retryable: true,
            backend);
    }

    private static LocalInferenceException InputFailure(string stableCode) => new(
        "input_invalid",
        stableCode,
        "Local transcription requires a normalized 16 kHz mono WAV checkpoint.",
        retryable: false);

    private static void ValidateModelPath(string modelPath)
    {
        if (!Path.IsPathFullyQualified(modelPath))
        {
            throw new LocalInferenceException(
                "model_invalid",
                "model_path_not_absolute",
                "The local transcription model path is invalid.",
                retryable: false);
        }

        if (!File.Exists(modelPath))
        {
            throw new LocalInferenceException(
                "model_invalid",
                "model_missing",
                "The local transcription model is missing.",
                retryable: false);
        }
    }

    private VerifiedModelPayload OpenVerifiedModelPayload(string modelPath, string expectedSha256)
    {
        var canonicalPath = Path.GetFullPath(modelPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalInferenceException(
                "model_invalid",
                "model_unreadable",
                "The verified transcription model could not be opened.",
                retryable: false);
        }

        try
        {
            var actualSha256 = SHA256.HashData(stream);
            var expected = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, expected))
            {
                throw new LocalInferenceException(
                    "model_invalid",
                    "model_hash_mismatch",
                    "The local transcription model no longer matches its verified identity.",
                    retryable: false);
            }

            stream.Position = 0;
            return new VerifiedModelPayload(
                canonicalPath,
                ResolveNativeModelPath(stream, canonicalPath),
                expected,
                stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private string ResolveNativeModelPath(FileStream stream, string canonicalPath)
    {
        if (_host != NativeWhisperHost.MacOsArm64)
        {
            // FileShare.Read denies write/delete sharing on Windows while native opens this path.
            return canonicalPath;
        }

        var handle = stream.SafeFileHandle;
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw ModelIdentityChanged();
        }

        var fileDescriptor = handle.DangerousGetHandle().ToInt64();
        if (fileDescriptor is < 0 or > int.MaxValue)
        {
            throw ModelIdentityChanged();
        }

        // macOS /dev/fd reopens the already-hashed inode held by the managed lease.
        return "/dev/fd/" + fileDescriptor.ToString(CultureInfo.InvariantCulture);
    }

    private static void VerifyCurrentModelIdentity(VerifiedModelPayload model)
    {
        try
        {
            using var current = new FileStream(
                model.CanonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            var actualSha256 = SHA256.HashData(current);
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, model.ExpectedSha256))
            {
                throw ModelIdentityChanged();
            }
        }
        catch (LocalInferenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ModelIdentityChanged();
        }
    }

    private static LocalInferenceException ModelIdentityChanged() => new(
        "model_invalid",
        "model_identity_changed",
        "The local transcription model changed while its context was loading.",
        retryable: false);

    private static WorkerProgressPayload CreateProgress(
        string stage,
        long completedMilliseconds,
        long totalMilliseconds)
    {
        long cpuMilliseconds;
        try
        {
            using var process = Process.GetCurrentProcess();
            cpuMilliseconds = Math.Max(0, checked((long)process.TotalProcessorTime.TotalMilliseconds));
        }
        catch (Exception)
        {
            cpuMilliseconds = 0;
        }

        return new WorkerProgressPayload(
            stage,
            Math.Clamp(completedMilliseconds, 0, totalMilliseconds),
            totalMilliseconds,
            Math.Max(0, Environment.WorkingSet),
            cpuMilliseconds);
    }

    private static void SafeReport(
        IProgress<WorkerProgressPayload> progress,
        WorkerProgressPayload payload)
    {
        try
        {
            progress.Report(payload);
        }
        catch (Exception)
        {
            // Progress is advisory and never crosses the native boundary as an exception.
        }
    }

    private static void ReportNativeProgress(nint userData, uint percent)
    {
        if (userData == 0)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userData).Target is NativeProgressState state)
            {
                state.Report(percent);
            }
        }
        catch (Exception)
        {
            // Reverse P/Invoke callbacks must never unwind into the native runtime.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private StringComparison PathComparison => _host == NativeWhisperHost.WindowsX64
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record NativeRuntimeCandidate(
        string FileName,
        string Token,
        uint NativeBackend,
        bool HasFallback);

    private sealed class VerifiedModelPayload(
        string canonicalPath,
        string nativeModelPath,
        byte[] expectedSha256,
        FileStream stream) : IDisposable
    {
        public string CanonicalPath { get; } = canonicalPath;

        private string NativeModelPath { get; } = nativeModelPath;

        public byte[] ExpectedSha256 { get; } = expectedSha256;

        public string PrepareNativeModelPath()
        {
            stream.Position = 0;
            return NativeModelPath;
        }

        public void Dispose() => stream.Dispose();
    }

    private sealed class NativeCancellationState(
        NativeWhisperLibrary library,
        nint context)
    {
        private int _cancelled;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                try
                {
                    library.Cancel(context);
                }
                catch (Exception)
                {
                    // The worker's bounded shutdown remains the final cancellation guard.
                }
            }
        }
    }

    private sealed class NativeProgressState(
        IProgress<WorkerProgressPayload> progress,
        long totalMilliseconds)
    {
        private int _lastPercent = -1;

        public void Report(uint value)
        {
            var percent = checked((int)Math.Min(value, 100));
            var previous = Volatile.Read(ref _lastPercent);
            while (percent > previous)
            {
                var observed = Interlocked.CompareExchange(ref _lastPercent, percent, previous);
                if (observed == previous)
                {
                    var completed = checked(totalMilliseconds * percent / 100);
                    SafeReport(progress, CreateProgress(
                        "inferencing",
                        completed,
                        totalMilliseconds));
                    return;
                }

                previous = observed;
            }
        }
    }
}
