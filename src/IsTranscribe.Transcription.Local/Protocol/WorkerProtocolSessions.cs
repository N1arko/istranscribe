namespace IsTranscribe.Transcription.Local.Protocol;

public enum ParentWorkerSessionState
{
    Created,
    AwaitingInitialReady,
    Idle,
    AwaitingProbe,
    Running,
    CancelRequested,
    ShutdownSent,
}

/// <summary>
/// Parent-side order, sequence and identity guard for one worker lifetime.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class ParentWorkerProtocolSession
{
    private long _nextOutgoingSequence;
    private long _nextIncomingSequence;
    private string? _pendingCorrelationId;
    private string? _activeJobId;
    private int _activeChunkIndex = -1;
    private long _lastProgressMilliseconds = -1;
    private long _progressTotalMilliseconds = -1;

    public ParentWorkerSessionState State { get; private set; } = ParentWorkerSessionState.Created;

    public bool HasActiveJob => State is ParentWorkerSessionState.Running
        or ParentWorkerSessionState.CancelRequested;

    public WorkerEnvelope CreateHello(int parentProcessId, string clientVersion)
    {
        RequireState(ParentWorkerSessionState.Created);
        _pendingCorrelationId = NewCorrelationId();
        State = ParentWorkerSessionState.AwaitingInitialReady;
        return CreateSessionEnvelope(
            WorkerMessageKind.Hello,
            _pendingCorrelationId,
            new WorkerHelloPayload(parentProcessId, clientVersion));
    }

    public WorkerEnvelope CreateProbe(WorkerProbePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireState(ParentWorkerSessionState.Idle);
        _pendingCorrelationId = NewCorrelationId();
        State = ParentWorkerSessionState.AwaitingProbe;
        return CreateSessionEnvelope(WorkerMessageKind.Probe, _pendingCorrelationId, payload);
    }

    public WorkerEnvelope CreateStart(string jobId, int chunkIndex, WorkerStartPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireState(ParentWorkerSessionState.Idle);
        _pendingCorrelationId = NewCorrelationId();
        _activeJobId = jobId;
        _activeChunkIndex = chunkIndex;
        _lastProgressMilliseconds = -1;
        _progressTotalMilliseconds = -1;
        State = ParentWorkerSessionState.Running;
        return CreateJobEnvelope(WorkerMessageKind.Start, _pendingCorrelationId, jobId, chunkIndex, payload);
    }

    public WorkerEnvelope CreateCancel(string reason)
    {
        RequireState(ParentWorkerSessionState.Running);
        State = ParentWorkerSessionState.CancelRequested;
        return CreateJobEnvelope(
            WorkerMessageKind.Cancel,
            _pendingCorrelationId!,
            _activeJobId!,
            _activeChunkIndex,
            new WorkerCancelPayload(reason));
    }

    public WorkerEnvelope CreateShutdown(string reason)
    {
        if (State is ParentWorkerSessionState.Created or ParentWorkerSessionState.ShutdownSent)
        {
            throw InvalidOrder($"shutdown is invalid while parent session is {State}.");
        }

        State = ParentWorkerSessionState.ShutdownSent;
        return CreateSessionEnvelope(
            WorkerMessageKind.Shutdown,
            NewCorrelationId(),
            new WorkerShutdownPayload(reason));
    }

    public void AcceptIncoming(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        WorkerProtocolSchema.Validate(envelope);
        ValidateIncomingSequence(envelope);

        switch (State)
        {
            case ParentWorkerSessionState.AwaitingInitialReady:
                RequireKind(envelope, WorkerMessageKind.Ready);
                RequireCorrelation(envelope, _pendingCorrelationId!);
                if (envelope.Payload is not WorkerReadyPayload { State: "initialized" })
                {
                    throw InvalidOrder("Initial ready message must use state 'initialized'.");
                }

                State = ParentWorkerSessionState.Idle;
                break;
            case ParentWorkerSessionState.AwaitingProbe:
                if (envelope.Kind == WorkerMessageKind.Ready)
                {
                    RequireCorrelation(envelope, _pendingCorrelationId!);
                    if (envelope.Payload is not WorkerReadyPayload { State: "probe_completed" })
                    {
                        throw InvalidOrder("Probe ready message must use state 'probe_completed'.");
                    }

                    State = ParentWorkerSessionState.Idle;
                }
                else if (envelope.Kind == WorkerMessageKind.Failure)
                {
                    RequireCorrelation(envelope, _pendingCorrelationId!);
                    RequireSessionIdentity(envelope);
                    State = ParentWorkerSessionState.Idle;
                }
                else
                {
                    throw InvalidOrder($"{envelope.Kind} is invalid while awaiting probe response.");
                }

                break;
            case ParentWorkerSessionState.Running:
            case ParentWorkerSessionState.CancelRequested:
                AcceptJobEvent(envelope);
                break;
            default:
                throw InvalidOrder($"{envelope.Kind} is invalid while parent session is {State}.");
        }

        _nextIncomingSequence++;
    }

    private void AcceptJobEvent(WorkerEnvelope envelope)
    {
        if (envelope.Kind is not (WorkerMessageKind.Progress
            or WorkerMessageKind.Result
            or WorkerMessageKind.Failure))
        {
            throw InvalidOrder($"{envelope.Kind} is not a valid job event.");
        }

        RequireCorrelation(envelope, _pendingCorrelationId!);
        RequireJobIdentity(envelope, _activeJobId!, _activeChunkIndex);
        if (envelope.Payload is WorkerProgressPayload progress)
        {
            ValidateProgress(progress);
        }

        if (envelope.Kind is WorkerMessageKind.Result or WorkerMessageKind.Failure)
        {
            State = ParentWorkerSessionState.Idle;
            _activeJobId = null;
            _activeChunkIndex = -1;
            _lastProgressMilliseconds = -1;
            _progressTotalMilliseconds = -1;
        }
    }

    private void ValidateProgress(WorkerProgressPayload progress)
    {
        if (_progressTotalMilliseconds >= 0 && progress.TotalMilliseconds != _progressTotalMilliseconds)
        {
            throw InvalidOrder("Job progress total changed during an active chunk.");
        }

        if (progress.CompletedMilliseconds < _lastProgressMilliseconds)
        {
            throw InvalidOrder("Job progress moved backwards.");
        }

        _progressTotalMilliseconds = progress.TotalMilliseconds;
        _lastProgressMilliseconds = progress.CompletedMilliseconds;
    }

    private WorkerEnvelope CreateSessionEnvelope(
        WorkerMessageKind kind,
        string correlationId,
        IWorkerPayload payload) => new(
            WorkerProtocol.CurrentVersion,
            kind,
            correlationId,
            string.Empty,
            -1,
            _nextOutgoingSequence++,
            payload);

    private WorkerEnvelope CreateJobEnvelope(
        WorkerMessageKind kind,
        string correlationId,
        string jobId,
        int chunkIndex,
        IWorkerPayload payload) => new(
            WorkerProtocol.CurrentVersion,
            kind,
            correlationId,
            jobId,
            chunkIndex,
            _nextOutgoingSequence++,
            payload);

    private void ValidateIncomingSequence(WorkerEnvelope envelope)
    {
        if (envelope.Sequence != _nextIncomingSequence)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidSequence,
                $"Expected incoming sequence {_nextIncomingSequence}, received {envelope.Sequence}.");
        }
    }

    private void RequireState(ParentWorkerSessionState expected)
    {
        if (State != expected)
        {
            throw InvalidOrder($"Operation requires parent session state {expected}; current state is {State}.");
        }
    }

    internal static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    internal static void RequireKind(WorkerEnvelope envelope, WorkerMessageKind kind)
    {
        if (envelope.Kind != kind)
        {
            throw InvalidOrder($"Expected {kind}, received {envelope.Kind}.");
        }
    }

    internal static void RequireCorrelation(WorkerEnvelope envelope, string correlationId)
    {
        if (!string.Equals(envelope.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.CorrelationMismatch,
                "Message correlation does not match the active command.");
        }
    }

    internal static void RequireSessionIdentity(WorkerEnvelope envelope)
    {
        if (envelope.JobId.Length != 0 || envelope.ChunkIndex != -1)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.JobMismatch,
                "Expected a session-scoped message.");
        }
    }

    internal static void RequireJobIdentity(WorkerEnvelope envelope, string jobId, int chunkIndex)
    {
        if (!string.Equals(envelope.JobId, jobId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.JobMismatch,
                "Message job does not match the active job.");
        }

        if (envelope.ChunkIndex != chunkIndex)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.ChunkMismatch,
                "Message chunk does not match the active chunk.");
        }
    }

    internal static WorkerProtocolException InvalidOrder(string message) =>
        new(WorkerProtocolError.InvalidOrder, message);
}

public enum ChildWorkerSessionState
{
    AwaitingHello,
    AwaitingInitialReady,
    Idle,
    AwaitingProbeResponse,
    Running,
    CancelRequested,
    ShutdownReceived,
}

/// <summary>
/// Child-side mirror of the private protocol conversation.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public sealed class ChildWorkerProtocolSession
{
    private long _nextIncomingSequence;
    private long _nextOutgoingSequence;
    private string? _pendingCorrelationId;
    private string? _activeJobId;
    private int _activeChunkIndex = -1;
    private long _lastProgressMilliseconds = -1;
    private long _progressTotalMilliseconds = -1;

    public ChildWorkerSessionState State { get; private set; } = ChildWorkerSessionState.AwaitingHello;

    public WorkerHelloPayload? Hello { get; private set; }

    public void AcceptIncoming(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        WorkerProtocolSchema.Validate(envelope);
        if (envelope.Sequence != _nextIncomingSequence)
        {
            throw new WorkerProtocolException(
                WorkerProtocolError.InvalidSequence,
                $"Expected incoming sequence {_nextIncomingSequence}, received {envelope.Sequence}.");
        }

        switch (State)
        {
            case ChildWorkerSessionState.AwaitingHello:
                ParentWorkerProtocolSession.RequireKind(envelope, WorkerMessageKind.Hello);
                ParentWorkerProtocolSession.RequireSessionIdentity(envelope);
                Hello = (WorkerHelloPayload)envelope.Payload;
                _pendingCorrelationId = envelope.CorrelationId;
                State = ChildWorkerSessionState.AwaitingInitialReady;
                break;
            case ChildWorkerSessionState.Idle:
                AcceptIdleCommand(envelope);
                break;
            case ChildWorkerSessionState.Running:
                AcceptRunningCommand(envelope);
                break;
            case ChildWorkerSessionState.CancelRequested:
                if (envelope.Kind != WorkerMessageKind.Shutdown)
                {
                    throw ParentWorkerProtocolSession.InvalidOrder(
                        $"{envelope.Kind} is invalid after cancellation was requested.");
                }

                ParentWorkerProtocolSession.RequireSessionIdentity(envelope);
                State = ChildWorkerSessionState.ShutdownReceived;
                break;
            default:
                throw ParentWorkerProtocolSession.InvalidOrder(
                    $"{envelope.Kind} is invalid while child session is {State}.");
        }

        _nextIncomingSequence++;
    }

    public WorkerEnvelope CreateReady(WorkerReadyPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (State == ChildWorkerSessionState.AwaitingInitialReady && payload.State == "initialized")
        {
            State = ChildWorkerSessionState.Idle;
        }
        else if (State == ChildWorkerSessionState.AwaitingProbeResponse && payload.State == "probe_completed")
        {
            State = ChildWorkerSessionState.Idle;
        }
        else
        {
            throw ParentWorkerProtocolSession.InvalidOrder(
                $"Ready state '{payload.State}' is invalid while child session is {State}.");
        }

        return CreateSessionEvent(WorkerMessageKind.Ready, payload);
    }

    public WorkerEnvelope CreateProgress(WorkerProgressPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireActiveState();
        ValidateProgress(payload);
        return CreateJobEvent(WorkerMessageKind.Progress, payload);
    }

    public WorkerEnvelope CreateResult(WorkerResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireActiveState();
        var envelope = CreateJobEvent(WorkerMessageKind.Result, payload);
        CompleteJob();
        return envelope;
    }

    public WorkerEnvelope CreateFailure(WorkerFailurePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (State == ChildWorkerSessionState.AwaitingProbeResponse)
        {
            State = ChildWorkerSessionState.Idle;
            return CreateSessionEvent(WorkerMessageKind.Failure, payload);
        }

        RequireActiveState();
        var envelope = CreateJobEvent(WorkerMessageKind.Failure, payload);
        CompleteJob();
        return envelope;
    }

    private void AcceptIdleCommand(WorkerEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case WorkerMessageKind.Probe:
                ParentWorkerProtocolSession.RequireSessionIdentity(envelope);
                _pendingCorrelationId = envelope.CorrelationId;
                State = ChildWorkerSessionState.AwaitingProbeResponse;
                break;
            case WorkerMessageKind.Start:
                _pendingCorrelationId = envelope.CorrelationId;
                _activeJobId = envelope.JobId;
                _activeChunkIndex = envelope.ChunkIndex;
                _lastProgressMilliseconds = -1;
                _progressTotalMilliseconds = -1;
                State = ChildWorkerSessionState.Running;
                break;
            case WorkerMessageKind.Shutdown:
                ParentWorkerProtocolSession.RequireSessionIdentity(envelope);
                State = ChildWorkerSessionState.ShutdownReceived;
                break;
            default:
                throw ParentWorkerProtocolSession.InvalidOrder(
                    $"{envelope.Kind} is invalid while child session is idle.");
        }
    }

    private void AcceptRunningCommand(WorkerEnvelope envelope)
    {
        if (envelope.Kind == WorkerMessageKind.Cancel)
        {
            ParentWorkerProtocolSession.RequireCorrelation(envelope, _pendingCorrelationId!);
            ParentWorkerProtocolSession.RequireJobIdentity(envelope, _activeJobId!, _activeChunkIndex);
            State = ChildWorkerSessionState.CancelRequested;
            return;
        }

        if (envelope.Kind == WorkerMessageKind.Shutdown)
        {
            ParentWorkerProtocolSession.RequireSessionIdentity(envelope);
            State = ChildWorkerSessionState.ShutdownReceived;
            return;
        }

        throw ParentWorkerProtocolSession.InvalidOrder(
            $"{envelope.Kind} is invalid while a job is running.");
    }

    private WorkerEnvelope CreateSessionEvent(WorkerMessageKind kind, IWorkerPayload payload) => new(
        WorkerProtocol.CurrentVersion,
        kind,
        _pendingCorrelationId!,
        string.Empty,
        -1,
        _nextOutgoingSequence++,
        payload);

    private WorkerEnvelope CreateJobEvent(WorkerMessageKind kind, IWorkerPayload payload) => new(
        WorkerProtocol.CurrentVersion,
        kind,
        _pendingCorrelationId!,
        _activeJobId!,
        _activeChunkIndex,
        _nextOutgoingSequence++,
        payload);

    private void RequireActiveState()
    {
        if (State is not (ChildWorkerSessionState.Running or ChildWorkerSessionState.CancelRequested))
        {
            throw ParentWorkerProtocolSession.InvalidOrder(
                $"Job event is invalid while child session is {State}.");
        }
    }

    private void CompleteJob()
    {
        State = ChildWorkerSessionState.Idle;
        _activeJobId = null;
        _activeChunkIndex = -1;
        _lastProgressMilliseconds = -1;
        _progressTotalMilliseconds = -1;
    }

    private void ValidateProgress(WorkerProgressPayload progress)
    {
        if (_progressTotalMilliseconds >= 0 && progress.TotalMilliseconds != _progressTotalMilliseconds)
        {
            throw ParentWorkerProtocolSession.InvalidOrder(
                "Job progress total changed during an active chunk.");
        }

        if (progress.CompletedMilliseconds < _lastProgressMilliseconds)
        {
            throw ParentWorkerProtocolSession.InvalidOrder("Job progress moved backwards.");
        }

        _progressTotalMilliseconds = progress.TotalMilliseconds;
        _lastProgressMilliseconds = progress.CompletedMilliseconds;
    }
}
