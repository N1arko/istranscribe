using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// Conversation order and identity validation shared by parent and child.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
public sealed class WorkerProtocolSessionTests
{
    [Fact]
    public void HandshakeProbeAndJobMaintainIndependentMonotonicSequences()
    {
        var parent = new ParentWorkerProtocolSession();
        var child = new ChildWorkerProtocolSession();

        var hello = parent.CreateHello(42, "2.0.0");
        Assert.Equal(0, hello.Sequence);
        child.AcceptIncoming(hello);
        var initialReady = child.CreateReady(new WorkerReadyPayload(
            "initialized", "1.0.0", null, null));
        Assert.Equal(0, initialReady.Sequence);
        parent.AcceptIncoming(initialReady);

        var probe = parent.CreateProbe(WorkerTestData.Probe());
        Assert.Equal(1, probe.Sequence);
        child.AcceptIncoming(probe);
        var probeReady = child.CreateReady(new WorkerReadyPayload(
            "probe_completed", "1.0.0", "cpu", 4_000_000_000));
        Assert.Equal(1, probeReady.Sequence);
        parent.AcceptIncoming(probeReady);

        var start = parent.CreateStart("job-1", 3, WorkerTestData.Start());
        Assert.Equal(2, start.Sequence);
        child.AcceptIncoming(start);
        var progress = child.CreateProgress(WorkerTestData.Progress());
        Assert.Equal(2, progress.Sequence);
        parent.AcceptIncoming(progress);
        var result = child.CreateResult(WorkerTestData.Result());
        Assert.Equal(3, result.Sequence);
        parent.AcceptIncoming(result);

        Assert.Equal(ParentWorkerSessionState.Idle, parent.State);
        Assert.Equal(ChildWorkerSessionState.Idle, child.State);
    }

    [Fact]
    public void CancellationMustMatchActiveCorrelationJobAndChunk()
    {
        var (parent, child) = Handshake();
        child.AcceptIncoming(parent.CreateStart("job-1", 7, WorkerTestData.Start()));

        var cancel = parent.CreateCancel("user_cancelled");
        child.AcceptIncoming(cancel);
        var failure = child.CreateFailure(new WorkerFailurePayload(
            "cancelled", "cancelled", "Local transcription was cancelled.", true, "cpu"));
        parent.AcceptIncoming(failure);

        Assert.Equal(ParentWorkerSessionState.Idle, parent.State);
        Assert.Equal(ChildWorkerSessionState.Idle, child.State);
    }

    [Fact]
    public void OutOfOrderCommandFailsClosed()
    {
        var parent = new ParentWorkerProtocolSession();

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            parent.CreateStart("job-1", 0, WorkerTestData.Start()));

        Assert.Equal(WorkerProtocolError.InvalidOrder, exception.Error);
    }

    [Fact]
    public void DuplicateOrSkippedSequenceFailsClosed()
    {
        var parent = new ParentWorkerProtocolSession();
        var hello = parent.CreateHello(42, "2.0.0");
        var response = new WorkerEnvelope(
            WorkerProtocol.CurrentVersion,
            WorkerMessageKind.Ready,
            hello.CorrelationId,
            string.Empty,
            -1,
            1,
            new WorkerReadyPayload("initialized", "1.0.0", null, null));

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            parent.AcceptIncoming(response));

        Assert.Equal(WorkerProtocolError.InvalidSequence, exception.Error);
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("job")]
    [InlineData("chunk")]
    public void MismatchedActiveIdentityFailsClosed(string mismatch)
    {
        var (parent, child) = Handshake();
        var start = parent.CreateStart("job-1", 2, WorkerTestData.Start());
        child.AcceptIncoming(start);
        var progress = child.CreateProgress(WorkerTestData.Progress());
        progress = mismatch switch
        {
            "correlation" => progress with { CorrelationId = Guid.NewGuid().ToString("N") },
            "job" => progress with { JobId = "job-2" },
            "chunk" => progress with { ChunkIndex = 3 },
            _ => progress,
        };

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            parent.AcceptIncoming(progress));

        var expected = mismatch switch
        {
            "correlation" => WorkerProtocolError.CorrelationMismatch,
            "job" => WorkerProtocolError.JobMismatch,
            _ => WorkerProtocolError.ChunkMismatch,
        };
        Assert.Equal(expected, exception.Error);
    }

    [Fact]
    public void NewStartCannotReplaceActiveJob()
    {
        var (parent, child) = Handshake();
        child.AcceptIncoming(parent.CreateStart("job-1", 0, WorkerTestData.Start()));
        var rogueStart = new WorkerEnvelope(
            WorkerProtocol.CurrentVersion,
            WorkerMessageKind.Start,
            Guid.NewGuid().ToString("N"),
            "job-2",
            0,
            2,
            WorkerTestData.Start());

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            child.AcceptIncoming(rogueStart));

        Assert.Equal(WorkerProtocolError.InvalidOrder, exception.Error);
    }

    [Fact]
    public void ProgressCannotMoveBackwardOrChangeTotal()
    {
        var (parent, child) = Handshake();
        var start = parent.CreateStart("job-1", 0, WorkerTestData.Start());
        child.AcceptIncoming(start);
        var first = child.CreateProgress(WorkerTestData.Progress(30_000));
        parent.AcceptIncoming(first);
        var backward = first with
        {
            Sequence = first.Sequence + 1,
            Payload = WorkerTestData.Progress(20_000),
        };

        var backwardException = Assert.Throws<WorkerProtocolException>(() =>
            parent.AcceptIncoming(backward));
        Assert.Equal(WorkerProtocolError.InvalidOrder, backwardException.Error);

        var childException = Assert.Throws<WorkerProtocolException>(() =>
            child.CreateProgress(new WorkerProgressPayload(
                "inferencing",
                40_000,
                TotalMilliseconds: 61_000,
                WorkingSetBytes: 100_000_000,
                CpuMilliseconds: 2_500)));
        Assert.Equal(WorkerProtocolError.InvalidOrder, childException.Error);
    }

    private static (ParentWorkerProtocolSession Parent, ChildWorkerProtocolSession Child) Handshake()
    {
        var parent = new ParentWorkerProtocolSession();
        var child = new ChildWorkerProtocolSession();
        var hello = parent.CreateHello(42, "2.0.0");
        child.AcceptIncoming(hello);
        parent.AcceptIncoming(child.CreateReady(new WorkerReadyPayload(
            "initialized", "1.0.0", null, null)));
        return (parent, child);
    }
}
