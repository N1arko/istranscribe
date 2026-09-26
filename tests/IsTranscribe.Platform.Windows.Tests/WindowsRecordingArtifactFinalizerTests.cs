using IsTranscribe.Core.Audio;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Application.Recording;
using IsTranscribe.Platform.Windows.Audio.Finalization;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#finalization
/// </summary>
public sealed class WindowsRecordingArtifactFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_EncodesVerifiesAndPromotesWithoutDeletingSource()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var encoder = new FakeEncoder();
            var finalizer = CreateFinalizer(root, encoder);
            var checkpoints = new List<RecordingArtifactStage>();

            var result = await finalizer.FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                (stage, _, _, _) =>
                {
                    checkpoints.Add(stage);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(finalPath, result.PrimaryAudioPath);
            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(stagedPath));
            Assert.True(File.Exists(sourcePath));
            Assert.Equal(1, encoder.EncodeCount);
            Assert.Equal(
                [
                    RecordingArtifactStage.Processing,
                    RecordingArtifactStage.Verifying,
                    RecordingArtifactStage.Promoting,
                    RecordingArtifactStage.Ready
                ],
                checkpoints.Distinct().ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_WhenEncoderIsUnavailable_PreservesSourceAndRequiresAttention()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "output.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var encoder = new FakeEncoder(isAvailable: false);
            var finalizer = CreateFinalizer(root, encoder);

            var result = await finalizer.FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal("mp3_encoder_unavailable", result.ErrorCode);
            Assert.Equal(sourcePath, result.RecoverableAudioPath);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(RecordingArtifactStage.Processing)]
    [InlineData(RecordingArtifactStage.Verifying)]
    [InlineData(RecordingArtifactStage.Promoting)]
    [InlineData(RecordingArtifactStage.Ready)]
    public async Task FinalizeAsync_CheckpointFailureAtEveryStagePreservesRecoverableAudio(
        RecordingArtifactStage injectedStage)
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var injected = false;

            var result = await CreateFinalizer(root, new FakeEncoder()).FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                (stage, _, _, _) =>
                {
                    if (stage == injectedStage && !injected)
                    {
                        injected = true;
                        throw new IOException($"Injected checkpoint failure at {stage}.");
                    }

                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(injected);
            Assert.False(result.IsReady);
            Assert.True(File.Exists(sourcePath));
            Assert.True(
                File.Exists(sourcePath) || File.Exists(stagedPath) || File.Exists(finalPath),
                "Failure injection removed every readable artifact.");
            if (injectedStage == RecordingArtifactStage.Ready)
            {
                Assert.True(File.Exists(finalPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_DiskFullClassificationPreservesSource()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

            var result = await CreateFinalizer(
                    root,
                    new FakeEncoder(encodeErrorCode: "disk_full"))
                .FinalizeAsync(
                    CreateRequest(sourcePath, stagedPath, finalPath),
                    static (_, _, _, _) => ValueTask.CompletedTask,
                    CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal("disk_full", result.ErrorCode);
            Assert.Equal(sourcePath, result.RecoverableAudioPath);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(stagedPath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_UnavailableTargetDirectoryPreservesSource()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var blockedDirectory = Path.Combine(root, "blocked");
            var finalPath = Path.Combine(blockedDirectory, "meeting.mp3");
            var stagedPath = Path.Combine(blockedDirectory, ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(blockedDirectory, [1]);

            var result = await CreateFinalizer(root, new FakeEncoder()).FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_ResumesFromReadableStagedArtifactWithoutReencoding()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(stagedPath, FakeEncoder.EncodedBytes);
            var encoder = new FakeEncoder();
            var finalizer = CreateFinalizer(root, encoder);

            var result = await finalizer.FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(0, encoder.EncodeCount);
            Assert.True(File.Exists(finalPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_RebuildsReadableTruncatedArtifactWhenSourceExists(bool artifactIsFinal)
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            var truncatedPath = artifactIsFinal ? finalPath : stagedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(truncatedPath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(truncatedPath, FakeEncoder.TruncatedEncodedBytes);
            var encoder = new FakeEncoder(truncatedDuration: TimeSpan.FromSeconds(1));
            var finalizer = CreateFinalizer(root, encoder);

            var result = await finalizer.FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(1, encoder.EncodeCount);
            Assert.Equal(FakeEncoder.EncodedBytes, await File.ReadAllBytesAsync(finalPath));
            Assert.False(File.Exists(stagedPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_AcceptsReadableStagedArtifactWithinMp3DurationTolerance()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(stagedPath, FakeEncoder.TruncatedEncodedBytes);
            var encoder = new FakeEncoder(truncatedDuration: TimeSpan.FromMilliseconds(1_925));
            var finalizer = CreateFinalizer(root, encoder);

            var result = await finalizer.FinalizeAsync(
                CreateRequest(sourcePath, stagedPath, finalPath),
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(0, encoder.EncodeCount);
            Assert.Equal(FakeEncoder.TruncatedEncodedBytes, await File.ReadAllBytesAsync(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_RejectsNewlyEncodedArtifactOutsideMp3DurationTolerance()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "temp", "mix.wav");
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var encoder = new FakeEncoder();
            var finalizer = CreateFinalizer(root, encoder);
            var request = new RecordingArtifactFinalizationRequest(
                Guid.NewGuid(),
                [
                    new AudioCaptureArtifactSnapshot(
                        AudioCaptureArtifactKind.Mixed,
                        sourcePath,
                        new FileInfo(sourcePath).Length,
                        DateTimeOffset.UtcNow)
                ],
                stagedPath,
                finalPath,
                TimeSpan.FromSeconds(3));

            var result = await finalizer.FinalizeAsync(
                request,
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal("primary_artifact_duration_mismatch", result.ErrorCode);
            Assert.Equal(sourcePath, result.RecoverableAudioPath);
            Assert.Equal(1, encoder.EncodeCount);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(stagedPath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeAsync_PreservesReadableTruncatedArtifactWhenSourceIsMissing(bool artifactIsFinal)
    {
        var root = CreateRoot();
        try
        {
            var finalPath = Path.Combine(root, "recordings", "meeting.mp3");
            var stagedPath = Path.Combine(root, "recordings", ".meeting.mp3.partial");
            var truncatedPath = artifactIsFinal ? finalPath : stagedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(truncatedPath)!);
            await File.WriteAllBytesAsync(truncatedPath, FakeEncoder.TruncatedEncodedBytes);
            var encoder = new FakeEncoder(truncatedDuration: TimeSpan.FromSeconds(1));
            var finalizer = CreateFinalizer(root, encoder);
            var request = new RecordingArtifactFinalizationRequest(
                Guid.NewGuid(),
                [],
                stagedPath,
                finalPath,
                TimeSpan.FromSeconds(2));

            var result = await finalizer.FinalizeAsync(
                request,
                static (_, _, _, _) => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal("primary_artifact_duration_mismatch", result.ErrorCode);
            Assert.Equal(truncatedPath, result.RecoverableAudioPath);
            Assert.Equal(0, encoder.EncodeCount);
            Assert.True(File.Exists(truncatedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WindowsRecordingArtifactFinalizer CreateFinalizer(string root, IAudioArtifactEncoder encoder) =>
        new(encoder, new BootstrapFileLogger(Path.Combine(root, "logs", "host.log")));

    private static RecordingArtifactFinalizationRequest CreateRequest(
        string sourcePath,
        string stagedPath,
        string finalPath) =>
        new(
            Guid.NewGuid(),
            [
                new AudioCaptureArtifactSnapshot(
                    AudioCaptureArtifactKind.Mixed,
                    sourcePath,
                    new FileInfo(sourcePath).Length,
                    DateTimeOffset.UtcNow)
            ],
            stagedPath,
            finalPath,
            TimeSpan.FromSeconds(2));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-finalizer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeEncoder(
        bool isAvailable = true,
        TimeSpan? truncatedDuration = null,
        string? encodeErrorCode = null) : IAudioArtifactEncoder
    {
        public static readonly byte[] EncodedBytes = [0xFF, 0xFB, 0x94, 0x00, 1, 2, 3, 4];
        public static readonly byte[] TruncatedEncodedBytes = [0xFF, 0xFB, 0x94, 0x00, 5, 6, 7, 8];

        private readonly TimeSpan? _truncatedDuration = truncatedDuration;

        public int EncodeCount { get; private set; }

        public AudioArtifactEncoderAvailability ProbeAvailability() => new(
            isAvailable,
            "MP3",
            48_000,
            2,
            16,
            128_000,
            isAvailable ? null : "mp3_encoder_unavailable",
            isAvailable ? null : "fixture unavailable");

        public async Task<AudioArtifactEncodeResult> EncodeAsync(
            AudioArtifactEncodeRequest request,
            IProgress<AudioArtifactEncodingProgress>? progress,
            CancellationToken cancellationToken)
        {
            EncodeCount++;
            if (!string.IsNullOrWhiteSpace(encodeErrorCode))
            {
                return new AudioArtifactEncodeResult(
                    false,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    encodeErrorCode,
                    "Injected encode failure.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.PartialOutputPath)!);
            await File.WriteAllBytesAsync(request.PartialOutputPath, EncodedBytes, cancellationToken);
            progress?.Report(new AudioArtifactEncodingProgress(4, 4, 1));
            return new AudioArtifactEncodeResult(
                true,
                request.PartialOutputPath,
                EncodedBytes.Length,
                TimeSpan.FromSeconds(2),
                48_000,
                2,
                16,
                128_000,
                null,
                null);
        }

        public AudioArtifactReadabilityProbe ProbeReadability(string artifactPath)
        {
            var bytes = File.Exists(artifactPath) ? File.ReadAllBytes(artifactPath) : [];
            var duration = bytes.SequenceEqual(EncodedBytes)
                ? TimeSpan.FromSeconds(2)
                : _truncatedDuration.HasValue && bytes.SequenceEqual(TruncatedEncodedBytes)
                    ? _truncatedDuration
                    : null;
            var readable = duration.HasValue;
            return new AudioArtifactReadabilityProbe(
                readable,
                readable ? bytes.Length : 0,
                duration,
                readable ? 48_000 : null,
                readable ? 2 : null,
                readable ? 16 : null,
                readable ? null : "unreadable",
                readable ? null : "fixture unreadable");
        }
    }
}
