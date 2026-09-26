using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;
using IsTranscribe.Transcription.Worker.Runtime;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

internal sealed class InProcessWorkerLauncher(
    ILocalInferenceBackend backend,
    LocalTranscriptionWorkerHostOptions? hostOptions = null) : IWorkerProcessLauncher
{
    public TestWorkerProcess? LastProcess { get; private set; }

    public WorkerProcessConnection Launch(string workerExecutablePath)
    {
        var (parentCommand, childCommand) = TestPipeFactory.CreateOneWay(System.IO.Pipes.PipeDirection.Out);
        var (parentResult, childResult) = TestPipeFactory.CreateOneWay(System.IO.Pipes.PipeDirection.In);

        var childProtocol = new WorkerFramedConnection(childCommand, childResult, ownsStreams: false);
        var lifetime = new CancellationTokenSource();
        var host = new LocalTranscriptionWorkerHost(
            childProtocol,
            backend,
            new NeverParentMonitor(),
            hostOptions ?? new LocalTranscriptionWorkerHostOptions(
                "test-worker",
                TimeSpan.FromMilliseconds(50)));
        var process = new TestWorkerProcess(
            host.RunAsync(lifetime.Token),
            lifetime,
            childProtocol,
            childCommand,
            childResult);
        LastProcess = process;
        return new WorkerProcessConnection(parentCommand, parentResult, process);
    }

    internal sealed class TestWorkerProcess : IWorkerChildProcess
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly WorkerFramedConnection _protocol;
        private readonly Stream _command;
        private readonly Stream _result;
        private readonly Task _runner;
        private int _killCount;
        private int _disposed;

        public TestWorkerProcess(
            Task hostTask,
            CancellationTokenSource lifetime,
            WorkerFramedConnection protocol,
            Stream command,
            Stream result)
        {
            _lifetime = lifetime;
            _protocol = protocol;
            _command = command;
            _result = result;
            _runner = ObserveAndCloseAsync(hostTask);
        }

        public int Id => 4242;

        public bool HasExited => _runner.IsCompleted;

        public int KillCount => Volatile.Read(ref _killCount);

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _runner.WaitAsync(cancellationToken);

        public void KillProcessTree()
        {
            Interlocked.Increment(ref _killCount);
            _lifetime.Cancel();
            _command.Dispose();
            _result.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _lifetime.Cancel();
            _command.Dispose();
            _result.Dispose();
            await ObserveAsync(_runner);
            _lifetime.Dispose();
        }

        private async Task ObserveAndCloseAsync(Task hostTask)
        {
            await ObserveAsync(hostTask);
            await _protocol.DisposeAsync();
            await _command.DisposeAsync();
            await _result.DisposeAsync();
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class NeverParentMonitor : IParentProcessMonitor
    {
        public Task WaitForExitAsync(int parentProcessId, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
