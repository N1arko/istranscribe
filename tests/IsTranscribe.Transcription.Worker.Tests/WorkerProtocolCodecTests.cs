using System.Buffers.Binary;
using System.Text;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// Fail-closed wire-format coverage.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
public sealed class WorkerProtocolCodecTests
{
    [Fact]
    public void EveryMessageKindRoundTripsWithExactPayloadType()
    {
        var correlation = Guid.NewGuid().ToString("N");
        WorkerEnvelope[] messages =
        [
            Session(WorkerMessageKind.Hello, correlation, 0, new WorkerHelloPayload(42, "2.0.0")),
            Session(WorkerMessageKind.Ready, correlation, 1, new WorkerReadyPayload("initialized", "1.0.0", null, null)),
            Session(WorkerMessageKind.Probe, correlation, 2, WorkerTestData.Probe()),
            Job(WorkerMessageKind.Start, correlation, 3, WorkerTestData.Start()),
            Job(WorkerMessageKind.Progress, correlation, 4, WorkerTestData.Progress()),
            Job(WorkerMessageKind.Result, correlation, 5, WorkerTestData.Result()),
            Job(WorkerMessageKind.Failure, correlation, 6, new WorkerFailurePayload(
                "native_failure", "native_crash", "Local inference failed.", true, "cpu")),
            Job(WorkerMessageKind.Cancel, correlation, 7, new WorkerCancelPayload("user_cancelled")),
            Session(WorkerMessageKind.Shutdown, correlation, 8, new WorkerShutdownPayload("app_shutdown")),
        ];

        foreach (var message in messages)
        {
            var encoded = WorkerProtocolCodec.Serialize(message);
            var decoded = WorkerProtocolCodec.Deserialize(encoded);

            Assert.Equal(encoded, WorkerProtocolCodec.Serialize(decoded));
            Assert.Equal(message.Payload.GetType(), decoded.Payload.GetType());
        }
    }

    [Fact]
    public async Task FrameUsesLittleEndianLengthPrefix()
    {
        var stream = new MemoryStream();
        await using var connection = new WorkerFramedConnection(stream, stream, ownsStreams: false);
        var message = Session(
            WorkerMessageKind.Hello,
            Guid.NewGuid().ToString("N"),
            0,
            new WorkerHelloPayload(42, "2.0.0"));

        await connection.WriteAsync(message, CancellationToken.None);

        var bytes = stream.ToArray();
        var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.Equal(checked((uint)(bytes.Length - 4)), encodedLength);
        Assert.Equal((byte)'{', bytes[4]);
    }

    [Fact]
    public async Task OversizedPrefixFailsBeforePayloadAllocation()
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame,
            WorkerProtocol.MaximumFrameBytes + 1U);
        await using var connection = new WorkerFramedConnection(
            new MemoryStream(frame),
            Stream.Null,
            ownsStreams: true);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await connection.ReadAsync(CancellationToken.None));

        Assert.Equal(WorkerProtocolError.FrameTooLarge, exception.Error);
    }

    [Fact]
    public async Task PartialPrefixAndPartialPayloadAreUnexpectedEof()
    {
        var partialPrefix = new WorkerFramedConnection(
            new MemoryStream([0x02, 0x00]),
            Stream.Null,
            ownsStreams: true);
        await using (partialPrefix)
        {
            var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await partialPrefix.ReadAsync(CancellationToken.None));
            Assert.Equal(WorkerProtocolError.UnexpectedEndOfStream, exception.Error);
        }

        var partialPayloadBytes = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(partialPayloadBytes, 4);
        partialPayloadBytes[4] = (byte)'{';
        partialPayloadBytes[5] = (byte)'}';
        await using var partialPayload = new WorkerFramedConnection(
            new MemoryStream(partialPayloadBytes),
            Stream.Null,
            ownsStreams: true);
        var payloadException = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await partialPayload.ReadAsync(CancellationToken.None));
        Assert.Equal(WorkerProtocolError.UnexpectedEndOfStream, payloadException.Error);
    }

    [Fact]
    public void MalformedUtf8AndJsonFailClosed()
    {
        var utf8 = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Deserialize([0xc3, 0x28]));
        Assert.Equal(WorkerProtocolError.InvalidUtf8, utf8.Error);

        var json = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Deserialize("{"u8));
        Assert.Equal(WorkerProtocolError.InvalidJson, json.Error);
    }

    [Theory]
    [InlineData("\"unknown\":true,")]
    [InlineData("\"kind\":\"hello\",")]
    public void UnknownAndDuplicateEnvelopePropertiesAreRejected(string injectedProperty)
    {
        var json = $$"""
            {
              {{injectedProperty}}
              "protocolVersion": 1,
              "kind": "hello",
              "correlationId": "0123456789abcdef0123456789abcdef",
              "jobId": "",
              "chunkIndex": -1,
              "sequence": 0,
              "payload": { "parentProcessId": 42, "clientVersion": "1.0.0" }
            }
            """;

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(WorkerProtocolError.InvalidSchema, exception.Error);
    }

    [Fact]
    public void UnknownPayloadPropertyAndKindPayloadMismatchAreRejected()
    {
        const string unknownPayload = """
            {
              "protocolVersion": 1,
              "kind": "hello",
              "correlationId": "0123456789abcdef0123456789abcdef",
              "jobId": "",
              "chunkIndex": -1,
              "sequence": 0,
              "payload": { "parentProcessId": 42, "clientVersion": "1.0.0", "extra": true }
            }
            """;
        var schema = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(unknownPayload)));
        Assert.Equal(WorkerProtocolError.InvalidSchema, schema.Error);

        var mismatch = Session(
            WorkerMessageKind.Hello,
            Guid.NewGuid().ToString("N"),
            0,
            new WorkerShutdownPayload("app_shutdown"));
        var mismatchException = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Serialize(mismatch));
        Assert.Equal(WorkerProtocolError.InvalidSchema, mismatchException.Error);
    }

    [Fact]
    public void UnsupportedVersionAndInvalidIdentityAreRejected()
    {
        var version = new WorkerEnvelope(
            2,
            WorkerMessageKind.Hello,
            Guid.NewGuid().ToString("N"),
            string.Empty,
            -1,
            0,
            new WorkerHelloPayload(42, "1.0.0"));
        Assert.Equal(
            WorkerProtocolError.UnsupportedVersion,
            Assert.Throws<WorkerProtocolException>(() => WorkerProtocolCodec.Serialize(version)).Error);

        var jobWithPathIdentity = Job(
            WorkerMessageKind.Start,
            Guid.NewGuid().ToString("N"),
            0,
            WorkerTestData.Start()) with
        { JobId = "/private/recording.mp3" };
        Assert.Equal(
            WorkerProtocolError.InvalidSchema,
            Assert.Throws<WorkerProtocolException>(() => WorkerProtocolCodec.Serialize(jobWithPathIdentity)).Error);
    }

    [Fact]
    public void TranscriptSizeIsBoundedBeforeFrameWrite()
    {
        var result = WorkerTestData.Result(new string('x', WorkerProtocol.MaximumStringBytes + 1));
        var envelope = Job(
            WorkerMessageKind.Result,
            Guid.NewGuid().ToString("N"),
            0,
            result);

        var exception = Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocolCodec.Serialize(envelope));

        Assert.Equal(WorkerProtocolError.InvalidSchema, exception.Error);
    }

    private static WorkerEnvelope Session(
        WorkerMessageKind kind,
        string correlation,
        long sequence,
        IWorkerPayload payload) => new(
            WorkerProtocol.CurrentVersion,
            kind,
            correlation,
            string.Empty,
            -1,
            sequence,
            payload);

    private static WorkerEnvelope Job(
        WorkerMessageKind kind,
        string correlation,
        long sequence,
        IWorkerPayload payload) => new(
            WorkerProtocol.CurrentVersion,
            kind,
            correlation,
            "job-1",
            0,
            sequence,
            payload);
}
