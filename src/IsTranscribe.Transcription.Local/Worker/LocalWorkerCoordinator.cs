using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Local.Worker;

public sealed record LocalWorkerCoordinatorOptions(
    string ClientVersion,
    TimeSpan HandshakeTimeout,
    TimeSpan ProbeTimeout,
    TimeSpan JobTimeout,
    TimeSpan CancelGracePeriod,
    TimeSpan ShutdownGracePeriod)
{
    public static LocalWorkerCoordinatorOptions Default { get; } = new(
        ClientVersion: LocalWorkerRuntimeContract.CurrentWorkerVersion,
        HandshakeTimeout: TimeSpan.FromSeconds(5),
        ProbeTimeout: TimeSpan.FromMinutes(2),
        JobTimeout: TimeSpan.FromHours(6),
        CancelGracePeriod: TimeSpan.FromSeconds(2),
        ShutdownGracePeriod: TimeSpan.FromSeconds(2));
}

public sealed class WorkerRemoteFailureException : Exception
{
    public WorkerRemoteFailureException(WorkerFailurePayload failure)
        : base(failure.SafeMessage)
    {
        Failure = failure;
    }

    public WorkerFailurePayload Failure { get; }
}

public sealed class WorkerProcessExitedException : Exception
{
    public WorkerProcessExitedException()
        : base("Local transcription worker closed its result pipe before completing the command.")
    {
    }
}

/// <summary>
/// Parent-side lifecycle coordinator with bounded cancellation and process-tree termination.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// </summary>
public sealed class LocalWorkerCoordinator : IAsyncDisposable
{
    private readonly IWorkerProcessLauncher _launcher;
    private readonly LocalWorkerCoordinatorOptions _options;
    private readonly ParentWorkerProtocolSession _session = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private WorkerProcessConnection? _processConnection;
    private WorkerFramedConnection? _protocol;
    private CancellationTokenSource? _connectionLifetime;
    private bool _terminal;
    private bool _disposed;

    public LocalWorkerCoordinator(
        IWorkerProcessLauncher launcher,
        LocalWorkerCoordinatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _options = options ?? LocalWorkerCoordinatorOptions.Default;
        ValidateOptions(_options);
    }

    public bool IsStarted => _protocol is not null && !_disposed;

    public async Task<WorkerReadyPayload> StartAsync(
        string workerExecutablePath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminal)
        {
            throw new InvalidOperationException("Local worker coordinator lifetime has ended.");
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_protocol is not null)
            {
                throw new InvalidOperationException("Local worker coordinator is already started.");
            }

            try
            {
                _connectionLifetime = new CancellationTokenSource();
                _processConnection = _launcher.Launch(workerExecutablePath);
                _protocol = new WorkerFramedConnection(
                    _processConnection.ResultInput,
                    _processConnection.CommandOutput,
                    ownsStreams: true);
                await _protocol.WriteAsync(
                    _session.CreateHello(Environment.ProcessId, _options.ClientVersion),
                    cancellationToken);
                var response = await ReadRequiredAsync(_options.HandshakeTimeout, cancellationToken);
                _session.AcceptIncoming(response);
                return (WorkerReadyPayload)response.Payload;
            }
            catch
            {
                await TerminateProcessAsync();
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<WorkerReadyPayload> ProbeAsync(
        WorkerProbePayload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            await _protocol!.WriteAsync(_session.CreateProbe(request), cancellationToken);
            WorkerEnvelope response;
            try
            {
                response = await ReadRequiredAsync(_options.ProbeTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                await TerminateProcessAsync();
                throw;
            }

            _session.AcceptIncoming(response);
            if (response.Payload is WorkerFailurePayload failure)
            {
                throw new WorkerRemoteFailureException(failure);
            }

            return (WorkerReadyPayload)response.Payload;
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (WorkerProtocolException)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (WorkerProcessExitedException)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (IOException)
        {
            await TerminateProcessAsync();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<WorkerResultPayload> TranscribeAsync(
        string jobId,
        int chunkIndex,
        WorkerStartPayload request,
        IProgress<WorkerProgressPayload>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken);
        var cancellationCompleted = false;
        try
        {
            EnsureStarted();
            await _protocol!.WriteAsync(
                _session.CreateStart(jobId, chunkIndex, request),
                cancellationToken);
            var receiveTask = ReceiveJobResultAsync(progress);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var timeoutTask = Task.Delay(_options.JobTimeout);
            var completed = await Task.WhenAny(receiveTask, cancellationTask, timeoutTask);
            if (completed == receiveTask)
            {
                return await receiveTask;
            }

            if (completed == cancellationTask)
            {
                await CancelAndBoundAsync(receiveTask);
                cancellationCompleted = true;
                throw new OperationCanceledException(cancellationToken);
            }

            await CancelAndBoundAsync(receiveTask);
            throw new TimeoutException("Local transcription worker did not complete within the bounded job timeout.");
        }
        catch (WorkerProtocolException)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (WorkerProcessExitedException)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationCompleted)
        {
            await TerminateProcessAsync();
            throw;
        }
        catch (OperationCanceledException) when (cancellationCompleted)
        {
            throw;
        }
        catch (WorkerRemoteFailureException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            await TerminateProcessAsync();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _protocol is null)
        {
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_protocol is null)
            {
                return;
            }

            try
            {
                await _protocol.WriteAsync(_session.CreateShutdown("parent_shutdown"), cancellationToken);
                await WaitForExitOrTerminateAsync(_options.ShutdownGracePeriod, cancellationToken);
                _terminal = true;
            }
            catch
            {
                await TerminateProcessAsync();
                throw;
            }
            finally
            {
                await DisposeConnectionAsync();
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await ShutdownAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            await TerminateProcessAsync();
        }
        finally
        {
            _disposed = true;
            _operationLock.Dispose();
        }
    }

    private async Task<WorkerResultPayload> ReceiveJobResultAsync(
        IProgress<WorkerProgressPayload>? progress)
    {
        while (true)
        {
            var response = await ReadRequiredAsync(
                Timeout.InfiniteTimeSpan,
                _connectionLifetime?.Token ?? CancellationToken.None);
            _session.AcceptIncoming(response);
            switch (response.Payload)
            {
                case WorkerProgressPayload update:
                    progress?.Report(update);
                    break;
                case WorkerResultPayload result:
                    return result;
                case WorkerFailurePayload failure:
                    throw new WorkerRemoteFailureException(failure);
            }
        }
    }

    private async Task CancelAndBoundAsync(Task<WorkerResultPayload> receiveTask)
    {
        if (_protocol is not null && _session.State == ParentWorkerSessionState.Running)
        {
            try
            {
                await _protocol.WriteAsync(
                    _session.CreateCancel("parent_cancelled"),
                    CancellationToken.None);
            }
            catch (Exception)
            {
                await TerminateAndDrainAsync(receiveTask);
                return;
            }
        }

        var completed = await Task.WhenAny(receiveTask, Task.Delay(_options.CancelGracePeriod));
        if (completed == receiveTask)
        {
            try
            {
                await receiveTask;
            }
            catch (WorkerRemoteFailureException)
            {
            }
            catch (Exception)
            {
                await TerminateAndDrainAsync(receiveTask);
            }

            return;
        }

        await TerminateAndDrainAsync(receiveTask);
    }

    private async Task<WorkerEnvelope> ReadRequiredAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var readTask = _protocol!.ReadAsync(cancellationToken).AsTask();
        WorkerEnvelope? response;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            response = await readTask;
        }
        else
        {
            response = await readTask.WaitAsync(timeout, cancellationToken);
        }

        return response ?? throw new WorkerProcessExitedException();
    }

    private async Task WaitForExitOrTerminateAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        if (_processConnection is null || _processConnection.Process.HasExited)
        {
            return;
        }

        try
        {
            await _processConnection.Process.WaitForExitAsync(cancellationToken).WaitAsync(gracePeriod, cancellationToken);
        }
        catch (TimeoutException)
        {
            await KillAndWaitAsync(_processConnection.Process);
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(_processConnection.Process);
            throw;
        }
    }

    private async Task TerminateProcessAsync()
    {
        _terminal = true;
        _connectionLifetime?.Cancel();
        if (_processConnection is not null)
        {
            try
            {
                if (!_processConnection.Process.HasExited)
                {
                    await KillAndWaitAsync(_processConnection.Process);
                }
            }
            catch (Exception)
            {
            }
        }

        await DisposeConnectionAsync();
    }

    private async Task TerminateAndDrainAsync(Task receiveTask)
    {
        await TerminateProcessAsync();
        try
        {
            await receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }

    private static async Task KillAndWaitAsync(IWorkerChildProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.KillProcessTree();
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        if (!process.HasExited)
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connectionLifetime is not null)
        {
            await _connectionLifetime.CancelAsync();
        }

        if (_protocol is not null)
        {
            await _protocol.DisposeAsync();
            _protocol = null;
        }

        if (_processConnection is not null)
        {
            await _processConnection.Process.DisposeAsync();
            _processConnection = null;
        }

        _connectionLifetime?.Dispose();
        _connectionLifetime = null;
    }

    private void EnsureStarted()
    {
        if (_terminal)
        {
            throw new InvalidOperationException("Local worker coordinator lifetime has ended.");
        }

        if (_protocol is null || _processConnection is null)
        {
            throw new InvalidOperationException("Local worker coordinator has not been started.");
        }
    }

    private static void ValidateOptions(LocalWorkerCoordinatorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientVersion);
        RequireTimeout(options.HandshakeTimeout, nameof(options.HandshakeTimeout));
        RequireTimeout(options.ProbeTimeout, nameof(options.ProbeTimeout));
        RequireTimeout(options.JobTimeout, nameof(options.JobTimeout));
        RequireTimeout(options.CancelGracePeriod, nameof(options.CancelGracePeriod));
        RequireTimeout(options.ShutdownGracePeriod, nameof(options.ShutdownGracePeriod));
    }

    private static void RequireTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(name, "Timeout is outside its allowed range.");
        }
    }
}
