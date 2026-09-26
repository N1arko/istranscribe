using IsTranscribe.Host.Audio.Capture;
using NAudio.Wave;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class BufferedWaveArtifactWriterTests
{
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
    [Fact]
    public void PromoteFlushesBufferedAudioIntoWaveArtifact()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var artifactPath = Path.Combine(tempRoot, "output.wav");
            using var writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Output,
                artifactPath,
                new WaveFormat(8_000, 16, 1),
                prebufferSeconds: 1);

            writer.Append([1, 2, 3, 4], 4);
            Assert.False(File.Exists(artifactPath));

            writer.Promote();
            writer.Append([5, 6], 2);
            var artifact = writer.Complete(TimeSpan.FromMilliseconds(125));

            Assert.NotNull(artifact);
            Assert.Equal(6, artifact!.BytesWritten);
            Assert.Equal(TimeSpan.FromMilliseconds(125), artifact.RelativeStartOffset);
            Assert.True(File.Exists(artifactPath));

            using var reader = new WaveFileReader(artifactPath);
            var buffer = new byte[6];
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            Assert.Equal(6, bytesRead);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, buffer);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DiscardRemovesUnpromotedStateWithoutCreatingArtifact()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var artifactPath = Path.Combine(tempRoot, "mic.wav");
            using var writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Microphone,
                artifactPath,
                new WaveFormat(8_000, 16, 1),
                prebufferSeconds: 1);

            writer.Append([1, 2, 3, 4], 4);
            writer.Discard();

            Assert.False(File.Exists(artifactPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#mixing
    [Fact]
    public void CompleteAddsDiscardedRollingPrebufferDurationToTimelineOffset()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var artifactPath = Path.Combine(tempRoot, "output.wav");
            using var writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Output,
                artifactPath,
                new WaveFormat(4, 8, 1),
                prebufferSeconds: 1);

            writer.Append([1, 2, 3, 4], 4);
            writer.Append([5, 6, 7, 8], 4);
            writer.Promote();
            var artifact = writer.Complete(TimeSpan.FromMilliseconds(100));

            Assert.NotNull(artifact);
            Assert.Equal(TimeSpan.FromMilliseconds(1_100), artifact!.RelativeStartOffset);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    [Fact]
    public void PersistingWriterCheckpointsReadableWaveHeaderBeforeFinalClose()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var artifactPath = Path.Combine(tempRoot, "output.wav");
            using var writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Output,
                artifactPath,
                new WaveFormat(8_000, 16, 1),
                prebufferSeconds: 0);
            writer.Promote();
            var threeSeconds = new byte[8_000 * 2 * 3];

            writer.Append(threeSeconds, 2);
            using (var firstPacketStream = new FileStream(
                       artifactPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite))
            using (var firstPacketReader = new WaveFileReader(firstPacketStream))
            {
                Assert.Equal(2, firstPacketReader.Length);
            }

            writer.Append(threeSeconds, threeSeconds.Length - 2);

            using (var stream = new FileStream(
                       artifactPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite))
            using (var reader = new WaveFileReader(stream))
            {
                Assert.InRange(reader.TotalTime.TotalSeconds, 2.99, 3.01);
                Assert.Equal(threeSeconds.Length, reader.Length);
            }

            var artifact = writer.Complete();
            Assert.NotNull(artifact);
            Assert.Equal(threeSeconds.Length, artifact.BytesWritten);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
    [Fact]
    public async Task StopCompletionSignalsWaiterAndEventWhenFinalCheckpointFlushFails()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var artifactPath = Path.Combine(tempRoot, "output.wav");
            var failFlush = false;
            using var writer = new BufferedWaveArtifactWriter(
                AudioCaptureArtifactKind.Output,
                artifactPath,
                new WaveFormat(8_000, 16, 1),
                prebufferSeconds: 0,
                stream =>
                {
                    if (failFlush)
                    {
                        throw new IOException("Injected durable flush failure.");
                    }

                    stream.Flush(flushToDisk: true);
                });
            writer.Promote();
            writer.Append([1, 2, 3, 4], 4);
            failFlush = true;

            var completion = new CaptureStopCompletion();
            Exception? reportedFailure = null;
            var stoppedNotified = false;
            completion.Complete(
                () => writer.Complete(),
                exception => reportedFailure = exception,
                () => stoppedNotified = true);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await completion.WaitAsync(timeout.Token);

            Assert.IsType<IOException>(reportedFailure);
            Assert.True(stoppedNotified);
            using var releasedFile = new FileStream(
                artifactPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
    [Fact]
    public void PauseResumeWritesSilenceIntoEveryPersistingSourceWithoutTimelineDrift()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var timeProvider = new ManualTimeProvider();
            var outputPath = Path.Combine(tempRoot, "output.wav");
            var microphonePath = Path.Combine(tempRoot, "mic.wav");
            using var output = CreateWriter(
                AudioCaptureArtifactKind.Output,
                outputPath,
                new WaveFormat(8_000, 16, 1),
                timeProvider);
            using var microphone = CreateWriter(
                AudioCaptureArtifactKind.Microphone,
                microphonePath,
                new WaveFormat(16_000, 16, 1),
                timeProvider);
            output.Promote();
            microphone.Promote();
            output.Append(new byte[1_600], 1_600);
            microphone.Append(new byte[3_200], 3_200);

            output.SetPaused(true);
            microphone.SetPaused(true);
            timeProvider.Advance(TimeSpan.FromSeconds(1.5));
            output.SetPaused(false);
            microphone.SetPaused(false);
            output.Append(new byte[1_600], 1_600);
            microphone.Append(new byte[3_200], 3_200);

            output.Complete();
            microphone.Complete();

            using var outputReader = new WaveFileReader(outputPath);
            using var microphoneReader = new WaveFileReader(microphonePath);
            Assert.InRange(outputReader.TotalTime.TotalSeconds, 1.699, 1.701);
            Assert.InRange(microphoneReader.TotalTime.TotalSeconds, 1.699, 1.701);
            Assert.InRange(
                Math.Abs((outputReader.TotalTime - microphoneReader.TotalTime).TotalMilliseconds),
                0,
                0.1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
    [Fact]
    public void CompleteWhilePausedWritesSilenceThroughTheStopTimestamp()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var timeProvider = new ManualTimeProvider();
            var artifactPath = Path.Combine(tempRoot, "output.wav");
            using var writer = CreateWriter(
                AudioCaptureArtifactKind.Output,
                artifactPath,
                new WaveFormat(8_000, 16, 1),
                timeProvider);
            writer.Promote();
            writer.Append(new byte[1_600], 1_600);
            writer.SetPaused(true);

            timeProvider.Advance(TimeSpan.FromSeconds(1.5));
            writer.Complete();

            using var reader = new WaveFileReader(artifactPath);
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.599, 1.601);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static BufferedWaveArtifactWriter CreateWriter(
        AudioCaptureArtifactKind kind,
        string path,
        WaveFormat format,
        TimeProvider timeProvider) => new(
        kind,
        path,
        format,
        prebufferSeconds: 0,
        static stream => stream.Flush(flushToDisk: true),
        timeProvider);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Epoch = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(_timestamp);

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "isTranscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
