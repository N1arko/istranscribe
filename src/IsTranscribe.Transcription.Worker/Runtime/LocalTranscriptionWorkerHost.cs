using System.Threading.Channels;
using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;

namespace IsTranscribe.Transcription.Worker.Runtime;

public sealed record LocalTranscriptionWorkerHostOptions(
    string WorkerVersion,
    TimeSpan ShutdownGracePeriod)
{
    public static LocalTranscriptionWorkerHostOptions Default { get; } = new(
        WorkerVersion: LocalWorkerRuntimeContract.CurrentWorkerVersion,
        ShutdownGracePeriod: TimeSpan.FromSeconds(2));
}

/// <summary>
/// Child-process protocol host. It never writes application data to stdout/stderr and
/// publishes transcript text exclusively through a terminal result frame.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </summary>
public sealed class LocalTranscriptionWorkerHost
{
    internal const int MaximumBufferedSignals = 32;

    private readonly WorkerFramedConnection _connection;
    private readonly ILocalInferenceBackend _backend;
    private readonly IParentProcessMonitor _parentMonitor;
    private readonly LocalTranscriptionWorkerHostOptions _options;
    private readonly ChildWorkerProtocolSession _session = new();

    public LocalTranscriptionWorkerHost(
        WorkerFramedConnection connection,
        ILocalInferenceBackend backend,
        IParentProcessMonitor parentMonitor,
        LocalTranscriptionWorkerHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(parentMonitor);
        _connection = connection;
        _backend = backend;
        _parentMonitor = parentMonitor;
        _options = options ?? LocalTranscriptionWorkerHostOptions.Default;
        if (string.IsNullOrWhiteSpace(_options.WorkerVersion))
        {
            throw new ArgumentException("Worker version is required.", nameof(options));
        }

        if (_options.ShutdownGracePeriod < TimeSpan.Zero
            || _options.ShutdownGracePeriod > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shutdown grace period is outside its allowed range.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var helloEnvelope = await _connection.ReadAsync(cancellationToken);
        if (helloEnvelope is null)
        {
            return;
        }

        _session.AcceptIncoming(helloEnvelope);
        var hello = _session.Hello!;
        using var parentLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentExitTask = _parentMonitor.WaitForExitAsync(hello.ParentProcessId, parentLifetime.Token);
        await _connection.WriteAsync(
            _session.CreateReady(new WorkerReadyPayload(
                "initialized",
                _options.WorkerVersion,
                Backend: null,
                AvailableMemoryBytes: null)),
            cancellationToken);

        try
        {
            Task<WorkerEnvelope?>? pendingReadTask = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                pendingReadTask ??= _connection.ReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(pendingReadTask, parentExitTask);
                if (completed == parentExitTask)
                {
                    await ObserveAsync(parentExitTask);
                    return;
                }

                var envelope = await pendingReadTask;
                pendingReadTask = null;
                if (envelope is null)
                {
                    return;
                }

                _session.AcceptIncoming(envelope);
                switch (envelope.Payload)
                {
                    case WorkerProbePayload probe:
                        if (!await HandleProbeAsync(probe, parentExitTask, cancellationToken))
                        {
                            return;
                        }

                        break;
                    case WorkerStartPayload start:
                        var outcome = await HandleJobAsync(start, parentExitTask, cancellationToken);
                        if (!outcome.ContinueRunning)
                        {
                            return;
                        }

                        pendingReadTask = outcome.PendingReadTask;
                        break;
                    case WorkerShutdownPayload:
                        return;
                    default:
                        throw new WorkerProtocolException(
                            WorkerProtocolError.InvalidOrder,
                            $"Unexpected command payload {envelope.Payload.GetType().Name} while worker is idle.");
                }
            }
        }
        finally
        {
            parentLifetime.Cancel();
        }
    }

    private async Task<bool> HandleProbeAsync(
        WorkerProbePayload request,
        Task parentExitTask,
        CancellationToken cancellationToken)
    {
        using var probeLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTask = _backend.ProbeAsync(request, probeLifetime.Token).AsTask();
        var completed = await Task.WhenAny(probeTask, parentExitTask);
        if (completed == parentExitTask)
        {
            probeLifetime.Cancel();
            _ = ObserveAsync(probeTask);
            return false;
        }

        try
        {
            var result = await probeTask;
            await _connection.WriteAsync(
                _session.CreateReady(new WorkerReadyPayload(
                    "probe_completed",
                    _options.WorkerVersion,
                    result.Backend,
                    result.AvailableMemoryBytes)),
                cancellationToken);
        }
        catch (LocalInferenceException exception)
        {
            await _connection.WriteAsync(
                _session.CreateFailure(exception.ToPayload()),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await _connection.WriteAsync(
                _session.CreateFailure(new WorkerFailurePayload(
                    "backend_unavailable",
                    "probe_failed",
                    "The local inference backend could not be initialized.",
                    Retryable: true,
                    request.RequestedBackend)),
                cancellationToken);
        }

        return true;
    }

    private async Task<JobLoopOutcome> HandleJobAsync(
        WorkerStartPayload request,
        Task parentExitTask,
        CancellationToken cancellationToken)
    {
        using var jobLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signals = Channel.CreateBounded<WorkerSignal>(new BoundedChannelOptions(MaximumBufferedSignals)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var progress = new ChannelProgress(signals.Writer);
        var inferenceTask = RunInferenceAsync(request, progress, signals.Writer, jobLifetime.Token);
        Task<WorkerEnvelope?>? readTask = null;
        Task<WorkerSignal>? signalTask = null;

        while (true)
        {
            readTask ??= _connection.ReadAsync(cancellationToken).AsTask();
            signalTask ??= signals.Reader.ReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, signalTask, parentExitTask);
            if (completed == parentExitTask)
            {
                jobLifetime.Cancel();
                await WaitForGraceAsync(inferenceTask);
                return new JobLoopOutcome(false, null);
            }

            if (completed == readTask)
            {
                var command = await readTask;
                readTask = null;
                if (command is null)
                {
                    jobLifetime.Cancel();
                    await WaitForGraceAsync(inferenceTask);
                    return new JobLoopOutcome(false, null);
                }

                _session.AcceptIncoming(command);
                if (command.Payload is WorkerCancelPayload)
                {
                    jobLifetime.Cancel();
                    continue;
                }

                if (command.Payload is WorkerShutdownPayload)
                {
                    jobLifetime.Cancel();
                    await WaitForGraceAsync(inferenceTask);
                    return new JobLoopOutcome(false, null);
                }

                throw new WorkerProtocolException(
                    WorkerProtocolError.InvalidOrder,
                    $"Unexpected command payload {command.Payload.GetType().Name} while a job is active.");
            }

            var signal = await signalTask;
            signalTask = null;
            switch (signal)
            {
                case ProgressSignal progressSignal:
                    await _connection.WriteAsync(
                        _session.CreateProgress(progressSignal.Payload),
                        cancellationToken);
                    break;
                case ResultSignal resultSignal:
                    await _connection.WriteAsync(
                        _session.CreateResult(resultSignal.Payload),
                        cancellationToken);
                    await inferenceTask;
                    return new JobLoopOutcome(true, readTask);
                case FailureSignal failureSignal:
                    await _connection.WriteAsync(
                        _session.CreateFailure(failureSignal.Payload),
                        cancellationToken);
                    await inferenceTask;
                    return new JobLoopOutcome(true, readTask);
            }
        }
    }

    private async Task RunInferenceAsync(
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload> progress,
        ChannelWriter<WorkerSignal> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _backend.TranscribeAsync(request, progress, cancellationToken);
            await writer.WriteAsync(new ResultSignal(result), CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await writer.WriteAsync(
                new FailureSignal(new WorkerFailurePayload(
                    "cancelled",
                    "cancelled",
                    "Local transcription was cancelled.",
                    Retryable: true,
                    request.Backend)),
                CancellationToken.None);
        }
        catch (LocalInferenceException exception)
        {
            await writer.WriteAsync(new FailureSignal(exception.ToPayload()), CancellationToken.None);
        }
        catch (Exception)
        {
            await writer.WriteAsync(
                new FailureSignal(new WorkerFailurePayload(
                    "native_failure",
                    "unexpected_native_failure",
                    "The local inference worker stopped processing this chunk.",
                    Retryable: true,
                    request.Backend)),
                CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task WaitForGraceAsync(Task inferenceTask)
    {
        if (inferenceTask.IsCompleted)
        {
            await ObserveAsync(inferenceTask);
            return;
        }

        var delay = Task.Delay(_options.ShutdownGracePeriod);
        if (await Task.WhenAny(inferenceTask, delay) == inferenceTask)
        {
            await ObserveAsync(inferenceTask);
        }
        else
        {
            _ = ObserveAsync(inferenceTask);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private abstract record WorkerSignal;

    private sealed record JobLoopOutcome(
        bool ContinueRunning,
        Task<WorkerEnvelope?>? PendingReadTask);

    private sealed record ProgressSignal(WorkerProgressPayload Payload) : WorkerSignal;

    private sealed record ResultSignal(WorkerResultPayload Payload) : WorkerSignal;

    private sealed record FailureSignal(WorkerFailurePayload Payload) : WorkerSignal;

    private sealed class ChannelProgress(ChannelWriter<WorkerSignal> writer)
        : IProgress<WorkerProgressPayload>
    {
        public void Report(WorkerProgressPayload value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!writer.TryWrite(new ProgressSignal(value)))
            {
                throw new InvalidOperationException("Worker progress channel is closed.");
            }
        }
    }
}
