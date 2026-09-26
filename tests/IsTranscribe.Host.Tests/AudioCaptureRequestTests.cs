using IsTranscribe.Host.Audio.Capture;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class AudioCaptureRequestTests
{
    [Fact]
    public void ValidateRejectsConflictingOutputSources()
    {
        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Manual,
            [
                AudioCaptureSourceRequest.ProcessOutput(101, "Zoom.exe"),
                AudioCaptureSourceRequest.DeviceLoopback("render-1")
            ],
            TempSessionDirectoryPath: "C:\\temp\\session",
            PrebufferSeconds: 0,
            CreateMixedArtifact: false);

        var exception = Assert.Throws<ArgumentException>(() => request.Validate());
        Assert.Contains("cannot be requested together", exception.Message);
    }

    [Fact]
    public void ValidateAcceptsProcessOutputWithMicrophone()
    {
        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Auto,
            [
                AudioCaptureSourceRequest.ProcessOutput(202, "chrome.exe"),
                AudioCaptureSourceRequest.Microphone("mic-1")
            ],
            TempSessionDirectoryPath: "C:\\temp\\session",
            PrebufferSeconds: 15,
            CreateMixedArtifact: true);

        request.Validate();
    }
}
