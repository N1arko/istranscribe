using System.Text;

namespace IsTranscribe.Transcription.Local.Protocol;

// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
internal static class WorkerProtocolSchema
{
    public static void Validate(WorkerEnvelope envelope)
    {
        if (envelope.ProtocolVersion != WorkerProtocol.CurrentVersion)
        {
            throw Failure(
                WorkerProtocolError.UnsupportedVersion,
                $"Unsupported worker protocol version {envelope.ProtocolVersion}.");
        }

        RequireCorrelation(envelope.CorrelationId);
        if (envelope.Sequence < 0)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, "sequence must be non-negative.");
        }

        ValidateIdentity(envelope);
        ValidatePayloadKind(envelope.Kind, envelope.Payload);
        ValidatePayload(envelope.Payload);
    }

    private static void ValidateIdentity(WorkerEnvelope envelope)
    {
        var requiresJob = envelope.Kind is WorkerMessageKind.Start
            or WorkerMessageKind.Progress
            or WorkerMessageKind.Result
            or WorkerMessageKind.Cancel;
        var requiresSession = envelope.Kind is WorkerMessageKind.Hello
            or WorkerMessageKind.Ready
            or WorkerMessageKind.Probe
            or WorkerMessageKind.Shutdown;

        if (requiresJob)
        {
            RequireJobId(envelope.JobId);
            if (envelope.ChunkIndex < 0)
            {
                throw Failure(WorkerProtocolError.InvalidSchema, "Chunk message requires a non-negative chunkIndex.");
            }

            return;
        }

        if (requiresSession)
        {
            if (envelope.JobId.Length != 0 || envelope.ChunkIndex != -1)
            {
                throw Failure(WorkerProtocolError.InvalidSchema, "Session message must use an empty jobId and chunkIndex -1.");
            }

            return;
        }

        if (envelope.Kind == WorkerMessageKind.Failure)
        {
            if (envelope.JobId.Length == 0)
            {
                if (envelope.ChunkIndex != -1)
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Session failure must use chunkIndex -1.");
                }
            }
            else
            {
                RequireJobId(envelope.JobId);
                if (envelope.ChunkIndex < 0)
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Job failure requires a non-negative chunkIndex.");
                }
            }
        }
    }

    private static void ValidatePayloadKind(WorkerMessageKind kind, IWorkerPayload payload)
    {
        var valid = (kind, payload) switch
        {
            (WorkerMessageKind.Hello, WorkerHelloPayload) => true,
            (WorkerMessageKind.Ready, WorkerReadyPayload) => true,
            (WorkerMessageKind.Probe, WorkerProbePayload) => true,
            (WorkerMessageKind.Start, WorkerStartPayload) => true,
            (WorkerMessageKind.Progress, WorkerProgressPayload) => true,
            (WorkerMessageKind.Result, WorkerResultPayload) => true,
            (WorkerMessageKind.Failure, WorkerFailurePayload) => true,
            (WorkerMessageKind.Cancel, WorkerCancelPayload) => true,
            (WorkerMessageKind.Shutdown, WorkerShutdownPayload) => true,
            _ => false,
        };
        if (!valid)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, "Message kind and payload type do not match.");
        }
    }

    private static void ValidatePayload(IWorkerPayload payload)
    {
        switch (payload)
        {
            case WorkerHelloPayload value:
                RequirePositive(value.ParentProcessId, nameof(value.ParentProcessId));
                RequireToken(value.ClientVersion, nameof(value.ClientVersion), 128);
                break;
            case WorkerReadyPayload value:
                if (value.State is not ("initialized" or "probe_completed"))
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Unknown ready state.");
                }

                RequireToken(value.WorkerVersion, nameof(value.WorkerVersion), 128);
                RequireOptionalToken(value.Backend, nameof(value.Backend), 64);
                RequireOptionalNonNegative(value.AvailableMemoryBytes, nameof(value.AvailableMemoryBytes));
                break;
            case WorkerProbePayload value:
                RequirePath(value.ModelPath, nameof(value.ModelPath));
                RequireSha256(value.ModelSha256, nameof(value.ModelSha256));
                RequireToken(value.RequestedBackend, nameof(value.RequestedBackend), 64);
                RequireThreadCount(value.MaximumThreads);
                break;
            case WorkerStartPayload value:
                RequirePath(value.InputPath, nameof(value.InputPath));
                RequireSha256(value.InputSha256, nameof(value.InputSha256));
                RequirePath(value.ModelPath, nameof(value.ModelPath));
                RequireSha256(value.ModelSha256, nameof(value.ModelSha256));
                if (string.IsNullOrWhiteSpace(value.Language) || value.Language.Length > 35
                    || value.Language.Any(character => !char.IsAsciiLetter(character) && character != '-'))
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Unsupported language.");
                }

                RequireToken(value.Backend, nameof(value.Backend), 64);
                if (value.StartMilliseconds < 0 || value.EndMilliseconds <= value.StartMilliseconds)
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Chunk time bounds are invalid.");
                }

                RequireThreadCount(value.MaximumThreads);
                break;
            case WorkerProgressPayload value:
                if (value.Stage is not ("preparing" or "decoding" or "inferencing" or "finalizing"))
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Unknown progress stage.");
                }

                if (value.TotalMilliseconds <= 0
                    || value.CompletedMilliseconds < 0
                    || value.CompletedMilliseconds > value.TotalMilliseconds
                    || value.WorkingSetBytes < 0
                    || value.CpuMilliseconds < 0)
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Progress values are outside their valid range.");
                }

                break;
            case WorkerResultPayload value:
                RequireToken(value.Language, nameof(value.Language), 16);
                RequireNonNegative(value.ProcessingMilliseconds, nameof(value.ProcessingMilliseconds));
                RequireToken(value.RuntimeVersion, nameof(value.RuntimeVersion), 128);
                RequireSha256(value.ModelSha256, nameof(value.ModelSha256));
                RequireToken(value.Backend, nameof(value.Backend), 64);
                if (value.Segments.Count > WorkerProtocol.MaximumSegmentsPerChunk)
                {
                    throw Failure(WorkerProtocolError.InvalidSchema, "Result contains too many segments.");
                }

                var transcriptBytes = 0;
                foreach (var segment in value.Segments)
                {
                    if (segment.StartMilliseconds < 0 || segment.EndMilliseconds < segment.StartMilliseconds)
                    {
                        throw Failure(WorkerProtocolError.InvalidSchema, "Segment time bounds are invalid.");
                    }

                    transcriptBytes = checked(transcriptBytes + Encoding.UTF8.GetByteCount(segment.Text));
                    if (transcriptBytes > WorkerProtocol.MaximumStringBytes)
                    {
                        throw Failure(WorkerProtocolError.InvalidSchema, "Transcript text exceeds the per-frame limit.");
                    }
                }

                break;
            case WorkerFailurePayload value:
                RequireToken(value.Category, nameof(value.Category), 64);
                RequireToken(value.StableCode, nameof(value.StableCode), 128);
                RequireBoundedText(value.SafeMessage, nameof(value.SafeMessage), 2048);
                RequireOptionalToken(value.Backend, nameof(value.Backend), 64);
                break;
            case WorkerCancelPayload value:
                RequireBoundedText(value.Reason, nameof(value.Reason), 256);
                break;
            case WorkerShutdownPayload value:
                RequireBoundedText(value.Reason, nameof(value.Reason), 256);
                break;
        }
    }

    private static void RequireCorrelation(string value)
    {
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out _))
        {
            throw Failure(WorkerProtocolError.InvalidSchema, "correlationId must be a 32-character GUID.");
        }
    }

    private static void RequireJobId(string value)
    {
        RequireToken(value, "jobId", 128);
    }

    private static void RequirePath(string value, string name)
    {
        RequireBoundedText(value, name, 4096);
        if (value.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} contains a null character.");
        }
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} must be a SHA-256 hex digest.");
        }
    }

    private static void RequireToken(string value, string name, int maximumLength)
    {
        RequireBoundedText(value, name, maximumLength);
        if (value.Any(static character => !(char.IsAsciiLetterOrDigit(character)
                                             || character is '.' or '_' or '-' or ':')))
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} contains an invalid token character.");
        }
    }

    private static void RequireOptionalToken(string? value, string name, int maximumLength)
    {
        if (value is not null)
        {
            RequireToken(value, name, maximumLength);
        }
    }

    private static void RequireBoundedText(string value, string name, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} is empty or exceeds its limit.");
        }
    }

    private static void RequireThreadCount(int value)
    {
        if (value is < 1 or > 256)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, "maximumThreads must be between 1 and 256.");
        }
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} must be positive.");
        }
    }

    private static void RequireNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} must be non-negative.");
        }
    }

    private static void RequireOptionalNonNegative(long? value, string name)
    {
        if (value is < 0)
        {
            throw Failure(WorkerProtocolError.InvalidSchema, $"{name} must be non-negative when present.");
        }
    }

    private static WorkerProtocolException Failure(WorkerProtocolError error, string message) =>
        new(error, message);
}
