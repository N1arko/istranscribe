using System.Buffers.Binary;
using System.Text;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Runtime;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// In-process child host coverage with a fake inference backend.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
[Collection(WorkerLifecycleCollection.Name)]
public sealed class LocalTranscriptionWorkerHostTests
{
    [Fact]
    public async Task HandshakeProgressAndResultFlowAcrossPrivatePipes()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var backend = new FakeBackend
        {
            Transcribe = (request, progress, _) =>
            {
                progress.Report(WorkerTestData.Progress());
                return ValueTask.FromResult(WorkerTestData.Result());
            },
        };
        var host = CreateHost(pipes, backend);
        var hostTask = host.RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);

        await pipes.Parent.WriteAsync(
            parent.CreateStart("job-1", 0, WorkerTestData.Start()),
            CancellationToken.None);
        var progressEnvelope = await ReadRequiredAsync(pipes.Parent);
        parent.AcceptIncoming(progressEnvelope);
        var resultEnvelope = await ReadRequiredAsync(pipes.Parent);
        parent.AcceptIncoming(resultEnvelope);

        Assert.Equal(WorkerTestData.Progress(), Assert.IsType<WorkerProgressPayload>(progressEnvelope.Payload));
        var result = Assert.IsType<WorkerResultPayload>(resultEnvelope.Payload);
        Assert.Equal("Привет, мир.", Assert.Single(result.Segments).Text);
        Assert.Equal(WorkerTestData.Start(), Assert.Single(backend.StartRequests));

        await pipes.Parent.WriteAsync(parent.CreateShutdown("test_complete"), CancellationToken.None);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NoisyNativeProgressIsCoalescedInBoundedChannel()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        const int updateCount = 10_000;
        var backend = new FakeBackend
        {
            Transcribe = (_, progress, _) =>
            {
                for (var index = 0; index < updateCount; index++)
                {
                    progress.Report(new WorkerProgressPayload(
                        "inferencing",
                        index,
                        updateCount,
                        WorkingSetBytes: 100_000_000,
                        CpuMilliseconds: index));
                }

                return ValueTask.FromResult(WorkerTestData.Result());
            },
        };
        var hostTask = CreateHost(pipes, backend).RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);
        await pipes.Parent.WriteAsync(
            parent.CreateStart("job-1", 0, WorkerTestData.Start()),
            CancellationToken.None);
        var updates = new List<WorkerProgressPayload>();

        while (true)
        {
            var envelope = await ReadRequiredAsync(pipes.Parent);
            parent.AcceptIncoming(envelope);
            if (envelope.Payload is WorkerProgressPayload update)
            {
                updates.Add(update);
                continue;
            }

            Assert.IsType<WorkerResultPayload>(envelope.Payload);
            break;
        }

        Assert.InRange(updates.Count, 1, 32);
        Assert.Equal(updateCount - 1, updates[^1].CompletedMilliseconds);
        await pipes.Parent.WriteAsync(parent.CreateShutdown("test_complete"), CancellationToken.None);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProbePublishesCapabilitiesWithoutStartingInference()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var backend = new FakeBackend
        {
            Probe = (request, _) => ValueTask.FromResult(
                new LocalWorkerProbeResult(request.RequestedBackend, 8_000_000_000)),
        };
        var hostTask = CreateHost(pipes, backend).RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);

        await pipes.Parent.WriteAsync(parent.CreateProbe(WorkerTestData.Probe()), CancellationToken.None);
        var readyEnvelope = await ReadRequiredAsync(pipes.Parent);
        parent.AcceptIncoming(readyEnvelope);

        var ready = Assert.IsType<WorkerReadyPayload>(readyEnvelope.Payload);
        Assert.Equal("probe_completed", ready.State);
        Assert.Equal("cpu", ready.Backend);
        Assert.Equal(8_000_000_000, ready.AvailableMemoryBytes);
        Assert.Empty(backend.StartRequests);

        await pipes.Parent.WriteAsync(parent.CreateShutdown("test_complete"), CancellationToken.None);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelInterruptsBackendAndReturnsStableFailure()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            Transcribe = async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        var hostTask = CreateHost(pipes, backend).RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);
        await pipes.Parent.WriteAsync(
            parent.CreateStart("job-1", 0, WorkerTestData.Start()),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await pipes.Parent.WriteAsync(parent.CreateCancel("user_cancelled"), CancellationToken.None);
        var failureEnvelope = await ReadRequiredAsync(pipes.Parent);
        parent.AcceptIncoming(failureEnvelope);

        var failure = Assert.IsType<WorkerFailurePayload>(failureEnvelope.Payload);
        Assert.Equal("cancelled", failure.Category);
        Assert.Equal("cancelled", failure.StableCode);

        await pipes.Parent.WriteAsync(parent.CreateShutdown("test_complete"), CancellationToken.None);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ImmediateCancelThenShutdownEndsHostWithinGraceWindow()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            Transcribe = async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        var hostTask = CreateHost(pipes, backend).RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);
        await pipes.Parent.WriteAsync(
            parent.CreateStart("job-1", 0, WorkerTestData.Start()),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await pipes.Parent.WriteAsync(parent.CreateCancel("user_cancelled"), CancellationToken.None);
        await pipes.Parent.WriteAsync(parent.CreateShutdown("app_shutdown"), CancellationToken.None);

        await hostTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task UnexpectedBackendCrashBecomesSafeFailureAndKeepsHostAlive()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var backend = new FakeBackend
        {
            Transcribe = (_, _, _) => throw new AccessViolationException("sensitive native detail"),
        };
        var hostTask = CreateHost(pipes, backend).RunAsync(CancellationToken.None);
        var parent = await HandshakeAsync(pipes.Parent);
        await pipes.Parent.WriteAsync(
            parent.CreateStart("job-1", 0, WorkerTestData.Start()),
            CancellationToken.None);

        var failureEnvelope = await ReadRequiredAsync(pipes.Parent);
        parent.AcceptIncoming(failureEnvelope);
        var failure = Assert.IsType<WorkerFailurePayload>(failureEnvelope.Payload);
        Assert.Equal("native_failure", failure.Category);
        Assert.Equal("unexpected_native_failure", failure.StableCode);
        Assert.DoesNotContain("sensitive", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);

        await pipes.Parent.WriteAsync(parent.CreateShutdown("test_complete"), CancellationToken.None);
        await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CommandPipeEofAndParentDeathBothEndHost()
    {
        await using (var eofPipes = ConnectedWorkerPipes.Create())
        {
            var hostTask = CreateHost(eofPipes, new FakeBackend()).RunAsync(CancellationToken.None);
            _ = await HandshakeAsync(eofPipes.Parent);

            await eofPipes.ParentCommandOutput.DisposeAsync();

            await hostTask.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await using var deathPipes = ConnectedWorkerPipes.Create();
        var parentMonitor = new ControllableParentMonitor();
        var deathHostTask = CreateHost(deathPipes, new FakeBackend(), parentMonitor)
            .RunAsync(CancellationToken.None);
        _ = await HandshakeAsync(deathPipes.Parent);

        parentMonitor.SignalExit();

        await deathHostTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OversizedFrameTerminatesConversationWithProtocolFailure()
    {
        await using var pipes = ConnectedWorkerPipes.Create();
        var hostTask = CreateHost(pipes, new FakeBackend()).RunAsync(CancellationToken.None);
        _ = await HandshakeAsync(pipes.Parent);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, WorkerProtocol.MaximumFrameBytes + 1U);

        await pipes.ParentCommandOutput.WriteAsync(prefix);
        await pipes.ParentCommandOutput.FlushAsync();

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await hostTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(WorkerProtocolError.FrameTooLarge, exception.Error);
    }

    [Fact]
    public void TranscriptTextAppearsOnlyInResultPayload()
    {
        const string transcript = "unique transcript payload 7f9d";
        var correlation = Guid.NewGuid().ToString("N");
        var start = new WorkerEnvelope(
            WorkerProtocol.CurrentVersion,
            WorkerMessageKind.Start,
            correlation,
            "job-1",
            0,
            0,
            WorkerTestData.Start());
        var progress = start with
        {
            Kind = WorkerMessageKind.Progress,
            Payload = WorkerTestData.Progress(),
        };
        var result = start with
        {
            Kind = WorkerMessageKind.Result,
            Payload = WorkerTestData.Result(transcript),
        };

        var startJson = Encoding.UTF8.GetString(WorkerProtocolCodec.Serialize(start));
        var progressJson = Encoding.UTF8.GetString(WorkerProtocolCodec.Serialize(progress));
        var resultJson = Encoding.UTF8.GetString(WorkerProtocolCodec.Serialize(result));

        Assert.DoesNotContain(transcript, startJson, StringComparison.Ordinal);
        Assert.DoesNotContain(transcript, progressJson, StringComparison.Ordinal);
        Assert.Contains(transcript, resultJson, StringComparison.Ordinal);
    }

    private static LocalTranscriptionWorkerHost CreateHost(
        ConnectedWorkerPipes pipes,
        FakeBackend backend,
        IParentProcessMonitor? parentMonitor = null) => new(
            pipes.Child,
            backend,
            parentMonitor ?? new NeverParentMonitor(),
            new LocalTranscriptionWorkerHostOptions("test-worker", TimeSpan.FromMilliseconds(100)));

    private static async Task<ParentWorkerProtocolSession> HandshakeAsync(
        WorkerFramedConnection parentConnection)
    {
        var parent = new ParentWorkerProtocolSession();
        await parentConnection.WriteAsync(
            parent.CreateHello(Environment.ProcessId, "test-parent"),
            CancellationToken.None);
        var ready = await ReadRequiredAsync(parentConnection);
        parent.AcceptIncoming(ready);
        return parent;
    }

    private static async Task<WorkerEnvelope> ReadRequiredAsync(WorkerFramedConnection connection) =>
        await connection.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2))
        ?? throw new InvalidOperationException("Unexpected EOF.");

    private sealed class NeverParentMonitor : IParentProcessMonitor
    {
        public Task WaitForExitAsync(int parentProcessId, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ControllableParentMonitor : IParentProcessMonitor
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExitAsync(int parentProcessId, CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void SignalExit() => _exit.TrySetResult();
    }

    private sealed class FakeBackend : ILocalInferenceBackend
    {
        public Func<WorkerProbePayload, CancellationToken, ValueTask<LocalWorkerProbeResult>> Probe { get; init; } =
            static (request, _) => ValueTask.FromResult(new LocalWorkerProbeResult(
                request.RequestedBackend,
                AvailableMemoryBytes: null));

        public Func<WorkerStartPayload, IProgress<WorkerProgressPayload>, CancellationToken, ValueTask<WorkerResultPayload>>
            Transcribe
        { get; init; } = static (_, _, _) => ValueTask.FromResult(WorkerTestData.Result());

        public List<WorkerStartPayload> StartRequests { get; } = [];

        public ValueTask<LocalWorkerProbeResult> ProbeAsync(
            WorkerProbePayload request,
            CancellationToken cancellationToken) => Probe(request, cancellationToken);

        public ValueTask<WorkerResultPayload> TranscribeAsync(
            WorkerStartPayload request,
            IProgress<WorkerProgressPayload> progress,
            CancellationToken cancellationToken)
        {
            StartRequests.Add(request);
            return Transcribe(request, progress, cancellationToken);
        }
    }
}
