using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace IsTranscribe.Transcription.Local.Protocol;

/// <summary>
/// Strict, bounded JSON codec for worker messages. Unknown, duplicate and missing
/// fields fail closed so protocol changes require an explicit version change.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
public static class WorkerProtocolCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    public static byte[] Serialize(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        WorkerProtocolSchema.Validate(envelope);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", envelope.ProtocolVersion);
            writer.WriteString("kind", ToWireName(envelope.Kind));
            writer.WriteString("correlationId", envelope.CorrelationId);
            writer.WriteString("jobId", envelope.JobId);
            writer.WriteNumber("chunkIndex", envelope.ChunkIndex);
            writer.WriteNumber("sequence", envelope.Sequence);
            writer.WritePropertyName("payload");
            WritePayload(writer, envelope.Payload);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > WorkerProtocol.MaximumFrameBytes)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.FrameTooLarge,
                $"Encoded frame exceeds {WorkerProtocol.MaximumFrameBytes} bytes.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static WorkerEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new WorkerProtocolException(WorkerProtocolError.EmptyFrame, "Frame payload is empty.");
        }

        if (utf8Json.Length > WorkerProtocol.MaximumFrameBytes)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.FrameTooLarge,
                $"Frame exceeds {WorkerProtocol.MaximumFrameBytes} bytes.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidUtf8,
                "Frame is not valid UTF-8.",
                exception);
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
            var root = StrictObject.Read(
                document.RootElement,
                "protocolVersion",
                "kind",
                "correlationId",
                "jobId",
                "chunkIndex",
                "sequence",
                "payload");

            var version = root.UInt32("protocolVersion");
            if (version != WorkerProtocol.CurrentVersion)
            {
                throw new WorkerProtocolException(
                    WorkerProtocolError.UnsupportedVersion,
                    $"Unsupported worker protocol version {version}.");
            }

            var kind = ParseWireName(root.String("kind"));
            var envelope = new WorkerEnvelope(
                version,
                kind,
                root.String("correlationId"),
                root.String("jobId"),
                root.Int32("chunkIndex"),
                root.Int64("sequence"),
                ReadPayload(kind, root.Element("payload")));
            WorkerProtocolSchema.Validate(envelope);
            return envelope;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidJson,
                "Frame contains invalid JSON.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidSchema,
                "Frame does not match the worker protocol schema.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidSchema,
                "Frame contains a number outside the supported range.",
                exception);
        }
    }

    private static void WritePayload(Utf8JsonWriter writer, IWorkerPayload payload)
    {
        writer.WriteStartObject();
        switch (payload)
        {
            case WorkerHelloPayload value:
                writer.WriteNumber("parentProcessId", value.ParentProcessId);
                writer.WriteString("clientVersion", value.ClientVersion);
                break;
            case WorkerReadyPayload value:
                writer.WriteString("state", value.State);
                writer.WriteString("workerVersion", value.WorkerVersion);
                WriteNullableString(writer, "backend", value.Backend);
                WriteNullableInt64(writer, "availableMemoryBytes", value.AvailableMemoryBytes);
                break;
            case WorkerProbePayload value:
                writer.WriteString("modelPath", value.ModelPath);
                writer.WriteString("modelSha256", value.ModelSha256);
                writer.WriteString("requestedBackend", value.RequestedBackend);
                writer.WriteNumber("maximumThreads", value.MaximumThreads);
                break;
            case WorkerStartPayload value:
                writer.WriteString("inputPath", value.InputPath);
                writer.WriteString("inputSha256", value.InputSha256);
                writer.WriteString("modelPath", value.ModelPath);
                writer.WriteString("modelSha256", value.ModelSha256);
                writer.WriteString("language", value.Language);
                writer.WriteString("backend", value.Backend);
                writer.WriteNumber("startMilliseconds", value.StartMilliseconds);
                writer.WriteNumber("endMilliseconds", value.EndMilliseconds);
                writer.WriteNumber("maximumThreads", value.MaximumThreads);
                break;
            case WorkerProgressPayload value:
                writer.WriteString("stage", value.Stage);
                writer.WriteNumber("completedMilliseconds", value.CompletedMilliseconds);
                writer.WriteNumber("totalMilliseconds", value.TotalMilliseconds);
                writer.WriteNumber("workingSetBytes", value.WorkingSetBytes);
                writer.WriteNumber("cpuMilliseconds", value.CpuMilliseconds);
                break;
            case WorkerResultPayload value:
                writer.WriteString("language", value.Language);
                writer.WriteNumber("processingMilliseconds", value.ProcessingMilliseconds);
                writer.WriteString("runtimeVersion", value.RuntimeVersion);
                writer.WriteString("modelSha256", value.ModelSha256);
                writer.WriteString("backend", value.Backend);
                writer.WritePropertyName("segments");
                writer.WriteStartArray();
                foreach (var segment in value.Segments)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("startMilliseconds", segment.StartMilliseconds);
                    writer.WriteNumber("endMilliseconds", segment.EndMilliseconds);
                    writer.WriteString("text", segment.Text);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case WorkerFailurePayload value:
                writer.WriteString("category", value.Category);
                writer.WriteString("stableCode", value.StableCode);
                writer.WriteString("safeMessage", value.SafeMessage);
                writer.WriteBoolean("retryable", value.Retryable);
                WriteNullableString(writer, "backend", value.Backend);
                break;
            case WorkerCancelPayload value:
                writer.WriteString("reason", value.Reason);
                break;
            case WorkerShutdownPayload value:
                writer.WriteString("reason", value.Reason);
                break;
            default:
                throw SchemaFailure($"Unsupported payload type {payload.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static IWorkerPayload ReadPayload(WorkerMessageKind kind, JsonElement payload)
    {
        return kind switch
        {
            WorkerMessageKind.Hello => ReadHello(payload),
            WorkerMessageKind.Ready => ReadReady(payload),
            WorkerMessageKind.Probe => ReadProbe(payload),
            WorkerMessageKind.Start => ReadStart(payload),
            WorkerMessageKind.Progress => ReadProgress(payload),
            WorkerMessageKind.Result => ReadResult(payload),
            WorkerMessageKind.Failure => ReadFailure(payload),
            WorkerMessageKind.Cancel => ReadCancel(payload),
            WorkerMessageKind.Shutdown => ReadShutdown(payload),
            _ => throw SchemaFailure("Unsupported message kind."),
        };
    }

    private static WorkerHelloPayload ReadHello(JsonElement payload)
    {
        var value = StrictObject.Read(payload, "parentProcessId", "clientVersion");
        return new WorkerHelloPayload(value.Int32("parentProcessId"), value.String("clientVersion"));
    }

    private static WorkerReadyPayload ReadReady(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "state",
            "workerVersion",
            "backend",
            "availableMemoryBytes");
        return new WorkerReadyPayload(
            value.String("state"),
            value.String("workerVersion"),
            value.NullableString("backend"),
            value.NullableInt64("availableMemoryBytes"));
    }

    private static WorkerProbePayload ReadProbe(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "modelPath",
            "modelSha256",
            "requestedBackend",
            "maximumThreads");
        return new WorkerProbePayload(
            value.String("modelPath"),
            value.String("modelSha256"),
            value.String("requestedBackend"),
            value.Int32("maximumThreads"));
    }

    private static WorkerStartPayload ReadStart(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "inputPath",
            "inputSha256",
            "modelPath",
            "modelSha256",
            "language",
            "backend",
            "startMilliseconds",
            "endMilliseconds",
            "maximumThreads");
        return new WorkerStartPayload(
            value.String("inputPath"),
            value.String("inputSha256"),
            value.String("modelPath"),
            value.String("modelSha256"),
            value.String("language"),
            value.String("backend"),
            value.Int64("startMilliseconds"),
            value.Int64("endMilliseconds"),
            value.Int32("maximumThreads"));
    }

    private static WorkerProgressPayload ReadProgress(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "stage",
            "completedMilliseconds",
            "totalMilliseconds",
            "workingSetBytes",
            "cpuMilliseconds");
        return new WorkerProgressPayload(
            value.String("stage"),
            value.Int64("completedMilliseconds"),
            value.Int64("totalMilliseconds"),
            value.Int64("workingSetBytes"),
            value.Int64("cpuMilliseconds"));
    }

    private static WorkerResultPayload ReadResult(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "language",
            "processingMilliseconds",
            "runtimeVersion",
            "modelSha256",
            "backend",
            "segments");
        var segmentsElement = value.Element("segments");
        if (segmentsElement.ValueKind != JsonValueKind.Array)
        {
            throw SchemaFailure("segments must be an array.");
        }

        var segments = new List<WorkerSegmentPayload>();
        foreach (var item in segmentsElement.EnumerateArray())
        {
            if (segments.Count == WorkerProtocol.MaximumSegmentsPerChunk)
            {
                throw SchemaFailure("segments exceeds the per-chunk limit.");
            }

            var segment = StrictObject.Read(item, "startMilliseconds", "endMilliseconds", "text");
            segments.Add(new WorkerSegmentPayload(
                segment.Int64("startMilliseconds"),
                segment.Int64("endMilliseconds"),
                segment.String("text")));
        }

        return new WorkerResultPayload(
            value.String("language"),
            value.Int64("processingMilliseconds"),
            value.String("runtimeVersion"),
            value.String("modelSha256"),
            value.String("backend"),
            segments);
    }

    private static WorkerFailurePayload ReadFailure(JsonElement payload)
    {
        var value = StrictObject.Read(
            payload,
            "category",
            "stableCode",
            "safeMessage",
            "retryable",
            "backend");
        return new WorkerFailurePayload(
            value.String("category"),
            value.String("stableCode"),
            value.String("safeMessage"),
            value.Boolean("retryable"),
            value.NullableString("backend"));
    }

    private static WorkerCancelPayload ReadCancel(JsonElement payload)
    {
        var value = StrictObject.Read(payload, "reason");
        return new WorkerCancelPayload(value.String("reason"));
    }

    private static WorkerShutdownPayload ReadShutdown(JsonElement payload)
    {
        var value = StrictObject.Read(payload, "reason");
        return new WorkerShutdownPayload(value.String("reason"));
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableInt64(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static string ToWireName(WorkerMessageKind kind) => kind switch
    {
        WorkerMessageKind.Hello => "hello",
        WorkerMessageKind.Ready => "ready",
        WorkerMessageKind.Probe => "probe",
        WorkerMessageKind.Start => "start",
        WorkerMessageKind.Progress => "progress",
        WorkerMessageKind.Result => "result",
        WorkerMessageKind.Failure => "failure",
        WorkerMessageKind.Cancel => "cancel",
        WorkerMessageKind.Shutdown => "shutdown",
        _ => throw SchemaFailure("Unsupported message kind."),
    };

    private static WorkerMessageKind ParseWireName(string value) => value switch
    {
        "hello" => WorkerMessageKind.Hello,
        "ready" => WorkerMessageKind.Ready,
        "probe" => WorkerMessageKind.Probe,
        "start" => WorkerMessageKind.Start,
        "progress" => WorkerMessageKind.Progress,
        "result" => WorkerMessageKind.Result,
        "failure" => WorkerMessageKind.Failure,
        "cancel" => WorkerMessageKind.Cancel,
        "shutdown" => WorkerMessageKind.Shutdown,
        _ => throw SchemaFailure($"Unknown message kind '{value}'."),
    };

    private static WorkerProtocolException SchemaFailure(string message) =>
        new(WorkerProtocolError.InvalidSchema, message);

    private sealed class StrictObject
    {
        private readonly Dictionary<string, JsonElement> _properties;

        private StrictObject(Dictionary<string, JsonElement> properties)
        {
            _properties = properties;
        }

        public static StrictObject Read(JsonElement element, params string[] expectedProperties)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw SchemaFailure("Expected a JSON object.");
            }

            var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
            var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!expected.Contains(property.Name))
                {
                    throw SchemaFailure($"Unknown property '{property.Name}'.");
                }

                if (!found.TryAdd(property.Name, property.Value))
                {
                    throw SchemaFailure($"Duplicate property '{property.Name}'.");
                }
            }

            if (found.Count != expected.Count)
            {
                var missing = expected.Where(name => !found.ContainsKey(name)).Order().First();
                throw SchemaFailure($"Missing property '{missing}'.");
            }

            return new StrictObject(found);
        }

        public JsonElement Element(string name) => _properties[name];

        public string String(string name)
        {
            var element = Element(name);
            if (element.ValueKind != JsonValueKind.String)
            {
                throw SchemaFailure($"Property '{name}' must be a string.");
            }

            return element.GetString()!;
        }

        public string? NullableString(string name)
        {
            var element = Element(name);
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                _ => throw SchemaFailure($"Property '{name}' must be a string or null."),
            };
        }

        public bool Boolean(string name)
        {
            var element = Element(name);
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw SchemaFailure($"Property '{name}' must be a boolean."),
            };
        }

        public int Int32(string name)
        {
            var element = Element(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            {
                throw SchemaFailure($"Property '{name}' must be a 32-bit integer.");
            }

            return value;
        }

        public uint UInt32(string name)
        {
            var element = Element(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out var value))
            {
                throw SchemaFailure($"Property '{name}' must be an unsigned 32-bit integer.");
            }

            return value;
        }

        public long Int64(string name)
        {
            var element = Element(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            {
                throw SchemaFailure($"Property '{name}' must be a 64-bit integer.");
            }

            return value;
        }

        public long? NullableInt64(string name)
        {
            var element = Element(name);
            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            {
                throw SchemaFailure($"Property '{name}' must be a 64-bit integer or null.");
            }

            return value;
        }
    }
}

/// <summary>
/// Length-prefixed transport with a little-endian uint32 prefix and one reader / one writer.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class WorkerFramedConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public WorkerFramedConnection(Stream input, Stream output, bool ownsStreams = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        _input = input;
        _output = output;
        _ownsStreams = ownsStreams;
    }

    public async ValueTask<WorkerEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var prefix = new byte[WorkerProtocol.FramePrefixBytes];
        var first = await _input.ReadAsync(prefix.AsMemory(0, 1), cancellationToken);
        if (first == 0)
        {
            return null;
        }

        await ReadRemainderAsync(prefix.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0)
        {
            throw new WorkerProtocolException(WorkerProtocolError.EmptyFrame, "Frame length is zero.");
        }

        if (length > WorkerProtocol.MaximumFrameBytes)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.FrameTooLarge,
                $"Frame length {length} exceeds {WorkerProtocol.MaximumFrameBytes} bytes.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await ReadRemainderAsync(payload, cancellationToken);
        return WorkerProtocolCodec.Deserialize(payload);
    }

    public async ValueTask WriteAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = WorkerProtocolCodec.Serialize(envelope);
        var prefix = new byte[WorkerProtocol.FramePrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(prefix, cancellationToken);
            await _output.WriteAsync(payload, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsStreams)
        {
            await _input.DisposeAsync();
            if (!ReferenceEquals(_input, _output))
            {
                await _output.DisposeAsync();
            }
        }

        _writeLock.Dispose();
    }

    private async ValueTask ReadRemainderAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _input.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0)
            {
                throw new WorkerProtocolException(
                    WorkerProtocolError.UnexpectedEndOfStream,
                    "Stream ended in the middle of a frame.");
            }

            offset += count;
        }
    }
}
