using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Recording;
using IsTranscribe.Core.Audio;
using IsTranscribe.Application.Runtime;

namespace IsTranscribe.Platform.Windows.Audio.Finalization;

/// <summary>
/// Encodes, verifies and atomically promotes one user-facing meeting artifact.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization
/// </remarks>
internal sealed class WindowsRecordingArtifactFinalizer(
    IAudioArtifactEncoder encoder,
    BootstrapFileLogger logger)
{
    // One MPEG-1 Layer III frame at the canonical 48 kHz sample rate is 24 ms. The
    // 100 ms envelope also absorbs Media Foundation frame/padding duration rounding
    // while remaining small enough to reject a meaningfully truncated artifact.
    internal static readonly TimeSpan Mp3DurationTolerance = TimeSpan.FromMilliseconds(100);

    private readonly IAudioArtifactEncoder _encoder = encoder;
    private readonly BootstrapFileLogger _logger = logger;

    public async Task<RecordingArtifactFinalizationResult> FinalizeAsync(
        RecordingArtifactFinalizationRequest request,
        Func<RecordingArtifactStage, string?, double?, CancellationToken, ValueTask> checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidatePaths(request);

        var sourcePath = SelectReadableSource(request.CaptureArtifacts);
        try
        {
            var finalProbe = ProbeSafely(request.FinalPrimaryPath);
            if (finalProbe.IsReadable)
            {
                if (HasExpectedDuration(finalProbe, request.ExpectedPreparedSourceDuration, out var durationError))
                {
                    await checkpoint(
                        RecordingArtifactStage.Ready,
                        request.FinalPrimaryPath,
                        1,
                        cancellationToken).ConfigureAwait(false);
                    return RecordingArtifactFinalizationResult.Ready(
                        request.FinalPrimaryPath,
                        finalProbe.Duration);
                }

                if (sourcePath is null)
                {
                    return await RequireAttentionAsync(
                        request,
                        checkpoint,
                        "primary_artifact_duration_mismatch",
                        durationError,
                        cancellationToken).ConfigureAwait(false);
                }

                TryDeleteArtifact(request.FinalPrimaryPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.FinalPrimaryPath)!);
            await checkpoint(
                RecordingArtifactStage.Processing,
                request.StagedPrimaryPath,
                0,
                cancellationToken).ConfigureAwait(false);

            var stagedProbe = ProbeSafely(request.StagedPrimaryPath);
            if (stagedProbe.IsReadable
                && !HasExpectedDuration(
                    stagedProbe,
                    request.ExpectedPreparedSourceDuration,
                    out var stagedDurationError))
            {
                if (sourcePath is null)
                {
                    return await RequireAttentionAsync(
                        request,
                        checkpoint,
                        "primary_artifact_duration_mismatch",
                        stagedDurationError,
                        cancellationToken).ConfigureAwait(false);
                }

                TryDeleteArtifact(request.StagedPrimaryPath);
                stagedProbe = ProbeSafely(request.StagedPrimaryPath);
            }

            if (!stagedProbe.IsReadable)
            {
                TryDeleteArtifact(request.StagedPrimaryPath);
                if (sourcePath is null)
                {
                    return await RequireAttentionAsync(
                        request,
                        checkpoint,
                        "artifact_source_missing",
                        "No readable source audio is available for finalization.",
                        cancellationToken).ConfigureAwait(false);
                }

                var availability = _encoder.ProbeAvailability();
                if (!availability.IsAvailable)
                {
                    return await RequireAttentionAsync(
                        request,
                        checkpoint,
                        availability.ReasonCode ?? "mp3_encoder_unavailable",
                        availability.Detail ?? "The Windows MP3 encoder is unavailable.",
                        cancellationToken).ConfigureAwait(false);
                }

                var encodeProgress = new ThrottledEncodingProgress(fraction =>
                    checkpoint(
                            RecordingArtifactStage.Processing,
                            request.StagedPrimaryPath,
                            fraction,
                            CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult());
                var encodeResult = await _encoder.EncodeAsync(
                    new AudioArtifactEncodeRequest(sourcePath, request.StagedPrimaryPath),
                    encodeProgress,
                    cancellationToken).ConfigureAwait(false);
                if (!encodeResult.Succeeded)
                {
                    TryDeleteArtifact(request.StagedPrimaryPath);
                    return await RequireAttentionAsync(
                        request,
                        checkpoint,
                        encodeResult.ErrorCode ?? "mp3_encode_failed",
                        encodeResult.ErrorMessage ?? "The primary audio artifact could not be encoded.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await checkpoint(
                RecordingArtifactStage.Verifying,
                request.StagedPrimaryPath,
                1,
                cancellationToken).ConfigureAwait(false);
            stagedProbe = ProbeSafely(request.StagedPrimaryPath);
            if (!stagedProbe.IsReadable || stagedProbe.Duration is null or { Ticks: <= 0 })
            {
                TryDeleteArtifact(request.StagedPrimaryPath);
                return await RequireAttentionAsync(
                    request,
                    checkpoint,
                    stagedProbe.ReasonCode ?? "primary_artifact_unreadable",
                    stagedProbe.Detail ?? "The encoded primary artifact did not pass verification.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!HasExpectedDuration(
                    stagedProbe,
                    request.ExpectedPreparedSourceDuration,
                    out var verifiedDurationError))
            {
                TryDeleteArtifact(request.StagedPrimaryPath);
                return await RequireAttentionAsync(
                    request,
                    checkpoint,
                    "primary_artifact_duration_mismatch",
                    verifiedDurationError,
                    cancellationToken).ConfigureAwait(false);
            }

            await checkpoint(
                RecordingArtifactStage.Promoting,
                request.StagedPrimaryPath,
                1,
                cancellationToken).ConfigureAwait(false);
            PromoteAtomically(request.StagedPrimaryPath, request.FinalPrimaryPath);

            finalProbe = ProbeSafely(request.FinalPrimaryPath);
            if (!finalProbe.IsReadable)
            {
                return await RequireAttentionAsync(
                    request,
                    checkpoint,
                    finalProbe.ReasonCode ?? "promoted_artifact_unreadable",
                    finalProbe.Detail ?? "The promoted primary artifact could not be reopened.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!HasExpectedDuration(
                    finalProbe,
                    request.ExpectedPreparedSourceDuration,
                    out var promotedDurationError))
            {
                return await RequireAttentionAsync(
                    request,
                    checkpoint,
                    "primary_artifact_duration_mismatch",
                    promotedDurationError,
                    cancellationToken).ConfigureAwait(false);
            }

            await checkpoint(
                RecordingArtifactStage.Ready,
                request.FinalPrimaryPath,
                1,
                cancellationToken).ConfigureAwait(false);
            _logger.LogEvent(
                "Info",
                "RECORDING_ARTIFACT_READY",
                "Primary meeting artifact was verified and promoted.",
                metadata: new Dictionary<string, object?>
                {
                    ["session_id"] = request.SessionId.ToString("N"),
                    ["file_bytes"] = finalProbe.FileBytes,
                    ["duration_ms"] = finalProbe.Duration?.TotalMilliseconds
                });
            return RecordingArtifactFinalizationResult.Ready(
                request.FinalPrimaryPath,
                finalProbe.Duration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RequireAttentionAsync(
                request,
                checkpoint,
                "artifact_finalization_cancelled",
                "Artifact finalization was interrupted and can be recovered on the next launch.",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Recording artifact finalization failed for session {request.SessionId:N}.");
            return await RequireAttentionAsync(
                request,
                checkpoint,
                "artifact_finalization_failed",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<RecordingArtifactFinalizationResult> RequireAttentionAsync(
        RecordingArtifactFinalizationRequest request,
        Func<RecordingArtifactStage, string?, double?, CancellationToken, ValueTask> checkpoint,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var recoverablePath = SelectRecoverablePath(request);
        await checkpoint(
            RecordingArtifactStage.AttentionRequired,
            recoverablePath,
            null,
            cancellationToken).ConfigureAwait(false);
        _logger.LogEvent(
            "Warning",
            "RECORDING_ARTIFACT_ATTENTION_REQUIRED",
            "Recording sources were preserved because finalization needs attention.",
            metadata: new Dictionary<string, object?>
            {
                ["session_id"] = request.SessionId.ToString("N"),
                ["error_code"] = errorCode,
                ["recoverable_artifact_present"] = recoverablePath is not null,
                ["recoverable_extension"] = recoverablePath is null
                    ? null
                    : Path.GetExtension(recoverablePath)
            });
        return RecordingArtifactFinalizationResult.AttentionRequired(
            errorCode,
            errorMessage,
            recoverablePath);
    }

    private AudioArtifactReadabilityProbe ProbeSafely(string path)
    {
        if (!File.Exists(path))
        {
            return new AudioArtifactReadabilityProbe(
                false,
                0,
                null,
                null,
                null,
                null,
                "artifact_missing",
                "Artifact file does not exist.");
        }

        try
        {
            return _encoder.ProbeReadability(path);
        }
        catch (Exception exception)
        {
            return new AudioArtifactReadabilityProbe(
                false,
                0,
                null,
                null,
                null,
                null,
                "artifact_probe_failed",
                exception.Message);
        }
    }

    private string? SelectRecoverablePath(RecordingArtifactFinalizationRequest request)
    {
        if (ProbeSafely(request.FinalPrimaryPath).IsReadable)
        {
            return request.FinalPrimaryPath;
        }

        if (ProbeSafely(request.StagedPrimaryPath).IsReadable)
        {
            return request.StagedPrimaryPath;
        }

        return SelectReadableSource(request.CaptureArtifacts);
    }

    private static string? SelectReadableSource(IReadOnlyList<AudioCaptureArtifactSnapshot> artifacts) =>
        artifacts
            .OrderBy(static artifact => artifact.Kind switch
            {
                AudioCaptureArtifactKind.Mixed => 0,
                AudioCaptureArtifactKind.Output => 1,
                AudioCaptureArtifactKind.Microphone => 2,
                _ => 3
            })
            .Select(static artifact => artifact.Path)
            .FirstOrDefault(IsReadableFile);

    private static bool IsReadableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void PromoteAtomically(string stagedPath, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            throw new IOException("The final primary artifact path already exists.");
        }

        File.Move(stagedPath, finalPath);
    }

    private static bool HasExpectedDuration(
        AudioArtifactReadabilityProbe probe,
        TimeSpan expectedDuration,
        out string errorMessage)
    {
        if (probe.Duration is not { Ticks: > 0 } actualDuration)
        {
            errorMessage = "The primary artifact has no readable duration.";
            return false;
        }

        var difference = (actualDuration - expectedDuration).Duration();
        if (difference <= Mp3DurationTolerance)
        {
            errorMessage = string.Empty;
            return true;
        }

        errorMessage = FormattableString.Invariant(
            $"The primary artifact duration ({actualDuration.TotalMilliseconds:F0} ms) differs from the prepared source ({expectedDuration.TotalMilliseconds:F0} ms) by {difference.TotalMilliseconds:F0} ms; the MP3 tolerance is {Mp3DurationTolerance.TotalMilliseconds:F0} ms.");
        return false;
    }

    private static void TryDeleteArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked artifact remains recoverable and will be reconciled on restart.
        }
    }

    private static void ValidatePaths(RecordingArtifactFinalizationRequest request)
    {
        if (request.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id is required.", nameof(request));
        }

        if (request.ExpectedPreparedSourceDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ExpectedPreparedSourceDuration,
                "Expected prepared-source duration must be positive.");
        }

        var stagedDirectory = Path.GetFullPath(Path.GetDirectoryName(request.StagedPrimaryPath)!);
        var finalDirectory = Path.GetFullPath(Path.GetDirectoryName(request.FinalPrimaryPath)!);
        if (!string.Equals(stagedDirectory, finalDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Staged and final primary artifacts must share one directory for atomic promotion.",
                nameof(request));
        }
    }

    private sealed class ThrottledEncodingProgress(Action<double> callback)
        : IProgress<AudioArtifactEncodingProgress>
    {
        private const double MinimumFractionStep = 0.02;
        private static readonly long MinimumTimestampStep = System.Diagnostics.Stopwatch.Frequency;
        private readonly object _gate = new();
        private double _lastFraction;
        private long _lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        public void Report(AudioArtifactEncodingProgress value)
        {
            lock (_gate)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (value.Fraction < 1
                    && value.Fraction - _lastFraction < MinimumFractionStep
                    && now - _lastTimestamp < MinimumTimestampStep)
                {
                    return;
                }

                _lastFraction = value.Fraction;
                _lastTimestamp = now;
            }

            callback(Math.Clamp(value.Fraction, 0, 1));
        }
    }
}

internal sealed record RecordingArtifactFinalizationRequest(
    Guid SessionId,
    IReadOnlyList<AudioCaptureArtifactSnapshot> CaptureArtifacts,
    string StagedPrimaryPath,
    string FinalPrimaryPath,
    TimeSpan ExpectedPreparedSourceDuration);

internal sealed record RecordingArtifactFinalizationResult(
    bool IsReady,
    string? PrimaryAudioPath,
    TimeSpan? Duration,
    string? ErrorCode,
    string? ErrorMessage,
    string? RecoverableAudioPath)
{
    public static RecordingArtifactFinalizationResult Ready(string primaryAudioPath, TimeSpan? duration) =>
        new(true, primaryAudioPath, duration, null, null, primaryAudioPath);

    public static RecordingArtifactFinalizationResult AttentionRequired(
        string errorCode,
        string errorMessage,
        string? recoverableAudioPath) =>
        new(false, null, null, errorCode, errorMessage, recoverableAudioPath);
}
