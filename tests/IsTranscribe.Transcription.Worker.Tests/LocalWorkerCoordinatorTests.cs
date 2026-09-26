using IsTranscribe.Transcription.Local.Protocol;
using IsTranscribe.Transcription.Local.Worker;
using IsTranscribe.Transcription.Worker.Runtime;
using IsTranscribe.Transcription.Worker.Tests.Support;
using Xunit;

namespace IsTranscribe.Transcription.Worker.Tests;

/// <summary>
/// Parent lifecycle, timeout and process containment coverage.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#verification
/// </summary>
[Collection(WorkerLifecycleCollection.Name)]
public sealed class LocalWorkerCoordinatorTests
{
    [Fact]
    public void DefaultProbeTimeoutCoversFullPublicModelVerification()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), LocalWorkerCoordinatorOptions.Default.ProbeTimeout);
    }

    [Fact]
    public async Task CoordinatorCompletesHandshakeProbeProgressResultAndShutdown()
    {
        var backend = new DelegateBackend
        {
            Transcribe = (_, progress, _) =>
            {
                progress.Report(WorkerTestData.Progress(20_000));
                progress.Report(WorkerTestData.Progress(60_000));
                return ValueTask.FromResult(WorkerTestData.Result("coordinator transcript"));
            },
        };
        var launcher = new InProcessWorkerLauncher(backend);
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions());
        var progress = new InlineProgress<WorkerProgressPayload>();

        var initial = await coordinator.StartAsync("unused-test-worker", CancellationToken.None);
        var probe = await coordinator.ProbeAsync(WorkerTestData.Probe(), CancellationToken.None);
        var result = await coordinator.TranscribeAsync(
            "job-1",
            0,
            WorkerTestData.Start(),
            progress,
            CancellationToken.None);

        Assert.Equal("initialized", initial.State);
        Assert.Equal("probe_completed", probe.State);
        Assert.Equal("cpu", probe.Backend);
        Assert.Equal(2, progress.Values.Count);
        Assert.Equal("coordinator transcript", Assert.Single(result.Segments).Text);

        await coordinator.ShutdownAsync(CancellationToken.None);
        Assert.True(launcher.LastProcess!.HasExited);
        Assert.Equal(0, launcher.LastProcess.KillCount);
    }

    [Fact]
    public async Task RealChildProcessHandshakesOverInheritedAnonymousPipes()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "IsTranscribe.Transcription.Worker.exe"
            : "IsTranscribe.Transcription.Worker";
        var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
        Assert.True(File.Exists(executablePath), $"Missing worker apphost at {executablePath}.");
        await using var coordinator = new LocalWorkerCoordinator(
            new AnonymousPipeWorkerProcessLauncher(),
            FastOptions() with
            {
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                ProbeTimeout = TimeSpan.FromSeconds(30),
            });

        var ready = await coordinator.StartAsync(executablePath, CancellationToken.None);
        var missingModelPath = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-missing-model-{Guid.NewGuid():N}",
            "ggml-small.bin");
        var failure = await Assert.ThrowsAsync<WorkerRemoteFailureException>(() =>
            coordinator.ProbeAsync(
                WorkerTestData.Probe() with { ModelPath = missingModelPath },
                CancellationToken.None));

        Assert.Equal("initialized", ready.State);
        Assert.Equal("model_missing", failure.Failure.StableCode);
        await coordinator.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IgnoredCancellationEndsWithBoundedProcessTreeTermination()
    {
        var never = new TaskCompletionSource<WorkerResultPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend
        {
            Transcribe = (_, _, _) => new ValueTask<WorkerResultPayload>(never.Task),
        };
        var launcher = new InProcessWorkerLauncher(backend);
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions());
        await coordinator.StartAsync("unused-test-worker", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TranscribeAsync(
                "job-1",
                0,
                WorkerTestData.Start(),
                progress: null,
                cancellation.Token));

        Assert.Equal(1, launcher.LastProcess!.KillCount);
        Assert.True(launcher.LastProcess.HasExited);
        Assert.False(coordinator.IsStarted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync("unused-test-worker", CancellationToken.None));
    }

    [Fact]
    public async Task CooperativeCancellationKeepsSessionAlignedForNextJob()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var backend = new DelegateBackend
        {
            Transcribe = async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return WorkerTestData.Result("second job completed");
            },
        };
        var launcher = new InProcessWorkerLauncher(backend);
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions());
        await coordinator.StartAsync("unused-test-worker", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var first = coordinator.TranscribeAsync(
            "job-1",
            0,
            WorkerTestData.Start(),
            progress: null,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = await coordinator.TranscribeAsync(
            "job-2",
            0,
            WorkerTestData.Start(),
            progress: null,
            CancellationToken.None);

        Assert.True(coordinator.IsStarted);
        Assert.Equal("second job completed", Assert.Single(second.Segments).Text);
        await coordinator.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ImmediateCancellationFollowedByShutdownLeavesNoWorkerProcess()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend
        {
            Transcribe = async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        var launcher = new InProcessWorkerLauncher(backend);
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions());
        await coordinator.StartAsync("unused-test-worker", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var transcription = coordinator.TranscribeAsync(
            "job-1",
            0,
            WorkerTestData.Start(),
            progress: null,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        var shutdown = coordinator.ShutdownAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(launcher.LastProcess!.HasExited);
        Assert.Equal(0, launcher.LastProcess.KillCount);
    }

    [Fact]
    public async Task HungProbeTimesOutAndTerminatesProcessTree()
    {
        var never = new TaskCompletionSource<LocalWorkerProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelegateBackend
        {
            Probe = (_, _) => new ValueTask<LocalWorkerProbeResult>(never.Task),
        };
        var launcher = new InProcessWorkerLauncher(backend);
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions() with
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(50),
        });
        await coordinator.StartAsync("unused-test-worker", CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.ProbeAsync(WorkerTestData.Probe(), CancellationToken.None));

        Assert.Equal(1, launcher.LastProcess!.KillCount);
        Assert.True(launcher.LastProcess.HasExited);
    }

    [Fact]
    public async Task ChildEofIsReportedAsWorkerExitWithoutDamagingParent()
    {
        var launcher = new EofAfterStartLauncher();
        await using var coordinator = new LocalWorkerCoordinator(launcher, FastOptions());
        await coordinator.StartAsync("unused-test-worker", CancellationToken.None);

        await Assert.ThrowsAsync<WorkerProcessExitedException>(() =>
            coordinator.TranscribeAsync(
                "job-1",
                0,
                WorkerTestData.Start(),
                progress: null,
                CancellationToken.None));

        Assert.True(launcher.Process!.HasExited);
    }

    [Fact]
    public void LaunchContractContainsOnlyModeArgumentAndSanitizedEnvironment()
    {
        const string secretName = "ISTRANSCRIBE_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(secretName);
        Environment.SetEnvironmentVariable(secretName, "must-not-reach-worker");
        try
        {
            var startInfo = AnonymousPipeWorkerProcessLauncher.CreateStartInfo(
                "/app/IsTranscribe.Transcription.Worker",
                "command-handle",
                "result-handle");

            Assert.Equal([WorkerProcessContract.ModeArgument], startInfo.ArgumentList);
            Assert.DoesNotContain("recording", string.Join(' ', startInfo.ArgumentList), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model", string.Join(' ', startInfo.ArgumentList), StringComparison.OrdinalIgnoreCase);
            Assert.False(startInfo.Environment.ContainsKey(secretName));
            Assert.Equal(
                "command-handle",
                startInfo.Environment[WorkerProcessContract.CommandPipeEnvironmentVariable]);
            Assert.Equal(
                "result-handle",
                startInfo.Environment[WorkerProcessContract.ResultPipeEnvironmentVariable]);
            Assert.DoesNotContain(startInfo.Environment.Keys, static name =>
                name.Contains("GROQ", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OPENROUTER", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
        }
    }

    private static LocalWorkerCoordinatorOptions FastOptions() => new(
        ClientVersion: "test-parent",
        HandshakeTimeout: TimeSpan.FromSeconds(2),
        ProbeTimeout: TimeSpan.FromSeconds(2),
        JobTimeout: TimeSpan.FromSeconds(2),
        CancelGracePeriod: TimeSpan.FromMilliseconds(75),
        ShutdownGracePeriod: TimeSpan.FromMilliseconds(75));

    private sealed class DelegateBackend : ILocalInferenceBackend
    {
        public Func<WorkerProbePayload, CancellationToken, ValueTask<LocalWorkerProbeResult>> Probe { get; init; } =
            static (request, _) => ValueTask.FromResult(new LocalWorkerProbeResult(
                request.RequestedBackend,
                8_000_000_000));

        public Func<WorkerStartPayload, IProgress<WorkerProgressPayload>, CancellationToken, ValueTask<WorkerResultPayload>>
            Transcribe
        { get; init; } = static (_, _, _) => ValueTask.FromResult(WorkerTestData.Result());

        public ValueTask<LocalWorkerProbeResult> ProbeAsync(
            WorkerProbePayload request,
            CancellationToken cancellationToken) => Probe(request, cancellationToken);

        public ValueTask<WorkerResultPayload> TranscribeAsync(
            WorkerStartPayload request,
            IProgress<WorkerProgressPayload> progress,
            CancellationToken cancellationToken) => Transcribe(request, progress, cancellationToken);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class EofAfterStartLauncher : IWorkerProcessLauncher
    {
        public ScriptedProcess? Process { get; private set; }

        public WorkerProcessConnection Launch(string workerExecutablePath)
        {
            var (parentCommand, childCommand) = TestPipeFactory.CreateOneWay(
                System.IO.Pipes.PipeDirection.Out);
            var (parentResult, childResult) = TestPipeFactory.CreateOneWay(
                System.IO.Pipes.PipeDirection.In);
            var lifetime = new CancellationTokenSource();
            var process = new ScriptedProcess(
                RunScriptAsync(childCommand, childResult, lifetime.Token),
                lifetime,
                childCommand,
                childResult);
            Process = process;
            return new WorkerProcessConnection(parentCommand, parentResult, process);
        }

        private static async Task RunScriptAsync(
            Stream command,
            Stream result,
            CancellationToken cancellationToken)
        {
            await using var connection = new WorkerFramedConnection(
                command,
                result,
                ownsStreams: false);
            var child = new ChildWorkerProtocolSession();
            var hello = await connection.ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException("Expected hello.");
            child.AcceptIncoming(hello);
            await connection.WriteAsync(
                child.CreateReady(new WorkerReadyPayload("initialized", "test-worker", null, null)),
                cancellationToken);
            var start = await connection.ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException("Expected start.");
            child.AcceptIncoming(start);
        }
    }

    private sealed class ScriptedProcess : IWorkerChildProcess
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly Stream _command;
        private readonly Stream _result;
        private readonly Task _runner;

        public ScriptedProcess(
            Task script,
            CancellationTokenSource lifetime,
            Stream command,
            Stream result)
        {
            _lifetime = lifetime;
            _command = command;
            _result = result;
            _runner = FinishAsync(script);
        }

        public int Id => 4243;

        public bool HasExited => _runner.IsCompleted;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _runner.WaitAsync(cancellationToken);

        public void KillProcessTree()
        {
            _lifetime.Cancel();
            _command.Dispose();
            _result.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _command.Dispose();
            _result.Dispose();
            await _runner;
            _lifetime.Dispose();
        }

        private async Task FinishAsync(Task script)
        {
            try
            {
                await script;
            }
            catch (Exception)
            {
            }
            finally
            {
                await _command.DisposeAsync();
                await _result.DisposeAsync();
            }
        }
    }
}
