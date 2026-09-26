using System.Runtime.Versioning;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Application.Diagnostics;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioRecorderEngineTests
{
    [Fact]
    public async Task StartAsyncInAskModeKeepsSessionBufferedUntilPromote()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(AudioCaptureArtifactKind.Microphone, Path.Combine(tempRoot, "mic.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());

            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));

            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Ask,
                [AudioCaptureSourceRequest.Microphone("mic-1")],
                tempRoot,
                PrebufferSeconds: 15,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);

            Assert.False(session.Snapshot.IsPersistingAudio);
            Assert.False(adapter.PersistImmediately);
            Assert.NotNull(adapter.SessionTimelineOriginUtc);

            await session.PromotePrebufferAsync(CancellationToken.None);

            Assert.True(session.Snapshot.IsPersistingAudio);
            Assert.True(adapter.PromoteCalled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncRejectsUnsupportedProcessLoopbackRequests()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(new FakeCaptureAdapter(AudioCaptureArtifactKind.Output, Path.Combine(tempRoot, "output.wav"))),
                new FakeMixArtifactBuilder());

            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(false, true, true, true, true));

            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Auto,
                [AudioCaptureSourceRequest.ProcessOutput(404, "Zoom.exe")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await Assert.ThrowsAsync<NotSupportedException>(async () => await engine.StartAsync(request, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SessionCompletesAndPreservesArtifactWhenSourceStopsNaturally()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(AudioCaptureArtifactKind.Output, Path.Combine(tempRoot, "output.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());

            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));

            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            adapter.TriggerNaturalStop();

            await Task.Delay(100);

            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            Assert.Single(session.Snapshot.Artifacts);
            Assert.Single(session.Snapshot.StopEvents);
            Assert.Equal(AudioCaptureStopKind.SourceCompleted, session.Snapshot.StopEvents[0].StopKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PauseAndResumeDelegateToAdapters()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(AudioCaptureArtifactKind.Output, Path.Combine(tempRoot, "output.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());

            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));

            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);

            await session.PauseAsync(CancellationToken.None);
            await session.ResumeAsync(CancellationToken.None);

            Assert.True(adapter.PauseCalled);
            Assert.True(adapter.ResumeCalled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
    [Fact]
    public async Task StopAsyncContinuesAfterSourceFinalizationFailureAndPreservesHealthySibling()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var failingOutput = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                StopException = new IOException("Injected source finalization failure.")
            };
            var healthyMicrophone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(failingOutput, healthyMicrophone),
                new FakeMixArtifactBuilder());

            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            await session.StopAsync(CancellationToken.None);

            Assert.Equal(2, failingOutput.StopCallCount);
            Assert.Equal(1, healthyMicrophone.StopCallCount);
            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            var artifact = Assert.Single(session.Snapshot.Artifacts);
            Assert.Equal(AudioCaptureArtifactKind.Microphone, artifact.Kind);
            Assert.Contains(session.Snapshot.Faults, fault => fault.Code == "capture_stop_failed");
            Assert.Equal(2, session.Snapshot.StopEvents.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentStopWaitsForNaturalFinalizationAlreadyInProgress()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var releaseFinalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                StopDelay = releaseFinalization.Task,
                StopCalled = () => stopStarted.TrySetResult()
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            adapter.TriggerNaturalStop();
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var concurrentStop = session.StopAsync(CancellationToken.None).AsTask();
            Assert.False(concurrentStop.IsCompleted);

            releaseFinalization.TrySetResult();
            await concurrentStop.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            Assert.Single(session.Snapshot.Artifacts);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledStopWaitDoesNotCancelInternalFinalizationOrLaterWaiter()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var releaseFinalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"))
            {
                StopDelay = releaseFinalization.Task,
                StopCalled = () => stopStarted.TrySetResult()
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [AudioCaptureSourceRequest.Microphone("mic-1")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.StopAsync(canceled.Token).AsTask());
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var laterWaiter = session.StopAsync(CancellationToken.None).AsTask();
            Assert.False(laterWaiter.IsCompleted);

            releaseFinalization.TrySetResult();
            await laterWaiter.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            Assert.Single(session.Snapshot.Artifacts);
            Assert.DoesNotContain(session.Snapshot.Faults, fault => fault.Code == "capture_stop_failed");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StopAsyncRequestsEverySourceBeforeAwaitingBlockedAdapter()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var releaseOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var microphoneStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var blockedOutput = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                StopDelay = releaseOutput.Task
            };
            var healthyMicrophone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"))
            {
                StopCalled = () => microphoneStopStarted.TrySetResult()
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(blockedOutput, healthyMicrophone),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            var stopTask = session.StopAsync(CancellationToken.None).AsTask();

            await microphoneStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, blockedOutput.StopCallCount);
            Assert.Equal(1, healthyMicrophone.StopCallCount);
            Assert.False(stopTask.IsCompleted);

            releaseOutput.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, session.Snapshot.Artifacts.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSourceFaultsPublishCompleteImmutableSnapshots()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var output = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"));
            var microphone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(output, microphone),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            using var callbackBarrier = new Barrier(participantCount: 2);
            const int faultsPerSource = 200;
            var outputFaults = Task.Run(() =>
            {
                callbackBarrier.SignalAndWait();
                for (var index = 0; index < faultsPerSource; index++)
                {
                    output.TriggerFault(index);
                }
            });
            var microphoneFaults = Task.Run(() =>
            {
                callbackBarrier.SignalAndWait();
                for (var index = 0; index < faultsPerSource; index++)
                {
                    microphone.TriggerFault(index);
                }
            });

            await Task.WhenAll(outputFaults, microphoneFaults).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(faultsPerSource * 2, session.Snapshot.Faults.Count);
            Assert.All(session.Snapshot.Faults, fault => Assert.StartsWith("test_fault_", fault.Code));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ThrowingSnapshotObserverCannotAbortAdapterFinalization()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var output = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"));
            var microphone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(output, microphone),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            session.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.State == AudioRecorderState.Stopping)
                {
                    throw new InvalidOperationException("Injected observer failure.");
                }
            };

            await session.StopAsync(CancellationToken.None);

            Assert.Equal(1, output.StopCallCount);
            Assert.Equal(1, microphone.StopCallCount);
            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            Assert.Equal(2, session.Snapshot.Artifacts.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StopWaitsForClaimedTwoSourcePrebufferPromotion()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var releasePromotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bothPromotionsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var promotionStartCount = 0;
            Action promotionStarted = () =>
            {
                if (Interlocked.Increment(ref promotionStartCount) == 2)
                {
                    bothPromotionsStarted.TrySetResult();
                }
            };
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var output = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                PromoteDelay = releasePromotion.Task,
                PromoteCalledAction = promotionStarted
            };
            var microphone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"))
            {
                PromoteDelay = releasePromotion.Task,
                PromoteCalledAction = promotionStarted
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(output, microphone),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Ask,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 5,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            var promotionTask = session.PromotePrebufferAsync(CancellationToken.None).AsTask();
            await bothPromotionsStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var stopTask = session.StopAsync(CancellationToken.None).AsTask();
            Assert.False(stopTask.IsCompleted);
            Assert.Equal(0, output.StopCallCount);
            Assert.Equal(0, microphone.StopCallCount);

            releasePromotion.TrySetResult();
            await promotionTask.WaitAsync(TimeSpan.FromSeconds(1));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(output.PromoteCalled);
            Assert.True(microphone.PromoteCalled);
            Assert.True(session.Snapshot.IsPersistingAudio);
            Assert.Equal(2, session.Snapshot.Artifacts.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PromotionAfterStopClaimIsDeterministicNoOp()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                StopDelay = releaseStop.Task,
                StopCalled = () => stopStarted.TrySetResult()
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Ask,
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")],
                tempRoot,
                PrebufferSeconds: 5,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            var stopTask = session.StopAsync(CancellationToken.None).AsTask();
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await session.PromotePrebufferAsync(CancellationToken.None);
            Assert.False(adapter.PromoteCalled);

            releaseStop.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(session.Snapshot.IsPersistingAudio);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PromotionReportsNaturallyStoppedSourceAndKeepsHealthySibling()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var stoppedOutput = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                PromotionOutcome = AudioCapturePromotionOutcome.SourceUnavailable
            };
            var healthyMicrophone = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Microphone,
                Path.Combine(tempRoot, "mic.wav"));
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(stoppedOutput, healthyMicrophone),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Ask,
                [
                    AudioCaptureSourceRequest.DeviceLoopback("render-1"),
                    AudioCaptureSourceRequest.Microphone("mic-1")
                ],
                tempRoot,
                PrebufferSeconds: 5,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            stoppedOutput.TriggerNaturalStopWithoutArtifact();

            await session.PromotePrebufferAsync(CancellationToken.None);

            Assert.True(session.Snapshot.IsPersistingAudio);
            var fault = Assert.Single(session.Snapshot.Faults, static fault =>
                fault.Code == "prebuffer_source_unavailable");
            Assert.Equal(AudioCaptureSourceKind.DeviceLoopback, fault.SourceKind);

            await session.StopAsync(CancellationToken.None);
            var artifact = Assert.Single(session.Snapshot.Artifacts);
            Assert.Equal(AudioCaptureArtifactKind.Microphone, artifact.Kind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StopRetriesTransientInitiationFailureBeforePublishingTerminalSnapshot()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var logger = new BootstrapFileLogger(Path.Combine(tempRoot, "host.log"));
            var adapter = new FakeCaptureAdapter(
                AudioCaptureArtifactKind.Output,
                Path.Combine(tempRoot, "output.wav"))
            {
                StopFailuresRemaining = 1
            };
            var engine = new AudioRecorderEngine(
                logger,
                new FakeAdapterFactory(adapter),
                new FakeMixArtifactBuilder());
            engine.UpdateCapabilitySnapshot(new AudioFoundationCapabilitySnapshot(true, true, true, true, true));
            var request = new AudioCaptureRequest(
                Guid.NewGuid(),
                AudioCaptureMode.Manual,
                [AudioCaptureSourceRequest.DeviceLoopback("render-1")],
                tempRoot,
                PrebufferSeconds: 0,
                CreateMixedArtifact: false);

            await using var session = await engine.StartAsync(request, CancellationToken.None);
            await session.StopAsync(CancellationToken.None);

            Assert.Equal(2, adapter.StopCallCount);
            Assert.Equal(AudioRecorderState.Completed, session.Snapshot.State);
            Assert.Single(session.Snapshot.Artifacts);
            Assert.DoesNotContain(session.Snapshot.Faults, static fault => fault.Code == "capture_stop_failed");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "isTranscribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeAdapterFactory : IAudioCaptureAdapterFactory
    {
        private readonly Queue<FakeCaptureAdapter> _adapters;

        public FakeAdapterFactory(params FakeCaptureAdapter[] adapters)
        {
            _adapters = new Queue<FakeCaptureAdapter>(adapters);
        }

        public IAudioCaptureAdapter Create(
            AudioCaptureRequest request,
            AudioCaptureSourceRequest source,
            int prebufferSeconds) => _adapters.Dequeue();
    }

    private sealed class FakeMixArtifactBuilder : IAudioArtifactPreparer
    {
        public ValueTask<AudioCaptureArtifact?> PrepareAsync(
            AudioCaptureRequest request,
            IReadOnlyList<AudioCaptureArtifact> sourceArtifacts,
            CancellationToken cancellationToken) => ValueTask.FromResult<AudioCaptureArtifact?>(null);
    }

    private sealed class FakeCaptureAdapter(AudioCaptureArtifactKind artifactKind, string artifactPath) : IAudioCaptureAdapter
    {
        private readonly AudioCaptureArtifactKind _artifactKind = artifactKind;
        private readonly string _artifactPath = artifactPath;

        public event EventHandler<AudioCaptureFault>? Faulted;

        public event EventHandler<AudioCaptureStopEvent>? Stopped;

        public bool IsActive { get; private set; }

        public AudioCaptureArtifact? FinalArtifact { get; private set; }

        public AudioCaptureStopEvent? StopEvent { get; private set; }

        public bool PersistImmediately { get; private set; }

        public bool PromoteCalled { get; private set; }

        public bool PauseCalled { get; private set; }

        public bool ResumeCalled { get; private set; }

        public DateTimeOffset? SessionTimelineOriginUtc { get; private set; }

        public Exception? StopException { get; init; }

        public int StopFailuresRemaining { get; set; }

        public int StopCallCount { get; private set; }

        public Task? StopDelay { get; init; }

        public Action? StopCalled { get; init; }

        public Task? PromoteDelay { get; init; }

        public Action? PromoteCalledAction { get; init; }

        public AudioCapturePromotionOutcome PromotionOutcome { get; init; } =
            AudioCapturePromotionOutcome.Promoted;

        public ValueTask StartAsync(
            bool persistImmediately,
            DateTimeOffset sessionTimelineOriginUtc,
            CancellationToken cancellationToken)
        {
            PersistImmediately = persistImmediately;
            SessionTimelineOriginUtc = sessionTimelineOriginUtc;
            IsActive = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<AudioCapturePromotionOutcome> PromoteAsync(CancellationToken cancellationToken)
        {
            PromoteCalled = true;
            PromoteCalledAction?.Invoke();
            if (PromoteDelay is not null)
            {
                await PromoteDelay.ConfigureAwait(false);
            }

            return PromotionOutcome;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            PauseCalled = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalled = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<AudioCaptureArtifact?> StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            StopCalled?.Invoke();
            if (StopDelay is not null)
            {
                await StopDelay.ConfigureAwait(false);
            }

            IsActive = false;
            StopEvent ??= new AudioCaptureStopEvent(
                DateTimeOffset.UtcNow,
                _artifactKind == AudioCaptureArtifactKind.Microphone ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.DeviceLoopback,
                AudioCaptureStopKind.Requested,
                "stop_requested",
                "Capture stopped on request.");

            if (StopFailuresRemaining > 0)
            {
                StopFailuresRemaining--;
                throw new IOException("Injected transient stop initiation failure.");
            }

            if (StopException is not null)
            {
                throw StopException;
            }

            if (PersistImmediately
                || (PromoteCalled && PromotionOutcome != AudioCapturePromotionOutcome.SourceUnavailable))
            {
                FinalArtifact ??= new AudioCaptureArtifact(_artifactKind, _artifactPath, 0, DateTimeOffset.UtcNow);
            }

            Stopped?.Invoke(this, StopEvent);
            return FinalArtifact;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void TriggerNaturalStop()
        {
            IsActive = false;
            StopEvent = new AudioCaptureStopEvent(
                DateTimeOffset.UtcNow,
                _artifactKind == AudioCaptureArtifactKind.Microphone ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.DeviceLoopback,
                AudioCaptureStopKind.SourceCompleted,
                "source_completed",
                "Capture source stopped.");
            FinalArtifact = new AudioCaptureArtifact(_artifactKind, _artifactPath, 0, DateTimeOffset.UtcNow);
            Stopped?.Invoke(this, StopEvent);
        }

        public void TriggerNaturalStopWithoutArtifact()
        {
            IsActive = false;
            StopEvent = new AudioCaptureStopEvent(
                DateTimeOffset.UtcNow,
                _artifactKind == AudioCaptureArtifactKind.Microphone
                    ? AudioCaptureSourceKind.Microphone
                    : AudioCaptureSourceKind.DeviceLoopback,
                AudioCaptureStopKind.SourceCompleted,
                "source_completed",
                "Capture source stopped before promotion.");
            FinalArtifact = null;
            Stopped?.Invoke(this, StopEvent);
        }

        public void TriggerFault(int sequence)
        {
            var sourceKind = _artifactKind == AudioCaptureArtifactKind.Microphone
                ? AudioCaptureSourceKind.Microphone
                : AudioCaptureSourceKind.DeviceLoopback;
            Faulted?.Invoke(
                this,
                new AudioCaptureFault(
                    DateTimeOffset.UtcNow,
                    AudioCaptureFailureKind.IoFailure,
                    sourceKind,
                    $"test_fault_{sequence}",
                    "Injected concurrent source fault.",
                    _artifactPath));
        }
    }
}
