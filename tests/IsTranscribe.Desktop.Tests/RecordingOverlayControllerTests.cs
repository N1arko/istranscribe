using Avalonia.Controls;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Services;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013.A-floating-recording-widget#verification
/// </remarks>
public sealed class RecordingOverlayControllerTests
{
    [Fact]
    public void RecordingAndPausedSnapshotsShareOneSurfaceUntilCaptureEnds()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create());
        var surface = new FakeRecordingOverlaySurface();
        using var controller = new RecordingOverlayController(
            runtime,
            surfaceFactory: () => surface,
            dispatch: static action => action());

        controller.Start();
        Assert.Equal(0, surface.ShowCalls);

        var sessionId = Guid.NewGuid();
        runtime.Publish(Recording(sessionId, isPaused: false));
        Assert.True(surface.IsVisible);
        Assert.Equal(1, surface.ShowCalls);
        Assert.Equal(1, surface.ExpandCalls);

        runtime.Publish(Recording(sessionId, isPaused: true));
        Assert.True(surface.IsVisible);
        Assert.Equal(1, surface.ShowCalls);
        Assert.Equal(1, surface.ExpandCalls);

        runtime.Publish(SnapshotFactory.Create(ApplicationActivityState.Processing));
        Assert.False(surface.IsVisible);
        Assert.Equal(1, surface.HideCalls);

        runtime.Publish(Recording(Guid.NewGuid(), isPaused: false));
        Assert.True(surface.IsVisible);
        Assert.Equal(2, surface.ShowCalls);
        Assert.Equal(2, surface.ExpandCalls);
    }

    [Fact]
    public void StartUsesCurrentRecordingAndDisposeClosesAndUnsubscribes()
    {
        var runtime = new FakeApplicationRuntime(Recording(Guid.NewGuid(), isPaused: true));
        var surface = new FakeRecordingOverlaySurface();
        var controller = new RecordingOverlayController(
            runtime,
            surfaceFactory: () => surface,
            dispatch: static action => action());

        controller.Start();
        Assert.True(controller.IsVisible);
        Assert.Equal(1, surface.ShowCalls);

        controller.Dispose();
        Assert.Equal(1, surface.CloseCalls);
        Assert.False(surface.IsVisible);

        runtime.Publish(Recording(Guid.NewGuid(), isPaused: false));
        Assert.Equal(1, surface.ShowCalls);
    }

    private static ApplicationRuntimeSnapshot Recording(Guid sessionId, bool isPaused) =>
        SnapshotFactory.Create(
            ApplicationActivityState.Recording,
            activeMeeting: new ActiveMeetingSnapshot(
                sessionId,
                "Zoom",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                HasOutput: true,
                HasMicrophone: true,
                IsPaused: isPaused)
            {
                ActiveDuration = TimeSpan.FromMinutes(2),
                ActiveDurationMeasuredAtUtc = DateTimeOffset.UtcNow
            });

    private sealed class FakeRecordingOverlaySurface : IRecordingOverlaySurface
    {
        public bool IsVisible { get; private set; }

        public int ShowCalls { get; private set; }

        public int ExpandCalls { get; private set; }

        public int HideCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public void ShowNear(Window? placementOwner)
        {
            ShowCalls++;
            IsVisible = true;
        }

        public void ExpandForNewSession() => ExpandCalls++;

        public void HideSurface()
        {
            HideCalls++;
            IsVisible = false;
        }

        public void CloseForShutdown()
        {
            CloseCalls++;
            IsVisible = false;
        }
    }
}
