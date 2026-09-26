using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Durable worker regressions for timeout splitting and immutable input identity.
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
/// </summary>
public sealed class TranscriptionQueueWorkerSafetyRegressionTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RepeatedTransportTimeoutSplitsOnlyTheRejectedChunkThenCompletesChildren()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromMinutes(4), sampleRate: 100);
        string? parentChunkId = null;
        var callsByChunk = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var engine = new SyntheticRemoteEngine((request, _) =>
        {
            var chunkId = Assert.IsType<string>(request.ChunkId);
            parentChunkId ??= chunkId;
            var attempt = callsByChunk.AddOrUpdate(chunkId, 1, static (_, count) => count + 1);
            if (string.Equals(chunkId, parentChunkId, StringComparison.Ordinal) && attempt <= 2)
            {
                return ValueTask.FromResult(TranscriptionResult.Failed(new TranscriptionError(
                    TranscriptionErrorCategory.Network,
                    "transport_timeout",
                    "Synthetic provider transport timeout.",
                    suggestedDelay: TimeSpan.Zero,
                    requestId: $"timeout-{attempt}",
                    disposition: TranscriptionFailureDisposition.TryAgain)));
            }

            return ValueTask.FromResult(Completed(
                request,
                $"Completed split child {chunkId[..8]}.",
                $"request-{chunkId[..8]}"));
        });
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        await fixture.WaitForStatusAsync(job.JobId, TranscriptionJobStatus.Completed);

        var chunks = await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None);
        var parent = Assert.Single(chunks, static chunk => chunk.Status == TranscriptionChunkStatus.Split);
        var children = chunks
            .Where(static chunk => chunk.Status == TranscriptionChunkStatus.Completed)
            .OrderBy(static chunk => chunk.SequenceIndex)
            .ToArray();
        Assert.Equal(parentChunkId, parent.Id);
        Assert.Equal(2, parent.AttemptCount);
        Assert.Equal(2, callsByChunk[parent.Id]);
        Assert.Equal(2, children.Length);
        Assert.All(children, child => Assert.Equal(parent.Id, child.ParentChunkId));
        Assert.All(children, child => Assert.Equal(1, callsByChunk[child.Id]));
        Assert.Equal(4, engine.Requests.Count);
    }

    [Fact]
    public async Task InputHashMutationRequiresAttentionBeforeAnyEngineRequest()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        var job = await fixture.AddRemoteJobAsync(TimeSpan.FromSeconds(2), sampleRate: 1_000);
        await using (var stream = new FileStream(
                         job.AudioPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.Read))
        {
            stream.Position = 44;
            stream.WriteByte(0x7f);
            await stream.FlushAsync();
        }

        var engine = new SyntheticRemoteEngine((request, _) => ValueTask.FromResult(
            Completed(request, "This response must never be produced.", "unexpected-request")));
        await using var worker = fixture.CreateWorker(engine);

        await worker.StartAsync(CancellationToken.None);
        var attention = await fixture.WaitForStatusAsync(
            job.JobId,
            TranscriptionJobStatus.AttentionRequired);

        Assert.Equal("input_changed", attention.StableErrorCode);
        Assert.Empty(engine.Requests);
        Assert.Empty(await fixture.Repository.ListChunksAsync(job.JobId, CancellationToken.None));
        Assert.NotEqual(job.InputSha256, await Sha256Async(job.AudioPath));
    }

    private static TranscriptionResult Completed(
        TranscriptionRequest request,
        string text,
        string requestId)
    {
        var start = request.SourceStart ?? TimeSpan.Zero;
        var end = request.SourceEnd ?? start.Add(TimeSpan.FromSeconds(1));
        var duration = end - start;
        return TranscriptionResult.Completed(
            text,
            "en",
            [new TranscriptionSegment(text, TimeSpan.Zero, duration)],
            new TranscriptionResultMetadata(
                ResolvedModelId: request.ModelId,
                RequestId: requestId,
                Usage: new TranscriptionUsage(AudioSeconds: duration.TotalSeconds),
                SourceStart: start,
                SourceEnd: end,
                AudioDuration: duration));
    }

    private sealed class SyntheticRemoteEngine(
        Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> handler)
        : ITranscriptionEngine
    {
        private readonly Func<TranscriptionRequest, CancellationToken, ValueTask<TranscriptionResult>> _handler =
            handler;
        private readonly ConcurrentQueue<TranscriptionRequest> _requests = new();

        public TranscriptionEngineCapabilities Capabilities { get; } = new(
            TranscriptionEngineIds.Groq,
            "Synthetic Groq",
            TranscriptionExecutionKind.Remote,
            RequiresNetwork: true,
            "Synthetic in-memory engine; no data leaves the test process.",
            SupportedTestPlatforms,
            [new TranscriptionModelCapability("synthetic-model", "Synthetic model", IsRecommended: true)],
            ["en"],
            SupportsAutomaticLanguageDetection: true,
            SupportsDiarization: false,
            TranscriptionTimestampCapabilities.Segment);

        public IReadOnlyList<TranscriptionRequest> Requests => _requests.ToArray();

        public ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            request.Validate();
            _requests.Enqueue(request);
            progress?.Report(new TranscriptionProgress(TranscriptionProgressStage.Uploading, 0.5));
            return _handler(request, cancellationToken);
        }
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly FixedTimeProvider _timeProvider = new(Baseline);

        private WorkerFixture(
            string root,
            LocalAppPaths paths,
            SqliteConnection connection,
            ApplicationSettings settings)
        {
            _root = root;
            Paths = paths;
            _connection = connection;
            Settings = settings;
            Repository = new TranscriptionJobRepository(paths);
            WorkerRoot = Path.Combine(paths.TempDirectory, "transcription-worker-safety");
        }

        public LocalAppPaths Paths { get; }

        public ApplicationSettings Settings { get; }

        public TranscriptionJobRepository Repository { get; }

        public string WorkerRoot { get; }

        public static async ValueTask<WorkerFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"istranscribe-worker-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var settings = ApplicationSettings.Default with
            {
                Storage = ApplicationSettings.Default.Storage with
                {
                    RecordingsFolder = Path.Combine(root, "recordings"),
                    FailedTempFolder = paths.TempDirectory
                }
            };
            return new WorkerFixture(root, paths, connection, settings);
        }

        public async ValueTask<SyntheticJob> AddRemoteJobAsync(TimeSpan duration, int sampleRate)
        {
            var sessionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var fixtureDirectory = Path.Combine(_root, "fixtures");
            Directory.CreateDirectory(fixtureDirectory);
            var audioPath = Path.Combine(fixtureDirectory, $"{sessionId:N}.wav");
            await WriteWaveAsync(audioPath, duration, sampleRate);
            var inputSha256 = await Sha256Async(audioPath);
            var session = MeetingSessionRecord.Create(
                sessionId,
                Baseline,
                "manual",
                "mixed",
                "synthetic-output",
                "synthetic-microphone") with
            {
                Status = "ready",
                SourceApp = "Synthetic worker safety fixture",
                EndedAtUtc = Baseline.Add(duration),
                PrimaryAudioPath = audioPath,
                DurationSeconds = duration.TotalSeconds,
                UpdatedAtUtc = Baseline.Add(duration)
            };
            await new MeetingSessionRepository(_connection)
                .UpsertAsync(session, CancellationToken.None);
            var enqueue = await Repository.EnqueueAsync(
                new TranscriptionJobEnqueueRequest(
                    jobId.ToString("N"),
                    sessionId.ToString("N"),
                    TranscriptionEngineIds.Groq,
                    TranscriptionExecutionKind.Remote,
                    "synthetic-model",
                    audioPath,
                    inputSha256,
                    new FileInfo(audioPath).Length,
                    Baseline,
                    duration.TotalSeconds,
                    EngineOptionsJson: null,
                    RequestedLanguage: "en",
                    RemoteConsentRevision: "synthetic-disclosure-v1",
                    RemoteConsentAtUtc: Baseline.AddMinutes(-1),
                    PrivacyPolicyJson: "{\"synthetic\":true}"),
                CancellationToken.None);
            Assert.True(enqueue.Created);
            return new SyntheticJob(
                jobId.ToString("N"),
                audioPath,
                inputSha256);
        }

        public TranscriptionQueueWorker CreateWorker(ITranscriptionEngine engine) => new(
            Repository,
            new TranscriptionSessionContextReader(Paths),
            new TranscriptionEngineRegistry([engine]),
            new AudioChunkPlanner(),
            new ManagedAudioChunkMaterializer(),
            new TranscriptionChunkResultStore(),
            new TranscriptMerger(),
            new TranscriptArtifactMaterializer(new ArtifactPathResolver(Paths)),
            () => Settings,
            WorkerRoot,
            _timeProvider,
            TimeSpan.FromMilliseconds(10));

        public async ValueTask<TranscriptionJobRecord> WaitForStatusAsync(
            string jobId,
            TranscriptionJobStatus expectedStatus)
        {
            var timeout = Stopwatch.StartNew();
            TranscriptionJobRecord? last = null;
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                last = await Repository.GetAsync(jobId, CancellationToken.None);
                if (last?.Status == expectedStatus)
                {
                    return last;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException(
                $"Job '{jobId}' did not reach {expectedStatus}; last status was {last?.Status}.");
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SyntheticJob(
        string JobId,
        string AudioPath,
        string InputSha256);

    private static IReadOnlyList<TranscriptionPlatformTarget> SupportedTestPlatforms { get; } =
    [
        new(TranscriptionOperatingSystem.Windows, Architecture.X64),
        new(TranscriptionOperatingSystem.Windows, Architecture.Arm64),
        new(TranscriptionOperatingSystem.MacOS, Architecture.X64),
        new(TranscriptionOperatingSystem.MacOS, Architecture.Arm64),
        new(TranscriptionOperatingSystem.Linux, Architecture.X64),
        new(TranscriptionOperatingSystem.Linux, Architecture.Arm64)
    ];

    private static async ValueTask WriteWaveAsync(
        string path,
        TimeSpan duration,
        int sampleRate)
    {
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channelCount * (bitsPerSample / 8);
        var sampleCount = checked((int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        var dataSize = checked(sampleCount * blockAlign);
        var payload = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)(36 + dataSize)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(payload, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), checked((ushort)channelCount));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(28, 4),
            checked((uint)(sampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(32, 2), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(34, 2), checked((ushort)bitsPerSample));
        Encoding.ASCII.GetBytes("data").CopyTo(payload, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40, 4), checked((uint)dataSize));
        await File.WriteAllBytesAsync(path, payload);
    }

    private static async ValueTask<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
